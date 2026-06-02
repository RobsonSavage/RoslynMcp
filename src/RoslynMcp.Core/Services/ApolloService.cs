using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Apollo;
using RoslynMcp.Shared.Contracts.Common;
using Serilog;

namespace RoslynMcp.Core.Services;

public class ApolloService
{
    private readonly IWorkspaceProvider _workspace;
    private readonly ILogger _logger;

    public ApolloService(IWorkspaceProvider workspace, ILogger logger)
    {
        _workspace = workspace;
        _logger = logger;
    }

    // ── 1. apollo_diagnose ──

    public async Task<Result<ApolloDiagnoseResponse>> DiagnoseAsync(
        ApolloDiagnoseRequest request, CancellationToken ct = default)
    {
        try
        {
            var solution = _workspace.CurrentSolution;
            if (solution is null)
                return Result<ApolloDiagnoseResponse>.Fail("No solution loaded");

            var allDiagnostics = new List<Diagnostic>();

            if (request.FilePath != null)
            {
                var doc = await _workspace.GetDocumentAsync(request.FilePath, ct: ct).ConfigureAwait(false);
                if (doc is null)
                    return Result<ApolloDiagnoseResponse>.Fail($"Document not found: {request.FilePath}");

                var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                if (model is null)
                    return Result<ApolloDiagnoseResponse>.Fail("Could not get semantic model");

                allDiagnostics.AddRange(model.GetDiagnostics(cancellationToken: ct));
            }
            else
            {
                foreach (var project in solution.Projects)
                {
                    ct.ThrowIfCancellationRequested();
                    var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                    if (compilation is null) continue;
                    allDiagnostics.AddRange(compilation.GetDiagnostics(ct));
                }
            }

            // Filter to errors only
            var errors = allDiagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            // Filter by ErrorId
            if (request.ErrorId != null)
                errors = errors.Where(d => d.Id == request.ErrorId).ToList();

            // Filter by ErrorMessage
            if (request.ErrorMessage != null)
                errors = errors.Where(d =>
                    d.GetMessage().IndexOf(request.ErrorMessage, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

            var diagnosticItems = errors
                .Select(d => ToDiagnosticItem(d))
                .ToList();

            // Determine root cause from most common error
            string? rootCause = null;
            if (errors.Count > 0)
            {
                var mostCommon = errors
                    .GroupBy(d => d.Id)
                    .OrderByDescending(g => g.Count())
                    .First();
                rootCause = CategorizeRootCause(mostCommon.Key, mostCommon.First().GetMessage());
            }

            var suggestions = errors
                .Select(d => d.Id)
                .Distinct()
                .Select(SuggestFix)
                .Where(s => s != null)
                .Cast<DiagnosticSuggestion>()
                .ToList();

            return new ApolloDiagnoseResponse(diagnosticItems, rootCause, suggestions);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "apollo_diagnose failed");
            return Result<ApolloDiagnoseResponse>.Fail($"Analysis failed: {ex.GetType().Name}");
        }
    }

    // ── 2. apollo_isolate ──

    public async Task<Result<ApolloIsolateResponse>> IsolateAsync(
        ApolloIsolateRequest request, CancellationToken ct = default)
    {
        try
        {
            var solution = _workspace.CurrentSolution;
            if (solution is null)
                return Result<ApolloIsolateResponse>.Fail("No solution loaded");

            var doc = await _workspace.GetDocumentAsync(request.FilePath, ct: ct).ConfigureAwait(false);
            if (doc is null)
                return Result<ApolloIsolateResponse>.Fail($"Document not found: {request.FilePath}");

            var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (model is null)
                return Result<ApolloIsolateResponse>.Fail("Could not get semantic model");

            var diagnostics = model.GetDiagnostics(cancellationToken: ct)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            if (request.ErrorId != null)
                diagnostics = diagnostics.Where(d => d.Id == request.ErrorId).ToList();

            if (diagnostics.Count == 0)
            {
                return new ApolloIsolateResponse(
                    IsolatedRange: null,
                    SuspectedCause: "No matching errors found",
                    Confidence: "none");
            }

            var target = diagnostics.First();
            var location = target.Location;

            CodeRange? range = null;
            string? suspectedCause = null;
            var confidence = "medium";

            if (location.IsInSource)
            {
                var span = location.GetLineSpan();
                range = new CodeRange(
                    span.StartLinePosition.Line,
                    span.StartLinePosition.Character,
                    span.EndLinePosition.Line,
                    span.EndLinePosition.Character);

                suspectedCause = CategorizeRootCause(target.Id, target.GetMessage());
                confidence = diagnostics.Count == 1 ? "high" : "medium";
            }
            else
            {
                suspectedCause = target.GetMessage();
                confidence = "low";
            }

            return new ApolloIsolateResponse(range, suspectedCause, confidence);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "apollo_isolate failed");
            return Result<ApolloIsolateResponse>.Fail($"Isolation failed: {ex.GetType().Name}");
        }
    }

    // ── 3. apollo_fix ──

    public async Task<Result<ApolloFixResponse>> FixAsync(
        ApolloFixRequest request, CancellationToken ct = default)
    {
        try
        {
            var solution = _workspace.CurrentSolution;
            if (solution is null)
                return Result<ApolloFixResponse>.Fail("No solution loaded");

            var doc = await _workspace.GetDocumentAsync(request.FilePath, ct: ct).ConfigureAwait(false);
            if (doc is null)
                return Result<ApolloFixResponse>.Fail($"Document not found: {request.FilePath}");

            var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (model is null)
                return Result<ApolloFixResponse>.Fail("Could not get semantic model");

            var diagnostic = model.GetDiagnostics(cancellationToken: ct)
                .FirstOrDefault(d => d.Id == request.DiagnosticId);

            if (diagnostic is null)
                return Result<ApolloFixResponse>.Fail(
                    $"Diagnostic {request.DiagnosticId} not found in file");

            // Build suggested changes based on common diagnostic patterns
            var changes = new List<ApolloFixChange>();
            var sourceText = await doc.GetTextAsync(ct).ConfigureAwait(false);

            if (diagnostic.Location.IsInSource)
            {
                var span = diagnostic.Location.SourceSpan;
                var lineSpan = diagnostic.Location.GetLineSpan();
                var oldText = sourceText.GetSubText(span).ToString();

                var suggestedFix = GetSuggestedFixText(
                    diagnostic.Id, diagnostic.GetMessage(), oldText);

                if (suggestedFix != null)
                {
                    changes.Add(new ApolloFixChange(
                        request.FilePath,
                        oldText,
                        suggestedFix,
                        new CodeRange(
                            lineSpan.StartLinePosition.Line,
                            lineSpan.StartLinePosition.Character,
                            lineSpan.EndLinePosition.Line,
                            lineSpan.EndLinePosition.Character)));
                }
            }

            // Preview mode: don't apply
            bool applied = false;
            // Intentional second call to GetDiagnostics: this is post-fix validation to report
            // the current error count, not a redundant call. The first call (above) retrieved
            // a specific diagnostic for fix generation; this one counts remaining errors.
            int newDiagnosticCount = model.GetDiagnostics(cancellationToken: ct)
                .Count(d => d.Severity == DiagnosticSeverity.Error);

            return new ApolloFixResponse(changes, applied, newDiagnosticCount);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "apollo_fix failed");
            return Result<ApolloFixResponse>.Fail($"Fix failed: {ex.GetType().Name}");
        }
    }

    // ── 4. apollo_validate ──

    public async Task<Result<ApolloValidateResponse>> ValidateAsync(
        ApolloValidateRequest request, CancellationToken ct = default)
    {
        try
        {
            var solution = _workspace.CurrentSolution;
            if (solution is null)
                return Result<ApolloValidateResponse>.Fail("No solution loaded");

            var doc = await _workspace.GetDocumentAsync(request.FilePath, ct: ct).ConfigureAwait(false);
            if (doc is null)
                return Result<ApolloValidateResponse>.Fail($"Document not found: {request.FilePath}");

            var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (model is null)
                return Result<ApolloValidateResponse>.Fail("Could not get semantic model");

            var currentErrors = model.GetDiagnostics(cancellationToken: ct)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(ToDiagnosticItem)
                .ToList();

            bool resolved = true;
            var newErrors = new List<DiagnosticItem>();

            if (request.OriginalDiagnosticId != null)
            {
                resolved = !currentErrors.Any(d => d.Id == request.OriginalDiagnosticId);
                // All current errors are "remaining" if original not resolved, or "new" if it is
                if (resolved)
                    newErrors = currentErrors;
            }
            else
            {
                resolved = currentErrors.Count == 0;
            }

            return new ApolloValidateResponse(resolved, currentErrors, newErrors);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "apollo_validate failed");
            return Result<ApolloValidateResponse>.Fail($"Validation failed: {ex.GetType().Name}");
        }
    }

    // ── Helpers ──

    private static DiagnosticItem ToDiagnosticItem(Diagnostic d)
    {
        var location = d.Location.IsInSource
            ? d.Location.GetLineSpan()
            : default;

        return new DiagnosticItem(
            d.Id,
            d.GetMessage(),
            d.Severity.ToString(),
            new CodeLocation(
                d.Location.IsInSource ? location.Path ?? "" : "",
                location.StartLinePosition.Line,
                location.StartLinePosition.Character,
                location.EndLinePosition.Line,
                location.EndLinePosition.Character),
            d.Descriptor.Category);
    }

    private static string CategorizeRootCause(string diagnosticId, string message)
    {
        return diagnosticId switch
        {
            "CS0246" or "CS0234" => $"Missing type or namespace reference: {message}",
            "CS1061" => $"Missing member on type: {message}",
            "CS0103" => $"Undefined identifier: {message}",
            "CS0029" or "CS0266" => $"Type conversion error: {message}",
            "CS0019" => $"Operator cannot be applied: {message}",
            "CS1501" or "CS1503" => $"Argument mismatch: {message}",
            "CS0117" => $"Type does not contain member: {message}",
            "CS0535" => $"Interface member not implemented: {message}",
            "CS0012" => $"Missing assembly reference: {message}",
            "CS1729" => $"Constructor argument mismatch: {message}",
            _ when diagnosticId.StartsWith("CS0") => $"Syntax/binding error: {message}",
            _ when diagnosticId.StartsWith("CS1") => $"Syntax error: {message}",
            _ => message,
        };
    }

    private static DiagnosticSuggestion? SuggestFix(string diagnosticId)
    {
        return diagnosticId switch
        {
            "CS0246" => new DiagnosticSuggestion(
                "Add missing using directive or assembly reference",
                "using MissingNamespace;",
                "reference", "high"),
            "CS1061" => new DiagnosticSuggestion(
                "Check spelling or add the missing member to the type",
                null,
                "member", "medium"),
            "CS0103" => new DiagnosticSuggestion(
                "Declare the variable or add a using directive",
                null,
                "declaration", "medium"),
            "CS0029" => new DiagnosticSuggestion(
                "Add explicit cast or use a conversion method",
                "(TargetType)expression",
                "conversion", "medium"),
            "CS0535" => new DiagnosticSuggestion(
                "Implement the missing interface member",
                null,
                "implementation", "high"),
            "CS0012" => new DiagnosticSuggestion(
                "Add the required assembly/package reference",
                null,
                "reference", "high"),
            _ => null,
        };
    }

    private static string? GetSuggestedFixText(string diagnosticId, string message, string oldText)
    {
        // Provide basic fix suggestions for common diagnostics.
        // Real CodeFixProviders require MEF composition (Phase 4).
        return diagnosticId switch
        {
            "CS0103" when oldText.Length > 0 =>
                $"/* TODO: declare '{oldText}' */ {oldText}",
            _ => null,
        };
    }
}

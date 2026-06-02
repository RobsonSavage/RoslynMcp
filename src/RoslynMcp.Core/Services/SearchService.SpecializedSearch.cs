using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Common;
using RoslynMcp.Shared.Contracts.Search;

namespace RoslynMcp.Core.Services;

public partial class SearchService
{
    private static class SubscriptionKinds
    {
        public const string Subscribe = "Subscribe";
        public const string Unsubscribe = "Unsubscribe";
        public const string Reference = "Reference";
    }

    private static class TestFrameworks
    {
        public const string MSTest = "MSTest";
        public const string NUnit = "NUnit";
        public const string XUnit = "xUnit";
        public const string Unknown = "Unknown";
    }

    private static readonly RoslynMcp.Shared.Contracts.Common.SymbolInfo UnknownSymbol =
        new("Unknown", "Unknown", "Unknown");

    // ── 12. find_tests_for_type ──

    public async Task<Result<FindTestsForTypeResponse>> FindTestsForTypeAsync(
        FindTestsForTypeRequest request, CancellationToken ct = default)
    {
        var (targetType, typeError) = await _helpers.ResolveTypeAsync(
            request.TypeName, request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (typeError != null) return Result<FindTestsForTypeResponse>.Fail(typeError);

        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("{MethodName}: No solution loaded", nameof(FindTestsForTypeAsync));
            return Result<FindTestsForTypeResponse>.Fail("No solution loaded");
        }
        var snapshotSolution = solution;
        var refs = await SymbolFinder.FindReferencesAsync(targetType!, snapshotSolution, ct).ConfigureAwait(false);

        // Phase 1: Build stubs + refLocationsByTree
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Build reference locations lookup (all refs — reused in enrichment)
        var refLocationsByTree = new Dictionary<SyntaxTree, List<Microsoft.CodeAnalysis.Text.TextSpan>>();
        var refsByDocument = new Dictionary<DocumentId, List<Microsoft.CodeAnalysis.Text.TextSpan>>();
        foreach (var group in refs)
        {
            foreach (var loc in group.Locations)
            {
                if (!loc.Location.IsInSource) continue;
                ct.ThrowIfCancellationRequested();

                var tree = loc.Location.SourceTree;
                if (tree is not null)
                {
                    if (!refLocationsByTree.TryGetValue(tree, out var spans))
                    {
                        spans = new List<Microsoft.CodeAnalysis.Text.TextSpan>();
                        refLocationsByTree[tree] = spans;
                    }
                    spans.Add(loc.Location.SourceSpan);
                }

                if (loc.Document is not null)
                {
                    if (!refsByDocument.TryGetValue(loc.Document.Id, out var docSpans))
                    {
                        docSpans = new List<Microsoft.CodeAnalysis.Text.TextSpan>();
                        refsByDocument[loc.Document.Id] = docSpans;
                    }
                    docSpans.Add(loc.Location.SourceSpan);
                }
            }
        }

        // Discover test classes — batched per document: O(distinct documents) model/root fetches
        var testClassSet = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var stubs = new List<TestClassStub>();
        foreach (var kvp in refsByDocument)
        {
            if (stubs.Count >= PagingHelper.MaxResults) break;
            ct.ThrowIfCancellationRequested();

            var refDoc = snapshotSolution.GetDocument(kvp.Key);
            if (refDoc is null) continue;

            var model = await refDoc.GetSemanticModelAsync(ct).ConfigureAwait(false);
            var root = await refDoc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (model is null || root is null) continue;

            foreach (var span in kvp.Value)
            {
                if (stubs.Count >= PagingHelper.MaxResults) break;
                var node = root.FindToken(span.Start).Parent;

                // Walk up to find containing test class
                var current = node;
                while (current != null)
                {
                    if (model.GetDeclaredSymbol(current, ct) is INamedTypeSymbol typeSym
                        && RoslynMapper.IsTestClass(typeSym))
                    {
                        if (testClassSet.Add(typeSym))
                        {
                            // Pre-filter: only stub if at least one test method contains a reference
                            if (HasMatchingTestMethod(typeSym, refLocationsByTree))
                            {
                                var loc = typeSym.Locations.FirstOrDefault(l => l.IsInSource);
                                if (loc is not null)
                                {
                                    var codeLocation = RoslynMapper.ToCodeLocation(loc);
                                    if (codeLocation is not null)
                                        stubs.Add(new TestClassStub(RoslynMapper.ToSymbolInfo(typeSym), codeLocation, kvp.Key, GetMetadataName(typeSym)));
                                }
                            }
                        }
                        break;
                    }
                    current = current.Parent;
                }
            }
        }

        sw.Restart();

        // Phase 2: Enrich requested page (test method scanning deferred)
        var result = await PagingHelper.PageAndEnrichAsync(
            stubs, request.Page, request.PageSize,
            (TestClassStub stub, CancellationToken ct2) =>
                EnrichTestClassAsync(stub, snapshotSolution, refLocationsByTree, ct2),
            (i, ex) => _logger.Warning(ex, "find_tests_for_type enrichment failed at {Index}", i),
            ct).ConfigureAwait(false);


        return new FindTestsForTypeResponse(targetType!.ToDisplayString(), result);
    }

    private static bool HasMatchingTestMethod(
        INamedTypeSymbol testClass,
        Dictionary<SyntaxTree, List<Microsoft.CodeAnalysis.Text.TextSpan>> refLocationsByTree)
    {
        foreach (var method in testClass.GetMembers().OfType<IMethodSymbol>())
        {
            if (!IsRelevantTestMember(method)) continue;
            var declRef = method.DeclaringSyntaxReferences.FirstOrDefault();
            if (declRef is null) continue;
            if (refLocationsByTree.TryGetValue(declRef.SyntaxTree, out var spans))
            {
                var methodSpan = declRef.Span;
                foreach (var s in spans)
                {
                    if (methodSpan.Contains(s)) return true;
                }
            }
        }
        return false;
    }

    private async Task<TestItem> EnrichTestClassAsync(
        TestClassStub stub,
        Solution solution,
        Dictionary<SyntaxTree, List<Microsoft.CodeAnalysis.Text.TextSpan>> refLocationsByTree,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Re-resolve the type from the snapshot solution (avoids pinning compilations in stubs)
        var doc = solution.GetDocument(stub.DocumentId);
        if (doc is null)
            return new TestItem(stub.Symbol, stub.Location, TestFrameworks.Unknown, new List<string>());

        var compilation = await doc.Project.GetCompilationAsync(ct).ConfigureAwait(false);
        var testClass = compilation?.GetTypeByMetadataName(stub.MetadataName);
        if (testClass is null)
            return new TestItem(stub.Symbol, stub.Location, TestFrameworks.Unknown, new List<string>());

        bool referencedInSetup = false;
        var testMethods = new List<string>();

        foreach (var method in testClass.GetMembers().OfType<IMethodSymbol>())
        {
            ct.ThrowIfCancellationRequested();
            var methodDeclRef = method.DeclaringSyntaxReferences.FirstOrDefault();
            if (methodDeclRef is null) continue;

            if (!refLocationsByTree.TryGetValue(methodDeclRef.SyntaxTree, out var treeSpans))
                continue;

            var methodSpan = methodDeclRef.Span;
            bool hasRef = false;
            foreach (var refSpan in treeSpans)
            {
                if (methodSpan.Contains(refSpan)) { hasRef = true; break; }
            }
            if (!hasRef) continue;

            if (RoslynMapper.IsTestMethod(method))
                testMethods.Add(method.Name);
            else if (method.MethodKind == MethodKind.Constructor || IsSetupMethod(method))
                referencedInSetup = true;
        }

        // If target type is referenced in setup/constructor, all test methods are affected
        if (referencedInSetup)
        {
            testMethods = testClass.GetMembers().OfType<IMethodSymbol>()
                .Where(RoslynMapper.IsTestMethod)
                .Select(m => m.Name)
                .ToList();
        }

        return new TestItem(stub.Symbol, stub.Location, DetectTestFramework(testClass), testMethods);
    }

    // ── 13. find_event_subscribers ──

    public async Task<Result<FindEventSubscribersResponse>> FindEventSubscribersAsync(
        FindEventSubscribersRequest request, CancellationToken ct = default)
    {
        var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (error != null) return Result<FindEventSubscribersResponse>.Fail(error);

        if (symbol is not IEventSymbol eventSymbol)
            return Result<FindEventSubscribersResponse>.Fail("Symbol at position is not an event");

        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("{MethodName}: No solution loaded", nameof(FindEventSubscribersAsync));
            return Result<FindEventSubscribersResponse>.Fail("No solution loaded");
        }
        var refs = await SymbolFinder.FindReferencesAsync(eventSymbol, solution, ct).ConfigureAwait(false);

        // Pass 1: Build stubs (includes subscription kind determination via GetSyntaxRootAsync)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stubs = new List<EventSubscriberStub>();
        foreach (var group in refs)
        {
            foreach (var loc in group.Locations)
            {
                if (stubs.Count >= PagingHelper.MaxResults) break;
                ct.ThrowIfCancellationRequested();
                if (!loc.Location.IsInSource) continue;
                if (loc.Document is null) continue;
                var span = loc.Location.GetLineSpan();
                var codeLocation = RoslynMapper.ToCodeLocation(span);
                if (codeLocation is null) continue;

                var refDoc = solution.GetDocument(loc.Document.Id);
                string subscriptionKind = SubscriptionKinds.Reference;

                if (refDoc != null)
                {
                    var root = await refDoc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                    var node = root?.FindToken(loc.Location.SourceSpan.Start).Parent;

                    if (node?.Parent is AssignmentExpressionSyntax assignment)
                    {
                        if (assignment.IsKind(SyntaxKind.AddAssignmentExpression))
                            subscriptionKind = SubscriptionKinds.Subscribe;
                        else if (assignment.IsKind(SyntaxKind.SubtractAssignmentExpression))
                            subscriptionKind = SubscriptionKinds.Unsubscribe;
                    }
                }

                // Only include subscribe/unsubscribe, not plain references
                if (subscriptionKind != SubscriptionKinds.Subscribe && subscriptionKind != SubscriptionKinds.Unsubscribe) continue;

                stubs.Add(new EventSubscriberStub(codeLocation, loc.Document.Id, loc.Location.SourceSpan.Start, subscriptionKind, span.StartLinePosition.Line));
            }
            if (stubs.Count >= PagingHelper.MaxResults) break;
        }

        var snapshotSolution = solution;
        var includeCtx = request.IncludeContext;
        sw.Restart();

        // Pass 2: Enrich requested page
        var result = await PagingHelper.PageAndEnrichAsync(
            stubs, request.Page, request.PageSize,
            (EventSubscriberStub stub, CancellationToken ct2) => EnrichEventSubscriberAsync(stub, snapshotSolution, includeCtx, ct2),
            (i, ex) => _logger.Warning(ex, "find_event_subscribers enrichment failed at {Index}", i),
            ct).ConfigureAwait(false);


        return new FindEventSubscribersResponse(RoslynMapper.ToSymbolInfo(symbol!), result);
    }

    private async Task<EventSubscriberItem> EnrichEventSubscriberAsync(
        EventSubscriberStub stub, Solution solution, bool includeContext, CancellationToken ct)
    {
        ISymbol? subscriber = null;
        string? contextLine = null;

        if (stub.DocumentId is not null)
        {
            var refDoc = solution.GetDocument(stub.DocumentId);
            if (refDoc != null)
            {
                var root = await refDoc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                var node = root?.FindToken(stub.SourceSpanStart).Parent;

                if (node?.Parent is AssignmentExpressionSyntax assignment)
                {
                    var model = await refDoc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                    if (model != null)
                    {
                        var handlerInfo = model.GetSymbolInfo(assignment.Right, ct);
                        subscriber = handlerInfo.Symbol;
                    }
                }

                if (includeContext)
                {
                    var text = await refDoc.GetTextAsync(ct).ConfigureAwait(false);
                    contextLine = RoslynMapper.GetContextLine(text, stub.StartLine);
                }
            }
        }

        var subscriberInfo = subscriber != null
            ? RoslynMapper.ToSymbolInfo(subscriber)
            : new RoslynMcp.Shared.Contracts.Common.SymbolInfo("Unknown", "Unknown", "Handler");
        return new EventSubscriberItem(subscriberInfo, stub.Location, stub.SubscriptionKind, contextLine);
    }

    private static string DetectTestFramework(INamedTypeSymbol testClass)
    {
        if (testClass.GetAttributes().Any(a => a.AttributeClass?.Name == "TestClassAttribute"))
            return TestFrameworks.MSTest;
        if (testClass.GetAttributes().Any(a => a.AttributeClass?.Name == "TestFixtureAttribute"))
            return TestFrameworks.NUnit;
        if (testClass.GetMembers().OfType<IMethodSymbol>().Any(m =>
            m.GetAttributes().Any(a => a.AttributeClass?.Name is "FactAttribute" or "TheoryAttribute")))
            return TestFrameworks.XUnit;
        return TestFrameworks.Unknown;
    }

    private static bool IsRelevantTestMember(IMethodSymbol method)
    {
        if (RoslynMapper.IsTestMethod(method)) return true;
        if (method.MethodKind == MethodKind.Constructor) return true;
        return IsSetupMethod(method);
    }

    private static bool IsSetupMethod(IMethodSymbol method)
    {
        return method.GetAttributes().Any(a =>
            a.AttributeClass?.Name is "SetUpAttribute" or "TestInitializeAttribute"
                or "OneTimeSetUpAttribute" or "ClassInitializeAttribute");
    }

    private static string GetMetadataName(INamedTypeSymbol type)
    {
        if (type.ContainingType != null)
            return GetMetadataName(type.ContainingType) + "+" + type.MetadataName;
        return type.ContainingNamespace.IsGlobalNamespace
            ? type.MetadataName
            : $"{type.ContainingNamespace.ToDisplayString()}.{type.MetadataName}";
    }
}

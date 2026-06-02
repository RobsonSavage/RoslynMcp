using RoslynMcp.Shared.Contracts.Apollo;

namespace RoslynMcp.Server.Tools;

[McpServerToolType]
public class ApolloTools
{
    private readonly ApolloService _service;
    private readonly IToolResultMapper _mapper;
    private readonly ILogger _logger;
    private readonly IToolMetricsService _metrics;

    public ApolloTools(ApolloService service, IToolResultMapper mapper, ILogger logger, IToolMetricsService metrics)
    {
        _service = service;
        _mapper = mapper;
        _logger = logger;
        _metrics = metrics;
    }

    // ── 1. apollo_diagnose ──

    [McpServerTool(Name = "apollo_diagnose"), Description("Diagnose compilation errors with root cause analysis and fix suggestions")]
    public async Task<CallToolResult> ApolloDiagnose(
        [Description("Path to C# file to diagnose")] string? filePath = null,
        [Description("Filter by specific error ID (e.g. CS0246)")] string? errorId = null,
        [Description("Filter by error message substring")] string? errorMessage = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            var request = new ApolloDiagnoseRequest(filePath, errorId, errorMessage);
            var result = await _service.DiagnoseAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("apollo_diagnose", sw.Elapsed, isError);
        }
    }

    // ── 2. apollo_isolate ──

    [McpServerTool(Name = "apollo_isolate"), Description("Isolate the source location and suspected cause of an error")]
    public async Task<CallToolResult> ApolloIsolate(
        [Description("Path to C# file containing the error")] string filePath,
        [Description("Filter by specific error ID (e.g. CS0246)")] string? errorId = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var request = new ApolloIsolateRequest(filePath, errorId);
            var result = await _service.IsolateAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("apollo_isolate", sw.Elapsed, isError);
        }
    }

    // ── 3. apollo_fix ──

    [McpServerTool(Name = "apollo_fix"), Description("Generate suggested code fixes for a specific diagnostic")]
    public async Task<CallToolResult> ApolloFix(
        [Description("Path to C# file")] string filePath,
        [Description("Diagnostic ID to fix (e.g. CS0246)")] string diagnosticId,
        [Description("Index of the fix to apply when multiple are available")] int fixIndex = 0,
        [Description("Preview changes without applying")] bool preview = true,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (string.IsNullOrWhiteSpace(diagnosticId)) return _mapper.Error("diagnosticId is required");
            if (fixIndex < 0) return _mapper.Error("fixIndex must be >= 0");
            var request = new ApolloFixRequest(filePath, diagnosticId, fixIndex, preview);
            var result = await _service.FixAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("apollo_fix", sw.Elapsed, isError);
        }
    }

    // ── 4. apollo_validate ──

    [McpServerTool(Name = "apollo_validate"), Description("Validate whether a previous error has been resolved after applying a fix")]
    public async Task<CallToolResult> ApolloValidate(
        [Description("Path to C# file to validate")] string filePath,
        [Description("Original diagnostic ID to check if resolved")] string? originalDiagnosticId = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var request = new ApolloValidateRequest(filePath, originalDiagnosticId);
            var result = await _service.ValidateAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("apollo_validate", sw.Elapsed, isError);
        }
    }
}

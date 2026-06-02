using RoslynMcp.Shared.Contracts.Structure;

namespace RoslynMcp.Server.Tools;

[McpServerToolType]
public class StructureTools
{
    private readonly StructureService _service;
    private readonly IToolResultMapper _mapper;
    private readonly ILogger _logger;
    private readonly IToolMetricsService _metrics;

    public StructureTools(StructureService service, IToolResultMapper mapper, ILogger logger, IToolMetricsService metrics)
    {
        _service = service;
        _mapper = mapper;
        _logger = logger;
        _metrics = metrics;
    }

    // ── 1. get_solution_structure ──

    [McpServerTool(Name = "get_solution_structure"), Description("Get an overview of all projects in the loaded solution")]
    public async Task<CallToolResult> GetSolutionStructure(
        [Description("Include additional metadata about projects")] bool includeMetadata = false,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            var request = new GetSolutionStructureRequest(includeMetadata);
            var result = await _service.GetSolutionStructureAsync(request, ct);
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
            _metrics.Record("get_solution_structure", sw.Elapsed, isError);
        }
    }

    // ── 2. get_project_structure ──

    [McpServerTool(Name = "get_project_structure"), Description("Get detailed structure of a specific project including documents and references")]
    public async Task<CallToolResult> GetProjectStructure(
        [Description("Name of the project")] string projectName,
        [Description("Include document list")] bool includeDocuments = true,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(projectName)) return _mapper.Error("projectName is required");
            var request = new GetProjectStructureRequest(projectName, includeDocuments);
            var result = await _service.GetProjectStructureAsync(request, ct);
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
            _metrics.Record("get_project_structure", sw.Elapsed, isError);
        }
    }

    // ── 3. get_file_outline ──

    [McpServerTool(Name = "get_file_outline"), Description("Get a hierarchical outline of types and members in a file")]
    public async Task<CallToolResult> GetFileOutline(
        [Description("Path to the C# file")] string filePath,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var request = new GetFileOutlineRequest(filePath);
            var result = await _service.GetFileOutlineAsync(request, ct);
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
            _metrics.Record("get_file_outline", sw.Elapsed, isError);
        }
    }

    // ── 4. get_dependency_graph ──

    [McpServerTool(Name = "get_dependency_graph"), Description("Get the project dependency graph for the solution")]
    public async Task<CallToolResult> GetDependencyGraph(
        [Description("Root project name (null for entire solution)")] string? projectName = null,
        [Description("Maximum depth to traverse")] int depth = 3,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (depth < 0 || depth > ValidationLimits.MaxRecursionDepth) return _mapper.Error("depth must be 0-10");
            var request = new GetDependencyGraphRequest(projectName, depth);
            var result = await _service.GetDependencyGraphAsync(request, ct);
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
            _metrics.Record("get_dependency_graph", sw.Elapsed, isError);
        }
    }

    // ── 5. get_types_in_file ──

    [McpServerTool(Name = "get_types_in_file"), Description("Get all type declarations in a file with their metadata")]
    public async Task<CallToolResult> GetTypesInFile(
        [Description("Path to the C# file")] string filePath,
        [Description("Include nested types")] bool includeNested = true,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var request = new GetTypesInFileRequest(filePath, includeNested);
            var result = await _service.GetTypesInFileAsync(request, ct);
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
            _metrics.Record("get_types_in_file", sw.Elapsed, isError);
        }
    }

    // ── 6. get_constructor_parameters ──

    [McpServerTool(Name = "get_constructor_parameters"), Description("Get constructor signatures and parameters for a type")]
    public async Task<CallToolResult> GetConstructorParameters(
        [Description("Fully qualified type name")] string? typeName = null,
        [Description("Path to the C# file containing the type")] string? filePath = null,
        [Description("0-based line number of the type")] int? line = null,
        [Description("0-based column number of the type")] int? column = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(typeName) && string.IsNullOrWhiteSpace(filePath))
                return _mapper.Error("At least typeName or filePath must be provided");
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var pathError = ToolValidation.ValidateFilePath(filePath);
                if (pathError != null) return _mapper.Error(pathError);
            }
            var request = new GetConstructorParametersRequest(typeName, filePath, line, column);
            var result = await _service.GetConstructorParametersAsync(request, ct);
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
            _metrics.Record("get_constructor_parameters", sw.Elapsed, isError);
        }
    }

    // ── 7. get_overloads ──

    [McpServerTool(Name = "get_overloads"), Description("Get all overloads of a method at a given position")]
    public async Task<CallToolResult> GetOverloads(
        [Description("Path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Include surrounding context lines")] bool includeContext = false,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            var request = new GetOverloadsRequest(filePath, line, column, includeContext);
            var result = await _service.GetOverloadsAsync(request, ct);
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
            _metrics.Record("get_overloads", sw.Elapsed, isError);
        }
    }

    // ── 8. get_accessibility ──

    [McpServerTool(Name = "get_accessibility"), Description("Get declared and effective accessibility of a symbol")]
    public async Task<CallToolResult> GetAccessibility(
        [Description("Path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            var request = new GetAccessibilityRequest(filePath, line, column);
            var result = await _service.GetAccessibilityAsync(request, ct);
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
            _metrics.Record("get_accessibility", sw.Elapsed, isError);
        }
    }

    // ── 9. get_xml_documentation ──

    [McpServerTool(Name = "get_xml_documentation"), Description("Get parsed XML documentation comments for a symbol")]
    public async Task<CallToolResult> GetXmlDocumentation(
        [Description("Path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            var request = new GetXmlDocumentationRequest(filePath, line, column);
            var result = await _service.GetXmlDocumentationAsync(request, ct);
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
            _metrics.Record("get_xml_documentation", sw.Elapsed, isError);
        }
    }
}

using RoslynMcp.Shared.Contracts.Refactor;

namespace RoslynMcp.Server.Tools;

[McpServerToolType]
public class RefactorTools
{
    private readonly RefactoringService _service;
    private readonly IToolResultMapper _mapper;
    private readonly ILogger _logger;
    private readonly IToolMetricsService _metrics;

    public RefactorTools(RefactoringService service, IToolResultMapper mapper, ILogger logger, IToolMetricsService metrics)
    {
        _service = service;
        _mapper = mapper;
        _logger = logger;
        _metrics = metrics;
    }

    private CallToolResult? ValidateFilePath(string filePath, string paramName = "filePath")
    {
        var error = ToolValidation.ValidateFilePath(filePath, paramName);
        return error != null ? _mapper.Error(error) : null;
    }

    // ── 1. preview_rename ──

    [McpServerTool(Name = "preview_rename"), Description("Preview the effects of renaming a symbol across the solution")]
    public async Task<CallToolResult> PreviewRename(
        [Description("Path to C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("New name for the symbol")] string newName,
        [Description("Include renames in comments")] bool includeComments = false,
        [Description("Include renames in string literals")] bool includeStrings = false,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var pathError = ValidateFilePath(filePath);
            if (pathError != null) return pathError;
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (string.IsNullOrWhiteSpace(newName)) return _mapper.Error("newName is required");
            var request = new RenameRequest(filePath, line, column, newName, includeComments, includeStrings);
            var result = await _service.PreviewRenameAsync(request, ct);
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
            _metrics.Record("preview_rename", sw.Elapsed, isError);
        }
    }

    // ── 2. apply_rename ──

    [McpServerTool(Name = "apply_rename"), Description("Apply a symbol rename across the solution")]
    public async Task<CallToolResult> ApplyRename(
        [Description("Path to C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("New name for the symbol")] string newName,
        [Description("Include renames in comments")] bool includeComments = false,
        [Description("Include renames in string literals")] bool includeStrings = false,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var pathError = ValidateFilePath(filePath);
            if (pathError != null) return pathError;
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (string.IsNullOrWhiteSpace(newName)) return _mapper.Error("newName is required");
            var request = new RenameRequest(filePath, line, column, newName, includeComments, includeStrings);
            var result = await _service.ApplyRenameAsync(request, ct);
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
            _metrics.Record("apply_rename", sw.Elapsed, isError);
        }
    }

    // ── 3. organize_usings ──

    [McpServerTool(Name = "organize_usings"), Description("Organize using directives by removing unused and sorting")]
    public async Task<CallToolResult> OrganizeUsings(
        [Description("Path to C# file")] string filePath,
        [Description("Remove unused using directives")] bool removeUnused = true,
        [Description("Sort using directives alphabetically")] bool sort = true,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var pathError = ValidateFilePath(filePath);
            if (pathError != null) return pathError;
            var request = new OrganizeUsingsRequest(filePath, removeUnused, sort);
            var result = await _service.OrganizeUsingsAsync(request, ct);
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
            _metrics.Record("organize_usings", sw.Elapsed, isError);
        }
    }

    // ── 4. preview_extract_interface ──

    [McpServerTool(Name = "preview_extract_interface"), Description("Preview extracting an interface from a class or struct")]
    public async Task<CallToolResult> PreviewExtractInterface(
        [Description("Path to C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Name for the extracted interface")] string interfaceName,
        [Description("Specific member names to include")] IReadOnlyList<string>? memberNames = null,
        [Description("Target file path for the interface")] string? targetFilePath = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var pathError = ValidateFilePath(filePath);
            if (pathError != null) return pathError;
            if (targetFilePath != null)
            {
                var targetPathError = ValidateFilePath(targetFilePath, "targetFilePath");
                if (targetPathError != null) return targetPathError;
            }
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (string.IsNullOrWhiteSpace(interfaceName)) return _mapper.Error("interfaceName is required");
            var request = new ExtractInterfaceRequest(filePath, line, column, interfaceName, memberNames, targetFilePath);
            var result = await _service.PreviewExtractInterfaceAsync(request, ct);
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
            _metrics.Record("preview_extract_interface", sw.Elapsed, isError);
        }
    }

    // ── 4b. apply_extract_interface ──

    [McpServerTool(Name = "apply_extract_interface"), Description("Apply extracting an interface from a class or struct")]
    public async Task<CallToolResult> ApplyExtractInterface(
        [Description("Path to C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Name for the extracted interface")] string interfaceName,
        [Description("Specific member names to include")] IReadOnlyList<string>? memberNames = null,
        [Description("Target file path for the interface")] string? targetFilePath = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var pathError = ValidateFilePath(filePath);
            if (pathError != null) return pathError;
            if (targetFilePath != null)
            {
                var targetPathError = ValidateFilePath(targetFilePath, "targetFilePath");
                if (targetPathError != null) return targetPathError;
            }
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (string.IsNullOrWhiteSpace(interfaceName)) return _mapper.Error("interfaceName is required");
            var request = new ExtractInterfaceRequest(filePath, line, column, interfaceName, memberNames, targetFilePath);
            var result = await _service.ApplyExtractInterfaceAsync(request, ct);
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
            _metrics.Record("apply_extract_interface", sw.Elapsed, isError);
        }
    }

    // ── 5. preview_move_type ──

    [McpServerTool(Name = "preview_move_type"), Description("Preview moving a type to a different file")]
    public async Task<CallToolResult> PreviewMoveType(
        [Description("Path to C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Target file path to move the type to")] string targetFilePath,
        [Description("Target namespace for the moved type")] string? targetNamespace = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var pathError = ValidateFilePath(filePath);
            if (pathError != null) return pathError;
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (string.IsNullOrWhiteSpace(targetFilePath)) return _mapper.Error("targetFilePath is required");
            var targetPathError = ValidateFilePath(targetFilePath, "targetFilePath");
            if (targetPathError != null) return targetPathError;
            var request = new MoveTypeRequest(filePath, line, column, targetFilePath, targetNamespace);
            var result = await _service.PreviewMoveTypeAsync(request, ct);
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
            _metrics.Record("preview_move_type", sw.Elapsed, isError);
        }
    }

    // ── 6. apply_move_type ──

    [McpServerTool(Name = "apply_move_type"), Description("Apply moving a type to a different file")]
    public async Task<CallToolResult> ApplyMoveType(
        [Description("Path to C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Target file path to move the type to")] string targetFilePath,
        [Description("Target namespace for the moved type")] string? targetNamespace = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var pathError = ValidateFilePath(filePath);
            if (pathError != null) return pathError;
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (string.IsNullOrWhiteSpace(targetFilePath)) return _mapper.Error("targetFilePath is required");
            var targetPathError = ValidateFilePath(targetFilePath, "targetFilePath");
            if (targetPathError != null) return targetPathError;
            var request = new MoveTypeRequest(filePath, line, column, targetFilePath, targetNamespace);
            var result = await _service.ApplyMoveTypeAsync(request, ct);
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
            _metrics.Record("apply_move_type", sw.Elapsed, isError);
        }
    }

    // ── 7. preview_extract_method ──

    [McpServerTool(Name = "preview_extract_method"), Description("Preview extracting a code range into a new method")]
    public async Task<CallToolResult> PreviewExtractMethod(
        [Description("Path to C# file")] string filePath,
        [Description("0-based start line")] int startLine,
        [Description("0-based start column")] int startColumn,
        [Description("0-based end line")] int endLine,
        [Description("0-based end column")] int endColumn,
        [Description("Name for the extracted method")] string methodName,
        [Description("Accessibility modifier (public, private, etc.)")] string? accessibility = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var pathError = ValidateFilePath(filePath);
            if (pathError != null) return pathError;
            if (startLine < 0) return _mapper.Error("startLine must be >= 0");
            if (startColumn < 0) return _mapper.Error("startColumn must be >= 0");
            if (endLine < 0) return _mapper.Error("endLine must be >= 0");
            if (endColumn < 0) return _mapper.Error("endColumn must be >= 0");
            if (endLine < startLine) return _mapper.Error("endLine must be >= startLine");
            if (string.IsNullOrWhiteSpace(methodName)) return _mapper.Error("methodName is required");
            var request = new ExtractMethodRequest(filePath, startLine, startColumn, endLine, endColumn, methodName, accessibility);
            var result = await _service.PreviewExtractMethodAsync(request, ct);
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
            _metrics.Record("preview_extract_method", sw.Elapsed, isError);
        }
    }

    // ── 8. apply_extract_method ──

    [McpServerTool(Name = "apply_extract_method"), Description("Apply extracting a code range into a new method")]
    public async Task<CallToolResult> ApplyExtractMethod(
        [Description("Path to C# file")] string filePath,
        [Description("0-based start line")] int startLine,
        [Description("0-based start column")] int startColumn,
        [Description("0-based end line")] int endLine,
        [Description("0-based end column")] int endColumn,
        [Description("Name for the extracted method")] string methodName,
        [Description("Accessibility modifier (public, private, etc.)")] string? accessibility = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var pathError = ValidateFilePath(filePath);
            if (pathError != null) return pathError;
            if (startLine < 0) return _mapper.Error("startLine must be >= 0");
            if (startColumn < 0) return _mapper.Error("startColumn must be >= 0");
            if (endLine < 0) return _mapper.Error("endLine must be >= 0");
            if (endColumn < 0) return _mapper.Error("endColumn must be >= 0");
            if (endLine < startLine) return _mapper.Error("endLine must be >= startLine");
            if (string.IsNullOrWhiteSpace(methodName)) return _mapper.Error("methodName is required");
            var request = new ExtractMethodRequest(filePath, startLine, startColumn, endLine, endColumn, methodName, accessibility);
            var result = await _service.ApplyExtractMethodAsync(request, ct);
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
            _metrics.Record("apply_extract_method", sw.Elapsed, isError);
        }
    }

    // ── 9. preview_split_class ──

    [McpServerTool(Name = "preview_split_class"), Description("Preview splitting members out of a class into a new class")]
    public async Task<CallToolResult> PreviewSplitClass(
        [Description("Path to C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Name for the new class")] string newClassName,
        [Description("Names of members to move to the new class")] IReadOnlyList<string> memberNames,
        [Description("Target file path for the new class")] string? targetFilePath = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var pathError = ValidateFilePath(filePath);
            if (pathError != null) return pathError;
            if (targetFilePath != null)
            {
                var targetPathError = ValidateFilePath(targetFilePath, "targetFilePath");
                if (targetPathError != null) return targetPathError;
            }
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (string.IsNullOrWhiteSpace(newClassName)) return _mapper.Error("newClassName is required");
            if (memberNames == null || memberNames.Count == 0) return _mapper.Error("memberNames must not be empty");
            var request = new SplitClassRequest(filePath, line, column, newClassName, memberNames, targetFilePath);
            var result = await _service.PreviewSplitClassAsync(request, ct);
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
            _metrics.Record("preview_split_class", sw.Elapsed, isError);
        }
    }

    // ── 10. apply_split_class ──

    [McpServerTool(Name = "apply_split_class"), Description("Apply splitting members out of a class into a new class")]
    public async Task<CallToolResult> ApplySplitClass(
        [Description("Path to C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Name for the new class")] string newClassName,
        [Description("Names of members to move to the new class")] IReadOnlyList<string> memberNames,
        [Description("Target file path for the new class")] string? targetFilePath = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var pathError = ValidateFilePath(filePath);
            if (pathError != null) return pathError;
            if (targetFilePath != null)
            {
                var targetPathError = ValidateFilePath(targetFilePath, "targetFilePath");
                if (targetPathError != null) return targetPathError;
            }
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (string.IsNullOrWhiteSpace(newClassName)) return _mapper.Error("newClassName is required");
            if (memberNames == null || memberNames.Count == 0) return _mapper.Error("memberNames must not be empty");
            var request = new SplitClassRequest(filePath, line, column, newClassName, memberNames, targetFilePath);
            var result = await _service.ApplySplitClassAsync(request, ct);
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
            _metrics.Record("apply_split_class", sw.Elapsed, isError);
        }
    }
}

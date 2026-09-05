using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using RoslynMcp.Shared;
using Serilog;

namespace RoslynMcp.Server.Services;

public class DefaultToolResultMapper : IToolResultMapper
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly Regex s_windowsPathRegex = new(
        @"(?:[A-Za-z]:\\|\\\\)[^\r\n""'<>|*?]+",
        RegexOptions.Compiled);

    private static readonly Regex s_unixPathRegex = new(
        @"(?<!/)/(?:home|usr|var|tmp|opt|etc|root|mnt|srv|proc|sys|dev|run)/[^\s""'<>|*?]+",
        RegexOptions.Compiled);

    private static readonly Regex s_secretsRegex = new(
        @"(password|pwd|user\s*id|secret|token|key|accesstoken|sharedaccesskey|sharedaccesssignature|api[_-]?key|client[_-]?secret|connection[_-]?string)\s*=\s*[^;]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IWorkspaceProvider? _workspaceProvider;

    public DefaultToolResultMapper(IWorkspaceProvider? workspaceProvider = null)
    {
        _workspaceProvider = workspaceProvider;
    }

    private string? SolutionDirectory => _workspaceProvider?.SolutionDirectory;

    public CallToolResult Success<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, s_jsonOptions);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }]
        };
    }

    public CallToolResult ValidationError(IEnumerable<ValidationResult> errors)
    {
        var messages = errors.Select(e =>
            $"{string.Join(", ", e.MemberNames)}: {e.ErrorMessage}");
        var errorMessage = string.Join("; ", messages);
        return Error(errorMessage);
    }

    public CallToolResult Error(string message, string? errorCode = null)
    {
        var json = JsonSerializer.Serialize(new ErrorResponse(message, errorCode), s_jsonOptions);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }],
            IsError = true
        };
    }

    public CallToolResult Exception(Exception ex, ILogger logger)
    {
        logger.Error(ex, "Tool execution failed");
        var sanitized = SanitizeException(ex, depth: 0);
        return Error(sanitized);
    }

    private string SanitizeException(Exception ex, int depth)
    {
        var msg = ex.GetType().Name + ": " + ex.Message;

        const int MaxMessageLength = 500;
        if (msg.Length > MaxMessageLength)
            msg = msg[..MaxMessageLength] + "...[truncated]";

        // Redact Windows paths (drive-letter and UNC)
        msg = s_windowsPathRegex.Replace(msg,
            match =>
            {
                var solDir = SolutionDirectory;
                if (solDir is not null)
                {
                    var dirWithSep = solDir.EndsWith(@"\")
                        ? solDir
                        : solDir + @"\";
                    if (match.Value.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase)
                        || match.Value.Equals(solDir, StringComparison.OrdinalIgnoreCase))
                        return match.Value;
                }
                else
                {
                    // Standalone mode: redact all absolute paths
                }
                return "[path-redacted]";
            });

        // Redact Unix absolute paths
        msg = s_unixPathRegex.Replace(msg,
            match =>
            {
                var solDir2 = SolutionDirectory;
                if (solDir2 is not null
                    && match.Value.StartsWith(solDir2, StringComparison.Ordinal))
                    return match.Value;
                return "[path-redacted]";
            });

        // Redact connection string secrets
        msg = s_secretsRegex.Replace(msg, "$1=[REDACTED]");

        if (depth < 3 && ex.InnerException is { } inner)
            msg += " ---> " + SanitizeException(inner, depth + 1);

        return msg;
    }
}

using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Protocol;
using Serilog;

namespace RoslynMcp.Server.Services;

public interface IToolResultMapper
{
    CallToolResult Success<T>(T value);
    CallToolResult ValidationError(IEnumerable<ValidationResult> errors);
    CallToolResult Error(string message, string? errorCode = null);
    CallToolResult Exception(Exception ex, ILogger logger);
}

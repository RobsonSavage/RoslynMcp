namespace RoslynMcp.Shared;

public record ErrorResponse(string Message, string? ErrorCode = null);

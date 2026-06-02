namespace RoslynMcp.Server.Tools;

/// <summary>
/// Shared validation helpers for tool input parameters.
/// </summary>
public static class ToolValidation
{
    /// <summary>
    /// Validates that a file path is absolute and contains no path traversal segments.
    /// Returns an error message string if invalid, or null if valid.
    /// </summary>
    public static string? ValidateFilePath(string filePath, string paramName = "filePath")
    {
        if (!Path.IsPathFullyQualified(filePath))
            return $"{paramName} must be an absolute path";

        // Reject paths with traversal segments
        if (filePath.Contains(".." + Path.DirectorySeparatorChar) ||
            filePath.Contains(".." + Path.AltDirectorySeparatorChar) ||
            filePath.EndsWith(".."))
            return $"{paramName} must not contain path traversal (..)";

        return null; // valid
    }
}

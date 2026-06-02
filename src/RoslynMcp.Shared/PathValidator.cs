using System.IO;
using System.Runtime.InteropServices;
using System.Security;

namespace RoslynMcp.Shared;

public static class PathValidator
{
    private static readonly StringComparison s_pathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static Result<string> Canonicalize(string filePath, string? solutionDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return Result<string>.Fail("File path cannot be empty", "INVALID_PATH");

        string canonical;
        try
        {
            canonical = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityException or PathTooLongException or NotSupportedException)
        {
            return Result<string>.Fail($"Invalid file path: {ex.Message}", "INVALID_PATH");
        }

        if (solutionDirectory is not null)
        {
            string canonicalSolutionDir;
            try
            {
                canonicalSolutionDir = Path.GetFullPath(solutionDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or SecurityException or PathTooLongException or NotSupportedException)
            {
                return Result<string>.Fail($"Invalid solution directory path: {ex.Message}", "INVALID_PATH");
            }

            var dirWithSep = canonicalSolutionDir.EndsWith(Path.DirectorySeparatorChar.ToString(), s_pathComparison)
                ? canonicalSolutionDir
                : canonicalSolutionDir + Path.DirectorySeparatorChar;

            if (!canonical.StartsWith(dirWithSep, s_pathComparison)
                && !canonical.Equals(canonicalSolutionDir, s_pathComparison))
            {
                return Result<string>.Fail("Path is outside solution directory", "PATH_OUTSIDE_SOLUTION");
            }
        }

        return canonical;
    }
}

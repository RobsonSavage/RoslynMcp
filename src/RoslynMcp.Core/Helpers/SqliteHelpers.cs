namespace RoslynMcp.Core.Helpers;

/// <summary>
/// Shared SQLite utility methods used by multiple services.
/// </summary>
public static class SqliteHelpers
{
    /// <summary>
    /// Escapes LIKE wildcard characters (%, _, \) in user input so they are treated as literals
    /// when used with <c>ESCAPE '\'</c>.
    /// </summary>
    public static string EscapeLikeWildcards(string input)
    {
        return input
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }
}

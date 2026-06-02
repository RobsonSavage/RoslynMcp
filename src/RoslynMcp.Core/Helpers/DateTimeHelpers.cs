using System.Globalization;

namespace RoslynMcp.Core.Helpers;

public static class DateTimeHelpers
{
    /// <summary>
    /// Parses a SQLite datetime string (CURRENT_TIMESTAMP format or ISO 8601 round-trip)
    /// as UTC. Treats timezone-unaware strings as UTC, not local time.
    /// </summary>
    public static DateTime ParseUtcDateTime(string s)
    {
        if (string.IsNullOrEmpty(s))
            throw new FormatException("Cannot parse null or empty string as UTC DateTime. This indicates missing data in the database.");

        return DateTime.Parse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
}

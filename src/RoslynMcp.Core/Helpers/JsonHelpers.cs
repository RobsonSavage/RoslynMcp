using System.Text.Json;

namespace RoslynMcp.Core.Helpers;

public static class JsonHelpers
{
    public static List<string> DeserializeTags(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json!) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    public static string SerializeTags(IReadOnlyList<string>? tags)
    {
        return JsonSerializer.Serialize(tags ?? Array.Empty<string>());
    }
}

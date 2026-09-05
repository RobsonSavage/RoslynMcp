using System.IO;
using System.Text;
using System.Text.Json;
using RoslynMcp.Shared.Contracts.Util;
using Serilog;

namespace RoslynMcp.Core.Helpers;

public enum ImportResult { Success, NoOp, FallbackToDefaults, Error }

/// <summary>
/// Manages configuration stored in .roslyn-mcp-data/config.json.
/// Thread-safe via locking. Persists changes immediately.
/// </summary>
public class ConfigManager
{
    private string? _configPath;
    private readonly object _lock = new();
    private readonly ILogger? _logger;
    private Dictionary<string, string> _values;

    private static readonly ConfigDefinition[] s_definitions =
    {
        new("timeout.default", "int", "30", "Default tool timeout in seconds"),
        new("warmup.enabled", "bool", "false", "Enable workspace warmup on start"),
        new("warmup.parallelism", "int", "0", "Warmup parallelism (0 = ProcessorCount/2)"),
        new("paging.default_page_size", "int", "5", "Default page size for paged results"),
        new("paging.max_page_size", "int", "200", "Maximum page size"),
        new("logging.level", "string", "Information", "Logging level"),
        new("logging.file_retention_days", "int", "7", "Log file retention in days"),
        new("sqlite.busy_timeout_ms", "int", "1000", "SQLite busy timeout in ms"),
        new("sqlite.cache_size_kb", "int", "16000", "SQLite cache size in KB"),
        new("graph.auto_rebuild", "bool", "true", "Rebuild the dependency graph after every solution load"),
        new("workspace.idle_unload_minutes", "int", "30", "Unload the workspace after N minutes idle (0 = never)"),
        new("workspace.watch_files", "bool", "true", "Refresh the workspace from disk when source files change"),
    };

    private static readonly Dictionary<string, ConfigDefinition> s_definitionMap =
        s_definitions.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
    private static readonly string[] s_retiredKeys = ["workspace.follow_roots"];

    private const string ToolPrefix = "tools.";
    private const string TimeoutPrefix = "timeout.";

    public ConfigManager(string? configDir, ILogger? logger = null)
    {
        _configPath = configDir == null ? null : Path.Combine(configDir, "config.json");
        _logger = logger;
        _values = _configPath == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : Load(_configPath);
    }

    public string? ConfigDirectory
    {
        get
        {
            lock (_lock)
                return _configPath == null ? null : Path.GetDirectoryName(_configPath);
        }
    }

    public void SwitchDirectory(string? configDir)
    {
        var configPath = configDir == null ? null : Path.Combine(configDir, "config.json");
        var values = configPath == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : Load(configPath);

        lock (_lock)
        {
            _configPath = configPath;
            _values = values;
        }

        _logger?.Information("Configuration switched to {ConfigPath}", configPath);
    }

    public ConfigGetResponse Get(string key)
    {
        lock (_lock)
        {
            _values.TryGetValue(key, out var value);

            if (s_definitionMap.TryGetValue(key, out var def))
                return new ConfigGetResponse(key, value, def.DefaultValue, def.Type, def.Description);

            if (key.StartsWith(ToolPrefix, StringComparison.OrdinalIgnoreCase) && key.EndsWith(".enabled", StringComparison.OrdinalIgnoreCase))
                return new ConfigGetResponse(key, value, "true", "bool", "Enable/disable tool");

            if (key.StartsWith(TimeoutPrefix, StringComparison.OrdinalIgnoreCase) && key != "timeout.default")
                return new ConfigGetResponse(key, value, null, "int", "Timeout override for tool");

            return new ConfigGetResponse(key, value, null, "unknown", null);
        }
    }

    public ConfigSetResponse? Set(string key, string value, out string? error)
    {
        lock (_lock)
        {
            if (_configPath == null)
            {
                error = "NO_SOLUTION_SELECTED: Select a solution before changing configuration.";
                return null;
            }

            bool isKnown = s_definitionMap.ContainsKey(key);
            bool isDynamic = key.StartsWith(ToolPrefix, StringComparison.OrdinalIgnoreCase)
                || (key.StartsWith(TimeoutPrefix, StringComparison.OrdinalIgnoreCase) && key != "timeout.default");

            if (!isKnown && !isDynamic)
            {
                error = $"Unknown configuration key: {key}";
                return null;
            }

            var type = isKnown ? s_definitionMap[key].Type : (key.EndsWith(".enabled", StringComparison.OrdinalIgnoreCase) ? "bool" : "int");
            var validationError = ValidateValue(value, type);
            if (validationError != null)
            {
                error = validationError;
                return null;
            }

            _values.TryGetValue(key, out var previous);
            _values[key] = value;
            Save();
            error = null;
            return new ConfigSetResponse(key, value, previous);
        }
    }

    public ConfigListResponse List()
    {
        lock (_lock)
        {
            var entries = new List<ConfigEntry>();

            foreach (var def in s_definitions)
            {
                _values.TryGetValue(def.Key, out var value);
                entries.Add(new ConfigEntry(def.Key, value, def.DefaultValue, def.Type, def.Description));
            }

            // Include any dynamic keys that are set but not in static definitions
            foreach (var kvp in _values)
            {
                if (!s_definitionMap.ContainsKey(kvp.Key))
                {
                    var type = kvp.Key.EndsWith(".enabled", StringComparison.OrdinalIgnoreCase) ? "bool"
                        : kvp.Key.StartsWith(TimeoutPrefix, StringComparison.OrdinalIgnoreCase) ? "int"
                        : "string";
                    var desc = kvp.Key.StartsWith(ToolPrefix, StringComparison.OrdinalIgnoreCase)
                        ? "Tool-specific setting"
                        : kvp.Key.StartsWith(TimeoutPrefix, StringComparison.OrdinalIgnoreCase)
                        ? "Timeout override for tool"
                        : "Custom setting";
                    entries.Add(new ConfigEntry(kvp.Key, kvp.Value, "true", type, desc));
                }
            }

            return new ConfigListResponse(entries);
        }
    }

    public ToolEnabledResponse ToolEnabled(string toolName, bool? enabled)
    {
        var key = $"tools.{toolName}.enabled";

        lock (_lock)
        {
            _values.TryGetValue(key, out var current);
            var currentEnabled = current == null || !string.Equals(current, "false", StringComparison.OrdinalIgnoreCase);

            if (enabled.HasValue)
            {
                if (_configPath == null)
                    throw new InvalidOperationException(
                        "NO_SOLUTION_SELECTED: Select a solution before changing configuration.");

                var newValue = enabled.Value ? "true" : "false";
                var wasChanged = currentEnabled != enabled.Value;
                _values[key] = newValue;
                if (wasChanged) Save();
                return new ToolEnabledResponse(toolName, enabled.Value, wasChanged);
            }

            return new ToolEnabledResponse(toolName, currentEnabled);
        }
    }

    public ImportResult ImportFromV1(string sourcePath, bool force, ILogger logger)
    {
        if (_configPath == null)
        {
            logger.Error("Cannot import configuration before a solution is selected");
            return ImportResult.Error;
        }

        Dictionary<string, string> sourceData;
        try
        {
            var json = File.ReadAllText(sourcePath, Encoding.UTF8);
            sourceData = ParseSimpleJson(json);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to parse source config: {Path}", sourcePath);
            return ImportResult.FallbackToDefaults;
        }

        lock (_lock)
        {
            // If v2 config exists and not forcing, check if contents are identical
            if (File.Exists(_configPath) && !force)
            {
                var existingKeys = _values.Keys
                    .Where(k => !string.Equals(k, "version", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                var sourceKeys = sourceData.Keys
                    .Where(k => IsKnownOrDynamic(k))
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (existingKeys.Count == sourceKeys.Count
                    && existingKeys.SequenceEqual(sourceKeys, StringComparer.OrdinalIgnoreCase)
                    && existingKeys.All(k => string.Equals(_values[k], sourceData[k], StringComparison.Ordinal)))
                {
                    logger.Information("Import skipped: existing config is identical");
                    return ImportResult.NoOp;
                }
            }

            // Pre-import backup
            if (File.Exists(_configPath))
            {
                var backupPath = _configPath + $".{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                File.Copy(_configPath, backupPath, overwrite: true);
                logger.Information("Backed up existing config to {BackupPath}", backupPath);
            }

            // Map keys from source
            var imported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in sourceData)
            {
                if (string.Equals(kvp.Key, "version", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!IsKnownOrDynamic(kvp.Key))
                {
                    logger.Warning("Unknown key '{Key}' skipped", kvp.Key);
                    continue;
                }

                var type = GetKeyType(kvp.Key);
                var validationError = ValidateValue(kvp.Value, type);
                if (validationError != null)
                {
                    logger.Error("Invalid value for '{Key}': {Error}", kvp.Key, validationError);
                    continue;
                }

                imported[kvp.Key] = kvp.Value;
            }

            imported["version"] = "2.0";
            _values = imported;
            Save();

            logger.Information("Imported {Count} keys from {Path}", imported.Count - 1, sourcePath);
            return ImportResult.Success;
        }
    }

    private static bool IsKnownOrDynamic(string key)
    {
        if (s_definitionMap.ContainsKey(key))
            return true;
        if (key.StartsWith(ToolPrefix, StringComparison.OrdinalIgnoreCase))
            return true;
        if (key.StartsWith(TimeoutPrefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(key, "timeout.default", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string GetKeyType(string key)
    {
        if (s_definitionMap.TryGetValue(key, out var def))
            return def.Type;
        if (key.EndsWith(".enabled", StringComparison.OrdinalIgnoreCase))
            return "bool";
        return "int"; // timeout.* keys default to int
    }

    private Dictionary<string, string> Load(string configPath)
    {
        if (!File.Exists(configPath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(configPath, Encoding.UTF8);
            var values = ParseSimpleJson(json);
            var removedRetiredKey = false;
            foreach (var key in s_retiredKeys)
                removedRetiredKey |= values.Remove(key);

            if (removedRetiredKey)
            {
                try
                {
                    SaveConfig(configPath, values);
                    _logger?.Information("Removed retired configuration keys from {Path}", configPath);
                }
                catch (Exception ex)
                {
                    _logger?.Warning(ex, "Could not remove retired configuration keys from {Path}", configPath);
                }
            }
            return values;
        }
        catch (IOException ex)
        {
            _logger?.Warning(ex, "Config load failed (IO): {Path}", configPath);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            _logger?.Warning(ex, "Config load failed (parse): {Path}", configPath);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Config load failed (unexpected): {Path}", configPath);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        var configPath = _configPath
            ?? throw new InvalidOperationException(
                "NO_SOLUTION_SELECTED: Select a solution before changing configuration.");
        SaveConfig(configPath, _values);
    }

    private static void SaveConfig(string configPath, Dictionary<string, string> values)
    {
        var dir = Path.GetDirectoryName(configPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tempPath = configPath + ".tmp";
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(values, options);
        File.WriteAllText(tempPath, json, Encoding.UTF8);

        try
        {
            if (File.Exists(configPath))
                File.Replace(tempPath, configPath, configPath + ".bak");
            else
                File.Move(tempPath, configPath);
        }
        catch (IOException)
        {
            // Fallback: direct write if Replace fails (e.g., cross-volume)
            File.Copy(tempPath, configPath, overwrite: true);
            try { File.Delete(tempPath); } catch { }
        }
    }

    private static string? ValidateValue(string value, string type)
    {
        switch (type)
        {
            case "int":
                if (!int.TryParse(value, out _))
                    return $"Value '{value}' is not a valid integer";
                break;
            case "bool":
                if (!string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                    return $"Value '{value}' is not a valid boolean (true/false)";
                break;
        }
        return null;
    }

    /// <summary>
    /// Parses flat JSON key-value objects. Throws JsonException on invalid JSON.
    /// </summary>
    private static Dictionary<string, string> ParseSimpleJson(string json)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString()!
                : prop.Value.GetRawText();
        }
        return dict;
    }

    private readonly record struct ConfigDefinition(string Key, string Type, string DefaultValue, string Description);
}

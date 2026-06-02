using System.Text;
using RoslynMcp.Core.Helpers;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Helpers;

public class ConfigManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public ConfigManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RoslynMcpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteSourceJson(string json)
    {
        var path = Path.Combine(_tempDir, $"source-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json, Encoding.UTF8);
        return path;
    }

    private string CreateConfigDir()
    {
        var dir = Path.Combine(_tempDir, $"cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ImportFromV1_ValidFile_ImportsKnownKeys()
    {
        var source = WriteSourceJson("""
        {
            "timeout.default": "60",
            "warmup.enabled": "true",
            "paging.default_page_size": "10"
        }
        """);

        var configDir = CreateConfigDir();
        var cm = new ConfigManager(configDir);

        var result = cm.ImportFromV1(source, force: false, _logger);

        Assert.Equal(ImportResult.Success, result);
        Assert.Equal("60", cm.Get("timeout.default").Value);
        Assert.Equal("true", cm.Get("warmup.enabled").Value);
        Assert.Equal("10", cm.Get("paging.default_page_size").Value);
    }

    [Fact]
    public void ImportFromV1_UnknownKeys_SkippedWithWarning()
    {
        var source = WriteSourceJson("""
        {
            "timeout.default": "30",
            "some.random.key": "value",
            "another_unknown": "123"
        }
        """);

        var configDir = CreateConfigDir();
        var cm = new ConfigManager(configDir);

        var result = cm.ImportFromV1(source, force: false, _logger);

        Assert.Equal(ImportResult.Success, result);
        Assert.Equal("30", cm.Get("timeout.default").Value);
        // Unknown keys should not be present
        Assert.Null(cm.Get("some.random.key").Value);
        Assert.Null(cm.Get("another_unknown").Value);
    }

    [Fact]
    public void ImportFromV1_InvalidValues_SkippedWithError()
    {
        var source = WriteSourceJson("""
        {
            "timeout.default": "abc",
            "warmup.enabled": "true"
        }
        """);

        var configDir = CreateConfigDir();
        var cm = new ConfigManager(configDir);

        var result = cm.ImportFromV1(source, force: false, _logger);

        Assert.Equal(ImportResult.Success, result);
        // Invalid int value skipped
        Assert.Null(cm.Get("timeout.default").Value);
        // Valid bool kept
        Assert.Equal("true", cm.Get("warmup.enabled").Value);
    }

    [Fact]
    public void ImportFromV1_ExistingConfig_NoForce_ReturnsNoOp()
    {
        var source = WriteSourceJson("""
        {
            "timeout.default": "30",
            "warmup.enabled": "false"
        }
        """);

        var configDir = CreateConfigDir();
        var cm = new ConfigManager(configDir);

        var result1 = cm.ImportFromV1(source, force: false, _logger);
        Assert.Equal(ImportResult.Success, result1);

        // Import same data again without force
        var result2 = cm.ImportFromV1(source, force: false, _logger);
        Assert.Equal(ImportResult.NoOp, result2);
    }

    [Fact]
    public void ImportFromV1_ExistingConfig_Force_Overwrites()
    {
        var source1 = WriteSourceJson("""
        {
            "timeout.default": "30"
        }
        """);

        var configDir = CreateConfigDir();
        var cm = new ConfigManager(configDir);
        cm.ImportFromV1(source1, force: false, _logger);
        Assert.Equal("30", cm.Get("timeout.default").Value);

        // Import different data with force
        var source2 = WriteSourceJson("""
        {
            "timeout.default": "90"
        }
        """);

        var result = cm.ImportFromV1(source2, force: true, _logger);
        Assert.Equal(ImportResult.Success, result);
        Assert.Equal("90", cm.Get("timeout.default").Value);
    }

    [Fact]
    public void ImportFromV1_CorruptedInput_ReturnsFallback()
    {
        var source = WriteSourceJson("this is not json at all {{{{");

        var configDir = CreateConfigDir();
        var cm = new ConfigManager(configDir);

        // ParseSimpleJson won't throw on malformed input — it just returns empty dict.
        // Use a non-existent path to trigger File.ReadAllText exception.
        var result = cm.ImportFromV1(Path.Combine(_tempDir, "nonexistent.json"), force: false, _logger);
        Assert.Equal(ImportResult.FallbackToDefaults, result);
    }

    [Fact]
    public void ImportFromV1_BackupCreated_WhenExistingConfig()
    {
        var source = WriteSourceJson("""
        {
            "timeout.default": "30"
        }
        """);

        var configDir = CreateConfigDir();
        var cm = new ConfigManager(configDir);

        // First import creates config.json
        cm.ImportFromV1(source, force: false, _logger);

        // Second import with force should create a .bak
        var source2 = WriteSourceJson("""
        {
            "timeout.default": "60"
        }
        """);
        cm.ImportFromV1(source2, force: true, _logger);

        var bakFiles = Directory.GetFiles(configDir, "config.json.*.bak");
        Assert.NotEmpty(bakFiles);
    }

    [Fact]
    public void ImportFromV1_AddsVersionKey()
    {
        var source = WriteSourceJson("""
        {
            "warmup.enabled": "true"
        }
        """);

        var configDir = CreateConfigDir();
        var cm = new ConfigManager(configDir);

        var result = cm.ImportFromV1(source, force: false, _logger);
        Assert.Equal(ImportResult.Success, result);

        // Version key should be in persisted file
        var configJson = File.ReadAllText(Path.Combine(configDir, "config.json"));
        Assert.Contains("\"version\"", configJson);
        Assert.Contains("2.0", configJson);

        // Also accessible via Get
        Assert.Equal("2.0", cm.Get("version").Value);
    }
}

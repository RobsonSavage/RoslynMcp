using System.Diagnostics;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynMcp.Server.Providers;
using Xunit;

namespace RoslynMcp.Integration;

/// <summary>
/// Bug 4953: an analyzer the workspace has loaded must not stay locked on disk, or a restore that
/// re-extracts its package fails with access denied while any server is running.
/// </summary>
[Collection("workspace")]
public sealed class AnalyzerShadowCopyTest
{
    private static readonly object s_locatorLock = new();

    [Fact]
    public async Task LoadedAnalyzer_CanBeDeleted()
    {
        lock (s_locatorLock)
        {
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();
        }

        var sdk = MSBuildLocator.QueryVisualStudioInstances().First().MSBuildPath;
        var source = Path.Combine(sdk, "Sdks", "Microsoft.NET.Sdk", "analyzers", "Microsoft.CodeAnalysis.NetAnalyzers.dll");
        var dir = Directory.CreateTempSubdirectory("roslynmcp-4953-").FullName;
        try
        {
            var analyzer = Path.Combine(dir, "analyzers", "Microsoft.CodeAnalysis.NetAnalyzers.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(analyzer)!);
            File.Copy(source, analyzer);
            var project = Path.Combine(dir, "Probe.csproj");
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableNETAnalyzers>false</EnableNETAnalyzers>
                  </PropertyGroup>
                  <ItemGroup>
                    <Analyzer Include="analyzers\Microsoft.CodeAnalysis.NetAnalyzers.dll" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(dir, "Class1.cs"), "public class Class1 { }");
            using (var restore = Process.Start(new ProcessStartInfo("dotnet", $"restore \"{project}\"") { UseShellExecute = false })!)
            {
                await restore.WaitForExitAsync();
                Assert.Equal(0, restore.ExitCode);
            }

            using var workspace = MSBuildWorkspace.Create(ShadowCopyAnalyzerService.Host);
            var loaded = await workspace.OpenProjectAsync(project);
            var reference = Assert.Single(loaded.AnalyzerReferences,
                r => string.Equals(r.FullPath, analyzer, StringComparison.OrdinalIgnoreCase));
            Assert.NotEmpty(reference.GetAnalyzersForAllLanguages());

            File.Delete(analyzer);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

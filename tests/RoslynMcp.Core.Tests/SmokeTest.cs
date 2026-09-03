using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests;

public class SmokeTest
{
    [Fact]
    public void Services_CanBeInstantiated()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        using var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Test.cs", "class C { }");

        var tempDir = Path.Combine(Path.GetTempPath(), $"roslynmcp-smoke-{Guid.NewGuid()}");
        try
        {
            var provider = helper.CreateProvider();
            var helpers = helper.CreateHelpers(logger);
            var config = new ConfigManager(tempDir);

            // Verify all services can be constructed without throwing
            var search = new SearchService(provider, helpers, logger);
            var structure = new StructureService(provider, helpers, logger);
            var analyze = new AnalyzeService(provider, helpers, logger);
            var refactoring = new RefactoringService(provider, helpers, logger);
            var util = new UtilService(provider, helpers, config, new TestSolutionContextSwitcher(), logger);

            Assert.NotNull(search);
            Assert.NotNull(structure);
            Assert.NotNull(analyze);
            Assert.NotNull(refactoring);
            Assert.NotNull(util);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}

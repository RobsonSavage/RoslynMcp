using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Structure;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class StructureServiceTests_SymbolInfo : IDisposable
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private WorkspaceTestHelper? _helper;

    private StructureService CreateService(WorkspaceTestHelper helper)
    {
        _helper = helper;
        var provider = helper.CreateProvider();
        var helpers = helper.CreateHelpers(_logger);
        return new StructureService(provider, helpers, _logger);
    }

    public void Dispose() => _helper?.Dispose();

    // ─── Test 1: GetConstructorParameters_FindsCtorParams ───

    [Fact]
    public async Task GetConstructorParameters_FindsCtorParams()
    {
        var source = @"
namespace TestNs
{
    public class MyService
    {
        private readonly string _name;
        private readonly int _count;

        public MyService(string name, int count)
        {
            _name = name;
            _count = count;
        }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "MyService.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "MyService.cs", source);

        var service = CreateService(helper);

        var result = await service.GetConstructorParametersAsync(
            new GetConstructorParametersRequest(TypeName: "MyService"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("MyService", result.Value!.Type.Name);
        Assert.Single(result.Value.Constructors);

        var ctor = result.Value.Constructors[0];
        Assert.Equal("Public", ctor.Accessibility);
        Assert.Equal(2, ctor.Parameters.Count);
        Assert.Equal("name", ctor.Parameters[0].Name);
        Assert.Equal("string", ctor.Parameters[0].Type);
        Assert.Equal("count", ctor.Parameters[1].Name);
        Assert.Equal("int", ctor.Parameters[1].Type);
    }

    // ─── Test 2: GetConstructorParameters_MultipleCtors ───

    [Fact]
    public async Task GetConstructorParameters_MultipleCtors()
    {
        var source = @"
namespace TestNs
{
    public class Config
    {
        public Config() { }
        public Config(string path) { }
        public Config(string path, bool readOnly) { }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Config.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Config.cs", source);

        var service = CreateService(helper);

        var result = await service.GetConstructorParametersAsync(
            new GetConstructorParametersRequest(TypeName: "Config"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(3, result.Value!.Constructors.Count);

        var paramCounts = result.Value.Constructors.Select(c => c.Parameters.Count).OrderBy(c => c).ToList();
        Assert.Equal(0, paramCounts[0]);
        Assert.Equal(1, paramCounts[1]);
        Assert.Equal(2, paramCounts[2]);
    }

    // ─── Test 3: GetOverloads_FindsAllOverloads ───

    [Fact]
    public async Task GetOverloads_FindsAllOverloads()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Calculator
        // Line 4:     {
        // Line 5:         public int Add(int a, int b) { return a + b; }
        // Line 6:         public double Add(double a, double b) { return a + b; }
        // Line 7:         public int Add(int a, int b, int c) { return a + b + c; }
        // Line 8:     }
        // Line 9: }
        var source = @"
namespace TestNs
{
    public class Calculator
    {
        public int Add(int a, int b) { return a + b; }
        public double Add(double a, double b) { return a + b; }
        public int Add(int a, int b, int c) { return a + b + c; }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Calculator.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Calculator.cs", source);

        var service = CreateService(helper);

        // Point at first "Add" method: line 5
        // "        public int Add(int a, int b) { return a + b; }"
        //  01234567890123456789
        //  ^^^^^^^^               = 8 spaces
        //          ^^^^^^         = "public"
        //                ^        = space
        //                 ^^^     = "int"
        //                    ^    = space
        //                     A   = col 19
        var result = await service.GetOverloadsAsync(
            new GetOverloadsRequest(filePath, Line: 5, Column: 19), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Add", result.Value!.Method.Name);
        Assert.Equal(3, result.Value.Overloads.Count);
    }

    // ─── Test 4: GetOverloads_ReturnsErrorForNonMethod ───

    [Fact]
    public async Task GetOverloads_ReturnsErrorForNonMethod()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:         public int Value;
        // Line 6:     }
        // Line 7: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public int Value;
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Point at "Value" field: line 5
        // "        public int Value;"
        //  01234567890123456789
        //  ^^^^^^^^               = 8 spaces
        //          ^^^^^^         = "public"
        //                ^        = space
        //                 ^^^     = "int"
        //                    ^    = space
        //                     V   = col 19
        var result = await service.GetOverloadsAsync(
            new GetOverloadsRequest(filePath, Line: 5, Column: 19), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("not a method", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Test 5: GetAccessibility_ReturnsEffectiveAccessibility ───

    [Fact]
    public async Task GetAccessibility_ReturnsEffectiveAccessibility()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     internal class Container
        // Line 4:     {
        // Line 5:         public void PublicInInternal() { }
        // Line 6:     }
        // Line 7: }
        var source = @"
namespace TestNs
{
    internal class Container
    {
        public void PublicInInternal() { }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Container.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Container.cs", source);

        var service = CreateService(helper);

        // Point at "PublicInInternal" method: line 5
        // "        public void PublicInInternal() { }"
        //  01234567890123456789012
        //  ^^^^^^^^                 = 8 spaces
        //          ^^^^^^           = "public"
        //                ^          = space
        //                 ^^^^      = "void"
        //                     ^     = space
        //                      P    = col 20
        var result = await service.GetAccessibilityAsync(
            new GetAccessibilityRequest(filePath, Line: 5, Column: 20), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("PublicInInternal", result.Value!.Symbol.Name);
        Assert.Equal("Public", result.Value.DeclaredAccessibility);
        // Effective should be Internal (limited by containing type)
        Assert.Equal("Internal", result.Value.EffectiveAccessibility);
    }

    // ─── Test 6: GetAccessibility_TopLevelType ───

    [Fact]
    public async Task GetAccessibility_TopLevelType()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class PublicClass
        // Line 4:     {
        // Line 5:     }
        // Line 6: }
        var source = @"
namespace TestNs
{
    public class PublicClass
    {
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "PublicClass.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "PublicClass.cs", source);

        var service = CreateService(helper);

        // Point at "PublicClass": line 3
        // "    public class PublicClass"
        //  01234567890123456789
        //  ^^^^                   = 4 spaces
        //      ^^^^^^             = "public"
        //            ^            = space
        //             ^^^^^       = "class"
        //                  ^      = space
        //                   P     = col 17
        var result = await service.GetAccessibilityAsync(
            new GetAccessibilityRequest(filePath, Line: 3, Column: 17), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Public", result.Value!.DeclaredAccessibility);
        Assert.Equal("Public", result.Value.EffectiveAccessibility);
    }

    // ─── Test 7: GetXmlDocumentation_ParsesSummary ───

    [Fact]
    public async Task GetXmlDocumentation_ParsesSummary()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Documented
        // Line 4:     {
        // Line 5:         /// <summary>
        // Line 6:         /// Calculates the sum of two integers.
        // Line 7:         /// </summary>
        // Line 8:         /// <param name="a">First number.</param>
        // Line 9:         /// <param name="b">Second number.</param>
        // Line 10:        /// <returns>The sum of a and b.</returns>
        // Line 11:        public int Add(int a, int b) { return a + b; }
        // Line 12:    }
        // Line 13: }
        var source = @"
namespace TestNs
{
    public class Documented
    {
        /// <summary>
        /// Calculates the sum of two integers.
        /// </summary>
        /// <param name=""a"">First number.</param>
        /// <param name=""b"">Second number.</param>
        /// <returns>The sum of a and b.</returns>
        public int Add(int a, int b) { return a + b; }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Documented.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Documented.cs", source);

        var service = CreateService(helper);

        // Point at "Add" method: line 11
        // "        public int Add(int a, int b) { return a + b; }"
        //  01234567890123456789
        //  ^^^^^^^^               = 8 spaces
        //          ^^^^^^         = "public"
        //                ^        = space
        //                 ^^^     = "int"
        //                    ^    = space
        //                     A   = col 19
        var result = await service.GetXmlDocumentationAsync(
            new GetXmlDocumentationRequest(filePath, Line: 11, Column: 19), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Add", result.Value!.Symbol.Name);

        Assert.NotNull(result.Value.Summary);
        Assert.Contains("Calculates the sum", result.Value.Summary);

        Assert.NotNull(result.Value.Returns);
        Assert.Contains("sum of a and b", result.Value.Returns);

        Assert.NotNull(result.Value.Parameters);
        Assert.Equal(2, result.Value.Parameters.Count);
        Assert.Equal("a", result.Value.Parameters[0].Name);
        Assert.Contains("First number", result.Value.Parameters[0].Description);
        Assert.Equal("b", result.Value.Parameters[1].Name);
    }

    // ─── Test 8: GetXmlDocumentation_NoDocumentation ───

    [Fact]
    public async Task GetXmlDocumentation_NoDocumentation()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Undocumented
        // Line 4:     {
        // Line 5:         public void NoDoc() { }
        // Line 6:     }
        // Line 7: }
        var source = @"
namespace TestNs
{
    public class Undocumented
    {
        public void NoDoc() { }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Undocumented.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Undocumented.cs", source);

        var service = CreateService(helper);

        // Point at "NoDoc" method: line 5
        // "        public void NoDoc() { }"
        //  01234567890123456789012
        //  ^^^^^^^^                 = 8 spaces
        //          ^^^^^^           = "public"
        //                ^          = space
        //                 ^^^^      = "void"
        //                     ^     = space
        //                      N    = col 20
        var result = await service.GetXmlDocumentationAsync(
            new GetXmlDocumentationRequest(filePath, Line: 5, Column: 20), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Null(result.Value!.Summary);
        Assert.Null(result.Value.Parameters);
    }
}

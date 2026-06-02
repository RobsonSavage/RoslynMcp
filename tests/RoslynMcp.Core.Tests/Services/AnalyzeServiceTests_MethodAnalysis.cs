using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Analyze;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class AnalyzeServiceTests_MethodAnalysis : IDisposable
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private WorkspaceTestHelper? _helper;

    private AnalyzeService CreateService(WorkspaceTestHelper helper)
    {
        _helper = helper;
        var provider = helper.CreateProvider();
        var helpers = helper.CreateHelpers(_logger);
        return new AnalyzeService(provider, helpers, _logger);
    }

    public void Dispose()
    {
        _helper?.Dispose();
    }

    // ─── Test 1: UnderstandMethod_ReturnsAggregateInfo ───

    [Fact]
    public async Task UnderstandMethod_ReturnsAggregateInfo()
    {
        // Source layout (0-indexed lines):
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:         public int Helper(int x) { return x + 1; }
        // Line 6:         public int DoWork(int a, int b) { return Helper(a) + Helper(b); }
        // Line 7:         public void Caller() { DoWork(1, 2); }
        // Line 8:     }
        // Line 9: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public int Helper(int x) { return x + 1; }
        public int DoWork(int a, int b) { return Helper(a) + Helper(b); }
        public void Caller() { DoWork(1, 2); }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Point at "DoWork" declaration: line 6
        // "        public int DoWork(int a, int b) { return Helper(a) + Helper(b); }"
        //  01234567890123456789012
        //  ^^^^^^^^                 = 8 spaces
        //          ^^^^^^           = "public"
        //                ^          = space
        //                 ^^^       = "int"
        //                    ^      = space
        //                     D     = col 19
        var request = new UnderstandMethodRequest(filePath, Line: 6, Column: 19);
        var result = await service.UnderstandMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Equal("DoWork", response.Symbol.Name);
        Assert.NotNull(response.Signature);
        Assert.NotNull(response.ReturnType);
        Assert.True(response.Parameters.Count >= 2, $"Expected at least 2 parameters, got {response.Parameters.Count}");
        Assert.True(response.Metrics.CyclomaticComplexity >= 1, "Expected CyclomaticComplexity >= 1");
        Assert.True(response.Callers.Count >= 1, $"Expected at least 1 caller, got {response.Callers.Count}");
        Assert.True(response.Callees.Count >= 1, $"Expected at least 1 callee, got {response.Callees.Count}");
    }

    // ─── Test 2: UnderstandMethod_FailsOnNonMethod ───

    [Fact]
    public async Task UnderstandMethod_FailsOnNonMethod()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class MyClass
        // Line 4:     {
        // Line 5:     }
        // Line 6: }
        var source = @"
namespace TestNs
{
    public class MyClass
    {
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "MyClass.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "MyClass.cs", source);

        var service = CreateService(helper);

        // Point at "MyClass" on line 3
        // "    public class MyClass"
        //  0123456789012345678
        //  ^^^^                 = 4 spaces
        //      ^^^^^^           = "public"
        //            ^          = space
        //             ^^^^^     = "class"
        //                  ^    = space
        //                   M   = col 17
        var request = new UnderstandMethodRequest(filePath, Line: 3, Column: 17);
        var result = await service.UnderstandMethodAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("not a method", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Test 3: GetMethodBody_ReturnsSource ───

    [Fact]
    public async Task GetMethodBody_ReturnsSource()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:         public int Compute(int x)
        // Line 6:         {
        // Line 7:             var result = x * 2;
        // Line 8:             result = result + 1;
        // Line 9:             return result;
        // Line 10:        }
        // Line 11:    }
        // Line 12: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public int Compute(int x)
        {
            var result = x * 2;
            result = result + 1;
            return result;
        }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Point at "Compute" declaration: line 5
        // "        public int Compute(int x)"
        //  01234567890123456789012
        //  ^^^^^^^^                 = 8 spaces
        //          ^^^^^^           = "public"
        //                ^          = space
        //                 ^^^       = "int"
        //                    ^      = space
        //                     C     = col 19
        var request = new GetMethodBodyRequest(filePath, Line: 5, Column: 19);
        var result = await service.GetMethodBodyAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Contains("Compute", response.BodySource);
        Assert.True(response.LineCount > 1, $"Expected LineCount > 1, got {response.LineCount}");
    }

    // ─── Test 4: GetMethodBody_FailsOnNonMethod ───

    [Fact]
    public async Task GetMethodBody_FailsOnNonMethod()
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
        //  01234567890123456789012345
        //  ^^^^^^^^                   = 8 spaces
        //          ^^^^^^             = "public"
        //                ^            = space
        //                 ^^^         = "int"
        //                    ^        = space
        //                     V       = col 19
        var request = new GetMethodBodyRequest(filePath, Line: 5, Column: 19);
        var result = await service.GetMethodBodyAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    // ─── Test 5: GetCodeMetrics_SimpleMethod ───

    [Fact]
    public async Task GetCodeMetrics_SimpleMethod()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:         public int Simple() { return 42; }
        // Line 6:     }
        // Line 7: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public int Simple() { return 42; }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Point at "Simple" declaration: line 5
        // "        public int Simple() { return 42; }"
        //  01234567890123456789012
        //  ^^^^^^^^                 = 8 spaces
        //          ^^^^^^           = "public"
        //                ^          = space
        //                 ^^^       = "int"
        //                    ^      = space
        //                     S     = col 19
        var request = new GetCodeMetricsRequest(FilePath: filePath, Line: 5, Column: 19);
        var result = await service.GetCodeMetricsAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var metrics = result.Value!.Metrics;
        Assert.Equal(1, metrics.CyclomaticComplexity);
        Assert.True(metrics.LinesOfCode > 0, $"Expected LinesOfCode > 0, got {metrics.LinesOfCode}");
        Assert.True(metrics.MaintainabilityIndex > 0, $"Expected MaintainabilityIndex > 0, got {metrics.MaintainabilityIndex}");
    }

    // ─── Test 6: GetCodeMetrics_ComplexMethod ───

    [Fact]
    public async Task GetCodeMetrics_ComplexMethod()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:         public int Complex(int x)
        // Line 6:         {
        // Line 7:             if (x > 0 && x < 100)
        // Line 8:             {
        // Line 9:                 for (int i = 0; i < x; i++)
        // Line 10:                {
        // Line 11:                    x = x > 50 ? x - 1 : x + 1;
        // Line 12:                }
        // Line 13:            }
        // Line 14:            else
        // Line 15:            {
        // Line 16:                try { x = -x; }
        // Line 17:                catch (System.Exception) { x = 0; }
        // Line 18:            }
        // Line 19:            return x;
        // Line 20:        }
        // Line 21:    }
        // Line 22: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public int Complex(int x)
        {
            if (x > 0 && x < 100)
            {
                for (int i = 0; i < x; i++)
                {
                    x = x > 50 ? x - 1 : x + 1;
                }
            }
            else
            {
                try { x = -x; }
                catch (System.Exception) { x = 0; }
            }
            return x;
        }
    }
}";
        // Complexity contributors:
        //   Base: 1
        //   if: +1
        //   &&: +1
        //   for: +1
        //   ternary (?:): +1
        //   catch: +1
        //   Total: 6
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Point at "Complex" declaration: line 5
        // "        public int Complex(int x)"
        //  01234567890123456789012
        //  ^^^^^^^^                 = 8 spaces
        //          ^^^^^^           = "public"
        //                ^          = space
        //                 ^^^       = "int"
        //                    ^      = space
        //                     C     = col 19
        var request = new GetCodeMetricsRequest(FilePath: filePath, Line: 5, Column: 19);
        var result = await service.GetCodeMetricsAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var metrics = result.Value!.Metrics;
        Assert.True(metrics.CyclomaticComplexity > 1,
            $"Expected CyclomaticComplexity > 1, got {metrics.CyclomaticComplexity}");
    }

    // ─── Test 7: AnalyzeDataFlow_DetectsFlows ───

    [Fact]
    public async Task AnalyzeDataFlow_DetectsFlows()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:         public void Compute(int input)
        // Line 6:         {
        // Line 7:             var x = input;
        // Line 8:             var y = x + 1;
        // Line 9:             var z = y * 2;
        // Line 10:        }
        // Line 11:    }
        // Line 12: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void Compute(int input)
        {
            var x = input;
            var y = x + 1;
            var z = y * 2;
        }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Analyze line 8 only: "            var y = x + 1;"
        // This single statement has data flowing in (x, from earlier assignment)
        // and declares variable y (VariablesDeclared), reads x (ReadInside),
        // writes y (WrittenInside), and y flows out to line 9.
        // Line 8: col 12 = start of "var", col 26 = past ";"
        //  "            var y = x + 1;"
        //   012345678901234567890123456
        //   ^^^^^^^^^^^^                = 12 spaces
        //               ^^^             = "var"
        //                  ^            = space
        //                   ^           = "y"
        //                    ^          = space
        //                     ^         = "="
        //                      ^        = space
        //                       ^       = "x"
        //                        ^      = space
        //                         ^     = "+"
        //                          ^    = space
        //                           ^   = "1"
        //                            ;  = col 26
        var request = new AnalyzeDataFlowRequest(filePath,
            StartLine: 8, StartColumn: 12,
            EndLine: 8, EndColumn: 26);
        var result = await service.AnalyzeDataFlowAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var flow = result.Value!.DataFlow;
        Assert.True(flow.VariablesDeclared.Count > 0, "Expected VariablesDeclared to be populated");
        Assert.True(flow.DataFlowsIn.Count > 0, "Expected DataFlowsIn to be populated");
        Assert.True(flow.ReadInside.Count > 0, "Expected ReadInside to be populated");
        Assert.True(flow.WrittenInside.Count > 0, "Expected WrittenInside to be populated");
    }

    // ─── Test 8: AnalyzeDataFlow_NoStatements ───

    [Fact]
    public async Task AnalyzeDataFlow_NoStatements()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:     }
        // Line 6: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Point at the empty area inside the class: line 5, col 0..4
        var request = new AnalyzeDataFlowRequest(filePath,
            StartLine: 4, StartColumn: 0,
            EndLine: 5, EndColumn: 4);
        var result = await service.AnalyzeDataFlowAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("No statements found", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Test 9: AnalyzeOperations_ReturnsTree ───

    [Fact]
    public async Task AnalyzeOperations_ReturnsTree()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:         public void Run()
        // Line 6:         {
        // Line 7:             var x = 1 + 2;
        // Line 8:         }
        // Line 9:     }
        // Line 10: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void Run()
        {
            var x = 1 + 2;
        }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Point at "x" in the statement on line 7
        // "            var x = 1 + 2;"
        //  01234567890123456
        //  ^^^^^^^^^^^^       = 12 spaces
        //              ^^^    = "var"
        //                 ^   = space
        //                  x  = col 16
        var request = new AnalyzeOperationsRequest(filePath, Line: 7, Column: 16);
        var result = await service.AnalyzeOperationsAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var rootOp = result.Value!.RootOperation;
        Assert.NotNull(rootOp);
        Assert.NotNull(rootOp.OperationKind);
        Assert.True(rootOp.Children.Count > 0, $"Expected children, got {rootOp.Children.Count}");
    }

    // ─── Test 10: AnalyzeOperations_NoSymbol ───

    [Fact]
    public async Task AnalyzeOperations_NoSymbol()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:     }
        // Line 6: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Point at an empty spot inside the class body: line 4, col 5
        // "    {"
        //  01234
        //  ^^^^   = 4 spaces
        //      {  = col 4 (opening brace of class body)
        var request = new AnalyzeOperationsRequest(filePath, Line: 4, Column: 5);
        var result = await service.AnalyzeOperationsAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }
}

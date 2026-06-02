using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Search;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class SearchServiceTests_Navigation : IDisposable
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private WorkspaceTestHelper? _helper;

    private SearchService CreateService(WorkspaceTestHelper helper)
    {
        _helper = helper;
        var provider = helper.CreateProvider();
        var helpers = helper.CreateHelpers(_logger);
        return new SearchService(provider, helpers, _logger);
    }

    public void Dispose()
    {
        _helper?.Dispose();
    }

    // ─── Test 1: FindReferences_FindsUsages ───

    [Fact]
    public async Task FindReferences_FindsUsages()
    {
        // Source layout (0-indexed lines):
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:         public void Bar() { }
        // Line 6:         public void Baz() { Bar(); }
        // Line 7:     }
        // Line 8: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void Bar() { }
        public void Baz() { Bar(); }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Point at "Bar" declaration: line 5, col 20
        // "        public void Bar() { }"
        //  01234567890123456789012
        //  ^^^^^^^^               = 8 spaces
        //          ^^^^^^         = "public" (6)
        //                ^        = space
        //                 ^^^^    = "void" (4)
        //                     ^   = space
        //                      B  = col 20
        var request = new FindReferencesRequest(filePath, Line: 5, Column: 20);
        var result = await service.FindReferencesAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Bar", result.Value!.TargetSymbol.Name);

        var refs = result.Value.References.Items;
        Assert.True(refs.Count >= 1, $"Expected at least 1 reference, got {refs.Count}");

        // The call site on line 6: "        public void Baz() { Bar(); }"
        //  01234567890123456789012345678
        //  ^^^^^^^^                       = 8 spaces
        //          ^^^^^^                 = "public"
        //                ^                = space
        //                 ^^^^            = "void"
        //                     ^           = space
        //                      ^^^        = "Baz"
        //                         ^^      = "()"
        //                           ^     = " "
        //                            ^    = "{"
        //                             ^   = " "
        //                              B  = col 28
        var callRef = refs.FirstOrDefault(r => r.Location.StartLine == 6);
        Assert.NotNull(callRef);
        Assert.Equal(28, callRef.Location.StartColumn);
    }

    // ─── Test 2: FindReferences_DetectsWriteAccess ───

    [Fact]
    public async Task FindReferences_DetectsWriteAccess()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:         public int Value;
        // Line 6:         public void Set() { Value = 42; }
        // Line 7:     }
        // Line 8: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public int Value;
        public void Set() { Value = 42; }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Point at "Value" field declaration: line 5
        // "        public int Value;"
        //  01234567890123456789012345
        //  ^^^^^^^^                   = 8 spaces
        //          ^^^^^^             = "public"
        //                ^            = space
        //                 ^^^         = "int"
        //                    ^        = space
        //                     V       = col 19
        var request = new FindReferencesRequest(filePath, Line: 5, Column: 19);
        var result = await service.FindReferencesAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Value", result.Value!.TargetSymbol.Name);

        var refs = result.Value.References.Items;
        Assert.True(refs.Count >= 1, $"Expected at least 1 reference, got {refs.Count}");

        // Write access on line 6: "        public void Set() { Value = 42; }"
        //  01234567890123456789012345678
        //  ^^^^^^^^                       = 8 spaces
        //          ^^^^^^                 = "public"
        //                ^                = space
        //                 ^^^^            = "void"
        //                     ^           = space
        //                      ^^^        = "Set"
        //                         ^^      = "()"
        //                           ^     = " "
        //                            ^    = "{"
        //                             ^   = " "
        //                              V  = col 28
        var writeRef = refs.FirstOrDefault(r => r.Location.StartLine == 6);
        Assert.NotNull(writeRef);
        Assert.True(writeRef.IsWriteAccess, "Reference should be detected as write access");
    }

    // ─── Test 3: FindReferences_WithContext ───

    [Fact]
    public async Task FindReferences_WithContext()
    {
        // Same source as Test 1
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void Bar() { }
        public void Baz() { Bar(); }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Point at "Bar" declaration: line 5, col 20
        var request = new FindReferencesRequest(filePath, Line: 5, Column: 20, IncludeContext: true);
        var result = await service.FindReferencesAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);

        var refs = result.Value!.References.Items;
        Assert.True(refs.Count >= 1, $"Expected at least 1 reference, got {refs.Count}");

        // Every reference with context should have ContextLine populated
        var callRef = refs.FirstOrDefault(r => r.Location.StartLine == 6);
        Assert.NotNull(callRef);
        Assert.NotNull(callRef.ContextLine);
        // GetContextLine trims, so it should be the trimmed line content
        Assert.Contains("Bar()", callRef.ContextLine);
    }

    // ─── Test 4: FindImplementations_FindsInterfaceImpl ───

    [Fact]
    public async Task FindImplementations_FindsInterfaceImpl()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public interface IService
        // Line 4:     {
        // Line 5:         void Execute();
        // Line 6:     }
        // Line 7:     public class MyService : IService
        // Line 8:     {
        // Line 9:         public void Execute() { }
        // Line 10:    }
        // Line 11: }
        var source = @"
namespace TestNs
{
    public interface IService
    {
        void Execute();
    }
    public class MyService : IService
    {
        public void Execute() { }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Service.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Service.cs", source);

        var service = CreateService(helper);

        // Point at "Execute" in interface declaration: line 5
        // "        void Execute();"
        //  01234567890123456789
        //  ^^^^^^^^               = 8 spaces
        //          ^^^^           = "void"
        //              ^          = space
        //               E         = col 13
        var request = new FindImplementationsRequest(filePath, Line: 5, Column: 13);
        var result = await service.FindImplementationsAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Execute", result.Value!.TargetSymbol.Name);

        var impls = result.Value.Implementations.Items;
        Assert.True(impls.Count >= 1, $"Expected at least 1 implementation, got {impls.Count}");

        // The implementation is in MyService at line 9
        var implItem = impls.FirstOrDefault(i => i.Symbol.Name == "Execute");
        Assert.NotNull(implItem);
        Assert.Equal(filePath, implItem.Location.FilePath);
    }

    // ─── Test 5: FindImplementations_FindsMultipleImplementations ───

    [Fact]
    public async Task FindImplementations_FindsMultipleImplementations()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public interface IProcessor
        // Line 4:     {
        // Line 5:         void Process();
        // Line 6:     }
        // Line 7:     public class FastProcessor : IProcessor
        // Line 8:     {
        // Line 9:         public void Process() { }
        // Line 10:    }
        // Line 11:    public class SlowProcessor : IProcessor
        // Line 12:    {
        // Line 13:        public void Process() { }
        // Line 14:    }
        // Line 15: }
        var source = @"
namespace TestNs
{
    public interface IProcessor
    {
        void Process();
    }
    public class FastProcessor : IProcessor
    {
        public void Process() { }
    }
    public class SlowProcessor : IProcessor
    {
        public void Process() { }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Processors.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Processors.cs", source);

        var service = CreateService(helper);

        // Point at "Process" in IProcessor: line 5
        // "        void Process();"
        //  0123456789012
        //  ^^^^^^^^       = 8 spaces
        //          ^^^^   = "void"
        //              ^  = space
        //               P = col 13
        var request = new FindImplementationsRequest(filePath, Line: 5, Column: 13);
        var result = await service.FindImplementationsAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Process", result.Value!.TargetSymbol.Name);

        var impls = result.Value.Implementations.Items;
        Assert.True(impls.Count >= 2, $"Expected at least 2 implementations, got {impls.Count}");

        var implNames = impls
            .Where(i => i.Symbol.ContainingType != null)
            .Select(i => i.Symbol.ContainingType!)
            .ToList();
        Assert.Contains(implNames, n => n.Contains("FastProcessor"));
        Assert.Contains(implNames, n => n.Contains("SlowProcessor"));
    }

    // ─── Test 6: FindCallers_FindsMethodCallers ───

    [Fact]
    public async Task FindCallers_FindsMethodCallers()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:         public void Target() { }
        // Line 6:         public void Caller() { Target(); }
        // Line 7:     }
        // Line 8: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void Target() { }
        public void Caller() { Target(); }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Point at "Target" declaration: line 5
        // "        public void Target() { }"
        //  01234567890123456789012
        //  ^^^^^^^^                 = 8 spaces
        //          ^^^^^^           = "public"
        //                ^          = space
        //                 ^^^^      = "void"
        //                     ^     = space
        //                      T    = col 20
        var request = new FindCallersRequest(filePath, Line: 5, Column: 20);
        var result = await service.FindCallersAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Target", result.Value!.TargetSymbol.Name);

        var callers = result.Value.Callers.Items;
        Assert.True(callers.Count >= 1, $"Expected at least 1 caller, got {callers.Count}");

        var callerItem = callers.FirstOrDefault(c => c.CallingSymbol.Name == "Caller");
        Assert.NotNull(callerItem);
        Assert.True(callerItem.IsDirect);
    }

    // ─── Test 7: FindCallees_FindsMethodCalls ───

    [Fact]
    public async Task FindCallees_FindsMethodCalls()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:         public void A() { B(); C(); }
        // Line 6:         public void B() { }
        // Line 7:         public void C() { }
        // Line 8:     }
        // Line 9: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void A() { B(); C(); }
        public void B() { }
        public void C() { }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Point at "A" declaration: line 5
        // "        public void A() { B(); C(); }"
        //  01234567890123456789012
        //  ^^^^^^^^                 = 8 spaces
        //          ^^^^^^           = "public"
        //                ^          = space
        //                 ^^^^      = "void"
        //                     ^     = space
        //                      A    = col 20
        var request = new FindCalleesRequest(filePath, Line: 5, Column: 20);
        var result = await service.FindCalleesAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("A", result.Value!.TargetSymbol.Name);

        var callees = result.Value.Callees.Items;
        Assert.True(callees.Count >= 2, $"Expected at least 2 callees, got {callees.Count}");

        var calleeNames = callees.Select(c => c.CalledSymbol.Name).ToList();
        Assert.Contains("B", calleeNames);
        Assert.Contains("C", calleeNames);
    }

    // ─── Test 8: FindCallees_ReturnsErrorForNonMethod ───

    [Fact]
    public async Task FindCallees_ReturnsErrorForNonMethod()
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
        var request = new FindCalleesRequest(filePath, Line: 3, Column: 17);
        var result = await service.FindCalleesAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("not a method", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Test 9: FindDefinition_FindsSourceDefinition ───

    [Fact]
    public async Task FindDefinition_FindsSourceDefinition()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Widget
        // Line 4:     {
        // Line 5:     }
        // Line 6:     public class Consumer
        // Line 7:     {
        // Line 8:         public Widget Create() { return new Widget(); }
        // Line 9:     }
        // Line 10: }
        var source = @"
namespace TestNs
{
    public class Widget
    {
    }
    public class Consumer
    {
        public Widget Create() { return new Widget(); }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Widget.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Widget.cs", source);

        var service = CreateService(helper);

        // Point at "Widget" usage as return type on line 8
        // "        public Widget Create() { return new Widget(); }"
        //  01234567890123456
        //  ^^^^^^^^           = 8 spaces
        //          ^^^^^^     = "public"
        //                ^    = space
        //                 W   = col 15
        var request = new FindDefinitionRequest(filePath, Line: 8, Column: 15);
        var result = await service.FindDefinitionAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Widget", result.Value!.Symbol.Name);

        var defs = result.Value.Definitions.Items;
        Assert.True(defs.Count >= 1, $"Expected at least 1 definition, got {defs.Count}");

        // Definition should point to the class declaration on line 3
        var sourceDef = defs.FirstOrDefault(d => !d.IsMetadataDefinition);
        Assert.NotNull(sourceDef);
        Assert.Equal(filePath, sourceDef.Location.FilePath);
        Assert.Equal(3, sourceDef.Location.StartLine);
    }

    // ─── Test 10: FindDefinition_HandlesPartialClass ───

    [Fact]
    public async Task FindDefinition_HandlesPartialClass()
    {
        // File1 (Part1.cs):
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public partial class Split
        // Line 4:     {
        // Line 5:         public void MethodA() { }
        // Line 6:     }
        // Line 7: }
        var source1 = @"
namespace TestNs
{
    public partial class Split
    {
        public void MethodA() { }
    }
}";

        // File2 (Part2.cs):
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public partial class Split
        // Line 4:     {
        // Line 5:         public void MethodB() { }
        // Line 6:     }
        // Line 7: }
        var source2 = @"
namespace TestNs
{
    public partial class Split
    {
        public void MethodB() { }
    }
}";

        // File3 (Usage.cs):
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Usage
        // Line 4:     {
        // Line 5:         public Split GetSplit() { return new Split(); }
        // Line 6:     }
        // Line 7: }
        var source3 = @"
namespace TestNs
{
    public class Usage
    {
        public Split GetSplit() { return new Split(); }
    }
}";

        var filePath1 = WorkspaceTestHelper.GetFilePath("TestProject", "Part1.cs");
        var filePath2 = WorkspaceTestHelper.GetFilePath("TestProject", "Part2.cs");
        var filePath3 = WorkspaceTestHelper.GetFilePath("TestProject", "Usage.cs");

        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Part1.cs", source1)
            .AddDocument("TestProject", "Part2.cs", source2)
            .AddDocument("TestProject", "Usage.cs", source3);

        var service = CreateService(helper);

        // Point at "Split" usage as return type on line 5 of Usage.cs
        // "        public Split GetSplit() { return new Split(); }"
        //  012345678901234
        //  ^^^^^^^^         = 8 spaces
        //          ^^^^^^   = "public"
        //                ^  = space
        //                 S = col 15
        var request = new FindDefinitionRequest(filePath3, Line: 5, Column: 15);
        var result = await service.FindDefinitionAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Split", result.Value!.Symbol.Name);

        var defs = result.Value.Definitions.Items;
        // Partial class should have 2 source definitions (one per file)
        Assert.True(defs.Count >= 2, $"Expected at least 2 definitions for partial class, got {defs.Count}");

        var defFiles = defs
            .Where(d => !d.IsMetadataDefinition)
            .Select(d => d.Location.FilePath)
            .OrderBy(f => f)
            .ToList();

        Assert.Contains(filePath1, defFiles);
        Assert.Contains(filePath2, defFiles);
    }
}

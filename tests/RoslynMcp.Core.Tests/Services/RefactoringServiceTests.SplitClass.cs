using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Refactor;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public partial class RefactoringServiceTests
{
    // ─── Test SC1: Simple split moves two methods ───

    [Fact]
    public async Task SplitClass_SimpleTwoMethods()
    {
        var source = @"
namespace TestNs
{
    public class Calculator
    {
        public int Add(int a, int b) { return a + b; }
        public int Sub(int a, int b) { return a - b; }
        public int Mul(int a, int b) { return a * b; }
        public int Div(int a, int b) { return a / b; }
    }
}";
        var (service, filePath) = SetupService(source, "Calculator.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Calculator");

        var request = new SplitClassRequest(filePath, line, col, "Operations",
            new[] { "Add", "Sub" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal("Operations", result.Value.NewClassName);
        Assert.Equal(2, result.Value.Preview.AffectedFiles.Count);

        // Source file change should contain "partial class"
        var sourceChange = result.Value.Preview.AffectedFiles.Single(f => f.FilePath == filePath);
        var sourceText = Assert.Single(sourceChange.Changes).NewText;
        Assert.Contains("partial class", sourceText);

        // Source should retain Mul and Div but not Add/Sub
        Assert.Contains("Mul", sourceText);
        Assert.DoesNotContain("Add(int a, int b)", sourceText);

        // Target file should contain both moved methods
        var targetChange = result.Value.Preview.AffectedFiles.Single(f => f.FilePath != filePath);
        var targetText = Assert.Single(targetChange.Changes).NewText;
        Assert.Contains("Add", targetText);
        Assert.Contains("Sub", targetText);
        // Target should use original class name, not NewClassName
        Assert.Contains("Calculator", targetText);
    }

    // ─── Test SC2: Already-partial class doesn't duplicate keyword ───

    [Fact]
    public async Task SplitClass_AlreadyPartial()
    {
        var source = @"
namespace TestNs
{
    public partial class Widget
    {
        public void Render() { }
        public void Update() { }
    }
}";
        var (service, filePath) = SetupService(source, "Widget.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Widget");

        var request = new SplitClassRequest(filePath, line, col, "Rendering",
            new[] { "Render" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);

        // Source should have exactly one "partial" keyword, not two
        var sourceFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath == filePath);
        var sourceText = Assert.Single(sourceFile.Changes).NewText;
        var partialCount = System.Text.RegularExpressions.Regex.Matches(sourceText, @"\bpartial\b").Count;
        Assert.Equal(1, partialCount);
    }

    // ─── Test SC3: Auto-generated target file path ───

    [Fact]
    public async Task SplitClass_AutoGeneratePath()
    {
        var source = @"
namespace TestNs
{
    public class Calculator
    {
        public void Add() { }
        public void Sub() { }
    }
}";
        var (service, filePath) = SetupService(source, "Calculator.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Calculator");

        var request = new SplitClassRequest(filePath, line, col, "Operations",
            new[] { "Add" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);

        // Target file path should be Calculator.Operations.cs
        var targetFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath != filePath);
        Assert.Single(targetFile.Changes);
        Assert.EndsWith("Calculator.Operations.cs", targetFile.FilePath);
    }

    // ─── Test SC4: Explicit target file path ───

    [Fact]
    public async Task SplitClass_ExplicitTargetPath()
    {
        var source = @"
namespace TestNs
{
    public class Calculator
    {
        public void Add() { }
        public void Sub() { }
    }
}";
        var (service, filePath) = SetupService(source, "Calculator.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Calculator");

        var targetPath = WorkspaceTestHelper.GetFilePath("TestProject", "CalcMath.cs");
        var request = new SplitClassRequest(filePath, line, col, "Math",
            new[] { "Add" }, targetPath);
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        var targetFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath != filePath);
        Assert.Single(targetFile.Changes);
        Assert.Equal(targetPath, targetFile.FilePath);
    }

    // ─── Test SC5: Member not found ───

    [Fact]
    public async Task SplitClass_MemberNotFound()
    {
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void Bar() { }
    }
}";
        var (service, filePath) = SetupService(source, "Foo.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Foo");

        var request = new SplitClassRequest(filePath, line, col, "Part",
            new[] { "NonExistent" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("NonExistent", result.Error.Message);
        Assert.Contains("not found", result.Error.Message);
    }

    // ─── Test SC6: Constructor rejected ───

    [Fact]
    public async Task SplitClass_ConstructorRejected()
    {
        var source = @"
namespace TestNs
{
    public class Service
    {
        public Service() { }
        public void Run() { }
    }
}";
        var (service, filePath) = SetupService(source, "Service.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Service");

        var request = new SplitClassRequest(filePath, line, col, "Part",
            new[] { ".ctor" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("Constructor", result.Error.Message);
    }

    // ─── Test SC7: Apply returns correct counts and changes ───

    [Fact]
    public async Task SplitClass_ApplyReturnsCorrectCounts()
    {
        var source = @"
namespace TestNs
{
    public class Calculator
    {
        public int Add(int a, int b) { return a + b; }
        public int Sub(int a, int b) { return a - b; }
        public int Mul(int a, int b) { return a * b; }
    }
}";
        var (service, filePath) = SetupService(source, "Calculator.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Calculator");

        var request = new SplitClassRequest(filePath, line, col, "Ops",
            new[] { "Add", "Sub" });
        var result = await service.ApplySplitClassAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.MembersMoved);
        Assert.Equal(2, result.Value.FilesChanged);
        Assert.NotNull(result.Value.Changes);
        Assert.Equal(2, result.Value.Changes.Count);

        // Verify source file content
        var sourceFile = result.Value.Changes.Single(f => f.FilePath == filePath);
        var sourceContent = Assert.Single(sourceFile.Changes).NewText;
        Assert.Contains("partial class", sourceContent);
        Assert.Contains("Mul", sourceContent);
        Assert.DoesNotContain("Add(int a, int b)", sourceContent);
        Assert.DoesNotContain("Sub(int a, int b)", sourceContent);

        // Verify target file content
        var targetFile = result.Value.Changes.Single(f => f.FilePath != filePath);
        var targetContent = Assert.Single(targetFile.Changes).NewText;
        Assert.Contains("partial class", targetContent);
        Assert.Contains("Add", targetContent);
        Assert.Contains("Sub", targetContent);
    }

    // ─── Test SC8: Properties and fields mix ───

    [Fact]
    public async Task SplitClass_PropertiesAndFields()
    {
        var source = @"
namespace TestNs
{
    public class Config
    {
        private int _timeout;
        public string Name { get; set; }
        public void Reset() { }
        public int Timeout => _timeout;
    }
}";
        var (service, filePath) = SetupService(source, "Config.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Config");

        var request = new SplitClassRequest(filePath, line, col, "Props",
            new[] { "_timeout", "Timeout" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);

        var targetFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath != filePath);
        var targetContent = Assert.Single(targetFile.Changes).NewText;
        Assert.Contains("_timeout", targetContent);
        Assert.Contains("Timeout", targetContent);
    }

    // ─── Test SC9: Generic class with type parameter (Finding #2, #12) ───

    [Fact]
    public async Task SplitClass_GenericSingleTypeParam()
    {
        var source = @"
namespace TestNs
{
    public class Repository<T>
    {
        public void Add(T item) { }
        public void Remove(T item) { }
    }
}";
        var (service, filePath) = SetupService(source, "Repository.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Repository");

        var request = new SplitClassRequest(filePath, line, col, "Commands",
            new[] { "Add" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);

        // Both partials must have <T>
        var sourceFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath == filePath);
        var targetFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath != filePath);
        Assert.Contains("<T>", Assert.Single(sourceFile.Changes).NewText);
        Assert.Contains("<T>", Assert.Single(targetFile.Changes).NewText);
    }

    // ─── Test SC10: Generic class with constraints (Finding #2, #12) ───

    [Fact]
    public async Task SplitClass_GenericWithConstraints()
    {
        var source = @"
namespace TestNs
{
    public class Container<T> where T : class
    {
        public void Store(T item) { }
        public T Retrieve() { return default; }
    }
}";
        var (service, filePath) = SetupService(source, "Container.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Container");

        var request = new SplitClassRequest(filePath, line, col, "Storage",
            new[] { "Store" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);

        // Both partials must have the constraint
        var sourceFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath == filePath);
        var targetFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath != filePath);
        Assert.Contains("where T : class", Assert.Single(sourceFile.Changes).NewText);
        Assert.Contains("where T : class", Assert.Single(targetFile.Changes).NewText);
    }

    // ─── Test SC11: Static class preserves static modifier (Finding #5) ───

    [Fact]
    public async Task SplitClass_StaticClass()
    {
        var source = @"
namespace TestNs
{
    public static class Utils
    {
        public static void Log(string msg) { }
        public static void Trace(string msg) { }
    }
}";
        var (service, filePath) = SetupService(source, "Utils.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Utils");

        var request = new SplitClassRequest(filePath, line, col, "Logging",
            new[] { "Log" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);

        // Both partials must be static
        var sourceFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath == filePath);
        var targetFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath != filePath);
        Assert.Contains("static partial class", Assert.Single(sourceFile.Changes).NewText);
        Assert.Contains("static partial class", Assert.Single(targetFile.Changes).NewText);
    }

    // ─── Test SC12: Struct split preserves type kind (Finding #4) ───

    [Fact]
    public async Task SplitClass_StructPreservesTypeKind()
    {
        var source = @"
namespace TestNs
{
    public struct Point
    {
        public int X;
        public int Y;
        public double Distance() { return System.Math.Sqrt(X * X + Y * Y); }
    }
}";
        var (service, filePath) = SetupService(source, "Point.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "struct Point");

        var request = new SplitClassRequest(filePath, line, col, "Methods",
            new[] { "Distance" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);

        // Target must be struct, not class
        var targetFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath != filePath);
        var targetText = Assert.Single(targetFile.Changes).NewText;
        Assert.Contains("partial struct", targetText);
        Assert.DoesNotContain("partial class", targetText);
    }

    // ─── Test SC13: Invalid NewClassName rejected (Finding #11) ───

    [Fact]
    public async Task SplitClass_InvalidNewClassNameRejected()
    {
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void Bar() { }
    }
}";
        var (service, filePath) = SetupService(source, "Foo.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Foo");

        // "class" is a C# keyword
        var request = new SplitClassRequest(filePath, line, col, "class",
            new[] { "Bar" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Equal("RESERVED_KEYWORD", result.Error!.ErrorCode);
    }

    // ─── Test SC14: Cancellation rethrows OperationCanceledException ───

    [Fact]
    public async Task SplitClass_CancellationRethrows()
    {
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void Bar() { }
        public void Baz() { }
    }
}";
        var (service, filePath) = SetupService(source, "Foo.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Foo");

        var request = new SplitClassRequest(filePath, line, col, "Part",
            new[] { "Bar" });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PreviewSplitClassAsync(request, cts.Token));
    }

    // --- Test SC-sec: Path traversal in TargetFilePath rejected ---

    [Fact]
    public async Task SplitClass_RejectsTraversalInTargetFilePath()
    {
        var source = @"
namespace TestNs
{
    public class Calculator
    {
        public int Add(int a, int b) { return a + b; }
        public int Sub(int a, int b) { return a - b; }
    }
}";
        var helper = new WorkspaceTestHelper()
            .WithSolutionPath(@"C:\test\TestProject.sln")
            .AddProject("TestProject")
            .AddDocument("TestProject", "Calculator.cs", source);
        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Calculator.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Calculator");

        var request = new SplitClassRequest(filePath, line, col, "Ops",
            new[] { "Add" }, TargetFilePath: @"C:\test\..\evil.cs");
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("PATH_OUTSIDE_SOLUTION", result.Error!.ErrorCode);
    }

    // --- Test SC15: All members moved leaves empty source class ---

    [Fact]
    public async Task SplitClass_AllMembersMoved_LeavesEmptySourceClass()
    {
        var source = @"
namespace TestNs
{
    public class Simple
    {
        public void OnlyMethod() { }
    }
}";
        var (service, filePath) = SetupService(source, "Simple.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Simple");

        var request = new SplitClassRequest(filePath, line, col, "Part",
            new[] { "OnlyMethod" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        var sourceFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath == filePath);
        var sourceText = Assert.Single(sourceFile.Changes).NewText;
        // Source class should be empty but still valid partial
        Assert.Contains("partial class Simple", sourceText);
        Assert.DoesNotContain("OnlyMethod", sourceText);
    }

    // --- Test SC16: Duplicate member names in request deduplicates ---

    [Fact]
    public async Task SplitClass_DuplicateMemberNames_Deduplicates()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public int Add(int a, int b) { return a + b; }
        public int Sub(int a, int b) { return a - b; }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Calc");

        var request = new SplitClassRequest(filePath, line, col, "Part",
            new[] { "Add", "Add" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        var targetFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath != filePath);
        var targetText = Assert.Single(targetFile.Changes).NewText;
        // Should have Add only once despite duplicate request
        var addCount = System.Text.RegularExpressions.Regex.Matches(targetText, @"(?:int|void|double)\s+Add\s*\(").Count;
        Assert.Equal(1, addCount);
    }

    // --- Test SC17: Overloaded methods are all moved ---

    [Fact]
    public async Task SplitClass_OverloadedMethods_MovesAllOverloads()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public int Add(int a, int b) { return a + b; }
        public double Add(double a, double b) { return a + b; }
        public int Sub(int a, int b) { return a - b; }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "class Calc");

        var request = new SplitClassRequest(filePath, line, col, "Math",
            new[] { "Add" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);

        // Both overloads should be in target
        var targetFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath != filePath);
        var targetText = Assert.Single(targetFile.Changes).NewText;
        Assert.Contains("int Add(int a, int b)", targetText);
        Assert.Contains("double Add(double a, double b)", targetText);

        // Source should not have Add
        var sourceFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath == filePath);
        var sourceText = Assert.Single(sourceFile.Changes).NewText;
        Assert.DoesNotContain("Add(", sourceText);
        Assert.Contains("Sub", sourceText);
    }

    // --- Test SC18: Record type preserves keyword and strips parameters ---

    [Fact]
    public async Task SplitClass_RecordType_PreservesRecordKeyword()
    {
        var source = @"
namespace TestNs
{
    public record Person(string Name, int Age)
    {
        public string Greet() { return $""Hello, {Name}""; }
        public bool IsAdult() { return Age >= 18; }
    }
}";
        var (service, filePath) = SetupService(source, "Person.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "record Person");

        var request = new SplitClassRequest(filePath, line, col, "Behaviors",
            new[] { "Greet" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);

        // Target should be partial record WITHOUT parameters
        var targetFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath != filePath);
        var targetText = Assert.Single(targetFile.Changes).NewText;
        Assert.Contains("partial record Person", targetText);
        Assert.DoesNotContain("string Name", targetText);
        Assert.Contains("Greet", targetText);

        // Source should retain parameters
        var sourceFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath == filePath);
        var sourceText = Assert.Single(sourceFile.Changes).NewText;
        Assert.Contains("partial record Person", sourceText);
        Assert.Contains("string Name", sourceText);
    }

    // --- Test SC19: Interface type preserves keyword ---

    [Fact]
    public async Task SplitClass_InterfaceType_PreservesInterfaceKeyword()
    {
        var source = @"
namespace TestNs
{
    public interface ICalculator
    {
        int Add(int a, int b);
        int Sub(int a, int b);
        void Reset();
    }
}";
        var (service, filePath) = SetupService(source, "ICalculator.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "interface ICalculator");

        var request = new SplitClassRequest(filePath, line, col, "Math",
            new[] { "Add", "Sub" });
        var result = await service.PreviewSplitClassAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);

        // Target should be partial interface
        var targetFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath != filePath);
        var targetText = Assert.Single(targetFile.Changes).NewText;
        Assert.Contains("partial interface ICalculator", targetText);
        Assert.Contains("Add", targetText);
        Assert.Contains("Sub", targetText);
        Assert.DoesNotContain("Reset", targetText);

        // Source should also be partial interface
        var sourceFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath == filePath);
        var sourceText = Assert.Single(sourceFile.Changes).NewText;
        Assert.Contains("partial interface ICalculator", sourceText);
        Assert.Contains("Reset", sourceText);
    }
}

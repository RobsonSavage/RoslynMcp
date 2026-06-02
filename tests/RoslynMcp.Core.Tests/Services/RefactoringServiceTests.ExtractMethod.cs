using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Refactor;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public partial class RefactoringServiceTests
{
    private static int GetLineLength(string source, int lineIndex)
    {
        var lines = source.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        return lineIndex < lines.Length ? lines[lineIndex].TrimEnd().Length : 0;
    }

    // ─── Test 10: Simple void extraction ───

    [Fact]
    public async Task ExtractMethod_SimpleVoid_TwoStatements()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public void Run()
        {
            System.Console.WriteLine(1);
            System.Console.WriteLine(2);
        }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "System.Console.WriteLine(1)");
        var (endLine, _) = WorkspaceTestHelper.FindPosition(source, "System.Console.WriteLine(2)");
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "PrintNums");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal("PrintNums", result.Value.MethodName);
        Assert.NotNull(result.Value.Preview);
        Assert.Single(result.Value.Preview.AffectedFiles);
        Assert.Equal(2, result.Value.Preview.TotalChanges);
    }

    // ─── Test 11: Extraction with parameters ───

    [Fact]
    public async Task ExtractMethod_WithParameters()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public void Run()
        {
            int x = 10;
            System.Console.WriteLine(x);
        }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "System.Console");
        var (endLine, _) = WorkspaceTestHelper.FindPosition(source, "WriteLine(x);");
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "PrintX");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal("PrintX", result.Value.MethodName);
        var insertChange = result.Value.Preview.AffectedFiles[0].Changes
            .Single(c => c.NewText.Contains("PrintX") && c.NewText.Contains("{"));
        Assert.Contains("x", insertChange.NewText);
    }

    // ─── Test 12: Extraction with return value ───

    [Fact]
    public async Task ExtractMethod_WithReturnValue()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public void Run()
        {
            int result = 0;
            result = 42;
            System.Console.WriteLine(result);
        }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "int result");
        var (endLine, _) = WorkspaceTestHelper.FindPosition(source, "result = 42;");
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "ComputeResult");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        var insertChange = result.Value.Preview.AffectedFiles[0].Changes
            .Single(c => c.NewText.Contains("ComputeResult") && c.NewText.Contains("{"));
        Assert.Contains("return", insertChange.NewText);
    }

    // ─── Test 13: Extraction with ref parameter ───

    [Fact]
    public async Task ExtractMethod_WithRefParameter()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public void Run()
        {
            int x = 1;
            x = x + 10;
            System.Console.WriteLine(x);
        }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "x = x + 10");
        var endLine = startLine;
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "AddTen");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        var insertChange = result.Value.Preview.AffectedFiles[0].Changes
            .Single(c => c.NewText.Contains("AddTen") && c.NewText.Contains("{"));
        Assert.Contains("ref", insertChange.NewText);
    }

    // ─── Test 14: Extraction with out-flowing variable as return ───

    [Fact]
    public async Task ExtractMethod_OutFlowingAsReturn()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public void Run()
        {
            int y;
            y = 99;
            System.Console.WriteLine(y);
        }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "y = 99");
        var endLine = startLine;
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "SetY");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        var insertChange = result.Value.Preview.AffectedFiles[0].Changes
            .Single(c => c.NewText.Contains("SetY") && c.NewText.Contains("{"));
        Assert.Contains("out", insertChange.NewText);
        Assert.Contains("int", insertChange.NewText);
        var replaceChange = result.Value.Preview.AffectedFiles[0].Changes
            .Single(c => c.NewText.Contains("SetY") && !c.NewText.Contains("{"));
        Assert.Contains("out", replaceChange.NewText);
        Assert.Contains("SetY", replaceChange.NewText);
    }

    // ─── Test 15: Async extraction ───

    [Fact]
    public async Task ExtractMethod_AsyncSelection()
    {
        var source = @"
using System.Threading.Tasks;
namespace TestNs
{
    public class Svc
    {
        public async Task RunAsync()
        {
            await Task.Delay(100);
        }
    }
}";
        var (service, filePath) = SetupService(source, "Svc.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "await Task.Delay");
        var endLine = startLine;
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "DelayAsync");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        var insertChange = result.Value.Preview.AffectedFiles[0].Changes
            .Single(c => c.NewText.Contains("DelayAsync") && c.NewText.Contains("{"));
        Assert.Contains("async", insertChange.NewText);
        Assert.Contains("Task", insertChange.NewText);
        var replaceChange = result.Value.Preview.AffectedFiles[0].Changes
            .Single(c => c.NewText.Contains("DelayAsync") && !c.NewText.Contains("{"));
        Assert.Contains("await", replaceChange.NewText);
    }

    // ─── Test 16: Static method ───

    [Fact]
    public async Task ExtractMethod_StaticEnclosing()
    {
        var source = @"
namespace TestNs
{
    public class Util
    {
        public static void DoWork()
        {
            int a = 1;
            int b = 2;
        }
    }
}";
        var (service, filePath) = SetupService(source, "Util.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "int a");
        var (endLine, _) = WorkspaceTestHelper.FindPosition(source, "int b = 2;");
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "InitVars");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        var insertChange = result.Value.Preview.AffectedFiles[0].Changes
            .Single(c => c.NewText.Contains("InitVars") && c.NewText.Contains("{"));
        Assert.Contains("static", insertChange.NewText);
    }

    // ─── Test 17: Sub-expression from initializer (relaxed parent check) ───

    [Fact]
    public async Task ExtractMethod_SubExpression_FromInitializer()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public void Run()
        {
            int a = 3;
            int b = 4;
            var max = System.Math.Max(a, b);
        }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "System.Math.Max");
        var endLine = startLine;
        var line = source.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[endLine];
        var endCol = line.IndexOf(')') + 1;

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "GetMax");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal("GetMax", result.Value.MethodName);
    }

    // ─── Test 18: Sub-expression from binary initializer (relaxed parent check) ───

    [Fact]
    public async Task ExtractMethod_SubExpression_FromBinaryInitializer()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public void Run()
        {
            int x = 5;
            int y = 10;
            var sum = x + y;
        }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "x + y");
        var endLine = startLine;
        var endCol = startCol + 5;

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "Add");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal("Add", result.Value.MethodName);
    }

    // ─── Test 18b: Sub-expression with unsupported parent rejected ───

    [Fact]
    public async Task ExtractMethod_SubExpression_UnsupportedParent_Rejected()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public void Run()
        {
            int count = 10;
            for (int i = 0; i < count; i++)
            {
                System.Console.WriteLine(i);
            }
        }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");

        // Select "i < count" inside the for-loop condition -- its parent is ForStatementSyntax (not in the allowed list)
        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "i < count");
        var endLine = startLine;
        var endCol = startCol + "i < count".Length;

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "CheckBound");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("Sub-expression", result.Error.Message);
    }

    // ─── Test 19: Extract from property getter ───

    [Fact]
    public async Task ExtractMethod_FromPropertyGetter()
    {
        var source = @"
namespace TestNs
{
    public class Props
    {
        private int _value = 10;
        public int Value
        {
            get
            {
                int temp = _value;
                return temp;
            }
        }
    }
}";
        var (service, filePath) = SetupService(source, "Props.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "int temp");
        var endLine = startLine;
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "GetTemp");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal("GetTemp", result.Value.MethodName);
    }

    // ─── Test 20: Extract from nested class ───

    [Fact]
    public async Task ExtractMethod_FromNestedClass()
    {
        var source = @"
namespace TestNs
{
    public class Outer
    {
        public class Inner
        {
            public void Work()
            {
                int z = 42;
            }
        }
    }
}";
        var (service, filePath) = SetupService(source, "Outer.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "int z = 42");
        var endLine = startLine;
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "InitZ");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal("InitZ", result.Value.MethodName);
    }

    // ─── Test 21: Existing variable assigned (not declared) after ───

    [Fact]
    public async Task ExtractMethod_ExistingVarAssignedAfter()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public void Run()
        {
            int result;
            result = 100;
            System.Console.WriteLine(result);
        }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "result = 100");
        var endLine = startLine;
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "SetResult");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        var replaceChange = result.Value.Preview.AffectedFiles[0].Changes
            .Single(c => c.NewText.Contains("SetResult") && !c.NewText.Contains("{"));
        Assert.Contains("result", replaceChange.NewText);
    }

    // ─── Test 22: Reject goto ───

    [Fact]
    public async Task ExtractMethod_RejectGoto()
    {
        var source = @"
namespace TestNs
{
    public class Bad
    {
        public void Run()
        {
            goto end;
            end:
            return;
        }
    }
}";
        var (service, filePath) = SetupService(source, "Bad.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "goto end");
        var endLine = startLine;
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "BadMethod");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("goto", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Test 23: Reject multiple out variables ───

    [Fact]
    public async Task ExtractMethod_RejectMultipleOutVars()
    {
        var source = @"
namespace TestNs
{
    public class Multi
    {
        public void Run()
        {
            int a = 1;
            int b = 2;
            System.Console.WriteLine(a);
            System.Console.WriteLine(b);
        }
    }
}";
        var (service, filePath) = SetupService(source, "Multi.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "int a = 1");
        var (endLine, _) = WorkspaceTestHelper.FindPosition(source, "int b = 2;");
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "InitBoth");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("multiple", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Test 24: Reject return in selection ───

    [Fact]
    public async Task ExtractMethod_RejectReturn()
    {
        var source = @"
namespace TestNs
{
    public class Ret
    {
        public int Compute()
        {
            return 42;
        }
    }
}";
        var (service, filePath) = SetupService(source, "Ret.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "return 42");
        var endLine = startLine;
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "GetValue");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("return", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Test 25: Invalid range ───

    [Fact]
    public async Task ExtractMethod_InvalidRange()
    {
        var source = @"
namespace TestNs
{
    public class Small
    {
        public void Run() { }
    }
}";
        var (service, filePath) = SetupService(source, "Small.cs");

        var request = new ExtractMethodRequest(filePath, StartLine: 100, StartColumn: 0, EndLine: 100, EndColumn: 5, MethodName: "Bad");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    // ─── Test 26: Reject unbalanced #if directive ───

    [Fact]
    public async Task ExtractMethod_RejectUnbalancedDirective()
    {
        var source = @"
namespace TestNs
{
    public class Cond
    {
        public void Run()
        {
#if true
            int a = 1;
#endif
            int b = 2;
        }
    }
}";
        var (service, filePath) = SetupService(source, "Cond.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "int a = 1");
        var endLine = startLine;
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "CondMethod");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("preprocessor", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Test 27: Ref on non-eligible symbol falls back to by-value ───

    [Fact]
    public async Task ExtractMethod_RefNonEligibleFallsBackToByValue()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public void Run()
        {
            int x = 5;
            System.Console.WriteLine(x);
        }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "System.Console");
        var endLine = startLine;
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "Print");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        var insertChange = result.Value.Preview.AffectedFiles[0].Changes
            .Single(c => c.NewText.Contains("Print") && c.NewText.Contains("{"));
        Assert.DoesNotContain("ref ", insertChange.NewText);
        Assert.DoesNotContain("out ", insertChange.NewText);
    }

    // ─── Test 28: Name collision appends numeric suffix ───

    [Fact]
    public async Task ExtractMethod_NameCollision()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public void Run()
        {
            int a = 1;
        }
        private void DoWork() { }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "int a = 1");
        var endLine = startLine;
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "DoWork");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.StartsWith("DoWork", result.Value.MethodName);
        Assert.NotEqual("DoWork", result.Value.MethodName);
    }

    // ─── Test 29: Apply returns correct FilesChanged and location ───

    [Fact]
    public async Task ExtractMethod_ApplyReturnsCorrectMeta()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public void Run()
        {
            int a = 1;
        }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "int a = 1");
        var endLine = startLine;
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "InitA");
        var result = await service.ApplyExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal(1, result.Value.FilesChanged);
        Assert.NotNull(result.Value.NewMethodLocation);
        Assert.Equal("InitA", result.Value.MethodName);
    }

    // ─── Test 30: Field access (this.x) not treated as parameter ───

    [Fact]
    public async Task ExtractMethod_FieldNotParameter()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        private int _field = 10;
        public void Run()
        {
            System.Console.WriteLine(_field);
        }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "System.Console.WriteLine(_field)");
        var endLine = startLine;
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "PrintField");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        var insertChange = result.Value.Preview.AffectedFiles[0].Changes
            .Single(c => c.NewText.Contains("PrintField") && c.NewText.Contains("{"));
        Assert.DoesNotContain("_field", insertChange.NewText.Split('(')[1]?.Split(')')[0] ?? "");
    }

    // ─── Test 32: Cancellation rethrows OperationCanceledException ───

    [Fact]
    public async Task ExtractMethod_CancellationRethrows()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public void Run()
        {
            int a = 1;
        }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "int a = 1");
        var endLine = startLine;
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "InitA");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PreviewExtractMethodAsync(request, cts.Token));
    }

    [Fact]
    public async Task ExtractMethod_RejectsKeywordAsMethodName()
    {
        var source = @"
namespace TestNs
{
    public class Calc
    {
        public void Run()
        {
            System.Console.WriteLine(1);
            System.Console.WriteLine(2);
        }
    }
}";
        var (service, filePath) = SetupService(source, "Calc.cs");

        var (startLine, startCol) = WorkspaceTestHelper.FindPosition(source, "System.Console.WriteLine(1)");
        var (endLine, _) = WorkspaceTestHelper.FindPosition(source, "System.Console.WriteLine(2)");
        var endCol = GetLineLength(source, endLine);

        var request = new ExtractMethodRequest(filePath, startLine, startCol, endLine, endCol, MethodName: "void");
        var result = await service.PreviewExtractMethodAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("keyword", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }
}

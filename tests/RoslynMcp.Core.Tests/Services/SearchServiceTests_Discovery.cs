using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Search;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class SearchServiceTests_Discovery : IDisposable
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    private SearchService CreateService(WorkspaceTestHelper helper)
    {
        var provider = helper.CreateProvider();
        var helpers = helper.CreateHelpers(_logger);
        return new SearchService(provider, helpers, _logger);
    }

    private SearchService CreateService(IWorkspaceProvider provider)
    {
        var helpers = new WorkspaceHelpers(provider, new SymbolResolver(_logger));
        return new SearchService(provider, helpers, _logger);
    }

    public void Dispose()
    {
        (_logger as IDisposable)?.Dispose();
    }

    // ────────────────────────────────────────────────────────────────────
    // 1. FindEntryPoints_FindsMainMethod
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindEntryPoints_FindsMainMethod()
    {
        // WorkspaceTestHelper defaults to OutputKind.DynamicallyLinkedLibrary,
        // but Compilation.GetEntryPoint() only works with ConsoleApplication.
        // Build the workspace manually with the correct OutputKind.

        var source = @"namespace ConsoleApp
{
    class Program
    {
        static void Main(string[] args)
        {
        }
    }
}";

        using var workspace = new AdhocWorkspace();

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };
        foreach (var dll in new[] { "System.Runtime.dll", "System.Console.dll", "netstandard.dll" })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path))
                refs.Add(MetadataReference.CreateFromFile(path));
        }

        var projectId = ProjectId.CreateNewId("ConsoleApp");
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "ConsoleApp",
            "ConsoleApp",
            LanguageNames.CSharp,
            metadataReferences: refs,
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            compilationOptions: new CSharpCompilationOptions(OutputKind.ConsoleApplication));

        workspace.AddProject(projectInfo);

        var docId = DocumentId.CreateNewId(projectId, "Program.cs");
        var filePath = @"C:\test\ConsoleApp\Program.cs";
        workspace.AddDocument(DocumentInfo.Create(
            docId, "Program.cs",
            loader: TextLoader.From(
                TextAndVersion.Create(SourceText.From(source), VersionStamp.Create(), filePath)),
            filePath: filePath));

        var provider = new TestWorkspaceProvider(workspace);
        var service = CreateService(provider);

        var result = await service.FindEntryPointsAsync(
            new FindEntryPointsRequest(PageSize: 50));

        Assert.True(result.IsSuccess);
        var response = result.Value!;
        Assert.True(response.EntryPoints.TotalCount >= 1,
            $"Expected at least 1 entry point, got {response.EntryPoints.TotalCount}");

        var mainEntry = response.EntryPoints.Items
            .FirstOrDefault(e => e.Kind == "Main");
        Assert.NotNull(mainEntry);
        Assert.Equal("Main", mainEntry.Symbol.Name);
    }

    // ────────────────────────────────────────────────────────────────────
    // 2. FindExtensionMethods_FindsByTypeName
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindExtensionMethods_FindsByTypeName()
    {
        var targetSource = @"namespace MyApp
{
    public class Customer
    {
        public string Name { get; set; }
    }
}";
        var extensionSource = @"namespace MyApp.Extensions
{
    public static class CustomerExtensions
    {
        public static string GetGreeting(this MyApp.Customer customer)
        {
            return ""Hello, "" + customer.Name;
        }

        public static string GetFarewell(this MyApp.Customer customer)
        {
            return ""Goodbye, "" + customer.Name;
        }
    }
}";

        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("MyApp")
            .AddDocument("MyApp", "Customer.cs", targetSource)
            .AddDocument("MyApp", "CustomerExtensions.cs", extensionSource);

        var service = CreateService(helper);

        var result = await service.FindExtensionMethodsAsync(
            new FindExtensionMethodsRequest(TypeName: "Customer", PageSize: 50));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Contains("Customer", response.TargetType);
        Assert.True(response.ExtensionMethods.TotalCount >= 2,
            $"Expected at least 2 extension methods, got {response.ExtensionMethods.TotalCount}");

        var methodNames = response.ExtensionMethods.Items
            .Select(m => m.Symbol.Name).ToList();
        Assert.Contains("GetGreeting", methodNames);
        Assert.Contains("GetFarewell", methodNames);

        // Verify extended type is populated
        foreach (var item in response.ExtensionMethods.Items)
        {
            Assert.False(string.IsNullOrEmpty(item.ExtendedType));
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // 3. FindAttributeUsages_FindsDecoratedMembers
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindAttributeUsages_FindsDecoratedMembers()
    {
        var attrSource = @"using System;

namespace MyApp
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class AuditAttribute : Attribute { }
}";
        var usageSource = @"namespace MyApp
{
    [Audit]
    public class OrderService
    {
        [Audit]
        public void PlaceOrder() { }

        public void CancelOrder() { }
    }
}";

        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("MyApp")
            .AddDocument("MyApp", "AuditAttribute.cs", attrSource)
            .AddDocument("MyApp", "OrderService.cs", usageSource);

        var service = CreateService(helper);

        var result = await service.FindAttributeUsagesAsync(
            new FindAttributeUsagesRequest(AttributeName: "Audit", PageSize: 50));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Equal("Audit", response.AttributeName);

        // Should find both usages: one on the class, one on the method
        Assert.True(response.Usages.TotalCount >= 2,
            $"Expected at least 2 attribute usages, got {response.Usages.TotalCount}");

        var decoratedNames = response.Usages.Items
            .Select(u => u.DecoratedSymbol.Name).ToList();
        Assert.Contains("OrderService", decoratedNames);
        Assert.Contains("PlaceOrder", decoratedNames);
    }

    // ────────────────────────────────────────────────────────────────────
    // 4. FindAttributeUsages_HandlesAttributeSuffix
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindAttributeUsages_HandlesAttributeSuffix()
    {
        // Define a custom attribute with full "Attribute" suffix.
        // Search for "Cacheable" (without suffix) should still find [Cacheable] usages
        // because the service appends "Attribute" before searching.
        var attrSource = @"using System;

namespace MyApp
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class CacheableAttribute : Attribute { }
}";
        var usageSource = @"namespace MyApp
{
    [Cacheable]
    public class ProductService { }

    [Cacheable]
    public class OrderService { }
}";

        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("MyApp")
            .AddDocument("MyApp", "CacheableAttribute.cs", attrSource)
            .AddDocument("MyApp", "Services.cs", usageSource);

        var service = CreateService(helper);

        // Search by short name (without "Attribute" suffix)
        var result = await service.FindAttributeUsagesAsync(
            new FindAttributeUsagesRequest(AttributeName: "Cacheable", PageSize: 50));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Equal("Cacheable", response.AttributeName);

        // Both [Cacheable] usages should be found
        Assert.True(response.Usages.TotalCount >= 2,
            $"Expected at least 2 Cacheable usages, got {response.Usages.TotalCount}");

        var decoratedNames = response.Usages.Items
            .Select(u => u.DecoratedSymbol.Name).ToList();
        Assert.Contains("ProductService", decoratedNames);
        Assert.Contains("OrderService", decoratedNames);
    }

    // ────────────────────────────────────────────────────────────────────
    // 5. FindTestsForType_FindsXunitTests
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindTestsForType_FindsXunitTests()
    {
        // Production class
        var fooSource = @"namespace MyApp
{
    public class Calculator
    {
        public int Add(int a, int b) => a + b;
        public int Subtract(int a, int b) => a - b;
    }
}";

        // Minimal xUnit attribute stubs so the attribute resolution works
        var xunitStub = @"namespace Xunit
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class FactAttribute : System.Attribute { }
}";

        // Test class referencing Calculator
        var testSource = @"using MyApp;

namespace MyApp.Tests
{
    public class CalculatorTests
    {
        [Xunit.Fact]
        public void Add_ReturnsSum()
        {
            var calc = new Calculator();
            var result = calc.Add(1, 2);
        }

        [Xunit.Fact]
        public void Subtract_ReturnsDifference()
        {
            var calc = new Calculator();
            var result = calc.Subtract(5, 3);
        }
    }
}";

        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("MyApp")
            .AddDocument("MyApp", "Calculator.cs", fooSource)
            .AddDocument("MyApp", "FactAttribute.cs", xunitStub)
            .AddDocument("MyApp", "CalculatorTests.cs", testSource);

        var service = CreateService(helper);

        var result = await service.FindTestsForTypeAsync(
            new FindTestsForTypeRequest(TypeName: "Calculator", PageSize: 50));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Contains("Calculator", response.TargetType);

        Assert.True(response.Tests.TotalCount >= 1,
            $"Expected at least 1 test class, got {response.Tests.TotalCount}");

        var testItem = response.Tests.Items.First();
        Assert.Equal("CalculatorTests", testItem.TestClass.Name);
        Assert.Equal("xUnit", testItem.TestFramework);
        Assert.Contains("Add_ReturnsSum", testItem.TestMethodNames);
        Assert.Contains("Subtract_ReturnsDifference", testItem.TestMethodNames);
    }

    // ────────────────────────────────────────────────────────────────────
    // 6. TextSearch_FindsLiteralMatches
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TextSearch_FindsLiteralMatches()
    {
        // Line 0: "namespace MyApp"
        // Line 1: "{"
        // Line 2: "    public class Foo"
        // Line 3: "    {"
        // Line 4: "        // MARKER found here"
        // Line 5: "    }"
        // Line 6: "}"
        var source1 = "namespace MyApp\n{\n    public class Foo\n    {\n        // MARKER found here\n    }\n}";

        // Line 0: "namespace MyApp"
        // Line 1: "{"
        // Line 2: "    public class Bar"
        // Line 3: "    {"
        // Line 4: "        public string Value = \"MARKER\";"
        // Line 5: "    }"
        // Line 6: "}"
        var source2 = "namespace MyApp\n{\n    public class Bar\n    {\n        public string Value = \"MARKER\";\n    }\n}";

        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("MyApp")
            .AddDocument("MyApp", "Foo.cs", source1)
            .AddDocument("MyApp", "Bar.cs", source2);

        var service = CreateService(helper);

        var result = await service.TextSearchAsync(
            new TextSearchRequest(Pattern: "MARKER", PageSize: 50));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Equal("MARKER", response.Pattern);

        // Should find "MARKER" in both files
        Assert.Equal(2, response.Matches.TotalCount);

        // Verify line numbers are correct (0-based)
        // In source1, "MARKER" appears on line 4, at column 11 ("        // MARKER" -> col 11)
        var fooMatch = response.Matches.Items
            .FirstOrDefault(m => m.FilePath.Contains("Foo.cs"));
        Assert.NotNull(fooMatch);
        Assert.Equal(4, fooMatch.Line);
        // "        // MARKER" -> spaces(8) + "//"(2) + " "(1) = column 11
        Assert.Equal(11, fooMatch.Column);

        // In source2, "MARKER" appears on line 4, inside the string literal
        var barMatch = response.Matches.Items
            .FirstOrDefault(m => m.FilePath.Contains("Bar.cs"));
        Assert.NotNull(barMatch);
        Assert.Equal(4, barMatch.Line);
    }

    // ────────────────────────────────────────────────────────────────────
    // 7. TextSearch_CaseSensitive
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TextSearch_CaseSensitive()
    {
        var source = "namespace MyApp\n{\n    // Hello world\n    // hello again\n    // HELLO LOUD\n}";

        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("MyApp")
            .AddDocument("MyApp", "Greet.cs", source);

        var service = CreateService(helper);

        // Case-insensitive search should find all 3
        var insensitiveResult = await service.TextSearchAsync(
            new TextSearchRequest(Pattern: "hello", CaseSensitive: false, PageSize: 50));

        Assert.True(insensitiveResult.IsSuccess, insensitiveResult.Error?.Message);
        Assert.Equal(3, insensitiveResult.Value!.Matches.TotalCount);

        // Case-sensitive search for lowercase "hello" should find only 1
        var sensitiveResult = await service.TextSearchAsync(
            new TextSearchRequest(Pattern: "hello", CaseSensitive: true, PageSize: 50));

        Assert.True(sensitiveResult.IsSuccess, sensitiveResult.Error?.Message);
        Assert.Equal(1, sensitiveResult.Value!.Matches.TotalCount);

        // The match should be on line 3 ("    // hello again")
        var match = sensitiveResult.Value!.Matches.Items[0];
        Assert.Equal(3, match.Line);
    }

    // ────────────────────────────────────────────────────────────────────
    // 8. TextSearch_RegexSearch
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TextSearch_RegexSearch()
    {
        var source = "namespace MyApp\n{\n    public int Count123 = 0;\n    public int Value456 = 1;\n    public string Name = \"test\";\n}";

        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("MyApp")
            .AddDocument("MyApp", "Data.cs", source);

        var service = CreateService(helper);

        // Regex to find identifiers ending with digits
        var result = await service.TextSearchAsync(
            new TextSearchRequest(Pattern: @"\b\w+\d{3}\b", IsRegex: true, PageSize: 50));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;

        // Should match "Count123" and "Value456"
        Assert.True(response.Matches.TotalCount >= 2,
            $"Expected at least 2 regex matches, got {response.Matches.TotalCount}");

        var matchedTexts = response.Matches.Items.Select(m => m.MatchedText).ToList();
        Assert.Contains("Count123", matchedTexts);
        Assert.Contains("Value456", matchedTexts);
    }

    // ────────────────────────────────────────────────────────────────────
    // 9. TextSearch_FilePatternFilter
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TextSearch_FilePatternFilter()
    {
        var source = "namespace MyApp\n{\n    // UNIQUE_TOKEN_XYZ\n}";

        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("MyApp")
            .AddDocument("MyApp", "Code.cs", source);

        var service = CreateService(helper);

        // FilePattern "*.cs" should match the .cs file
        var csResult = await service.TextSearchAsync(
            new TextSearchRequest(
                Pattern: "UNIQUE_TOKEN_XYZ",
                FilePattern: "*.cs",
                PageSize: 50));

        Assert.True(csResult.IsSuccess, csResult.Error?.Message);
        Assert.Equal(1, csResult.Value!.Matches.TotalCount);

        // FilePattern "*.txt" should find nothing in a workspace with only .cs files
        var txtResult = await service.TextSearchAsync(
            new TextSearchRequest(
                Pattern: "UNIQUE_TOKEN_XYZ",
                FilePattern: "*.txt",
                PageSize: 50));

        Assert.True(txtResult.IsSuccess, txtResult.Error?.Message);
        Assert.Equal(0, txtResult.Value!.Matches.TotalCount);
    }

    // ────────────────────────────────────────────────────────────────────
    // 10. TextSearch_InvalidRegex
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TextSearch_InvalidRegex()
    {
        var source = "namespace MyApp\n{\n}";

        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("MyApp")
            .AddDocument("MyApp", "Empty.cs", source);

        var service = CreateService(helper);

        // Invalid regex pattern with unmatched parenthesis
        var result = await service.TextSearchAsync(
            new TextSearchRequest(Pattern: @"(unclosed[", IsRegex: true, PageSize: 50));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("Invalid regex", result.Error!.Message);
    }
}

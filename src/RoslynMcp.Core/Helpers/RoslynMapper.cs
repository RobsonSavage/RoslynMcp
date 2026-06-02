using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Contracts = RoslynMcp.Shared.Contracts.Common;

namespace RoslynMcp.Core.Helpers;

public static class RoslynMapper
{
    private static readonly SymbolDisplayFormat s_qualifiedFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    public static Contracts.SymbolInfo ToSymbolInfo(ISymbol symbol)
    {
        return new Contracts.SymbolInfo(
            Name: symbol.Name,
            FullyQualifiedName: symbol.ToDisplayString(s_qualifiedFormat),
            Kind: GetSymbolKind(symbol),
            ContainingType: symbol.ContainingType?.ToDisplayString(s_qualifiedFormat),
            ContainingNamespace: symbol.ContainingNamespace?.IsGlobalNamespace == true
                ? null
                : symbol.ContainingNamespace?.ToDisplayString());
    }

    public static Contracts.CodeLocation? ToCodeLocation(Location location)
    {
        if (!location.IsInSource)
            return null;

        var span = location.GetLineSpan();
        return ToCodeLocation(span);
    }

    public static Contracts.CodeLocation ToCodeLocation(FileLinePositionSpan span)
    {
        return new Contracts.CodeLocation(
            FilePath: span.Path,
            StartLine: span.StartLinePosition.Line,
            StartColumn: span.StartLinePosition.Character,
            EndLine: span.EndLinePosition.Line,
            EndColumn: span.EndLinePosition.Character);
    }

    public static Contracts.CodeRange ToCodeRange(TextSpan span, SourceText text)
    {
        var start = text.Lines.GetLinePosition(span.Start);
        var end = text.Lines.GetLinePosition(span.End);
        return new Contracts.CodeRange(start.Line, start.Character, end.Line, end.Character);
    }

    public static Contracts.ParameterInfo ToParameterInfo(IParameterSymbol param)
    {
        return new Contracts.ParameterInfo(
            Name: param.Name,
            Type: param.Type.ToDisplayString(),
            IsOptional: param.IsOptional,
            DefaultValue: param.HasExplicitDefaultValue ? param.ExplicitDefaultValue?.ToString() : null,
            IsParams: param.IsParams,
            IsRef: param.RefKind == RefKind.Ref,
            IsOut: param.RefKind == RefKind.Out);
    }

    public static Contracts.MemberSummary ToMemberSummary(ISymbol member)
    {
        var returnType = member switch
        {
            IMethodSymbol m => m.ReturnType.ToDisplayString(),
            IPropertySymbol p => p.Type.ToDisplayString(),
            IFieldSymbol f => f.Type.ToDisplayString(),
            IEventSymbol e => e.Type.ToDisplayString(),
            _ => "void"
        };

        return new Contracts.MemberSummary(
            Name: member.Name,
            Kind: GetSymbolKind(member),
            ReturnType: returnType,
            Accessibility: member.DeclaredAccessibility.ToString(),
            IsStatic: member.IsStatic,
            IsAbstract: member.IsAbstract,
            IsVirtual: member.IsVirtual,
            IsOverride: member.IsOverride);
    }

    public static string? GetContextLine(SourceText text, int line)
    {
        if (line < 0 || line >= text.Lines.Count)
            return null;
        return text.Lines[line].ToString().Trim();
    }

    public static string GetSymbolKind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol t => t.TypeKind switch
        {
            TypeKind.Class => "Class",
            TypeKind.Interface => "Interface",
            TypeKind.Struct => "Struct",
            TypeKind.Enum => "Enum",
            TypeKind.Delegate => "Delegate",
            _ => "Type"
        },
        IMethodSymbol => "Method",
        IPropertySymbol => "Property",
        IFieldSymbol => "Field",
        IEventSymbol => "Event",
        INamespaceSymbol => "Namespace",
        IParameterSymbol => "Parameter",
        ILocalSymbol => "Local",
        _ => symbol.Kind.ToString()
    };

    /// <summary>
    /// Walk up the syntax tree from a position to find the enclosing member and type declarations.
    /// </summary>
    public static (string? MemberName, string? TypeName) GetEnclosingDeclaration(
        SemanticModel model, SyntaxNode? node, CancellationToken ct)
    {
        while (node != null)
        {
            var symbol = model.GetDeclaredSymbol(node, ct);
            if (symbol is IMethodSymbol or IPropertySymbol or IEventSymbol or IFieldSymbol)
                return (symbol.Name, symbol.ContainingType?.ToDisplayString(s_qualifiedFormat));
            if (symbol is INamedTypeSymbol typeSymbol)
                return (null, typeSymbol.ToDisplayString(s_qualifiedFormat));
            node = node.Parent;
        }
        return (null, null);
    }

    /// <summary>
    /// Check if a reference location is a write access (assignment LHS, out/ref arg, increment/decrement).
    /// </summary>
    public static bool IsWriteAccess(SyntaxNode node)
    {
        var parent = node.Parent;
        if (parent is AssignmentExpressionSyntax assignment && assignment.Left.Span.Contains(node.Span))
            return true;
        if (parent is ArgumentSyntax arg && arg.RefOrOutKeyword.RawKind != 0)
            return true;
        if (parent is PostfixUnaryExpressionSyntax or PrefixUnaryExpressionSyntax)
            return true;
        return false;
    }

    /// <summary>
    /// Check if a type symbol is a test class (has [TestClass], [TestFixture], or contains test methods).
    /// </summary>
    public static bool IsTestClass(INamedTypeSymbol type)
    {
        return type.GetAttributes().Any(a =>
        {
            var name = a.AttributeClass?.Name;
            return name is "TestClassAttribute" or "TestFixtureAttribute";
        }) || type.GetMembers().OfType<IMethodSymbol>().Any(IsTestMethod);
    }

    /// <summary>
    /// Check if a method is a test method ([Fact], [Theory], [Test], [TestMethod]).
    /// </summary>
    public static bool IsTestMethod(IMethodSymbol method)
    {
        return method.GetAttributes().Any(a =>
        {
            var name = a.AttributeClass?.Name;
            return name is "TestMethodAttribute" or "TestAttribute" or "FactAttribute" or "TheoryAttribute";
        });
    }
}

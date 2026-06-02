using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Helpers;

/// <summary>
/// Walks a syntax tree collecting all symbols invoked or constructed.
/// Shared between SearchService.FindCalleesAsync and AnalyzeService.UnderstandMethodAsync.
/// </summary>
internal sealed class CalleeCollector : CSharpSyntaxWalker
{
    private readonly SemanticModel _model;
    private readonly CancellationToken _ct;
    public List<(ISymbol Symbol, Location CallSite)> Callees { get; } = new List<(ISymbol, Location)>();

    public CalleeCollector(SemanticModel model, CancellationToken ct)
    {
        _model = model;
        _ct = ct;
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        _ct.ThrowIfCancellationRequested();
        var info = _model.GetSymbolInfo(node, _ct);
        if (info.Symbol != null)
            Callees.Add((info.Symbol, node.GetLocation()));
        base.VisitInvocationExpression(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        _ct.ThrowIfCancellationRequested();
        var info = _model.GetSymbolInfo(node, _ct);
        if (info.Symbol != null)
            Callees.Add((info.Symbol, node.GetLocation()));
        base.VisitObjectCreationExpression(node);
    }

    public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        _ct.ThrowIfCancellationRequested();
        var info = _model.GetSymbolInfo(node, _ct);
        if (info.Symbol != null)
            Callees.Add((info.Symbol, node.GetLocation()));
        base.VisitElementAccessExpression(node);
    }

    public override void VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        _ct.ThrowIfCancellationRequested();
        var info = _model.GetSymbolInfo(node, _ct);
        if (info.Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator })
            Callees.Add((info.Symbol, node.GetLocation()));
        base.VisitBinaryExpression(node);
    }

    public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        _ct.ThrowIfCancellationRequested();
        var info = _model.GetSymbolInfo(node, _ct);
        if (info.Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator })
            Callees.Add((info.Symbol, node.GetLocation()));
        base.VisitPrefixUnaryExpression(node);
    }

    public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        _ct.ThrowIfCancellationRequested();
        var info = _model.GetSymbolInfo(node, _ct);
        if (info.Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator })
            Callees.Add((info.Symbol, node.GetLocation()));
        base.VisitPostfixUnaryExpression(node);
    }
}

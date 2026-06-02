using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared.Contracts.Analyze;

namespace RoslynMcp.Core.Services;

internal sealed class PerformanceIssueWalker : CSharpSyntaxWalker
{
    private readonly SemanticModel _model;
    private readonly CancellationToken _ct;
    private int _loopDepth;
    public List<PerformanceIssue> Issues { get; } = new List<PerformanceIssue>();

    private static readonly HashSet<string> LinqMethodNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Where", "Select", "SelectMany", "OrderBy", "OrderByDescending",
        "ThenBy", "ThenByDescending", "GroupBy", "Join", "GroupJoin",
        "Distinct", "Union", "Intersect", "Except", "Concat",
        "Zip", "Skip", "Take", "SkipWhile", "TakeWhile",
        "First", "FirstOrDefault", "Last", "LastOrDefault",
        "Single", "SingleOrDefault", "Any", "All", "Count",
        "Sum", "Min", "Max", "Average", "Aggregate",
        "ToList", "ToArray", "ToDictionary", "ToLookup"
    };

    public PerformanceIssueWalker(SemanticModel model, CancellationToken ct)
    {
        _model = model;
        _ct = ct;
    }

    public override void VisitForStatement(ForStatementSyntax node)
    {
        _loopDepth++;
        base.VisitForStatement(node);
        _loopDepth--;
    }

    public override void VisitForEachStatement(ForEachStatementSyntax node)
    {
        _loopDepth++;
        base.VisitForEachStatement(node);
        _loopDepth--;
    }

    public override void VisitWhileStatement(WhileStatementSyntax node)
    {
        _loopDepth++;
        base.VisitWhileStatement(node);
        _loopDepth--;
    }

    public override void VisitDoStatement(DoStatementSyntax node)
    {
        _loopDepth++;
        base.VisitDoStatement(node);
        _loopDepth--;
    }

    public override void VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        _ct.ThrowIfCancellationRequested();

        // StringConcatInLoop: string + inside loop
        if (_loopDepth > 0 && node.IsKind(SyntaxKind.AddExpression))
        {
            var typeInfo = _model.GetTypeInfo(node, _ct);
            if (typeInfo.Type?.SpecialType == SpecialType.System_String)
            {
                var location = RoslynMapper.ToCodeLocation(node.GetLocation());
                if (location != null)
                {
                    Issues.Add(new PerformanceIssue(
                        "StringConcatInLoop",
                        "String concatenation with + inside a loop creates unnecessary allocations. Use StringBuilder instead.",
                        "Warning",
                        location,
                        "Use StringBuilder for string concatenation in loops"));
                }
            }
        }

        base.VisitBinaryExpression(node);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        _ct.ThrowIfCancellationRequested();

        // LinqInLoop: LINQ method calls inside loops
        if (_loopDepth > 0 && node.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var methodName = memberAccess.Name.Identifier.Text;
            if (LinqMethodNames.Contains(methodName))
            {
                var symbolInfo = _model.GetSymbolInfo(node, _ct);
                if (symbolInfo.Symbol is IMethodSymbol methodSym)
                {
                    var containingNs = methodSym.ContainingType?.ContainingNamespace?.ToDisplayString();
                    if (containingNs != null &&
                        (containingNs.StartsWith("System.Linq") || methodSym.ContainingType?.Name == "Enumerable"))
                    {
                        var location = RoslynMapper.ToCodeLocation(node.GetLocation());
                        if (location != null)
                        {
                            Issues.Add(new PerformanceIssue(
                                "LinqInLoop",
                                $"LINQ method '{methodName}' called inside a loop may cause excessive allocations and evaluations.",
                                "Info",
                                location,
                                "Consider materializing the collection before the loop or using a for/foreach loop instead"));
                        }
                    }
                }
            }
        }

        base.VisitInvocationExpression(node);
    }

    public override void VisitCastExpression(CastExpressionSyntax node)
    {
        _ct.ThrowIfCancellationRequested();

        // BoxingInHotPath: value type cast to object/interface inside loops
        if (_loopDepth > 0)
        {
            CheckBoxing(node.Expression, node.Type, node.GetLocation());
        }

        base.VisitCastExpression(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        _ct.ThrowIfCancellationRequested();

        // LargeObjectAllocation: new array/list with > 85000 literal size
        if (node.ArgumentList?.Arguments.Count == 1)
        {
            var arg = node.ArgumentList.Arguments[0].Expression;
            if (arg is LiteralExpressionSyntax literal && literal.Token.Value is int size && size > 85000)
            {
                var typeInfo = _model.GetTypeInfo(node, _ct);
                var typeName = typeInfo.Type?.Name ?? "";
                if (typeName.Contains("List") || typeName.Contains("Array") || typeName.Contains("Dictionary"))
                {
                    var location = RoslynMapper.ToCodeLocation(node.GetLocation());
                    if (location != null)
                    {
                        Issues.Add(new PerformanceIssue(
                            "LargeObjectAllocation",
                            $"Large object allocation ({size} elements) will be placed on the Large Object Heap, which has expensive GC characteristics.",
                            "Warning",
                            location,
                            "Consider using ArrayPool<T> or chunked processing"));
                    }
                }
            }
        }

        base.VisitObjectCreationExpression(node);
    }

    public override void VisitArrayCreationExpression(ArrayCreationExpressionSyntax node)
    {
        _ct.ThrowIfCancellationRequested();

        // LargeObjectAllocation: new T[85001]
        foreach (var rankSpec in node.Type.RankSpecifiers)
        {
            foreach (var sizeExpr in rankSpec.Sizes)
            {
                if (sizeExpr is LiteralExpressionSyntax literal && literal.Token.Value is int size && size > 85000)
                {
                    var location = RoslynMapper.ToCodeLocation(node.GetLocation());
                    if (location != null)
                    {
                        Issues.Add(new PerformanceIssue(
                            "LargeObjectAllocation",
                            $"Large array allocation ({size} elements) will be placed on the Large Object Heap.",
                            "Warning",
                            location,
                            "Consider using ArrayPool<T> or chunked processing"));
                    }
                }
            }
        }

        base.VisitArrayCreationExpression(node);
    }

    private void CheckBoxing(ExpressionSyntax expression, TypeSyntax targetType, Location location)
    {
        var sourceType = _model.GetTypeInfo(expression, _ct).Type;
        var destType = _model.GetTypeInfo(targetType, _ct).Type;

        if (sourceType != null && destType != null
            && sourceType.IsValueType
            && (destType.SpecialType == SpecialType.System_Object || destType.TypeKind == TypeKind.Interface))
        {
            var codeLoc = RoslynMapper.ToCodeLocation(location);
            if (codeLoc != null)
            {
                Issues.Add(new PerformanceIssue(
                    "BoxingInHotPath",
                    $"Value type '{sourceType.Name}' boxed to '{destType.Name}' inside a loop causes heap allocation per iteration.",
                    "Info",
                    codeLoc,
                    "Use generic constraints or concrete types to avoid boxing"));
            }
        }
    }
}

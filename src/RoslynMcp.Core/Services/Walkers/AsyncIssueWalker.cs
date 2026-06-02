using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared.Contracts.Analyze;

namespace RoslynMcp.Core.Services;

internal sealed class AsyncIssueWalker : CSharpSyntaxWalker
{
    private readonly SemanticModel _model;
    private readonly CancellationToken _ct;
    public List<AsyncIssue> Issues { get; } = new List<AsyncIssue>();

    public AsyncIssueWalker(SemanticModel model, CancellationToken ct)
    {
        _model = model;
        _ct = ct;
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        _ct.ThrowIfCancellationRequested();
        var methodSymbol = _model.GetDeclaredSymbol(node, _ct) as IMethodSymbol;

        if (node.Modifiers.Any(SyntaxKind.AsyncKeyword))
        {
            // AsyncVoid: async void methods (not event handlers)
            if (methodSymbol != null && methodSymbol.ReturnsVoid)
            {
                bool isEventHandler = methodSymbol.Parameters.Length == 2
                    && methodSymbol.Parameters[0].Type.SpecialType == SpecialType.System_Object
                    && (methodSymbol.Parameters[1].Type.Name.EndsWith("EventArgs")
                        || IsEventArgsType(methodSymbol.Parameters[1].Type));
                if (!isEventHandler)
                {
                    var location = RoslynMapper.ToCodeLocation(node.Identifier.GetLocation());
                    if (location != null)
                    {
                        Issues.Add(new AsyncIssue(
                            "AsyncVoid",
                            $"Async void method '{methodSymbol.Name}' - exceptions cannot be caught by callers",
                            methodSymbol != null ? RoslynMapper.ToSymbolInfo(methodSymbol) : null,
                            location));
                    }
                }
            }

            // MissingAwait: async methods with no await expressions
            bool hasAwait = node.DescendantNodes().OfType<AwaitExpressionSyntax>().Any();
            if (!hasAwait)
            {
                var location = RoslynMapper.ToCodeLocation(node.Identifier.GetLocation());
                if (location != null)
                {
                    Issues.Add(new AsyncIssue(
                        "MissingAwait",
                        $"Async method '{node.Identifier.Text}' contains no await expressions",
                        methodSymbol != null ? RoslynMapper.ToSymbolInfo(methodSymbol) : null,
                        location));
                }
            }
        }

        base.VisitMethodDeclaration(node);
    }

    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        _ct.ThrowIfCancellationRequested();

        // SyncOverAsync: .Result or .Wait() on Task
        var memberName = node.Name.Identifier.Text;
        if (memberName == "Result")
        {
            var typeInfo = _model.GetTypeInfo(node.Expression, _ct);
            if (IsTaskType(typeInfo.Type))
            {
                var location = RoslynMapper.ToCodeLocation(node.Name.GetLocation());
                if (location != null)
                {
                    Issues.Add(new AsyncIssue(
                        "SyncOverAsync",
                        "Accessing .Result on a Task blocks the calling thread and can cause deadlocks",
                        null,
                        location));
                }
            }
        }

        base.VisitMemberAccessExpression(node);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        _ct.ThrowIfCancellationRequested();

        // SyncOverAsync: .Wait() on Task
        if (node.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var memberName = memberAccess.Name.Identifier.Text;
            if (memberName == "Wait")
            {
                var typeInfo = _model.GetTypeInfo(memberAccess.Expression, _ct);
                if (IsTaskType(typeInfo.Type))
                {
                    var location = RoslynMapper.ToCodeLocation(memberAccess.Name.GetLocation());
                    if (location != null)
                    {
                        Issues.Add(new AsyncIssue(
                            "SyncOverAsync",
                            "Calling .Wait() on a Task blocks the calling thread and can cause deadlocks",
                            null,
                            location));
                    }
                }
            }
        }

        // FireAndForget: async calls not awaited
        var symbolInfo = _model.GetSymbolInfo(node, _ct);
        if (symbolInfo.Symbol is IMethodSymbol calledMethod && IsTaskReturning(calledMethod))
        {
            // Check if the invocation is being awaited
            var parent = node.Parent;
            bool isAwaited = parent is AwaitExpressionSyntax;
            bool isAssigned = parent is AssignmentExpressionSyntax
                || parent is EqualsValueClauseSyntax
                || parent is ReturnStatementSyntax
                || parent is ArgumentSyntax;

            if (!isAwaited && !isAssigned)
            {
                // It's an expression statement - fire and forget
                if (parent is ExpressionStatementSyntax)
                {
                    var location = RoslynMapper.ToCodeLocation(node.GetLocation());
                    if (location != null)
                    {
                        Issues.Add(new AsyncIssue(
                            "FireAndForget",
                            $"Task-returning method '{calledMethod.Name}' called without await - exceptions will be lost",
                            RoslynMapper.ToSymbolInfo(calledMethod),
                            location));
                    }
                }
            }
        }

        base.VisitInvocationExpression(node);
    }

    private static bool IsEventArgsType(ITypeSymbol type)
    {
        var current = type;
        while (current != null)
        {
            if (current.Name == "EventArgs" && current.ContainingNamespace?.ToDisplayString() == "System")
                return true;
            current = current.BaseType;
        }
        return false;
    }

    private static bool IsTaskType(ITypeSymbol? type)
    {
        if (type is null) return false;
        var name = type.ToDisplayString();
        return name.StartsWith("System.Threading.Tasks.Task")
            || name.StartsWith("System.Threading.Tasks.ValueTask");
    }

    private static bool IsTaskReturning(IMethodSymbol method)
    {
        return IsTaskType(method.ReturnType);
    }
}

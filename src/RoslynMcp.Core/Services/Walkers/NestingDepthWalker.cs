using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Services;

internal sealed class NestingDepthWalker : CSharpSyntaxWalker
{
    private int _currentDepth;
    public int MaxDepth { get; private set; }

    public override void VisitBlock(BlockSyntax node)
    {
        _currentDepth++;
        if (_currentDepth > MaxDepth) MaxDepth = _currentDepth;
        base.VisitBlock(node);
        _currentDepth--;
    }
}

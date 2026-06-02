using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Shared;
using Serilog;

namespace RoslynMcp.Core.Services;

public class SymbolResolver
{
    private readonly ILogger _logger;

    public SymbolResolver(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<Result<ISymbol>> ResolveSymbolAsync(
        Document document,
        int line,
        int column,
        CancellationToken ct = default)
    {
        var syntaxTree = await document.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
        if (syntaxTree is null)
            return Result<ISymbol>.Fail("Could not get syntax tree");

        var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (semanticModel is null)
            return Result<ISymbol>.Fail("Could not get semantic model");

        var text = await syntaxTree.GetTextAsync(ct).ConfigureAwait(false);

        if (line < 0 || line >= text.Lines.Count)
            return Result<ISymbol>.Fail(
                $"Line {line} is out of range (file has {text.Lines.Count} lines)",
                "OUT_OF_RANGE");

        var lineLength = text.Lines[line].End - text.Lines[line].Start;
        if (column < 0)
            column = 0;
        else if (column > lineLength)
            column = lineLength;

        var position = text.Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(line, column));
        var node = (await syntaxTree.GetRootAsync(ct).ConfigureAwait(false)).FindToken(position).Parent;

        if (node is null)
            return Result<ISymbol>.Fail("No syntax node found at position");

        var symbolInfo = semanticModel.GetSymbolInfo(node, ct);
        var symbol = symbolInfo.Symbol ?? semanticModel.GetDeclaredSymbol(node, ct);

        if (symbol is null)
            return Result<ISymbol>.Fail("No symbol found at position");


        return Result<ISymbol>.Ok(symbol);
    }
}

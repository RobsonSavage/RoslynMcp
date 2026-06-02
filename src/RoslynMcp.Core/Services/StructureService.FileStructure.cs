using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Common;
using RoslynMcp.Shared.Contracts.Structure;

namespace RoslynMcp.Core.Services;

public partial class StructureService
{
    // ── 3. get_file_outline ──

    public async Task<Result<FileOutlineResponse>> GetFileOutlineAsync(
        GetFileOutlineRequest request, CancellationToken ct = default)
    {
        if (!_workspace.HasSolution)
        {
            _logger.Warning("get_file_outline: No solution loaded");
            return Result<FileOutlineResponse>.Fail("No solution loaded");
        }

        var doc = await _workspace.GetDocumentAsync(request.FilePath, ct: ct).ConfigureAwait(false);
        if (doc is null)
        {
            _logger.Warning("get_file_outline: Document not found: {FilePath}", request.FilePath);
            return Result<FileOutlineResponse>.Fail($"Document not found: {request.FilePath}");
        }

        var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);

        if (root is null || model is null)
            return Result<FileOutlineResponse>.Fail("Could not get syntax tree or semantic model");

        var items = CollectOutlineItems(root, model, text, ct);

        return new FileOutlineResponse(request.FilePath, items);
    }

    // ── 5. get_types_in_file ──

    public async Task<Result<TypesInFileResponse>> GetTypesInFileAsync(
        GetTypesInFileRequest request, CancellationToken ct = default)
    {
        if (!_workspace.HasSolution)
        {
            _logger.Warning("get_types_in_file: No solution loaded");
            return Result<TypesInFileResponse>.Fail("No solution loaded");
        }

        var doc = await _workspace.GetDocumentAsync(request.FilePath, ct: ct).ConfigureAwait(false);
        if (doc is null)
        {
            _logger.Warning("get_types_in_file: Document not found: {FilePath}", request.FilePath);
            return Result<TypesInFileResponse>.Fail($"Document not found: {request.FilePath}");
        }

        var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);

        if (root is null || model is null)
            return Result<TypesInFileResponse>.Fail("Could not get syntax tree or semantic model");

        var types = new List<TypeSummary>();
        var typeDecls = request.IncludeNested
            ? root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            : root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                .Where(t => t.Parent is not TypeDeclarationSyntax);

        foreach (var typeDecl in typeDecls)
        {
            ct.ThrowIfCancellationRequested();
            var symbol = model.GetDeclaredSymbol(typeDecl, ct) as INamedTypeSymbol;
            if (symbol is null) continue;

            types.Add(new TypeSummary(
                Name: symbol.Name,
                FullyQualifiedName: symbol.ToDisplayString(),
                Kind: RoslynMapper.GetSymbolKind(symbol),
                Accessibility: symbol.DeclaredAccessibility.ToString(),
                Range: RoslynMapper.ToCodeRange(typeDecl.Span, text),
                IsPartial: typeDecl is TypeDeclarationSyntax td && td.Modifiers.Any(SyntaxKind.PartialKeyword),
                IsAbstract: symbol.IsAbstract,
                IsStatic: symbol.IsStatic));
        }

        return new TypesInFileResponse(request.FilePath, types);
    }

    #region Private Helpers

    private const int MaxOutlineDepth = 20;

    private static IReadOnlyList<OutlineItem> CollectOutlineItems(
        SyntaxNode parent, SemanticModel model, SourceText text, CancellationToken ct,
        int depth = 0)
    {
        if (depth >= MaxOutlineDepth)
            return Array.Empty<OutlineItem>();

        var items = new List<OutlineItem>();

        foreach (var child in parent.ChildNodes())
        {
            ct.ThrowIfCancellationRequested();

            if (child is BaseNamespaceDeclarationSyntax)
            {
                // Flatten namespaces - recurse into their children
                items.AddRange(CollectOutlineItems(child, model, text, ct, depth + 1));
            }
            else if (child is EnumDeclarationSyntax enumDecl)
            {
                var symbol = model.GetDeclaredSymbol(enumDecl, ct);
                if (symbol is null) continue;

                var enumMembers = new List<OutlineItem>();
                foreach (var member in enumDecl.Members)
                {
                    var memberSymbol = model.GetDeclaredSymbol(member, ct);
                    if (memberSymbol is null) continue;
                    enumMembers.Add(CreateLeafOutlineItem(memberSymbol, "EnumMember", null, member.Span, text));
                }

                items.Add(new OutlineItem(
                    symbol.Name, "Enum", null,
                    symbol.DeclaredAccessibility.ToString(),
                    RoslynMapper.ToCodeRange(enumDecl.Span, text),
                    enumMembers));
            }
            else if (child is TypeDeclarationSyntax typeDecl)
            {
                var symbol = model.GetDeclaredSymbol(typeDecl, ct);
                if (symbol is null) continue;

                var children = CollectOutlineItems(typeDecl, model, text, ct, depth + 1);
                items.Add(new OutlineItem(
                    symbol.Name,
                    RoslynMapper.GetSymbolKind(symbol),
                    null,
                    symbol.DeclaredAccessibility.ToString(),
                    RoslynMapper.ToCodeRange(typeDecl.Span, text),
                    children));
            }
            else if (child is DelegateDeclarationSyntax delegateDecl)
            {
                var symbol = model.GetDeclaredSymbol(delegateDecl, ct);
                if (symbol is null) continue;

                var returnType = (symbol as INamedTypeSymbol)?.DelegateInvokeMethod?.ReturnType.ToDisplayString();
                items.Add(CreateLeafOutlineItem(symbol, "Delegate", returnType, delegateDecl.Span, text));
            }
            else if (child is MethodDeclarationSyntax methodDecl)
            {
                var symbol = model.GetDeclaredSymbol(methodDecl, ct) as IMethodSymbol;
                if (symbol is null) continue;

                items.Add(CreateLeafOutlineItem(symbol, "Method", symbol.ReturnType.ToDisplayString(), methodDecl.Span, text));
            }
            else if (child is ConstructorDeclarationSyntax ctorDecl)
            {
                var symbol = model.GetDeclaredSymbol(ctorDecl, ct);
                if (symbol is null) continue;

                items.Add(CreateLeafOutlineItem(symbol, "Constructor", null, ctorDecl.Span, text));
            }
            else if (child is PropertyDeclarationSyntax propDecl)
            {
                var symbol = model.GetDeclaredSymbol(propDecl, ct) as IPropertySymbol;
                if (symbol is null) continue;

                items.Add(CreateLeafOutlineItem(symbol, "Property", symbol.Type.ToDisplayString(), propDecl.Span, text));
            }
            else if (child is FieldDeclarationSyntax fieldDecl)
            {
                foreach (var variable in fieldDecl.Declaration.Variables)
                {
                    var symbol = model.GetDeclaredSymbol(variable, ct);
                    if (symbol is not IFieldSymbol fieldSymbol) continue;

                    items.Add(CreateLeafOutlineItem(fieldSymbol, "Field", fieldSymbol.Type.ToDisplayString(), variable.Span, text));
                }
            }
            else if (child is EventDeclarationSyntax eventDecl)
            {
                var symbol = model.GetDeclaredSymbol(eventDecl, ct) as IEventSymbol;
                if (symbol is null) continue;

                items.Add(CreateLeafOutlineItem(symbol, "Event", symbol.Type.ToDisplayString(), eventDecl.Span, text));
            }
            else if (child is EventFieldDeclarationSyntax eventFieldDecl)
            {
                foreach (var variable in eventFieldDecl.Declaration.Variables)
                {
                    var symbol = model.GetDeclaredSymbol(variable, ct);
                    if (symbol is not IEventSymbol eventSymbol) continue;

                    items.Add(CreateLeafOutlineItem(eventSymbol, "Event", eventSymbol.Type.ToDisplayString(), variable.Span, text));
                }
            }
        }

        return items;
    }

    private static OutlineItem CreateLeafOutlineItem(
        ISymbol symbol, string kind, string? detail, TextSpan span, SourceText text)
    {
        return new OutlineItem(
            symbol.Name, kind, detail,
            symbol.DeclaredAccessibility.ToString(),
            RoslynMapper.ToCodeRange(span, text),
            Array.Empty<OutlineItem>());
    }

    #endregion
}

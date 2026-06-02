using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Common;
using RoslynMcp.Shared.Contracts.Refactor;
using System.IO;
using RefactorTextChange = RoslynMcp.Shared.Contracts.Refactor.TextChange;

namespace RoslynMcp.Core.Services;

public partial class RefactoringService
{
    // ── 5. preview_move_type ──

    public async Task<Result<MoveTypePreviewResponse>> PreviewMoveTypeAsync(
        MoveTypeRequest request, CancellationToken ct = default)
    {
        try
        {
            var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
            if (error != null) return Result<MoveTypePreviewResponse>.Fail(error);

            if (symbol is not INamedTypeSymbol typeSymbol)
                return Result<MoveTypePreviewResponse>.Fail("Symbol at position is not a type");

            var declRef = typeSymbol.DeclaringSyntaxReferences.FirstOrDefault();
            if (declRef is null)
                return Result<MoveTypePreviewResponse>.Fail("Could not find type declaration");

            var typeDecl = await declRef.GetSyntaxAsync(ct).ConfigureAwait(false) as TypeDeclarationSyntax;
            if (typeDecl is null)
                return Result<MoveTypePreviewResponse>.Fail("Could not resolve type declaration syntax");

            var sourceRoot = await doc!.GetSyntaxRootAsync(ct).ConfigureAwait(false) as CompilationUnitSyntax;
            if (sourceRoot is null)
                return Result<MoveTypePreviewResponse>.Fail("Could not get compilation unit");

            var sourceText = await doc.GetTextAsync(ct).ConfigureAwait(false);

            var pathResult = ValidateTargetPath(request.TargetFilePath);
            if (!pathResult.IsSuccess)
                return Result<MoveTypePreviewResponse>.Fail(pathResult.Error!.Message, pathResult.Error.ErrorCode);

            // Build preview: source file changes (type removed)
            var typeSpan = typeDecl.FullSpan;
            var sourceRange = RoslynMapper.ToCodeRange(typeSpan, sourceText);
            var sourceFileChange = new FileChange(
                doc.FilePath ?? request.FilePath,
                new[] { new RefactorTextChange(sourceRange, "") });

            // Build preview: target file changes (type added)
            var typeText = typeDecl.ToFullString();
            var targetNamespace = request.TargetNamespace
                ?? typeSymbol.ContainingNamespace?.ToDisplayString();

            if (targetNamespace != null)
            {
                var nsError = ValidateQualifiedName(targetNamespace);
                if (nsError != null)
                    return Result<MoveTypePreviewResponse>.Fail(nsError!);
            }

            var targetDoc = await _workspace.GetDocumentAsync(request.TargetFilePath, ct: ct).ConfigureAwait(false);
            string targetContent;
            if (targetDoc != null)
            {
                // Target exists: merge type into existing file
                var targetRoot = await targetDoc.GetSyntaxRootAsync(ct).ConfigureAwait(false) as CompilationUnitSyntax;
                if (targetRoot != null)
                    targetContent = MergeTypeIntoExistingFile(targetRoot, typeText);
                else
                    targetContent = BuildMoveTypeTargetContent(sourceRoot, typeText, targetNamespace);
            }
            else
            {
                targetContent = BuildMoveTypeTargetContent(sourceRoot, typeText, targetNamespace);
            }

            var targetRange = new CodeRange(0, 0, 0, 0);
            var targetFileChange = new FileChange(
                request.TargetFilePath,
                new[] { new RefactorTextChange(targetRange, targetContent) });

            var preview = new RefactoringPreview(
                new[] { sourceFileChange, targetFileChange },
                TotalChanges: 2);


            return new MoveTypePreviewResponse(
                RoslynMapper.ToSymbolInfo(typeSymbol),
                doc.FilePath ?? request.FilePath,
                request.TargetFilePath,
                preview);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "preview_move_type failed for {FilePath}", request.FilePath);
            return Result<MoveTypePreviewResponse>.Fail("Failed to preview move type: " + ex.Message);
        }
    }

    // ── 6. apply_move_type ──

    public async Task<Result<MoveTypeApplyResponse>> ApplyMoveTypeAsync(
        MoveTypeRequest request, CancellationToken ct = default)
    {
        try
        {
            var previewResult = await PreviewMoveTypeAsync(request, ct).ConfigureAwait(false);
            if (!previewResult.IsSuccess)
                return Result<MoveTypeApplyResponse>.Fail(previewResult.Error!);

            var preview = previewResult.Value!;

            _logger.Warning("apply_move_type computed changes for {Type} but workspace write-back not available in standalone mode",
                preview.Symbol.Name);

            return new MoveTypeApplyResponse(
                preview.Symbol,
                preview.SourceFilePath,
                preview.TargetFilePath,
                FilesChanged: preview.Preview.AffectedFiles.Count,
                Changes: preview.Preview.AffectedFiles);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "apply_move_type failed for {FilePath}", request.FilePath);
            return Result<MoveTypeApplyResponse>.Fail("Failed to apply move type: " + ex.Message);
        }
    }

    // ── MoveType helpers ──

    private static string BuildMoveTypeTargetContent(
        CompilationUnitSyntax sourceRoot, string typeText, string? targetNamespace)
    {
        // Copy using directives from source
        var usings = string.Join(Environment.NewLine,
            sourceRoot.Usings.Select(u => u.ToString()));

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(usings))
        {
            parts.Add(usings);
            parts.Add("");
        }

        if (!string.IsNullOrEmpty(targetNamespace))
        {
            // Match source namespace style (file-scoped vs block-scoped)
            var sourceNsDecl = sourceRoot.DescendantNodes()
                .OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();

            if (sourceNsDecl is NamespaceDeclarationSyntax)
            {
                // Block-scoped namespace
                parts.Add($"namespace {targetNamespace}");
                parts.Add("{");
                parts.Add(typeText.TrimStart());
                parts.Add("}");
            }
            else
            {
                // File-scoped namespace (default for new files)
                parts.Add($"namespace {targetNamespace};");
                parts.Add("");
                parts.Add(typeText.TrimStart());
            }
        }
        else
        {
            parts.Add(typeText.TrimStart());
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static string MergeTypeIntoExistingFile(
        CompilationUnitSyntax existingRoot, string typeText)
    {
        // Parse the type text into a syntax node
        var typeTree = CSharpSyntaxTree.ParseText(typeText);
        var typeRoot = typeTree.GetCompilationUnitRoot();
        var newType = typeRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault();

        // If we can't parse it, fall back to appending the raw text
        if (newType is null)
        {
            newType = SyntaxFactory.ParseMemberDeclaration(typeText) as TypeDeclarationSyntax;
        }

        if (newType is null)
        {
            // Last resort: append to end of file
            return existingRoot.ToFullString().TrimEnd()
                + Environment.NewLine + Environment.NewLine
                + typeText.TrimStart() + Environment.NewLine;
        }

        // Ensure the new type has proper leading trivia (blank line before)
        newType = newType
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

        CompilationUnitSyntax modifiedRoot;

        // Find target namespace to insert into
        var nsDecl = existingRoot.DescendantNodes().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        var fileScopedNs = existingRoot.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();

        if (nsDecl != null)
        {
            // Block-scoped namespace: add member to it
            var newNsDecl = nsDecl.AddMembers(newType);
            modifiedRoot = existingRoot.ReplaceNode(nsDecl, newNsDecl);
        }
        else if (fileScopedNs != null)
        {
            // File-scoped namespace: add member to it
            var newFileScopedNs = fileScopedNs.AddMembers(newType);
            modifiedRoot = existingRoot.ReplaceNode(fileScopedNs, newFileScopedNs);
        }
        else
        {
            // No namespace: add as top-level member
            modifiedRoot = existingRoot.AddMembers(newType);
        }

        // Format the result
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(modifiedRoot, workspace);
        return formatted.ToFullString();
    }
}

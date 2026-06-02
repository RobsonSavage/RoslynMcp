using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Common;
using RoslynMcp.Shared.Contracts.Refactor;
using RefactorTextChange = RoslynMcp.Shared.Contracts.Refactor.TextChange;

namespace RoslynMcp.Core.Services;

public partial class RefactoringService
{
    // ── 1. preview_rename ──

    public async Task<Result<RenamePreviewResponse>> PreviewRenameAsync(
        RenameRequest request, CancellationToken ct = default)
    {
        try
        {
            var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
            if (error != null) return Result<RenamePreviewResponse>.Fail(error);

            var identifierError = ValidateIdentifier(request.NewName);
            if (identifierError != null)
                return Result<RenamePreviewResponse>.Fail(identifierError!);

            var solution = _workspace.CurrentSolution;
            if (solution is null)
                return Result<RenamePreviewResponse>.Fail("No solution loaded");
            var newSolution = await Renamer.RenameSymbolAsync(
                solution, symbol!, new SymbolRenameOptions(), request.NewName, ct).ConfigureAwait(false);

            var preview = await BuildRenamePreviewAsync(solution, newSolution, ct).ConfigureAwait(false);


            return new RenamePreviewResponse(
                RoslynMapper.ToSymbolInfo(symbol!),
                request.NewName,
                preview);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "preview_rename failed for {FilePath}", request.FilePath);
            return Result<RenamePreviewResponse>.Fail("Failed to preview rename: " + ex.Message);
        }
    }

    // ── 2. apply_rename ──

    public async Task<Result<RenameApplyResponse>> ApplyRenameAsync(
        RenameRequest request, CancellationToken ct = default)
    {
        try
        {
            var previewResult = await PreviewRenameAsync(request, ct).ConfigureAwait(false);
            if (!previewResult.IsSuccess)
                return Result<RenameApplyResponse>.Fail(previewResult.Error!);

            var preview = previewResult.Value!;

            _logger.Warning("apply_rename computed {ChangeCount} changes for {Symbol} -> {NewName} but workspace write-back not available in standalone mode",
                preview.Preview.TotalChanges, preview.Symbol.Name, request.NewName);

            return new RenameApplyResponse(
                preview.Symbol,
                request.NewName,
                preview.Preview.AffectedFiles.Count,
                preview.Preview.TotalChanges);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "apply_rename failed for {FilePath}", request.FilePath);
            return Result<RenameApplyResponse>.Fail("Failed to apply rename: " + ex.Message);
        }
    }

    // ── Rename helpers ──

    private static async Task<RefactoringPreview> BuildRenamePreviewAsync(
        Solution oldSolution, Solution newSolution, CancellationToken ct)
    {
        var fileChanges = new List<FileChange>();
        int totalChanges = 0;

        var changes = newSolution.GetChanges(oldSolution);
        foreach (var projectChanges in changes.GetProjectChanges())
        {
            foreach (var docId in projectChanges.GetChangedDocuments())
            {
                ct.ThrowIfCancellationRequested();

                var oldDoc = oldSolution.GetDocument(docId);
                var newDoc = newSolution.GetDocument(docId);
                if (oldDoc is null || newDoc is null) continue;

                var oldText = await oldDoc.GetTextAsync(ct).ConfigureAwait(false);
                var newText = await newDoc.GetTextAsync(ct).ConfigureAwait(false);

                var textChanges = newText.GetTextChanges(oldText);
                if (textChanges.Count == 0) continue;

                var filePath = oldDoc.FilePath ?? "";
                var mappedChanges = new List<RefactorTextChange>();

                foreach (var change in textChanges)
                {
                    var range = RoslynMapper.ToCodeRange(change.Span, oldText);
                    mappedChanges.Add(new RefactorTextChange(range, change.NewText ?? ""));
                }

                fileChanges.Add(new FileChange(filePath, mappedChanges));
                totalChanges += mappedChanges.Count;
            }
        }

        return new RefactoringPreview(fileChanges, totalChanges);
    }
}

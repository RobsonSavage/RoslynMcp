using System.ComponentModel.DataAnnotations;
using RoslynMcp.Shared.Contracts.Common;

namespace RoslynMcp.Shared.Contracts.Refactor;

// ── Common refactoring types ──

public record FileChange(
    string FilePath,
    IReadOnlyList<TextChange> Changes);

public record TextChange(
    CodeRange Range,
    string NewText);

public record RefactoringPreview(
    IReadOnlyList<FileChange> AffectedFiles,
    int TotalChanges);

// ── preview_rename / apply_rename ──

/// <param name="Line">1-based line number in the source file.</param>
/// <param name="Column">1-based column number in the source file.</param>
public record RenameRequest(
    [property: Required, MinLength(1)] string FilePath,
    [property: Range(1, int.MaxValue)] int Line,
    [property: Range(1, int.MaxValue)] int Column,
    [property: Required, StringLength(ValidationLimits.MaxIdentifierLength)] string NewName,
    bool IncludeComments = false,
    bool IncludeStrings = false);

public record RenamePreviewResponse(
    SymbolInfo Symbol,
    string NewName,
    RefactoringPreview Preview);

public record RenameApplyResponse(
    SymbolInfo Symbol,
    string NewName,
    int FilesChanged,
    int TotalReplacements);

// ── organize_usings ──

public record OrganizeUsingsRequest(
    [property: Required, MinLength(1)] string FilePath,
    bool RemoveUnused = true,
    bool Sort = true);

public record OrganizeUsingsResponse(
    string FilePath,
    int UsingsRemoved,
    int UsingsSorted,
    IReadOnlyList<string> RemovedUsings,
    IReadOnlyList<string>? SortedUsings = null);

// ── extract_interface ──

/// <param name="Line">1-based line number in the source file.</param>
/// <param name="Column">1-based column number in the source file.</param>
public record ExtractInterfaceRequest(
    [property: Required, MinLength(1)] string FilePath,
    [property: Range(1, int.MaxValue)] int Line,
    [property: Range(1, int.MaxValue)] int Column,
    [property: Required, StringLength(ValidationLimits.MaxIdentifierLength)] string InterfaceName,
    IReadOnlyList<string>? MemberNames = null,
    string? TargetFilePath = null);

/// <summary>
/// Shared fields for extract-interface preview and apply responses.
/// </summary>
public record ExtractInterfaceResultBase(
    SymbolInfo SourceType,
    string InterfaceName,
    string InterfaceFilePath,
    IReadOnlyList<string> ExtractedMembers);

public record ExtractInterfaceResponse(
    SymbolInfo SourceType,
    string InterfaceName,
    string InterfaceFilePath,
    IReadOnlyList<string> ExtractedMembers)
    : ExtractInterfaceResultBase(SourceType, InterfaceName, InterfaceFilePath, ExtractedMembers);

public record ExtractInterfacePreviewResponse(
    SymbolInfo SourceType,
    string InterfaceName,
    string InterfaceFilePath,
    IReadOnlyList<string> ExtractedMembers,
    RefactoringPreview Preview)
    : ExtractInterfaceResultBase(SourceType, InterfaceName, InterfaceFilePath, ExtractedMembers);

public record ExtractInterfaceApplyResponse(
    SymbolInfo SourceType,
    string InterfaceName,
    string InterfaceFilePath,
    IReadOnlyList<string> ExtractedMembers,
    int FilesChanged,
    IReadOnlyList<FileChange>? Changes = null)
    : ExtractInterfaceResultBase(SourceType, InterfaceName, InterfaceFilePath, ExtractedMembers);

// ── preview_move_type / apply_move_type ──

/// <param name="Line">1-based line number in the source file.</param>
/// <param name="Column">1-based column number in the source file.</param>
public record MoveTypeRequest(
    [property: Required, MinLength(1)] string FilePath,
    [property: Range(1, int.MaxValue)] int Line,
    [property: Range(1, int.MaxValue)] int Column,
    [property: Required, MinLength(1)] string TargetFilePath,
    string? TargetNamespace = null);

/// <summary>
/// Shared fields for move-type preview and apply responses.
/// </summary>
public record MoveTypeResultBase(
    SymbolInfo Symbol,
    string SourceFilePath,
    string TargetFilePath);

public record MoveTypePreviewResponse(
    SymbolInfo Symbol,
    string SourceFilePath,
    string TargetFilePath,
    RefactoringPreview Preview)
    : MoveTypeResultBase(Symbol, SourceFilePath, TargetFilePath);

/// <remarks>Standalone mode: changes are computed but not applied. Caller must apply returned Changes to persist modifications.</remarks>
public record MoveTypeApplyResponse(
    SymbolInfo Symbol,
    string SourceFilePath,
    string TargetFilePath,
    int FilesChanged,
    IReadOnlyList<FileChange>? Changes = null)
    : MoveTypeResultBase(Symbol, SourceFilePath, TargetFilePath);

// ── preview_extract_method / apply_extract_method ──

/// <param name="StartLine">1-based start line number.</param>
/// <param name="StartColumn">1-based start column number.</param>
/// <param name="EndLine">1-based end line number (inclusive).</param>
/// <param name="EndColumn">1-based end column number (exclusive).</param>
public record ExtractMethodRequest(
    [property: Required, MinLength(1)] string FilePath,
    [property: Range(1, int.MaxValue)] int StartLine,
    [property: Range(1, int.MaxValue)] int StartColumn,
    [property: Range(1, int.MaxValue)] int EndLine,
    [property: Range(1, int.MaxValue)] int EndColumn,
    [property: Required, StringLength(ValidationLimits.MaxIdentifierLength)] string MethodName,
    string? Accessibility = null) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartLine > EndLine || (StartLine == EndLine && StartColumn >= EndColumn))
            yield return new ValidationResult(
                "Start position must be before end position",
                new[] { nameof(StartLine), nameof(EndLine), nameof(StartColumn), nameof(EndColumn) });
    }
}

public record ExtractMethodPreviewResponse(
    string MethodName,
    RefactoringPreview Preview);

/// <remarks>Standalone mode: changes are computed but not applied. Caller must apply returned Changes to persist modifications.</remarks>
public record ExtractMethodApplyResponse(
    string MethodName,
    CodeLocation NewMethodLocation,
    int FilesChanged,
    IReadOnlyList<FileChange>? Changes = null);

// ── preview_split_class / apply_split_class ──

/// <param name="Line">1-based line number in the source file.</param>
/// <param name="Column">1-based column number in the source file.</param>
public record SplitClassRequest(
    [property: Required, MinLength(1)] string FilePath,
    [property: Range(1, int.MaxValue)] int Line,
    [property: Range(1, int.MaxValue)] int Column,
    [property: Required, StringLength(ValidationLimits.MaxIdentifierLength)] string NewClassName,
    [property: Required, MinLength(1)] IReadOnlyList<string> MemberNames,
    string? TargetFilePath = null);

/// <summary>
/// Shared fields for split-class preview and apply responses.
/// </summary>
public record SplitClassResultBase(
    SymbolInfo SourceType,
    string NewClassName);

public record SplitClassPreviewResponse(
    SymbolInfo SourceType,
    string NewClassName,
    RefactoringPreview Preview)
    : SplitClassResultBase(SourceType, NewClassName);

/// <remarks>Standalone mode: changes are computed but not applied. Caller must apply returned Changes to persist modifications.</remarks>
public record SplitClassApplyResponse(
    SymbolInfo SourceType,
    string NewClassName,
    string NewClassFilePath,
    int MembersMoved,
    int FilesChanged,
    IReadOnlyList<FileChange>? Changes = null)
    : SplitClassResultBase(SourceType, NewClassName);

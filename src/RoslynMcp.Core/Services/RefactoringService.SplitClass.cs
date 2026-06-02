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
    // ── SplitClass helper types ──

    private record MemberToMove(ISymbol Symbol, MemberDeclarationSyntax Syntax);

    // ── 9. preview_split_class ──

    public async Task<Result<SplitClassPreviewResponse>> PreviewSplitClassAsync(
        SplitClassRequest request, CancellationToken ct = default)
    {
        try
        {
            // 1. Resolve source type
            var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
            if (error != null || doc == null)
                return Result<SplitClassPreviewResponse>.Fail(error ?? "Document not resolved");

            if (symbol is not INamedTypeSymbol typeSymbol)
                return Result<SplitClassPreviewResponse>.Fail("Symbol at position is not a type");

            var declRef = typeSymbol.DeclaringSyntaxReferences.FirstOrDefault();
            if (declRef is null)
                return Result<SplitClassPreviewResponse>.Fail("Could not find type declaration");

            var typeDecl = await declRef.GetSyntaxAsync(ct).ConfigureAwait(false) as TypeDeclarationSyntax;
            if (typeDecl is null)
                return Result<SplitClassPreviewResponse>.Fail("Could not resolve type declaration syntax");

            var sourceRoot = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false) as CompilationUnitSyntax;
            if (sourceRoot is null)
                return Result<SplitClassPreviewResponse>.Fail("Could not get compilation unit");

            var sourceText = await doc.GetTextAsync(ct).ConfigureAwait(false);

            var identifierError = ValidateIdentifier(request.NewClassName);
            if (identifierError != null)
                return Result<SplitClassPreviewResponse>.Fail(identifierError!);

            // 2. Validate and collect members to move
            var membersResult = SplitClassCollectMembers(
                typeSymbol, request.MemberNames, typeDecl, doc.FilePath ?? request.FilePath, ct);
            if (!membersResult.IsSuccess)
                return Result<SplitClassPreviewResponse>.Fail(membersResult.Error!);

            var membersToMove = membersResult.Value!;
            ct.ThrowIfCancellationRequested();

            // 3. Modify source type: add "partial" + remove moved members
            var membersToRemoveSet = new HashSet<MemberDeclarationSyntax>(
                membersToMove.Select(m => m.Syntax));
            var remainingMembers = SyntaxFactory.List(
                typeDecl.Members.Where(m => !membersToRemoveSet.Contains(m)));
            var modifiedSourceType = SplitClassSetMembers(typeDecl, remainingMembers);
            modifiedSourceType = SplitClassEnsurePartial(modifiedSourceType);

            // Source file change: replace original type declaration with modified one
            var sourceChange = new RefactorTextChange(
                RoslynMapper.ToCodeRange(typeDecl.FullSpan, sourceText),
                modifiedSourceType.ToFullString());
            var sourceFileChange = new FileChange(
                doc.FilePath ?? request.FilePath,
                new[] { sourceChange });

            // 4. Build new partial class file content
            // Uses typeSymbol.Name (not request.NewClassName) for the class declaration name.
            // Preserves type kind (class/struct/record), all modifiers, type parameters, and constraints.
            // Base list and attributes stay on the source partial only.
            var targetContent = BuildSplitClassTargetContent(
                typeSymbol, typeDecl,
                membersToMove.Select(m => m.Syntax).ToList(),
                sourceRoot);

            // 5. Determine target file path
            var targetFilePath = request.TargetFilePath
                ?? SplitClassGenerateTargetPath(doc.FilePath ?? request.FilePath, request.NewClassName);

            var pathResult = ValidateTargetPath(targetFilePath);
            if (!pathResult.IsSuccess)
                return Result<SplitClassPreviewResponse>.Fail(
                    pathResult.Error!.Message, pathResult.Error.ErrorCode);

            // 6. Handle target file merge (if target already exists)
            var targetDoc = await _workspace.GetDocumentAsync(targetFilePath, ct: ct).ConfigureAwait(false);
            if (targetDoc != null)
            {
                var targetRoot = await targetDoc.GetSyntaxRootAsync(ct).ConfigureAwait(false) as CompilationUnitSyntax;
                if (targetRoot != null)
                {
                    // Validate namespace compatibility
                    var sourceNs = typeSymbol.ContainingNamespace?.IsGlobalNamespace == true
                        ? null : typeSymbol.ContainingNamespace?.ToDisplayString();
                    var targetNsDecl = targetRoot.DescendantNodes()
                        .OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
                    var targetNs = targetNsDecl?.Name.ToString();

                    if (!string.Equals(sourceNs, targetNs, StringComparison.Ordinal))
                        return Result<SplitClassPreviewResponse>.Fail(
                            $"Target file namespace '{targetNs}' differs from source namespace '{sourceNs}'");

                    // Extract just the type declaration for merging into existing file
                    var newTree = CSharpSyntaxTree.ParseText(targetContent);
                    var newRoot = await newTree.GetRootAsync(ct).ConfigureAwait(false) as CompilationUnitSyntax;
                    var newType = newRoot?.DescendantNodes()
                        .OfType<TypeDeclarationSyntax>().FirstOrDefault();
                    if (newType != null)
                        targetContent = MergeTypeIntoExistingFile(
                            targetRoot, newType.ToFullString());
                }
            }

            // Target file change
            var targetRange = new CodeRange(0, 0, 0, 0);
            var targetFileChange = new FileChange(
                targetFilePath,
                new[] { new RefactorTextChange(targetRange, targetContent) });

            var preview = new RefactoringPreview(
                new[] { sourceFileChange, targetFileChange },
                TotalChanges: 2);


            return new SplitClassPreviewResponse(
                RoslynMapper.ToSymbolInfo(typeSymbol),
                request.NewClassName,
                preview);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "preview_split_class failed for {FilePath}", request.FilePath);
            return Result<SplitClassPreviewResponse>.Fail(
                $"Failed to preview split class: {ex.Message}");
        }
    }

    // ── 10. apply_split_class ──

    public async Task<Result<SplitClassApplyResponse>> ApplySplitClassAsync(
        SplitClassRequest request, CancellationToken ct = default)
    {
        try
        {
            var previewResult = await PreviewSplitClassAsync(request, ct).ConfigureAwait(false);
            if (!previewResult.IsSuccess)
                return Result<SplitClassApplyResponse>.Fail(previewResult.Error!);

            var preview = previewResult.Value!;
            var targetFilePath = preview.Preview.AffectedFiles[1].FilePath;

            _logger.Warning(
                "apply_split_class computed changes for {Type} but workspace write-back not available in standalone mode",
                preview.SourceType.Name);

            return new SplitClassApplyResponse(
                preview.SourceType,
                request.NewClassName,
                targetFilePath,
                MembersMoved: request.MemberNames.Count,
                FilesChanged: 2,
                Changes: preview.Preview.AffectedFiles);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "apply_split_class failed for {FilePath}", request.FilePath);
            return Result<SplitClassApplyResponse>.Fail(
                $"Failed to apply split class: {ex.Message}");
        }
    }

    // ── SplitClass helpers ──

    private static Result<IReadOnlyList<MemberToMove>> SplitClassCollectMembers(
        INamedTypeSymbol sourceType,
        IReadOnlyList<string> memberNames,
        TypeDeclarationSyntax typeDecl,
        string sourceFilePath,
        CancellationToken ct)
    {
        var result = new List<MemberToMove>();
        var seen = new HashSet<MemberDeclarationSyntax>(SyntaxNodeComparer.Instance);
        var allMembers = sourceType.GetMembers();

        foreach (var memberName in memberNames)
        {
            ct.ThrowIfCancellationRequested();

            var matches = allMembers
                .Where(m => m.Name == memberName && !m.IsImplicitlyDeclared)
                .ToList();

            if (matches.Count == 0)
                return Result<IReadOnlyList<MemberToMove>>.Fail(
                    $"Member '{memberName}' not found in type '{sourceType.Name}'");

            foreach (var member in matches)
            {
                if (member is IMethodSymbol { MethodKind: MethodKind.Constructor })
                    return Result<IReadOnlyList<MemberToMove>>.Fail(
                        "Constructors cannot be moved to partial classes");

                if (member is IMethodSymbol { MethodKind: MethodKind.Destructor })
                    return Result<IReadOnlyList<MemberToMove>>.Fail(
                        "Destructors cannot be moved to partial classes");

                if (member is INamedTypeSymbol)
                    return Result<IReadOnlyList<MemberToMove>>.Fail(
                        "Nested type movement not yet supported");

                // Null guard: synthesized or metadata-only members have no syntax
                var declRef = member.DeclaringSyntaxReferences.FirstOrDefault();
                if (declRef == null)
                    return Result<IReadOnlyList<MemberToMove>>.Fail(
                        $"Member '{member.Name}' has no syntax declaration (likely synthesized or from metadata)");

                // Validate same file (multi-file partial split not supported)
                if (!string.Equals(declRef.SyntaxTree.FilePath, sourceFilePath,
                    StringComparison.OrdinalIgnoreCase))
                    return Result<IReadOnlyList<MemberToMove>>.Fail(
                        $"Member '{member.Name}' is declared in a different file; multi-file partial split not supported");

                // Fields/events: DeclaringSyntaxReferences points to VariableDeclaratorSyntax,
                // not FieldDeclarationSyntax. Walk up to the enclosing MemberDeclarationSyntax.
                var syntaxNode = declRef.GetSyntax(ct);
                var syntax = syntaxNode as MemberDeclarationSyntax
                    ?? syntaxNode.FirstAncestorOrSelf<MemberDeclarationSyntax>();
                if (syntax == null)
                    return Result<IReadOnlyList<MemberToMove>>.Fail(
                        $"Could not resolve syntax for member '{member.Name}'");

                if (syntax is FieldDeclarationSyntax fieldDecl && fieldDecl.Declaration.Variables.Count > 1)
                    return Result<IReadOnlyList<MemberToMove>>.Fail(
                        $"Field '{member.Name}' is part of a multi-variable declaration; split the declaration first");

                if (syntax is EventFieldDeclarationSyntax eventFieldDecl && eventFieldDecl.Declaration.Variables.Count > 1)
                    return Result<IReadOnlyList<MemberToMove>>.Fail(
                        $"Event '{member.Name}' is part of a multi-variable declaration; split the declaration first");

                // Avoid duplicates (overloads or accessor pairs may resolve to same syntax)
                if (seen.Add(syntax))
                    result.Add(new MemberToMove(member, syntax));
            }
        }

        if (result.Count == 0)
            return Result<IReadOnlyList<MemberToMove>>.Fail("No valid members to move");

        return result;
    }

    private static string SplitClassGenerateTargetPath(string sourceFilePath, string partitionName)
    {
        var dir = Path.GetDirectoryName(sourceFilePath) ?? "";
        var nameWithoutExt = Path.GetFileNameWithoutExtension(sourceFilePath);
        return Path.Combine(dir, $"{nameWithoutExt}.{partitionName}.cs");
    }

    private string BuildSplitClassTargetContent(
        INamedTypeSymbol typeSymbol,
        TypeDeclarationSyntax sourceTypeDecl,
        IReadOnlyList<MemberDeclarationSyntax> members,
        CompilationUnitSyntax sourceRoot)
    {
        // Clone source type with only moved members.
        // Preserves: type kind, name, modifiers, type parameters, constraints.
        // Strips: base list, attributes (stay on source partial).
        var newType = SplitClassBuildNewPartialType(sourceTypeDecl, SyntaxFactory.List(members));

        // Build compilation unit with usings + namespace + type
        var compilationUnit = SyntaxFactory.CompilationUnit()
            .WithUsings(sourceRoot.Usings);

        var ns = typeSymbol.ContainingNamespace?.IsGlobalNamespace == true
            ? null
            : typeSymbol.ContainingNamespace?.ToDisplayString();

        if (ns != null)
        {
            // Match source namespace style (file-scoped vs block-scoped)
            var sourceNsDecl = sourceRoot.DescendantNodes()
                .OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();

            MemberDeclarationSyntax nsDecl;
            if (sourceNsDecl is FileScopedNamespaceDeclarationSyntax)
            {
                nsDecl = SyntaxFactory.FileScopedNamespaceDeclaration(
                        SyntaxFactory.ParseName(ns))
                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(newType));
            }
            else
            {
                nsDecl = SyntaxFactory.NamespaceDeclaration(
                        SyntaxFactory.ParseName(ns))
                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(newType));
            }

            compilationUnit = compilationUnit.WithMembers(
                SyntaxFactory.SingletonList(nsDecl));
        }
        else
        {
            compilationUnit = compilationUnit.WithMembers(
                SyntaxFactory.SingletonList<MemberDeclarationSyntax>(newType));
        }

        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(compilationUnit, workspace);
        return formatted.ToFullString();
    }

    /// <summary>
    /// Creates a new partial type declaration by cloning the source type.
    /// Preserves: type kind (class/struct/record/interface), name, modifiers
    /// (accessibility, static, sealed, abstract, unsafe), type parameters, constraints.
    /// Strips: base list, attributes, record parameter list.
    /// </summary>
    private static TypeDeclarationSyntax SplitClassBuildNewPartialType(
        TypeDeclarationSyntax sourceTypeDecl,
        SyntaxList<MemberDeclarationSyntax> members)
    {
        var newType = SplitClassSetMembers(sourceTypeDecl, members);
        newType = SplitClassStripInheritedProperties(newType);
        newType = SplitClassEnsurePartial(newType);
        return (TypeDeclarationSyntax)newType
            .WithLeadingTrivia()
            .WithTrailingTrivia();
    }

    /// <summary>
    /// Applies a transformation function to a TypeDeclarationSyntax, dispatching to the
    /// correct concrete type (class/struct/record/interface) so the result retains its kind.
    /// </summary>
    private static TypeDeclarationSyntax SplitClassApplyTransform(
        TypeDeclarationSyntax typeDecl,
        Func<ClassDeclarationSyntax, ClassDeclarationSyntax> classTransform,
        Func<StructDeclarationSyntax, StructDeclarationSyntax> structTransform,
        Func<RecordDeclarationSyntax, RecordDeclarationSyntax> recordTransform,
        Func<InterfaceDeclarationSyntax, InterfaceDeclarationSyntax> interfaceTransform)
    {
        return typeDecl switch
        {
            ClassDeclarationSyntax cls => classTransform(cls),
            StructDeclarationSyntax str => structTransform(str),
            RecordDeclarationSyntax rec => recordTransform(rec),
            InterfaceDeclarationSyntax iface => interfaceTransform(iface),
            _ => throw new InvalidOperationException(
                $"Unsupported type kind: {typeDecl.Kind()}")
        };
    }

    private static TypeDeclarationSyntax SplitClassSetMembers(
        TypeDeclarationSyntax typeDecl,
        SyntaxList<MemberDeclarationSyntax> members)
    {
        return SplitClassApplyTransform(typeDecl,
            cls => cls.WithMembers(members),
            str => str.WithMembers(members),
            rec => rec.WithMembers(members),
            iface => iface.WithMembers(members));
    }

    /// <summary>Strips base list, attributes, and record parameter list from the type.</summary>
    private static TypeDeclarationSyntax SplitClassStripInheritedProperties(
        TypeDeclarationSyntax typeDecl)
    {
        return SplitClassApplyTransform(typeDecl,
            cls => cls
                .WithBaseList(null)
                .WithAttributeLists(SyntaxFactory.List<AttributeListSyntax>()),
            str => str
                .WithBaseList(null)
                .WithAttributeLists(SyntaxFactory.List<AttributeListSyntax>()),
            rec => rec
                .WithBaseList(null)
                .WithAttributeLists(SyntaxFactory.List<AttributeListSyntax>())
                .WithParameterList(null),
            iface => iface
                .WithBaseList(null)
                .WithAttributeLists(SyntaxFactory.List<AttributeListSyntax>()));
    }

    private static TypeDeclarationSyntax SplitClassEnsurePartial(
        TypeDeclarationSyntax typeDecl)
    {
        if (typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
            return typeDecl;

        var newModifiers = typeDecl.Modifiers.Add(
            SyntaxFactory.Token(SyntaxKind.PartialKeyword)
                .WithTrailingTrivia(SyntaxFactory.Space));

        return SplitClassApplyTransform(typeDecl,
            cls => cls.WithModifiers(newModifiers),
            str => str.WithModifiers(newModifiers),
            rec => rec.WithModifiers(newModifiers),
            iface => iface.WithModifiers(newModifiers));
    }

    /// <summary>Reference-equality comparer for syntax nodes (netstandard2.0 compatible).</summary>
    private sealed class SyntaxNodeComparer : IEqualityComparer<MemberDeclarationSyntax>
    {
        public static readonly SyntaxNodeComparer Instance = new();
        public bool Equals(MemberDeclarationSyntax? x, MemberDeclarationSyntax? y) => ReferenceEquals(x, y);
        public int GetHashCode(MemberDeclarationSyntax obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

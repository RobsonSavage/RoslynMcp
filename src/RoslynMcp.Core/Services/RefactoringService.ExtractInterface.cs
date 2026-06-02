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
    // ── 4. preview_extract_interface ──

    public async Task<Result<ExtractInterfacePreviewResponse>> PreviewExtractInterfaceAsync(
        ExtractInterfaceRequest request, CancellationToken ct = default)
    {
        try
        {
            // 1. Resolve and validate
            var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
            if (error != null) return Result<ExtractInterfacePreviewResponse>.Fail(error);

            if (symbol is not INamedTypeSymbol typeSymbol)
                return Result<ExtractInterfacePreviewResponse>.Fail("Symbol at position is not a type");

            if (typeSymbol.TypeKind != TypeKind.Class && typeSymbol.TypeKind != TypeKind.Struct)
                return Result<ExtractInterfacePreviewResponse>.Fail("Symbol must be a class or struct to extract an interface");

            // 2. Collect members
            var publicMembers = typeSymbol.GetMembers()
                .Where(m => m.DeclaredAccessibility == Accessibility.Public
                            && !m.IsStatic
                            && m is IMethodSymbol or IPropertySymbol or IEventSymbol
                            && m.Name != ".ctor")
                .ToList();

            IReadOnlyList<ISymbol> selectedMembers;
            if (request.MemberNames != null && request.MemberNames.Count > 0)
            {
                var memberSet = new HashSet<string>(request.MemberNames, StringComparer.Ordinal);
                selectedMembers = publicMembers.Where(m => memberSet.Contains(m.Name)).ToList();
                if (selectedMembers.Count == 0)
                    return Result<ExtractInterfacePreviewResponse>.Fail("None of the specified members were found as public instance members");
            }
            else
            {
                selectedMembers = publicMembers;
            }

            if (selectedMembers.Count == 0)
                return Result<ExtractInterfacePreviewResponse>.Fail("No public instance members found to extract");

            // 3. Validate interface name
            var identifierError = ValidateIdentifier(request.InterfaceName);
            if (identifierError != null)
                return Result<ExtractInterfacePreviewResponse>.Fail(identifierError!);

            // 4. Build interface member declarations as syntax nodes (not strings)
            var interfaceMembers = new List<MemberDeclarationSyntax>();
            foreach (var member in selectedMembers)
            {
                ct.ThrowIfCancellationRequested();
                var memberDecl = BuildInterfaceMemberDeclaration(member);
                if (memberDecl != null)
                    interfaceMembers.Add(memberDecl);
            }

            // 5. Determine target file path
            var targetFilePath = request.TargetFilePath;
            if (string.IsNullOrEmpty(targetFilePath) && doc?.FilePath != null)
            {
                var dir = Path.GetDirectoryName(doc.FilePath) ?? "";
                targetFilePath = Path.Combine(dir, request.InterfaceName + ".cs");
            }
            if (string.IsNullOrEmpty(targetFilePath))
                return Result<ExtractInterfacePreviewResponse>.Fail("Could not determine target file path");

            var pathResult = ValidateTargetPath(targetFilePath!);
            if (!pathResult.IsSuccess)
                return Result<ExtractInterfacePreviewResponse>.Fail(pathResult.Error!.Message, pathResult.Error.ErrorCode);
            targetFilePath = pathResult.Value;

            // 6. Build complete interface compilation unit
            var interfaceDecl = SyntaxFactory.InterfaceDeclaration(request.InterfaceName)
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithMembers(SyntaxFactory.List(interfaceMembers));

            // Copy generic type parameters if the source type is generic
            if (typeSymbol.IsGenericType)
            {
                var typeParams = typeSymbol.TypeParameters.Select(tp =>
                    SyntaxFactory.TypeParameter(tp.Name)).ToArray();
                interfaceDecl = interfaceDecl.WithTypeParameterList(
                    SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(typeParams)));
            }

            var sourceRoot = await doc!.GetSyntaxRootAsync(ct).ConfigureAwait(false) as CompilationUnitSyntax;
            if (sourceRoot is null)
                return Result<ExtractInterfacePreviewResponse>.Fail("Could not get compilation unit");

            // Build compilation unit with usings + namespace
            var compilationUnit = SyntaxFactory.CompilationUnit()
                .WithUsings(sourceRoot.Usings);

            var ns = typeSymbol.ContainingNamespace?.IsGlobalNamespace == true
                ? null : typeSymbol.ContainingNamespace?.ToDisplayString();

            if (ns != null)
            {
                var sourceNsDecl = sourceRoot.DescendantNodes()
                    .OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();

                MemberDeclarationSyntax nsDecl;
                if (sourceNsDecl is FileScopedNamespaceDeclarationSyntax)
                {
                    nsDecl = SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName(ns))
                        .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(interfaceDecl));
                }
                else
                {
                    nsDecl = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(ns))
                        .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(interfaceDecl));
                }
                compilationUnit = compilationUnit.WithMembers(SyntaxFactory.SingletonList(nsDecl));
            }
            else
            {
                compilationUnit = compilationUnit.WithMembers(
                    SyntaxFactory.SingletonList<MemberDeclarationSyntax>(interfaceDecl));
            }

            // Format with single workspace
            string interfaceContent;
            using (var workspace = new AdhocWorkspace())
            {
                var formatted = Formatter.Format(compilationUnit, workspace);
                interfaceContent = formatted.ToFullString();
            }

            // 7. Build file changes
            // Interface file: new file with full content
            var targetRange = new CodeRange(0, 0, 0, 0);
            var interfaceFileChange = new FileChange(
                targetFilePath!,
                new[] { new RefactorTextChange(targetRange, interfaceContent) });

            // Source file: add interface to base list
            var sourceText = await doc.GetTextAsync(ct).ConfigureAwait(false);
            var declRef = typeSymbol.DeclaringSyntaxReferences.FirstOrDefault();
            if (declRef is null)
                return Result<ExtractInterfacePreviewResponse>.Fail(
                    "Cannot modify source type: no declaring syntax reference found (type may be metadata-only)");

            var typeDecl = await declRef.GetSyntaxAsync(ct).ConfigureAwait(false) as TypeDeclarationSyntax;

            var fileChanges = new List<FileChange> { interfaceFileChange };

            if (typeDecl != null)
            {
                var interfaceBaseType = SyntaxFactory.SimpleBaseType(
                    SyntaxFactory.IdentifierName(request.InterfaceName));

                TypeDeclarationSyntax modifiedTypeDecl;
                if (typeDecl.BaseList != null)
                {
                    // Add interface after existing base types
                    modifiedTypeDecl = typeDecl.WithBaseList(
                        typeDecl.BaseList.AddTypes(interfaceBaseType));
                }
                else
                {
                    // Create new base list
                    modifiedTypeDecl = typeDecl.WithBaseList(
                        SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(interfaceBaseType)));
                }

                var sourceRange = RoslynMapper.ToCodeRange(typeDecl.FullSpan, sourceText);
                var sourceFileChange = new FileChange(
                    doc.FilePath ?? request.FilePath,
                    new[] { new RefactorTextChange(sourceRange, modifiedTypeDecl.ToFullString()) });
                fileChanges.Add(sourceFileChange);
            }

            var preview = new RefactoringPreview(fileChanges, fileChanges.Count);
            var extractedMemberNames = selectedMembers.Select(m => m.Name).ToList();


            return new ExtractInterfacePreviewResponse(
                RoslynMapper.ToSymbolInfo(typeSymbol),
                request.InterfaceName,
                targetFilePath!,
                extractedMemberNames,
                preview);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "preview_extract_interface failed for {FilePath}", request.FilePath);
            return Result<ExtractInterfacePreviewResponse>.Fail("Failed to extract interface: " + ex.Message);
        }
    }

    // ── 4b. apply_extract_interface ──

    public async Task<Result<ExtractInterfaceApplyResponse>> ApplyExtractInterfaceAsync(
        ExtractInterfaceRequest request, CancellationToken ct = default)
    {
        try
        {
            var previewResult = await PreviewExtractInterfaceAsync(request, ct).ConfigureAwait(false);
            if (!previewResult.IsSuccess)
                return Result<ExtractInterfaceApplyResponse>.Fail(previewResult.Error!);

            var preview = previewResult.Value!;

            _logger.Warning(
                "apply_extract_interface computed changes for {Type} but workspace write-back not available in standalone mode",
                preview.SourceType.Name);

            return new ExtractInterfaceApplyResponse(
                preview.SourceType,
                preview.InterfaceName,
                preview.InterfaceFilePath,
                preview.ExtractedMembers,
                FilesChanged: preview.Preview.AffectedFiles.Count,
                Changes: preview.Preview.AffectedFiles);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "apply_extract_interface failed for {FilePath}", request.FilePath);
            return Result<ExtractInterfaceApplyResponse>.Fail(
                "Failed to apply extract interface: " + ex.Message);
        }
    }

    // ── ExtractInterface helpers ──

    private static MemberDeclarationSyntax? BuildInterfaceMemberDeclaration(ISymbol member)
    {
        switch (member)
        {
            case IMethodSymbol method:
                if (method.MethodKind != MethodKind.Ordinary)
                    return null;

                var returnType = SyntaxFactory.ParseTypeName(method.ReturnType.ToDisplayString());
                var methodParams = method.Parameters.Select(p =>
                {
                    var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Name))
                        .WithType(SyntaxFactory.ParseTypeName(p.Type.ToDisplayString()).WithTrailingTrivia(SyntaxFactory.Space));
                    if (p.RefKind == RefKind.Ref)
                        param = param.AddModifiers(SyntaxFactory.Token(SyntaxKind.RefKeyword));
                    else if (p.RefKind == RefKind.Out)
                        param = param.AddModifiers(SyntaxFactory.Token(SyntaxKind.OutKeyword));
                    else if (p.RefKind == RefKind.In)
                        param = param.AddModifiers(SyntaxFactory.Token(SyntaxKind.InKeyword));
                    if (p.IsParams)
                        param = param.AddModifiers(SyntaxFactory.Token(SyntaxKind.ParamsKeyword));
                    if (p.HasExplicitDefaultValue)
                        param = param.WithDefault(SyntaxFactory.EqualsValueClause(
                            BuildDefaultValueExpression(p.ExplicitDefaultValue, p.Type)));
                    return param;
                }).ToArray();

                var methodDecl = SyntaxFactory.MethodDeclaration(returnType, method.Name)
                    .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(methodParams)))
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

                if (method.IsGenericMethod)
                {
                    var typeParams = method.TypeParameters.Select(tp =>
                        SyntaxFactory.TypeParameter(tp.Name)).ToArray();
                    methodDecl = methodDecl.WithTypeParameterList(
                        SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(typeParams)));

                    var constraints = new List<TypeParameterConstraintClauseSyntax>();
                    foreach (var tp in method.TypeParameters)
                    {
                        var constraintList = new List<TypeParameterConstraintSyntax>();
                        if (tp.HasReferenceTypeConstraint)
                            constraintList.Add(SyntaxFactory.ClassOrStructConstraint(SyntaxKind.ClassConstraint));
                        if (tp.HasValueTypeConstraint)
                            constraintList.Add(SyntaxFactory.ClassOrStructConstraint(SyntaxKind.StructConstraint));
                        foreach (var ct in tp.ConstraintTypes)
                            constraintList.Add(SyntaxFactory.TypeConstraint(SyntaxFactory.ParseTypeName(ct.ToDisplayString())));
                        if (tp.HasConstructorConstraint)
                            constraintList.Add(SyntaxFactory.ConstructorConstraint());

                        if (constraintList.Count > 0)
                        {
                            constraints.Add(SyntaxFactory.TypeParameterConstraintClause(tp.Name)
                                .WithConstraints(SyntaxFactory.SeparatedList(constraintList)));
                        }
                    }
                    if (constraints.Count > 0)
                        methodDecl = methodDecl.WithConstraintClauses(SyntaxFactory.List(constraints));
                }

                return methodDecl.NormalizeWhitespace();

            case IPropertySymbol property:
                var accessorList = new List<AccessorDeclarationSyntax>();
                if (property.GetMethod != null && property.GetMethod.DeclaredAccessibility == Accessibility.Public)
                    accessorList.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
                if (property.SetMethod != null && property.SetMethod.DeclaredAccessibility == Accessibility.Public)
                {
                    var setterKind = property.SetMethod.IsInitOnly
                        ? SyntaxKind.InitAccessorDeclaration
                        : SyntaxKind.SetAccessorDeclaration;
                    accessorList.Add(SyntaxFactory.AccessorDeclaration(setterKind)
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
                }

                return SyntaxFactory.PropertyDeclaration(
                        SyntaxFactory.ParseTypeName(property.Type.ToDisplayString()),
                        property.Name)
                    .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessorList)))
                    .WithSemicolonToken(default)
                    .NormalizeWhitespace();

            case IEventSymbol eventSymbol:
                return SyntaxFactory.EventFieldDeclaration(
                    SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.ParseTypeName(eventSymbol.Type.ToDisplayString()))
                    .AddVariables(SyntaxFactory.VariableDeclarator(eventSymbol.Name)))
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                    .NormalizeWhitespace();

            default:
                return null;
        }
    }

    private static ExpressionSyntax BuildDefaultValueExpression(object? value, ITypeSymbol type)
    {
        if (value is null)
            return SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        if (value is bool b)
            return SyntaxFactory.LiteralExpression(b ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression);
        if (value is string s)
            return SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(s));
        if (value is char c)
            return SyntaxFactory.LiteralExpression(SyntaxKind.CharacterLiteralExpression, SyntaxFactory.Literal(c));
        if (value is int i)
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(i));
        if (value is long l)
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(l));
        if (value is float f)
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(f));
        if (value is double d)
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(d));
        if (value is decimal m)
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(m));
        if (value is byte by)
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(by));
        if (value is sbyte sb)
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(sb));
        if (value is short sh)
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(sh));
        if (value is ushort us)
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(us));
        if (value is uint ui)
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(ui));
        if (value is ulong ul)
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(ul));
        if (type.TypeKind == TypeKind.Enum)
        {
            // Emit (EnumType)intValue for enum defaults
            return SyntaxFactory.CastExpression(
                SyntaxFactory.ParseTypeName(type.ToDisplayString()),
                SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(Convert.ToInt32(value))));
        }
        // Fallback: default expression
        return SyntaxFactory.DefaultExpression(SyntaxFactory.ParseTypeName(type.ToDisplayString()));
    }
}

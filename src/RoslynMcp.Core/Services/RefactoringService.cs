using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using Serilog;
using System.IO;

namespace RoslynMcp.Core.Services;

public partial class RefactoringService
{
    private readonly IWorkspaceProvider _workspace;
    private readonly IWorkspaceHelpers _helpers;
    private readonly ILogger _logger;

    public RefactoringService(IWorkspaceProvider workspace, IWorkspaceHelpers helpers, ILogger logger)
    {
        _workspace = workspace;
        _helpers = helpers;
        _logger = logger;
    }

    /// <summary>Returns null if valid; ErrorResponse with code if invalid.</summary>
    private static ErrorResponse? ValidateIdentifier(string name)
    {
        if (!SyntaxFacts.IsValidIdentifier(name))
            return new ErrorResponse($"'{name}' is not a valid C# identifier", "INVALID_IDENTIFIER");
        if (SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None)
            return new ErrorResponse($"'{name}' is a C# keyword", "RESERVED_KEYWORD");
        return null;
    }

    /// <summary>Returns null if valid; ErrorResponse with code if invalid.</summary>
    private static ErrorResponse? ValidateQualifiedName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new ErrorResponse("Namespace cannot be empty", "INVALID_IDENTIFIER");
        foreach (var segment in name.Split('.'))
        {
            if (string.IsNullOrWhiteSpace(segment))
                return new ErrorResponse($"'{name}' contains an empty namespace segment", "INVALID_IDENTIFIER");
            var error = ValidateIdentifier(segment);
            if (error != null)
                return new ErrorResponse($"Namespace segment: {error.Message}", error.ErrorCode);
        }
        return null;
    }

    private Result<string> ValidateTargetPath(string targetFilePath)
    {
        var solutionPath = _workspace.CurrentSolution?.FilePath;
        var solutionDir = solutionPath != null ? Path.GetDirectoryName(solutionPath) : null;
        return PathValidator.Canonicalize(targetFilePath, solutionDir);
    }
}

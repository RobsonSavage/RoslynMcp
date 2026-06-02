using System.ComponentModel.DataAnnotations;
using RoslynMcp.Shared.Contracts.Common;

namespace RoslynMcp.Shared.Contracts.Structure;

// ── get_solution_structure ──

public record GetSolutionStructureRequest(
    bool IncludeMetadata = false);

public record SolutionStructureResponse(
    string SolutionPath,
    IReadOnlyList<ProjectSummary> Projects);

// ── get_project_structure ──

public record GetProjectStructureRequest(
    [property: Required] string ProjectName,
    bool IncludeDocuments = true);

public record DocumentSummary(
    string FilePath,
    string? RelativePath = null,
    int? LineCount = null);

public record ProjectReferenceInfo(
    string ProjectName,
    string FilePath);

public record NuGetReferenceInfo(
    string PackageName,
    string? Version = null);

public record ProjectStructureResponse(
    ProjectSummary Project,
    IReadOnlyList<DocumentSummary> Documents,
    IReadOnlyList<ProjectReferenceInfo> ProjectReferences,
    IReadOnlyList<NuGetReferenceInfo> NuGetReferences);

// ── get_file_outline ──

public record GetFileOutlineRequest(
    [property: Required] string FilePath);

public record OutlineItem(
    string Name,
    string Kind,
    string? ReturnType,
    string Accessibility,
    CodeRange Range,
    IReadOnlyList<OutlineItem> Children);

public record FileOutlineResponse(
    string FilePath,
    IReadOnlyList<OutlineItem> Items);

// ── get_dependency_graph ──

public record GetDependencyGraphRequest(
    string? ProjectName = null,
    int Depth = 3);

public record DependencyNode(
    string ProjectName,
    IReadOnlyList<string> DependsOn);

public record DependencyGraphResponse(
    IReadOnlyList<DependencyNode> Nodes);

// ── get_types_in_file ──

public record GetTypesInFileRequest(
    [property: Required] string FilePath,
    bool IncludeNested = true);

public record TypeSummary(
    string Name,
    string FullyQualifiedName,
    string Kind,
    string Accessibility,
    CodeRange Range,
    bool IsPartial = false,
    bool IsAbstract = false,
    bool IsStatic = false);

public record TypesInFileResponse(
    string FilePath,
    IReadOnlyList<TypeSummary> Types);

// ── get_constructor_parameters ──

public record GetConstructorParametersRequest(
    string? TypeName = null,
    string? FilePath = null,
    int? Line = null,
    int? Column = null);

public record ConstructorSummary(
    string Accessibility,
    IReadOnlyList<ParameterInfo> Parameters,
    CodeLocation? Location = null);

public record ConstructorParametersResponse(
    SymbolInfo Type,
    IReadOnlyList<ConstructorSummary> Constructors);

// ── get_overloads ──

public record GetOverloadsRequest(
    [property: Required] string FilePath,
    int Line,
    int Column,
    bool IncludeContext = false);

public record OverloadItem(
    string Signature,
    IReadOnlyList<ParameterInfo> Parameters,
    string? ReturnType = null,
    CodeLocation? Location = null,
    string? ContextLine = null);

public record OverloadsResponse(
    SymbolInfo Method,
    IReadOnlyList<OverloadItem> Overloads);

// ── get_accessibility ──

public record GetAccessibilityRequest(
    [property: Required] string FilePath,
    int Line,
    int Column);

public record AccessibilityResponse(
    SymbolInfo Symbol,
    string DeclaredAccessibility,
    string EffectiveAccessibility);

// ── get_xml_documentation ──

public record GetXmlDocumentationRequest(
    [property: Required] string FilePath,
    int Line,
    int Column);

public record ParameterDocumentation(
    string Name,
    string Description);

public record ExceptionDocumentation(
    string Type,
    string Description);

public record XmlDocumentationResponse(
    SymbolInfo Symbol,
    string? Summary = null,
    string? Remarks = null,
    string? Returns = null,
    IReadOnlyList<ParameterDocumentation>? Parameters = null,
    IReadOnlyList<ExceptionDocumentation>? Exceptions = null,
    string? Example = null,
    string? RawXml = null);

using RoslynMcp.Shared.Contracts.Common;

namespace RoslynMcp.Shared.Contracts.Graph;

// ── graph_add_node ──

public record GraphAddNodeRequest(
    string Id,
    string Type,
    string? Label = null,
    string? Properties = null);
public record GraphAddNodeResponse(string Id, bool Created);

// ── graph_add_edge ──

public record GraphAddEdgeRequest(
    string SourceId,
    string TargetId,
    string Type,
    string? Label = null,
    string? Properties = null);
public record GraphAddEdgeResponse(long Id, bool Created);

// ── graph_remove_node ──

public record GraphRemoveNodeRequest(string Id, bool Cascade = true);
public record GraphRemoveNodeResponse(int RemovedNodes, int RemovedEdges);

// ── graph_query_neighbors ──

public record GraphQueryNeighborsRequest(
    string NodeId,
    string Direction = GraphDirection.Both,
    string? EdgeType = null,
    int Depth = 1);
public record GraphNode(string Id, string Type, string? Label, string? Properties);
public record GraphEdge(long Id, string SourceId, string TargetId, string Type, string? Label, string? Properties);
public record GraphQueryNeighborsResponse(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges, bool IsTruncated = false);

// ── graph_query_path ──

public record GraphQueryPathRequest(string SourceId, string TargetId, int MaxDepth = 10);
public record GraphPath(IReadOnlyList<string> NodeIds, int Length);
public record GraphQueryPathResponse(IReadOnlyList<GraphPath> Paths, int? ShortestLength, bool IsTruncated = false);

// ── graph_query_subgraph ──

public record GraphQuerySubgraphRequest(
    string RootId,
    int Depth = 2,
    IReadOnlyList<string>? EdgeTypes = null);
public record GraphQuerySubgraphResponse(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges, bool IsTruncated = false);

// ── graph_impact ──

public record GraphImpactRequest(string NodeId, string Direction = GraphDirection.Outgoing);
public record GraphImpactResponse(IReadOnlyList<GraphNode> ImpactedNodes, IReadOnlyList<GraphPath> ImpactPaths, bool IsTruncated = false);

// ── graph_visualize ──

public record GraphVisualizeRequest(
    IReadOnlyList<string>? NodeIds = null,
    string Format = ExportFormat.Mermaid,
    int MaxNodes = 50);
public record GraphVisualizeResponse(string Content, string Format, int NodeCount, int EdgeCount);

// ── graph_stats ──

public record GraphStatsRequest();
public record GraphStatsResponse(
    int NodeCount,
    int EdgeCount,
    IReadOnlyList<string> NodeTypes,
    IReadOnlyList<string> EdgeTypes,
    bool IsStale);

// ── graph_rebuild ──

public record GraphRebuildRequest(bool FullRebuild = false);
public record GraphRebuildResponse(int NodeCount, int EdgeCount, string Duration);

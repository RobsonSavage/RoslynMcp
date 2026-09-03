namespace RoslynMcp.Core.Helpers.Migrations;

/// <summary>
/// Separates graph rows the server derives from the solution from rows a user added through
/// graph_add_node / graph_add_edge, so a rebuild can replace the former without destroying the
/// latter. Existing Project and ProjectReference rows are keyed by a ProjectId GUID that is minted
/// fresh on every solution load, so they are marked derived and the next rebuild clears them.
/// </summary>
public sealed class V4_GraphProvenance : IMigration
{
    public int Version => 4;
    public string Description => "Mark graph rows as derived from the solution or user-authored";

    public string Sql => """
        ALTER TABLE GraphNodes ADD COLUMN IsDerived INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE GraphEdges ADD COLUMN IsDerived INTEGER NOT NULL DEFAULT 0;

        UPDATE GraphNodes SET IsDerived = 1 WHERE Type = 'Project';
        UPDATE GraphEdges SET IsDerived = 1 WHERE Type = 'ProjectReference';
        """;
}

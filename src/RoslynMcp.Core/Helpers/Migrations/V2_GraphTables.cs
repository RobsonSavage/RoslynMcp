namespace RoslynMcp.Core.Helpers.Migrations;

public sealed class V2_GraphTables : IMigration
{
    public int Version => 2;
    public string Description => "Create GraphNodes and GraphEdges tables";

    public string Sql => """
        CREATE TABLE IF NOT EXISTS GraphNodes (
            Id          TEXT PRIMARY KEY,
            Type        TEXT NOT NULL,
            Label       TEXT,
            Properties  TEXT,
            CreatedAt   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS GraphEdges (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            SourceId    TEXT NOT NULL REFERENCES GraphNodes(Id),
            TargetId    TEXT NOT NULL REFERENCES GraphNodes(Id),
            Type        TEXT NOT NULL,
            Label       TEXT,
            Properties  TEXT,
            CreatedAt   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            UNIQUE(SourceId, TargetId, Type)
        );

        CREATE INDEX IF NOT EXISTS IX_GraphEdges_SourceId ON GraphEdges(SourceId);
        CREATE INDEX IF NOT EXISTS IX_GraphEdges_TargetId ON GraphEdges(TargetId);
        CREATE INDEX IF NOT EXISTS IX_GraphEdges_Type ON GraphEdges(Type);
        CREATE INDEX IF NOT EXISTS IX_GraphNodes_Type ON GraphNodes(Type);
        """;
}

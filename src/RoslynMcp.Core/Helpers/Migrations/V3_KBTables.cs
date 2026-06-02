namespace RoslynMcp.Core.Helpers.Migrations;

public sealed class V3_KBTables : IMigration
{
    public int Version => 3;
    public string Description => "Create KBEntries table (FTS5 initialized at runtime)";

    public string Sql => """
        CREATE TABLE IF NOT EXISTS KBEntries (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            Title           TEXT NOT NULL,
            Content         TEXT NOT NULL,
            Category        TEXT,
            Tags            TEXT,
            Metadata        TEXT,
            SolutionVersion INTEGER DEFAULT 0,
            CreatedAt       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            UpdatedAt       DATETIME
        );

        CREATE INDEX IF NOT EXISTS IX_KBEntries_Category ON KBEntries(Category);
        """;

    /// <summary>
    /// FTS5 setup SQL, applied separately by KBService since FTS5 may not be available.
    /// </summary>
    public static string Fts5Sql => """
        CREATE VIRTUAL TABLE IF NOT EXISTS KBEntries_fts USING fts5(
            Title,
            Content,
            content=KBEntries,
            content_rowid=Id,
            tokenize='porter unicode61'
        );

        CREATE TRIGGER IF NOT EXISTS KBEntries_ai AFTER INSERT ON KBEntries BEGIN
            INSERT INTO KBEntries_fts(rowid, Title, Content)
            VALUES (new.Id, new.Title, new.Content);
        END;

        CREATE TRIGGER IF NOT EXISTS KBEntries_ad AFTER DELETE ON KBEntries BEGIN
            INSERT INTO KBEntries_fts(KBEntries_fts, rowid, Title, Content)
            VALUES('delete', old.Id, old.Title, old.Content);
        END;

        CREATE TRIGGER IF NOT EXISTS KBEntries_au AFTER UPDATE ON KBEntries BEGIN
            INSERT INTO KBEntries_fts(KBEntries_fts, rowid, Title, Content)
            VALUES('delete', old.Id, old.Title, old.Content);
            INSERT INTO KBEntries_fts(rowid, Title, Content)
            VALUES (new.Id, new.Title, new.Content);
        END;
        """;
}

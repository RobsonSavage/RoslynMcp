namespace RoslynMcp.Core.Helpers.Migrations;

public sealed class V1_MemoryTables : IMigration
{
    public int Version => 1;
    public string Description => "Create Sessions and MemoryEntries tables";

    public string Sql => """
        CREATE TABLE IF NOT EXISTS Sessions (
            SessionId   TEXT PRIMARY KEY,
            SessionName TEXT,
            StartedAt   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            EndedAt     DATETIME,
            Metadata    TEXT
        );

        CREATE TABLE IF NOT EXISTS MemoryEntries (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            Key         TEXT NOT NULL,
            Value       TEXT NOT NULL,
            Category    TEXT,
            Tags        TEXT,
            SessionId   TEXT REFERENCES Sessions(SessionId),
            Metadata    TEXT,
            StoredAt    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            UpdatedAt   DATETIME,
            UNIQUE(Key, SessionId)
        );

        CREATE INDEX IF NOT EXISTS IX_MemoryEntries_Key ON MemoryEntries(Key);
        CREATE INDEX IF NOT EXISTS IX_MemoryEntries_Category ON MemoryEntries(Category);
        CREATE INDEX IF NOT EXISTS IX_MemoryEntries_SessionId ON MemoryEntries(SessionId);
        """;
}

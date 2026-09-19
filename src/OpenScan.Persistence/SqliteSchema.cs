using Dapper;

namespace OpenScan.Persistence;

/// <summary>Schéma minimal SQLite (section 18) : profiles, jobs, pages.</summary>
public static class SqliteSchema
{
    public static void EnsureCreated(SqliteConnectionFactory factory)
    {
        DapperTypeHandlers.EnsureRegistered();

        using var connection = factory.Create();
        connection.Execute("""
            CREATE TABLE IF NOT EXISTS Profiles (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Source TEXT NOT NULL,
                Duplex INTEGER NOT NULL,
                Resolution INTEGER NOT NULL,
                PixelFormat TEXT NOT NULL,
                BitDepth INTEGER NULL,
                PageSize TEXT NOT NULL,
                Orientation TEXT NOT NULL,
                Brightness INTEGER NULL,
                Contrast INTEGER NULL,
                Feeder TEXT NOT NULL,
                BlankPageDetection INTEGER NOT NULL,
                Deskew INTEGER NOT NULL,
                Crop TEXT NOT NULL,
                Compression TEXT NULL,
                OutputFormat TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Jobs (
                Id TEXT PRIMARY KEY,
                ScannerId TEXT NOT NULL,
                ProfileId TEXT NOT NULL,
                RequestedConfiguration TEXT NULL,
                EffectiveConfiguration TEXT NULL,
                Status TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                StartedAt TEXT NULL,
                CompletedAt TEXT NULL,
                TotalPages INTEGER NOT NULL,
                ProcessedPages INTEGER NOT NULL,
                FailedPages INTEGER NOT NULL,
                Error TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Pages (
                Id TEXT PRIMARY KEY,
                JobId TEXT NOT NULL,
                PageNumber INTEGER NOT NULL,
                Status TEXT NOT NULL,
                Width INTEGER NOT NULL,
                Height INTEGER NOT NULL,
                Dpi INTEGER NOT NULL,
                PixelFormat TEXT NULL,
                Source TEXT NULL,
                FileReference TEXT NULL,
                Checksum TEXT NULL,
                CreatedAt TEXT NOT NULL,
                Error TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Pages_JobId ON Pages(JobId);
            """);
    }
}

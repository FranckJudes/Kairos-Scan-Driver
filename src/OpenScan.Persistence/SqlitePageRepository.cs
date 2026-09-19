using System.Text.Json;
using Dapper;
using OpenScan.Contracts;
using OpenScan.Core.Abstractions;

namespace OpenScan.Persistence;

public sealed class SqlitePageRepository(SqliteConnectionFactory factory) : IPageRepository
{
    public async Task SaveAsync(ScanPage page, CancellationToken ct = default)
    {
        using var connection = factory.Create();
        await connection.ExecuteAsync("""
            INSERT INTO Pages
                (Id, JobId, PageNumber, Status, Width, Height, Dpi, PixelFormat, Source,
                 FileReference, Checksum, CreatedAt, Error)
            VALUES
                (@Id, @JobId, @PageNumber, @Status, @Width, @Height, @Dpi, @PixelFormat, @Source,
                 @FileReference, @Checksum, @CreatedAt, @Error)
            ON CONFLICT(Id) DO UPDATE SET
                Status = excluded.Status,
                FileReference = excluded.FileReference,
                Checksum = excluded.Checksum,
                Error = excluded.Error
            """, new
        {
            page.Id,
            page.JobId,
            page.PageNumber,
            Status = page.Status.ToString(),
            page.Width,
            page.Height,
            page.Dpi,
            page.PixelFormat,
            page.Source,
            page.FileReference,
            page.Checksum,
            page.CreatedAt,
            Error = page.Error is null ? null : JsonSerializer.Serialize(page.Error)
        });
    }

    public async Task<ScanPage?> GetAsync(string pageId, CancellationToken ct = default)
    {
        using var connection = factory.Create();
        var row = await connection.QuerySingleOrDefaultAsync<PageRow>(
            "SELECT * FROM Pages WHERE Id = @pageId", new { pageId });
        return row?.ToPage();
    }

    public async Task<IReadOnlyList<ScanPage>> ListByJobAsync(string jobId, CancellationToken ct = default)
    {
        using var connection = factory.Create();
        var rows = await connection.QueryAsync<PageRow>(
            "SELECT * FROM Pages WHERE JobId = @jobId ORDER BY PageNumber", new { jobId });
        return rows.Select(r => r.ToPage()).ToList();
    }

    private sealed class PageRow
    {
        public string Id { get; set; } = "";
        public string JobId { get; set; } = "";
        public int PageNumber { get; set; }
        public string Status { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public int Dpi { get; set; }
        public string? PixelFormat { get; set; }
        public string? Source { get; set; }
        public string? FileReference { get; set; }
        public string? Checksum { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string? Error { get; set; }

        public ScanPage ToPage() => new()
        {
            Id = Id,
            JobId = JobId,
            PageNumber = PageNumber,
            Status = Enum.Parse<PageStatus>(Status),
            Width = Width,
            Height = Height,
            Dpi = Dpi,
            PixelFormat = PixelFormat,
            Source = Source,
            FileReference = FileReference,
            Checksum = Checksum,
            CreatedAt = CreatedAt,
            Error = Error is null ? null : JsonSerializer.Deserialize<ScanError>(Error)
        };
    }
}

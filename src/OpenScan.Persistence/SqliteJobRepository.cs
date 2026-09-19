using System.Text.Json;
using Dapper;
using OpenScan.Contracts;
using OpenScan.Core.Abstractions;

namespace OpenScan.Persistence;

public sealed class SqliteJobRepository(SqliteConnectionFactory factory) : IJobRepository
{
    public async Task SaveAsync(ScanJob job, CancellationToken ct = default)
    {
        using var connection = factory.Create();
        await connection.ExecuteAsync("""
            INSERT INTO Jobs
                (Id, ScannerId, ProfileId, RequestedConfiguration, EffectiveConfiguration, Status,
                 CreatedAt, StartedAt, CompletedAt, TotalPages, ProcessedPages, FailedPages, Error)
            VALUES
                (@Id, @ScannerId, @ProfileId, @RequestedConfiguration, @EffectiveConfiguration, @Status,
                 @CreatedAt, @StartedAt, @CompletedAt, @TotalPages, @ProcessedPages, @FailedPages, @Error)
            ON CONFLICT(Id) DO UPDATE SET
                EffectiveConfiguration = excluded.EffectiveConfiguration,
                Status = excluded.Status,
                StartedAt = excluded.StartedAt,
                CompletedAt = excluded.CompletedAt,
                TotalPages = excluded.TotalPages,
                ProcessedPages = excluded.ProcessedPages,
                FailedPages = excluded.FailedPages,
                Error = excluded.Error
            """, new
        {
            job.Id,
            job.ScannerId,
            job.ProfileId,
            RequestedConfiguration = Serialize(job.RequestedConfiguration),
            EffectiveConfiguration = Serialize(job.EffectiveConfiguration),
            Status = job.Status.ToString(),
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt,
            job.TotalPages,
            job.ProcessedPages,
            job.FailedPages,
            Error = Serialize(job.Error)
        });
    }

    public async Task<ScanJob?> GetAsync(string jobId, CancellationToken ct = default)
    {
        using var connection = factory.Create();
        var row = await connection.QuerySingleOrDefaultAsync<JobRow>(
            "SELECT * FROM Jobs WHERE Id = @jobId", new { jobId });
        return row?.ToJob();
    }

    public async Task<IReadOnlyList<ScanJob>> ListAsync(CancellationToken ct = default)
    {
        using var connection = factory.Create();
        var rows = await connection.QueryAsync<JobRow>("SELECT * FROM Jobs ORDER BY CreatedAt DESC");
        return rows.Select(r => r.ToJob()).ToList();
    }

    private static string? Serialize<T>(T? value) => value is null ? null : JsonSerializer.Serialize(value);

    private sealed class JobRow
    {
        public string Id { get; set; } = "";
        public string ScannerId { get; set; } = "";
        public string ProfileId { get; set; } = "";
        public string? RequestedConfiguration { get; set; }
        public string? EffectiveConfiguration { get; set; }
        public string Status { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public int TotalPages { get; set; }
        public int ProcessedPages { get; set; }
        public int FailedPages { get; set; }
        public string? Error { get; set; }

        public ScanJob ToJob() => new()
        {
            Id = Id,
            ScannerId = ScannerId,
            ProfileId = ProfileId,
            RequestedConfiguration = RequestedConfiguration is null ? null : JsonSerializer.Deserialize<ScanProfile>(RequestedConfiguration),
            EffectiveConfiguration = EffectiveConfiguration is null ? null : JsonSerializer.Deserialize<EffectiveConfiguration>(EffectiveConfiguration),
            Status = Enum.Parse<JobStatus>(Status),
            CreatedAt = CreatedAt,
            StartedAt = StartedAt,
            CompletedAt = CompletedAt,
            TotalPages = TotalPages,
            ProcessedPages = ProcessedPages,
            FailedPages = FailedPages,
            Error = Error is null ? null : JsonSerializer.Deserialize<ScanError>(Error)
        };
    }
}

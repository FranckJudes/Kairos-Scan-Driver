namespace OpenScan.Contracts;

/// <summary>Job de numérisation (section 7.5).</summary>
public sealed class ScanJob
{
    public string Id { get; set; } = string.Empty;
    public string ScannerId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public ScanProfile? RequestedConfiguration { get; set; }
    public EffectiveConfiguration? EffectiveConfiguration { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Created;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int TotalPages { get; set; }
    public int ProcessedPages { get; set; }
    public int FailedPages { get; set; }
    public ScanError? Error { get; set; }
}

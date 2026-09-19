namespace OpenScan.Contracts;

/// <summary>Page numérisée (section 7.6).</summary>
public sealed class ScanPage
{
    public string Id { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public PageStatus Status { get; set; } = PageStatus.Acquired;
    public int Width { get; set; }
    public int Height { get; set; }
    public int Dpi { get; set; }
    public string? PixelFormat { get; set; }
    public string? Source { get; set; }
    public string? FileReference { get; set; }
    public string? Checksum { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ScanError? Error { get; set; }
}

/// <summary>Erreur normalisée indépendante des backends (section 17).</summary>
public sealed class ScanError
{
    public ErrorCode Code { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Backend { get; set; }
    public string? ScannerId { get; set; }
    public string? JobId { get; set; }
    public string? PageId { get; set; }
    public bool Recoverable { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string? TechnicalDetails { get; set; }
}

/// <summary>Page brute produite par un backend, avant traitement/persistance.</summary>
public sealed class AcquiredPage
{
    public required int PageNumber { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Dpi { get; init; }
    public string PixelFormat { get; init; } = "color";
    public string Source { get; init; } = "adf";
    public required Stream Data { get; init; }
}

namespace OpenScan.Contracts;


public sealed class ScanEngineException : Exception
{
    public ErrorCode Code { get; }
    public bool Recoverable { get; }
    public string? Backend { get; init; }
    public string? ScannerId { get; init; }
    public string? JobId { get; init; }
    public string? PageId { get; init; }
    public string? TechnicalDetails { get; init; }

    public ScanEngineException(ErrorCode code, string message, bool recoverable = false, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Recoverable = recoverable;
    }

    public ScanError ToScanError() => new()
    {
        Code = Code,
        Message = Message,
        Backend = Backend,
        ScannerId = ScannerId,
        JobId = JobId,
        PageId = PageId,
        Recoverable = Recoverable,
        TechnicalDetails = TechnicalDetails ?? InnerException?.ToString()
    };
}

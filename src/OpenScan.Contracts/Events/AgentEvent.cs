namespace OpenScan.Contracts;

/// <summary>Enveloppe d'événement temps réel diffusée sur le WebSocket (section 14).</summary>
public sealed class AgentEvent
{
    public required string Event { get; init; }
    public string? ScannerId { get; init; }
    public string? JobId { get; init; }
    public int? PageNumber { get; init; }
    public string? PageId { get; init; }
    public ScanError? Error { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Catalogue des noms d'événements minimaux imposés par le cahier des charges.</summary>
public static class EventNames
{
    public const string ScannerConnected = "scanner.connected";
    public const string ScannerDisconnected = "scanner.disconnected";
    public const string ScannerStatusChanged = "scanner.status_changed";

    public const string ScanCreated = "scan.created";
    public const string ScanValidating = "scan.validating";
    public const string ScanStarted = "scan.started";
    public const string ScanPaused = "scan.paused";
    public const string ScanCancelled = "scan.cancelled";
    public const string ScanCompleted = "scan.completed";
    public const string ScanError = "scan.error";

    public const string PageAcquired = "page.acquired";
    public const string PageProcessing = "page.processing";
    public const string PageReady = "page.ready";
    public const string PagePersisted = "page.persisted";
    public const string PageError = "page.error";
}

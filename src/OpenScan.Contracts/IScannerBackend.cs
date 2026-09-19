namespace OpenScan.Contracts;


public interface IScannerBackend
{
    string BackendId { get; }

    Task<IReadOnlyList<Scanner>> DiscoverAsync(CancellationToken ct = default);

    Task OpenAsync(string scannerId, CancellationToken ct = default);

    Task CloseAsync(string scannerId, CancellationToken ct = default);

    Task<ScannerCapabilities> GetCapabilitiesAsync(string scannerId, CancellationToken ct = default);

    Task ConfigureAsync(string scannerId, string jobId, EffectiveConfiguration configuration, CancellationToken ct = default);

    Task StartAsync(string scannerId, string jobId, CancellationToken ct = default);

    IAsyncEnumerable<AcquiredPage> AcquireAsync(string scannerId, string jobId, CancellationToken ct = default);

    Task StopAsync(string scannerId, string jobId, CancellationToken ct = default);

    Task<ScannerStatus> GetStatusAsync(string scannerId, CancellationToken ct = default);

    Task CancelAsync(string scannerId, string jobId, CancellationToken ct = default);
}

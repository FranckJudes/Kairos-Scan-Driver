using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OpenScan.Contracts;

namespace OpenScan.Backends.Mock;

public sealed class MockScannerBackend : IScannerBackend
{
    public const string Id = "mock";
    private const int DefaultPageCount = 3;

    private static readonly Scanner FakeScanner = new()
    {
        Id = "mock-scanner-1",
        Name = "OpenScan Mock Scanner",
        Manufacturer = "OpenScan",
        Model = "Virtual ADF/Flatbed",
        SerialNumber = "MOCK-0001",
        Backend = Id,
        ConnectionType = ConnectionType.Virtual,
        Status = ScannerStatus.Idle
    };

    private static readonly ScannerCapabilities FakeCapabilities = new()
    {
        Sources = ["adf", "flatbed"],
        Duplex = true,
        Resolutions = [100, 150, 200, 300, 600],
        PixelFormats = ["color", "grayscale", "bw"],
        BitDepths = [1, 8, 24],
        PaperSizes = ["a4", "letter", "a3"],
        Adf = true,
        Flatbed = true,
        MaxPageSize = new PageSize { Width = 21000, Height = 35600 },
        MinPageSize = new PageSize { Width = 5000, Height = 5000 }
    };

    private readonly ConcurrentDictionary<string, int> _pagesPerJob = new();

    public string BackendId => Id;

    public Task<IReadOnlyList<Scanner>> DiscoverAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Scanner>>([FakeScanner]);

    public Task OpenAsync(string scannerId, CancellationToken ct = default)
    {
        EnsureKnown(scannerId);
        return Task.CompletedTask;
    }

    public Task CloseAsync(string scannerId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<ScannerCapabilities> GetCapabilitiesAsync(string scannerId, CancellationToken ct = default)
    {
        EnsureKnown(scannerId);
        return Task.FromResult(FakeCapabilities);
    }

    public Task ConfigureAsync(string scannerId, string jobId, EffectiveConfiguration configuration, CancellationToken ct = default)
    {
        EnsureKnown(scannerId);
        return Task.CompletedTask; 
    }

    public Task StartAsync(string scannerId, string jobId, CancellationToken ct = default)
    {
        EnsureKnown(scannerId);
        _pagesPerJob[jobId] = DefaultPageCount;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<AcquiredPage> AcquireAsync(
        string scannerId,
        string jobId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var pageCount = _pagesPerJob.GetValueOrDefault(jobId, DefaultPageCount);
        for (var i = 1; i <= pageCount; i++)
        {
            if (!_pagesPerJob.ContainsKey(jobId))
            {
                yield break;
            }

            ct.ThrowIfCancellationRequested();
            await Task.Delay(150, ct); 

            yield return new AcquiredPage
            {
                PageNumber = i,
                Width = 2480,
                Height = 3508,
                Dpi = 300,
                PixelFormat = "color",
                Source = "adf",
                Data = GeneratePageData(i)
            };
        }
    }

    public Task StopAsync(string scannerId, string jobId, CancellationToken ct = default)
    {
        _pagesPerJob.TryRemove(jobId, out _);
        return Task.CompletedTask;
    }

    public Task<ScannerStatus> GetStatusAsync(string scannerId, CancellationToken ct = default) =>
        Task.FromResult(ScannerStatus.Idle);

    public Task CancelAsync(string scannerId, string jobId, CancellationToken ct = default)
    {
        _pagesPerJob.TryRemove(jobId, out _);
        return Task.CompletedTask;
    }

    private static void EnsureKnown(string scannerId)
    {
        if (scannerId != FakeScanner.Id)
        {
            throw new ScanEngineException(ErrorCode.ScannerNotFound, $"Scanner '{scannerId}' introuvable.")
            {
                Backend = Id,
                ScannerId = scannerId
            };
        }
    }

    private static Stream GeneratePageData(int pageNumber)
    {
        // Contenu déterministe et bon marché (pas d'encodage image réel) : suffisant
        // pour exercer le pipeline de bout en bout (acquisition -> stockage -> checksum).
        var buffer = new byte[64 * 1024];
        var seed = (byte)(pageNumber * 37);
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(seed + i);
        }

        return new MemoryStream(buffer, writable: false);
    }
}

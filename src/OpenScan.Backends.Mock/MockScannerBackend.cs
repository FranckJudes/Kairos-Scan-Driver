using System.Buffers.Binary;
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

    public sealed record MockJobBehavior(int PageCount, int? FailAtPage = null, ScanEngineException? Exception = null);

    private readonly ConcurrentDictionary<string, int> _pagesPerJob = new();
    private readonly ConcurrentDictionary<string, MockJobBehavior> _jobBehaviors = new();
    private MockJobBehavior? _defaultBehavior;

    public void SetJobBehavior(string jobId, int pageCount, int? failAtPage = null, ScanEngineException? exception = null)
    {
        _jobBehaviors[jobId] = new MockJobBehavior(pageCount, failAtPage, exception);
    }

    public void SetDefaultBehavior(int pageCount, int? failAtPage = null, ScanEngineException? exception = null)
    {
        _defaultBehavior = new MockJobBehavior(pageCount, failAtPage, exception);
    }

    public void ResetDefaultBehavior() => _defaultBehavior = null;

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
        if (_jobBehaviors.TryGetValue(jobId, out var behavior))
        {
            _pagesPerJob[jobId] = behavior.PageCount;
        }
        else if (_defaultBehavior is not null)
        {
            _pagesPerJob[jobId] = _defaultBehavior.PageCount;
        }
        else
        {
            _pagesPerJob[jobId] = DefaultPageCount;
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<AcquiredPage> AcquireAsync(
        string scannerId,
        string jobId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var behavior = _jobBehaviors.GetValueOrDefault(jobId) ?? _defaultBehavior;
        var pageCount = behavior?.PageCount ?? _pagesPerJob.GetValueOrDefault(jobId, DefaultPageCount);
        for (var i = 1; i <= pageCount; i++)
        {
            if (!_pagesPerJob.ContainsKey(jobId))
            {
                yield break;
            }

            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct); 

            if (behavior?.FailAtPage == i)
            {
                throw behavior.Exception ?? new ScanEngineException(ErrorCode.PaperJam, $"Erreur simulée à la page {i}", recoverable: true);
            }

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

    private static Stream GeneratePageData(int pageNumber, int width = 2480, int height = 3508)
    {
        var rowStride = ((width * 3 + 3) / 4) * 4;
        var imageSize = rowStride * height;
        var fileSize = 54 + imageSize;

        var buffer = new byte[fileSize];

        // BITMAPFILEHEADER (14 octets)
        buffer[0] = 0x42; // 'B'
        buffer[1] = 0x4D; // 'M'
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(2, 4), fileSize);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(10, 4), 54); // OffBits

        // BITMAPINFOHEADER (40 octets)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(14, 4), 40); // biSize
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(18, 4), width); // biWidth
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(22, 4), height); // biHeight (bottom-up)
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(26, 2), 1); // biPlanes
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(28, 2), 24); // biBitCount
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(30, 4), 0); // biCompression = BI_RGB
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(34, 4), imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(38, 4), 11811); // ~300 DPI XPelsPerMeter
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(42, 4), 11811); // ~300 DPI YPelsPerMeter

        // Remplir les pixels (mire déterministe et contrastée)
        var seedR = (byte)(80 + (pageNumber * 45) % 150);
        var seedG = (byte)(110 + (pageNumber * 65) % 130);
        var seedB = (byte)(140 + (pageNumber * 85) % 100);

        var pixelSpan = buffer.AsSpan(54);
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * rowStride;
            var r = (byte)(seedR + (y * 50 / height));
            for (var x = 0; x < width; x++)
            {
                var px = rowOffset + (x * 3);
                // BGR format pour BMP
                pixelSpan[px] = (byte)(seedB + (x * 40 / width));
                pixelSpan[px + 1] = seedG;
                pixelSpan[px + 2] = r;
            }
        }

        return new MemoryStream(buffer, writable: false);
    }
}

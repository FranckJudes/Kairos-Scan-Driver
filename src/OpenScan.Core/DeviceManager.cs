using System.Collections.Concurrent;
using OpenScan.Contracts;

namespace OpenScan.Core;


public sealed class DeviceManager
{
    private readonly Dictionary<string, IScannerBackend> _backends;
    private readonly ConcurrentDictionary<string, Scanner> _scanners = new();
    private readonly ConcurrentDictionary<string, string> _reservations = new();

    public DeviceManager(IEnumerable<IScannerBackend> backends)
    {
        _backends = backends.ToDictionary(b => b.BackendId);
    }

    public async Task<IReadOnlyList<Scanner>> DiscoverAllAsync(CancellationToken ct = default)
    {
        foreach (var backend in _backends.Values)
        {
            IReadOnlyList<Scanner> found;
            try
            {
                found = await backend.DiscoverAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested is false)
            {
                continue;
            }

            foreach (var scanner in found)
            {
                scanner.Backend = backend.BackendId;
                _scanners[scanner.Id] = scanner;
            }
        }

        return _scanners.Values.ToList();
    }

    public IReadOnlyList<Scanner> GetKnownScanners() => _scanners.Values.ToList();

    public Scanner GetScanner(string scannerId) =>
        _scanners.TryGetValue(scannerId, out var scanner)
            ? scanner
            : throw new ScanEngineException(ErrorCode.ScannerNotFound, $"Scanner '{scannerId}' introuvable.");

    public IScannerBackend GetBackendFor(Scanner scanner) =>
        _backends.TryGetValue(scanner.Backend, out var backend)
            ? backend
            : throw new ScanEngineException(ErrorCode.BackendError, $"Backend '{scanner.Backend}' introuvable.");

    public void Reserve(string scannerId, string jobId)
    {
        if (!_reservations.TryAdd(scannerId, jobId))
        {
            throw new ScanEngineException(ErrorCode.ScannerBusy, $"Scanner '{scannerId}' est déjà occupé par un autre job.");
        }
    }

    public void Release(string scannerId) => _reservations.TryRemove(scannerId, out _);
}

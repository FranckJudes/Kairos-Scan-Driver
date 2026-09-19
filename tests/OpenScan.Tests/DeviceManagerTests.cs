using OpenScan.Backends.Mock;
using OpenScan.Contracts;
using OpenScan.Core;

namespace OpenScan.Tests;

public class DeviceManagerTests
{
    private static async Task<DeviceManager> CreateWithMockDiscoveredAsync()
    {
        var manager = new DeviceManager([new MockScannerBackend()]);
        await manager.DiscoverAllAsync();
        return manager;
    }

    [Fact]
    public async Task DiscoverAllAsync_ReturnsMockScanner()
    {
        var manager = await CreateWithMockDiscoveredAsync();

        var scanners = manager.GetKnownScanners();

        Assert.Single(scanners);
        Assert.Equal(MockScannerBackend.Id, scanners[0].Backend);
    }

    [Fact]
    public async Task GetScanner_UnknownId_ThrowsScannerNotFound()
    {
        var manager = await CreateWithMockDiscoveredAsync();

        var ex = Assert.Throws<ScanEngineException>(() => manager.GetScanner("does-not-exist"));

        Assert.Equal(ErrorCode.ScannerNotFound, ex.Code);
    }

    [Fact]
    public async Task Reserve_SameScannerTwice_ThrowsScannerBusy()
    {
        var manager = await CreateWithMockDiscoveredAsync();
        var scannerId = manager.GetKnownScanners()[0].Id;

        manager.Reserve(scannerId, "job-1");
        var ex = Assert.Throws<ScanEngineException>(() => manager.Reserve(scannerId, "job-2"));

        Assert.Equal(ErrorCode.ScannerBusy, ex.Code);
    }

    [Fact]
    public async Task Reserve_AfterRelease_SucceedsForNewJob()
    {
        var manager = await CreateWithMockDiscoveredAsync();
        var scannerId = manager.GetKnownScanners()[0].Id;

        manager.Reserve(scannerId, "job-1");
        manager.Release(scannerId);

        manager.Reserve(scannerId, "job-2"); // ne doit pas lever d'exception
    }
}

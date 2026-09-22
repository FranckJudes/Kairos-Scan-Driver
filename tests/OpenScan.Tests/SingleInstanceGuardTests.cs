using OpenScan.Core.Hosting;
using Xunit;

namespace OpenScan.Tests;

public class SingleInstanceGuardTests : IDisposable
{
    private readonly string _testDir;

    public SingleInstanceGuardTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "OpenScanTests_Guard_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore clean up errors
        }
    }

    [Fact]
    public void TryAcquireFileLock_SingleAcquisition_Succeeds()
    {
        var lockFile = Path.Combine(_testDir, "test.lock");

        var acquired = SingleInstanceGuard.TryAcquireFileLock(lockFile, out var handle);

        Assert.True(acquired);
        Assert.NotNull(handle);
        handle.Dispose();
    }

    [Fact]
    public void TryAcquireFileLock_DoubleAcquisition_Fails()
    {
        var lockFile = Path.Combine(_testDir, "test.lock");

        var acquired1 = SingleInstanceGuard.TryAcquireFileLock(lockFile, out var handle1);
        Assert.True(acquired1);
        Assert.NotNull(handle1);

        using (handle1)
        {
            var acquired2 = SingleInstanceGuard.TryAcquireFileLock(lockFile, out var handle2);
            Assert.False(acquired2);
            Assert.Null(handle2);
        }
    }

    [Fact]
    public void TryAcquireFileLock_AfterDispose_CanAcquireAgain()
    {
        var lockFile = Path.Combine(_testDir, "test.lock");

        var acquired1 = SingleInstanceGuard.TryAcquireFileLock(lockFile, out var handle1);
        Assert.True(acquired1);
        Assert.NotNull(handle1);

        handle1.Dispose();

        var acquired2 = SingleInstanceGuard.TryAcquireFileLock(lockFile, out var handle2);
        Assert.True(acquired2);
        Assert.NotNull(handle2);
        handle2.Dispose();
    }

    [Fact]
    public void TryAcquireFileLock_CreatesSubdirectoriesIfNotExist()
    {
        var nestedLockFile = Path.Combine(_testDir, "nested", "folder", "test.lock");

        var acquired = SingleInstanceGuard.TryAcquireFileLock(nestedLockFile, out var handle);

        Assert.True(acquired);
        Assert.NotNull(handle);
        Assert.True(File.Exists(nestedLockFile));
        handle.Dispose();
    }

    [Fact]
    public void TryAcquire_OnCurrentPlatform_AcquiresAndBlocksSecondAcquisition()
    {
        var target = OperatingSystem.IsWindows()
            ? @"Local\OpenScanAgentTest_" + Guid.NewGuid().ToString("n")
            : Path.Combine(_testDir, "agent.lock");

        var acquired1 = SingleInstanceGuard.TryAcquire(target, out var handle1);
        Assert.True(acquired1);
        Assert.NotNull(handle1);

        using (handle1)
        {
            var acquired2 = SingleInstanceGuard.TryAcquire(target, out var handle2);
            Assert.False(acquired2);
            Assert.Null(handle2);
        }

        // Après libération, une nouvelle tentative réussit
        var acquired3 = SingleInstanceGuard.TryAcquire(target, out var handle3);
        Assert.True(acquired3);
        Assert.NotNull(handle3);
        handle3.Dispose();
    }
}

using OpenScan.Backends.Twain;
using OpenScan.Contracts;

namespace OpenScan.Tests;

public class TwainScannerBackendTests : IDisposable
{
    private readonly string _testDirectory;

    public TwainScannerBackendTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"openscan-twain-test-{Guid.NewGuid():n}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch { /* ignore cleanup errors */ }
    }

    [Fact]
    public void ResolveWorker_WithNonExistentCustomPath_FailsWithExactCustomPathInSearchedList()
    {
        var dummyPath = Path.Combine(_testDirectory, "non_existent_worker.exe");
        var backend = new TwainScannerBackend(customWorkerExecutablePath: dummyPath);

        var result = backend.ResolveWorkerExecutablePath();

        Assert.False(result.Found);
        Assert.Null(result.ExecutablePath);
        Assert.Single(result.SearchedPaths);
        Assert.Equal(dummyPath, result.SearchedPaths[0]);
    }

    [Fact]
    public void ResolveWorker_WithoutCustomPath_InspectsExpectedCandidateLocations()
    {
        var backend = new TwainScannerBackend();

        var result = backend.ResolveWorkerExecutablePath();

        Assert.NotEmpty(result.SearchedPaths);
        // Doit inclure le répertoire de base et la remontée vers les projets de dev
        Assert.Contains(result.SearchedPaths, p => p.Contains(AppContext.BaseDirectory));
        Assert.Contains(result.SearchedPaths, p => p.Contains("OpenScan.Twain.Worker"));
    }

    [Fact]
    public async Task DiscoverAsync_WhenWorkerNotFound_LogsWarningListingCandidatesAndReturnsEmptyList()
    {
        var logger = new CapturingTwainLogger();
        var dummyPath = Path.Combine(_testDirectory, "missing_worker.exe");
        var backend = new TwainScannerBackend(customWorkerExecutablePath: dummyPath, logger: logger);

        var scanners = await backend.DiscoverAsync();

        Assert.Empty(scanners);
        Assert.Single(logger.Warnings);
        Assert.Contains("Le worker TWAIN (OpenScan.Twain.Worker) est introuvable", logger.Warnings[0]);
        Assert.Contains(dummyPath, logger.Warnings[0]);
    }

    [Fact]
    public async Task DiscoverAsync_WhenWorkerReturnsErrorResponse_LogsWarningAndReturnsEmptyList()
    {
        var logger = new CapturingTwainLogger();
        var scriptPath = Path.Combine(_testDirectory, "dsm_error_worker.sh");
        await File.WriteAllTextAsync(scriptPath, """
            #!/bin/sh
            read line
            echo '{"type":"error","code":"DriverError","message":"Impossible de localiser twaindsm.dll","recoverable":false,"technicalDetails":"DllNotFoundException"}'
            exit 0
            """);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var backend = new TwainScannerBackend(customWorkerExecutablePath: scriptPath, logger: logger);

        var scanners = await backend.DiscoverAsync();

        Assert.Empty(scanners);
        Assert.Contains(logger.Warnings, w => w.Contains("Impossible de localiser twaindsm.dll") && w.Contains("DriverError"));
    }

    [Fact]
    public async Task GetCapabilitiesAsync_WhenWorkerNotFound_ThrowsScanEngineExceptionListingCandidates()
    {
        var dummyPath = Path.Combine(_testDirectory, "missing_worker.exe");
        var backend = new TwainScannerBackend(customWorkerExecutablePath: dummyPath);

        var ex = await Assert.ThrowsAsync<ScanEngineException>(() => backend.GetCapabilitiesAsync("scanner-1"));

        Assert.Equal(ErrorCode.BackendError, ex.Code);
        Assert.Contains("Exécutable OpenScan.Twain.Worker introuvable", ex.Message);
        Assert.Contains(dummyPath, ex.Message);
    }

    [Fact]
    public async Task DiscoverAsync_OnNonWindows_WithoutCustomWorker_ReturnsEmptyList()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var backend = new TwainScannerBackend();
        var scanners = await backend.DiscoverAsync();

        Assert.NotNull(scanners);
        Assert.Empty(scanners);
    }

    [Fact]
    public async Task AcquireAsync_WhenWorkerCrashes_ThrowsScanEngineExceptionWithDriverError()
    {
        // Création d'un worker script simulant un crash brutal (SIGSEGV / exit 139)
        var scriptPath = Path.Combine(_testDirectory, "crashing_worker.sh");
        await File.WriteAllTextAsync(scriptPath, """
            #!/bin/sh
            read line
            read line
            exit 139
            """);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var backend = new TwainScannerBackend(customWorkerExecutablePath: scriptPath);

        var config = new EffectiveConfiguration
        {
            Effective = new Dictionary<string, object?>
            {
                ["source"] = "adf",
                ["resolution"] = 300,
                ["pixel_format"] = "color"
            }
        };

        await backend.ConfigureAsync("scanner-1", "job-crash", config);

        var ex = await Assert.ThrowsAsync<ScanEngineException>(async () =>
        {
            await foreach (var _ in backend.AcquireAsync("scanner-1", "job-crash"))
            {
                // ne doit pas atteindre ici
            }
        });

        Assert.Equal(ErrorCode.DriverError, ex.Code);
        Assert.False(ex.Recoverable);
        Assert.Equal("twain", ex.Backend);
        Assert.Equal("job-crash", ex.JobId);
    }

    [Fact]
    public async Task AcquireAsync_WhenWorkerProducesPage_YieldsAcquiredPageAndCleansUp()
    {
        var tempImageFile = Path.Combine(_testDirectory, "test_page_1.bmp");
        await File.WriteAllBytesAsync(tempImageFile, [0x42, 0x4D, 0x00, 0x00]); // Entête BMP minimale

        var scriptPath = Path.Combine(_testDirectory, "success_worker.sh");
        await File.WriteAllTextAsync(scriptPath, $$"""
            #!/bin/sh
            read line
            read line
            echo '{"type":"page","jobId":"job-ok","pageNumber":1,"filePath":"{{tempImageFile}}","width":200,"height":300,"dpi":300,"pixelFormat":"color","source":"adf"}'
            echo '{"type":"batchComplete","jobId":"job-ok"}'
            exit 0
            """);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var backend = new TwainScannerBackend(customWorkerExecutablePath: scriptPath);

        var config = new EffectiveConfiguration
        {
            Effective = new Dictionary<string, object?>
            {
                ["source"] = "adf",
                ["resolution"] = 300,
                ["pixel_format"] = "color"
            }
        };

        await backend.ConfigureAsync("scanner-1", "job-ok", config);

        var pages = new List<AcquiredPage>();
        await foreach (var page in backend.AcquireAsync("scanner-1", "job-ok"))
        {
            pages.Add(page);
            // Vérifier que les données sont lisibles
            Assert.NotNull(page.Data);
            Assert.True(page.Data.CanRead);
            // Fermeture du flux simulant la fin de traitement par JobManager
            await page.Data.DisposeAsync();
        }

        Assert.Single(pages);
        Assert.Equal(1, pages[0].PageNumber);
        Assert.Equal(300, pages[0].Dpi);
        Assert.Equal("color", pages[0].PixelFormat);

        // Grâce à FileOptions.DeleteOnClose, le fichier temporaire doit avoir disparu
        Assert.False(File.Exists(tempImageFile));
    }

    private sealed class CapturingTwainLogger : ITwainLogger
    {
        public List<string> Infos { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];

        public void LogInfo(string message) => Infos.Add(message);
        public void LogWarning(string message) => Warnings.Add(message);
        public void LogError(string message, Exception? ex = null) => Errors.Add(message);
    }
}

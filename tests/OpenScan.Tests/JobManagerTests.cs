using System.Collections.Concurrent;
using OpenScan.Backends.Mock;
using OpenScan.Contracts;
using OpenScan.Core;
using OpenScan.Core.Abstractions;
using OpenScan.Persistence;

namespace OpenScan.Tests;

/// <summary>Flux de bout en bout via le backend Mock : Device Manager -> Job Manager -> stockage fichier.</summary>
public class JobManagerTests : IDisposable
{
    private static readonly HashSet<JobStatus> TerminalStatuses = [JobStatus.Completed, JobStatus.Cancelled, JobStatus.Error];

    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "openscan-tests-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
        {
            Directory.Delete(_storageDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartScanAsync_AcquiresAndPersistsAllPages()
    {
        var (jobs, deviceManager, jobRepo, pageRepo, events) = CreateSut();
        var scannerId = deviceManager.GetKnownScanners()[0].Id;
        var profile = new ScanProfile { Id = "p1", Source = "adf", Resolution = 300, PixelFormat = "color" };

        var job = await jobs.StartScanAsync(scannerId, profile);
        var finalJob = await WaitForTerminalStatusAsync(jobRepo, job.Id);

        Assert.Equal(JobStatus.Completed, finalJob.Status);
        Assert.Equal(3, finalJob.ProcessedPages);
        Assert.Equal(0, finalJob.FailedPages);

        var pages = await pageRepo.ListByJobAsync(job.Id);
        Assert.Equal(3, pages.Count);
        Assert.All(pages, p => Assert.Equal(PageStatus.Persisted, p.Status));
        Assert.All(pages, p => Assert.False(string.IsNullOrEmpty(p.Checksum)));

        Assert.Contains(events, e => e.Event == EventNames.ScanCompleted && e.JobId == job.Id);
        Assert.Equal(3, events.Count(e => e.Event == EventNames.PagePersisted));
    }

    [Fact]
    public async Task CancelJobAsync_StopsAcquisitionBeforeAllPagesComplete()
    {
        var (jobs, deviceManager, jobRepo, pageRepo, events) = CreateSut();
        var scannerId = deviceManager.GetKnownScanners()[0].Id;
        var profile = new ScanProfile { Id = "p1", Source = "adf", Resolution = 300, PixelFormat = "color" };

        var job = await jobs.StartScanAsync(scannerId, profile);
        await jobs.CancelJobAsync(job.Id);

        var finalJob = await WaitForTerminalStatusAsync(jobRepo, job.Id);

        Assert.Equal(JobStatus.Cancelled, finalJob.Status);
        Assert.True(finalJob.ProcessedPages < 3);
        Assert.Contains(events, e => e.Event == EventNames.ScanCancelled && e.JobId == job.Id);
    }

    [Fact]
    public async Task StartScanAsync_ScannerAlreadyReserved_ThrowsScannerBusy()
    {
        var (jobs, deviceManager, _, _, _) = CreateSut();
        var scannerId = deviceManager.GetKnownScanners()[0].Id;
        var profile = new ScanProfile { Id = "p1", Source = "adf", Resolution = 300, PixelFormat = "color" };

        await jobs.StartScanAsync(scannerId, profile); // occupe le scanner (job en cours d'exécution)

        var ex = await Assert.ThrowsAsync<ScanEngineException>(() => jobs.StartScanAsync(scannerId, profile));
        Assert.Equal(ErrorCode.ScannerBusy, ex.Code);
    }

    private (JobManager Jobs, DeviceManager Devices, InMemoryJobRepository JobRepo, InMemoryPageRepository PageRepo, List<AgentEvent> Events) CreateSut()
    {
        var deviceManager = new DeviceManager([new MockScannerBackend()]);
        deviceManager.DiscoverAllAsync().GetAwaiter().GetResult();

        var eventBus = new InMemoryEventBus();
        var events = new List<AgentEvent>();
        eventBus.Subscribe(evt =>
        {
            lock (events) { events.Add(evt); }
            return Task.CompletedTask;
        });

        var jobRepo = new InMemoryJobRepository();
        var pageRepo = new InMemoryPageRepository();
        var storage = new FileSystemPageStorage(_storageDirectory);
        var jobs = new JobManager(deviceManager, eventBus, jobRepo, pageRepo, storage);

        return (jobs, deviceManager, jobRepo, pageRepo, events);
    }

    private static async Task<ScanJob> WaitForTerminalStatusAsync(IJobRepository repo, string jobId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var job = await repo.GetAsync(jobId);
            if (job is not null && TerminalStatuses.Contains(job.Status))
            {
                return job;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Job '{jobId}' n'a pas atteint un état terminal à temps.");
    }

    private sealed class InMemoryJobRepository : IJobRepository
    {
        private readonly ConcurrentDictionary<string, ScanJob> _jobs = new();

        public Task SaveAsync(ScanJob job, CancellationToken ct = default)
        {
            _jobs[job.Id] = Clone(job);
            return Task.CompletedTask;
        }

        public Task<ScanJob?> GetAsync(string jobId, CancellationToken ct = default) =>
            Task.FromResult(_jobs.TryGetValue(jobId, out var job) ? Clone(job) : null);

        public Task<IReadOnlyList<ScanJob>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScanJob>>(_jobs.Values.Select(Clone).ToList());

        private static ScanJob Clone(ScanJob job) => new()
        {
            Id = job.Id,
            ScannerId = job.ScannerId,
            ProfileId = job.ProfileId,
            RequestedConfiguration = job.RequestedConfiguration,
            EffectiveConfiguration = job.EffectiveConfiguration,
            Status = job.Status,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            TotalPages = job.TotalPages,
            ProcessedPages = job.ProcessedPages,
            FailedPages = job.FailedPages,
            Error = job.Error
        };
    }

    private sealed class InMemoryPageRepository : IPageRepository
    {
        private readonly ConcurrentDictionary<string, ScanPage> _pages = new();

        public Task SaveAsync(ScanPage page, CancellationToken ct = default)
        {
            _pages[page.Id] = page;
            return Task.CompletedTask;
        }

        public Task<ScanPage?> GetAsync(string pageId, CancellationToken ct = default) =>
            Task.FromResult(_pages.TryGetValue(pageId, out var page) ? page : null);

        public Task<IReadOnlyList<ScanPage>> ListByJobAsync(string jobId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScanPage>>(
                _pages.Values.Where(p => p.JobId == jobId).OrderBy(p => p.PageNumber).ToList());
    }
}

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
    private static readonly HashSet<JobStatus> TerminalStatuses = [JobStatus.Completed, JobStatus.Cancelled, JobStatus.Error, JobStatus.RecoverableError];

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
        var (jobs, deviceManager, jobRepo, pageRepo, events, _) = CreateSut();
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
        var (jobs, deviceManager, jobRepo, pageRepo, events, _) = CreateSut();
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
        var (jobs, deviceManager, _, _, _, _) = CreateSut();
        var scannerId = deviceManager.GetKnownScanners()[0].Id;
        var profile = new ScanProfile { Id = "p1", Source = "adf", Resolution = 300, PixelFormat = "color" };

        await jobs.StartScanAsync(scannerId, profile); // occupe le scanner (job en cours d'exécution)

        var ex = await Assert.ThrowsAsync<ScanEngineException>(() => jobs.StartScanAsync(scannerId, profile));
        Assert.Equal(ErrorCode.ScannerBusy, ex.Code);
    }

    [Fact]
    public async Task ResumeJobAsync_AfterCancelled_ResumesAndContinuesPageNumbering()
    {
        var (jobs, deviceManager, jobRepo, pageRepo, events, mock) = CreateSut();
        var scannerId = deviceManager.GetKnownScanners()[0].Id;
        var profile = new ScanProfile { Id = "p1", Source = "adf", Resolution = 300, PixelFormat = "color" };

        mock.SetDefaultBehavior(pageCount: 5);
        var job = await jobs.StartScanAsync(scannerId, profile);
        await jobs.CancelJobAsync(job.Id);

        var cancelledJob = await WaitForTerminalStatusAsync(jobRepo, job.Id);
        Assert.Equal(JobStatus.Cancelled, cancelledJob.Status);
        var initialProcessed = cancelledJob.ProcessedPages;
        Assert.True(initialProcessed < 5);

        // Configurer 2 pages pour la reprise
        mock.SetDefaultBehavior(pageCount: 2);
        var resumedJob = await jobs.ResumeJobAsync(job.Id);
        Assert.Equal(JobStatus.Running, resumedJob.Status);

        var completedJob = await WaitForTerminalStatusAsync(jobRepo, job.Id);
        Assert.Equal(JobStatus.Completed, completedJob.Status);
        Assert.Equal(initialProcessed + 2, completedJob.ProcessedPages);
        Assert.Equal(initialProcessed + 2, completedJob.TotalPages);

        var pages = await pageRepo.ListByJobAsync(job.Id);
        Assert.Equal(initialProcessed + 2, pages.Count);
        // Vérifier que tous les numéros de page sont consécutifs sans collision
        for (var i = 0; i < pages.Count; i++)
        {
            Assert.Equal(i + 1, pages[i].PageNumber);
        }
    }

    [Fact]
    public async Task RetryJobAsync_AfterRecoverableError_RetriesAndPreservesHistory()
    {
        var (jobs, deviceManager, jobRepo, pageRepo, events, mock) = CreateSut();
        var scannerId = deviceManager.GetKnownScanners()[0].Id;
        var profile = new ScanProfile { Id = "p1", Source = "adf", Resolution = 300, PixelFormat = "color" };

        // Échoue à la page 2 avec un bourrage papier (erreur récupérable)
        mock.SetDefaultBehavior(
            pageCount: 3,
            failAtPage: 2,
            exception: new ScanEngineException(ErrorCode.PaperJam, "Bourrage papier simulé", recoverable: true));

        var job = await jobs.StartScanAsync(scannerId, profile);
        var failedJob = await WaitForTerminalStatusAsync(jobRepo, job.Id);

        Assert.Equal(JobStatus.RecoverableError, failedJob.Status);
        Assert.Equal(1, failedJob.ProcessedPages);
        Assert.NotNull(failedJob.Error);
        Assert.Equal(ErrorCode.PaperJam, failedJob.Error!.Code);

        // L'utilisateur résout le bourrage et relance le job avec 2 pages restantes
        mock.SetDefaultBehavior(pageCount: 2);
        var retriedJob = await jobs.RetryJobAsync(job.Id);
        Assert.Equal(JobStatus.Running, retriedJob.Status);

        var completedJob = await WaitForTerminalStatusAsync(jobRepo, job.Id);
        Assert.Equal(JobStatus.Completed, completedJob.Status);
        Assert.Equal(3, completedJob.ProcessedPages);
        Assert.Equal(3, completedJob.TotalPages);

        var pages = await pageRepo.ListByJobAsync(job.Id);
        Assert.Equal(3, pages.Count);
        Assert.Equal([1, 2, 3], pages.Select(p => p.PageNumber).ToList());
    }

    [Fact]
    public async Task ResumeJobAsync_WithExplicitFromPage_SetsStartingPageNumber()
    {
        var (jobs, deviceManager, jobRepo, pageRepo, events, mock) = CreateSut();
        var scannerId = deviceManager.GetKnownScanners()[0].Id;
        var profile = new ScanProfile { Id = "p1", Source = "adf", Resolution = 300, PixelFormat = "color" };

        mock.SetDefaultBehavior(pageCount: 5);
        var job = await jobs.StartScanAsync(scannerId, profile);
        await jobs.CancelJobAsync(job.Id);
        await WaitForTerminalStatusAsync(jobRepo, job.Id);

        // Reprise explicite avec fromPage = 10
        mock.SetDefaultBehavior(pageCount: 2);
        await jobs.ResumeJobAsync(job.Id, fromPage: 10);

        var completedJob = await WaitForTerminalStatusAsync(jobRepo, job.Id);
        Assert.Equal(JobStatus.Completed, completedJob.Status);

        var pages = await pageRepo.ListByJobAsync(job.Id);
        // La première page reprise doit avoir le numéro 10, la suivante 11
        var resumedPages = pages.Where(p => p.PageNumber >= 10).OrderBy(p => p.PageNumber).ToList();
        Assert.Equal(2, resumedPages.Count);
        Assert.Equal(10, resumedPages[0].PageNumber);
        Assert.Equal(11, resumedPages[1].PageNumber);
        Assert.Equal(11, completedJob.TotalPages);
    }

    [Fact]
    public async Task ResumeJobAsync_WithInvalidFromPage_ThrowsInvalidConfiguration()
    {
        var (jobs, deviceManager, jobRepo, _, _, mock) = CreateSut();
        var scannerId = deviceManager.GetKnownScanners()[0].Id;
        var profile = new ScanProfile { Id = "p1", Source = "adf", Resolution = 300, PixelFormat = "color" };

        var job = await jobs.StartScanAsync(scannerId, profile);
        await jobs.CancelJobAsync(job.Id);
        await WaitForTerminalStatusAsync(jobRepo, job.Id);

        var ex = await Assert.ThrowsAsync<ScanEngineException>(() => jobs.ResumeJobAsync(job.Id, fromPage: 0));
        Assert.Equal(ErrorCode.InvalidConfiguration, ex.Code);
    }

    [Fact]
    public async Task ResumeJobAsync_OnCompletedJob_ThrowsInvalidJobState()
    {
        var (jobs, deviceManager, jobRepo, _, _, _) = CreateSut();
        var scannerId = deviceManager.GetKnownScanners()[0].Id;
        var profile = new ScanProfile { Id = "p1", Source = "adf", Resolution = 300, PixelFormat = "color" };

        var job = await jobs.StartScanAsync(scannerId, profile);
        await WaitForTerminalStatusAsync(jobRepo, job.Id);

        var ex = await Assert.ThrowsAsync<ScanEngineException>(() => jobs.ResumeJobAsync(job.Id));
        Assert.Equal(ErrorCode.InvalidJobState, ex.Code);
    }

    [Fact]
    public async Task RetryJobAsync_OnCompletedJob_ThrowsInvalidJobState()
    {
        var (jobs, deviceManager, jobRepo, _, _, _) = CreateSut();
        var scannerId = deviceManager.GetKnownScanners()[0].Id;
        var profile = new ScanProfile { Id = "p1", Source = "adf", Resolution = 300, PixelFormat = "color" };

        var job = await jobs.StartScanAsync(scannerId, profile);
        await WaitForTerminalStatusAsync(jobRepo, job.Id);

        var ex = await Assert.ThrowsAsync<ScanEngineException>(() => jobs.RetryJobAsync(job.Id));
        Assert.Equal(ErrorCode.InvalidJobState, ex.Code);
    }

    [Fact]
    public async Task ResumeJobAsync_OnErrorJob_ThrowsInvalidJobState()
    {
        var (jobs, deviceManager, jobRepo, _, _, mock) = CreateSut();
        var scannerId = deviceManager.GetKnownScanners()[0].Id;
        var profile = new ScanProfile { Id = "p1", Source = "adf", Resolution = 300, PixelFormat = "color" };

        // Erreur non récupérable -> statut Error (et non RecoverableError)
        mock.SetDefaultBehavior(
            pageCount: 2,
            failAtPage: 1,
            exception: new ScanEngineException(ErrorCode.DriverError, "Crash pilote non récupérable", recoverable: false));

        var job = await jobs.StartScanAsync(scannerId, profile);
        var failedJob = await WaitForTerminalStatusAsync(jobRepo, job.Id);
        Assert.Equal(JobStatus.Error, failedJob.Status);

        // Resume sur un statut Error doit être refusé (seul Retry est éligible)
        var ex = await Assert.ThrowsAsync<ScanEngineException>(() => jobs.ResumeJobAsync(job.Id));
        Assert.Equal(ErrorCode.InvalidJobState, ex.Code);
    }

    [Fact]
    public async Task RetryJobAsync_OnCancelledJob_ThrowsInvalidJobState()
    {
        var (jobs, deviceManager, jobRepo, _, _, _) = CreateSut();
        var scannerId = deviceManager.GetKnownScanners()[0].Id;
        var profile = new ScanProfile { Id = "p1", Source = "adf", Resolution = 300, PixelFormat = "color" };

        var job = await jobs.StartScanAsync(scannerId, profile);
        await jobs.CancelJobAsync(job.Id);
        var cancelledJob = await WaitForTerminalStatusAsync(jobRepo, job.Id);
        Assert.Equal(JobStatus.Cancelled, cancelledJob.Status);

        // Retry sur un job Cancelled doit être refusé (seul Resume est éligible)
        var ex = await Assert.ThrowsAsync<ScanEngineException>(() => jobs.RetryJobAsync(job.Id));
        Assert.Equal(ErrorCode.InvalidJobState, ex.Code);
    }

    [Fact]
    public async Task ReconcileOrphanedJobsAsync_RunningJob_ReclassifiesToRecoverableError()
    {
        var (jobs, _, jobRepo, _, events, _) = CreateSut();
        var orphanJob = new ScanJob
        {
            Id = "JOB-ORPHAN-001",
            ScannerId = "mock-scanner-1",
            Status = JobStatus.Running,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            TotalPages = 2,
            ProcessedPages = 2
        };
        await jobRepo.SaveAsync(orphanJob);

        await jobs.ReconcileOrphanedJobsAsync();

        var reconciled = await jobRepo.GetAsync(orphanJob.Id);
        Assert.NotNull(reconciled);
        Assert.Equal(JobStatus.RecoverableError, reconciled!.Status);
        Assert.NotNull(reconciled.Error);
        Assert.Equal(ErrorCode.AcquisitionError, reconciled.Error!.Code);
        Assert.Contains("Agent redémarré", reconciled.Error.Message);
        Assert.True(reconciled.Error.Recoverable);

        Assert.Contains(events, e => e.Event == EventNames.ScanError && e.JobId == orphanJob.Id);
    }

    private (JobManager Jobs, DeviceManager Devices, InMemoryJobRepository JobRepo, InMemoryPageRepository PageRepo, List<AgentEvent> Events, MockScannerBackend Mock) CreateSut(MockScannerBackend? mockBackend = null)
    {
        var mock = mockBackend ?? new MockScannerBackend();
        var deviceManager = new DeviceManager([mock]);
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

        return (jobs, deviceManager, jobRepo, pageRepo, events, mock);
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

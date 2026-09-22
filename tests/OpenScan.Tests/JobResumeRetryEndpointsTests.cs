using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenScan.Agent;
using OpenScan.Backends.Mock;
using OpenScan.Contracts;
using OpenScan.Core;
using OpenScan.Core.Abstractions;
using OpenScan.Persistence;

namespace OpenScan.Tests;

public class JobResumeRetryEndpointsTests : IDisposable
{
    private readonly string _storageDir;

    public JobResumeRetryEndpointsTests()
    {
        _storageDir = Path.Combine(Path.GetTempPath(), $"openscan-resume-tests-{Guid.NewGuid():n}");
        Directory.CreateDirectory(_storageDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDir))
        {
            try { Directory.Delete(_storageDir, recursive: true); } catch { }
        }
    }

    private (JobManager Jobs, InMemoryJobRepo JobRepo, MockScannerBackend Mock) CreateTestContext()
    {
        var mock = new MockScannerBackend();
        var deviceManager = new DeviceManager([mock]);
        deviceManager.DiscoverAllAsync().GetAwaiter().GetResult();

        var jobRepo = new InMemoryJobRepo();
        var pageRepo = new InMemoryPageRepo();
        var eventBus = new InMemoryEventBus();
        var storage = new FileSystemPageStorage(_storageDir);
        var jobs = new JobManager(deviceManager, eventBus, jobRepo, pageRepo, storage);

        return (jobs, jobRepo, mock);
    }

    [Fact]
    public async Task ResumeScanAsync_WithCancelledJob_ReturnsAccepted202()
    {
        var (jobs, jobRepo, _) = CreateTestContext();
        var jobId = "JOB-TEST-001";
        await jobRepo.SaveAsync(new ScanJob
        {
            Id = jobId,
            ScannerId = "mock-scanner-1",
            Status = JobStatus.Cancelled,
            EffectiveConfiguration = new EffectiveConfiguration(),
            RequestedConfiguration = new ScanProfile { Source = "adf", Resolution = 300, PixelFormat = "color" }
        });

        var result = await ScanEndpoints.ResumeScanAsync(jobId, fromPage: null, jobs);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var statusResult = (IStatusCodeHttpResult)result;
        Assert.Equal(StatusCodes.Status202Accepted, statusResult.StatusCode);
    }

    [Fact]
    public async Task ResumeScanAsync_WithJobNotFound_ReturnsNotFound404()
    {
        var (jobs, _, _) = CreateTestContext();

        var result = await ScanEndpoints.ResumeScanAsync("JOB-MISSING", fromPage: null, jobs);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var statusResult = (IStatusCodeHttpResult)result;
        Assert.Equal(StatusCodes.Status404NotFound, statusResult.StatusCode);
    }

    [Fact]
    public async Task ResumeScanAsync_WithCompletedJob_ReturnsConflict409()
    {
        var (jobs, jobRepo, _) = CreateTestContext();
        var jobId = "JOB-TEST-002";
        await jobRepo.SaveAsync(new ScanJob
        {
            Id = jobId,
            ScannerId = "mock-scanner-1",
            Status = JobStatus.Completed
        });

        var result = await ScanEndpoints.ResumeScanAsync(jobId, fromPage: null, jobs);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var statusResult = (IStatusCodeHttpResult)result;
        Assert.Equal(StatusCodes.Status409Conflict, statusResult.StatusCode);
    }

    [Fact]
    public async Task ResumeScanAsync_WithInvalidFromPage_ReturnsUnprocessableEntity422()
    {
        var (jobs, jobRepo, _) = CreateTestContext();
        var jobId = "JOB-TEST-003";
        await jobRepo.SaveAsync(new ScanJob
        {
            Id = jobId,
            ScannerId = "mock-scanner-1",
            Status = JobStatus.Cancelled
        });

        var result = await ScanEndpoints.ResumeScanAsync(jobId, fromPage: 0, jobs);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var statusResult = (IStatusCodeHttpResult)result;
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, statusResult.StatusCode);
    }

    [Fact]
    public async Task RetryScanAsync_WithErrorJob_ReturnsAccepted202()
    {
        var (jobs, jobRepo, _) = CreateTestContext();
        var jobId = "JOB-TEST-004";
        await jobRepo.SaveAsync(new ScanJob
        {
            Id = jobId,
            ScannerId = "mock-scanner-1",
            Status = JobStatus.Error,
            EffectiveConfiguration = new EffectiveConfiguration(),
            RequestedConfiguration = new ScanProfile { Source = "adf", Resolution = 300, PixelFormat = "color" }
        });

        var result = await ScanEndpoints.RetryScanAsync(jobId, jobs);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var statusResult = (IStatusCodeHttpResult)result;
        Assert.Equal(StatusCodes.Status202Accepted, statusResult.StatusCode);
    }

    [Fact]
    public async Task RetryScanAsync_WithJobNotFound_ReturnsNotFound404()
    {
        var (jobs, _, _) = CreateTestContext();

        var result = await ScanEndpoints.RetryScanAsync("JOB-MISSING", jobs);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var statusResult = (IStatusCodeHttpResult)result;
        Assert.Equal(StatusCodes.Status404NotFound, statusResult.StatusCode);
    }

    [Fact]
    public async Task RetryScanAsync_WithCancelledJob_ReturnsConflict409()
    {
        var (jobs, jobRepo, _) = CreateTestContext();
        var jobId = "JOB-TEST-005";
        await jobRepo.SaveAsync(new ScanJob
        {
            Id = jobId,
            ScannerId = "mock-scanner-1",
            Status = JobStatus.Cancelled
        });

        var result = await ScanEndpoints.RetryScanAsync(jobId, jobs);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var statusResult = (IStatusCodeHttpResult)result;
        Assert.Equal(StatusCodes.Status409Conflict, statusResult.StatusCode);
    }

    private sealed class InMemoryJobRepo : IJobRepository
    {
        private readonly ConcurrentDictionary<string, ScanJob> _jobs = new();
        public Task SaveAsync(ScanJob job, CancellationToken ct = default) { _jobs[job.Id] = job; return Task.CompletedTask; }
        public Task<ScanJob?> GetAsync(string jobId, CancellationToken ct = default) => Task.FromResult(_jobs.GetValueOrDefault(jobId));
        public Task<IReadOnlyList<ScanJob>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScanJob>>([.. _jobs.Values]);
    }

    private sealed class InMemoryPageRepo : IPageRepository
    {
        private readonly ConcurrentDictionary<string, ScanPage> _pages = new();
        public Task SaveAsync(ScanPage page, CancellationToken ct = default) { _pages[page.Id] = page; return Task.CompletedTask; }
        public Task<ScanPage?> GetAsync(string pageId, CancellationToken ct = default) => Task.FromResult(_pages.GetValueOrDefault(pageId));
        public Task<IReadOnlyList<ScanPage>> ListByJobAsync(string jobId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScanPage>>([.. _pages.Values.Where(p => p.JobId == jobId).OrderBy(p => p.PageNumber)]);
    }
}

using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenScan.Contracts;
using OpenScan.Core.Abstractions;

namespace OpenScan.Core;


public sealed class JobManager
{
    private const int PageQueueCapacity = 8;

    private readonly DeviceManager _deviceManager;
    private readonly IEventBus _eventBus;
    private readonly IJobRepository _jobRepository;
    private readonly IPageRepository _pageRepository;
    private readonly IPageStorage _pageStorage;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningJobs = new();

    public JobManager(
        DeviceManager deviceManager,
        IEventBus eventBus,
        IJobRepository jobRepository,
        IPageRepository pageRepository,
        IPageStorage pageStorage)
    {
        _deviceManager = deviceManager;
        _eventBus = eventBus;
        _jobRepository = jobRepository;
        _pageRepository = pageRepository;
        _pageStorage = pageStorage;
    }

    public async Task<ScanJob> StartScanAsync(string scannerId, ScanProfile profile, CancellationToken ct = default)
    {
        var scanner = _deviceManager.GetScanner(scannerId);
        var backend = _deviceManager.GetBackendFor(scanner);

        var job = new ScanJob
        {
            Id = NewJobId(),
            ScannerId = scannerId,
            ProfileId = profile.Id,
            RequestedConfiguration = profile,
            Status = JobStatus.Created
        };
        await _jobRepository.SaveAsync(job, ct);
        await PublishJobEvent(EventNames.ScanCreated, job, ct);

        job.Status = JobStatus.Validating;
        await PublishJobEvent(EventNames.ScanValidating, job, ct);

        await backend.OpenAsync(scannerId, ct);

        try
        {
            var capabilities = await backend.GetCapabilitiesAsync(scannerId, ct);
            job.EffectiveConfiguration = ConfigurationEngine.Validate(profile, capabilities);
        }
        catch (ScanEngineException ex)
        {
            job.Status = JobStatus.Error;
            job.Error = ex.ToScanError();
            await _jobRepository.SaveAsync(job, ct);
            await PublishJobEvent(EventNames.ScanError, job, ct);
            throw;
        }

        job.Status = JobStatus.Ready;
        await _jobRepository.SaveAsync(job, ct);

        _deviceManager.Reserve(scannerId, job.Id);

        try
        {
            await backend.ConfigureAsync(scannerId, job.Id, job.EffectiveConfiguration!, ct);
        }
        catch (ScanEngineException ex)
        {
            _deviceManager.Release(scannerId);
            job.Status = JobStatus.Error;
            job.Error = ex.ToScanError();
            await _jobRepository.SaveAsync(job, ct);
            await PublishJobEvent(EventNames.ScanError, job, ct);
            throw;
        }

        var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runningJobs[job.Id] = jobCts;

        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        await _jobRepository.SaveAsync(job, ct);
        await PublishJobEvent(EventNames.ScanStarted, job, ct);

        _ = RunAcquisitionAsync(job, backend, jobCts.Token);

        return job;
    }

    public async Task CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        var job = await _jobRepository.GetAsync(jobId, ct)
            ?? throw new ScanEngineException(ErrorCode.JobNotFound, $"Job '{jobId}' introuvable.");

        if (_runningJobs.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
        }

        var scanner = _deviceManager.GetScanner(job.ScannerId);
        var backend = _deviceManager.GetBackendFor(scanner);
        await backend.CancelAsync(job.ScannerId, jobId, CancellationToken.None);
    }

    private async Task RunAcquisitionAsync(ScanJob job, IScannerBackend backend, CancellationToken ct)
    {
        var channel = Channel.CreateBounded<AcquiredPage>(new BoundedChannelOptions(PageQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        var producer = ProduceAsync(job, backend, channel.Writer, ct);
        var consumer = ConsumeAsync(job, channel.Reader, ct);

        try
        {
            await Task.WhenAll(producer, consumer);

            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await _jobRepository.SaveAsync(job, CancellationToken.None);
            await PublishJobEvent(EventNames.ScanCompleted, job, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await _jobRepository.SaveAsync(job, CancellationToken.None);
            await PublishJobEvent(EventNames.ScanCancelled, job, CancellationToken.None);
        }
        catch (ScanEngineException ex)
        {
            job.Status = JobStatus.Error;
            job.Error = ex.ToScanError();
            job.CompletedAt = DateTimeOffset.UtcNow;
            await _jobRepository.SaveAsync(job, CancellationToken.None);
            await PublishJobEvent(EventNames.ScanError, job, CancellationToken.None);
        }
        finally
        {
            _deviceManager.Release(job.ScannerId);
            _runningJobs.TryRemove(job.Id, out _);
            await backend.CloseAsync(job.ScannerId, CancellationToken.None);
        }
    }

    private static async Task ProduceAsync(ScanJob job, IScannerBackend backend, ChannelWriter<AcquiredPage> writer, CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            await backend.StartAsync(job.ScannerId, job.Id, ct);
            await foreach (var page in backend.AcquireAsync(job.ScannerId, job.Id, ct))
            {
                await writer.WriteAsync(page, ct);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            writer.Complete(failure);
        }
    }

    private async Task ConsumeAsync(ScanJob job, ChannelReader<AcquiredPage> reader, CancellationToken ct)
    {
        await foreach (var acquired in reader.ReadAllAsync(ct))
        {
            var page = new ScanPage
            {
                Id = $"{job.Id}-{acquired.PageNumber:D4}",
                JobId = job.Id,
                PageNumber = acquired.PageNumber,
                Width = acquired.Width,
                Height = acquired.Height,
                Dpi = acquired.Dpi,
                PixelFormat = acquired.PixelFormat,
                Source = acquired.Source,
                Status = PageStatus.Acquired
            };

            await PublishPageEvent(EventNames.PageAcquired, job, page, ct);

            try
            {
                var (reference, checksum) = await _pageStorage.SaveAsync(job.Id, page.Id, acquired.Data, ct);
                page.FileReference = reference;
                page.Checksum = checksum;
                page.Status = PageStatus.Persisted;
                await _pageRepository.SaveAsync(page, ct);

                job.ProcessedPages++;
                await PublishPageEvent(EventNames.PagePersisted, job, page, ct);
            }
            catch (Exception ex)
            {
                page.Status = PageStatus.Error;
                page.Error = new ScanError
                {
                    Code = ErrorCode.StorageError,
                    Message = ex.Message,
                    JobId = job.Id,
                    PageId = page.Id,
                    Recoverable = true
                };
                await _pageRepository.SaveAsync(page, ct);
                job.FailedPages++;
                await PublishPageEvent(EventNames.PageError, job, page, ct);
            }
            finally
            {
                await acquired.Data.DisposeAsync();
            }

            job.TotalPages = Math.Max(job.TotalPages, acquired.PageNumber);
            await _jobRepository.SaveAsync(job, ct);
        }
    }

    private Task PublishJobEvent(string eventName, ScanJob job, CancellationToken ct) =>
        _eventBus.PublishAsync(new AgentEvent { Event = eventName, ScannerId = job.ScannerId, JobId = job.Id, Error = job.Error }, ct);

    private Task PublishPageEvent(string eventName, ScanJob job, ScanPage page, CancellationToken ct) =>
        _eventBus.PublishAsync(new AgentEvent
        {
            Event = eventName,
            ScannerId = job.ScannerId,
            JobId = job.Id,
            PageNumber = page.PageNumber,
            PageId = page.Id,
            Error = page.Error
        }, ct);

    private static string NewJobId() =>
        $"JOB-{DateTimeOffset.UtcNow:yyyy}-{Random.Shared.Next(100_000, 999_999)}";
}

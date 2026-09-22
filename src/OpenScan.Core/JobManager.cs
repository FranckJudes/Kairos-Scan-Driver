using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenScan.Contracts;
using OpenScan.Core.Abstractions;
using OpenScan.Pipeline;

namespace OpenScan.Core;


public sealed class JobManager
{
    private const int PageQueueCapacity = 8;

    private readonly DeviceManager _deviceManager;
    private readonly IEventBus _eventBus;
    private readonly IJobRepository _jobRepository;
    private readonly IPageRepository _pageRepository;
    private readonly IPageStorage _pageStorage;
    private readonly IImageProcessor _imageProcessor;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningJobs = new();

    public JobManager(
        DeviceManager deviceManager,
        IEventBus eventBus,
        IJobRepository jobRepository,
        IPageRepository pageRepository,
        IPageStorage pageStorage,
        IImageProcessor? imageProcessor = null)
    {
        _deviceManager = deviceManager;
        _eventBus = eventBus;
        _jobRepository = jobRepository;
        _pageRepository = pageRepository;
        _pageStorage = pageStorage;
        _imageProcessor = imageProcessor ?? new ImageSharpProcessor();
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

        await LaunchAcquisitionAsync(job, backend, pageNumberOffset: 0, ct);

        return job;
    }

    /// <summary>
    /// Reprend un job de numérisation annulé (<see cref="JobStatus.Cancelled"/>) ou en erreur récupérable (<see cref="JobStatus.RecoverableError"/>).
    /// </summary>
    /// <param name="jobId">Identifiant unique du job à reprendre.</param>
    /// <param name="fromPage">
    /// Numéro de page logique (1-indexé) à partir duquel numéroter les nouvelles pages acquises (<c>resume_from_page</c>).
    /// <para>
    /// <b>Limite physique du matériel (ADF) :</b> Aucun pilote ni protocole de scanner (SANE, WIA, TWAIN) ne permet
    /// de "rembobiner" ou faire "sauter" le chargeur automatique de documents (ADF) jusqu'à une page physique précise.
    /// Ce paramètre sert d'indice logique et de traçabilité pour décaler la numérotation des pages dans le système,
    /// tandis que le scanner traite les feuilles physiques restantes réinsérées par l'utilisateur.
    /// Si ce paramètre est omis (<c>null</c>), la numérotation reprend automatiquement à la suite (<c>job.TotalPages + 1</c>).
    /// </para>
    /// </param>
    /// <param name="ct">Jeton d'annulation.</param>
    /// <returns>Le <see cref="ScanJob"/> remis en exécution.</returns>
    /// <exception cref="ScanEngineException">
    /// Levée avec <see cref="ErrorCode.JobNotFound"/> si le job n'existe pas,
    /// avec <see cref="ErrorCode.InvalidJobState"/> si le job n'est pas dans un état éligible,
    /// ou avec <see cref="ErrorCode.ScannerBusy"/> si le scanner est actuellement réservé par un autre job.
    /// </exception>
    public async Task<ScanJob> ResumeJobAsync(string jobId, int? fromPage = null, CancellationToken ct = default)
    {
        var job = await _jobRepository.GetAsync(jobId, ct)
            ?? throw new ScanEngineException(ErrorCode.JobNotFound, $"Job '{jobId}' introuvable.");

        if (job.Status != JobStatus.Cancelled && job.Status != JobStatus.RecoverableError)
        {
            throw new ScanEngineException(
                ErrorCode.InvalidJobState,
                $"Le job '{jobId}' est dans l'état '{job.Status}' et ne peut pas être repris. Seuls les états Cancelled et RecoverableError sont éligibles.");
        }

        if (fromPage.HasValue && fromPage.Value < 1)
        {
            throw new ScanEngineException(ErrorCode.InvalidConfiguration, "Le paramètre 'fromPage' doit être supérieur ou égal à 1.");
        }

        var offset = fromPage.HasValue ? fromPage.Value - 1 : job.TotalPages;

        var scanner = _deviceManager.GetScanner(job.ScannerId);
        var backend = _deviceManager.GetBackendFor(scanner);

        await LaunchAcquisitionAsync(job, backend, offset, ct);

        return job;
    }

    /// <summary>
    /// Relance un job en erreur (<see cref="JobStatus.Error"/> ou <see cref="JobStatus.RecoverableError"/>).
    /// Les pages déjà acquises ou en erreur restent dans l'historique ; les nouvelles pages acquises s'ajoutent à la suite.
    /// </summary>
    /// <param name="jobId">Identifiant unique du job à relancer.</param>
    /// <param name="ct">Jeton d'annulation.</param>
    /// <returns>Le <see cref="ScanJob"/> remis en exécution.</returns>
    /// <exception cref="ScanEngineException">
    /// Levée avec <see cref="ErrorCode.JobNotFound"/> si le job n'existe pas,
    /// avec <see cref="ErrorCode.InvalidJobState"/> si le job n'est pas dans un état éligible,
    /// ou avec <see cref="ErrorCode.ScannerBusy"/> si le scanner est actuellement réservé par un autre job.
    /// </exception>
    public async Task<ScanJob> RetryJobAsync(string jobId, CancellationToken ct = default)
    {
        var job = await _jobRepository.GetAsync(jobId, ct)
            ?? throw new ScanEngineException(ErrorCode.JobNotFound, $"Job '{jobId}' introuvable.");

        if (job.Status != JobStatus.Error && job.Status != JobStatus.RecoverableError)
        {
            throw new ScanEngineException(
                ErrorCode.InvalidJobState,
                $"Le job '{jobId}' est dans l'état '{job.Status}' et ne peut pas être relancé. Seuls les états Error et RecoverableError sont éligibles.");
        }

        var offset = job.TotalPages;

        var scanner = _deviceManager.GetScanner(job.ScannerId);
        var backend = _deviceManager.GetBackendFor(scanner);

        await LaunchAcquisitionAsync(job, backend, offset, ct);

        return job;
    }

    /// <summary>
    /// Réconcilie les jobs orphelins restés à l'état Running après un arrêt brutal ou crash de l'Agent.
    /// Les requalifie en RecoverableError pour permettre à l'utilisateur de les reprendre ou relancer.
    /// </summary>
    public async Task ReconcileOrphanedJobsAsync(CancellationToken ct = default)
    {
        var jobs = await _jobRepository.ListAsync(ct);
        foreach (var job in jobs)
        {
            if (job.Status == JobStatus.Running)
            {
                job.Status = JobStatus.RecoverableError;
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.Error = new ScanError
                {
                    Code = ErrorCode.AcquisitionError,
                    Message = "Agent redémarré pendant l'acquisition.",
                    JobId = job.Id,
                    ScannerId = job.ScannerId,
                    Recoverable = true
                };

                await _jobRepository.SaveAsync(job, ct);
                await PublishJobEvent(EventNames.ScanError, job, ct);
            }
        }
    }

    private async Task LaunchAcquisitionAsync(
        ScanJob job,
        IScannerBackend backend,
        int pageNumberOffset,
        CancellationToken ct)
    {
        var effectiveConfig = job.EffectiveConfiguration;
        if (effectiveConfig is null)
        {
            if (job.RequestedConfiguration is not null)
            {
                var caps = await backend.GetCapabilitiesAsync(job.ScannerId, ct);
                effectiveConfig = ConfigurationEngine.Validate(job.RequestedConfiguration, caps);
                job.EffectiveConfiguration = effectiveConfig;
            }
            else
            {
                throw new ScanEngineException(ErrorCode.InvalidConfiguration, "Configuration du job introuvable.");
            }
        }

        _deviceManager.Reserve(job.ScannerId, job.Id);

        try
        {
            await backend.ConfigureAsync(job.ScannerId, job.Id, effectiveConfig, ct);
        }
        catch (ScanEngineException ex)
        {
            _deviceManager.Release(job.ScannerId);
            job.Status = JobStatus.Error;
            job.Error = ex.ToScanError();
            await _jobRepository.SaveAsync(job, ct);
            await PublishJobEvent(EventNames.ScanError, job, ct);
            throw;
        }

        var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runningJobs[job.Id] = jobCts;

        job.Status = JobStatus.Running;
        if (job.StartedAt is null)
        {
            job.StartedAt = DateTimeOffset.UtcNow;
        }
        job.CompletedAt = null;
        job.Error = null;
        await _jobRepository.SaveAsync(job, ct);
        await PublishJobEvent(EventNames.ScanStarted, job, ct);

        _ = RunAcquisitionAsync(job, backend, pageNumberOffset, jobCts.Token);
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

    private async Task RunAcquisitionAsync(ScanJob job, IScannerBackend backend, int pageNumberOffset, CancellationToken ct)
    {
        var channel = Channel.CreateBounded<AcquiredPage>(new BoundedChannelOptions(PageQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        var producer = ProduceAsync(job, backend, channel.Writer, ct);
        var consumer = ConsumeAsync(job, channel.Reader, pageNumberOffset, ct);

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
            job.Status = ex.Recoverable ? JobStatus.RecoverableError : JobStatus.Error;
            job.Error = ex.ToScanError();
            job.CompletedAt = DateTimeOffset.UtcNow;
            await _jobRepository.SaveAsync(job, CancellationToken.None);
            await PublishJobEvent(EventNames.ScanError, job, CancellationToken.None);
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Error;
            job.Error = new ScanError
            {
                Code = ErrorCode.AcquisitionError,
                Message = ex.Message,
                JobId = job.Id,
                Recoverable = true
            };
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

    private async Task ConsumeAsync(ScanJob job, ChannelReader<AcquiredPage> reader, int pageNumberOffset, CancellationToken ct)
    {
        await foreach (var acquired in reader.ReadAllAsync(ct))
        {
            var pageNumber = acquired.PageNumber + pageNumberOffset;
            var page = new ScanPage
            {
                Id = $"{job.Id}-{pageNumber:D4}",
                JobId = job.Id,
                PageNumber = pageNumber,
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
                var processed = await _imageProcessor.ProcessPageAsync(acquired, job.RequestedConfiguration, ct);
                page.Width = processed.Width;
                page.Height = processed.Height;
                page.PixelFormat = processed.PixelFormat;

                await using (processed.JpegStream)
                {
                    var (reference, checksum) = await _pageStorage.SaveAsync(job.Id, page.Id, processed.JpegStream, ct);
                    page.FileReference = reference;
                    page.Checksum = checksum;
                    page.Status = PageStatus.Persisted;
                    await _pageRepository.SaveAsync(page, ct);
                }

                job.ProcessedPages++;
                await PublishPageEvent(EventNames.PagePersisted, job, page, ct);
            }
            catch (Exception ex)
            {
                page.Status = PageStatus.Error;
                page.Error = new ScanError
                {
                    Code = ex is ScanEngineException see ? see.Code : ErrorCode.StorageError,
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

            job.TotalPages = Math.Max(job.TotalPages, pageNumber);
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

using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenScan.Contracts;

namespace OpenScan.Backends.Escl;

/// <summary>
/// Backend réseau eSCL / AirScan (section 3.1).
/// Communique en HTTP REST + XML avec les imprimantes et scanners réseau modernes (HP, Canon, Epson, Brother...).
/// </summary>
public sealed class EsclScannerBackend : IScannerBackend
{
    public const string Id = "escl";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EsclOptions _options;
    private readonly ILogger<EsclScannerBackend>? _logger;

    private readonly ConcurrentDictionary<string, EsclDeviceInfo> _devices = new();
    private readonly ConcurrentDictionary<string, EsclJobContext> _jobs = new();

    public string BackendId => Id;

    public EsclScannerBackend(
        IHttpClientFactory httpClientFactory,
        IOptions<EsclOptions>? options = null,
        ILogger<EsclScannerBackend>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options?.Value ?? new EsclOptions();
        _logger = logger;
    }

    public async Task<IReadOnlyList<Scanner>> DiscoverAsync(CancellationToken ct = default)
    {
        var client = CreateClient();

        foreach (var deviceEntry in _options.KnownDevices)
        {
            if (string.IsNullOrWhiteSpace(deviceEntry))
            {
                continue;
            }

            try
            {
                var baseUri = NormalizeBaseUri(deviceEntry);
                var capabilitiesUri = new Uri(baseUri, "/eSCL/ScannerCapabilities");

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                using var response = await client.GetAsync(capabilitiesUri, timeoutCts.Token);
                if (response.IsSuccessStatusCode)
                {
                    var xml = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                    var parsed = EsclCapabilitiesParser.Parse(xml);

                    var scannerId = $"escl:{baseUri.Authority}";
                    var scanner = new Scanner
                    {
                        Id = scannerId,
                        Name = !string.IsNullOrEmpty(parsed.MakeAndModel) ? parsed.MakeAndModel : $"eSCL Scanner ({baseUri.Authority})",
                        Manufacturer = parsed.Manufacturer,
                        Model = parsed.Model,
                        SerialNumber = parsed.SerialNumber,
                        Backend = Id,
                        ConnectionType = ConnectionType.Network,
                        Status = ScannerStatus.Idle
                    };

                    _devices[scannerId] = new EsclDeviceInfo(baseUri, scanner, parsed.Capabilities);
                }
            }
            catch (Exception ex) when (ct.IsCancellationRequested is false)
            {
                _logger?.LogDebug("Périphérique eSCL '{Device}' non joignable lors de la découverte: {Message}", deviceEntry, ex.Message);
            }
        }

        return _devices.Values.Select(d => d.Scanner).ToList();
    }

    public Task OpenAsync(string scannerId, CancellationToken ct = default)
    {
        _ = GetBaseUriForScanner(scannerId);
        return Task.CompletedTask;
    }

    public Task CloseAsync(string scannerId, CancellationToken ct = default) => Task.CompletedTask;

    public async Task<ScannerCapabilities> GetCapabilitiesAsync(string scannerId, CancellationToken ct = default)
    {
        if (_devices.TryGetValue(scannerId, out var device) && device.Capabilities is not null)
        {
            return device.Capabilities;
        }

        var baseUri = GetBaseUriForScanner(scannerId);
        var client = CreateClient();
        var capabilitiesUri = new Uri(baseUri, "/eSCL/ScannerCapabilities");

        try
        {
            using var response = await client.GetAsync(capabilitiesUri, ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                throw new ScanEngineException(
                    EsclErrorMapper.MapStatusCode(response.StatusCode),
                    $"Impossible de récupérer les capacités eSCL ({response.StatusCode}): {detail}",
                    recoverable: EsclErrorMapper.IsRecoverable(EsclErrorMapper.MapStatusCode(response.StatusCode)))
                {
                    Backend = Id,
                    ScannerId = scannerId
                };
            }

            var xml = await response.Content.ReadAsStringAsync(ct);
            var parsed = EsclCapabilitiesParser.Parse(xml);

            var scanner = new Scanner
            {
                Id = scannerId,
                Name = !string.IsNullOrEmpty(parsed.MakeAndModel) ? parsed.MakeAndModel : $"eSCL Scanner ({baseUri.Authority})",
                Manufacturer = parsed.Manufacturer,
                Model = parsed.Model,
                SerialNumber = parsed.SerialNumber,
                Backend = Id,
                ConnectionType = ConnectionType.Network,
                Status = ScannerStatus.Idle
            };

            _devices[scannerId] = new EsclDeviceInfo(baseUri, scanner, parsed.Capabilities);
            return parsed.Capabilities;
        }
        catch (Exception ex) when (ex is not ScanEngineException)
        {
            throw EsclErrorMapper.MapHttpException(ex, scannerId, context: "GetCapabilitiesAsync");
        }
    }

    public Task ConfigureAsync(string scannerId, string jobId, EffectiveConfiguration configuration, CancellationToken ct = default)
    {
        var baseUri = GetBaseUriForScanner(scannerId);
        var parameters = EsclScanSettingsBuilder.ExtractParameters(configuration);

        _jobs[jobId] = new EsclJobContext(scannerId, jobId, baseUri, parameters);
        return Task.CompletedTask;
    }

    public async Task StartAsync(string scannerId, string jobId, CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(jobId, out var jobCtx))
        {
            throw new ScanEngineException(ErrorCode.InvalidConfiguration, "ConfigureAsync doit être appelé avant StartAsync.")
            {
                Backend = Id,
                ScannerId = scannerId,
                JobId = jobId
            };
        }

        var client = CreateClient();
        var scanJobsUri = new Uri(jobCtx.BaseUri, "/eSCL/ScanJobs");
        var xml = EsclScanSettingsBuilder.BuildXml(jobCtx.Parameters);

        using var request = new HttpRequestMessage(HttpMethod.Post, scanJobsUri);
        request.Content = new StringContent(xml, Encoding.UTF8, "text/xml");

        try
        {
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Created)
            {
                var location = response.Headers.Location
                    ?? throw new ScanEngineException(ErrorCode.BackendError, "Le scanner eSCL n'a retourné aucun header Location pour le job créé.")
                    {
                        Backend = Id,
                        ScannerId = scannerId,
                        JobId = jobId
                    };

                if (!location.IsAbsoluteUri)
                {
                    location = new Uri(jobCtx.BaseUri, location);
                }

                jobCtx.JobLocation = location;
                return;
            }

            var errorDetail = await response.Content.ReadAsStringAsync(ct);
            var code = EsclErrorMapper.MapStatusCode(response.StatusCode);
            throw new ScanEngineException(
                code,
                $"Échec de création du job de scan eSCL ({response.StatusCode}): {errorDetail}",
                recoverable: EsclErrorMapper.IsRecoverable(code))
            {
                Backend = Id,
                ScannerId = scannerId,
                JobId = jobId
            };
        }
        catch (Exception ex) when (ex is not ScanEngineException)
        {
            throw EsclErrorMapper.MapHttpException(ex, scannerId, jobId, context: "StartAsync");
        }
    }

    public async IAsyncEnumerable<AcquiredPage> AcquireAsync(
        string scannerId,
        string jobId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(jobId, out var jobCtx) || jobCtx.JobLocation is null)
        {
            throw new ScanEngineException(ErrorCode.InvalidConfiguration, "StartAsync doit être appelé avec succès avant AcquireAsync.")
            {
                Backend = Id,
                ScannerId = scannerId,
                JobId = jobId
            };
        }

        var client = CreateClient();
        var nextDocumentUri = new Uri(jobCtx.JobLocation.ToString().TrimEnd('/') + "/NextDocument");
        var pageNumber = 0;

        while (!ct.IsCancellationRequested)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await client.GetAsync(nextDocumentUri, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex)
            {
                throw EsclErrorMapper.MapHttpException(ex, scannerId, jobId, context: "AcquireAsync:GetNextDocument");
            }

            using (response)
            {
                // 404 Not Found est le signal normal eSCL indiquant qu'il n'y a plus de document dans le bac ADF
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    yield break;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errorDetail = await response.Content.ReadAsStringAsync(ct);
                    var code = EsclErrorMapper.MapStatusCode(response.StatusCode);
                    throw new ScanEngineException(
                        code,
                        $"Erreur lors de la récupération de la page eSCL ({response.StatusCode}): {errorDetail}",
                        recoverable: EsclErrorMapper.IsRecoverable(code))
                    {
                        Backend = Id,
                        ScannerId = scannerId,
                        JobId = jobId
                    };
                }

                pageNumber++;

                var pageStream = new MemoryStream();
                await response.Content.CopyToAsync(pageStream, ct);
                pageStream.Position = 0;

                yield return new AcquiredPage
                {
                    PageNumber = pageNumber,
                    Width = 0,
                    Height = 0,
                    Dpi = jobCtx.Parameters.Resolution,
                    PixelFormat = NormalizePixelFormat(jobCtx.Parameters.ColorMode),
                    Source = jobCtx.Parameters.Source == "Feeder" ? "adf" : "flatbed",
                    Data = pageStream
                };
            }
        }
    }

    public Task StopAsync(string scannerId, string jobId, CancellationToken ct = default) =>
        CleanupJobAsync(scannerId, jobId);

    public Task CancelAsync(string scannerId, string jobId, CancellationToken ct = default) =>
        CleanupJobAsync(scannerId, jobId);

    public Task<ScannerStatus> GetStatusAsync(string scannerId, CancellationToken ct = default)
    {
        var isBusy = _jobs.Values.Any(j => j.ScannerId == scannerId);
        return Task.FromResult(isBusy ? ScannerStatus.Busy : ScannerStatus.Idle);
    }

    private async Task CleanupJobAsync(string scannerId, string jobId)
    {
        if (_jobs.TryRemove(jobId, out var jobCtx) && jobCtx.JobLocation is not null)
        {
            try
            {
                var client = CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await client.DeleteAsync(jobCtx.JobLocation, cts.Token);
            }
            catch
            {
                // Nettoyage best-effort : ignorer les erreurs de suppression
            }
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("escl");
        try
        {
            client.Timeout = _options.RequestTimeout;
        }
        catch (InvalidOperationException)
        {
            // Ignore if HttpClient instance has already started requests
        }
        return client;
    }

    private Uri GetBaseUriForScanner(string scannerId)
    {
        if (_devices.TryGetValue(scannerId, out var dev))
        {
            return dev.BaseUri;
        }

        if (scannerId.StartsWith("escl:", StringComparison.OrdinalIgnoreCase))
        {
            var raw = scannerId["escl:".Length..];
            return NormalizeBaseUri(raw);
        }

        throw new ScanEngineException(ErrorCode.ScannerNotFound, $"Scanner eSCL '{scannerId}' introuvable.")
        {
            Backend = Id,
            ScannerId = scannerId
        };
    }

    private static Uri NormalizeBaseUri(string hostOrUri)
    {
        var trimmed = hostOrUri.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        return new Uri(trimmed.TrimEnd('/'));
    }

    private static string NormalizePixelFormat(string colorMode) =>
        colorMode switch
        {
            "BlackAndWhite1" => "bw",
            "Grayscale8" => "grayscale",
            _ => "color"
        };

    private sealed record EsclDeviceInfo(Uri BaseUri, Scanner Scanner, ScannerCapabilities Capabilities);

    private sealed class EsclJobContext(string scannerId, string jobId, Uri baseUri, EsclJobParameters parameters)
    {
        public string ScannerId { get; } = scannerId;
        public string JobId { get; } = jobId;
        public Uri BaseUri { get; } = baseUri;
        public EsclJobParameters Parameters { get; } = parameters;
        public Uri? JobLocation { get; set; }
    }
}

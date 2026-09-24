using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenScan.Contracts;

namespace OpenScan.Backends.Wia;

public sealed class WiaScannerBackend(ILogger<WiaScannerBackend> logger) : IScannerBackend
{
    public const string Id = "wia";

    private readonly ILogger<WiaScannerBackend> _logger = logger;

    private const string DeviceManagerProgId = "WIA.DeviceManager";
    private const string FormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}"; 
    private const int ScannerDeviceType = 1;

    private const string DocumentHandlingCapabilitiesProperty = "Document Handling Capabilities";
    private const string DocumentHandlingSelectProperty = "Document Handling Select";
    private const string HorizontalResolutionProperty = "Horizontal Resolution";
    private const string VerticalResolutionProperty = "Vertical Resolution";
    private const string CurrentIntentProperty = "Current Intent";

    private readonly ConcurrentDictionary<string, ResolvedJobSettings> _jobSettings = new();

    public string BackendId => Id;

    public Task<IReadOnlyList<Scanner>> DiscoverAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<Scanner>>([]);
        }

        try
        {
            dynamic manager = CreateDeviceManager();
            var scanners = new List<Scanner>();
            var totalDeviceInfos = 0;

            foreach (dynamic info in manager.DeviceInfos)
            {
                totalDeviceInfos++;
                if ((int)info.Type != ScannerDeviceType)
                {
                    continue;
                }

                var deviceId = (string)info.DeviceID;
                var manufacturer = TryReadInfoProperty(info, "Manufacturer");
                var name = TryReadInfoProperty(info, "Name") ?? TryReadInfoProperty(info, "Description") ?? "Scanner WIA";

                scanners.Add(new Scanner
                {
                    Id = deviceId,
                    Name = name,
                    Manufacturer = manufacturer,
                    Model = name,
                    Backend = Id,
                    ConnectionType = ConnectionType.Usb,
                    Status = ScannerStatus.Idle
                });
            }

            // WIA.DeviceManager peut très bien répondre sans erreur mais lister zéro appareil
            // scanner (ex. service WIA arrêté malgré tout, pilote uniquement TWAIN, appareil
            // reconnu comme "Unknown device" côté Windows) — journaliser ce cas précis permet de
            // distinguer "aucun scanner branché" de "un scanner est branché mais Windows ne le
            // reconnaît pas comme périphérique d'imagerie WIA", ce que l'utilisateur ne peut sinon
            // absolument pas diagnostiquer lui-même.
            if (scanners.Count == 0)
            {
                _logger.LogWarning(
                    "WIA.DeviceManager n'a retourné aucun scanner ({TotalDeviceInfos} DeviceInfos au total, tous types confondus). " +
                    "Vérifier : le service Windows Image Acquisition (WIA) est démarré (services.msc), le scanner apparaît " +
                    "dans le Gestionnaire de périphériques sous \"Périphériques d'imagerie\" avec un pilote fonctionnel " +
                    "(pas \"Périphérique inconnu\"), et que le pilote installé est bien un pilote WIA (certains scanners " +
                    "n'exposent qu'un pilote TWAIN, dans ce cas WIA ne les verra jamais).",
                    totalDeviceInfos);
            }
            else
            {
                _logger.LogInformation("WIA.DeviceManager : {ScannerCount} scanner(s) détecté(s) sur {TotalDeviceInfos} DeviceInfos.", scanners.Count, totalDeviceInfos);
            }

            return Task.FromResult<IReadOnlyList<Scanner>>(scanners);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Échec de la découverte WIA (WIA.DeviceManager). Le composant COM est peut-être non enregistré ou le service Windows Image Acquisition (WIA) est arrêté.");
            return Task.FromResult<IReadOnlyList<Scanner>>([]);
        }
    }

    public Task OpenAsync(string scannerId, CancellationToken ct = default)
    {
        RequireWindows(scannerId, jobId: null);
        return Task.CompletedTask;
    }

    public Task CloseAsync(string scannerId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<ScannerCapabilities> GetCapabilitiesAsync(string scannerId, CancellationToken ct = default)
    {
        RequireWindows(scannerId, jobId: null);

        dynamic device = ConnectDevice(scannerId);
        int capabilitiesBitmask = TryReadDeviceProperty(device, DocumentHandlingCapabilitiesProperty, fallback: 0);
        var (adf, flatbed, duplex) = WiaCapabilityMapper.MapDocumentHandling(capabilitiesBitmask);

        dynamic item = device.Items[1];
        List<int> resolutions = ReadSupportedResolutions(item);

        var pixelFormats = new List<string> { "bw", "grayscale", "color" }; // WIA n'expose pas de liste fiable et standard ; les trois intents de base sont quasi-universels
        var bitDepths = new List<int> { 1, 8, 24 };

        return Task.FromResult(new ScannerCapabilities
        {
            Sources = WiaCapabilityMapper.MapSources(capabilitiesBitmask).ToList(),
            Duplex = duplex,
            Resolutions = resolutions,
            PixelFormats = pixelFormats,
            BitDepths = bitDepths,
            Adf = adf,
            Flatbed = flatbed
        });
    }

    public Task ConfigureAsync(string scannerId, string jobId, EffectiveConfiguration configuration, CancellationToken ct = default)
    {
        RequireWindows(scannerId, jobId);

        var requestedSource = AsString(configuration.Effective.GetValueOrDefault("source"), "adf");
        var duplex = AsBool(configuration.Effective.GetValueOrDefault("duplex"));
        var resolution = AsInt(configuration.Effective.GetValueOrDefault("resolution"), 300);
        var pixelFormat = AsString(configuration.Effective.GetValueOrDefault("pixel_format"), "color");

        dynamic device = ConnectDevice(scannerId);
        TrySetDeviceProperty(device, DocumentHandlingSelectProperty, WiaCapabilityMapper.ResolveDocumentHandlingSelect(requestedSource, duplex));

        dynamic item = device.Items[1];
        TrySetItemProperty(item, HorizontalResolutionProperty, resolution);
        TrySetItemProperty(item, VerticalResolutionProperty, resolution);
        TrySetItemProperty(item, CurrentIntentProperty, WiaCapabilityMapper.ResolveIntent(pixelFormat));

        _jobSettings[jobId] = new ResolvedJobSettings(
            scannerId, resolution, pixelFormat, requestedSource.Equals("adf", StringComparison.OrdinalIgnoreCase));

        return Task.CompletedTask;
    }

    public Task StartAsync(string scannerId, string jobId, CancellationToken ct = default) => Task.CompletedTask;

    public async IAsyncEnumerable<AcquiredPage> AcquireAsync(
        string scannerId,
        string jobId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        if (!_jobSettings.TryGetValue(jobId, out var settings))
        {
            throw new ScanEngineException(ErrorCode.InvalidConfiguration, "ConfigureAsync doit être appelé avant AcquireAsync.")
            {
                Backend = Id,
                ScannerId = scannerId,
                JobId = jobId
            };
        }

        var workDirectory = Path.Combine(Path.GetTempPath(), "openscan-wia", jobId);
        Directory.CreateDirectory(workDirectory);

        dynamic device = ConnectDevice(scannerId);
        dynamic item = device.Items[1];

        var pageNumber = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();

            dynamic? imageFile = null;
            var feederEmpty = false;
            ScanEngineException? terminalError = null;

            try
            {
                imageFile = item.Transfer(FormatBmp);
            }
            catch (COMException ex)
            {
                if (WiaErrorMapper.IsFeederEmpty(ex.HResult))
                {
                    feederEmpty = true;
                }
                else
                {
                    var code = WiaErrorMapper.MapErrorCode(ex.HResult);
                    terminalError = new ScanEngineException(code, ex.Message, WiaErrorMapper.IsRecoverable(code))
                    {
                        Backend = Id,
                        ScannerId = scannerId,
                        JobId = jobId,
                        TechnicalDetails = $"HRESULT=0x{unchecked((uint)ex.HResult):X8}"
                    };
                }
            }

            if (feederEmpty)
            {
                yield break;
            }

            if (terminalError is not null)
            {
                throw terminalError;
            }

            pageNumber++;
            var tempPath = Path.Combine(workDirectory, $"page-{pageNumber}.bmp");
            imageFile!.SaveFile(tempPath);
            Marshal.ReleaseComObject(imageFile);

            var (width, height) = ReadBmpDimensions(tempPath);

            yield return new AcquiredPage
            {
                PageNumber = pageNumber,
                Width = width,
                Height = height,
                Dpi = settings.Resolution,
                PixelFormat = settings.PixelFormat,
                Source = settings.IsAdf ? "adf" : "flatbed",
                Data = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose)
            };

            if (!settings.IsAdf)
            {
                yield break; 
            }
        }
    }

    public Task StopAsync(string scannerId, string jobId, CancellationToken ct = default)
    {
        _jobSettings.TryRemove(jobId, out _);
        return Task.CompletedTask;
    }

    public Task<ScannerStatus> GetStatusAsync(string scannerId, CancellationToken ct = default)
    {
        var busy = _jobSettings.Values.Any(s => s.ScannerId == scannerId);
        return Task.FromResult(busy ? ScannerStatus.Busy : ScannerStatus.Idle);
    }

    public Task CancelAsync(string scannerId, string jobId, CancellationToken ct = default)
    {
        _jobSettings.TryRemove(jobId, out _);
        return Task.CompletedTask;
    }

    private void RequireWindows(string scannerId, string? jobId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ScanEngineException(ErrorCode.BackendError, "Le backend WIA n'est disponible que sous Windows.")
            {
                Backend = Id,
                ScannerId = scannerId,
                JobId = jobId
            };
        }
    }

    private static dynamic CreateDeviceManager()
    {
        var type = Type.GetTypeFromProgID(DeviceManagerProgId)
            ?? throw new ScanEngineException(ErrorCode.BackendError, "Le composant WIA.DeviceManager n'est pas enregistré sur cette machine.") { Backend = Id };
        return Activator.CreateInstance(type)!;
    }

    private dynamic ConnectDevice(string scannerId)
    {
        dynamic manager = CreateDeviceManager();
        foreach (dynamic info in manager.DeviceInfos)
        {
            if ((string)info.DeviceID == scannerId)
            {
                return info.Connect();
            }
        }

        throw new ScanEngineException(ErrorCode.ScannerNotFound, $"Scanner WIA '{scannerId}' introuvable.") { Backend = Id, ScannerId = scannerId };
    }

    private static List<int> ReadSupportedResolutions(dynamic item)
    {
        try
        {
            dynamic property = item.Properties[HorizontalResolutionProperty];
            int subType = property.SubType; 

            if (subType == 3)
            {
                var min = Convert.ToInt32(property.SubTypeMin);
                var max = Convert.ToInt32(property.SubTypeMax);
                var step = Convert.ToInt32(property.SubTypeStep);
                return WiaCapabilityMapper.ExpandResolutionRange(min, max, step).ToList();
            }

            var values = new List<int>();
            foreach (var v in property.SubTypeValues)
            {
                values.Add(Convert.ToInt32(v));
            }

            return values;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string? TryReadInfoProperty(dynamic info, string propertyName)
    {
        try
        {
            return (string)info.Properties[propertyName].Value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int TryReadDeviceProperty(dynamic device, string propertyName, int fallback)
    {
        try
        {
            return Convert.ToInt32(device.Properties[propertyName].Value);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static void TrySetDeviceProperty(dynamic device, string propertyName, object value)
    {
        try
        {
            device.Properties[propertyName].Value = value;
        }
        catch (Exception)
        {
        }
    }

    private static void TrySetItemProperty(dynamic item, string propertyName, object value)
    {
        try
        {
            item.Properties[propertyName].Value = value;
        }
        catch (Exception)
        {
        }
    }

    private static (int Width, int Height) ReadBmpDimensions(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[26];
        var read = stream.Read(header);
        if (read < 26)
        {
            return (0, 0);
        }

        var width = BitConverter.ToInt32(header[18..22]);
        var height = BitConverter.ToInt32(header[22..26]);
        return (width, Math.Abs(height));
    }

    private static string AsString(object? value, string fallback)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() ?? fallback : fallback;
        }

        return value as string ?? fallback;
    }

    private static bool AsBool(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.True;
        }

        return value is true;
    }

    private static int AsInt(object? value, int fallback)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var i) ? i : fallback;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            _ => fallback
        };
    }

    private sealed record ResolvedJobSettings(string ScannerId, int Resolution, string PixelFormat, bool IsAdf);
}

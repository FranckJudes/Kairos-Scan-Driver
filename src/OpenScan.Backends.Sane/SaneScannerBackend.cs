using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenScan.Contracts;

namespace OpenScan.Backends.Sane;


public sealed class SaneScannerBackend : IScannerBackend
{
    public const string Id = "sane";

    private readonly ConcurrentDictionary<string, SaneDeviceOptions> _deviceOptionsCache = new();
    private readonly ConcurrentDictionary<string, ResolvedJobSettings> _jobSettings = new();

    public string BackendId => Id;

    public async Task<IReadOnlyList<Scanner>> DiscoverAsync(CancellationToken ct = default)
    {
        var (exitCode, stdout, _) = await RunScanimageAsync(["-f", "%d|%v|%m|%t%n"], ct);
        if (exitCode != 0)
        {
            return [];
        }

        var scanners = new List<Scanner>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 3)
            {
                continue;
            }

            var deviceId = parts[0].Trim();
            var vendor = parts[1].Trim();
            var model = parts[2].Trim();
            if (deviceId.Length == 0)
            {
                continue;
            }

            scanners.Add(new Scanner
            {
                Id = deviceId,
                Name = $"{vendor} {model}".Trim(),
                Manufacturer = vendor.Length > 0 ? vendor : null,
                Model = model.Length > 0 ? model : null,
                Backend = Id,
                ConnectionType = deviceId.StartsWith("airscan:", StringComparison.OrdinalIgnoreCase)
                    || deviceId.StartsWith("net:", StringComparison.OrdinalIgnoreCase)
                        ? ConnectionType.Network
                        : ConnectionType.Usb,
                Status = ScannerStatus.Idle
            });
        }

        return scanners;
    }

    public Task OpenAsync(string scannerId, CancellationToken ct = default) => Task.CompletedTask;

    public Task CloseAsync(string scannerId, CancellationToken ct = default) => Task.CompletedTask;

    public async Task<ScannerCapabilities> GetCapabilitiesAsync(string scannerId, CancellationToken ct = default)
    {
        var options = await GetOrQueryDeviceOptionsAsync(scannerId, ct);

        var sources = new List<string>();
        var adf = options.SourceValues.Any(IsAdfSource);
        var flatbed = options.SourceValues.Any(IsFlatbedSource);
        var duplex = options.SourceValues.Any(IsDuplexSource);
        if (adf)
        {
            sources.Add("adf");
        }

        if (flatbed)
        {
            sources.Add("flatbed");
        }

        var pixelFormats = new List<string>();
        var bitDepths = new List<int>();
        if (options.ModeValues.Any(m => m.Equals("Lineart", StringComparison.OrdinalIgnoreCase)))
        {
            pixelFormats.Add("bw");
            bitDepths.Add(1);
        }

        if (options.ModeValues.Any(m => m.Equals("Gray", StringComparison.OrdinalIgnoreCase)))
        {
            pixelFormats.Add("grayscale");
            bitDepths.Add(8);
        }

        if (options.ModeValues.Any(m => m.Equals("Color", StringComparison.OrdinalIgnoreCase)))
        {
            pixelFormats.Add("color");
            bitDepths.Add(24);
        }

        PageSize? maxPageSize = options is { MaxPageWidthMm: not null, MaxPageHeightMm: not null }
            ? new PageSize { Width = (int)(options.MaxPageWidthMm.Value * 100), Height = (int)(options.MaxPageHeightMm.Value * 100) }
            : null;

        return new ScannerCapabilities
        {
            Sources = sources,
            Duplex = duplex,
            Resolutions = options.Resolutions.ToList(),
            PixelFormats = pixelFormats,
            BitDepths = bitDepths,
            Adf = adf,
            Flatbed = flatbed,
            MaxPageSize = maxPageSize
        };
    }

    public async Task ConfigureAsync(string scannerId, string jobId, EffectiveConfiguration configuration, CancellationToken ct = default)
    {
        var options = await GetOrQueryDeviceOptionsAsync(scannerId, ct);

        var requestedSource = AsString(configuration.Effective.GetValueOrDefault("source"), "adf");
        var duplex = AsBool(configuration.Effective.GetValueOrDefault("duplex"));
        var resolution = AsInt(configuration.Effective.GetValueOrDefault("resolution"), 300);
        var pixelFormat = AsString(configuration.Effective.GetValueOrDefault("pixel_format"), "color");
        var pageSize = AsString(configuration.Effective.GetValueOrDefault("page_size"), "auto");

        var saneSource = ResolveSource(options, requestedSource, duplex)
            ?? throw new ScanEngineException(ErrorCode.CapabilityUnsupported,
                $"Aucune source SANE ne correspond à source='{requestedSource}', duplex={duplex} sur ce scanner.")
            { Backend = Id, ScannerId = scannerId, JobId = jobId };

        var saneMode = ResolveMode(options, pixelFormat)
            ?? throw new ScanEngineException(ErrorCode.CapabilityUnsupported,
                $"Aucun mode SANE ne correspond à pixel_format='{pixelFormat}' sur ce scanner.")
            { Backend = Id, ScannerId = scannerId, JobId = jobId };

        // Sans ça, scanimage utilise la zone de scan par défaut du pilote (souvent la surface
        // maximale vitre/ADF), laissant une marge blanche autour de tout document plus petit.
        // "swcrop" n'est disponible que sur certains pilotes SANE — on ne l'active que s'il est
        // réellement exposé par ce scanner (sinon scanimage échouerait sur une option inconnue).
        var softwareCrop = options.SupportsSoftwareCrop && pageSize.Equals("auto", StringComparison.OrdinalIgnoreCase);

        _jobSettings[jobId] = new ResolvedJobSettings(scannerId, saneSource, saneMode, resolution, requestedSource.Equals("adf", StringComparison.OrdinalIgnoreCase), softwareCrop);
    }

    public Task StartAsync(string scannerId, string jobId, CancellationToken ct = default) => Task.CompletedTask;

    public async IAsyncEnumerable<AcquiredPage> AcquireAsync(
        string scannerId,
        string jobId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_jobSettings.TryGetValue(jobId, out var settings))
        {
            throw new ScanEngineException(ErrorCode.InvalidConfiguration, "ConfigureAsync doit être appelé avant AcquireAsync.")
            {
                Backend = Id,
                ScannerId = scannerId,
                JobId = jobId
            };
        }

        var workDirectory = Path.Combine(Path.GetTempPath(), "openscan-sane", jobId);
        Directory.CreateDirectory(workDirectory);
        var batchPattern = Path.Combine(workDirectory, "page-%d.pnm");

        var arguments = new List<string>
        {
            "-d", settings.ScannerId,
            "--format=pnm",
            $"--resolution={settings.Resolution.ToString(CultureInfo.InvariantCulture)}",
            $"--mode={settings.SaneMode}",
            $"--source={settings.SaneSource}",
            $"--batch={batchPattern}",
            "--batch-print",
            "--batch-start=1"
        };

        if (!settings.IsAdf)
        {
            arguments.Add("--batch-count=1");
        }

        if (settings.SoftwareCrop)
        {
            arguments.Add("--swcrop=yes");
        }

        using var process = StartScanimage(arguments);
        using var killOnCancel = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
        });

        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            var pageNumber = 0;
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                var path = line.Trim();
                if (path.Length == 0 || !File.Exists(path))
                {
                    continue;
                }

                pageNumber++;
                var (width, height) = ReadPnmDimensions(path);

                yield return new AcquiredPage
                {
                    PageNumber = pageNumber,
                    Width = width,
                    Height = height,
                    Dpi = settings.Resolution,
                    PixelFormat = NormalizePixelFormat(settings.SaneMode),
                    Source = settings.IsAdf ? "adf" : "flatbed",
                    Data = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                        FileOptions.Asynchronous | FileOptions.DeleteOnClose)
                };
            }

            await process.WaitForExitAsync(ct);
            var stderrText = await stderrTask;

            if (process.ExitCode != 0 && !stderrText.Contains("out of documents", StringComparison.OrdinalIgnoreCase))
            {
                throw MapAcquisitionError(scannerId, jobId, process.ExitCode, stderrText);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* déjà terminé */ }
            }

            try { Directory.Delete(workDirectory, recursive: true); }
            catch (IOException) { /* fichiers déjà consommés/supprimés (DeleteOnClose) */ }
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

    private async Task<SaneDeviceOptions> GetOrQueryDeviceOptionsAsync(string scannerId, CancellationToken ct)
    {
        if (_deviceOptionsCache.TryGetValue(scannerId, out var cached))
        {
            return cached;
        }

        var (exitCode, stdout, stderr) = await RunScanimageAsync(["--help", "-d", scannerId], ct);
        if (exitCode != 0)
        {
            throw new ScanEngineException(ErrorCode.ScannerNotFound, $"Impossible d'interroger le scanner SANE '{scannerId}'.")
            {
                Backend = Id,
                ScannerId = scannerId,
                TechnicalDetails = stderr
            };
        }

        var options = SaneOptionsParser.Parse(stdout);
        _deviceOptionsCache[scannerId] = options;
        return options;
    }

    private static string? ResolveSource(SaneDeviceOptions options, string requestedSource, bool duplex)
    {
        if (requestedSource.Equals("flatbed", StringComparison.OrdinalIgnoreCase))
        {
            return options.SourceValues.FirstOrDefault(IsFlatbedSource);
        }

        if (duplex)
        {
            return options.SourceValues.FirstOrDefault(s => IsAdfSource(s) && IsDuplexSource(s));
        }

        return options.SourceValues.FirstOrDefault(s => IsAdfSource(s) && !IsDuplexSource(s))
            ?? options.SourceValues.FirstOrDefault(IsAdfSource);
    }

    private static string? ResolveMode(SaneDeviceOptions options, string pixelFormat) => pixelFormat.ToLowerInvariant() switch
    {
        "bw" => options.ModeValues.FirstOrDefault(m => m.Equals("Lineart", StringComparison.OrdinalIgnoreCase)),
        "grayscale" => options.ModeValues.FirstOrDefault(m => m.Equals("Gray", StringComparison.OrdinalIgnoreCase)),
        "color" => options.ModeValues.FirstOrDefault(m => m.Equals("Color", StringComparison.OrdinalIgnoreCase)),
        _ => null
    };

    private static string NormalizePixelFormat(string saneMode) => saneMode.ToLowerInvariant() switch
    {
        "lineart" => "bw",
        "gray" => "grayscale",
        "color" => "color",
        _ => saneMode.ToLowerInvariant()
    };

    private static bool IsAdfSource(string s) =>
        s.Contains("adf", StringComparison.OrdinalIgnoreCase) || s.Contains("feeder", StringComparison.OrdinalIgnoreCase);

    private static bool IsFlatbedSource(string s) =>
        s.Contains("flatbed", StringComparison.OrdinalIgnoreCase) || s.Contains("platen", StringComparison.OrdinalIgnoreCase);

    private static bool IsDuplexSource(string s) => s.Contains("duplex", StringComparison.OrdinalIgnoreCase);

    private static (int Width, int Height) ReadPnmDimensions(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        reader.ReadLine(); 

        string? line;
        do
        {
            line = reader.ReadLine();
        }
        while (line is not null && line.StartsWith('#'));

        if (line is null)
        {
            return (0, 0);
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
                ? (width, height)
                : (0, 0);
    }

    private static ScanEngineException MapAcquisitionError(string scannerId, string jobId, int exitCode, string stderrText)
    {
        var lower = stderrText.ToLowerInvariant();
        var code = lower switch
        {
            var s when s.Contains("no documents") || s.Contains("hopper") => ErrorCode.NoPaper,
            var s when s.Contains("jam") => ErrorCode.PaperJam,
            var s when s.Contains("multi") && s.Contains("feed") => ErrorCode.MultiFeed,
            var s when s.Contains("busy") => ErrorCode.ScannerBusy,
            var s when s.Contains("no such device") || s.Contains("invalid argument") || s.Contains("i/o error") => ErrorCode.DriverError,
            _ => ErrorCode.AcquisitionError
        };

        var recoverable = code is ErrorCode.PaperJam or ErrorCode.NoPaper or ErrorCode.MultiFeed;
        var message = stderrText.Trim().Length > 0 ? stderrText.Trim() : $"scanimage a échoué (code {exitCode}).";

        return new ScanEngineException(code, message, recoverable)
        {
            Backend = Id,
            ScannerId = scannerId,
            JobId = jobId,
            TechnicalDetails = stderrText
        };
    }

    private static Process StartScanimage(IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("scanimage")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        return process;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunScanimageAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        Process process;
        try
        {
            process = StartScanimage(arguments);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, string.Empty, "scanimage introuvable");
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
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

    private sealed record ResolvedJobSettings(string ScannerId, string SaneSource, string SaneMode, int Resolution, bool IsAdf, bool SoftwareCrop);
}

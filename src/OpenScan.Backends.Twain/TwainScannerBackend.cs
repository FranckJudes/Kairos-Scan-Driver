using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenScan.Contracts;

namespace OpenScan.Backends.Twain;

public sealed record WorkerResolutionResult(string? ExecutablePath, IReadOnlyList<string> SearchedPaths)
{
    public bool Found => !string.IsNullOrEmpty(ExecutablePath) && File.Exists(ExecutablePath);
}

/// <summary>
/// Client mince IScannerBackend pour TWAIN (section 6.5).
/// Ne contient AUCUN P/Invoke TWAIN : délègue entièrement l'exécution au processus isolé OpenScan.Twain.Worker.
/// Si le driver ou le DSM TWAIN crashe dans le worker, l'exception est interceptée ici sans jamais crasher l'Agent.
/// </summary>
public sealed class TwainScannerBackend : IScannerBackend
{
    public const string Id = "twain";

    private readonly string? _customWorkerExecutablePath;
    private readonly ITwainLogger _logger;
    private readonly ConcurrentDictionary<string, EffectiveConfiguration> _jobConfigurations = new();
    private readonly ConcurrentDictionary<string, Process> _activeWorkerProcesses = new();

    public string BackendId => Id;

    public TwainScannerBackend(string? customWorkerExecutablePath = null, ITwainLogger? logger = null)
    {
        _customWorkerExecutablePath = customWorkerExecutablePath;
        _logger = logger ?? ConsoleTwainLogger.Instance;
    }

    public TwainScannerBackend(ITwainLogger logger) : this(null, logger)
    {
    }

    public async Task<IReadOnlyList<Scanner>> DiscoverAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() && string.IsNullOrEmpty(_customWorkerExecutablePath))
        {
            return [];
        }

        var resolution = ResolveWorkerExecutablePath();
        if (!resolution.Found)
        {
            var searched = string.Join("\n  - ", resolution.SearchedPaths);
            _logger.LogWarning($"Le worker TWAIN (OpenScan.Twain.Worker) est introuvable. Aucun scanner TWAIN ne sera détecté.\nEmplacements recherchés ({resolution.SearchedPaths.Count} candidats) :\n  - {searched}");
            return [];
        }

        var workerPath = resolution.ExecutablePath!;
        _logger.LogInfo($"Démarrage du worker TWAIN pour découverte : {workerPath}");

        Process process;
        try
        {
            process = StartWorkerProcess(workerPath);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Impossible de démarrer le worker TWAIN ({workerPath}) : {ex.Message}", ex);
            return [];
        }

        try
        {
            await process.StandardInput.WriteLineAsync(WorkerProtocol.SerializeCommand(new DiscoverCommand()));
            await process.StandardInput.FlushAsync(ct);

            var responseLine = await ReadNextJsonLineAsync(process.StandardOutput, ct);
            if (responseLine is null)
            {
                var stderr = await process.StandardError.ReadToEndAsync(CancellationToken.None);
                _logger.LogWarning($"Le worker TWAIN n'a renvoyé aucune réponse JSON sur stdout. ExitCode={process.ExitCode}, Stderr: '{stderr.Trim()}'");
                return [];
            }

            var response = WorkerProtocol.DeserializeResponse(responseLine);
            if (response is ScannersResponse scannersResp)
            {
                foreach (var s in scannersResp.Scanners)
                {
                    s.Backend = Id;
                }
                _logger.LogInfo($"Découverte TWAIN terminée : {scannersResp.Scanners.Count} scanner(s) trouvé(s).");
                return scannersResp.Scanners;
            }

            if (response is ErrorResponse err)
            {
                _logger.LogWarning($"Le worker TWAIN a retourné une erreur lors de la découverte : {err.Code} - {err.Message}. Détails: {err.TechnicalDetails}");
                return [];
            }

            _logger.LogWarning($"Réponse inattendue du worker TWAIN lors de la découverte : {responseLine}");
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError($"Erreur lors de la découverte TWAIN : {ex.Message}", ex);
            return [];
        }
        finally
        {
            await StopWorkerGracefullyAsync(process);
        }
    }

    public Task OpenAsync(string scannerId, CancellationToken ct = default)
    {
        RequireWindows(scannerId, null);
        return Task.CompletedTask;
    }

    public Task CloseAsync(string scannerId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public async Task<ScannerCapabilities> GetCapabilitiesAsync(string scannerId, CancellationToken ct = default)
    {
        RequireWindows(scannerId, null);

        var workerPath = GetRequiredWorkerExecutablePath(scannerId, null);
        var process = StartWorkerProcess(workerPath);

        try
        {
            await process.StandardInput.WriteLineAsync(WorkerProtocol.SerializeCommand(new GetCapabilitiesCommand(scannerId)));
            await process.StandardInput.FlushAsync(ct);

            var responseLine = await ReadNextJsonLineAsync(process.StandardOutput, ct);
            if (responseLine is null)
            {
                var stderr = await process.StandardError.ReadToEndAsync(CancellationToken.None);
                _logger.LogError($"Le worker TWAIN n'a renvoyé aucune réponse pour GetCapabilities({scannerId}). Stderr: {stderr.Trim()}");
                throw new ScanEngineException(ErrorCode.DriverError, $"Le worker TWAIN n'a retourné aucune réponse pour les capacités du scanner '{scannerId}'.")
                {
                    Backend = Id,
                    ScannerId = scannerId,
                    TechnicalDetails = stderr
                };
            }

            var response = WorkerProtocol.DeserializeResponse(responseLine);
            if (response is CapabilitiesResponse capsResp)
            {
                return capsResp.Capabilities;
            }

            if (response is ErrorResponse err)
            {
                _logger.LogWarning($"Le worker TWAIN a échoué à lire les capacités de '{scannerId}': {err.Code} - {err.Message}");
                throw new ScanEngineException(err.Code, err.Message, err.Recoverable)
                {
                    Backend = Id,
                    ScannerId = scannerId,
                    TechnicalDetails = err.TechnicalDetails
                };
            }

            throw new ScanEngineException(ErrorCode.BackendError, $"Réponse inattendue du worker TWAIN: {responseLine}")
            {
                Backend = Id,
                ScannerId = scannerId
            };
        }
        finally
        {
            await StopWorkerGracefullyAsync(process);
        }
    }

    public Task ConfigureAsync(string scannerId, string jobId, EffectiveConfiguration configuration, CancellationToken ct = default)
    {
        RequireWindows(scannerId, jobId);
        _jobConfigurations[jobId] = configuration;
        return Task.CompletedTask;
    }

    public Task StartAsync(string scannerId, string jobId, CancellationToken ct = default)
    {
        RequireWindows(scannerId, jobId);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<AcquiredPage> AcquireAsync(
        string scannerId,
        string jobId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        RequireWindows(scannerId, jobId);

        var workerPath = GetRequiredWorkerExecutablePath(scannerId, jobId);
        var config = _jobConfigurations.GetValueOrDefault(jobId)
            ?? throw new ScanEngineException(ErrorCode.InvalidConfiguration, $"Aucune configuration enregistrée pour le job '{jobId}'.")
            {
                Backend = Id,
                ScannerId = scannerId,
                JobId = jobId
            };

        var source = config.Effective.GetValueOrDefault("source")?.ToString() ?? "adf";
        var duplex = config.Effective.TryGetValue("duplex", out var d) && d is true or "true" or 1;
        var resolution = config.Effective.TryGetValue("resolution", out var r) && int.TryParse(r?.ToString(), out var res) ? res : 300;
        var pixelFormat = config.Effective.GetValueOrDefault("pixel_format")?.ToString() ?? "color";
        // "auto" (TWSS_NONE) laisse le pilote détecter la taille réelle du document — une valeur
        // fixe comme "a4" forcerait une zone de capture physique constante, produisant une bande
        // blanche en bas de page si le document est plus court que cette taille.
        var pageSize = config.Effective.GetValueOrDefault("page_size")?.ToString() ?? "auto";

        var process = StartWorkerProcess(workerPath);
        _activeWorkerProcesses[jobId] = process;

        try
        {
            // 1. Envoyer la commande de configuration
            var configureCmd = new ConfigureCommand(scannerId, jobId, source, duplex, resolution, pixelFormat, pageSize);
            await process.StandardInput.WriteLineAsync(WorkerProtocol.SerializeCommand(configureCmd));
            await process.StandardInput.FlushAsync(ct);

            // 2. Envoyer la commande de lancement d'acquisition
            var acquireCmd = new AcquireCommand(scannerId, jobId);
            await process.StandardInput.WriteLineAsync(WorkerProtocol.SerializeCommand(acquireCmd));
            await process.StandardInput.FlushAsync(ct);

            // 3. Boucle de lecture en flux
            while (!process.HasExited && !ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await ReadNextJsonLineAsync(process.StandardOutput, ct);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                if (line is null)
                {
                    break;
                }

                var response = WorkerProtocol.DeserializeResponse(line);
                if (response is PageAcquiredResponse page)
                {
                    if (!File.Exists(page.FilePath))
                    {
                        _logger.LogError($"Fichier image TWAIN introuvable sur le disque : {page.FilePath}");
                        throw new ScanEngineException(ErrorCode.StorageError, $"Fichier image généré introuvable: {page.FilePath}")
                        {
                            Backend = Id,
                            ScannerId = scannerId,
                            JobId = jobId
                        };
                    }

                    // Ouverture en flux avec DeleteOnClose pour auto-nettoyage dès consommation complète (section 18)
                    var fileStream = new FileStream(
                        page.FilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        81920,
                        FileOptions.Asynchronous | FileOptions.DeleteOnClose);

                    yield return new AcquiredPage
                    {
                        PageNumber = page.PageNumber,
                        Width = page.Width,
                        Height = page.Height,
                        Dpi = page.Dpi,
                        PixelFormat = page.PixelFormat,
                        Source = page.Source,
                        Data = fileStream
                    };
                }
                else if (response is BatchCompleteResponse)
                {
                    yield break; // Fin normale du lot
                }
                else if (response is ErrorResponse err)
                {
                    _logger.LogWarning($"Le worker TWAIN a signalé une erreur pendant le job '{jobId}': {err.Code} - {err.Message}");
                    throw new ScanEngineException(err.Code, err.Message, err.Recoverable)
                    {
                        Backend = Id,
                        ScannerId = scannerId,
                        JobId = jobId,
                        TechnicalDetails = err.TechnicalDetails
                    };
                }
            }

            // Si le worker quitte inopinément sans BatchCompleteResponse
            if (!process.HasExited)
            {
                try
                {
                    process.WaitForExit(2000);
                }
                catch
                {
                    // ignore
                }
            }

            if (process.HasExited && process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(CancellationToken.None);
                _logger.LogError($"Le worker TWAIN a crashé ou s'est arrêté avec le code {process.ExitCode}. Stderr: {stderr.Trim()}");
                throw new ScanEngineException(
                    ErrorCode.DriverError,
                    $"Le worker TWAIN a crashé (code de sortie {process.ExitCode}).",
                    recoverable: false)
                {
                    Backend = Id,
                    ScannerId = scannerId,
                    JobId = jobId,
                    TechnicalDetails = stderr
                };
            }
        }
        finally
        {
            _activeWorkerProcesses.TryRemove(jobId, out _);
            _jobConfigurations.TryRemove(jobId, out _);

            await StopWorkerGracefullyAsync(process);
            process.Dispose();
        }
    }

    public Task StopAsync(string scannerId, string jobId, CancellationToken ct = default)
    {
        if (_activeWorkerProcesses.TryRemove(jobId, out var process))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            process.Dispose();
        }
        _jobConfigurations.TryRemove(jobId, out _);
        return Task.CompletedTask;
    }

    public Task<ScannerStatus> GetStatusAsync(string scannerId, CancellationToken ct = default)
    {
        var isBusy = _activeWorkerProcesses.Values.Any(p => !p.HasExited);
        return Task.FromResult(isBusy ? ScannerStatus.Busy : ScannerStatus.Idle);
    }

    public Task CancelAsync(string scannerId, string jobId, CancellationToken ct = default)
    {
        return StopAsync(scannerId, jobId, ct);
    }

    public WorkerResolutionResult ResolveWorkerExecutablePath()
    {
        var searched = new List<string>();

        if (!string.IsNullOrEmpty(_customWorkerExecutablePath))
        {
            searched.Add(_customWorkerExecutablePath);
            return new WorkerResolutionResult(
                File.Exists(_customWorkerExecutablePath) ? _customWorkerExecutablePath : null,
                searched);
        }

        var env = Environment.GetEnvironmentVariable("OPENSCAN_TWAIN_WORKER_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            searched.Add(env);
            if (File.Exists(env))
            {
                return new WorkerResolutionResult(env, searched);
            }
        }

        var exeNames = OperatingSystem.IsWindows()
            ? new[] { "OpenScan.Twain.Worker.exe", "OpenScan.Twain.Worker" }
            : new[] { "OpenScan.Twain.Worker", "OpenScan.Twain.Worker.exe" };

        var baseDir = AppContext.BaseDirectory;

        // 1. Sous-dossier dédié "twain-worker" à côté de l'Agent (voir OpenScan.Agent.csproj :
        //    le worker self-contained embarque son propre hostfxr.dll/coreclr.dll x86, qui ne
        //    doit JAMAIS être mélangé à plat avec les binaires x64 de l'Agent lui-même, sous
        //    peine de faire planter l'hôte .NET de l'Agent au démarrage).
        foreach (var name in exeNames)
        {
            var p = Path.Combine(baseDir, "twain-worker", name);
            searched.Add(p);
            if (File.Exists(p))
            {
                return new WorkerResolutionResult(p, searched);
            }
        }

        // 2. Même répertoire que l'Agent (baseDir) — compatibilité avec d'anciennes dispositions
        //    ou un déploiement manuel du worker directement à côté de l'Agent.
        foreach (var name in exeNames)
        {
            var p = Path.Combine(baseDir, name);
            searched.Add(p);
            if (File.Exists(p))
            {
                return new WorkerResolutionResult(p, searched);
            }
        }

        // 3. Répertoire de l'assembly courant si distinct
        var asmLocation = typeof(TwainScannerBackend).Assembly.Location;
        if (!string.IsNullOrEmpty(asmLocation))
        {
            var asmDir = Path.GetDirectoryName(asmLocation);
            if (!string.IsNullOrEmpty(asmDir) && !string.Equals(asmDir, baseDir, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var name in exeNames)
                {
                    var p = Path.Combine(asmDir, name);
                    searched.Add(p);
                    if (File.Exists(p))
                    {
                        return new WorkerResolutionResult(p, searched);
                    }
                }
            }
        }

        // 4. Remontée dynamique des répertoires parents pour environnement de développement
        var currentDir = new DirectoryInfo(baseDir);
        for (var depth = 0; depth < 8 && currentDir != null; depth++)
        {
            // "win-x86" : le worker est un déploiement self-contained ciblé win-x86 (voir
            // OpenScan.Twain.Worker.csproj), sa sortie de build vit donc sous ce sous-dossier de
            // RID et non directement dans bin\<Config>\net8.0\.
            var relativeCandidates = new[]
            {
                Path.Combine(currentDir.FullName, "src", "OpenScan.Twain.Worker", "bin", "Debug", "net8.0", "win-x86"),
                Path.Combine(currentDir.FullName, "src", "OpenScan.Twain.Worker", "bin", "Release", "net8.0", "win-x86"),
                Path.Combine(currentDir.FullName, "OpenScan.Twain.Worker", "bin", "Debug", "net8.0", "win-x86"),
                Path.Combine(currentDir.FullName, "OpenScan.Twain.Worker", "bin", "Release", "net8.0", "win-x86"),
                // Pour supporter les dossiers de checkout imbriqués (ex. OpenScan-Engine/OpenScan-Engine/...)
                Path.Combine(currentDir.FullName, "OpenScan-Engine", "src", "OpenScan.Twain.Worker", "bin", "Debug", "net8.0", "win-x86"),
                Path.Combine(currentDir.FullName, "OpenScan-Engine", "src", "OpenScan.Twain.Worker", "bin", "Release", "net8.0", "win-x86")
            };

            foreach (var relDir in relativeCandidates)
            {
                foreach (var name in exeNames)
                {
                    var p = Path.Combine(relDir, name);
                    if (!searched.Contains(p))
                    {
                        searched.Add(p);
                    }
                    if (File.Exists(p))
                    {
                        return new WorkerResolutionResult(p, searched);
                    }
                }
            }

            currentDir = currentDir.Parent;
        }

        return new WorkerResolutionResult(null, searched);
    }

    private void RequireWindows(string scannerId, string? jobId)
    {
        if (!OperatingSystem.IsWindows() && string.IsNullOrEmpty(_customWorkerExecutablePath))
        {
            throw new ScanEngineException(ErrorCode.BackendError, "Le backend TWAIN n'est disponible que sous Windows.")
            {
                Backend = Id,
                ScannerId = scannerId,
                JobId = jobId
            };
        }
    }

    private string GetRequiredWorkerExecutablePath(string scannerId, string? jobId)
    {
        var resolution = ResolveWorkerExecutablePath();
        if (!resolution.Found)
        {
            var searched = string.Join("\n  - ", resolution.SearchedPaths);
            var msg = $"Exécutable OpenScan.Twain.Worker introuvable.\nEmplacements recherchés ({resolution.SearchedPaths.Count} candidats) :\n  - {searched}";
            _logger.LogError(msg);
            throw new ScanEngineException(ErrorCode.BackendError, msg)
            {
                Backend = Id,
                ScannerId = scannerId,
                JobId = jobId
            };
        }
        return resolution.ExecutablePath!;
    }

    private static Process StartWorkerProcess(string executablePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = Process.Start(psi)
            ?? throw new ScanEngineException(ErrorCode.BackendError, $"Impossible de démarrer le worker TWAIN: {executablePath}");

        return process;
    }

    private static async Task StopWorkerGracefullyAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            await process.StandardInput.WriteLineAsync(WorkerProtocol.SerializeCommand(new ExitCommand()));
            await process.StandardInput.FlushAsync(CancellationToken.None);
            if (!process.WaitForExit(800))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }
    }

    private static async Task<string?> ReadNextJsonLineAsync(StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                return null;
            }

            var trimmed = line.Trim();
            if (trimmed.Length > 0 && trimmed.StartsWith('{'))
            {
                return trimmed;
            }
        }

        return null;
    }
}

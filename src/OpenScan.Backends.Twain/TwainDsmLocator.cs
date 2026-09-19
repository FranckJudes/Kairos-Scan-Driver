using System.Runtime.InteropServices;
using System.Text;

namespace OpenScan.Backends.Twain;

/// <summary>
/// Résultat d'une tentative de chargement du DSM TWAIN natif.
/// </summary>
public sealed record TwainDsmLoadResult(
    bool Success,
    IntPtr Handle,
    string? LoadedPath,
    IReadOnlyList<string> AttemptLogs,
    string DiagnosticSummary);

/// <summary>
/// Localisateur et chargeur robuste du Data Source Manager (DSM) TWAIN.
/// Gère les spécificités 32-bit (x86) / 64-bit (x64), WOW64, les emplacements Windows standard,
/// et fournit des diagnostics détaillés en cas d'échec de chargement.
/// </summary>
public static class TwainDsmLocator
{
    public const string DsmPathEnvVar = "OPENSCAN_TWAIN_DSM_PATH";
    public const string PreferLegacyEnvVar = "OPENSCAN_TWAIN_PREFER_LEGACY";

    private const string ModernDsmDll = "twaindsm.dll";
    private const string LegacyDsmDll = "twain_32.dll";

    /// <summary>
    /// Génère la liste ordonnée des chemins candidats pour localiser le DSM TWAIN.
    /// </summary>
    /// <param name="customPath">Chemin explicite optionnel (prioritaire).</param>
    /// <param name="preferLegacy">Si vrai, twain_32.dll est exploré avant twaindsm.dll.</param>
    /// <param name="windowsDir">Répertoire Windows explicite (pour tests/mock).</param>
    /// <param name="systemDir">Répertoire Système explicite (pour tests/mock).</param>
    /// <param name="appBaseDir">Répertoire de base de l'application (pour tests/mock).</param>
    public static List<string> GetCandidateDsmPaths(
        string? customPath = null,
        bool? preferLegacy = null,
        string? windowsDir = null,
        string? systemDir = null,
        string? appBaseDir = null)
    {
        var candidates = new List<string>();

        // 1. Variable d'environnement ou chemin personnalisé explicite
        var explicitPath = !string.IsNullOrWhiteSpace(customPath)
            ? customPath
            : Environment.GetEnvironmentVariable(DsmPathEnvVar);

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath.Trim());
        }

        var shouldUseLegacy = preferLegacy ?? IsLegacyPreferredFromEnvironment();

        // Répertoires Windows
        var winDir = !string.IsNullOrWhiteSpace(windowsDir)
            ? windowsDir
            : (OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : null);

        if (string.IsNullOrWhiteSpace(winDir) && OperatingSystem.IsWindows())
        {
            winDir = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
        }

        var sysDir = !string.IsNullOrWhiteSpace(systemDir)
            ? systemDir
            : (OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.System) : null);

        if (string.IsNullOrWhiteSpace(sysDir) && !string.IsNullOrWhiteSpace(winDir))
        {
            sysDir = CombinePath(winDir, "System32");
        }

        var sysWow64 = !string.IsNullOrWhiteSpace(winDir) ? CombinePath(winDir, "SysWOW64") : null;
        var twain32Sub = !string.IsNullOrWhiteSpace(winDir) ? CombinePath(winDir, "twain_32") : null;
        var appDir = !string.IsNullOrWhiteSpace(appBaseDir) ? appBaseDir : AppContext.BaseDirectory;

        // Candidats DSM 2.x moderne (twaindsm.dll)
        var modernCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(sysDir)) modernCandidates.Add(CombinePath(sysDir, ModernDsmDll));
        if (!string.IsNullOrWhiteSpace(sysWow64)) modernCandidates.Add(CombinePath(sysWow64, ModernDsmDll));
        if (!string.IsNullOrWhiteSpace(winDir)) modernCandidates.Add(CombinePath(winDir, ModernDsmDll));
        if (!string.IsNullOrWhiteSpace(appDir)) modernCandidates.Add(CombinePath(appDir, ModernDsmDll));
        modernCandidates.Add(ModernDsmDll);

        // Candidats DSM 1.x legacy (twain_32.dll)
        var legacyCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(winDir)) legacyCandidates.Add(CombinePath(winDir, LegacyDsmDll));
        if (!string.IsNullOrWhiteSpace(twain32Sub)) legacyCandidates.Add(CombinePath(twain32Sub, LegacyDsmDll));
        if (!string.IsNullOrWhiteSpace(sysDir)) legacyCandidates.Add(CombinePath(sysDir, LegacyDsmDll));
        if (!string.IsNullOrWhiteSpace(sysWow64)) legacyCandidates.Add(CombinePath(sysWow64, LegacyDsmDll));
        if (!string.IsNullOrWhiteSpace(appDir)) legacyCandidates.Add(CombinePath(appDir, LegacyDsmDll));
        legacyCandidates.Add(LegacyDsmDll);

        if (shouldUseLegacy)
        {
            candidates.AddRange(legacyCandidates);
            candidates.AddRange(modernCandidates);
        }
        else
        {
            candidates.AddRange(modernCandidates);
            candidates.AddRange(legacyCandidates);
        }

        // Nettoyer et dédupliquer en préservant l'ordre
        var distinctList = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in candidates)
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                distinctList.Add(path);
            }
        }

        return distinctList;
    }

    /// <summary>
    /// Tente de charger le DSM TWAIN en parcourant les candidats ordonnés.
    /// </summary>
    public static TwainDsmLoadResult TryLoadDsm(
        string? customPath = null,
        bool? preferLegacy = null)
    {
        var logs = new List<string>();
        var candidates = GetCandidateDsmPaths(customPath, preferLegacy);

        foreach (var candidate in candidates)
        {
            var isBareName = !candidate.Contains('\\') && !candidate.Contains('/') && !Path.IsPathRooted(candidate);

            if (!isBareName)
            {
                if (!File.Exists(candidate))
                {
                    logs.Add($"[FICHIER INTROUVABLE] {candidate}");
                    continue;
                }

                if (NativeLibrary.TryLoad(candidate, out var handle))
                {
                    logs.Add($"[CHARGÉ] {candidate}");
                    return new TwainDsmLoadResult(
                        Success: true,
                        Handle: handle,
                        LoadedPath: candidate,
                        AttemptLogs: logs,
                        DiagnosticSummary: string.Join(Environment.NewLine, logs));
                }

                logs.Add($"[ÉCHEC DE CHARGEMENT] {candidate} (fichier présent sur le disque mais NativeLibrary.TryLoad a échoué ; architecture incompatible x86/x64 ou dépendance native manquante)");
            }
            else
            {
                if (NativeLibrary.TryLoad(candidate, out var handle))
                {
                    logs.Add($"[CHARGÉ] {candidate} (via résolution standard de bibliothèques système)");
                    return new TwainDsmLoadResult(
                        Success: true,
                        Handle: handle,
                        LoadedPath: candidate,
                        AttemptLogs: logs,
                        DiagnosticSummary: string.Join(Environment.NewLine, logs));
                }

                logs.Add($"[NON LOCALISÉ] {candidate} (recherche système standard/PATH)");
            }
        }

        var summary = BuildDiagnosticSummary(logs);
        return new TwainDsmLoadResult(
            Success: false,
            Handle: IntPtr.Zero,
            LoadedPath: null,
            AttemptLogs: logs,
            DiagnosticSummary: summary);
    }

    /// <summary>
    /// Construit un rapport d'erreur complet avec l'architecture du processus et les conseils de résolution.
    /// </summary>
    public static string BuildDiagnosticSummary(IReadOnlyList<string> attemptLogs)
    {
        var is64BitProc = Environment.Is64BitProcess;
        var procBitness = is64BitProc ? "64 bits (x64)" : "32 bits (x86)";
        var osBitness = Environment.Is64BitOperatingSystem ? "64 bits" : "32 bits";
        var procPath = Environment.ProcessPath ?? "Inconnu";
        var procArch = RuntimeInformation.ProcessArchitecture.ToString();
        var osArch = RuntimeInformation.OSArchitecture.ToString();

        var sb = new StringBuilder();
        sb.AppendLine("Impossible de localiser ou charger le gestionnaire de sources TWAIN ('twaindsm.dll' ou 'twain_32.dll').");
        sb.AppendLine($"Contexte d'exécution du worker : Processus={procBitness} [{procArch}], OS={osBitness} [{osArch}], Exe='{procPath}'");
        sb.AppendLine($"Emplacements explorés ({attemptLogs.Count} candidats) :");
        foreach (var log in attemptLogs)
        {
            sb.AppendLine($"  - {log}");
        }

        sb.AppendLine("Diagnostics et solutions recommandées :");
        if (is64BitProc)
        {
            sb.AppendLine("  ! ATTENTION : Le processus worker s'exécute actuellement en 64 bits (x64).");
            sb.AppendLine("    La quasi-totalité des pilotes TWAIN des fabricants (notamment PaperStream IP pour Fujitsu fi-7030)");
            sb.AppendLine("    ainsi que le DSM Windows 'twain_32.dll' sont des bibliothèques strictement 32 bits (x86).");
            sb.AppendLine("    Un processus 64 bits ne peut PAS charger de DLL 32 bits.");
            sb.AppendLine("    Le worker doit impérativement cibler <PlatformTarget>x86</PlatformTarget>.");
        }
        else
        {
            sb.AppendLine("  - Le processus worker s'exécute bien en 32 bits (x86), compatible avec les pilotes TWAIN 32-bit.");
            sb.AppendLine("  - Vérifiez qu'un DSM TWAIN est installé :");
            sb.AppendLine("    * Soit le TWAIN DSM officiel 32-bit (twaindsm.dll dans C:\\Windows\\SysWOW64)");
            sb.AppendLine("    * Soit le DSM Windows standard (twain_32.dll dans C:\\Windows)");
        }
        sb.AppendLine($"  - Vous pouvez forcer un chemin de DLL personnalisé avec la variable d'environnement {DsmPathEnvVar}.");
        sb.AppendLine($"  - Vous pouvez forcer la priorité sur le DSM legacy 1.x avec {PreferLegacyEnvVar}=1.");

        return sb.ToString().TrimEnd();
    }

    private static bool IsLegacyPreferredFromEnvironment()
    {
        var val = Environment.GetEnvironmentVariable(PreferLegacyEnvVar);
        return string.Equals(val, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(val, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombinePath(string dir, params string[] parts)
    {
        var sep = dir.Contains('\\') ? '\\' : Path.DirectorySeparatorChar;
        var trimmedDir = dir.TrimEnd('\\', '/');
        var joined = string.Join(sep.ToString(), parts.Select(p => p.Trim('\\', '/')));
        return $"{trimmedDir}{sep}{joined}";
    }
}

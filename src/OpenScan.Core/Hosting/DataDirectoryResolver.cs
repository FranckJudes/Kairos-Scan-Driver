namespace OpenScan.Core.Hosting;

/// <summary>
/// Résout l'emplacement du répertoire de données persistant (SQLite, pages numérisées, verrou mono-instance).
/// </summary>
public static class DataDirectoryResolver
{
    public const string EnvironmentVariableName = "OPENSCAN_DATA_DIR";
    public const string DefaultLinuxSystemPath = "/var/lib/openscan";

    /// <summary>
    /// Résout le chemin du répertoire de données selon l'ordre de priorité :
    /// 1. Variable d'environnement OPENSCAN_DATA_DIR
    /// 2. Valeur explicite de configuration (appsettings.json)
    /// 3. Emplacement système par défaut (%ProgramData%\OpenScan sous Windows, /var/lib/openscan sous Linux si accessible en écriture)
    /// 4. Repli local sur &lt;baseDirectory&gt;/data (pour le développement local dotnet run)
    /// </summary>
    public static string Resolve(
        string? configuredPath = null,
        Func<string, string?>? getEnvironmentVariable = null,
        string? baseDirectory = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        baseDirectory ??= AppContext.BaseDirectory;

        // 1. Variable d'environnement prioritaire
        var envDir = getEnvironmentVariable(EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            return Path.GetFullPath(envDir.Trim());
        }

        // 2. Configuration explicite
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath.Trim());
        }

        // 3. Emplacement système par défaut
        if (OperatingSystem.IsWindows())
        {
            var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(commonAppData))
            {
                return Path.Combine(commonAppData, "OpenScan");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            // Vérifier si /var/lib/openscan est accessible en écriture (ex: service systemd avec utilisateur openscan)
            if (TryEnsureWritableDirectory(DefaultLinuxSystemPath))
            {
                return DefaultLinuxSystemPath;
            }
        }

        // 4. Repli pour développement local ou environnement restreint
        return Path.Combine(baseDirectory, "data");
    }

    /// <summary>
    /// Vérifie la capacité à créer le répertoire et à y écrire un fichier.
    /// </summary>
    public static bool TryEnsureWritableDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            var testFile = Path.Combine(path, $".write_test_{Guid.NewGuid():n}");
            using (File.Create(testFile, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }
}

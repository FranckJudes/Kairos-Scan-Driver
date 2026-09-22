using System.Diagnostics.CodeAnalysis;

namespace OpenScan.Core.Hosting;

/// <summary>
/// Verrou garantissant l'exécution d'une seule instance de l'Agent OpenScan.
/// Empêche les conflits de ports HTTP/WebSocket et la corruption de la base SQLite.
/// </summary>
public static class SingleInstanceGuard
{
    public const string DefaultWindowsMutexName = @"Global\OpenScanAgent";
    public const string DefaultLinuxLockFileName = "openscan-agent.lock";

    /// <summary>
    /// Tente d'acquérir le verrou d'instance unique pour la plateforme courante.
    /// </summary>
    /// <param name="targetIdentifierOrPath">
    /// Sous Windows : nom du Mutex (ex: @"Global\OpenScanAgent").
    /// Sous Linux : chemin complet du fichier de verrou (ex: "/var/lib/openscan/openscan-agent.lock").
    /// </param>
    /// <param name="lockHandle">Handle jetable relâchant le verrou à sa libération (Dispose).</param>
    /// <returns>True si le verrou a été acquis avec succès, False si une autre instance détient déjà le verrou.</returns>
    public static bool TryAcquire(string targetIdentifierOrPath, [NotNullWhen(true)] out IDisposable? lockHandle)
    {
        lockHandle = null;

        if (OperatingSystem.IsWindows())
        {
            return TryAcquireWindowsMutex(targetIdentifierOrPath, out lockHandle);
        }

        return TryAcquireFileLock(targetIdentifierOrPath, out lockHandle);
    }

    /// <summary>
    /// Acquisition de verrou fichier (utilisé sous Linux/macOS ou pour les tests multiplateformes).
    /// </summary>
    public static bool TryAcquireFileLock(string filePath, [NotNullWhen(true)] out IDisposable? lockHandle)
    {
        lockHandle = null;

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!OperatingSystem.IsMacOS())
            {
                try
                {
                    stream.Lock(0, 0);
                }
                catch (Exception)
                {
                    stream.Dispose();
                    return false;
                }
            }

            lockHandle = new FileLockHandle(stream);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Acquisition de verrou via Mutex nommé (utilisé sous Windows).
    /// </summary>
    public static bool TryAcquireWindowsMutex(string mutexName, [NotNullWhen(true)] out IDisposable? lockHandle)
    {
        lockHandle = null;

        try
        {
            var mutex = new Mutex(false, mutexName, out _);
            bool hasHandle;
            try
            {
                hasHandle = mutex.WaitOne(0, false);
            }
            catch (AbandonedMutexException)
            {
                // Le propriétaire précédent a terminé brutalement sans libérer le Mutex -> le verrou nous est accordé
                hasHandle = true;
            }

            if (!hasHandle)
            {
                mutex.Dispose();
                return false;
            }

            lockHandle = new MutexLockHandle(mutex);
            return true;
        }
        catch (UnauthorizedAccessException) when (mutexName.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase))
        {
            // Repli sur Local\ si les privilèges administrateur manquent (ex: session non-élevée)
            var localName = mutexName.Replace(@"Global\", @"Local\");
            return TryAcquireWindowsMutex(localName, out lockHandle);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class FileLockHandle(FileStream stream) : IDisposable
    {
        private FileStream? _stream = stream;

        public void Dispose()
        {
            var s = Interlocked.Exchange(ref _stream, null);
            if (s != null)
            {
                if (!OperatingSystem.IsMacOS())
                {
                    try
                    {
                        s.Unlock(0, 0);
                    }
                    catch
                    {
                        // Ignorer les erreurs de déverrouillage lors de la fermeture
                    }
                }

                s.Dispose();
            }
        }
    }

    private sealed class MutexLockHandle(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            var m = Interlocked.Exchange(ref _mutex, null);
            if (m != null)
            {
                try
                {
                    m.ReleaseMutex();
                }
                catch
                {
                    // Ignorer si déjà libéré ou abandonné
                }

                m.Dispose();
            }
        }
    }
}

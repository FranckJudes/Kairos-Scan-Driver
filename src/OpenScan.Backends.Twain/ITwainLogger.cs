namespace OpenScan.Backends.Twain;

/// <summary>
/// Abstraction de journalisation pour le backend TWAIN, découplée des frameworks tiers.
/// Permet l'injection de ILogger dans l'Agent ASP.NET Core et la capture dans les tests unitaires.
/// </summary>
public interface ITwainLogger
{
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? ex = null);
}

/// <summary>
/// Implémentation par défaut écrivant sur stdout/stderr pour diagnostic immédiat en console.
/// </summary>
public sealed class ConsoleTwainLogger : ITwainLogger
{
    public static readonly ConsoleTwainLogger Instance = new();

    public void LogInfo(string message) =>
        Console.WriteLine($"[TWAIN INFO] {message}");

    public void LogWarning(string message) =>
        Console.Error.WriteLine($"[TWAIN WARN] {message}");

    public void LogError(string message, Exception? ex = null) =>
        Console.Error.WriteLine($"[TWAIN ERROR] {message}{(ex != null ? $" | Exception: {ex}" : "")}");
}

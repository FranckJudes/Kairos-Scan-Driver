namespace OpenScan.Backends.Escl;

/// <summary>
/// Options de configuration du backend eSCL / AirScan.
/// </summary>
public sealed class EsclOptions
{
    public const string SectionName = "Escl";

    /// <summary>
    /// Liste des adresses IP, noms d'hôte ou URL des scanners eSCL connus (ex: "192.168.1.50", "printer.local:8080").
    /// </summary>
    public List<string> KnownDevices { get; set; } = [];

    /// <summary>
    /// Délai d'attente maximal pour les requêtes HTTP adressées aux scanners (par défaut 15 secondes).
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
}

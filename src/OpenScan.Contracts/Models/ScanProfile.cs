namespace OpenScan.Contracts;

/// <summary>Ce que l'utilisateur souhaite (section 7.3 / section 11).</summary>
public sealed class ScanProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = "adf";
    public bool Duplex { get; set; }
    public int Resolution { get; set; } = 300;
    public string PixelFormat { get; set; } = "color";
    public int? BitDepth { get; set; }
    // "auto" laisse le scanner détecter la taille réelle du document posé/chargé. Une valeur
    // fixe ("a4", "letter", ...) force le pilote à capturer une zone physique de cette taille
    // exacte quelle que soit la longueur réelle du document — s'il est plus court, le reste de
    // la zone est scanné quand même, ce qui produit une bande blanche en bas de la page (backend
    // TWAIN, voir TwainCapabilityMapper.MapToTwainPaperSize).
    public string PageSize { get; set; } = "auto";
    public string Orientation { get; set; } = "auto";
    public int? Brightness { get; set; }
    public int? Contrast { get; set; }
    public string Feeder { get; set; } = "auto";
    public bool BlankPageDetection { get; set; }
    public bool Deskew { get; set; }
    public string Crop { get; set; } = "none";
    public string? Compression { get; set; }
    public string OutputFormat { get; set; } = "pdf";
}

/// <summary>Un ajustement effectué entre la demande et la configuration réelle (R3, R4).</summary>
public sealed class ConfigurationAdjustment
{
    public string Parameter { get; set; } = string.Empty;
    public object? Requested { get; set; }
    public object? Effective { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Résultat de la validation du profil contre les capacités (section 7.4).</summary>
public sealed class EffectiveConfiguration
{
    public Dictionary<string, object?> Requested { get; set; } = [];
    public Dictionary<string, object?> Effective { get; set; } = [];
    public List<ConfigurationAdjustment> Adjustments { get; set; } = [];
}

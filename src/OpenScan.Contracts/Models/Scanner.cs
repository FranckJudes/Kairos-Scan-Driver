namespace OpenScan.Contracts;

/// <summary>Scanner découvert (section 7.1).</summary>
public sealed class Scanner
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string Backend { get; set; } = string.Empty;
    public ConnectionType ConnectionType { get; set; } = ConnectionType.Unknown;
    public ScannerStatus Status { get; set; } = ScannerStatus.Unknown;
    public ScannerCapabilities? Capabilities { get; set; }
}

/// <summary>Taille de page physique en 1/100 mm.</summary>
public sealed class PageSize
{
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>Ce que le scanner/backend est réellement capable de faire (section 7.2).</summary>
public sealed class ScannerCapabilities
{
    public List<string> Sources { get; set; } = [];
    public bool Duplex { get; set; }
    public List<int> Resolutions { get; set; } = [];
    public List<string> PixelFormats { get; set; } = [];
    public List<int> BitDepths { get; set; } = [];
    public List<string> PaperSizes { get; set; } = [];
    public bool Adf { get; set; }
    public bool Flatbed { get; set; }
    public PageSize? MaxPageSize { get; set; }
    public PageSize? MinPageSize { get; set; }
}

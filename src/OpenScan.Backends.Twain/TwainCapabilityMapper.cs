using OpenScan.Contracts;

namespace OpenScan.Backends.Twain;

/// <summary>
/// Représentation brute des capacités retournées par un pilote TWAIN, avant normalisation.
/// Permet de tester toute la logique de mapping sans instancier de session TWAIN réelle.
/// </summary>
public sealed class TwainRawCapabilities
{
    public bool HasFeeder { get; set; }
    public bool HasFlatbed { get; set; } = true;
    public bool HasDuplex { get; set; }
    public List<float> DiscreteResolutions { get; set; } = [];
    public (float Min, float Max, float Step)? ResolutionRange { get; set; }
    public List<int> PixelTypes { get; set; } = []; // 0=BW, 1=Gray, 2=RGB
    public List<int> SupportedSizes { get; set; } = []; // 1=A4, 3=Letter, 11=A3, etc.
    public (double WidthMm, double HeightMm)? MaxPhysicalSizeMm { get; set; }
}

/// <summary>
/// Mapping pur des conteneurs et valeurs TWAIN 2.x vers ScannerCapabilities.
/// Respecte la règle R2 (aucune logique fabricant en dur).
/// </summary>
public static class TwainCapabilityMapper
{
    // Constantes standard TWAIN ICAP_PIXELTYPE
    public const int TWPT_BW = 0;
    public const int TWPT_GRAY = 1;
    public const int TWPT_RGB = 2;
    public const int TWPT_PALETTE = 3;
    public const int TWPT_CMY = 4;
    public const int TWPT_CMYK = 5;

    // Constantes standard TWAIN ICAP_SUPPORTEDSIZES
    public const int TWSS_NONE = 0;
    public const int TWSS_A4 = 1;
    public const int TWSS_JISB5 = 2;
    public const int TWSS_USLETTER = 3;
    public const int TWSS_USLEGAL = 4;
    public const int TWSS_A5 = 5;
    public const int TWSS_B4 = 6;
    public const int TWSS_B6 = 7;
    public const int TWSS_USLEDGER = 9;
    public const int TWSS_USEXECUTIVE = 10;
    public const int TWSS_A3 = 11;

    private static readonly int[] CommonResolutions = [75, 100, 150, 200, 240, 300, 400, 600, 1200, 2400];

    public static ScannerCapabilities Map(TwainRawCapabilities raw)
    {
        var caps = new ScannerCapabilities();

        // Sources
        if (raw.HasFeeder)
        {
            caps.Sources.Add("adf");
            caps.Adf = true;
        }

        if (raw.HasFlatbed)
        {
            caps.Sources.Add("flatbed");
            caps.Flatbed = true;
        }

        // Si aucune source explicitement annoncée, flatbed par défaut
        if (caps.Sources.Count == 0)
        {
            caps.Sources.Add("flatbed");
            caps.Flatbed = true;
        }

        caps.Duplex = raw.HasDuplex;

        // Résolutions
        if (raw.DiscreteResolutions.Count > 0)
        {
            caps.Resolutions = raw.DiscreteResolutions
                .Select(r => (int)Math.Round(r))
                .Where(r => r > 0)
                .Distinct()
                .OrderBy(r => r)
                .ToList();
        }
        else if (raw.ResolutionRange.HasValue)
        {
            var (min, max, _) = raw.ResolutionRange.Value;
            var inRange = CommonResolutions.Where(r => r >= min && r <= max).ToList();
            caps.Resolutions = inRange.Count > 0 ? inRange : [(int)Math.Round(min), (int)Math.Round(max)];
        }
        else
        {
            caps.Resolutions = [150, 200, 300, 600];
        }

        // Modes pixel et profondeurs de bits
        foreach (var pt in raw.PixelTypes.Distinct())
        {
            switch (pt)
            {
                case TWPT_BW:
                    caps.PixelFormats.Add("bw");
                    caps.BitDepths.Add(1);
                    break;
                case TWPT_GRAY:
                    caps.PixelFormats.Add("grayscale");
                    caps.BitDepths.Add(8);
                    break;
                case TWPT_RGB:
                    caps.PixelFormats.Add("color");
                    caps.BitDepths.Add(24);
                    break;
            }
        }

        if (caps.PixelFormats.Count == 0)
        {
            caps.PixelFormats.AddRange(["color", "grayscale", "bw"]);
            caps.BitDepths.AddRange([24, 8, 1]);
        }

        caps.PixelFormats = caps.PixelFormats.Distinct().ToList();
        caps.BitDepths = caps.BitDepths.Distinct().OrderBy(b => b).ToList();

        // Tailles de papier supportées
        foreach (var size in raw.SupportedSizes.Distinct())
        {
            var name = MapPaperSize(size);
            if (name is not null)
            {
                caps.PaperSizes.Add(name);
            }
        }

        if (caps.PaperSizes.Count == 0)
        {
            caps.PaperSizes.AddRange(["a4", "letter"]);
        }

        caps.PaperSizes = caps.PaperSizes.Distinct().ToList();

        // Dimensions physiques
        if (raw.MaxPhysicalSizeMm.HasValue)
        {
            caps.MaxPageSize = new PageSize
            {
                Width = (int)Math.Round(raw.MaxPhysicalSizeMm.Value.WidthMm * 100), // 1/100 mm
                Height = (int)Math.Round(raw.MaxPhysicalSizeMm.Value.HeightMm * 100)
            };
        }

        return caps;
    }

    public static string? MapPaperSize(int twainSize) =>
        twainSize switch
        {
            TWSS_A4 => "a4",
            TWSS_USLETTER => "letter",
            TWSS_USLEGAL => "legal",
            TWSS_A3 => "a3",
            TWSS_A5 => "a5",
            TWSS_B4 => "b4",
            TWSS_B6 => "b6",
            TWSS_USLEDGER => "ledger",
            TWSS_USEXECUTIVE => "executive",
            TWSS_NONE => "auto",
            _ => null
        };

    public static int? MapToTwainPaperSize(string standardSize) =>
        standardSize.ToLowerInvariant() switch
        {
            "a4" => TWSS_A4,
            "letter" => TWSS_USLETTER,
            "legal" => TWSS_USLEGAL,
            "a3" => TWSS_A3,
            "a5" => TWSS_A5,
            "b4" => TWSS_B4,
            "b6" => TWSS_B6,
            "ledger" => TWSS_USLEDGER,
            "executive" => TWSS_USEXECUTIVE,
            "auto" => TWSS_NONE,
            _ => null
        };

    public static int MapToTwainPixelType(string pixelFormat) =>
        pixelFormat.ToLowerInvariant() switch
        {
            "bw" or "monochrome" or "lineart" => TWPT_BW,
            "gray" or "grayscale" => TWPT_GRAY,
            _ => TWPT_RGB
        };
}

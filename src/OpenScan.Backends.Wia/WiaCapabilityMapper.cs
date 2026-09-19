namespace OpenScan.Backends.Wia;


public static class WiaCapabilityMapper
{
    public const int FeederFlag = 0x1;
    public const int FlatbedFlag = 0x2;
    public const int DuplexFlag = 0x4;

    public const int IntentText = 0x1;
    public const int IntentGrayscale = 0x2;
    public const int IntentColor = 0x4;

    private static readonly int[] CommonResolutions = [75, 100, 150, 200, 240, 300, 400, 600, 1200, 2400, 4800];

    public static (bool Adf, bool Flatbed, bool Duplex) MapDocumentHandling(int capabilitiesBitmask) => (
        Adf: (capabilitiesBitmask & FeederFlag) != 0,
        Flatbed: (capabilitiesBitmask & FlatbedFlag) != 0,
        Duplex: (capabilitiesBitmask & DuplexFlag) != 0);

    public static IReadOnlyList<string> MapSources(int capabilitiesBitmask)
    {
        var (adf, flatbed, _) = MapDocumentHandling(capabilitiesBitmask);
        var sources = new List<string>();
        if (adf)
        {
            sources.Add("adf");
        }

        if (flatbed)
        {
            sources.Add("flatbed");
        }

        return sources;
    }

    public static int ResolveDocumentHandlingSelect(string requestedSource, bool duplex)
    {
        if (requestedSource.Equals("flatbed", StringComparison.OrdinalIgnoreCase))
        {
            return FlatbedFlag;
        }

        return duplex ? FeederFlag | DuplexFlag : FeederFlag;
    }

    public static int ResolveIntent(string pixelFormat) => pixelFormat.ToLowerInvariant() switch
    {
        "bw" => IntentText,
        "grayscale" => IntentGrayscale,
        "color" => IntentColor,
        _ => IntentColor
    };

    public static string NormalizePixelFormat(int intent) => intent switch
    {
        IntentText => "bw",
        IntentGrayscale => "grayscale",
        IntentColor => "color",
        _ => "color"
    };


    public static IReadOnlyList<int> ExpandResolutionRange(int min, int max, int step)
    {
        if (step <= 0)
        {
            return min == max ? [min] : [min, max];
        }

        var withinRange = CommonResolutions.Where(r => r >= min && r <= max && (r - min) % step == 0).ToList();
        return withinRange.Count > 0 ? withinRange : [min, max];
    }
}

using OpenScan.Contracts;

namespace OpenScan.Core;


public static class ConfigurationEngine
{
    public static EffectiveConfiguration Validate(ScanProfile requested, ScannerCapabilities capabilities)
    {
        var requestedDict = new Dictionary<string, object?>();
        var effectiveDict = new Dictionary<string, object?>();
        var adjustments = new List<ConfigurationAdjustment>();

        requestedDict["source"] = requested.Source;
        if (capabilities.Sources.Count > 0 && !capabilities.Sources.Contains(requested.Source, StringComparer.OrdinalIgnoreCase))
        {
            throw new ScanEngineException(ErrorCode.CapabilityUnsupported, $"La source '{requested.Source}' n'est pas supportée par ce scanner.");
        }
        effectiveDict["source"] = requested.Source;

        requestedDict["duplex"] = requested.Duplex;
        var effectiveDuplex = requested.Duplex && capabilities.Duplex;
        effectiveDict["duplex"] = effectiveDuplex;
        if (requested.Duplex && !effectiveDuplex)
        {
            adjustments.Add(new ConfigurationAdjustment
            {
                Parameter = "duplex",
                Requested = true,
                Effective = false,
                Reason = "unsupported_by_scanner"
            });
        }

        var effectiveResolution = NearestSupported(requested.Resolution, capabilities.Resolutions);
        requestedDict["resolution"] = requested.Resolution;
        effectiveDict["resolution"] = effectiveResolution;
        if (effectiveResolution != requested.Resolution)
        {
            adjustments.Add(new ConfigurationAdjustment
            {
                Parameter = "resolution",
                Requested = requested.Resolution,
                Effective = effectiveResolution,
                Reason = "nearest_supported"
            });
        }

        requestedDict["pixel_format"] = requested.PixelFormat;
        if (capabilities.PixelFormats.Count > 0 && !capabilities.PixelFormats.Contains(requested.PixelFormat, StringComparer.OrdinalIgnoreCase))
        {
            throw new ScanEngineException(ErrorCode.CapabilityUnsupported, $"Le mode couleur '{requested.PixelFormat}' n'est pas supporté par ce scanner.");
        }
        effectiveDict["pixel_format"] = requested.PixelFormat;

        requestedDict["page_size"] = requested.PageSize;
        effectiveDict["page_size"] = requested.PageSize;

        requestedDict["output_format"] = requested.OutputFormat;
        effectiveDict["output_format"] = requested.OutputFormat;

        return new EffectiveConfiguration
        {
            Requested = requestedDict,
            Effective = effectiveDict,
            Adjustments = adjustments
        };
    }

    private static int NearestSupported(int requested, List<int> supported)
    {
        if (supported.Count == 0)
        {
            return requested;
        }

        return supported.OrderBy(candidate => Math.Abs(candidate - requested)).First();
    }
}

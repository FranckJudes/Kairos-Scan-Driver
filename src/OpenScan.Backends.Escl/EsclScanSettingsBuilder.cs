using System.Text.Json;
using System.Xml.Linq;
using OpenScan.Contracts;

namespace OpenScan.Backends.Escl;

/// <summary>
/// Paramètres résolus pour la création d'un job eSCL.
/// </summary>
public sealed record EsclJobParameters(
    string Source,
    string ColorMode,
    int Resolution,
    bool Duplex,
    string DocumentFormat = "image/jpeg");

/// <summary>
/// Constructeur pur du document XML ScanSettings pour l'endpoint POST /eSCL/ScanJobs (section 3.1).
/// </summary>
public static class EsclScanSettingsBuilder
{
    public const string ScanNamespace = "http://schemas.hp.com/imaging/escl/2011/05/03";
    public const string PwgNamespace = "http://www.pwg.org/schemas/2010/12/sm";

    public static EsclJobParameters ExtractParameters(EffectiveConfiguration configuration)
    {
        var requestedSource = AsString(configuration.Effective.GetValueOrDefault("source"), "adf");
        var duplex = AsBool(configuration.Effective.GetValueOrDefault("duplex"));
        var resolution = AsInt(configuration.Effective.GetValueOrDefault("resolution"), 300);
        var pixelFormat = AsString(configuration.Effective.GetValueOrDefault("pixel_format"), "color");

        var isAdf = requestedSource.Equals("adf", StringComparison.OrdinalIgnoreCase);
        var inputSource = isAdf ? "Feeder" : "Platen";

        var colorMode = pixelFormat.ToLowerInvariant() switch
        {
            "bw" or "blackandwhite" or "lineart" => "BlackAndWhite1",
            "grayscale" or "gray" => "Grayscale8",
            _ => "RGB24"
        };

        return new EsclJobParameters(
            Source: inputSource,
            ColorMode: colorMode,
            Resolution: resolution,
            Duplex: isAdf && duplex,
            DocumentFormat: "image/jpeg");
    }

    public static string BuildXml(EsclJobParameters parameters)
    {
        XNamespace scan = ScanNamespace;
        XNamespace pwg = PwgNamespace;

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(scan + "ScanSettings",
                new XAttribute(XNamespace.Xmlns + "scan", ScanNamespace),
                new XAttribute(XNamespace.Xmlns + "pwg", PwgNamespace),
                new XElement(pwg + "Version", "2.0"),
                new XElement(pwg + "InputSource", parameters.Source),
                new XElement(scan + "ColorMode", parameters.ColorMode),
                new XElement(scan + "XResolution", parameters.Resolution),
                new XElement(scan + "YResolution", parameters.Resolution),
                new XElement(pwg + "DocumentFormat", parameters.DocumentFormat),
                new XElement(scan + "Duplex", parameters.Duplex ? "true" : "false")
            )
        );

        return doc.ToString();
    }

    private static string AsString(object? value, string fallback)
    {
        // EffectiveConfiguration.Effective rechargé depuis SQLite (ex. resume()/retry(), voir
        // SqliteJobRepository) désérialise les valeurs en JsonElement, pas en string natif —
        // sans ce cas, la config demandée est silencieusement ignorée après une reprise.
        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } s
                ? s
                : fallback;
        }

        return value is string str && !string.IsNullOrWhiteSpace(str) ? str : fallback;
    }

    private static bool AsBool(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.True;
        }

        return value is true;
    }

    private static int AsInt(object? value, int fallback)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var i) ? i : fallback;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            _ => fallback
        };
    }
}

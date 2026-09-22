using System.Xml.Linq;
using OpenScan.Contracts;

namespace OpenScan.Backends.Escl;

/// <summary>
/// Informations extraites du document XML ScannerCapabilities d'un périphérique eSCL.
/// </summary>
public sealed record EsclParsedCapabilities(
    string? MakeAndModel,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    ScannerCapabilities Capabilities);

/// <summary>
/// Parser tolérant pour les réponses XML ScannerCapabilities (section 3.1).
/// Ignore les namespaces stricts pour s'adapter aux légères variantes des constructeurs (HP, Canon, Epson, Brother...).
/// </summary>
public static class EsclCapabilitiesParser
{
    public static EsclParsedCapabilities Parse(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            throw new ArgumentException("Le contenu XML ne peut pas être vide.", nameof(xmlContent));
        }

        var doc = XDocument.Parse(xmlContent);

        var makeAndModel = FindValue(doc, "MakeAndModel") ?? FindValue(doc, "DeviceModel") ?? FindValue(doc, "DeviceName");
        string? manufacturer = null;
        string? model = null;

        if (!string.IsNullOrEmpty(makeAndModel))
        {
            var parts = makeAndModel.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                manufacturer = parts[0];
                model = parts[1];
            }
            else
            {
                manufacturer = makeAndModel;
                model = makeAndModel;
            }
        }

        var serialNumber = FindValue(doc, "SerialNumber");

        // 1. Détection des sources physiques (Platen / Feeder-ADF / Duplex)
        var hasPlaten = FindElements(doc, "Platen").Any() || FindElements(doc, "PlatenInputCaps").Any();
        var hasAdf = FindElements(doc, "Adf").Any() || FindElements(doc, "AdfSimplexInputCaps").Any();
        var hasDuplex = FindElements(doc, "AdfDuplexInputCaps").Any();

        var sources = new List<string>();
        if (hasAdf)
        {
            sources.Add("adf");
        }
        if (hasPlaten || (!hasPlaten && !hasAdf))
        {
            sources.Add("flatbed");
        }

        // 2. Détection des résolutions discrètes
        var resolutions = FindElements(doc, "XResolution")
            .Select(e => int.TryParse(e.Value, out var v) ? v : 0)
            .Where(v => v > 0)
            .Distinct()
            .OrderBy(v => v)
            .ToList();

        if (resolutions.Count == 0)
        {
            resolutions = [150, 200, 300, 600];
        }

        // 3. Détection des modes de couleur et profondeurs de bits
        var colorModes = FindElements(doc, "ColorMode")
            .Select(e => e.Value.Trim())
            .ToList();

        var pixelFormats = new List<string>();
        var bitDepths = new List<int>();

        if (colorModes.Any(c => c.Contains("RGB", StringComparison.OrdinalIgnoreCase) || c.Contains("Color", StringComparison.OrdinalIgnoreCase)))
        {
            pixelFormats.Add("color");
            bitDepths.Add(24);
        }

        if (colorModes.Any(c => c.Contains("Gray", StringComparison.OrdinalIgnoreCase)))
        {
            pixelFormats.Add("grayscale");
            bitDepths.Add(8);
        }

        if (colorModes.Any(c => c.Contains("Black", StringComparison.OrdinalIgnoreCase) || c.Contains("BW", StringComparison.OrdinalIgnoreCase) || c.Contains("Binary", StringComparison.OrdinalIgnoreCase)))
        {
            pixelFormats.Add("bw");
            bitDepths.Add(1);
        }

        if (pixelFormats.Count == 0)
        {
            pixelFormats = ["color", "grayscale", "bw"];
            bitDepths = [1, 8, 24];
        }

        // 4. Détection des dimensions maximales (en unités 1/300 pouce selon spec eSCL -> conversion en 1/100 mm)
        var maxWidthStr = FindValue(doc, "MaxWidth");
        var maxHeightStr = FindValue(doc, "MaxHeight");

        var maxWidth100mm = 21590; // Largeur US Letter par défaut (215.9 mm)
        var maxHeight100mm = 29700; // Hauteur A4 par défaut (297.0 mm)

        if (int.TryParse(maxWidthStr, out var maxWidth300) && maxWidth300 > 0)
        {
            maxWidth100mm = (int)Math.Round(maxWidth300 * 2540.0 / 300.0);
        }

        if (int.TryParse(maxHeightStr, out var maxHeight300) && maxHeight300 > 0)
        {
            maxHeight100mm = (int)Math.Round(maxHeight300 * 2540.0 / 300.0);
        }

        var paperSizes = new List<string> { "a4", "letter" };
        if (maxWidth100mm >= 29700 && maxHeight100mm >= 42000)
        {
            paperSizes.Add("a3");
        }

        var capabilities = new ScannerCapabilities
        {
            Sources = sources,
            Duplex = hasDuplex,
            Resolutions = resolutions,
            PixelFormats = pixelFormats,
            BitDepths = bitDepths,
            PaperSizes = paperSizes,
            Adf = hasAdf,
            Flatbed = hasPlaten || (!hasPlaten && !hasAdf),
            MaxPageSize = new PageSize { Width = maxWidth100mm, Height = maxHeight100mm },
            MinPageSize = new PageSize { Width = 5000, Height = 5000 }
        };

        return new EsclParsedCapabilities(makeAndModel, manufacturer, model, serialNumber, capabilities);
    }

    private static IEnumerable<XElement> FindElements(XContainer container, string localName) =>
        container.Descendants().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static string? FindValue(XContainer container, string localName) =>
        FindElements(container, localName).FirstOrDefault()?.Value?.Trim();
}

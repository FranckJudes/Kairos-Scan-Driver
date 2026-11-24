using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenScan.Backends.Sane;


public sealed record SaneDeviceOptions(
    IReadOnlyList<string> SourceValues,
    IReadOnlyList<string> ModeValues,
    IReadOnlyList<int> Resolutions,
    double? MaxPageWidthMm,
    double? MaxPageHeightMm,
    bool SupportsSoftwareCrop);


public static class SaneOptionsParser
{
    private static readonly int[] CommonResolutions = [75, 100, 150, 200, 240, 300, 400, 600, 1200, 2400, 4800];

    public static SaneDeviceOptions Parse(string helpOutput)
    {
        var sourceValues = ParseEnumOption(helpOutput, "source");
        var modeValues = ParseEnumOption(helpOutput, "mode");
        var resolutions = ParseResolutions(helpOutput);
        var maxWidth = ParseMaxMillimeters(helpOutput, "page-width");
        var maxHeight = ParseMaxMillimeters(helpOutput, "page-height");
        var supportsSoftwareCrop = ParseBooleanOption(helpOutput, "swcrop");

        return new SaneDeviceOptions(sourceValues, modeValues, resolutions, maxWidth, maxHeight, supportsSoftwareCrop);
    }

    // Les options booléennes de scanimage --help s'affichent sous la forme
    // "--nom[=(yes|no)]   description [défaut]" — "swcrop" ("software crop") n'existe que sur
    // certains pilotes SANE (ex. genesys) et demande au pilote de détecter et recadrer
    // automatiquement les bords blancs du document dans la zone de scan. Il n'existe aucune
    // option universelle équivalente côté SANE (contrairement à TWSS_NONE en TWAIN ou
    // WIA_PAGE_AUTO en WIA) — il faut donc vérifier sa présence avant de s'en servir, sous peine
    // de faire échouer scanimage avec une option inconnue sur les pilotes qui ne l'exposent pas.
    private static bool ParseBooleanOption(string helpOutput, string optionName) =>
        Regex.IsMatch(helpOutput, $@"^\s*--{optionName}\[=\(yes\|no\)\]", RegexOptions.Multiline);

    private static IReadOnlyList<string> ParseEnumOption(string helpOutput, string optionName)
    {
        var match = Regex.Match(helpOutput, $@"^\s*--{optionName}\s+(?<values>.+?)\s*\[(?<default>[^\]]*)\]\s*$", RegexOptions.Multiline);
        if (!match.Success)
        {
            return [];
        }

        return match.Groups["values"].Value
            .Split('|')
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .ToList();
    }

    private static IReadOnlyList<int> ParseResolutions(string helpOutput)
    {
        var match = Regex.Match(helpOutput, @"^\s*--resolution\s+(?<values>.+?)\s*\[(?<default>[^\]]*)\]\s*$", RegexOptions.Multiline);
        if (!match.Success)
        {
            return [];
        }

        var values = match.Groups["values"].Value;

        var rangeMatch = Regex.Match(values, @"(?<min>\d+)\s*\.\.\s*(?<max>\d+)");
        if (rangeMatch.Success)
        {
            var min = int.Parse(rangeMatch.Groups["min"].Value, CultureInfo.InvariantCulture);
            var max = int.Parse(rangeMatch.Groups["max"].Value, CultureInfo.InvariantCulture);
            var withinRange = CommonResolutions.Where(r => r >= min && r <= max).ToList();
            return withinRange.Count > 0 ? withinRange : [min, max];
        }

        return values.Split('|')
            .Select(v => Regex.Match(v, @"\d+"))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture))
            .ToList();
    }

    private static double? ParseMaxMillimeters(string helpOutput, string optionName)
    {
        var match = Regex.Match(
            helpOutput,
            $@"^\s*--{optionName}\s+.*?\.\.\s*(?<max>[\d.]+)mm",
            RegexOptions.Multiline);

        return match.Success && double.TryParse(match.Groups["max"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}

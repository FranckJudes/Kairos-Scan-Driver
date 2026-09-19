using OpenScan.Backends.Sane;

namespace OpenScan.Tests;

public class SaneOptionsParserTests
{
    // Sortie réelle de `scanimage --help -d fujitsu:fi-7030:113727` (scanner ADF sans chargeur plat,
    // résolution exprimée comme un intervalle continu) : capturée pour figer le format attendu.
    private const string FujitsuFi7030HelpOutput = """
        Options specific to device `fujitsu:fi-7030:113727':
          Standard:
            --source ADF Front|ADF Back|ADF Duplex [ADF Front]
                Selects the scan source (such as a document-feeder).
            --mode Lineart|Halftone|Gray|Color [Lineart]
                Selects the scan mode (e.g., lineart, monochrome, or color).
            --resolution 50..600dpi (in steps of 1) [600]
                Sets the resolution of the scanned image.
          Geometry:
            --page-width 0..221.121mm (in steps of 0.0211639) [215.872]
                Specifies the width of the media.
            --page-height 0..2712.88mm (in steps of 0.0211639) [279.364]
                Specifies the height of the media.
        """;

    // Sortie synthétique représentative d'un scanner à plat avec résolutions discrètes (format
    // rencontré sur d'autres familles de backends SANE, ex. genesys/epson2).
    private const string FlatbedWithDiscreteResolutionsHelpOutput = """
        Options specific to device `genesys:libusb:001:006':
          Scan Mode:
            --source Flatbed|Automatic Document Feeder|ADF Duplex [Flatbed]
                Selects the scan source.
            --mode Lineart|Gray|Color [Color]
                Selects the scan mode.
            --resolution 75|100|150|200|300|600|1200dpi [300]
                Sets the resolution of the scanned image.
          Geometry:
            --page-width 0..215.9mm [215.9]
                Specifies the width of the media.
            --page-height 0..297.18mm [297.18]
                Specifies the height of the media.
        """;

    [Fact]
    public void Parse_AdfOnlyScanner_ExtractsSourcesAndModes()
    {
        var options = SaneOptionsParser.Parse(FujitsuFi7030HelpOutput);

        Assert.Equal(["ADF Front", "ADF Back", "ADF Duplex"], options.SourceValues);
        Assert.Equal(["Lineart", "Halftone", "Gray", "Color"], options.ModeValues);
    }

    [Fact]
    public void Parse_ContinuousResolutionRange_ExpandsToCommonResolutionsWithinRange()
    {
        var options = SaneOptionsParser.Parse(FujitsuFi7030HelpOutput);

        Assert.Contains(300, options.Resolutions);
        Assert.Contains(600, options.Resolutions);
        Assert.DoesNotContain(1200, options.Resolutions); // hors de la plage 50..600
        Assert.All(options.Resolutions, r => Assert.InRange(r, 50, 600));
    }

    [Fact]
    public void Parse_PageDimensions_ConvertsMaxMillimeters()
    {
        var options = SaneOptionsParser.Parse(FujitsuFi7030HelpOutput);

        Assert.NotNull(options.MaxPageWidthMm);
        Assert.NotNull(options.MaxPageHeightMm);
        Assert.Equal(221.121, options.MaxPageWidthMm!.Value, precision: 3);
        Assert.Equal(2712.88, options.MaxPageHeightMm!.Value, precision: 2);
    }

    [Fact]
    public void Parse_DiscreteResolutionList_ParsesExactValues()
    {
        var options = SaneOptionsParser.Parse(FlatbedWithDiscreteResolutionsHelpOutput);

        Assert.Equal([75, 100, 150, 200, 300, 600, 1200], options.Resolutions);
    }

    [Fact]
    public void Parse_FlatbedScanner_ExtractsFlatbedAndAdfSources()
    {
        var options = SaneOptionsParser.Parse(FlatbedWithDiscreteResolutionsHelpOutput);

        Assert.Equal(["Flatbed", "Automatic Document Feeder", "ADF Duplex"], options.SourceValues);
    }

    [Fact]
    public void Parse_UnknownDeviceOutput_ReturnsEmptyOptionsWithoutThrowing()
    {
        var options = SaneOptionsParser.Parse("some unrelated text with no options");

        Assert.Empty(options.SourceValues);
        Assert.Empty(options.ModeValues);
        Assert.Empty(options.Resolutions);
        Assert.Null(options.MaxPageWidthMm);
    }
}

using System.Xml.Linq;
using OpenScan.Backends.Escl;
using OpenScan.Contracts;
using Xunit;

namespace OpenScan.Tests;

public class EsclScanSettingsBuilderTests
{
    [Fact]
    public void ExtractParameters_AdfColorDuplex_ExtractsCorrectly()
    {
        var config = new EffectiveConfiguration
        {
            Effective = new Dictionary<string, object?>
            {
                ["source"] = "adf",
                ["duplex"] = true,
                ["resolution"] = 300,
                ["pixel_format"] = "color"
            }
        };

        var parameters = EsclScanSettingsBuilder.ExtractParameters(config);

        Assert.Equal("Feeder", parameters.Source);
        Assert.Equal("RGB24", parameters.ColorMode);
        Assert.Equal(300, parameters.Resolution);
        Assert.True(parameters.Duplex);
        Assert.Equal("image/jpeg", parameters.DocumentFormat);
    }

    [Fact]
    public void ExtractParameters_FlatbedGrayscale_DisablesDuplexAndMapsGrayscale8()
    {
        var config = new EffectiveConfiguration
        {
            Effective = new Dictionary<string, object?>
            {
                ["source"] = "flatbed",
                ["duplex"] = true, // Duplex n'est pas applicable au plateau
                ["resolution"] = 200,
                ["pixel_format"] = "grayscale"
            }
        };

        var parameters = EsclScanSettingsBuilder.ExtractParameters(config);

        Assert.Equal("Platen", parameters.Source);
        Assert.Equal("Grayscale8", parameters.ColorMode);
        Assert.Equal(200, parameters.Resolution);
        Assert.False(parameters.Duplex);
    }

    [Fact]
    public void ExtractParameters_BlackAndWhite_MapsToBlackAndWhite1()
    {
        var config = new EffectiveConfiguration
        {
            Effective = new Dictionary<string, object?>
            {
                ["source"] = "adf",
                ["pixel_format"] = "bw",
                ["resolution"] = 600
            }
        };

        var parameters = EsclScanSettingsBuilder.ExtractParameters(config);

        Assert.Equal("BlackAndWhite1", parameters.ColorMode);
        Assert.Equal(600, parameters.Resolution);
    }

    [Fact]
    public void BuildXml_GeneratesValidXmlWithNamespacesAndValues()
    {
        var parameters = new EsclJobParameters(
            Source: "Feeder",
            ColorMode: "RGB24",
            Resolution: 300,
            Duplex: true,
            DocumentFormat: "image/jpeg");

        var xml = EsclScanSettingsBuilder.BuildXml(parameters);

        Assert.NotNull(xml);
        var doc = XDocument.Parse(xml);

        XNamespace scan = EsclScanSettingsBuilder.ScanNamespace;
        XNamespace pwg = EsclScanSettingsBuilder.PwgNamespace;

        var root = doc.Element(scan + "ScanSettings");
        Assert.NotNull(root);

        Assert.Equal("2.0", root.Element(pwg + "Version")?.Value);
        Assert.Equal("Feeder", root.Element(pwg + "InputSource")?.Value);
        Assert.Equal("RGB24", root.Element(scan + "ColorMode")?.Value);
        Assert.Equal("300", root.Element(scan + "XResolution")?.Value);
        Assert.Equal("300", root.Element(scan + "YResolution")?.Value);
        Assert.Equal("image/jpeg", root.Element(pwg + "DocumentFormat")?.Value);
        Assert.Equal("true", root.Element(scan + "Duplex")?.Value);
    }
}

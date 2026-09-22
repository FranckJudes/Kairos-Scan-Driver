using OpenScan.Backends.Escl;
using Xunit;

namespace OpenScan.Tests;

public class EsclCapabilitiesParserTests
{
    private const string SampleMfpXml = """
        <scan:ScannerCapabilities xmlns:scan="http://schemas.hp.com/imaging/escl/2011/05/03" xmlns:pwg="http://www.pwg.org/schemas/2010/12/sm">
          <pwg:Version>2.0</pwg:Version>
          <pwg:MakeAndModel>Example MFP</pwg:MakeAndModel>
          <pwg:SerialNumber>ABC123</pwg:SerialNumber>
          <scan:Platen>
            <scan:PlatenInputCaps>
              <scan:MaxWidth>2551</scan:MaxWidth>
              <scan:MaxHeight>3508</scan:MaxHeight>
              <scan:SettingProfiles>
                <scan:SettingProfile>
                  <scan:ColorModes>
                    <scan:ColorMode>BlackAndWhite1</scan:ColorMode>
                    <scan:ColorMode>Grayscale8</scan:ColorMode>
                    <scan:ColorMode>RGB24</scan:ColorMode>
                  </scan:ColorModes>
                  <scan:DocumentFormats>
                    <pwg:DocumentFormat>image/jpeg</pwg:DocumentFormat>
                  </scan:DocumentFormats>
                  <scan:SupportedResolutions>
                    <scan:DiscreteResolutions>
                      <scan:DiscreteResolution>
                        <scan:XResolution>300</scan:XResolution>
                        <scan:YResolution>300</scan:YResolution>
                      </scan:DiscreteResolution>
                      <scan:DiscreteResolution>
                        <scan:XResolution>600</scan:XResolution>
                        <scan:YResolution>600</scan:YResolution>
                      </scan:DiscreteResolution>
                    </scan:DiscreteResolutions>
                  </scan:SupportedResolutions>
                </scan:SettingProfile>
              </scan:SettingProfiles>
            </scan:PlatenInputCaps>
          </scan:Platen>
          <scan:Adf>
            <scan:AdfSimplexInputCaps>
              <scan:MaxWidth>2551</scan:MaxWidth>
              <scan:MaxHeight>3508</scan:MaxHeight>
            </scan:AdfSimplexInputCaps>
            <scan:AdfDuplexInputCaps>
              <scan:MaxWidth>2551</scan:MaxWidth>
              <scan:MaxHeight>3508</scan:MaxHeight>
            </scan:AdfDuplexInputCaps>
            <scan:FeederCapacity>50</scan:FeederCapacity>
          </scan:Adf>
        </scan:ScannerCapabilities>
        """;

    [Fact]
    public void Parse_FullCapabilities_ExtractsAllFieldsCorrectly()
    {
        var parsed = EsclCapabilitiesParser.Parse(SampleMfpXml);

        Assert.Equal("Example MFP", parsed.MakeAndModel);
        Assert.Equal("Example", parsed.Manufacturer);
        Assert.Equal("MFP", parsed.Model);
        Assert.Equal("ABC123", parsed.SerialNumber);

        var caps = parsed.Capabilities;
        Assert.True(caps.Adf);
        Assert.True(caps.Flatbed);
        Assert.True(caps.Duplex);
        Assert.Contains("adf", caps.Sources);
        Assert.Contains("flatbed", caps.Sources);

        Assert.Equal([300, 600], caps.Resolutions);
        Assert.Contains("color", caps.PixelFormats);
        Assert.Contains("grayscale", caps.PixelFormats);
        Assert.Contains("bw", caps.PixelFormats);
        Assert.Contains(24, caps.BitDepths);
        Assert.Contains(8, caps.BitDepths);
        Assert.Contains(1, caps.BitDepths);

        Assert.NotNull(caps.MaxPageSize);
        // 2551 * 2540 / 300 = 21598
        Assert.Equal(21598, caps.MaxPageSize.Width);
        // 3508 * 2540 / 300 = 29701
        Assert.Equal(29701, caps.MaxPageSize.Height);
    }

    [Fact]
    public void Parse_FlatbedOnly_SetsAdfAndDuplexToFalse()
    {
        const string flatbedXml = """
            <scan:ScannerCapabilities xmlns:scan="http://schemas.hp.com/imaging/escl/2011/05/03" xmlns:pwg="http://www.pwg.org/schemas/2010/12/sm">
              <pwg:MakeAndModel>Canon Flatbed Scanner</pwg:MakeAndModel>
              <scan:Platen>
                <scan:PlatenInputCaps>
                  <scan:SupportedResolutions>
                    <scan:DiscreteResolutions>
                      <scan:DiscreteResolution>
                        <scan:XResolution>200</scan:XResolution>
                      </scan:DiscreteResolution>
                    </scan:DiscreteResolutions>
                  </scan:SupportedResolutions>
                </scan:PlatenInputCaps>
              </scan:Platen>
            </scan:ScannerCapabilities>
            """;

        var parsed = EsclCapabilitiesParser.Parse(flatbedXml);

        Assert.False(parsed.Capabilities.Adf);
        Assert.True(parsed.Capabilities.Flatbed);
        Assert.False(parsed.Capabilities.Duplex);
        Assert.Single(parsed.Capabilities.Sources, "flatbed");
        Assert.Equal([200], parsed.Capabilities.Resolutions);
    }

    [Fact]
    public void Parse_AdfWithoutDuplex_SetsDuplexFalse()
    {
        const string adfXml = """
            <scan:ScannerCapabilities xmlns:scan="http://schemas.hp.com/imaging/escl/2011/05/03" xmlns:pwg="http://www.pwg.org/schemas/2010/12/sm">
              <scan:Adf>
                <scan:AdfSimplexInputCaps>
                  <scan:MaxWidth>2551</scan:MaxWidth>
                </scan:AdfSimplexInputCaps>
              </scan:Adf>
            </scan:ScannerCapabilities>
            """;

        var parsed = EsclCapabilitiesParser.Parse(adfXml);

        Assert.True(parsed.Capabilities.Adf);
        Assert.False(parsed.Capabilities.Duplex);
        Assert.Contains("adf", parsed.Capabilities.Sources);
    }

    [Fact]
    public void Parse_MinimalXml_ProvidesSensibleDefaultsWithoutCrashing()
    {
        const string minimalXml = "<scan:ScannerCapabilities xmlns:scan=\"http://schemas.hp.com/imaging/escl/2011/05/03\" xmlns:pwg=\"http://www.pwg.org/schemas/2010/12/sm\"><pwg:Version>2.0</pwg:Version></scan:ScannerCapabilities>";

        var parsed = EsclCapabilitiesParser.Parse(minimalXml);

        Assert.NotNull(parsed.Capabilities);
        Assert.NotEmpty(parsed.Capabilities.Resolutions);
        Assert.NotEmpty(parsed.Capabilities.PixelFormats);
        Assert.NotEmpty(parsed.Capabilities.Sources);
    }

    [Fact]
    public void Parse_EmptyXml_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => EsclCapabilitiesParser.Parse(""));
    }
}

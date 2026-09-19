using OpenScan.Backends.Twain;

namespace OpenScan.Tests;

public class TwainCapabilityMapperTests
{
    [Fact]
    public void Map_DiscreteResolutions_OrdersAndDeduplicates()
    {
        var raw = new TwainRawCapabilities
        {
            DiscreteResolutions = [300f, 150f, 600f, 300f, 100f]
        };

        var caps = TwainCapabilityMapper.Map(raw);

        Assert.Equal([100, 150, 300, 600], caps.Resolutions);
    }

    [Fact]
    public void Map_ContinuousResolutionRange_FiltersCommonResolutionsWithinRange()
    {
        var raw = new TwainRawCapabilities
        {
            ResolutionRange = (50f, 600f, 1f)
        };

        var caps = TwainCapabilityMapper.Map(raw);

        Assert.Contains(150, caps.Resolutions);
        Assert.Contains(300, caps.Resolutions);
        Assert.Contains(600, caps.Resolutions);
        Assert.DoesNotContain(1200, caps.Resolutions);
        Assert.All(caps.Resolutions, r => Assert.InRange(r, 50, 600));
    }

    [Fact]
    public void Map_FeederAndDuplex_ExtractsSourcesCorrectly()
    {
        var raw = new TwainRawCapabilities
        {
            HasFeeder = true,
            HasFlatbed = true,
            HasDuplex = true
        };

        var caps = TwainCapabilityMapper.Map(raw);

        Assert.True(caps.Adf);
        Assert.True(caps.Flatbed);
        Assert.True(caps.Duplex);
        Assert.Contains("adf", caps.Sources);
        Assert.Contains("flatbed", caps.Sources);
    }

    [Fact]
    public void Map_PixelTypes_MapsBwGrayAndColorWithBitDepths()
    {
        var raw = new TwainRawCapabilities
        {
            PixelTypes = [TwainCapabilityMapper.TWPT_BW, TwainCapabilityMapper.TWPT_GRAY, TwainCapabilityMapper.TWPT_RGB]
        };

        var caps = TwainCapabilityMapper.Map(raw);

        Assert.Equal(["bw", "grayscale", "color"], caps.PixelFormats);
        Assert.Equal([1, 8, 24], caps.BitDepths);
    }

    [Fact]
    public void Map_SupportedPaperSizes_MapsStandardSizes()
    {
        var raw = new TwainRawCapabilities
        {
            SupportedSizes = [TwainCapabilityMapper.TWSS_A4, TwainCapabilityMapper.TWSS_USLETTER, TwainCapabilityMapper.TWSS_A3]
        };

        var caps = TwainCapabilityMapper.Map(raw);

        Assert.Equal(["a4", "letter", "a3"], caps.PaperSizes);
    }

    [Theory]
    [InlineData("a4", TwainCapabilityMapper.TWSS_A4)]
    [InlineData("letter", TwainCapabilityMapper.TWSS_USLETTER)]
    [InlineData("legal", TwainCapabilityMapper.TWSS_USLEGAL)]
    [InlineData("a3", TwainCapabilityMapper.TWSS_A3)]
    public void MapToTwainPaperSize_MapsKnownSizes(string standard, int expectedTwain)
    {
        var twainSize = TwainCapabilityMapper.MapToTwainPaperSize(standard);
        Assert.Equal(expectedTwain, twainSize);
    }

    [Theory]
    [InlineData("bw", TwainCapabilityMapper.TWPT_BW)]
    [InlineData("lineart", TwainCapabilityMapper.TWPT_BW)]
    [InlineData("grayscale", TwainCapabilityMapper.TWPT_GRAY)]
    [InlineData("color", TwainCapabilityMapper.TWPT_RGB)]
    public void MapToTwainPixelType_MapsKnownModes(string format, int expectedTwain)
    {
        var twainPixel = TwainCapabilityMapper.MapToTwainPixelType(format);
        Assert.Equal(expectedTwain, twainPixel);
    }
}

using OpenScan.Backends.Wia;
using OpenScan.Contracts;

namespace OpenScan.Tests;

public class WiaCapabilityMapperTests
{
    [Fact]
    public void MapDocumentHandling_AdfDuplexOnlyBitmask_ExtractsCorrectFlags()
    {
        var (adf, flatbed, duplex) = WiaCapabilityMapper.MapDocumentHandling(WiaCapabilityMapper.FeederFlag | WiaCapabilityMapper.DuplexFlag);

        Assert.True(adf);
        Assert.False(flatbed);
        Assert.True(duplex);
    }

    [Fact]
    public void MapDocumentHandling_FlatbedOnlyBitmask_ExtractsCorrectFlags()
    {
        var (adf, flatbed, duplex) = WiaCapabilityMapper.MapDocumentHandling(WiaCapabilityMapper.FlatbedFlag);

        Assert.False(adf);
        Assert.True(flatbed);
        Assert.False(duplex);
    }

    [Fact]
    public void MapSources_AdfAndFlatbedBitmask_ReturnsBothNormalizedSources()
    {
        var sources = WiaCapabilityMapper.MapSources(WiaCapabilityMapper.FeederFlag | WiaCapabilityMapper.FlatbedFlag);

        Assert.Equal(["adf", "flatbed"], sources);
    }

    [Fact]
    public void ResolveDocumentHandlingSelect_FlatbedRequested_ReturnsFlatbedFlagRegardlessOfDuplex()
    {
        Assert.Equal(WiaCapabilityMapper.FlatbedFlag, WiaCapabilityMapper.ResolveDocumentHandlingSelect("flatbed", duplex: true));
    }

    [Fact]
    public void ResolveDocumentHandlingSelect_AdfDuplexRequested_CombinesFeederAndDuplexFlags()
    {
        var select = WiaCapabilityMapper.ResolveDocumentHandlingSelect("adf", duplex: true);

        Assert.Equal(WiaCapabilityMapper.FeederFlag | WiaCapabilityMapper.DuplexFlag, select);
    }

    [Theory]
    [InlineData("bw", WiaCapabilityMapper.IntentText)]
    [InlineData("grayscale", WiaCapabilityMapper.IntentGrayscale)]
    [InlineData("color", WiaCapabilityMapper.IntentColor)]
    public void ResolveIntent_MapsPixelFormatToWiaIntent(string pixelFormat, int expectedIntent)
    {
        Assert.Equal(expectedIntent, WiaCapabilityMapper.ResolveIntent(pixelFormat));
    }

    [Fact]
    public void ExpandResolutionRange_FiltersCommonResolutionsWithinStep()
    {
        var resolutions = WiaCapabilityMapper.ExpandResolutionRange(min: 100, max: 600, step: 50);

        Assert.Contains(100, resolutions);
        Assert.Contains(300, resolutions);
        Assert.Contains(600, resolutions);
        Assert.DoesNotContain(75, resolutions); // hors plage
        Assert.DoesNotContain(240, resolutions); // pas atteignable avec un pas de 50 depuis 100
    }

    [Fact]
    public void ExpandResolutionRange_NoStandardResolutionReachable_FallsBackToMinMax()
    {
        var resolutions = WiaCapabilityMapper.ExpandResolutionRange(min: 111, max: 119, step: 1);

        Assert.Equal([111, 119], resolutions);
    }

    [Fact]
    public void WiaErrorMapper_PaperEmpty_IsRecognizedAsFeederEmptyNotAnError()
    {
        Assert.True(WiaErrorMapper.IsFeederEmpty(WiaErrorMapper.PaperEmpty));
        Assert.False(WiaErrorMapper.IsFeederEmpty(WiaErrorMapper.PaperJam));
    }

    [Theory]
    [InlineData(WiaErrorMapper.PaperJam, ErrorCode.PaperJam)]
    [InlineData(WiaErrorMapper.MultiFeed, ErrorCode.MultiFeed)]
    [InlineData(WiaErrorMapper.Offline, ErrorCode.ScannerDisconnected)]
    [InlineData(WiaErrorMapper.Busy, ErrorCode.ScannerBusy)]
    [InlineData(WiaErrorMapper.DeviceCommunication, ErrorCode.DriverError)]
    public void WiaErrorMapper_MapsKnownHResultsToNormalizedErrorCodes(int hResult, ErrorCode expected)
    {
        Assert.Equal(expected, WiaErrorMapper.MapErrorCode(hResult));
    }
}

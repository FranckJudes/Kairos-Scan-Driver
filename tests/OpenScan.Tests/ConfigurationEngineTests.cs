using OpenScan.Contracts;
using OpenScan.Core;

namespace OpenScan.Tests;

public class ConfigurationEngineTests
{
    private static ScannerCapabilities MockCapabilities() => new()
    {
        Sources = ["adf", "flatbed"],
        Duplex = false,
        Resolutions = [100, 150, 200, 300, 600],
        PixelFormats = ["color", "grayscale", "bw"]
    };

    [Fact]
    public void Validate_ResolutionNotSupported_FallsBackToNearestAndRecordsAdjustment()
    {
        var profile = new ScanProfile { Source = "adf", Resolution = 350, PixelFormat = "color" };

        var result = ConfigurationEngine.Validate(profile, MockCapabilities());

        Assert.Equal(300, result.Effective["resolution"]);
        Assert.Contains(result.Adjustments, a => a.Parameter == "resolution" && a.Reason == "nearest_supported");
    }

    [Fact]
    public void Validate_DuplexUnsupportedByScanner_FallsBackToFalseAndRecordsAdjustment()
    {
        var profile = new ScanProfile { Source = "adf", Duplex = true, Resolution = 300, PixelFormat = "color" };

        var result = ConfigurationEngine.Validate(profile, MockCapabilities());

        Assert.Equal(false, result.Effective["duplex"]);
        Assert.Contains(result.Adjustments, a => a.Parameter == "duplex" && a.Reason == "unsupported_by_scanner");
    }

    [Fact]
    public void Validate_SourceNotSupported_ThrowsCapabilityUnsupported()
    {
        var profile = new ScanProfile { Source = "network", Resolution = 300, PixelFormat = "color" };

        var ex = Assert.Throws<ScanEngineException>(() => ConfigurationEngine.Validate(profile, MockCapabilities()));

        Assert.Equal(ErrorCode.CapabilityUnsupported, ex.Code);
    }

    [Fact]
    public void Validate_PixelFormatNotSupported_ThrowsCapabilityUnsupported()
    {
        var profile = new ScanProfile { Source = "adf", Resolution = 300, PixelFormat = "cmyk" };

        var ex = Assert.Throws<ScanEngineException>(() => ConfigurationEngine.Validate(profile, MockCapabilities()));

        Assert.Equal(ErrorCode.CapabilityUnsupported, ex.Code);
    }

    [Fact]
    public void Validate_SupportedResolution_NoAdjustmentRecorded()
    {
        var profile = new ScanProfile { Source = "adf", Resolution = 300, PixelFormat = "color" };

        var result = ConfigurationEngine.Validate(profile, MockCapabilities());

        Assert.DoesNotContain(result.Adjustments, a => a.Parameter == "resolution");
    }
}

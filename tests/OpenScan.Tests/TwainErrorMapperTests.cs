using OpenScan.Backends.Twain;
using OpenScan.Contracts;

namespace OpenScan.Tests;

public class TwainErrorMapperTests
{
    [Fact]
    public void IsFeederEmpty_WithNomediaCondition_ReturnsTrue()
    {
        var isFeederEmpty = TwainErrorMapper.IsFeederEmpty(
            TwainErrorMapper.TWRC_FAILURE,
            TwainErrorMapper.TWCC_NOMEDIA);

        Assert.True(isFeederEmpty);
    }

    [Fact]
    public void IsFeederEmpty_WithPaperJamCondition_ReturnsFalse()
    {
        var isFeederEmpty = TwainErrorMapper.IsFeederEmpty(
            TwainErrorMapper.TWRC_FAILURE,
            TwainErrorMapper.TWCC_PAPERJAM);

        Assert.False(isFeederEmpty);
    }

    [Theory]
    [InlineData(TwainErrorMapper.TWCC_PAPERJAM, ErrorCode.PaperJam, true)]
    [InlineData(TwainErrorMapper.TWCC_PAPERDOUBLEFEED, ErrorCode.MultiFeed, true)]
    [InlineData(TwainErrorMapper.TWCC_NOMEDIA, ErrorCode.NoPaper, true)]
    [InlineData(TwainErrorMapper.TWCC_CHECKDEVICEONLINE, ErrorCode.ScannerDisconnected, false)]
    [InlineData(TwainErrorMapper.TWCC_MAXCONNECTIONS, ErrorCode.ScannerBusy, true)]
    [InlineData(TwainErrorMapper.TWCC_DENIED, ErrorCode.ScannerBusy, true)]
    [InlineData(TwainErrorMapper.TWCC_NODS, ErrorCode.ScannerNotFound, false)]
    [InlineData(TwainErrorMapper.TWCC_CAPUNSUPPORTED, ErrorCode.CapabilityUnsupported, false)]
    [InlineData(TwainErrorMapper.TWCC_BADCAP, ErrorCode.CapabilityUnsupported, false)]
    [InlineData(TwainErrorMapper.TWCC_BADVALUE, ErrorCode.InvalidConfiguration, false)]
    [InlineData(TwainErrorMapper.TWCC_OPERATIONERROR, ErrorCode.DriverError, false)]
    [InlineData(TwainErrorMapper.TWCC_FILEWRITEERROR, ErrorCode.StorageError, false)]
    public void MapErrorCode_MapsKnownTwainConditions(ushort conditionCode, ErrorCode expectedCode, bool expectedRecoverable)
    {
        var code = TwainErrorMapper.MapErrorCode(conditionCode);
        var recoverable = TwainErrorMapper.IsRecoverable(code);

        Assert.Equal(expectedCode, code);
        Assert.Equal(expectedRecoverable, recoverable);
    }
}

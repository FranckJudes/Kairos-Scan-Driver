using OpenScan.Backends.Twain;
using OpenScan.Contracts;

namespace OpenScan.Tests;

public class WorkerProtocolTests
{
    [Fact]
    public void SerializeCommand_And_DeserializeCommand_RoundTripsDiscoverCommand()
    {
        var cmd = new DiscoverCommand();
        var json = WorkerProtocol.SerializeCommand(cmd);

        var deserialized = WorkerProtocol.DeserializeCommand(json);

        Assert.NotNull(deserialized);
        Assert.IsType<DiscoverCommand>(deserialized);
        Assert.Equal("discover", deserialized.Cmd);
    }

    [Fact]
    public void SerializeCommand_And_DeserializeCommand_RoundTripsConfigureCommand()
    {
        var cmd = new ConfigureCommand("twain-1", "job-100", "adf", true, 300, "color", "a4");
        var json = WorkerProtocol.SerializeCommand(cmd);

        var deserialized = WorkerProtocol.DeserializeCommand(json);

        var config = Assert.IsType<ConfigureCommand>(deserialized);
        Assert.Equal("twain-1", config.ScannerId);
        Assert.Equal("job-100", config.JobId);
        Assert.Equal("adf", config.Source);
        Assert.True(config.Duplex);
        Assert.Equal(300, config.Resolution);
        Assert.Equal("color", config.PixelFormat);
        Assert.Equal("a4", config.PageSize);
    }

    [Fact]
    public void SerializeCommand_And_DeserializeCommand_RoundTripsAcquireCommand()
    {
        var cmd = new AcquireCommand("scanner-2", "job-200");
        var json = WorkerProtocol.SerializeCommand(cmd);

        var deserialized = WorkerProtocol.DeserializeCommand(json);

        var acq = Assert.IsType<AcquireCommand>(deserialized);
        Assert.Equal("scanner-2", acq.ScannerId);
        Assert.Equal("job-200", acq.JobId);
    }

    [Fact]
    public void SerializeResponse_And_DeserializeResponse_RoundTripsPageAcquiredResponse()
    {
        var resp = new PageAcquiredResponse("job-1", 1, "/tmp/page-1.bmp", 2480, 3508, 300, "color", "adf");
        var json = WorkerProtocol.SerializeResponse(resp);

        var deserialized = WorkerProtocol.DeserializeResponse(json);

        var page = Assert.IsType<PageAcquiredResponse>(deserialized);
        Assert.Equal("job-1", page.JobId);
        Assert.Equal(1, page.PageNumber);
        Assert.Equal("/tmp/page-1.bmp", page.FilePath);
        Assert.Equal(2480, page.Width);
        Assert.Equal(3508, page.Height);
        Assert.Equal(300, page.Dpi);
        Assert.Equal("color", page.PixelFormat);
    }

    [Fact]
    public void SerializeResponse_And_DeserializeResponse_RoundTripsBatchCompleteResponse()
    {
        var resp = new BatchCompleteResponse("job-1");
        var json = WorkerProtocol.SerializeResponse(resp);

        var deserialized = WorkerProtocol.DeserializeResponse(json);

        var batch = Assert.IsType<BatchCompleteResponse>(deserialized);
        Assert.Equal("job-1", batch.JobId);
    }

    [Fact]
    public void SerializeResponse_And_DeserializeResponse_RoundTripsErrorResponse()
    {
        var resp = new ErrorResponse(ErrorCode.PaperJam, "Bourrage papier dans l'ADF", true, "scanner-1", "job-1", "CC=20");
        var json = WorkerProtocol.SerializeResponse(resp);

        var deserialized = WorkerProtocol.DeserializeResponse(json);

        var err = Assert.IsType<ErrorResponse>(deserialized);
        Assert.Equal(ErrorCode.PaperJam, err.Code);
        Assert.Equal("Bourrage papier dans l'ADF", err.Message);
        Assert.True(err.Recoverable);
        Assert.Equal("scanner-1", err.ScannerId);
        Assert.Equal("CC=20", err.TechnicalDetails);
    }

    [Fact]
    public void DeserializeResponse_UnknownType_ReturnsNull()
    {
        var json = """{"type":"unknown_future_type","foo":"bar"}""";
        var result = WorkerProtocol.DeserializeResponse(json);
        Assert.Null(result);
    }

    [Fact]
    public void DeserializeCommand_UnknownCmd_ReturnsNull()
    {
        var json = """{"cmd":"future_command"}""";
        var result = WorkerProtocol.DeserializeCommand(json);
        Assert.Null(result);
    }
}

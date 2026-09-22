using System.Net;
using OpenScan.Backends.Escl;
using OpenScan.Contracts;
using Xunit;

namespace OpenScan.Tests;

public class EsclErrorMapperTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ErrorCode.InvalidConfiguration)]
    [InlineData(HttpStatusCode.NotFound, ErrorCode.JobNotFound)]
    [InlineData(HttpStatusCode.Conflict, ErrorCode.ScannerBusy)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ErrorCode.ScannerBusy)]
    [InlineData(HttpStatusCode.RequestTimeout, ErrorCode.Timeout)]
    [InlineData(HttpStatusCode.GatewayTimeout, ErrorCode.Timeout)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorCode.BackendError)]
    [InlineData(HttpStatusCode.BadGateway, ErrorCode.BackendError)]
    public void MapStatusCode_ReturnsExpectedErrorCode(HttpStatusCode httpStatus, ErrorCode expectedCode)
    {
        var actual = EsclErrorMapper.MapStatusCode(httpStatus);
        Assert.Equal(expectedCode, actual);
    }

    [Theory]
    [InlineData(ErrorCode.ScannerBusy, true)]
    [InlineData(ErrorCode.ScannerDisconnected, true)]
    [InlineData(ErrorCode.Timeout, true)]
    [InlineData(ErrorCode.PaperJam, true)]
    [InlineData(ErrorCode.NoPaper, true)]
    [InlineData(ErrorCode.BackendError, false)]
    [InlineData(ErrorCode.InvalidConfiguration, false)]
    [InlineData(ErrorCode.DriverError, false)]
    public void IsRecoverable_ReturnsExpectedFlag(ErrorCode code, bool expected)
    {
        Assert.Equal(expected, EsclErrorMapper.IsRecoverable(code));
    }

    [Fact]
    public void MapHttpException_HttpRequestExceptionWithStatusCode_MapsCorrectly()
    {
        var httpEx = new HttpRequestException("Service indisponible", null, HttpStatusCode.ServiceUnavailable);
        var ex = EsclErrorMapper.MapHttpException(httpEx, "escl:192.168.1.50", "JOB-1");

        Assert.Equal(ErrorCode.ScannerBusy, ex.Code);
        Assert.True(ex.Recoverable);
        Assert.Equal("escl", ex.Backend);
        Assert.Equal("escl:192.168.1.50", ex.ScannerId);
        Assert.Equal("JOB-1", ex.JobId);
    }

    [Fact]
    public void MapHttpException_HttpRequestExceptionWithoutStatusCode_MapsToScannerDisconnected()
    {
        var httpEx = new HttpRequestException("Connexion refusée");
        var ex = EsclErrorMapper.MapHttpException(httpEx, "escl:192.168.1.50");

        Assert.Equal(ErrorCode.ScannerDisconnected, ex.Code);
        Assert.True(ex.Recoverable);
    }

    [Fact]
    public void MapHttpException_TimeoutException_MapsToTimeout()
    {
        var timeoutEx = new TimeoutException("Délai dépassé");
        var ex = EsclErrorMapper.MapHttpException(timeoutEx, "escl:192.168.1.50");

        Assert.Equal(ErrorCode.Timeout, ex.Code);
        Assert.True(ex.Recoverable);
    }

    [Fact]
    public void MapHttpException_GenericException_MapsToBackendError()
    {
        var genericEx = new InvalidOperationException("Erreur quelconque");
        var ex = EsclErrorMapper.MapHttpException(genericEx, "escl:192.168.1.50");

        Assert.Equal(ErrorCode.BackendError, ex.Code);
        Assert.False(ex.Recoverable);
    }
}

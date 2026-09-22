using System.Net;
using OpenScan.Contracts;

namespace OpenScan.Backends.Escl;

/// <summary>
/// Mappeur pur traduisant les statuts HTTP et erreurs réseau eSCL vers les ErrorCode OpenScan.
/// </summary>
public static class EsclErrorMapper
{
    public static ErrorCode MapStatusCode(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.BadRequest => ErrorCode.InvalidConfiguration,
            HttpStatusCode.NotFound => ErrorCode.JobNotFound,
            HttpStatusCode.Conflict => ErrorCode.ScannerBusy,
            HttpStatusCode.ServiceUnavailable => ErrorCode.ScannerBusy,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => ErrorCode.Timeout,
            _ => (int)statusCode >= 500 ? ErrorCode.BackendError : ErrorCode.AcquisitionError
        };

    public static bool IsRecoverable(ErrorCode code) =>
        code switch
        {
            ErrorCode.ScannerBusy => true,
            ErrorCode.ScannerDisconnected => true,
            ErrorCode.Timeout => true,
            ErrorCode.PaperJam => true,
            ErrorCode.NoPaper => true,
            _ => false
        };

    public static ScanEngineException MapHttpException(
        Exception ex,
        string scannerId,
        string? jobId = null,
        string? context = null)
    {
        if (ex is ScanEngineException see)
        {
            return see;
        }

        if (ex is OperationCanceledException or TimeoutException)
        {
            return new ScanEngineException(
                ErrorCode.Timeout,
                $"Délai d'attente dépassé lors de la communication avec le scanner eSCL '{scannerId}'.",
                recoverable: true,
                inner: ex)
            {
                Backend = EsclScannerBackend.Id,
                ScannerId = scannerId,
                JobId = jobId,
                TechnicalDetails = context
            };
        }

        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode.HasValue)
            {
                var code = MapStatusCode(httpEx.StatusCode.Value);
                var recoverable = IsRecoverable(code);
                return new ScanEngineException(
                    code,
                    $"Erreur HTTP {(int)httpEx.StatusCode.Value} ({httpEx.StatusCode.Value}) renvoyée par le scanner eSCL '{scannerId}': {httpEx.Message}",
                    recoverable: recoverable,
                    inner: ex)
                {
                    Backend = EsclScannerBackend.Id,
                    ScannerId = scannerId,
                    JobId = jobId,
                    TechnicalDetails = context
                };
            }

            return new ScanEngineException(
                ErrorCode.ScannerDisconnected,
                $"Impossible de joindre le scanner eSCL '{scannerId}'. Vérifiez la connectivité réseau.",
                recoverable: true,
                inner: ex)
            {
                Backend = EsclScannerBackend.Id,
                ScannerId = scannerId,
                JobId = jobId,
                TechnicalDetails = context
            };
        }

        return new ScanEngineException(
            ErrorCode.BackendError,
            $"Erreur inattendue du backend eSCL sur '{scannerId}': {ex.Message}",
            recoverable: false,
            inner: ex)
        {
            Backend = EsclScannerBackend.Id,
            ScannerId = scannerId,
            JobId = jobId,
            TechnicalDetails = context
        };
    }
}

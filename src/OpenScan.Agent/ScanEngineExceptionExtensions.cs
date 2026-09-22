using OpenScan.Contracts;

namespace OpenScan.Agent;

internal static class ScanEngineExceptionExtensions
{
    public static IResult ToProblem(this ScanEngineException ex)
    {
        var status = ex.Code switch
        {
            ErrorCode.ScannerNotFound or ErrorCode.JobNotFound => StatusCodes.Status404NotFound,
            ErrorCode.ScannerBusy or ErrorCode.Cancelled or ErrorCode.InvalidJobState => StatusCodes.Status409Conflict,
            ErrorCode.CapabilityUnsupported or ErrorCode.InvalidConfiguration => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Problem(
            title: ex.Code.ToString(),
            detail: ex.Message,
            statusCode: status,
            extensions: new Dictionary<string, object?> { ["recoverable"] = ex.Recoverable });
    }
}

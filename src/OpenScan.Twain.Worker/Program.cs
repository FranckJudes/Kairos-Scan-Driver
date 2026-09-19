using OpenScan.Backends.Twain;
using OpenScan.Contracts;

namespace OpenScan.Twain.Worker;

public static class Program
{
    public static async Task Main(string[] args)
    {
        using var session = new TwainSession();

        while (true)
        {
            var line = await Console.In.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var command = WorkerProtocol.DeserializeCommand(trimmed);
            if (command is null)
            {
                continue;
            }

            try
            {
                switch (command)
                {
                    case DiscoverCommand:
                        var scanners = session.DiscoverScanners();
                        await WriteResponseAsync(new ScannersResponse(scanners));
                        break;

                    case GetCapabilitiesCommand capsCmd:
                        var caps = session.GetCapabilities(capsCmd.ScannerId);
                        await WriteResponseAsync(new CapabilitiesResponse(capsCmd.ScannerId, caps));
                        break;

                    case ConfigureCommand cfgCmd:
                        session.Configure(cfgCmd.ScannerId, cfgCmd);
                        await WriteResponseAsync(new OkResponse("configure"));
                        break;

                    case AcquireCommand acqCmd:
                        session.Acquire(
                            acqCmd.ScannerId,
                            acqCmd.JobId,
                            onPageAcquired: page => WriteResponseSync(page),
                            onBatchComplete: () => WriteResponseSync(new BatchCompleteResponse(acqCmd.JobId)));
                        break;

                    case ExitCommand:
                        return;

                    case CancelCommand cancelCmd:
                        await WriteResponseAsync(new OkResponse("cancel"));
                        return;
                }
            }
            catch (Exception ex)
            {
                var errorResponse = new ErrorResponse(
                    ErrorCode.DriverError,
                    ex.Message,
                    Recoverable: false,
                    TechnicalDetails: ex.ToString());

                await WriteResponseAsync(errorResponse);
            }
        }
    }

    private static Task WriteResponseAsync<T>(T response) where T : class =>
        Console.Out.WriteLineAsync(WorkerProtocol.SerializeResponse(response));

    private static void WriteResponseSync<T>(T response) where T : class =>
        Console.Out.WriteLine(WorkerProtocol.SerializeResponse(response));
}

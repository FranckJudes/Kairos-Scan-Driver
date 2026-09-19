using OpenScan.Contracts;

namespace OpenScan.Backends.Wia;


public static class WiaErrorMapper
{
    public const int PaperJam = unchecked((int)0x80210005);
    public const int PaperEmpty = unchecked((int)0x80210003);
    public const int PaperProblem = unchecked((int)0x80210006);
    public const int Offline = unchecked((int)0x80210007);
    public const int Busy = unchecked((int)0x80210008);
    public const int CoverOpen = unchecked((int)0x80210016);
    public const int MultiFeed = unchecked((int)0x80210017);
    public const int DeviceCommunication = unchecked((int)0x8021000C);

    public static bool IsFeederEmpty(int hResult) => hResult == PaperEmpty;

    public static ErrorCode MapErrorCode(int hResult) => hResult switch
    {
        PaperJam or PaperProblem => ErrorCode.PaperJam,
        MultiFeed => ErrorCode.MultiFeed,
        Offline => ErrorCode.ScannerDisconnected,
        Busy => ErrorCode.ScannerBusy,
        DeviceCommunication => ErrorCode.DriverError,
        _ => ErrorCode.AcquisitionError
    };

    public static bool IsRecoverable(ErrorCode code) =>
        code is ErrorCode.PaperJam or ErrorCode.MultiFeed or ErrorCode.NoPaper;
}

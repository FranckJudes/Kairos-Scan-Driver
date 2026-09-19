using OpenScan.Contracts;

namespace OpenScan.Backends.Twain;

/// <summary>
/// Mapping pur des codes d'erreur et conditions TWAIN 2.x vers les codes normalisés OpenScan.
/// Ne dépend d'aucun P/Invoke ou composant I/O, testable unitairement (sections 12 et 17).
/// </summary>
public static class TwainErrorMapper
{
    // Constantes standard TWAIN 2.x
    public const ushort TWRC_SUCCESS = 0;
    public const ushort TWRC_FAILURE = 1;
    public const ushort TWRC_CHECKSTATUS = 2;
    public const ushort TWRC_CANCEL = 3;
    public const ushort TWRC_DSEVENT = 4;
    public const ushort TWRC_NOTDSEVENT = 5;
    public const ushort TWRC_XFERDONE = 6;
    public const ushort TWRC_ENDOFLIST = 7;

    public const ushort TWCC_SUCCESS = 0;
    public const ushort TWCC_BUMMER = 1;
    public const ushort TWCC_LOWMEMORY = 2;
    public const ushort TWCC_NODS = 3;
    public const ushort TWCC_MAXCONNECTIONS = 4;
    public const ushort TWCC_OPERATIONERROR = 5;
    public const ushort TWCC_BADCAP = 6;
    public const ushort TWCC_BADPROTOCOL = 9;
    public const ushort TWCC_BADVALUE = 10;
    public const ushort TWCC_SEQERROR = 11;
    public const ushort TWCC_BADDEST = 12;
    public const ushort TWCC_CAPUNSUPPORTED = 13;
    public const ushort TWCC_CAPBADOPERATION = 14;
    public const ushort TWCC_CAPSEQERROR = 15;
    public const ushort TWCC_DENIED = 16;
    public const ushort TWCC_FILEEXISTS = 17;
    public const ushort TWCC_FILENOTFOUND = 18;
    public const ushort TWCC_NOTEMPTY = 19;
    public const ushort TWCC_PAPERJAM = 20;
    public const ushort TWCC_PAPERDOUBLEFEED = 21;
    public const ushort TWCC_FILEWRITEERROR = 22;
    public const ushort TWCC_CHECKDEVICEONLINE = 23;
    public const ushort TWCC_INTERLOCK = 24;
    public const ushort TWCC_DAMAGEDCORNER = 25;
    public const ushort TWCC_FOCUSERROR = 26;
    public const ushort TWCC_DOCTOODARK = 27;
    public const ushort TWCC_DOCTOOLIGHT = 28;
    public const ushort TWCC_NOMEDIA = 29;

    /// <summary>
    /// Détermine si la condition TWAIN signale que le bac ADF est vide.
    /// TWAIN signale la fin de lot ADF via TWRC_FAILURE + TWCC_NOMEDIA.
    /// Ce cas DOIT être traité comme une fin de lot normale (pages épuisées), pas comme une erreur.
    /// </summary>
    public static bool IsFeederEmpty(ushort returnCode, ushort conditionCode) =>
        conditionCode == TWCC_NOMEDIA;

    /// <summary>
    /// Traduit un code de condition TWAIN vers un ErrorCode OpenScan.
    /// </summary>
    public static ErrorCode MapErrorCode(ushort conditionCode) =>
        conditionCode switch
        {
            TWCC_NODS => ErrorCode.ScannerNotFound,
            TWCC_MAXCONNECTIONS or TWCC_DENIED => ErrorCode.ScannerBusy,
            TWCC_CHECKDEVICEONLINE => ErrorCode.ScannerDisconnected,
            TWCC_PAPERJAM => ErrorCode.PaperJam,
            TWCC_PAPERDOUBLEFEED => ErrorCode.MultiFeed,
            TWCC_NOMEDIA => ErrorCode.NoPaper,
            TWCC_CAPUNSUPPORTED or TWCC_BADCAP => ErrorCode.CapabilityUnsupported,
            TWCC_BADVALUE => ErrorCode.InvalidConfiguration,
            TWCC_OPERATIONERROR => ErrorCode.DriverError,
            TWCC_FILEWRITEERROR => ErrorCode.StorageError,
            _ => ErrorCode.DriverError
        };

    /// <summary>
    /// Détermine si l'erreur est potentiellement récupérable (ex. bourrage résolu par l'utilisateur).
    /// </summary>
    public static bool IsRecoverable(ErrorCode code) =>
        code switch
        {
            ErrorCode.PaperJam => true,
            ErrorCode.NoPaper => true,
            ErrorCode.MultiFeed => true,
            ErrorCode.ScannerBusy => true,
            ErrorCode.Timeout => true,
            _ => false
        };
}

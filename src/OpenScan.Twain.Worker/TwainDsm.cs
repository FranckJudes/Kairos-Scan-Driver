using System.Runtime.InteropServices;

namespace OpenScan.Twain.Worker;

#pragma warning disable CA1401 // P/Invokes should not be visible

/// <summary>
/// Définition P/Invoke et structures standard TWAIN 2.x et Win32.
/// Seul ce projet touche au natif : isolé dans le worker séparé (section 6.5).
/// </summary>
public static class TwainDsm
{
    // Constantes Data Groups
    public const uint DG_CONTROL = 0x0001;
    public const uint DG_IMAGE = 0x0002;

    // Constantes Data Argument Types
    public const ushort DAT_NULL = 0x0000;
    public const ushort DAT_CAPABILITY = 0x0001;
    public const ushort DAT_EVENT = 0x0002;
    public const ushort DAT_IDENTITY = 0x0003;
    public const ushort DAT_PARENT = 0x0004;
    public const ushort DAT_PENDINGXFERS = 0x0005;
    public const ushort DAT_STATUS = 0x0008;
    public const ushort DAT_USERINTERFACE = 0x0009;
    public const ushort DAT_IMAGEINFO = 0x0101;
    public const ushort DAT_IMAGENATIVEXFER = 0x0104;

    // Constantes Messages
    public const ushort MSG_GET = 0x0001;
    public const ushort MSG_GETCURRENT = 0x0002;
    public const ushort MSG_GETDEFAULT = 0x0003;
    public const ushort MSG_GETFIRST = 0x0004;
    public const ushort MSG_GETNEXT = 0x0005;
    public const ushort MSG_SET = 0x0006;
    public const ushort MSG_RESET = 0x0007;
    public const ushort MSG_XFERREADY = 0x0101;
    public const ushort MSG_CLOSEDSREQ = 0x0102;
    public const ushort MSG_CLOSEDSOK = 0x0103;
    public const ushort MSG_DEVICEEVENT = 0x0104;
    public const ushort MSG_OPENDSM = 0x0301;
    public const ushort MSG_CLOSEDSM = 0x0302;
    public const ushort MSG_OPENDS = 0x0401;
    public const ushort MSG_CLOSEDS = 0x0402;
    public const ushort MSG_DISABLEDS = 0x0501;
    public const ushort MSG_ENABLEDS = 0x0502;
    public const ushort MSG_PROCESSEVENT = 0x0601;
    public const ushort MSG_ENDXFER = 0x0701;

    // Constantes Capabilities
    public const ushort CAP_FEEDERENABLED = 0x1002;
    public const ushort CAP_FEEDERLOADED = 0x1003;
    public const ushort CAP_AUTOSCAN = 0x1010;
    public const ushort CAP_DUPLEX = 0x1012;
    public const ushort CAP_DUPLEXENABLED = 0x1013;
    public const ushort ICAP_PIXELTYPE = 0x0101;
    public const ushort ICAP_UNITS = 0x0102;
    public const ushort ICAP_XRESOLUTION = 0x1118;
    public const ushort ICAP_YRESOLUTION = 0x1119;
    public const ushort ICAP_SUPPORTEDSIZES = 0x1122;

    // Conteneurs TWAIN
    public const ushort TWON_ARRAY = 3;
    public const ushort TWON_ENUMERATION = 4;
    public const ushort TWON_ONEVALUE = 5;
    public const ushort TWON_RANGE = 6;

    // Types d'items
    public const ushort TWTY_INT8 = 0x0000;
    public const ushort TWTY_INT16 = 0x0001;
    public const ushort TWTY_INT32 = 0x0002;
    public const ushort TWTY_UINT8 = 0x0003;
    public const ushort TWTY_UINT16 = 0x0004;
    public const ushort TWTY_UINT32 = 0x0005;
    public const ushort TWTY_BOOL = 0x0006;
    public const ushort TWTY_FIX32 = 0x0007;

    // Délégué DSM_Entry
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate ushort DsmEntryDelegate(
        ref TW_IDENTITY pOrigin,
        IntPtr pDest,
        uint dg,
        ushort dat,
        ushort msg,
        IntPtr pData);

    // Structure TWAIN TW_VERSION
    [StructLayout(LayoutKind.Sequential, Pack = 2, CharSet = CharSet.Ansi)]
    public struct TW_VERSION
    {
        public ushort MajorNum;
        public ushort MinorNum;
        public ushort Language;
        public ushort Country;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)]
        public string Info;
    }

    // Structure TWAIN TW_IDENTITY (taille fixe 156 octets)
    [StructLayout(LayoutKind.Sequential, Pack = 2, CharSet = CharSet.Ansi)]
    public struct TW_IDENTITY
    {
        public uint Id;
        public TW_VERSION Version;
        public ushort ProtocolMajor;
        public ushort ProtocolMinor;
        public uint SupportedGroups;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)]
        public string Manufacturer;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)]
        public string ProductFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)]
        public string ProductName;
    }

    // Structure TWAIN TW_STATUS
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_STATUS
    {
        public ushort ConditionCode;
        public ushort Data;
    }

    // Structure TWAIN TW_USERINTERFACE
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_USERINTERFACE
    {
        public ushort ShowUI;
        public ushort ModalUI;
        public IntPtr hParent;
    }

    // Structure TWAIN TW_EVENT
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_EVENT
    {
        public IntPtr pEvent;
        public ushort TWMessage;
    }

    // Structure TWAIN TW_CAPABILITY
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_CAPABILITY
    {
        public ushort Cap;
        public ushort ConType;
        public IntPtr hContainer;
    }

    // Structure TWAIN TW_ONEVALUE
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_ONEVALUE
    {
        public ushort ItemType;
        public uint Item;
    }

    // Structure TWAIN TW_FIX32
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_FIX32
    {
        public short Whole;
        public ushort Frac;

        public float ToFloat() => Whole + (Frac / 65536.0f);

        public static TW_FIX32 FromFloat(float value)
        {
            var whole = (short)Math.Floor(value);
            var frac = (ushort)Math.Round((value - whole) * 65536.0f);
            return new TW_FIX32 { Whole = whole, Frac = frac };
        }
    }

    // Structure TWAIN TW_RANGE
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_RANGE
    {
        public ushort ItemType;
        public uint MinValue;
        public uint MaxValue;
        public uint StepSize;
        public uint DefaultValue;
        public uint CurrentValue;
    }

    // Structure TWAIN TW_ENUMERATION
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_ENUMERATION
    {
        public ushort ItemType;
        public uint NumItems;
        public uint CurrentIndex;
        public uint DefaultIndex;
    }

    // Structure TWAIN TW_PENDINGXFERS
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_PENDINGXFERS
    {
        public ushort Count;
        public uint EOJ;
    }

    // Structure TWAIN TW_IMAGEINFO
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_IMAGEINFO
    {
        public TW_FIX32 XResolution;
        public TW_FIX32 YResolution;
        public int ImageWidth;
        public int ImageLength;
        public short SamplesPerPixel;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public short[] BitsPerSample;
        public short BitsPerPixel;
        public ushort Planar;
        public short PixelType;
        public ushort Compression;
    }

    // Win32 MSG struct
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    // Win32 BMP Headers
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct BITMAPFILEHEADER
    {
        public ushort bfType;
        public uint bfSize;
        public ushort bfReserved1;
        public ushort bfReserved2;
        public uint bfOffBits;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    // Win32 Kernel32
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern UIntPtr GlobalSize(IntPtr hMem);

    // Win32 User32
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    public const uint PM_REMOVE = 0x0001;
    public const uint WM_QUIT = 0x0012;
}

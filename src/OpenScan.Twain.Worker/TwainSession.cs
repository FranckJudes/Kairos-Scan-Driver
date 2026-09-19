using System.Runtime.InteropServices;
using OpenScan.Backends.Twain;
using OpenScan.Contracts;
using static OpenScan.Twain.Worker.TwainDsm;
using static OpenScan.Backends.Twain.TwainErrorMapper;

namespace OpenScan.Twain.Worker;

/// <summary>
/// Gestionnaire de session TWAIN avec machine à états 7 niveaux (section 6.5).
/// Exécute les appels DSM natifs et la boucle de messages Win32 dans ce processus worker.
/// </summary>
public sealed class TwainSession : IDisposable
{
    private IntPtr _dsmHandle = IntPtr.Zero;
    private DsmEntryDelegate? _dsmEntry;
    private IntPtr _windowHandle = IntPtr.Zero;

    private TW_IDENTITY _appIdentity;
    private TW_IDENTITY _dsIdentity;
    private bool _dsmOpen;
    private bool _dsOpen;
    private bool _dsEnabled;

    public TwainSession()
    {
        _appIdentity = new TW_IDENTITY
        {
            Version = new TW_VERSION { MajorNum = 2, MinorNum = 4, Language = 1, Country = 1, Info = "OpenScan 1.0" },
            ProtocolMajor = 2,
            ProtocolMinor = 4,
            SupportedGroups = DG_CONTROL | DG_IMAGE,
            Manufacturer = "OpenScan",
            ProductFamily = "Engine",
            ProductName = "OpenScan Worker"
        };
    }

    public void Initialize()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("TWAIN requiert un environnement Windows.");
        }

        var loadResult = TwainDsmLocator.TryLoadDsm();
        if (!loadResult.Success)
        {
            throw new DllNotFoundException(loadResult.DiagnosticSummary);
        }

        _dsmHandle = loadResult.Handle;

        var entryPtr = NativeLibrary.GetExport(_dsmHandle, "DSM_Entry");
        _dsmEntry = Marshal.GetDelegateForFunctionPointer<DsmEntryDelegate>(entryPtr);

        // Création d'une fenêtre masquée Win32 pour la boucle de messages TWAIN
        _windowHandle = CreateWindowExW(
            0,
            "STATIC",
            "OpenScanTwainMessageWindow",
            0,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Échec de la création de la fenêtre Win32. Code: {Marshal.GetLastPInvokeError()}");
        }

        // Ouverture du DSM (État 3)
        var winHandlePtr = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(winHandlePtr, _windowHandle);
        try
        {
            var rc = InvokeDsm(ref _appIdentity, DG_CONTROL, DAT_PARENT, MSG_OPENDSM, winHandlePtr);
            if (rc != TWRC_SUCCESS)
            {
                throw new InvalidOperationException($"Échec MSG_OPENDSM (code TWAIN {rc})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(winHandlePtr);
        }

        _dsmOpen = true;
    }

    public List<Scanner> DiscoverScanners()
    {
        EnsureInitialized();
        var scanners = new List<Scanner>();

        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<TW_IDENTITY>());
        try
        {
            var rc = InvokeDsm(ref _appIdentity, DG_CONTROL, DAT_IDENTITY, MSG_GETFIRST, ptr);
            while (rc == TWRC_SUCCESS)
            {
                var ds = Marshal.PtrToStructure<TW_IDENTITY>(ptr);
                var id = string.IsNullOrWhiteSpace(ds.ProductName) ? ds.Id.ToString() : ds.ProductName;
                scanners.Add(new Scanner
                {
                    Id = id,
                    Name = ds.ProductName,
                    Manufacturer = string.IsNullOrWhiteSpace(ds.Manufacturer) ? "TWAIN" : ds.Manufacturer,
                    Model = ds.ProductFamily,
                    Backend = "twain",
                    ConnectionType = ConnectionType.Usb,
                    Status = ScannerStatus.Idle
                });

                rc = InvokeDsm(ref _appIdentity, DG_CONTROL, DAT_IDENTITY, MSG_GETNEXT, ptr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        return scanners;
    }

    public ScannerCapabilities GetCapabilities(string scannerId)
    {
        EnsureInitialized();
        OpenDataSource(scannerId);

        var raw = new TwainRawCapabilities();
        try
        {
            // Inspection Feeder
            raw.HasFeeder = QueryCapBool(CAP_FEEDERENABLED) || QueryCapBool(CAP_FEEDERLOADED);
            raw.HasFlatbed = true; // La grande majorité des scanners ont un flatbed ou fallback
            raw.HasDuplex = QueryCapBool(CAP_DUPLEXENABLED) || QueryCapEnumValues(CAP_DUPLEX).Any(v => v > 0);

            // Inspection Résolutions
            raw.DiscreteResolutions = QueryResolutions(out var range);
            raw.ResolutionRange = range;

            // Inspection PixelTypes
            raw.PixelTypes = QueryCapEnumValues(ICAP_PIXELTYPE);

            // Inspection PaperSizes
            raw.SupportedSizes = QueryCapEnumValues(ICAP_SUPPORTEDSIZES);
        }
        finally
        {
            CloseDataSource();
        }

        return TwainCapabilityMapper.Map(raw);
    }

    public void Configure(string scannerId, ConfigureCommand config)
    {
        EnsureInitialized();
        OpenDataSource(scannerId);

        // 1. Source (ADF vs Flatbed)
        var isAdf = config.Source.Equals("adf", StringComparison.OrdinalIgnoreCase);
        SetCapBool(CAP_FEEDERENABLED, isAdf);

        // 2. Duplex
        if (config.Duplex)
        {
            SetCapBool(CAP_DUPLEXENABLED, true);
        }

        // 3. Résolution
        SetCapFix32(ICAP_XRESOLUTION, config.Resolution);
        SetCapFix32(ICAP_YRESOLUTION, config.Resolution);

        // 4. Mode couleur
        var twPixelType = TwainCapabilityMapper.MapToTwainPixelType(config.PixelFormat);
        SetCapUint16(ICAP_PIXELTYPE, (ushort)twPixelType);

        // 5. Format de papier
        var twSize = TwainCapabilityMapper.MapToTwainPaperSize(config.PageSize);
        if (twSize.HasValue)
        {
            SetCapUint16(ICAP_SUPPORTEDSIZES, (ushort)twSize.Value);
        }
    }

    public void Acquire(string scannerId, string jobId, Action<PageAcquiredResponse> onPageAcquired, Action onBatchComplete)
    {
        EnsureInitialized();
        if (!_dsOpen)
        {
            OpenDataSource(scannerId);
        }

        // État 5 : Activer la source sans interface utilisateur (ShowUI = 0)
        var ui = new TW_USERINTERFACE
        {
            ShowUI = 0,
            ModalUI = 0,
            hParent = _windowHandle
        };

        var uiPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TW_USERINTERFACE>());
        Marshal.StructureToPtr(ui, uiPtr, false);

        try
        {
            var rc = InvokeDsm(ref _appIdentity, ref _dsIdentity, DG_CONTROL, DAT_USERINTERFACE, MSG_ENABLEDS, uiPtr);
            if (rc != TWRC_SUCCESS && rc != TWRC_CHECKSTATUS)
            {
                var status = GetStatus();
                throw new InvalidOperationException($"Échec MSG_ENABLEDS. RC={rc}, CC={status.ConditionCode}");
            }

            _dsEnabled = true;

            // Répertoire temporaire de travail pour ce job
            var tempDir = Path.Combine(Path.GetTempPath(), "openscan-twain", jobId);
            Directory.CreateDirectory(tempDir);

            // Boucle de pompage de messages Win32 (États 5 -> 6 -> 7)
            var pageNumber = 0;
            var stopRequested = false;

            while (_dsEnabled && !stopRequested)
            {
                if (GetMessageW(out var msg, IntPtr.Zero, 0, 0) == -1)
                {
                    break;
                }

                var twEvent = new TW_EVENT
                {
                    pEvent = Marshal.AllocHGlobal(Marshal.SizeOf<MSG>()),
                    TWMessage = 0
                };
                Marshal.StructureToPtr(msg, twEvent.pEvent, false);

                var eventPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TW_EVENT>());
                Marshal.StructureToPtr(twEvent, eventPtr, false);

                ushort eventRc = 0;
                try
                {
                    eventRc = InvokeDsm(ref _appIdentity, ref _dsIdentity, DG_CONTROL, DAT_EVENT, MSG_PROCESSEVENT, eventPtr);
                    twEvent = Marshal.PtrToStructure<TW_EVENT>(eventPtr);
                }
                finally
                {
                    Marshal.FreeHGlobal(twEvent.pEvent);
                    Marshal.FreeHGlobal(eventPtr);
                }

                if (twEvent.TWMessage == MSG_XFERREADY)
                {
                    // Transfert des images disponibles
                    var transferMore = true;
                    while (transferMore)
                    {
                        var imageInfo = GetImageInfo();
                        var bitmapHandlePtr = Marshal.AllocHGlobal(IntPtr.Size);
                        Marshal.WriteIntPtr(bitmapHandlePtr, IntPtr.Zero);

                        ushort xferRc = 0;
                        IntPtr hBitmap = IntPtr.Zero;
                        try
                        {
                            xferRc = InvokeDsm(ref _appIdentity, ref _dsIdentity, DG_IMAGE, DAT_IMAGENATIVEXFER, MSG_GET, bitmapHandlePtr);
                            hBitmap = Marshal.ReadIntPtr(bitmapHandlePtr);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(bitmapHandlePtr);
                        }

                        if (xferRc == TWRC_XFERDONE && hBitmap != IntPtr.Zero)
                        {
                            pageNumber++;
                            var filePath = Path.Combine(tempDir, $"page-{pageNumber}.bmp");
                            SaveDibToBmpFile(hBitmap, filePath);

                            onPageAcquired(new PageAcquiredResponse(
                                jobId,
                                pageNumber,
                                filePath,
                                imageInfo.ImageWidth,
                                imageInfo.ImageLength,
                                (int)Math.Round(imageInfo.XResolution.ToFloat()),
                                TwainCapabilityMapper.MapToTwainPixelType("color") == imageInfo.PixelType ? "color" : "grayscale",
                                "adf"
                            ));

                            // Avertir la source que le transfert de la page est terminé
                            var pending = new TW_PENDINGXFERS();
                            var pendingPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TW_PENDINGXFERS>());
                            Marshal.StructureToPtr(pending, pendingPtr, false);

                            try
                            {
                                InvokeDsm(ref _appIdentity, ref _dsIdentity, DG_CONTROL, DAT_PENDINGXFERS, MSG_ENDXFER, pendingPtr);
                                pending = Marshal.PtrToStructure<TW_PENDINGXFERS>(pendingPtr);
                                if (pending.Count == 0)
                                {
                                    transferMore = false;
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(pendingPtr);
                            }
                        }
                        else if (xferRc == TWRC_CANCEL)
                        {
                            transferMore = false;
                            stopRequested = true;
                        }
                        else
                        {
                            // Échec du transfert : vérifier le code condition
                            var status = GetStatus();
                            if (IsFeederEmpty(xferRc, status.ConditionCode))
                            {
                                // Bac ADF vide : fin normale du lot !
                                transferMore = false;
                                stopRequested = true;
                            }
                            else
                            {
                                var errCode = MapErrorCode(status.ConditionCode);
                                throw new InvalidOperationException($"Erreur transfert image: TWAIN RC={xferRc}, CC={status.ConditionCode} ({errCode})");
                            }
                        }
                    }

                    stopRequested = true;
                }
                else if (twEvent.TWMessage == MSG_CLOSEDSREQ)
                {
                    stopRequested = true;
                }
                else if (eventRc == TWRC_NOTDSEVENT)
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }

            onBatchComplete();
        }
        finally
        {
            Marshal.FreeHGlobal(uiPtr);
            DisableDataSource();
            CloseDataSource();
        }
    }

    private void OpenDataSource(string scannerId)
    {
        if (_dsOpen)
        {
            CloseDataSource();
        }

        var scanners = DiscoverScanners();
        var target = scanners.FirstOrDefault(s => s.Id.Equals(scannerId, StringComparison.OrdinalIgnoreCase)
                                               || s.Name.Equals(scannerId, StringComparison.OrdinalIgnoreCase));

        _dsIdentity = new TW_IDENTITY
        {
            ProductName = target?.Name ?? scannerId,
            ProtocolMajor = 2,
            ProtocolMinor = 4,
            SupportedGroups = DG_CONTROL | DG_IMAGE
        };

        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<TW_IDENTITY>());
        Marshal.StructureToPtr(_dsIdentity, ptr, false);
        try
        {
            var rc = InvokeDsm(ref _appIdentity, DG_CONTROL, DAT_IDENTITY, MSG_OPENDS, ptr);
            if (rc != TWRC_SUCCESS && rc != TWRC_CHECKSTATUS)
            {
                var status = GetStatus();
                throw new InvalidOperationException($"Impossible d'ouvrir le scanner TWAIN '{scannerId}'. RC={rc}, CC={status.ConditionCode}");
            }
            _dsIdentity = Marshal.PtrToStructure<TW_IDENTITY>(ptr);
            _dsOpen = true;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void DisableDataSource()
    {
        if (!_dsEnabled) return;
        var ui = new TW_USERINTERFACE();
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<TW_USERINTERFACE>());
        Marshal.StructureToPtr(ui, ptr, false);
        try
        {
            InvokeDsm(ref _appIdentity, ref _dsIdentity, DG_CONTROL, DAT_USERINTERFACE, MSG_DISABLEDS, ptr);
        }
        catch { /* ignore */ }
        finally
        {
            Marshal.FreeHGlobal(ptr);
            _dsEnabled = false;
        }
    }

    private void CloseDataSource()
    {
        DisableDataSource();
        if (!_dsOpen) return;

        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<TW_IDENTITY>());
        Marshal.StructureToPtr(_dsIdentity, ptr, false);
        try
        {
            InvokeDsm(ref _appIdentity, DG_CONTROL, DAT_IDENTITY, MSG_CLOSEDS, ptr);
        }
        catch { /* ignore */ }
        finally
        {
            Marshal.FreeHGlobal(ptr);
            _dsOpen = false;
        }
    }

    private TW_STATUS GetStatus()
    {
        var status = new TW_STATUS();
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<TW_STATUS>());
        Marshal.StructureToPtr(status, ptr, false);
        try
        {
            if (_dsOpen)
            {
                InvokeDsm(ref _appIdentity, ref _dsIdentity, DG_CONTROL, DAT_STATUS, MSG_GET, ptr);
            }
            else
            {
                InvokeDsm(ref _appIdentity, DG_CONTROL, DAT_STATUS, MSG_GET, ptr);
            }
            return Marshal.PtrToStructure<TW_STATUS>(ptr);
        }
        catch
        {
            return new TW_STATUS { ConditionCode = TWCC_BUMMER };
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private TW_IMAGEINFO GetImageInfo()
    {
        var info = new TW_IMAGEINFO();
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<TW_IMAGEINFO>());
        Marshal.StructureToPtr(info, ptr, false);
        try
        {
            InvokeDsm(ref _appIdentity, ref _dsIdentity, DG_IMAGE, DAT_IMAGEINFO, MSG_GET, ptr);
            return Marshal.PtrToStructure<TW_IMAGEINFO>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // ----------------- Helpers Capabilities -----------------

    private bool QueryCapBool(ushort cap)
    {
        var values = QueryCapEnumValues(cap);
        return values.Any(v => v != 0);
    }

    private List<int> QueryCapEnumValues(ushort cap)
    {
        var results = new List<int>();
        var twCap = new TW_CAPABILITY { Cap = cap, ConType = 0xFFFF, hContainer = IntPtr.Zero };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<TW_CAPABILITY>());
        Marshal.StructureToPtr(twCap, ptr, false);

        try
        {
            var rc = InvokeDsm(ref _appIdentity, ref _dsIdentity, DG_CONTROL, DAT_CAPABILITY, MSG_GET, ptr);
            if (rc != TWRC_SUCCESS && rc != TWRC_CHECKSTATUS)
            {
                return results;
            }

            twCap = Marshal.PtrToStructure<TW_CAPABILITY>(ptr);
            if (twCap.hContainer == IntPtr.Zero)
            {
                return results;
            }

            var locked = GlobalLock(twCap.hContainer);
            if (locked == IntPtr.Zero)
            {
                return results;
            }

            try
            {
                if (twCap.ConType == TWON_ONEVALUE)
                {
                    var one = Marshal.PtrToStructure<TW_ONEVALUE>(locked);
                    results.Add((int)one.Item);
                }
                else if (twCap.ConType == TWON_ENUMERATION)
                {
                    var count = (int)Marshal.ReadInt32(locked, 2); // NumItems
                    var itemPtr = locked + 10;
                    for (var i = 0; i < count; i++)
                    {
                        results.Add(Marshal.ReadInt32(itemPtr, i * 4));
                    }
                }
                else if (twCap.ConType == TWON_RANGE)
                {
                    var range = Marshal.PtrToStructure<TW_RANGE>(locked);
                    results.Add((int)range.CurrentValue);
                }
            }
            finally
            {
                GlobalUnlock(twCap.hContainer);
                GlobalFree(twCap.hContainer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        return results;
    }

    private List<float> QueryResolutions(out (float Min, float Max, float Step)? rangeOut)
    {
        rangeOut = null;
        var results = new List<float>();
        var twCap = new TW_CAPABILITY { Cap = ICAP_XRESOLUTION, ConType = 0xFFFF, hContainer = IntPtr.Zero };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<TW_CAPABILITY>());
        Marshal.StructureToPtr(twCap, ptr, false);

        try
        {
            var rc = InvokeDsm(ref _appIdentity, ref _dsIdentity, DG_CONTROL, DAT_CAPABILITY, MSG_GET, ptr);
            if (rc != TWRC_SUCCESS) return results;

            twCap = Marshal.PtrToStructure<TW_CAPABILITY>(ptr);
            if (twCap.hContainer == IntPtr.Zero) return results;

            var locked = GlobalLock(twCap.hContainer);
            if (locked == IntPtr.Zero) return results;

            try
            {
                if (twCap.ConType == TWON_RANGE)
                {
                    var minFix = Marshal.PtrToStructure<TW_FIX32>(locked + 2);
                    var maxFix = Marshal.PtrToStructure<TW_FIX32>(locked + 6);
                    var stepFix = Marshal.PtrToStructure<TW_FIX32>(locked + 10);
                    rangeOut = (minFix.ToFloat(), maxFix.ToFloat(), stepFix.ToFloat());
                }
                else if (twCap.ConType == TWON_ONEVALUE)
                {
                    var fix = Marshal.PtrToStructure<TW_FIX32>(locked + 2);
                    results.Add(fix.ToFloat());
                }
                else if (twCap.ConType == TWON_ENUMERATION)
                {
                    var numItems = Marshal.ReadInt32(locked, 2);
                    var itemOffset = locked + 10;
                    for (var i = 0; i < numItems; i++)
                    {
                        var fix = Marshal.PtrToStructure<TW_FIX32>(itemOffset + (i * Marshal.SizeOf<TW_FIX32>()));
                        results.Add(fix.ToFloat());
                    }
                }
            }
            finally
            {
                GlobalUnlock(twCap.hContainer);
                GlobalFree(twCap.hContainer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        return results;
    }

    private void SetCapBool(ushort cap, bool value)
    {
        SetCapUint16(cap, (ushort)(value ? 1 : 0));
    }

    private void SetCapUint16(ushort cap, ushort value)
    {
        var twOne = new TW_ONEVALUE { ItemType = TWTY_UINT16, Item = value };
        var hContainer = Marshal.AllocHGlobal(Marshal.SizeOf<TW_ONEVALUE>());
        Marshal.StructureToPtr(twOne, hContainer, false);

        var twCap = new TW_CAPABILITY { Cap = cap, ConType = TWON_ONEVALUE, hContainer = hContainer };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<TW_CAPABILITY>());
        Marshal.StructureToPtr(twCap, ptr, false);

        try
        {
            InvokeDsm(ref _appIdentity, ref _dsIdentity, DG_CONTROL, DAT_CAPABILITY, MSG_SET, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(hContainer);
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void SetCapFix32(ushort cap, float value)
    {
        var fix = TW_FIX32.FromFloat(value);
        var hContainer = Marshal.AllocHGlobal(Marshal.SizeOf<TW_FIX32>() + 2);
        Marshal.WriteInt16(hContainer, 0, (short)TWTY_FIX32);
        Marshal.StructureToPtr(fix, hContainer + 2, false);

        var twCap = new TW_CAPABILITY { Cap = cap, ConType = TWON_ONEVALUE, hContainer = hContainer };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<TW_CAPABILITY>());
        Marshal.StructureToPtr(twCap, ptr, false);

        try
        {
            InvokeDsm(ref _appIdentity, ref _dsIdentity, DG_CONTROL, DAT_CAPABILITY, MSG_SET, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(hContainer);
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static void SaveDibToBmpFile(IntPtr hDIB, string outputPath)
    {
        var pDib = GlobalLock(hDIB);
        if (pDib == IntPtr.Zero)
        {
            throw new InvalidOperationException("GlobalLock a échoué sur le handle DIB TWAIN.");
        }

        try
        {
            var header = Marshal.PtrToStructure<BITMAPINFOHEADER>(pDib);
            var colorTableSize = header.biBitCount switch
            {
                1 => 2 * 4,
                4 => 16 * 4,
                8 => 256 * 4,
                _ => 0
            };

            var dibSize = (int)GlobalSize(hDIB);
            if (dibSize <= 0)
            {
                dibSize = (int)(header.biSize + colorTableSize + header.biSizeImage);
            }

            var bfh = new BITMAPFILEHEADER
            {
                bfType = 0x4D42, // 'BM'
                bfSize = (uint)(14 + dibSize),
                bfReserved1 = 0,
                bfReserved2 = 0,
                bfOffBits = (uint)(14 + header.biSize + colorTableSize)
            };

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
            using var writer = new BinaryWriter(fs);

            writer.Write(bfh.bfType);
            writer.Write(bfh.bfSize);
            writer.Write(bfh.bfReserved1);
            writer.Write(bfh.bfReserved2);
            writer.Write(bfh.bfOffBits);

            var dibBuffer = new byte[dibSize];
            Marshal.Copy(pDib, dibBuffer, 0, dibSize);
            writer.Write(dibBuffer);
        }
        finally
        {
            GlobalUnlock(hDIB);
            GlobalFree(hDIB);
        }
    }

    private ushort InvokeDsm(ref TW_IDENTITY app, ref TW_IDENTITY ds, uint dg, ushort dat, ushort msg, IntPtr pData)
    {
        var dsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TW_IDENTITY>());
        try
        {
            Marshal.StructureToPtr(ds, dsPtr, false);
            var rc = _dsmEntry!(ref app, dsPtr, dg, dat, msg, pData);
            ds = Marshal.PtrToStructure<TW_IDENTITY>(dsPtr);
            return rc;
        }
        finally
        {
            Marshal.FreeHGlobal(dsPtr);
        }
    }

    private ushort InvokeDsm(ref TW_IDENTITY app, uint dg, ushort dat, ushort msg, IntPtr pData)
    {
        return _dsmEntry!(ref app, IntPtr.Zero, dg, dat, msg, pData);
    }

    private void EnsureInitialized()
    {
        if (!_dsmOpen || _dsmEntry is null)
        {
            Initialize();
        }
    }

    public void Dispose()
    {
        try
        {
            CloseDataSource();
            if (_dsmOpen && _dsmEntry is not null)
            {
                var winHandlePtr = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(winHandlePtr, _windowHandle);
                try
                {
                    InvokeDsm(ref _appIdentity, DG_CONTROL, DAT_PARENT, MSG_CLOSEDSM, winHandlePtr);
                }
                finally
                {
                    Marshal.FreeHGlobal(winHandlePtr);
                }
                _dsmOpen = false;
            }

            if (_windowHandle != IntPtr.Zero)
            {
                DestroyWindow(_windowHandle);
                _windowHandle = IntPtr.Zero;
            }

            if (_dsmHandle != IntPtr.Zero)
            {
                NativeLibrary.Free(_dsmHandle);
                _dsmHandle = IntPtr.Zero;
            }
        }
        catch { /* ignore cleanup errors */ }
    }
}

// Screen capture via DXGI Desktop Duplication (Vortice) with a GDI BitBlt fallback.
//
// Primary path: D3D11 device (BGRA support) -> OutputDuplication.AcquireNextFrame
// with a timeout -> staging texture -> CPU read -> tightly packed BGRA bytes.
// The DXGI desktop texture never contains the cursor, and the GDI fallback path
// never draws one, so the hideCursor flag is informational only.
// Static-image optimization: a duplication timeout means "no new frame" and is
// reported as false without counting as a failure. After 3 consecutive real DXGI
// failures the instance permanently falls back to GDI BitBlt for its lifetime.
// Multi-DPI: display geometry comes from Win32 EnumDisplaySettings (DEVMODE), and
// the DXGI output is matched by device name with a desktop-coordinate fallback.
// TryGetFrame never throws; all errors are swallowed and reported as false.

using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NdiScreenshare.Capture;

public sealed class DesktopDuplicator : IDisposable
{
    private const int AcquireTimeoutMs = 200;
    private const int MaxDxgiFailuresBeforeGdiFallback = 3;

    private const int DisplayDevicePrimary = 0x4;
    private const int DisplayDeviceMirroringDriver = 0x8;
    private const int EnumCurrentSettings = -1;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint Srccopy = 0x00CC0020;
    private const uint DibRgbColors = 0;
    private const int BiRgb = 0;

    private static readonly bool s_vorticePresent = ProbeVortice();

    private readonly object _gate = new();
    private readonly DisplayInfo _target;
    private readonly double _scale;
    private readonly bool _hideCursor;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private int _stagingWidth;
    private int _stagingHeight;
    private int _dxgiFailures;
    private bool _useGdiFallback;
    private bool _disposed;

    public DesktopDuplicator(string deviceNameOrEmptyForPrimary, bool hideCursor = true, double scale = 1.0)
    {
        _hideCursor = hideCursor;
        _scale = (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0.0 || scale > 1.0) ? 1.0 : scale;
        // Informational: the DXGI desktop texture excludes the cursor by design
        // and the GDI fallback path never draws one.
        _ = _hideCursor;

        DisplayInfo target;
        try
        {
            target = ResolveTarget(deviceNameOrEmptyForPrimary);
        }
        catch
        {
            target = new DisplayInfo
            {
                DeviceName = "",
                FriendlyName = "Primary Display",
                IsPrimary = true,
                Width = 1920,
                Height = 1080,
                Left = 0,
                Top = 0,
            };
        }

        _target = target;

        bool dxgiReady = false;
        try
        {
            dxgiReady = s_vorticePresent && InitDxgi();
        }
        catch
        {
            dxgiReady = false;
        }

        if (!dxgiReady)
        {
            _useGdiFallback = true;
        }
    }

    public static List<DisplayInfo> GetDisplays()
    {
        var list = new List<DisplayInfo>();
        try
        {
            for (int adapterIndex = 0; adapterIndex < 64; adapterIndex++)
            {
                var adapter = new DisplayDevice();
                adapter.cb = Marshal.SizeOf<DisplayDevice>();

                bool ok;
                try
                {
                    ok = EnumDisplayDevices(null, adapterIndex, ref adapter, 0);
                }
                catch
                {
                    break;
                }

                if (!ok)
                {
                    break;
                }

                if ((adapter.StateFlags & DisplayDeviceMirroringDriver) != 0)
                {
                    continue;
                }

                string friendly = adapter.DeviceString;
                try
                {
                    var monitor = new DisplayDevice();
                    monitor.cb = Marshal.SizeOf<DisplayDevice>();
                    if (EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0) &&
                        !string.IsNullOrWhiteSpace(monitor.DeviceString))
                    {
                        friendly = monitor.DeviceString.Trim();
                    }
                }
                catch
                {
                    // Keep the adapter string.
                }

                int w = 0, h = 0, left = 0, top = 0;
                try
                {
                    var mode = new DevMode();
                    mode.dmSize = (short)Marshal.SizeOf<DevMode>();
                    if (EnumDisplaySettings(adapter.DeviceName, EnumCurrentSettings, ref mode) &&
                        mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0)
                    {
                        w = mode.dmPelsWidth;
                        h = mode.dmPelsHeight;
                        left = mode.dmPositionX;
                        top = mode.dmPositionY;
                    }
                }
                catch
                {
                    // Leave dimensions at zero; entry is skipped below.
                }

                if (w <= 0 || h <= 0)
                {
                    continue;
                }

                list.Add(new DisplayInfo
                {
                    DeviceName = adapter.DeviceName ?? "",
                    FriendlyName = string.IsNullOrWhiteSpace(friendly) ? (adapter.DeviceName ?? "") : friendly,
                    IsPrimary = (adapter.StateFlags & DisplayDevicePrimary) != 0,
                    Width = w,
                    Height = h,
                    Left = left,
                    Top = top,
                });
            }
        }
        catch
        {
            // Return whatever was collected (possibly empty).
        }

        if (list.Count == 0)
        {
            try
            {
                int w = GetSystemMetrics(SmCxScreen);
                int h = GetSystemMetrics(SmCyScreen);
                if (w > 0 && h > 0)
                {
                    list.Add(new DisplayInfo
                    {
                        DeviceName = "",
                        FriendlyName = "Primary Display",
                        IsPrimary = true,
                        Width = w,
                        Height = h,
                        Left = 0,
                        Top = 0,
                    });
                }
            }
            catch
            {
                // No fallback available.
            }
        }

        return list;
    }

    public bool TryGetFrame(out CapturedFrame? frame)
    {
        frame = null;
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                if (_target.Width <= 0 || _target.Height <= 0)
                {
                    return false;
                }

                if (!_useGdiFallback && _duplication != null)
                {
                    return TryGetFrameDxgi(out frame);
                }

                return TryGetFrameGdi(out frame);
            }
        }
        catch
        {
            frame = null;
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    ShutdownDxgi();
                }
                catch
                {
                    // Never throw from Dispose.
                }
            }
        }
        catch
        {
            // Never throw from Dispose.
        }

        GC.SuppressFinalize(this);
    }

    private static bool ProbeVortice()
    {
        try
        {
            _ = typeof(IDXGIFactory1);
            _ = typeof(ID3D11Device);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DisplayInfo ResolveTarget(string deviceNameOrEmptyForPrimary)
    {
        List<DisplayInfo> displays = GetDisplays();
        if (displays.Count == 0)
        {
            return new DisplayInfo
            {
                DeviceName = "",
                FriendlyName = "Primary Display",
                IsPrimary = true,
                Width = 1920,
                Height = 1080,
                Left = 0,
                Top = 0,
            };
        }

        if (!string.IsNullOrEmpty(deviceNameOrEmptyForPrimary))
        {
            foreach (DisplayInfo d in displays)
            {
                if (string.Equals(d.DeviceName, deviceNameOrEmptyForPrimary, StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
            }
        }

        foreach (DisplayInfo d in displays)
        {
            if (d.IsPrimary)
            {
                return d;
            }
        }

        return displays[0];
    }

    private bool InitDxgi()
    {
        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (int adapterIndex = 0; adapterIndex < 64; adapterIndex++)
            {
                Result enumAdapter = factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter);
                if (enumAdapter.Failure || adapter == null)
                {
                    break;
                }

                using (adapter)
                {
                    for (int outputIndex = 0; outputIndex < 64; outputIndex++)
                    {
                        Result enumOutput = adapter.EnumOutputs(outputIndex, out IDXGIOutput? output);
                        if (enumOutput.Failure || output == null)
                        {
                            break;
                        }

                        using (output)
                        {
                            OutputDescription desc;
                            try
                            {
                                desc = output.Description;
                            }
                            catch
                            {
                                continue;
                            }

                            if (!MatchesTarget(desc))
                            {
                                continue;
                            }

                            if (CreateDuplication(adapter, output))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall through to the GDI fallback below.
        }

        return _duplication != null;
    }

    private bool MatchesTarget(OutputDescription desc)
    {
        try
        {
            bool attached = desc.AttachedToDesktop;
            if (!attached)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(desc.DeviceName) &&
                !string.IsNullOrEmpty(_target.DeviceName) &&
                string.Equals(desc.DeviceName, _target.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Coordinate fallback (also covers multi-GPU cases where the
            // DXGI device name does not match the GDI device name).
            return desc.DesktopCoordinates.Left == _target.Left &&
                   desc.DesktopCoordinates.Top == _target.Top;
        }
        catch
        {
            return false;
        }
    }

    private bool CreateDuplication(IDXGIAdapter1 adapter, IDXGIOutput output)
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        try
        {
            Result created = D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                new[]
                {
                    FeatureLevel.Level_11_1,
                    FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1,
                    FeatureLevel.Level_10_0,
                },
                out device,
                out context);

            if (created.Failure || device == null || context == null)
            {
                try { device?.Dispose(); } catch { }
                try { context?.Dispose(); } catch { }
                return false;
            }

            IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
            try
            {
                IDXGIOutputDuplication duplication = output1.DuplicateOutput(device);
                _device = device;
                _context = context;
                _duplication = duplication;
                _dxgiFailures = 0;
                return true;
            }
            finally
            {
                try { output1.Dispose(); } catch { }
            }
        }
        catch
        {
            try { device?.Dispose(); } catch { }
            try { context?.Dispose(); } catch { }
            return false;
        }
    }

    private bool TryGetFrameDxgi(out CapturedFrame? frame)
    {
        frame = null;

        Result acquire;
        IDXGIResource? desktopResource;
        try
        {
            acquire = _duplication!.AcquireNextFrame(AcquireTimeoutMs, out OutduplFrameInfo _, out desktopResource);
        }
        catch
        {
            desktopResource = null;
            return OnDxgiError(out frame);
        }

        if (acquire == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            // Static-image optimization: no new frame was produced.
            try { desktopResource?.Dispose(); } catch { }
            return false;
        }

        if (acquire.Failure)
        {
            try { desktopResource?.Dispose(); } catch { }
            return OnDxgiError(out frame);
        }

        try
        {
            using (desktopResource)
            {
                if (desktopResource == null)
                {
                    return OnDxgiError(out frame);
                }

                using ID3D11Texture2D src = desktopResource.QueryInterface<ID3D11Texture2D>();
                Texture2DDescription srcDesc = src.Description;
                int w = srcDesc.Width;
                int h = srcDesc.Height;
                if (w <= 0 || h <= 0)
                {
                    return OnDxgiError(out frame);
                }

                EnsureStaging(w, h, srcDesc.Format);
                if (_staging == null || _context == null)
                {
                    return OnDxgiError(out frame);
                }

                _context.CopyResource(_staging, src);

                Result mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mappedRect);
                if (mapped.Failure || mappedRect.DataPointer == IntPtr.Zero)
                {
                    return OnDxgiError(out frame);
                }

                byte[] full;
                try
                {
                    int rowPitch = mappedRect.RowPitch;
                    full = new byte[w * h * 4];
                    if (rowPitch == w * 4)
                    {
                        Marshal.Copy(mappedRect.DataPointer, full, 0, full.Length);
                    }
                    else
                    {
                        for (int y = 0; y < h; y++)
                        {
                            Marshal.Copy(IntPtr.Add(mappedRect.DataPointer, y * rowPitch), full, y * w * 4, w * 4);
                        }
                    }
                }
                finally
                {
                    try { _context.Unmap(_staging, 0); } catch { }
                }

                _dxgiFailures = 0;
                frame = BuildFrame(full, w, h);
                return true;
            }
        }
        catch
        {
            return OnDxgiError(out frame);
        }
        finally
        {
            try { _duplication!.ReleaseFrame(); } catch { }
        }
    }

    private bool OnDxgiError(out CapturedFrame? frame)
    {
        frame = null;
        try
        {
            _dxgiFailures++;
            if (_dxgiFailures >= MaxDxgiFailuresBeforeGdiFallback)
            {
                ShutdownDxgi();
                _useGdiFallback = true;
                return TryGetFrameGdi(out frame);
            }
        }
        catch
        {
            // Stay on the current path; the next call will retry.
        }

        return false;
    }

    private void EnsureStaging(int width, int height, Format format)
    {
        if (_staging != null && _stagingWidth == width && _stagingHeight == height)
        {
            return;
        }

        try { _staging?.Dispose(); } catch { }
        _staging = null;
        _stagingWidth = 0;
        _stagingHeight = 0;

        if (_device == null)
        {
            return;
        }

        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = SampleDescription.Default,
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        _staging = _device.CreateTexture2D(in desc);
        _stagingWidth = width;
        _stagingHeight = height;
    }

    private void ShutdownDxgi()
    {
        try { _staging?.Dispose(); } catch { }
        _staging = null;
        _stagingWidth = 0;
        _stagingHeight = 0;

        try { _duplication?.Dispose(); } catch { }
        _duplication = null;

        try { _context?.Dispose(); } catch { }
        _context = null;

        try { _device?.Dispose(); } catch { }
        _device = null;

        _dxgiFailures = 0;
    }

    private bool TryGetFrameGdi(out CapturedFrame? frame)
    {
        frame = null;
        try
        {
            int w = _target.Width;
            int h = _target.Height;
            if (w <= 0 || h <= 0)
            {
                return false;
            }

            IntPtr screenDC = IntPtr.Zero;
            IntPtr memDC = IntPtr.Zero;
            IntPtr hbmp = IntPtr.Zero;
            IntPtr oldBmp = IntPtr.Zero;
            try
            {
                screenDC = GetDC(IntPtr.Zero);
                if (screenDC == IntPtr.Zero)
                {
                    return false;
                }

                memDC = CreateCompatibleDC(screenDC);
                if (memDC == IntPtr.Zero)
                {
                    return false;
                }

                // Top-down 32bpp DIB (negative height) so rows are already in order.
                // No cursor is ever drawn into this bitmap.
                var bmi = new BitmapInfo
                {
                    bmiHeader = new BitmapInfoHeader
                    {
                        biSize = Marshal.SizeOf<BitmapInfoHeader>(),
                        biWidth = w,
                        biHeight = -h,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BiRgb,
                        biSizeImage = 0,
                        biXPelsPerMeter = 0,
                        biYPelsPerMeter = 0,
                        biClrUsed = 0,
                        biClrImportant = 0,
                    },
                    bmiColors = 0,
                };

                hbmp = CreateDIBSection(screenDC, ref bmi, DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
                if (hbmp == IntPtr.Zero || bits == IntPtr.Zero)
                {
                    return false;
                }

                oldBmp = SelectObject(memDC, hbmp);
                if (oldBmp == IntPtr.Zero)
                {
                    return false;
                }

                if (!BitBlt(memDC, 0, 0, w, h, screenDC, _target.Left, _target.Top, Srccopy))
                {
                    return false;
                }

                var full = new byte[w * h * 4];
                Marshal.Copy(bits, full, 0, full.Length);
                frame = BuildFrame(full, w, h);
                return true;
            }
            finally
            {
                try { if (oldBmp != IntPtr.Zero && memDC != IntPtr.Zero) SelectObject(memDC, oldBmp); } catch { }
                try { if (hbmp != IntPtr.Zero) DeleteObject(hbmp); } catch { }
                try { if (memDC != IntPtr.Zero) DeleteDC(memDC); } catch { }
                try { if (screenDC != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDC); } catch { }
            }
        }
        catch
        {
            frame = null;
            return false;
        }
    }

    private CapturedFrame BuildFrame(byte[] full, int fullWidth, int fullHeight)
    {
        if (_scale >= 1.0 || fullWidth <= 0 || fullHeight <= 0)
        {
            return new CapturedFrame { Width = fullWidth, Height = fullHeight, Stride = fullWidth * 4, Bgra = full };
        }

        int dstW = Math.Max(1, (int)(fullWidth * _scale));
        int dstH = Math.Max(1, (int)(fullHeight * _scale));
        if (dstW >= fullWidth && dstH >= fullHeight)
        {
            return new CapturedFrame { Width = fullWidth, Height = fullHeight, Stride = fullWidth * 4, Bgra = full };
        }

        return new CapturedFrame
        {
            Width = dstW,
            Height = dstH,
            Stride = dstW * 4,
            Bgra = DownscaleBoxAverage(full, fullWidth, fullHeight, dstW, dstH),
        };
    }

    private static byte[] DownscaleBoxAverage(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        double xRatio = (double)srcW / dstW;
        double yRatio = (double)srcH / dstH;

        for (int dy = 0; dy < dstH; dy++)
        {
            int y0 = (int)(dy * yRatio);
            int y1 = Math.Max(y0 + 1, (int)((dy + 1) * yRatio));
            if (y1 > srcH)
            {
                y1 = srcH;
            }

            for (int dx = 0; dx < dstW; dx++)
            {
                int x0 = (int)(dx * xRatio);
                int x1 = Math.Max(x0 + 1, (int)((dx + 1) * xRatio));
                if (x1 > srcW)
                {
                    x1 = srcW;
                }

                long b = 0, g = 0, r = 0, a = 0;
                int n = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * srcW * 4;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int s = row + sx * 4;
                        b += src[s];
                        g += src[s + 1];
                        r += src[s + 2];
                        a += src[s + 3];
                        n++;
                    }
                }

                if (n == 0)
                {
                    n = 1;
                }

                int d = (dy * dstW + dx) * 4;
                dst[d] = (byte)(b / n);
                dst[d + 1] = (byte)(g / n);
                dst[d + 2] = (byte)(r / n);
                dst[d + 3] = (byte)(a / n);
            }
        }

        return dst;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
        public int bmiColors;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool EnumDisplayDevices(string? lpDevice, int iDevNum, ref DisplayDevice lpDisplayDevice, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);
}

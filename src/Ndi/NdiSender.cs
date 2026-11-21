// NDI sender for NDI Screenshare.
// Minimal native interop against Processing.NDI.Lib.x64.dll (NDI SDK v5/v6). BCL-only, no NuGet.
// All native entry points are resolved lazily via LoadLibrary/GetProcAddress so
// the app runs (and IsRuntimeAvailable() returns false) on machines without the
// NDI runtime installed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using NdiScreenshare.Infra;

namespace NdiScreenshare.Ndi
{
    /// <summary>
    /// Sends BGRA video frames to the network as an NDI source.
    /// No audio. Send() never throws.
    /// </summary>
    public sealed class NdiSender : IDisposable
    {
        // NDI 6 installs as Processing.NDI.Lib.x64.dll (not ndi.dll). Probe in order:
        // 1) NDI_RUNTIME_DIR_V6..V2 + NDI_SDK_DIR env vars, 2) bare names via PATH,
        // 3) HKLM Uninstall scan (64/32 views, any DisplayName containing 'NDI'),
        // 4) %ProgramFiles%\NDI (+ x86) depth-3 match, 5) legacy hardcoded paths.
        private const string NdiLegacyDllName = "ndi.dll";
        private const string NdiLibFileName = "Processing.NDI.Lib.x64.dll";
        private const string Ndi6RuntimePath = @"C:\Program Files\NDI\NDI 6 Runtime\v6\Processing.NDI.Lib.x64.dll";
        private const string Ndi6SdkPath = @"C:\Program Files\NDI\NDI 6 SDK\Bin\x64\Processing.NDI.Lib.x64.dll";

        // FourCC 'BGRA' as little-endian int: 'B' | 'G'<<8 | 'R'<<16 | 'A'<<24.
        private const int FourCC_BGRA = 0x41524742;

        // NDIlib_frame_format_type_progressive.
        private const int FrameFormatProgressive = 1;

        // NDIlib_send_timecode_synthesize.
        private const long TimecodeSynthesize = long.MaxValue;

        private const int DefaultFps = 20;
        private const int MaxConsecutiveErrors = 10;

        // ---- Native send-create settings (NDIlib_send_create_t) ----
        // Verified against Processing.NDI.structs.h (NDI 6 SDK): const char* p_ndi_name,
        // const char* p_groups, bool clock_video, bool clock_audio. C bool is 1 byte,
        // matching [MarshalAs(UnmanagedType.I1)]. Pack=8 matches the MSVC default
        // (/Zp8) used to build Processing.NDI.Lib.x64.dll.
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct NdiSendCreate
        {
            public IntPtr p_ndi_name; // const char* (UTF-8), null-terminated
            public IntPtr p_groups;   // const char* (UTF-8) or NULL
            [MarshalAs(UnmanagedType.I1)]
            public bool clock_video;
            [MarshalAs(UnmanagedType.I1)]
            public bool clock_audio;
        }

        // ---- Native video frame (NDIlib_video_frame_v2_t) ----
        // Layout verified field-by-field against Processing.NDI.structs.h (NDI 6 SDK):
        //   int xres, yres; NDIlib_FourCC_video_type_e FourCC (32-bit enum -> int);
        //   int frame_rate_N, frame_rate_D; float picture_aspect_ratio;
        //   NDIlib_frame_format_type_e frame_format_type (32-bit enum -> int);
        //   int64_t timecode; uint8_t* p_data;
        //   union { int line_stride_in_bytes; int data_size_in_bytes; };
        //   const char* p_metadata; int64_t timestamp.
        // Pack=8 matches the MSVC default (/Zp8) the NDI DLL is built with, so the
        // implicit padding before the 8-byte fields (timecode, p_data, p_metadata,
        // timestamp) matches the native layout exactly (total 72 bytes on x64).
        // Constant cross-checks: FourCC BGRA = NDI_LIB_FOURCC('B','G','R','A')
        //   = 0x41524742; progressive = NDIlib_frame_format_type_progressive = 1;
        //   synthesize = NDIlib_send_timecode_synthesize = INT64_MAX.
        // Header note: picture_aspect_ratio 0 means square pixels (valid), but we
        // pass the explicit (float)xres/yres ratio so no receiver can misread it.
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct NdiVideoFrameV2
        {
            public int xres;
            public int yres;
            public int FourCC;
            public int frame_rate_N;
            public int frame_rate_D;
            public float picture_aspect_ratio;
            public int frame_format_type;
            public long timecode;
            public IntPtr p_data;
            public int line_stride_in_bytes;
            public IntPtr p_metadata;
            public long timestamp;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool NdiInitializeDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NdiDestroyDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr NdiSendCreateDelegate(ref NdiSendCreate p_create_settings);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NdiSendDestroyDelegate(IntPtr p_instance);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NdiSendVideoDelegate(IntPtr p_instance, ref NdiVideoFrameV2 p_video_data);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr NdiVersionDelegate();

        // kernel32 for lazy loading (BCL P/Invoke, always present on Windows).
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        // ---- Shared native state (ndi.dll stays loaded for process lifetime) ----
        private static readonly object s_loadLock = new object();
        private static IntPtr s_module = IntPtr.Zero;
        private static bool s_loadAttempted;
        private static bool s_delegatesResolved;
        private static bool s_sdkInitialized;

        private static NdiInitializeDelegate s_initialize;
        private static NdiDestroyDelegate s_destroy;
        private static NdiSendCreateDelegate s_sendCreate;
        private static NdiSendDestroyDelegate s_sendDestroy;
        // NOTE: synchronous NDIlib_send_send_video_v2 is the ONLY send path (see Send).
        // The async export is deliberately not resolved: NDIlib_send_send_video_async_v2
        // keeps referencing the caller's buffer after it returns, which caused a fatal
        // native access violation (c0000005 in Processing.NDI.Lib.x64.dll) once the GC
        // moved/freed the unpinned buffer. Sync copies before returning, so the buffer
        // only needs to live for the duration of the call, which the GCHandle pin below
        // already guarantees. v3 is not resolved either: it needs a larger frame struct.
        private static NdiSendVideoDelegate s_sendVideoV2;
        private static string s_versionString;

        // ---- Diagnostics state (for GetDiagnostics + file log) ----
        private static string s_loadedPath;
        private static string s_loadedSource = "none";
        private static int s_probeSearched;
        private static int s_lastLoadError;
        private static readonly List<string> s_procsOk = new List<string>();
        private static readonly List<string> s_procsMissing = new List<string>();
        private static string s_initResult = "not-attempted";
        private static string s_sendCreateResult = "not-attempted";
        private static bool s_diagLoggedArch;

        // ---- Instance state ----
        private readonly object _sync = new object();
        private IntPtr _sender = IntPtr.Zero;
        private string _sourceName;
        private int _fps = DefaultFps;
        private int _consecutiveErrors;
        private bool _disposed;

        /// <summary>
        /// True when the NDI runtime DLL can be loaded. Never throws.
        /// </summary>
        public static bool IsRuntimeAvailable()
        {
            try
            {
                return EnsureLoaded() != IntPtr.Zero;
            }
            catch (Exception ex)
            {
                NdiLog("ERROR", "IsRuntimeAvailable failed: " + ex);
                return false;
            }
        }

        /// <summary>
        /// One-line diagnostics summary for the UI lane. Never throws.
        /// Format: loadedDll=... procs=ok/missing-list init=... lastError=...
        /// </summary>
        public static string GetDiagnostics()
        {
            try
            {
                string dll = string.IsNullOrEmpty(s_loadedPath) ? "(none)" : s_loadedPath;
                string ok;
                string missing;
                lock (s_loadLock)
                {
                    ok = s_procsOk.Count == 0 ? "(none)" : string.Join(",", s_procsOk.ToArray());
                    missing = s_procsMissing.Count == 0 ? "(none)" : string.Join(",", s_procsMissing.ToArray());
                }
                string ver = string.IsNullOrEmpty(s_versionString) ? "n/a" : s_versionString;
                return "loadedDll=" + dll + " procs_ok=[" + ok + "] procs_missing=[" + missing
                    + "] init=" + s_initResult + " send_create=" + s_sendCreateResult
                    + " ndi_version=" + ver + " lastError=" + s_lastLoadError
                    + " searched=" + s_probeSearched + " source=" + s_loadedSource
                    + " x64=" + Environment.Is64BitProcess + " ptrSize=" + IntPtr.Size;
            }
            catch
            {
                return "loadedDll=(err) init=(err) lastError=(err)";
            }
        }

        /// <summary>
        /// Creates the NDI source with the default frame rate (20 fps).
        /// </summary>
        public bool Initialize(string sourceName)
        {
            return Initialize(sourceName, DefaultFps);
        }

        /// <summary>
        /// Creates the NDI source. Safe to call again to rename / change fps.
        /// Returns false (never throws) when the runtime is missing or creation fails.
        /// </summary>
        public bool Initialize(string sourceName, int fps)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                NdiLog("WARN", "Initialize called with blank sourceName; returning false.");
                return false;
            }

            if (fps <= 0)
            {
                fps = DefaultFps;
            }
            else if (fps > 120)
            {
                fps = 120;
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    return false;
                }

                try
                {
                    if (!EnsureDelegates())
                    {
                        NdiLog("ERROR", "Initialize('" + sourceName + "') failed: NDI runtime not available. diag=" + GetDiagnostics());
                        return false;
                    }

                    if (!s_sdkInitialized)
                    {
                        bool ok = false;
                        try
                        {
                            ok = s_initialize();
                            s_initResult = ok ? "true" : "false(returned-false)";
                            NdiLog(ok ? "INFO" : "ERROR",
                                "NDIlib_initialize() returned " + ok.ToString().ToLowerInvariant()
                                + " for '" + sourceName + "'. diag=" + GetDiagnostics());
                        }
                        catch (Exception ex)
                        {
                            s_initResult = "threw:" + ex.GetType().Name;
                            NdiLog("ERROR", "NDIlib_initialize threw: " + ex + " diag=" + GetDiagnostics());
                            return false;
                        }

                        if (!ok)
                        {
                            // Plain initialize returned false. There is no NDIlib_initialize_v2 in the
                            // SDK; log that we checked alternates so the failure is visible in app.log.
                            NdiLog("ERROR", "NDIlib_initialize returned false; checked alternate init exports (none expected in NDI 6 SDK). diag=" + GetDiagnostics());
                            return false;
                        }

                        s_sdkInitialized = true;
                    }

                    DestroySenderLocked();

                    _sourceName = sourceName;
                    _fps = fps;

                    if (!CreateSenderLocked())
                    {
                        NdiLog("ERROR", "NDIlib_send_create failed for '" + sourceName + "' result=" + s_sendCreateResult + " diag=" + GetDiagnostics());
                        return false;
                    }

                    NdiLog("INFO", "Initialize('" + sourceName + "', fps=" + fps + ") OK sender=" + s_sendCreateResult + " diag=" + GetDiagnostics());
                    _consecutiveErrors = 0;
                    return true;
                }
                catch (Exception ex)
                {
                    NdiLog("ERROR", "Initialize failed: " + ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// Sends one BGRA frame. Never throws; failures are counted and the
        /// sender is auto-recreated after 10 consecutive errors.
        /// </summary>
        public void Send(NdiScreenshare.Capture.CapturedFrame frame)
        {
            try
            {
                if (frame == null)
                {
                    return;
                }

                byte[] bgra = frame.Bgra;
                int width = frame.Width;
                int height = frame.Height;
                int stride = frame.Stride;

                if (bgra == null || bgra.Length == 0 || width <= 0 || height <= 0)
                {
                    return;
                }

                int minStride = width * 4;
                if (stride < minStride)
                {
                    stride = minStride;
                }

                // Guard against short buffers (avoids native over-read).
                long required = (long)stride * (height - 1) + minStride;
                if ((long)bgra.Length < required)
                {
                    return;
                }

                NdiSendVideoDelegate sendFn;
                IntPtr sender;
                int fps;
                lock (_sync)
                {
                    if (_disposed || _sender == IntPtr.Zero)
                    {
                        return;
                    }

                    // Synchronous send ONLY. The async variant
                    // (NDIlib_send_send_video_async_v2) does NOT copy the frame: per
                    // Processing.NDI.Send.h, "the memory accessed ... cannot be freed or
                    // re-used by the caller until a synchronizing event has occurred".
                    // Unpinning/freeing the buffer in the finally below would hand NDI a
                    // buffer the GC may then move or collect while NDI is still reading
                    // it -> intermittent native access violation (c0000005), uncatchable
                    // from managed code, killing the process ~1 min after start.
                    // NDIlib_send_send_video_v2 copies synchronously before returning,
                    // so the GCHandle pin held for the duration of this call is enough.
                    sendFn = s_sendVideoV2;
                    if (sendFn == null)
                    {
                        return;
                    }

                    sender = _sender;
                    fps = _fps;
                }

                GCHandle pinned = default(GCHandle);
                try
                {
                    pinned = GCHandle.Alloc(bgra, GCHandleType.Pinned);

                    NdiVideoFrameV2 video = new NdiVideoFrameV2
                    {
                        xres = width,
                        yres = height,
                        FourCC = FourCC_BGRA,
                        frame_rate_N = fps * 1000,
                        frame_rate_D = 1000,
                        // Explicit ratio (header: 0 = square pixels, also valid).
                        picture_aspect_ratio = (float)width / (float)height,
                        frame_format_type = FrameFormatProgressive,
                        timecode = TimecodeSynthesize,
                        p_data = pinned.AddrOfPinnedObject(),
                        line_stride_in_bytes = stride,
                        p_metadata = IntPtr.Zero,
                        timestamp = 0,
                    };

                    sendFn(sender, ref video);
                }
                finally
                {
                    if (pinned.IsAllocated)
                    {
                        pinned.Free();
                    }
                }

                lock (_sync)
                {
                    _consecutiveErrors = 0;
                }
            }
            catch (Exception ex)
            {
                NdiLog("WARN", "Send failed: " + ex.GetType().Name + ": " + ex.Message);
                NoteSendError();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                DestroySenderLocked();

                try
                {
                    if (s_sdkInitialized && s_destroy != null)
                    {
                        s_destroy();
                    }
                }
                catch (Exception ex)
                {
                    NdiLog("WARN", "NDIlib_destroy threw: " + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    s_sdkInitialized = false;
                }
            }

            GC.SuppressFinalize(this);
        }

        // ---- Internals (all _sync-locked unless noted) ----

        private void NoteSendError()
        {
            try
            {
                bool recreate = false;
                lock (_sync)
                {
                    _consecutiveErrors++;
                    if (_consecutiveErrors >= MaxConsecutiveErrors
                        && !_disposed
                        && _sender != IntPtr.Zero
                        && _sourceName != null)
                    {
                        recreate = true;
                        _consecutiveErrors = 0;
                    }
                }

                if (recreate)
                {
                    NdiLog("WARN", "Recreating sender after repeated send failures.");
                    lock (_sync)
                    {
                        if (!_disposed && _sourceName != null)
                        {
                            DestroySenderLocked();
                            if (!CreateSenderLocked())
                            {
                                NdiLog("ERROR", "Sender recreation failed. diag=" + GetDiagnostics());
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                NdiLog("WARN", "Error accounting failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private bool CreateSenderLocked()
        {
            IntPtr namePtr = IntPtr.Zero;
            try
            {
                namePtr = AllocUtf8(_sourceName);

                NdiSendCreate settings = new NdiSendCreate
                {
                    p_ndi_name = namePtr,
                    p_groups = IntPtr.Zero,
                    clock_video = true,
                    clock_audio = false,
                };

                IntPtr sender = s_sendCreate(ref settings);
                s_sendCreateResult = sender == IntPtr.Zero ? "NULL(0)" : "0x" + sender.ToString("X");
                NdiLog(sender == IntPtr.Zero ? "ERROR" : "INFO",
                    "NDIlib_send_create('" + _sourceName + "') returned " + s_sendCreateResult + ".");
                if (sender == IntPtr.Zero)
                {
                    return false;
                }

                _sender = sender;
                return true;
            }
            catch (Exception ex)
            {
                s_sendCreateResult = "threw:" + ex.GetType().Name;
                NdiLog("ERROR", "CreateSender failed: " + ex);
                return false;
            }
            finally
            {
                if (namePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(namePtr);
                }
            }
        }

        private void DestroySenderLocked()
        {
            if (_sender != IntPtr.Zero)
            {
                try
                {
                    if (s_sendDestroy != null)
                    {
                        s_sendDestroy(_sender);
                    }
                }
                catch (Exception ex)
                {
                    NdiLog("WARN", "NDIlib_send_destroy threw: " + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    _sender = IntPtr.Zero;
                }
            }
        }

        /// <summary>
        /// File + debug log helper. Never throws.
        /// </summary>
        private static void NdiLog(string level, string msg)
        {
            try
            {
                string line = "[NdiSender] " + msg;
                Debug.WriteLine(line);
                try
                {
                    if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Error(line);
                    }
                    else if (string.Equals(level, "WARN", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Warn(line);
                    }
                    else
                    {
                        Logger.Info(line);
                    }
                }
                catch
                {
                    // Logger never throws by design; swallow regardless.
                }
            }
            catch
            {
                // Never throw from logging.
            }
        }

        private static string DescribeLoadError(int code)
        {
            // 2 = file not found, 126 = module/dependency missing, 193 = bitness mismatch.
            switch (code)
            {
                case 0: return "ok";
                case 2: return "file-not-found (path not on disk / not on PATH)";
                case 126: return "missing-dependency (DLL or its VC++ Redist dependency not found; install latest Microsoft VC++ x64 Redist)";
                case 193: return "bitness-mismatch (32/64-bit mismatch; process must be x64 for Processing.NDI.Lib.x64.dll)";
                default: return "win32-" + code;
            }
        }

        private static IntPtr EnsureLoaded()
        {
            if (s_module != IntPtr.Zero)
            {
                return s_module;
            }

            lock (s_loadLock)
            {
                if (s_module != IntPtr.Zero)
                {
                    return s_module;
                }

                if (s_loadAttempted)
                {
                    return s_module;
                }

                s_loadAttempted = true;
                try
                {
                    if (!s_diagLoggedArch)
                    {
                        s_diagLoggedArch = true;
                        NdiLog("INFO", "Load start: x64=" + Environment.Is64BitProcess
                            + " ptrSize=" + IntPtr.Size + " os=" + Environment.OSVersion
                            + " (need x64 process for Processing.NDI.Lib.x64.dll).");
                    }

                    List<KeyValuePair<string, string>> probes = BuildProbeList();
                    s_probeSearched = probes.Count;
                    NdiLog("INFO", "Probe list built: searched=" + probes.Count
                        + " order=env:N > path:2 > registry:N > programfiles:N > hardcoded:2.");

                    foreach (KeyValuePair<string, string> probe in probes)
                    {
                        string candidate = probe.Key;
                        string source = probe.Value;
                        bool exists = false;
                        try { exists = !string.IsNullOrEmpty(candidate) && File.Exists(candidate); }
                        catch { exists = false; }
                        try
                        {
                            IntPtr module = LoadLibrary(candidate);
                            if (module != IntPtr.Zero)
                            {
                                s_module = module;
                                s_loadedPath = candidate;
                                s_loadedSource = source;
                                s_lastLoadError = 0;
                                NdiLog("INFO", "probe OK [" + source + "]: LoadLibrary('" + candidate + "') succeeded handle=0x"
                                    + module.ToString("X") + " fileExists=" + exists + ".");
                                break;
                            }

                            int err = Marshal.GetLastWin32Error();
                            s_lastLoadError = err;
                            NdiLog("WARN", "probe FAIL [" + source + "]: LoadLibrary('" + candidate + "') returned NULL"
                                + " GetLastError=" + err + " (" + DescribeLoadError(err) + ")"
                                + " fileExists=" + exists + ".");
                        }
                        catch (Exception ex)
                        {
                            NdiLog("WARN", "probe EX [" + source + "]: LoadLibrary('" + candidate + "') threw "
                                + ex.GetType().Name + ": " + ex.Message);
                        }
                    }

                    if (s_module == IntPtr.Zero)
                    {
                        NdiLog("ERROR", "NDI runtime not found in probe list. lastError=" + s_lastLoadError
                            + " (" + DescribeLoadError(s_lastLoadError) + ")."
                            + " Hint: install NDI 6 Runtime + latest VC++ x64 Redist; ensure app runs as x64.");
                    }
                }
                catch (Exception ex)
                {
                    NdiLog("ERROR", "LoadLibrary failed: " + ex);
                    s_module = IntPtr.Zero;
                }

                return s_module;
            }
        }

        private static readonly string[] NdiFileNames = { NdiLibFileName, NdiLegacyDllName };

        private static readonly string[] NdiRuntimeEnvVars =
        {
            "NDI_RUNTIME_DIR_V6",
            "NDI_RUNTIME_DIR_V5",
            "NDI_RUNTIME_DIR_V4",
            "NDI_RUNTIME_DIR_V3",
            "NDI_RUNTIME_DIR_V2",
        };

        private static List<string> s_programFilesCache;
        private static bool s_programFilesScanned;

        /// <summary>
        /// Ordered probe list (each entry tagged with its source for diagnostics):
        /// 1) NDI_RUNTIME_DIR_V6..V2 env dirs (+ NDI_SDK_DIR\Bin\x64),
        /// 2) bare names via PATH, 3) registry Uninstall scan (64/32 views, any
        /// DisplayName containing 'NDI'), 4) %ProgramFiles%\NDI (+ x86) depth-3
        /// filename match, 5) legacy hardcoded C:\Program Files paths. Never throws.
        /// </summary>
        private static List<KeyValuePair<string, string>> BuildProbeList()
        {
            List<KeyValuePair<string, string>> probes = new List<KeyValuePair<string, string>>();
            try
            {
                // 1) Official discovery: NDI_RUNTIME_DIR_V* env vars (each holds the dir directly).
                foreach (string envName in NdiRuntimeEnvVars)
                {
                    string dir = null;
                    try { dir = Environment.GetEnvironmentVariable(envName); }
                    catch (Exception ex)
                    {
                        NdiLog("WARN", "env probe: GetEnvironmentVariable('" + envName + "') failed: "
                            + ex.GetType().Name + ": " + ex.Message);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(dir))
                    {
                        continue;
                    }

                    dir = dir.Trim().Trim('"');
                    foreach (string file in NdiFileNames)
                    {
                        try
                        {
                            string full = Path.Combine(dir, file);
                            probes.Add(new KeyValuePair<string, string>(full, "env:" + envName));
                            NdiLog("INFO", "env probe: " + envName + "='" + dir + "' -> '" + full + "'.");
                        }
                        catch (Exception ex)
                        {
                            NdiLog("WARN", "env probe combine failed: " + ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                }

                // 1b) NDI_SDK_DIR -> %NDI_SDK_DIR%\Bin\x64\<both names>.
                string sdkDir = null;
                try { sdkDir = Environment.GetEnvironmentVariable("NDI_SDK_DIR"); }
                catch (Exception ex)
                {
                    NdiLog("WARN", "env probe: GetEnvironmentVariable('NDI_SDK_DIR') failed: "
                        + ex.GetType().Name + ": " + ex.Message);
                }

                if (!string.IsNullOrWhiteSpace(sdkDir))
                {
                    sdkDir = sdkDir.Trim().Trim('"');
                    string binX64 = null;
                    try { binX64 = Path.Combine(sdkDir, "Bin", "x64"); }
                    catch (Exception ex)
                    {
                        NdiLog("WARN", "env probe NDI_SDK_DIR combine failed: " + ex.GetType().Name + ": " + ex.Message);
                    }

                    if (!string.IsNullOrWhiteSpace(binX64))
                    {
                        foreach (string file in NdiFileNames)
                        {
                            try
                            {
                                string full = Path.Combine(binX64, file);
                                probes.Add(new KeyValuePair<string, string>(full, "env:NDI_SDK_DIR"));
                                NdiLog("INFO", "env probe: NDI_SDK_DIR='" + sdkDir + "' -> '" + full + "'.");
                            }
                            catch (Exception ex)
                            {
                                NdiLog("WARN", "env probe combine failed: " + ex.GetType().Name + ": " + ex.Message);
                            }
                        }
                    }
                }

                // 2) Bare names via PATH.
                probes.Add(new KeyValuePair<string, string>(NdiLibFileName, "path"));
                probes.Add(new KeyValuePair<string, string>(NdiLegacyDllName, "path"));

                // 3) Registry scan: ANY Uninstall subkey whose DisplayName contains 'NDI'
                // (both 64- and 32-bit views) -> <InstallLocation>\<both names>.
                List<KeyValuePair<string, string>> registryProbes = GetNdiPathsFromRegistry();
                probes.AddRange(registryProbes);
                NdiLog("INFO", "registry probe: found " + registryProbes.Count + " candidate(s).");

                // 4) %ProgramFiles%\NDI and %ProgramFiles(x86)%\NDI, bounded depth-3
                // filename match for the two known names (fast, cached).
                List<KeyValuePair<string, string>> pfProbes = GetProgramFilesNdiProbes();
                probes.AddRange(pfProbes);
                NdiLog("INFO", "programfiles probe: found " + pfProbes.Count + " candidate(s).");

                // 5) Legacy hardcoded fallbacks (kept last).
                probes.Add(new KeyValuePair<string, string>(Ndi6RuntimePath, "hardcoded"));
                probes.Add(new KeyValuePair<string, string>(Ndi6SdkPath, "hardcoded"));
            }
            catch (Exception ex)
            {
                NdiLog("WARN", "BuildProbeList failed: " + ex.GetType().Name + ": " + ex.Message);
                try
                {
                    probes.Add(new KeyValuePair<string, string>(NdiLibFileName, "path"));
                    probes.Add(new KeyValuePair<string, string>(NdiLegacyDllName, "path"));
                    probes.Add(new KeyValuePair<string, string>(Ndi6RuntimePath, "hardcoded"));
                    probes.Add(new KeyValuePair<string, string>(Ndi6SdkPath, "hardcoded"));
                }
                catch
                {
                    // Never throw.
                }
            }

            return probes;
        }

        /// <summary>
        /// Scans HKLM Uninstall (64- and 32-bit views) for ANY subkey whose
        /// DisplayName contains 'NDI' and returns
        /// &lt;InstallLocation&gt;\&lt;both names&gt;. Never throws.
        /// </summary>
        private static List<KeyValuePair<string, string>> GetNdiPathsFromRegistry()
        {
            List<KeyValuePair<string, string>> found = new List<KeyValuePair<string, string>>();
            RegistryView[] views = { RegistryView.Registry64, RegistryView.Registry32 };
            foreach (RegistryView view in views)
            {
                string viewLabel = view == RegistryView.Registry64 ? "64" : "32";
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey uninstall = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (uninstall == null)
                        {
                            NdiLog("INFO", "registry probe [" + viewLabel + "]: Uninstall key missing.");
                            continue;
                        }

                        string[] subNames;
                        try
                        {
                            subNames = uninstall.GetSubKeyNames();
                        }
                        catch (Exception ex)
                        {
                            NdiLog("WARN", "registry probe [" + viewLabel + "]: GetSubKeyNames failed: "
                                + ex.GetType().Name + ": " + ex.Message);
                            continue;
                        }

                        foreach (string subName in subNames)
                        {
                            try
                            {
                                using (RegistryKey sub = uninstall.OpenSubKey(subName))
                                {
                                    if (sub == null)
                                    {
                                        continue;
                                    }

                                    string display = null;
                                    string location = null;
                                    try
                                    {
                                        display = sub.GetValue("DisplayName") as string;
                                        location = sub.GetValue("InstallLocation") as string;
                                    }
                                    catch (Exception ex)
                                    {
                                        NdiLog("WARN", "registry probe [" + viewLabel + "/" + subName + "] read failed: "
                                            + ex.GetType().Name + ": " + ex.Message);
                                        continue;
                                    }

                                    if (string.IsNullOrWhiteSpace(display)
                                        || display.IndexOf("NDI", StringComparison.OrdinalIgnoreCase) < 0)
                                    {
                                        continue;
                                    }

                                    NdiLog("INFO", "registry probe [" + viewLabel + "]: match DisplayName='"
                                        + display + "' InstallLocation='" + (location ?? "(null)") + "'.");

                                    if (string.IsNullOrWhiteSpace(location))
                                    {
                                        continue;
                                    }

                                    location = location.Trim().Trim('"');
                                    foreach (string file in NdiFileNames)
                                    {
                                        try
                                        {
                                            string combined;
                                            if (string.Equals(Path.GetFileName(location), file, StringComparison.OrdinalIgnoreCase)
                                                && File.Exists(location))
                                            {
                                                combined = location;
                                            }
                                            else
                                            {
                                                combined = Path.Combine(location, file);
                                            }

                                            found.Add(new KeyValuePair<string, string>(
                                                combined, "registry:" + viewLabel));
                                        }
                                        catch (Exception ex)
                                        {
                                            NdiLog("WARN", "registry probe combine failed: "
                                                + ex.GetType().Name + ": " + ex.Message);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                NdiLog("WARN", "Registry subkey probe failed: " + ex.GetType().Name + ": " + ex.Message);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    NdiLog("WARN", "Registry lookup [" + viewLabel + "] failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            return found;
        }

        /// <summary>
        /// Bounded (depth-3) filename match under %ProgramFiles%\NDI and
        /// %ProgramFiles(x86)%\NDI for the two known DLL names. Cached. Never throws.
        /// </summary>
        private static List<KeyValuePair<string, string>> GetProgramFilesNdiProbes()
        {
            List<KeyValuePair<string, string>> probes = new List<KeyValuePair<string, string>>();
            try
            {
                if (s_programFilesScanned && s_programFilesCache != null)
                {
                    foreach (string cached in s_programFilesCache)
                    {
                        probes.Add(new KeyValuePair<string, string>(cached, "programfiles"));
                    }

                    return probes;
                }

                List<string> roots = new List<string>();
                try
                {
                    string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    string pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                    if (!string.IsNullOrWhiteSpace(pf))
                    {
                        roots.Add(Path.Combine(pf, "NDI"));
                    }

                    if (!string.IsNullOrWhiteSpace(pfX86))
                    {
                        string x86Ndi = Path.Combine(pfX86, "NDI");
                        bool dup = false;
                        foreach (string r in roots)
                        {
                            if (string.Equals(r, x86Ndi, StringComparison.OrdinalIgnoreCase))
                            {
                                dup = true;
                                break;
                            }
                        }

                        if (!dup)
                        {
                            roots.Add(x86Ndi);
                        }
                    }
                }
                catch (Exception ex)
                {
                    NdiLog("WARN", "programfiles probe: SpecialFolder lookup failed: "
                        + ex.GetType().Name + ": " + ex.Message);
                }

                List<string> matches = new List<string>();
                foreach (string root in roots)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                        {
                            NdiLog("INFO", "programfiles probe: root missing '" + (root ?? "(null)") + "'.");
                            continue;
                        }

                        foreach (string match in ScanDirectoryBounded(root, 3))
                        {
                            matches.Add(match);
                            probes.Add(new KeyValuePair<string, string>(match, "programfiles"));
                        }
                    }
                    catch (Exception ex)
                    {
                        NdiLog("WARN", "programfiles probe root '" + root + "' failed: "
                            + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                s_programFilesCache = matches;
                s_programFilesScanned = true;
            }
            catch (Exception ex)
            {
                NdiLog("WARN", "programfiles probe failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            return probes;
        }

        private static List<string> ScanDirectoryBounded(string root, int maxDepth)
        {
            List<string> matches = new List<string>();
            try
            {
                List<string> current = new List<string>() { root };
                for (int depth = 0; depth <= maxDepth; depth++)
                {
                    List<string> next = new List<string>();
                    foreach (string dir in current)
                    {
                        string[] files = null;
                        try { files = Directory.GetFiles(dir, "*.dll"); }
                        catch (Exception ex)
                        {
                            NdiLog("WARN", "programfiles probe: GetFiles('" + dir + "') failed: "
                                + ex.GetType().Name + ": " + ex.Message);
                            continue;
                        }

                        foreach (string file in files)
                        {
                            try
                            {
                                string name = Path.GetFileName(file);
                                foreach (string want in NdiFileNames)
                                {
                                    if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase))
                                    {
                                        matches.Add(file);
                                        NdiLog("INFO", "programfiles probe: match '" + file + "'.");
                                        break;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                NdiLog("WARN", "programfiles probe file check failed: "
                                    + ex.GetType().Name + ": " + ex.Message);
                            }
                        }

                        if (depth < maxDepth)
                        {
                            try
                            {
                                string[] subdirs = Directory.GetDirectories(dir);
                                foreach (string sub in subdirs)
                                {
                                    next.Add(sub);
                                }
                            }
                            catch (Exception ex)
                            {
                                NdiLog("WARN", "programfiles probe: GetDirectories('" + dir + "') failed: "
                                    + ex.GetType().Name + ": " + ex.Message);
                            }
                        }
                    }

                    current = next;
                    if (current.Count == 0)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                NdiLog("WARN", "programfiles scan failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            return matches;
        }

        private static bool EnsureDelegates()
        {
            if (s_delegatesResolved)
            {
                return s_sendCreate != null && s_sendDestroy != null && s_initialize != null
                    && s_sendVideoV2 != null;
            }

            lock (s_loadLock)
            {
                if (s_delegatesResolved)
                {
                    return s_sendCreate != null && s_sendDestroy != null && s_initialize != null
                        && s_sendVideoV2 != null;
                }

                IntPtr module = EnsureLoaded();
                if (module == IntPtr.Zero)
                {
                    NdiLog("ERROR", "EnsureDelegates: no module loaded; skipping GetProcAddress. diag=" + GetDiagnostics());
                    return false;
                }

                NdiLog("INFO", "Resolving exports from '" + s_loadedPath + "' handle=0x" + module.ToString("X") + "...");

                s_initialize = GetFunction<NdiInitializeDelegate>(module, "NDIlib_initialize");
                s_destroy = GetFunction<NdiDestroyDelegate>(module, "NDIlib_destroy");
                s_sendCreate = GetFunction<NdiSendCreateDelegate>(module, "NDIlib_send_create");
                s_sendDestroy = GetFunction<NdiSendDestroyDelegate>(module, "NDIlib_send_destroy");

                // Video send: synchronous NDIlib_send_send_video_v2 ONLY. The async
                // variant is intentionally never resolved (see the field comment and
                // Send): it outlives the caller's buffer and caused fatal native AVs.
                s_sendVideoV2 = GetFunction<NdiSendVideoDelegate>(module, "NDIlib_send_send_video_v2");
                // v3 is not resolved either (needs a larger frame struct than v2).
                // 8th lookup: version string (diagnostic only, never required).
                NdiVersionDelegate verFn = GetFunction<NdiVersionDelegate>(module, "NDIlib_version");
                if (verFn != null)
                {
                    try
                    {
                        IntPtr p = verFn();
                        s_versionString = p == IntPtr.Zero ? "(null)" : (Marshal.PtrToStringAnsi(p) ?? "(decode-fail)");
                        NdiLog("INFO", "NDIlib_version() = '" + s_versionString + "'.");
                    }
                    catch (Exception ex)
                    {
                        NdiLog("WARN", "NDIlib_version() threw: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                // Alternate init-export probe (not expected in NDI 6 SDK): if plain
                // NDIlib_initialize missed, try a v2-suffixed name and log the result.
                if (s_initialize == null)
                {
                    NdiInitializeDelegate alt = GetFunction<NdiInitializeDelegate>(module, "NDIlib_initialize_v2");
                    if (alt != null)
                    {
                        s_initialize = alt;
                        NdiLog("INFO", "Using fallback export NDIlib_initialize_v2.");
                    }
                    else
                    {
                        NdiLog("ERROR", "Required export NDIlib_initialize missing (no NDIlib_initialize_v2 fallback).");
                    }
                }

                s_delegatesResolved = true;

                string selected = s_sendVideoV2 != null ? "v2-sync-only" : "(none)";
                NdiLog("INFO", "Exports resolved from '" + s_loadedPath + "': selected_send=" + selected
                    + " diag=" + GetDiagnostics());

                return s_initialize != null && s_sendCreate != null && s_sendDestroy != null
                    && s_sendVideoV2 != null;
            }
        }

        private static T GetFunction<T>(IntPtr module, string name) where T : class
        {
            try
            {
                IntPtr proc = GetProcAddress(module, name);
                if (proc == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    lock (s_loadLock)
                    {
                        if (!s_procsMissing.Contains(name))
                        {
                            s_procsMissing.Add(name);
                        }
                    }
                    NdiLog("WARN", "GetProcAddress('" + name + "') MISSED GetLastError=" + err + ".");
                    return null;
                }

                T del = Marshal.GetDelegateForFunctionPointer(proc, typeof(T)) as T;
                lock (s_loadLock)
                {
                    if (!s_procsOk.Contains(name))
                    {
                        s_procsOk.Add(name);
                    }
                }
                NdiLog("INFO", "GetProcAddress('" + name + "') resolved addr=0x" + proc.ToString("X") + ".");
                return del;
            }
            catch (Exception ex)
            {
                lock (s_loadLock)
                {
                    if (!s_procsMissing.Contains(name))
                    {
                        s_procsMissing.Add(name);
                    }
                }
                NdiLog("WARN", "GetProcAddress('" + name + "') failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static IntPtr AllocUtf8(string value)
        {
            if (value == null)
            {
                return IntPtr.Zero;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteByte(ptr, bytes.Length, 0);
            return ptr;
        }
    }
}

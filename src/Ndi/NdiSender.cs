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
        // 1) legacy NDI 5 name (PATH-resolved), 2) NDI 6 name (PATH-resolved),
        // 3) well-known full paths + HKLM uninstall InstallLocation lookup.
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
        [StructLayout(LayoutKind.Sequential)]
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
        [StructLayout(LayoutKind.Sequential)]
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
        private static NdiSendVideoDelegate s_sendVideoAsync;
        private static NdiSendVideoDelegate s_sendVideoV3;
        private static NdiSendVideoDelegate s_sendVideoV2;
        private static string s_versionString;

        // ---- Diagnostics state (for GetDiagnostics + file log) ----
        private static string s_loadedPath;
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

                    // Prefer async send when the runtime exports it, else sync (v3, then v2).
                    sendFn = s_sendVideoAsync ?? s_sendVideoV3 ?? s_sendVideoV2;
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
                        picture_aspect_ratio = 0f,
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

                    foreach (string candidate in BuildProbeList())
                    {
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
                                s_lastLoadError = 0;
                                NdiLog("INFO", "probe OK: LoadLibrary('" + candidate + "') succeeded handle=0x"
                                    + module.ToString("X") + " fileExists=" + exists + ".");
                                break;
                            }

                            int err = Marshal.GetLastWin32Error();
                            s_lastLoadError = err;
                            NdiLog("WARN", "probe FAIL: LoadLibrary('" + candidate + "') returned NULL"
                                + " GetLastError=" + err + " (" + DescribeLoadError(err) + ")"
                                + " fileExists=" + exists + ".");
                        }
                        catch (Exception ex)
                        {
                            NdiLog("WARN", "probe EX: LoadLibrary('" + candidate + "') threw "
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

        private static string[] BuildProbeList()
        {
            // Registry-derived path goes last (bonus fallback); PATH names first.
            string registryPath = null;
            try
            {
                registryPath = GetNdiRuntimePathFromRegistry();
                NdiLog("INFO", "registry probe: " + (string.IsNullOrWhiteSpace(registryPath) ? "(none found)" : registryPath));
            }
            catch (Exception ex)
            {
                NdiLog("WARN", "Registry probe failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(registryPath))
            {
                return new[]
                {
                    NdiLegacyDllName,
                    NdiLibFileName,
                    Ndi6RuntimePath,
                    Ndi6SdkPath,
                };
            }

            return new[]
            {
                NdiLegacyDllName,
                NdiLibFileName,
                Ndi6RuntimePath,
                Ndi6SdkPath,
                registryPath,
            };
        }

        /// <summary>
        /// Bonus fallback: HKLM uninstall entry for "NDI 6 Runtime" ->
        /// InstallLocation (dir + Processing.NDI.Lib.x64.dll) or DisplayIcon file.
        /// Returns null when not found. Never throws.
        /// </summary>
        private static string GetNdiRuntimePathFromRegistry()
        {
            try
            {
                using (RegistryKey uninstall = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (uninstall == null)
                    {
                        return null;
                    }

                    foreach (string subName in uninstall.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey sub = uninstall.OpenSubKey(subName))
                            {
                                if (sub == null)
                                {
                                    continue;
                                }

                                string display = sub.GetValue("DisplayName") as string;
                                if (string.IsNullOrWhiteSpace(display)
                                    || display.IndexOf("NDI", StringComparison.OrdinalIgnoreCase) < 0
                                    || display.IndexOf("Runtime", StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    continue;
                                }

                                string location = sub.GetValue("InstallLocation") as string;
                                if (!string.IsNullOrWhiteSpace(location))
                                {
                                    location = location.Trim().Trim('"');
                                    try
                                    {
                                        if (File.Exists(location)
                                            && string.Equals(Path.GetFileName(location), NdiLibFileName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            return location;
                                        }

                                        string combined = Path.Combine(location, NdiLibFileName);
                                        if (File.Exists(combined))
                                        {
                                            return combined;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        NdiLog("WARN", "InstallLocation probe failed: " + ex.GetType().Name + ": " + ex.Message);
                                    }
                                }

                                string icon = sub.GetValue("DisplayIcon") as string;
                                if (!string.IsNullOrWhiteSpace(icon))
                                {
                                    string file = icon.Trim().Trim('"');
                                    int comma = file.IndexOf(",\"", StringComparison.Ordinal);
                                    if (comma >= 0)
                                    {
                                        file = file.Substring(0, comma).Trim().Trim('"');
                                    }
                                    else
                                    {
                                        int lastComma = file.LastIndexOf(',');
                                        if (lastComma > 1 && file.Length - lastComma <= 4)
                                        {
                                            file = file.Substring(0, lastComma).Trim().Trim('"');
                                        }
                                    }

                                    try
                                    {
                                        if (File.Exists(file))
                                        {
                                            if (string.Equals(Path.GetFileName(file), NdiLibFileName, StringComparison.OrdinalIgnoreCase))
                                            {
                                                return file;
                                            }

                                            string dir = Path.GetDirectoryName(file);
                                            if (!string.IsNullOrWhiteSpace(dir))
                                            {
                                                string combined = Path.Combine(dir, NdiLibFileName);
                                                if (File.Exists(combined))
                                                {
                                                    return combined;
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        NdiLog("WARN", "DisplayIcon probe failed: " + ex.GetType().Name + ": " + ex.Message);
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
                NdiLog("WARN", "Registry lookup failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            return null;
        }

        private static bool EnsureDelegates()
        {
            if (s_delegatesResolved)
            {
                return s_sendCreate != null && s_sendDestroy != null && s_initialize != null
                    && (s_sendVideoAsync != null || s_sendVideoV3 != null || s_sendVideoV2 != null);
            }

            lock (s_loadLock)
            {
                if (s_delegatesResolved)
                {
                    return s_sendCreate != null && s_sendDestroy != null && s_initialize != null
                        && (s_sendVideoAsync != null || s_sendVideoV3 != null || s_sendVideoV2 != null);
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

                // Video send: async preferred, then v3, then v2 (any may be absent per SDK version).
                // NDI 6 frame structs grew (v3/v4); we keep the v2 struct and prefer whichever
                // entry point exists, logging each lookup so app.log shows the outcome.
                s_sendVideoAsync = GetFunction<NdiSendVideoDelegate>(module, "NDIlib_send_send_video_async_v2");
                s_sendVideoV3 = GetFunction<NdiSendVideoDelegate>(module, "NDIlib_send_send_video_v3");
                s_sendVideoV2 = GetFunction<NdiSendVideoDelegate>(module, "NDIlib_send_send_video_v2");

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

                string selected = s_sendVideoAsync != null ? "async_v2"
                    : s_sendVideoV3 != null ? "v3"
                    : s_sendVideoV2 != null ? "v2" : "(none)";
                NdiLog("INFO", "Exports resolved from '" + s_loadedPath + "': selected_send=" + selected
                    + " diag=" + GetDiagnostics());

                return s_initialize != null && s_sendCreate != null && s_sendDestroy != null
                    && (s_sendVideoAsync != null || s_sendVideoV3 != null || s_sendVideoV2 != null);
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

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using Microsoft.VisualBasic;
using Microsoft.Win32;
using NdiScreenshare.Capture;
using NdiScreenshare.Infra;
using NdiScreenshare.Ndi;

namespace NdiScreenshare;

public sealed class TrayContext : ApplicationContext
{
    /// <summary>Visual state of the tray icon + status header.</summary>
    private enum TrayState { Stopped, Live, Error }

    private readonly NotifyIcon _icon = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly AppConfig _cfg = AppConfig.Load();
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly ToolStripMenuItem _toggle = new();
    private readonly ToolStripMenuItem _restart = new("Restart streaming");
    private readonly ToolStripMenuItem _status1 = new() { Enabled = false };
    private readonly ToolStripMenuItem _status2 = new() { Enabled = false };
    private readonly ToolStripMenuItem _displays = new("Displays");
    private readonly ToolStripMenuItem _auto = new("Start with Windows");
    private readonly System.Windows.Forms.Timer _blink = new() { Interval = 750 };
    private readonly object _gate = new();
    private readonly Dictionary<(TrayState, bool), Icon> _icons = new();
    private readonly List<MemoryStream> _iconStreams = new(); // Icon(Stream) lazily reads; keep alive.

    private Thread? _capThread, _sendThread;
    private volatile bool _stop;
    private volatile bool _restartRequested;
    private CapturedFrame? _slot;
    private int _slotSeq, _sentSeq;
    private Watchdog? _watchdog;
    private DesktopDuplicator? _dup;
    private NdiSender? _sender;

    // In-process never-stop: heartbeat file (for the out-of-process supervisor),
    // retry timer (restarts a dead pipeline forever with backoff), and throttled
    // logging state for the capture/send loops. No exit path on streaming errors.
    private Heartbeat? _heartbeat;
    private System.Threading.Timer? _retryTimer;
    private long _lastStartAttemptTick;
    private int _startBackoffMs = 5000;
    private long _lastRetryLogTick;
    private long _lastCaptureErrorLogTick;
    private int _captureErrorCount;
    private long _lastSendErrorLogTick;
    private int _sendErrorCount;
    private volatile bool _quit;

    // In-process watcher thread (ticks every 2s): verifies frames are actually
    // flowing and workers are alive (see WatcherLoop). Counters below are written
    // by the capture/send loops and read by the watcher.
    private Thread? _watcherThread;
    private long _framesSent;
    private long _lastSendTick;
    private long _lastCaptureTick;
    private long _capIterations;
    private long _pipelineStartTick;
    private int _capErrors;   // consecutive capture-loop errors (reset on success)
    private int _sendErrors;  // consecutive send-loop errors (reset on success)
    private long _lastWatcherRebuildTick;
    private int _watcherBackoffMs = 10000;
    private long _lastWatcherLogTick;

    // Status/reporting (written on UI thread; fps counters written on send thread).
    private volatile int _measuredFps;
    private int _fpsCounter, _fpsWindowTick;
    private bool _blinkOn;
    private string _displayLabel = "auto (primary)";
    private string? _errorReason;   // non-null while the pipeline believes it is running but failed
    private TrayState _state = TrayState.Stopped;

    public TrayContext()
    {
        _icon.Icon = GetTrayIcon(TrayState.Stopped, false);
        _icon.Text = "NDI Screenshare";
        _icon.Visible = true;

        // --- Status header (disabled items = non-clickable) ---
        _status1.Text = "○ Stopped";
        _status2.Text = "ready";
        _menu.Items.Add(_status1);
        _menu.Items.Add(_status2);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(_toggle);
        _menu.Items.Add(_restart);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_displays);
        _menu.Items.Add(FpsMenu());
        _menu.Items.Add(ScaleMenu());

        var src = new ToolStripMenuItem("Source name...");
        src.Click += (_, _) =>
        {
            var name = Interaction.InputBox("NDI source name:", "NDI Screenshare", _cfg.SourceName);
            if (!string.IsNullOrWhiteSpace(name)) { _cfg.SourceName = name.Trim(); _cfg.Save(); RestartPipeline(); }
        };
        _menu.Items.Add(src);

        _auto.Click += (_, _) => { _cfg.AutoStart = !_cfg.AutoStart; _cfg.Save(); Autostart.SetEnabled(_cfg.AutoStart); SyncMenu(); };
        _menu.Items.Add(_auto);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => Quit();
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exit);

        _toggle.Click += (_, _) => { _cfg.Running = !_cfg.Running; _cfg.Save(); if (_cfg.Running) Start(); else Stop(); SyncMenu(); };
        _restart.Click += (_, _) => RestartStreaming();

        // Refresh the status header every time the menu is shown (Opening = before paint,
        // Popup = belt-and-braces so late-arriving state is reflected too).
        // Refresh the status header every time the menu is about to be shown.
        // (ContextMenuStrip.Opening is the pre-paint equivalent of the popup event.)
        _menu.Opening += (_, _) => RefreshStatus();

        _blink.Tick += (_, _) => { _blinkOn = !_blinkOn; ApplyTrayIcon(); };
        _icon.ContextMenuStrip = _menu;

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        RebuildDisplays();
        Autostart.SetEnabled(_cfg.AutoStart);
        SyncMenu();
        // Heartbeat for the out-of-process supervisor (check-heartbeat.ps1):
        // written every 2s; a stale file means this process is hung.
        _heartbeat = new Heartbeat();
        _heartbeat.Start();
        // In-process supervisor: if streaming is wanted but the pipeline is not
        // alive (NDI init failed, capture blew up, ...), retry Start() forever
        // with backoff instead of exiting or sitting dead.
        _retryTimer = new System.Threading.Timer(_ => EnsureRunning(), null, 5000, 5000);
        if (_cfg.Running) Start(); else Notify("Paused. Use tray menu to start.");
    }

    // ---------------------------------------------------------------- menus

    private ToolStripMenuItem FpsMenu()
    {
        var m = new ToolStripMenuItem("FPS");
        foreach (var fps in new[] { 10, 15, 20, 30 })
        {
            var f = fps;
            var mi = new ToolStripMenuItem($"{f}") { Checked = _cfg.Fps == f };
            mi.Click += (_, _) => { _cfg.Fps = f; _cfg.Save(); SyncMenu(); if (_cfg.Running) RestartPipeline(); };
            m.DropDownItems.Add(mi);
        }
        return m;
    }

    private ToolStripMenuItem ScaleMenu()
    {
        var m = new ToolStripMenuItem("Scale");
        foreach (var (label, v) in new[] { ("100%", 1.0), ("75%", 0.75), ("50%", 0.5) })
        {
            var vv = v;
            var mi = new ToolStripMenuItem(label) { Checked = Math.Abs(_cfg.Scale - vv) < 0.01 };
            mi.Click += (_, _) => { _cfg.Scale = vv; _cfg.Save(); SyncMenu(); };
            m.DropDownItems.Add(mi);
        }
        return m;
    }

    // ---------------------------------------------------------------- status

    /// <summary>Must run on the UI thread (called from menu Opening/Popup and via SyncMenu posts).</summary>
    private void RefreshStatus()
    {
        _state = ComputeState();

        _status1.Text = _state switch
        {
            TrayState.Live => $"● Streaming — {_cfg.SourceName}",
            TrayState.Error => $"▲ ERROR — {_cfg.SourceName}",
            _ => $"○ Stopped — {_cfg.SourceName}",
        };

        string fps = _state == TrayState.Live && _measuredFps > 0
            ? $"{_cfg.Fps} fps ({_measuredFps} actual)"
            : $"{_cfg.Fps} fps";
        string tail = _state == TrayState.Error && !string.IsNullOrEmpty(_errorReason)
            ? _errorReason
            : NdiStateText();
        _status2.Text = $"{fps} · {_displayLabel} · {tail}";

        _toggle.Text = _cfg.Running ? "Stop" : "Start";
        _auto.Checked = _cfg.AutoStart;
        _icon.Text = _state switch
        {
            TrayState.Live => $"NDI Screenshare — streaming {_cfg.SourceName}",
            TrayState.Error => $"NDI Screenshare — ERROR: {_errorReason ?? "see menu"}",
            _ => "NDI Screenshare (stopped)",
        };

        // Blink the live icon; hold steady for stopped/error so the tint reads clearly.
        bool blink = _state == TrayState.Live;
        if (_blink.Enabled != blink) _blink.Enabled = blink;
        if (!blink) _blinkOn = false;
        ApplyTrayIcon();
    }

    private TrayState ComputeState()
    {
        bool alive = _capThread?.IsAlive == true && _sendThread?.IsAlive == true;
        if (_cfg.Running && alive && _errorReason == null) return TrayState.Live;
        if (_cfg.Running && _errorReason != null) return TrayState.Error;
        return TrayState.Stopped;
    }

    /// <summary>
    /// NDI runtime state. IsRuntimeAvailable() + GetDiagnostics() are called through
    /// try/catch + reflection so the menu still works if the NdiSender API ever changes.
    /// The raw diagnostics line is far too long for a menu, so only the NDI version
    /// token is surfaced here (full diagnostics go to the log via NdiSender itself).
    /// </summary>
    private string NdiStateText()
    {
        try
        {
            bool avail;
            try { avail = NdiSender.IsRuntimeAvailable(); } catch { avail = false; }
            string? ver = ExtractToken(TryGetDiagnostics(), "ndi_version=");
            if (avail) return ver == null || ver == "n/a" ? "NDI runtime OK" : $"NDI runtime OK (v{ver.TrimStart('v')})";
            string? err = ExtractToken(TryGetDiagnostics(), "lastError=");
            return err == null || err == "(none)" ? "NDI runtime missing" : $"NDI runtime missing: {err}";
        }
        catch { return "NDI state unknown"; }
    }

    private static string? ExtractToken(string? line, string key)
    {
        if (string.IsNullOrEmpty(line)) return null;
        int i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        i += key.Length;
        int j = line.IndexOf(' ', i);
        string tok = (j < 0 ? line.Substring(i) : line.Substring(i, j - i)).Trim();
        return tok.Length == 0 ? null : tok;
    }

    private string? TryGetDiagnostics()
    {
        try
        {
            var t = typeof(NdiSender);
            MethodInfo? m = t.GetMethod("GetDiagnostics", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            object? target = null;
            if (m == null)
            {
                m = t.GetMethod("GetDiagnostics", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                target = _sender;
            }
            if (m == null) return null;
            target ??= m.IsStatic ? null : _sender;
            if (!m.IsStatic && target == null) return null;
            return (m.Invoke(target, null) as string) is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        }
        catch { return null; }
    }

    private void SyncMenu()
    {
        void apply() { RefreshStatus(); _restart.Text = _cfg.Running ? "Restart streaming" : "Start & restart"; }
        if (SynchronizationContext.Current == _ui) apply();
        else _ui?.Post(_ => apply(), null);
    }

    private void RebuildDisplays()
    {
        _displays.DropDownItems.Clear();
        List<DisplayInfo>? list = null;
        try { list = DesktopDuplicator.GetDisplays(); }
        catch (Exception ex) when (ex is TypeLoadException || ex is FileNotFoundException || ex is DllNotFoundException)
        { Logger.Error(ex, "Displays"); }
        catch (Exception ex) { Logger.Error(ex, "Displays"); }
        if (list == null || list.Count == 0)
        {
            _displayLabel = "GDI fallback";
            _displays.DropDownItems.Add(new ToolStripMenuItem("Capture unavailable (GDI fallback)") { Enabled = false });
            return;
        }
        foreach (var d in list)
        {
            var dev = d.DeviceName;
            bool selected = dev == _cfg.DisplayDeviceName || (string.IsNullOrEmpty(_cfg.DisplayDeviceName) && d.IsPrimary);
            if (selected) _displayLabel = $"{d.FriendlyName} {d.Width}x{d.Height}";
            var mi = new ToolStripMenuItem($"{d.FriendlyName} ({d.Width}x{d.Height})") { Checked = selected };
            mi.Click += (_, _) => { _cfg.DisplayDeviceName = dev; _cfg.Save(); RebuildDisplays(); RestartPipeline(); };
            _displays.DropDownItems.Add(mi);
        }
    }

    private void OnDisplayChanged(object? s, EventArgs e)
    {
        try { RebuildDisplays(); if (_cfg.Running) RestartPipeline(); }
        catch (Exception ex) { Logger.Error(ex, "DisplayChanged"); }
    }

    // ---------------------------------------------------------------- pipeline

    private void Start()
    {
        // Fully up already: nothing to do (lets the retry timer and menu call freely).
        if (_capThread?.IsAlive == true && _sendThread?.IsAlive == true && _watcherThread?.IsAlive == true) return;
        // Partial failure (e.g. a worker died but the rest is up): tear down fully
        // first so worker threads are never duplicated, then rebuild fresh below.
        try { Stop(); } catch (Exception ex) { Logger.Error(ex, "StartStop"); }
        _stop = false;
        _errorReason = null;
        // Baselines for the watcher thread (ticks every 2s, 10s start-up grace).
        _pipelineStartTick = Environment.TickCount64;
        _lastSendTick = _pipelineStartTick;
        _lastCaptureTick = _pipelineStartTick;
        _capErrors = 0;
        _sendErrors = 0;
        try
        {
            _sender?.Dispose();
            _sender = new NdiSender();
            if (!NdiSender.IsRuntimeAvailable()) { Fail("NDI runtime not found — install NDI Tools."); Notify("NDI runtime not found. Install NDI Tools."); return; }
            if (!_sender.Initialize(_cfg.SourceName)) { Fail("NDI init failed."); Notify("NDI init failed."); return; }
        }
        catch (Exception ex) when (ex is TypeLoadException || ex is FileNotFoundException || ex is DllNotFoundException)
        { Logger.Error(ex, "NDI unavailable (GDI fallback)"); Fail("NDI unavailable."); Notify("NDI unavailable (GDI fallback)."); return; }
        catch (Exception ex) { Logger.Error(ex, "Start"); Fail("Start failed."); Notify("Start failed."); return; }
        _watchdog?.Dispose();
        _watchdog = new Watchdog(5000, () => _restartRequested = true);
        _measuredFps = 0; _fpsCounter = 0; _fpsWindowTick = Environment.TickCount;
        _capThread = new Thread(CaptureLoop) { IsBackground = true, Name = "capture" };
        _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "send" };
        _watcherThread = new Thread(WatcherLoop) { IsBackground = true, Name = "watcher" };
        _capThread.Start();
        _sendThread.Start();
        _watcherThread.Start();
        Logger.Info($"Started: display='{_cfg.DisplayDeviceName}' fps={_cfg.Fps} scale={_cfg.Scale}");
        SyncMenu();
    }

    private void Stop()
    {
        _stop = true;
        var cap = _capThread; var snd = _sendThread; var watch = _watcherThread;
        if (cap != null && cap != Thread.CurrentThread && !cap.Join(2000))
            Logger.Warn("Stop: capture thread did not exit in 2s (may be blocked in a native call).");
        if (snd != null && snd != Thread.CurrentThread && !snd.Join(2000))
            Logger.Warn("Stop: send thread did not exit in 2s (may be blocked in a native call).");
        if (watch != null && watch != Thread.CurrentThread) watch.Join(2000);
        _capThread = null; _sendThread = null; _watcherThread = null;
        try { _dup?.Dispose(); } catch { } _dup = null;
        try { _sender?.Dispose(); } catch { } _sender = null;
        _watchdog?.Dispose(); _watchdog = null;
        lock (_gate) { _slot = null; }
        _measuredFps = 0;
        _errorReason = null;
        Logger.Info("Stopped.");
    }

    private void RestartPipeline()
    {
        try { Stop(); } catch (Exception ex) { Logger.Error(ex, "RestartStop"); }
        if (_cfg.Running) Start(); else SyncMenu();
    }

    /// <summary>Full teardown of sender + duplicator and re-init from current AppConfig, without exiting.</summary>
    private void RestartStreaming()
    {
        if (!_cfg.Running) { _cfg.Running = true; _cfg.Save(); }
        Notify("Restarting stream…");
        RestartPipeline();
    }

    /// <summary>
    /// In-process supervisor (runs on a 5s timer): if streaming is wanted but the
    /// pipeline is not alive, retry Start() forever with exponential backoff
    /// (5s..60s). Never throws, never exits the process; repeated failures are
    /// logged at most once per 30s so app.log is not flooded.
    /// </summary>
    private void EnsureRunning()
    {
        try
        {
            if (_quit || !_cfg.Running)
            {
                return;
            }

            // The watcher thread is part of the pipeline: if it died but the loops
            // are up, Start() (called below) restarts the missing piece.
            bool alive = _capThread?.IsAlive == true && _sendThread?.IsAlive == true
                && _watcherThread?.IsAlive == true;
            if (alive)
            {
                _startBackoffMs = 5000;
                return;
            }

            long now = Environment.TickCount64;
            if (now - _lastStartAttemptTick < _startBackoffMs)
            {
                return;
            }

            _lastStartAttemptTick = now;
            try
            {
                Start();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "EnsureRunning");
            }

            bool relit = _capThread?.IsAlive == true && _sendThread?.IsAlive == true
                && _watcherThread?.IsAlive == true;
            if (relit)
            {
                _startBackoffMs = 5000;
                Logger.Info("Pipeline recovered by in-process supervisor.");
            }
            else
            {
                _startBackoffMs = Math.Min(_startBackoffMs * 2, 60000);
                if (now - _lastRetryLogTick >= 30000)
                {
                    _lastRetryLogTick = now;
                    Logger.Warn("Pipeline still down; will keep retrying (backoff "
                        + (_startBackoffMs / 1000) + "s). reason=" + (_errorReason ?? "unknown"));
                }
            }
            SyncMenu();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "EnsureRunning");
        }
    }

    /// <summary>
    /// In-process watcher thread: ticks every 2s and verifies frames are actually
    /// flowing (frames-sent counter advancing, capture/send beats fresh) and that
    /// no worker is deadlocked or error-looping. On stall, repeated errors, or a
    /// dead worker it tears down and rebuilds the duplicator + sender from current
    /// config with backoff (10s doubling to 60s), logging every action.
    /// NOTE: this watcher lives inside the process, so it cannot survive a native
    /// access violation inside Processing.NDI.Lib.x64.dll — the whole process dies
    /// instantly and uncatchably. The out-of-process safety net for that case is
    /// the per-user "NDI Screenshare" scheduled task (logon trigger + restart on
    /// failure every 1 minute) plus the "NDI Screenshare Heartbeat" hang watchdog
    /// (install/setup-supervisor.ps1 + install/check-heartbeat.ps1).
    /// </summary>
    private void WatcherLoop()
    {
        long lastFrames = Interlocked.Read(ref _framesSent);
        long lastIter = Interlocked.Read(ref _capIterations);
        while (!_stop)
        {
            // 2s tick in short slices so Stop()/Quit() stays responsive.
            for (int i = 0; i < 10 && !_stop; i++) Thread.Sleep(200);
            if (_stop) return;

            try
            {
                if (_quit || !_cfg.Running)
                {
                    lastFrames = Interlocked.Read(ref _framesSent);
                    lastIter = Interlocked.Read(ref _capIterations);
                    continue;
                }

                string? problem = DetectPipelineProblem(ref lastFrames, ref lastIter);
                if (problem == null)
                {
                    _watcherBackoffMs = 10000;
                    continue;
                }

                long now = Environment.TickCount64;
                if (now - _lastWatcherRebuildTick < _watcherBackoffMs)
                {
                    if (now - _lastWatcherLogTick >= 30000)
                    {
                        _lastWatcherLogTick = now;
                        Logger.Warn("Watcher: pipeline problem persists (" + problem
                            + "); backing off " + (_watcherBackoffMs / 1000)
                            + "s before next rebuild.");
                    }
                    continue;
                }

                _lastWatcherRebuildTick = now;
                _watcherBackoffMs = Math.Min(_watcherBackoffMs * 2, 60000);
                Logger.Warn("Watcher: " + problem
                    + "; tearing down and rebuilding pipeline from current config.");
                try
                {
                    RestartPipeline();
                    Logger.Info("Watcher: pipeline rebuild issued.");
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "WatcherRebuild");
                }
                lastFrames = Interlocked.Read(ref _framesSent);
                lastIter = Interlocked.Read(ref _capIterations);
                // This instance exits; RestartPipeline -> Start() launched a fresh
                // watcher. (If the rebuild failed to relight, the 5s retry timer
                // calls Start() again, which restarts this watcher too.)
                return;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Watcher");
            }
        }
    }

    /// <summary>
    /// Single watcher health check. Returns a human-readable problem, or null when
    /// the pipeline looks healthy. Updates the caller's baseline counters.
    /// A long-idle (static) screen also yields no frames; that only triggers a
    /// rebuild after 30s of total silence, and the rebuild is harmless (same
    /// source name — receivers re-sync), matching the existing 5s Watchdog
    /// philosophy while being far gentler.
    /// </summary>
    private string? DetectPipelineProblem(ref long lastFrames, ref long lastIter)
    {
        long now = Environment.TickCount64;

        // Grace period after (re)start while threads spin up and frames start flowing.
        if (now - _pipelineStartTick < 10000)
        {
            lastFrames = Interlocked.Read(ref _framesSent);
            lastIter = Interlocked.Read(ref _capIterations);
            return null;
        }

        bool capAlive = _capThread?.IsAlive == true;
        bool sendAlive = _sendThread?.IsAlive == true;
        if (!capAlive || !sendAlive)
        {
            return "worker thread died (capture alive=" + capAlive
                + ", send alive=" + sendAlive + ")";
        }

        if (_capErrors >= 8)
        {
            return "capture erroring repeatedly (" + _capErrors + " consecutive)";
        }
        if (_sendErrors >= 8)
        {
            return "send erroring repeatedly (" + _sendErrors + " consecutive)";
        }

        long frames = Interlocked.Read(ref _framesSent);
        long iter = Interlocked.Read(ref _capIterations);
        bool iterStalled = iter == lastIter;
        lastFrames = frames;
        lastIter = iter;

        // Iterations advance on every capture-loop pass (even DXGI timeouts on a
        // static screen), so a flat counter with no errors means the loop is
        // blocked inside a native call — a deadlock no retry counter can see.
        if (iterStalled && _capErrors == 0)
        {
            return "capture loop stalled (no iterations in 2s+, no errors)";
        }

        long lastBeat = Math.Max(_lastSendTick, _lastCaptureTick);
        if (now - lastBeat > 30000 && _capErrors == 0 && _sendErrors == 0)
        {
            return "no frames flowing for 30s+ (framesSent=" + frames + ")";
        }

        return null;
    }

    /// <summary>
    /// Called by Program's global exception handlers: log the fatal, then rebuild
    /// the pipeline on the UI thread instead of dying. Never throws, never rethrows.
    /// </summary>
    public void RecoverFromFatal(string context, Exception ex)
    {
        try
        {
            Logger.Error(ex, context + " (recovering: rebuilding pipeline)");
        }
        catch
        {
            // Never throw.
        }

        try
        {
            if (_ui != null)
            {
                _ui.Post(_ =>
                {
                    try { RestartPipeline(); }
                    catch (Exception inner) { Logger.Error(inner, "RecoverRestart"); }
                }, null);
            }
            else
            {
                try { RestartPipeline(); }
                catch (Exception inner) { Logger.Error(inner, "RecoverRestart"); }
            }
        }
        catch
        {
            // Never throw.
        }
    }

    /// <summary>Log the first loop error immediately, then at most once per 30s.</summary>
    private static void LogThrottled(ref long lastLogTick, ref int counter, Exception ex, string ctx)
    {
        try
        {
            counter++;
            long now = Environment.TickCount64;
            if (counter == 1 || now - lastLogTick >= 30000)
            {
                lastLogTick = now;
                Logger.Error(ex, ctx + " (occurrence #" + counter + "; repeats throttled)");
            }
        }
        catch
        {
            // Never throw.
        }
    }

    /// <summary>Capped backoff for the capture/send loops: 500ms doubling to 8s max.</summary>
    private static int LoopBackoff(int consecutiveErrors)
    {
        try
        {
            return Math.Min(500 * (1 << Math.Min(consecutiveErrors, 4)), 8000);
        }
        catch
        {
            return 2000;
        }
    }

    /// <summary>Mark the pipeline as errored: red-tinted tray icon + reason in the status header.</summary>
    private void Fail(string reason)
    {
        _errorReason = reason;
        Logger.Error(new Exception(reason), "Pipeline");
        SyncMenu();
    }

    private void CaptureLoop()
    {
        while (!_stop)
        {
            try
            {
                Interlocked.Increment(ref _capIterations);
                if (_dup == null)
                {
                    try { _dup = new DesktopDuplicator(_cfg.DisplayDeviceName, _cfg.HideCursor); }
                    catch (Exception ex) when (ex is TypeLoadException || ex is FileNotFoundException || ex is DllNotFoundException)
                    {
                        LogThrottled(ref _lastCaptureErrorLogTick, ref _captureErrorCount, ex, "Capture unavailable");
                        Thread.Sleep(LoopBackoff(_capErrors++));
                        continue;
                    }
                }
                if (_dup!.TryGetFrame(out var frame) && frame != null)
                {
                    _capErrors = 0;
                    _lastCaptureTick = Environment.TickCount64;
                    lock (_gate) { _slot = frame; _slotSeq++; }
                    _watchdog?.Beat();
                }
                else Thread.Sleep(5);
            }
            catch (Exception ex)
            {
                LogThrottled(ref _lastCaptureErrorLogTick, ref _captureErrorCount, ex, "Capture");
                try { _dup?.Dispose(); } catch { } _dup = null;
                Thread.Sleep(LoopBackoff(_capErrors++));
            }
        }
    }

    private void SendLoop()
    {
        int next = Environment.TickCount;
        while (!_stop)
        {
            try
            {
                if (_restartRequested) { _restartRequested = false; RestartPipeline(); return; }
                int interval = 1000 / Math.Clamp(_cfg.Fps, 5, 60);
                CapturedFrame? f = null;
                lock (_gate)
                {
                    if (_slot != null && _slotSeq != _sentSeq) { f = _slot; _sentSeq = _slotSeq; }
                }
                if (f != null)
                {
                    try
                    {
                        _sender?.Send(_cfg.Scale < 0.99 ? Downscale(f, _cfg.Scale) : f);
                        _watchdog?.Beat();
                        NoteSentFrame();
                    }
                    catch (Exception ex)
                    {
                        LogThrottled(ref _lastSendErrorLogTick, ref _sendErrorCount, ex, "Send");
                        Thread.Sleep(LoopBackoff(_sendErrors++));
                    }
                }
                next += interval;
                int d = next - Environment.TickCount;
                if (d > 0) Thread.Sleep(d); else next = Environment.TickCount;
            }
            catch (Exception ex)
            {
                // Never let the send loop die: log throttled, back off, keep going.
                LogThrottled(ref _lastSendErrorLogTick, ref _sendErrorCount, ex, "SendLoop");
                Thread.Sleep(LoopBackoff(_sendErrors++));
                next = Environment.TickCount;
            }
        }
    }

    private void NoteSentFrame()
    {
        // Success marker for the watcher thread: frames flowing + when.
        Interlocked.Increment(ref _framesSent);
        _lastSendTick = Environment.TickCount64;
        _sendErrors = 0;
        _fpsCounter++;
        int t = Environment.TickCount;
        int dt = t - _fpsWindowTick;
        if (dt >= 1000)
        {
            _measuredFps = _fpsCounter * 1000 / dt;
            _fpsCounter = 0;
            _fpsWindowTick = t;
        }
    }

    private static CapturedFrame Downscale(CapturedFrame f, double scale)
    {
        int w = Math.Max(1, (int)(f.Width * scale)), h = Math.Max(1, (int)(f.Height * scale));
        var o = new byte[w * h * 4];
        int ostride = w * 4;
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Min(f.Height - 1, (int)(y / scale));
            int srow = sy * f.Stride, drow = y * ostride;
            for (int x = 0; x < w; x++)
            {
                int s = srow + Math.Min(f.Width - 1, (int)(x / scale)) * 4, d = drow + x * 4;
                o[d] = f.Bgra[s]; o[d + 1] = f.Bgra[s + 1]; o[d + 2] = f.Bgra[s + 2]; o[d + 3] = f.Bgra[s + 3];
            }
        }
        return new CapturedFrame { Width = w, Height = h, Stride = ostride, Bgra = o };
    }

    // ---------------------------------------------------------------- tray art

    private void ApplyTrayIcon()
    {
        _icon.Icon = GetTrayIcon(_state, _blinkOn);
    }

    private Icon GetTrayIcon(TrayState state, bool bright)
    {
        var key = (state, bright);
        if (_icons.TryGetValue(key, out var cached)) return cached;
        try
        {
            byte[] ico = TrayIconArt.BuildIco(state, bright);
            var ms = new MemoryStream(ico);
            var created = new Icon(ms); // needs its stream for the icon's lifetime
            _iconStreams.Add(ms);
            _icons[key] = created;
            return created;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "TrayIcon");
            return SystemIcons.Application;
        }
    }

    // ---------------------------------------------------------------- plumbing

    private void Notify(string text)
    {
        try
        {
            _icon.BalloonTipTitle = "NDI Screenshare";
            _icon.BalloonTipText = text;
            _icon.ShowBalloonTip(3000);
        }
        catch { }
        Logger.Info(text);
    }

    private void Quit()
    {
        _quit = true;
        try { _retryTimer?.Dispose(); } catch { } _retryTimer = null;
        try { _heartbeat?.Stop(); } catch { } _heartbeat = null;
        try
        {
            _blink.Stop();
            _blink.Dispose();
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            Stop();
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            foreach (var kv in _icons) kv.Value.Dispose();
            foreach (var s in _iconStreams) s.Dispose();
            _icons.Clear();
            _iconStreams.Clear();
        }
        catch { }
        ExitThread();
    }

    /// <summary>
    /// Code-drawn tray icon (no binary assets, no NuGet): monitor + broadcast arcs + play
    /// glyph + status dot, rendered as a multi-size (16/32) PNG-in-ICO in memory.
    /// Tint: live = teal, stopped = grey, error = amber/red. Near-white shell with a dark
    /// under-stroke keeps it readable on both light and dark taskbars.
    /// </summary>
    private static class TrayIconArt
    {
        public static byte[] BuildIco(TrayState state, bool bright)
        {
            var images = new List<(int size, byte[] png)>();
            foreach (int size in new[] { 16, 32 })
            {
                using var bmp = Render(size, state, bright);
                using var p = new MemoryStream();
                bmp.Save(p, ImageFormat.Png);
                images.Add((size, p.ToArray()));
            }

            using var outMs = new MemoryStream();
            using (var w = new BinaryWriter(outMs, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write((ushort)0);           // reserved
                w.Write((ushort)1);           // type: icon
                w.Write((ushort)images.Count);
                int offset = 6 + 16 * images.Count;
                foreach (var (size, png) in images)
                {
                    w.Write((byte)size); w.Write((byte)size);
                    w.Write((byte)0);        // palette colors
                    w.Write((byte)0);        // reserved
                    w.Write((ushort)1);      // planes
                    w.Write((ushort)32);     // bpp
                    w.Write((uint)png.Length);
                    w.Write((uint)offset);
                    offset += png.Length;
                }
                foreach (var (_, png) in images) w.Write(png);
            }
            return outMs.ToArray();
        }

        private static Bitmap Render(int size, TrayState state, bool bright)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float s = size / 32f;
            float U(float v) => v * s;

            Color shell = Color.FromArgb(240, 244, 248);              // near-white silhouette
            Color edge = Color.FromArgb(170, 32, 42, 58);             // dark under-stroke for light bg contrast
            Color accent = state switch
            {
                TrayState.Live => bright ? Color.FromArgb(74, 222, 199) : Color.FromArgb(26, 168, 150),
                TrayState.Error => Color.FromArgb(248, 140, 50),      // amber accents
                _ => Color.FromArgb(148, 158, 172),                   // grey accents
            };
            Color dot = state switch
            {
                TrayState.Live => bright ? Color.FromArgb(52, 234, 198) : Color.FromArgb(20, 170, 140),
                TrayState.Error => Color.FromArgb(239, 68, 68),       // red dot
                _ => Color.FromArgb(120, 130, 144),                   // grey dot
            };

            // --- broadcast arcs (top-right, radiating outward from the monitor corner) ---
            float cx = 18.5f, cy = 9f;
            foreach (var (r, wd) in new[] { (5.0f, 2.1f), (8.6f, 1.8f) })
            {
                using var under = new Pen(edge, U(wd + 1.2f));
                using var pen = new Pen(accent, U(wd));
                var rect = new RectangleF(U(cx - r), U(cy - r), U(2 * r), U(2 * r));
                g.DrawArc(under, rect, -80, 65);
                g.DrawArc(pen, rect, -80, 65);
            }

            // --- monitor: rounded frame, faint screen, stand ---
            using (var penUnder = new Pen(edge, U(2.2f + 1.3f)) { LineJoin = LineJoin.Round })
            using (var penShell = new Pen(shell, U(2.2f)) { LineJoin = LineJoin.Round })
            {
                var frame = RoundedRect(U(2), U(8), U(16), U(12), U(2.2f));
                g.DrawPath(penUnder, frame);
                g.DrawPath(penShell, frame);

                using var screenFill = new SolidBrush(Color.FromArgb(state == TrayState.Stopped ? 26 : 40, accent));
                var screen = RoundedRect(U(4.4f), U(10.2f), U(11.2f), U(7.6f), U(1.2f));
                g.FillPath(screenFill, screen);

                g.DrawLine(penUnder, U(10f), U(20f), U(10f), U(22.6f));
                g.DrawLine(penShell, U(10f), U(20f), U(10f), U(22.6f));
                g.DrawLine(penUnder, U(6.5f), U(23.2f), U(13.5f), U(23.2f));
                g.DrawLine(penShell, U(6.5f), U(23.2f), U(13.5f), U(23.2f));
            }

            // --- play glyph centred on screen: share/monitoring motif ---
            var tri = new[]
            {
                new PointF(U(8.2f), U(11.6f)),
                new PointF(U(8.2f), U(16.6f)),
                new PointF(U(12.6f), U(14.1f)),
            };
            using (var triUnder = new Pen(edge, U(1.6f)) { LineJoin = LineJoin.Round })
            using (var triFill = new SolidBrush(state == TrayState.Stopped ? Color.FromArgb(215, 222, 230) : accent))
            {
                g.DrawPolygon(triUnder, tri);
                g.FillPolygon(triFill, tri);
            }

            // --- status dot (bottom-right): teal/green live, grey stopped, red error ---
            float dr = 4.6f;
            using (var ring = new Pen(Color.FromArgb(200, 15, 23, 32), U(1.5f)))
            using (var halo = new Pen(Color.FromArgb(230, 240, 244, 248), U(1.1f)))
            using (var dotBrush = new SolidBrush(dot))
            {
                var oval = new RectangleF(U(26 - dr), U(26 - dr), U(2 * dr), U(2 * dr));
                g.DrawEllipse(ring, oval);
                g.FillEllipse(dotBrush, oval);
                g.DrawEllipse(halo, oval);
            }

            return bmp;
        }

        private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = 2 * r;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}

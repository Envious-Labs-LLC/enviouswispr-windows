using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EnviousWispr.Audio;
using EnviousWispr.Asr;
using EnviousWispr.Input;
using EnviousWispr.Polish;
using Microsoft.ML.OnnxRuntime;

namespace EnviousWispr;

public partial class App : Application
{
    private OverlayWindow? _overlay;
    private DictationPipeline? _pipeline;
    private GlobalHotkey? _hotkey;
    private Tray.TrayIcon? _tray;
    private Mutex? _singleInstance;
    private string _readyDetail = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\EnviousWispr.Windows", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log($"unhandled: {args.Exception}");
            args.Handled = true;
            try
            {
                MessageBox.Show(
                    "EnviousWispr could not continue. Please relaunch it from the desktop shortcut. " +
                    "If it happens again, share the enviouswispr.log file from the app folder.",
                    "EnviousWispr stopped",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { /* shutdown must not depend on the error dialog */ }
            Shutdown(-1);
        };

        var cfg = ConfigLoader.Load();
        Log($"base dir: {cfg.BaseDir}");

        // 1. ASR engine (fast path: int8 QDQ, threads pinned per S1).
        var asrModelDir = cfg.Resolve(cfg.Asr.ModelDir);
        Log($"loading Parakeet from {asrModelDir} (pack={cfg.Asr.Pack}, provider={cfg.Asr.Provider}, threads={cfg.Asr.IntraOpThreads})");
        if (cfg.Asr.Provider == "cuda" && cfg.Asr.Pack != "fp32")
            Log("WARNING: provider=cuda with the int8 QDQ pack is the measured disaster case " +
                "(742 Memcpy nodes, ~2x slower than pinned CPU — notes/spike-s1.md). " +
                "Use pack=fp32 for the GPU tier.");
        ParakeetEngine asr;
        var asrMode = $"{cfg.Asr.Provider}/{cfg.Asr.Pack}";
        var cudaRuntimeDir = string.IsNullOrWhiteSpace(cfg.Asr.CudaRuntimeDir)
            ? null
            : cfg.Resolve(cfg.Asr.CudaRuntimeDir);
        try
        {
            asr = new ParakeetEngine(asrModelDir, cfg.Asr.IntraOpThreads, cfg.Asr.InterOpThreads,
                cfg.Asr.MaxTokensPerStep, cfg.Asr.Pack, useCuda: cfg.Asr.Provider == "cuda",
                cudaRuntimeDir: cudaRuntimeDir);
        }
        catch (Exception ex) when (cfg.Asr.Provider == "cuda" &&
                                   ex is OnnxRuntimeException or DllNotFoundException or
                                       EntryPointNotFoundException or DirectoryNotFoundException)
        {
            Log($"CUDA ASR unavailable ({ex.Message}); falling back to CPU/{cfg.Asr.Pack}");
            asr = new ParakeetEngine(asrModelDir, cfg.Asr.IntraOpThreads, cfg.Asr.InterOpThreads,
                cfg.Asr.MaxTokensPerStep, cfg.Asr.Pack, useCuda: false);
            asrMode = $"cpu/{cfg.Asr.Pack}";
        }
        Log($"Parakeet engine ready ({asrMode})");
        _readyDetail = $"hold {cfg.Hotkey} · {asrMode}";

        // 2. EG-1 polish server (background; the app is usable while it loads).
        EgOneServer? server = null;
        if (cfg.Eg1.Enabled)
        {
            server = new EgOneServer();
            server.Log += s => Log(s);
            var shardPath = Path.Combine(cfg.Resolve(cfg.Eg1.ModelDir), cfg.Eg1.EntrypointShard);
            _ = Task.Run(async () =>
            {
                try
                {
                    var ok = await server.StartAsync(cfg.Resolve(cfg.Eg1.ServerExe), shardPath,
                        cfg.Eg1.ContextTokens, cfg.Eg1.StartTimeoutSeconds, cfg.Eg1.GpuLayers);
                    if (cfg.Eg1.GpuLayers != "0")
                        Log($"EG-1 gpuLayers={cfg.Eg1.GpuLayers} (requires a CUDA llama-server as eg1.serverExe)");
                    if (ok && server.Endpoint is not null)
                    {
                        var polisher = new EgOnePolisher(server.Endpoint, cfg.Eg1.RequestTimeoutSeconds);
                        // Activation probe — GREEN requires the real transformation.
                        var probe = await polisher.PolishAsync(EgOneProbe.ProbeTranscript);
                        var (green, output) = EgOneProbe.Evaluate(probe);
                        _polisherRef = green ? polisher : null;
                        Log($"EG-1 probe: {(green ? "GREEN" : "YELLOW")} → {output}");
                        if (green)
                        {
                            _readyDetail = $"hold {cfg.Hotkey} · ASR {asrMode} · EG-1 ready";
                        }
                        else
                        {
                            await server.DisposeAsync();
                            _readyDetail = $"hold {cfg.Hotkey} · raw text mode";
                        }
                        ShowState(PipelineState.Idle, _readyDetail);
                    }
                    else
                    {
                        _polisherRef = null;
                        _readyDetail = $"hold {cfg.Hotkey} · raw text mode";
                        ShowState(PipelineState.Idle, _readyDetail);
                    }
                }
                catch (Exception ex)
                {
                    Log($"EG-1 startup failed: {ex.Message}");
                    _polisherRef = null;
                    await server.DisposeAsync();
                    _readyDetail = $"hold {cfg.Hotkey} · raw text mode";
                    ShowState(PipelineState.Idle, _readyDetail);
                }
            });
            ShowState(PipelineState.Idle, "EG-1 loading…");
        }
        else
        {
            ShowState(PipelineState.Idle, "EG-1 disabled");
        }

        // 3. Capture + pipeline.
        var capture = new CaptureService();
        _pipeline = new DictationPipeline(capture, asr, server, () => _polisherRef,
            text => Dispatcher.Invoke(() => TextInserter.Paste(text)));
        _pipeline.Log += s => Log(s);
        _pipeline.StateChanged += s => ShowState(s, DetailFor(s));

        // 4. Overlay + hotkey.
        _overlay = new OverlayWindow();
        _overlay.Show();
        ShowState(PipelineState.Idle, _readyDetail);

        var vk = ParseHotkey(cfg.Hotkey);
        _hotkey = new GlobalHotkey(vk);
        _hotkey.KeyDown += () => _pipeline.HotKeyDown();
        _hotkey.KeyUp += () => _pipeline.HotKeyUp();
        Log($"hotkey {cfg.Hotkey} (vk {vk}) active");

        // 5. Tray: presence + status + the only quit path. Autostart defaults ON
        //    (dictation must be resident at login, like the Mac's menu-bar app);
        //    the tray menu item toggles it.
        var autostart = Tray.TrayIcon.AutostartEnabled();
        if (!Tray.TrayIcon.AutostartConfigured())
        {
            Tray.TrayIcon.SetAutostart(true);
            autostart = true;
            Log("autostart: enabled (HKCU Run)");
        }
        else if (autostart)
        {
            // A newer published build may live at a different path. Keep the
            // existing opt-in but refresh the Run entry to this executable.
            Tray.TrayIcon.SetAutostart(true);
        }
        _tray = new Tray.TrayIcon($"{cfg.Hotkey} ready", cfg.Hotkey, autostart,
            toggle => { Tray.TrayIcon.SetAutostart(toggle); Log($"autostart: {(toggle ? "enabled" : "disabled")} (tray)"); },
            () => { Log("quit requested from tray"); Shutdown(); });
    }

    // The polisher is created on the EG-1 load task; the pipeline reads it
    // per-dictation so a late server start still upgrades the next run.
    private volatile EgOnePolisher? _polisherRef;

    private static uint ParseHotkey(string name) =>
        Enum.TryParse<Key>(name, true, out var key)
            ? (uint)KeyInterop.VirtualKeyFromKey(key)
            : throw new ArgumentException($"bad hotkey {name}");

    private void ShowState(PipelineState s, string detail)
    {
        var (label, color) = s switch
        {
            PipelineState.Idle => ("ready", Color.FromRgb(0x55, 0xFF, 0x55)),
            PipelineState.Recording => ("recording…", Color.FromRgb(0xFF, 0x55, 0x55)),
            PipelineState.Transcribing => ("transcribing…", Color.FromRgb(0xFF, 0xC4, 0x00)),
            PipelineState.Polishing => ("polishing…", Color.FromRgb(0xC7, 0x92, 0xFF)),
            PipelineState.Done => ("done", Color.FromRgb(0x55, 0xD0, 0xFF)),
            PipelineState.Error => ("error", Color.FromRgb(0xFF, 0x40, 0x40)),
            _ => ("?", Color.FromRgb(0x88, 0x88, 0x88)),
        };
        _overlay?.SetState(label, detail, color);
        _tray?.SetStatus(label, detail);
    }

    private string DetailFor(PipelineState s)
    {
        var t = _pipeline?.LastTimings;
        if (s == PipelineState.Done && t is not null)
        {
            if (t.Delivery == PasteResult.NotAttempted)
                return "no speech heard";
            if (t.Delivery == PasteResult.ClipboardOnly)
                return "copied · press Ctrl+V";
            return $"asr {t.AsrMs} ms · polish {t.PolishMs?.ToString() ?? "n/a"} ms · total {t.TotalMs} ms";
        }
        return s switch
        {
            PipelineState.Recording => "release to transcribe",
            PipelineState.Idle => _readyDetail,
            _ => string.Empty,
        };
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Console.WriteLine(line);
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "enviouswispr.log");
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch { /* logging must never break dictation */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _tray?.Dispose();
        _ = _pipeline?.DisposeAsync();
        try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

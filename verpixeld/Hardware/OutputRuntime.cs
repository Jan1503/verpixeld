using SkiaSharp;
using verpixeld.Configuration;
using verpixeld.Services;

namespace verpixeld.Hardware;

/// <summary>
///     Holds the currently active output backend and the live option objects for every backend.
///     Only one renderer is ever running. Option changes apply to the live instance when possible;
///     switching backends (or changing geometry) is done under a lock so the render loop never
///     talks to a disposed device. Geometry changes still require a process restart because the
///     rest of the app (CanvasManager, media, AI) is sized once at startup.
/// </summary>
public sealed class OutputRuntime : IMatrixRenderer, IDisposable
{
    private readonly object _lock = new();
    private IMatrixRenderer _inner = null!;
    private bool _started;

    public OutputRuntime(AppOptions app, MatrixOptions matrix, HdmiOptions hdmi, SpiOptions spi,
        NetworkOptions network, ImageCorrectionService imageCorrection)
    {
        App = app;
        Matrix = matrix;
        Hdmi = hdmi;
        Spi = spi;
        Network = network;
        ImageCorrection = imageCorrection;
        Mode = NormalizeMode(app.OutputMode, app.SimulationMode);
    }

    public AppOptions App { get; }
    public MatrixOptions Matrix { get; }
    public HdmiOptions Hdmi { get; }
    public SpiOptions Spi { get; }
    public NetworkOptions Network { get; }
    public ImageCorrectionService ImageCorrection { get; }

    /// <summary>Canonical mode: network | hdmi | spi | gpio | simulation.</summary>
    public string Mode { get; private set; }

    public int Width
    {
        get { lock (_lock) return _inner.Width; }
    }

    public int Height
    {
        get { lock (_lock) return _inner.Height; }
    }

    public bool HandlesColorCorrection
    {
        get { lock (_lock) return _inner.HandlesColorCorrection; }
    }

    public T? As<T>() where T : class
    {
        lock (_lock) return _inner as T;
    }

    public void Start()
    {
        if (_started) return;
        if (Mode == "gpio" && AppPaths.RunningInContainer())
        {
            var fallback = string.IsNullOrWhiteSpace(Network.Host) ? "simulation" : "network";
            Console.WriteLine(
                $"[OUTPUT] GPIO is not available in a container; using {fallback} " +
                $"(set App__OutputMode=network and Network__Host=<panel-ip>).");
            Mode = fallback;
            App.OutputMode = fallback;
            App.SimulationMode = fallback == "simulation";
        }

        try
        {
            _inner = CreateRenderer(Mode);
            _inner.Initialize();
        }
        catch (Exception ex) when (Mode == "gpio")
        {
            // Native GPIO create used to SIGSEGV on a bad options struct / 128-row
            // FM6126A config, which killed the whole process before the web UI
            // could come up. A managed failure (or a now-fixed ABI mismatch that
            // returns NULL) must keep the app running so the output can be switched.
            Console.WriteLine($"[OUTPUT] GPIO init failed: {ex.Message}");
            Console.WriteLine(
                "[OUTPUT] Falling back to simulation so the web UI stays up. Switch Active Output to Network (or fix Hardware rows/cols) and Save.");
            try
            {
                if (_inner is IDisposable d) d.Dispose();
            }
            catch
            {
                /* ignore */
            }

            Mode = "simulation";
            App.SimulationMode = true;
            _inner = CreateRenderer("simulation");
            _inner.Initialize();
        }

        _started = true;
        Console.WriteLine($"[OUTPUT] Active: {Mode} ({_inner.Width}x{_inner.Height})");
    }

    public void Initialize()
    {
        lock (_lock) _inner.Initialize();
    }

    public void RenderFrame(SKBitmap bitmap)
    {
        lock (_lock) _inner.RenderFrame(bitmap);
    }

    public void Shutdown()
    {
        lock (_lock) _inner.Shutdown();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_inner is IDisposable d)
                d.Dispose();
        }
    }

    /// <summary>
    ///     Switch the active output. Persists App.OutputMode. Live-swaps when the new backend has the
    ///     same pixel size as the running canvas; otherwise the config is saved and a restart is required.
    /// </summary>
    public OutputSwitchResult SetMode(string requested, bool persist = true)
    {
        var mode = NormalizeMode(requested, simulation: false);
        if (persist)
            PersistMode(mode);

        if (string.Equals(mode, Mode, StringComparison.OrdinalIgnoreCase))
            return new OutputSwitchResult(true, false, Mode, mode, $"Already on {mode}.");

        // rpi-rgb-led-matrix claims GPIO, starts a realtime refresh thread, and
        // is documented to be created before other threads exist. Live init or
        // teardown from a running ASP.NET process segfaults (and older
        // librgbmatrix.so builds also reject --led-rp1-pio). Always restart.
        if (mode == "gpio" || Mode == "gpio")
        {
            App.OutputMode = mode;
            App.SimulationMode = mode == "simulation";
            return new OutputSwitchResult(true, true, Mode, mode,
                "Hardware (rpi-rgb-led-matrix) can only be started at process launch. Setting saved — restart verpixeld to apply.");
        }

        IMatrixRenderer next;
        try
        {
            next = CreateRenderer(mode);
        }
        catch (Exception ex)
        {
            return new OutputSwitchResult(false, true, Mode, mode, $"Cannot build {mode}: {ex.Message}");
        }

        if (next.Width != Width || next.Height != Height)
        {
            if (next is IDisposable d) d.Dispose();
            App.OutputMode = mode;
            App.SimulationMode = mode == "simulation";
            return new OutputSwitchResult(true, true, Mode, mode,
                $"Output '{mode}' is {next.Width}x{next.Height}, current canvas is {Width}x{Height}. Restart required.");
        }

        try
        {
            // Drop frames onto a no-op so the old device can be released before the new one claims it.
            IMatrixRenderer old;
            var placeholder = new SimulationMatrixRenderer(Width, Height);
            placeholder.Initialize();
            lock (_lock)
            {
                old = _inner;
                _inner = placeholder;
            }

            try { old.Shutdown(); } catch (Exception ex) { Console.WriteLine($"[OUTPUT] old shutdown: {ex.Message}"); }
            if (old is IDisposable od)
                try { od.Dispose(); } catch { /* ignore */ }

            next.Initialize();
            lock (_lock)
            {
                _inner = next;
                Mode = mode;
                App.OutputMode = mode;
                App.SimulationMode = mode == "simulation";
            }

            Console.WriteLine($"[OUTPUT] Switched live → {mode} ({Width}x{Height})");
            return new OutputSwitchResult(true, false, mode, mode, $"Now outputting via {mode}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OUTPUT] Live switch to {mode} failed: {ex.Message}");
            return new OutputSwitchResult(false, true, Mode, mode,
                $"Live switch to {mode} failed ({ex.Message}). Setting saved; restart to apply.");
        }
    }

    public static string NormalizeMode(string? raw, bool simulation)
    {
        var mode = (raw ?? "").Trim().ToLowerInvariant();
        if (mode is "hardware" or "hub75" or "rpi" or "hzeller") mode = "gpio";
        if (string.IsNullOrEmpty(mode))
            mode = simulation ? "simulation" : "gpio";
        return mode switch
        {
            "network" or "hdmi" or "spi" or "gpio" or "simulation" => mode,
            _ => "gpio"
        };
    }

    internal IMatrixRenderer CreateRenderer(string mode)
    {
        var ledW = Matrix.Cols * Matrix.ChainLength;
        var ledH = Matrix.Rows * Matrix.Parallel;
        if (ledW <= 0 || ledH <= 0)
        {
            ledW = App.DisplayWidth;
            ledH = App.DisplayHeight;
        }

        return mode switch
        {
            "simulation" => new SimulationMatrixRenderer(
                App.DisplayWidth > 0 ? App.DisplayWidth : ledW,
                App.DisplayHeight > 0 ? App.DisplayHeight : ledH),
            "hdmi" => new HdmiMatrixRenderer(
                Hdmi.WallWidth > 0 ? Hdmi.WallWidth : ledW,
                Hdmi.WallHeight > 0 ? Hdmi.WallHeight : ledH,
                Hdmi),
            "spi" => new SpiMatrixRenderer(
                Spi.WallWidth > 0 ? Spi.WallWidth : 256,
                Spi.WallHeight > 0 ? Spi.WallHeight : 128,
                Spi),
            "network" => new NetworkMatrixRenderer(
                Network.WallWidth > 0 ? Network.WallWidth : 256,
                Network.WallHeight > 0 ? Network.WallHeight : 128,
                Network, ImageCorrection),
            _ => new RgbMatrixRenderer(Matrix)
        };
    }

    private void PersistMode(string mode)
    {
        try
        {
            var root = AppSettingsStore.Load();
            var app = AppSettingsStore.Section(root, "App");
            AppSettingsStore.Set(app, "OutputMode", mode);
            AppSettingsStore.Set(app, "SimulationMode", mode == "simulation");
            AppSettingsStore.Save(root);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OUTPUT] persist mode failed: {ex.Message}");
        }
    }
}

public readonly record struct OutputSwitchResult(
    bool Success, bool RequiresRestart, string ActiveMode, string SavedMode, string Message);

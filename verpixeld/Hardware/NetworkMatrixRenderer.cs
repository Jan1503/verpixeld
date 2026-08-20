using System.Runtime.InteropServices;
using System.Text.Json;
using PixPlane;
using SkiaSharp;
using verpixeld.Configuration;
using verpixeld.Services;

namespace verpixeld.Hardware;

/// <summary>
///     Streams composed frames to the RP2350 + W6300 receiver over UDP. All the panel-specific work
///     (bit-plane packing, geometry, colour LUT, per-column seam correction, paced UDP) lives in the
///     reusable <see cref="PixPlane.PanelStreamer"/> library; this class only adapts verpixeld's config +
///     SKBitmap frames to it, and keeps the hot-reloadable <c>seam_correction.json</c> convenience.
/// </summary>
public sealed class NetworkMatrixRenderer : IMatrixRenderer, IDisposable
{
    private readonly NetworkOptions _options;
    private readonly ImageCorrectionService? _correction;
    private readonly string _seamFile;
    private DateTime _seamStamp = DateTime.MinValue;
    private long _frameCounter;

    private PanelStreamer? _streamer;
    private PanelColorSettings _color;
    private byte[] _srcBuf = [];
    private readonly object _lock = new();   // guards _streamer swaps vs the render-loop thread
    private int _switching;                  // 0 idle, 1 live depth switch in flight
    private int _desiredBits;

    public int Width { get; }
    public int Height { get; }

    /// <summary>
    ///     The network path bakes the global image correction into its own 8-bit -> ColorBits (13-bit) LUT,
    ///     so the render loop must NOT pre-correct the shared 8-bit bitmap (that would requantise to 8 bit
    ///     and waste the panel's depth). Gamma/contrast/brightness/white-balance are therefore applied here
    ///     at full 13-bit precision inside <see cref="PanelStreamer"/>.
    /// </summary>
    public bool HandlesColorCorrection => true;

    /// <summary>R/B swap for this panel's wiring (kept separate from the shared image correction).</summary>
    public bool SwapRedBlue => _color.SwapRedBlue;

    // Live network target (read by the config API; changed via Reconfigure without a restart).
    public string Host => _options.Host;
    public int Port => _options.Port;
    public double TargetMbps => _options.TargetMbps;
    public int ColorBits => _options.ColorBits;

    public NetworkMatrixRenderer(int wallWidth, int wallHeight, NetworkOptions options,
        ImageCorrectionService? correction = null)
    {
        Width = wallWidth;
        Height = wallHeight;
        _options = options;
        _correction = correction;
        _color = ColorFrom(options, correction);

        var sf = string.IsNullOrWhiteSpace(options.SeamCorrectionFile) ? "seam_correction.json" : options.SeamCorrectionFile;
        _seamFile = Path.IsPathRooted(sf) ? sf : Path.Combine(AppContext.BaseDirectory, sf);

        if (correction != null) correction.Changed += OnCorrectionChanged;
    }

    // Colour = the shared image correction (gamma/contrast/brightness/white balance) applied at 13-bit by the
    // PanelStreamer LUT, plus this panel's wiring swap. When no correction is wired, falls back to linear.
    private static PanelColorSettings ColorFrom(NetworkOptions o, ImageCorrectionService? c) => new()
    {
        Curve = c?.Curve ?? "none",
        Gamma = c?.Gamma ?? 2.2,
        Contrast = c?.Contrast ?? 1.0,
        Brightness = c?.Brightness ?? 1.0,
        GainR = c?.GainR ?? 1.0,
        GainG = c?.GainG ?? 1.0,
        GainB = c?.GainB ?? 1.0,
        SwapRedBlue = o.SwapRedBlue
    };

    private void OnCorrectionChanged(ImageCorrectionService c)
    {
        lock (_lock)
        {
            _color = ColorFrom(_options, c);
            _color.SwapRedBlue = _options.SwapRedBlue; // preserve the live swap
            _streamer?.UpdateColor(_color);
        }
    }

    public void Initialize()
    {
        if (Width != 256 || Height != 128)
            throw new InvalidOperationException($"Network bridge expects a 256x128 wall (got {Width}x{Height}).");

        lock (_lock)
        {
            if (_streamer != null) return;
            _streamer = NewStreamer();
            _seamStamp = SafeStamp();
        }

        Console.WriteLine($"[NET] UDP -> {_options.Host}:{_options.Port}, {Width}x{Height} {_options.ColorBits}-bit, " +
                          $"paced to {_options.TargetMbps:0.#} Mbit/s.");
    }

    private PanelStreamer NewStreamer() => new(new PanelStreamOptions
    {
        Host = _options.Host,
        Port = _options.Port,
        TargetMbps = _options.TargetMbps,
        ColorBits = _options.ColorBits,
        WallWidth = Width,
        WallHeight = Height,
        Color = _color,
        Seam = LoadSeamColumns()
    });

    /// <summary>
    ///     Live update of the network target (IP / port / pacing / colour depth) from the web GUI, with no
    ///     restart. The old UDP streamer is torn down and a fresh one opened on the new host, keeping the
    ///     current colour + seam correction. ColorBits must still match the panel firmware's mode (8/14).
    /// </summary>
    public void Reconfigure(string host, int port, double targetMbps, int colorBits)
    {
        lock (_lock)
        {
            _options.Host = string.IsNullOrWhiteSpace(host) ? _options.Host : host.Trim();
            _options.Port = port is > 0 and < 65536 ? port : _options.Port;
            _options.TargetMbps = targetMbps is > 0 and <= 1000 ? targetMbps : _options.TargetMbps;
            _options.ColorBits = colorBits is 8 or 10 or 13 or 14 ? colorBits : _options.ColorBits;

            if (_streamer != null)   // already streaming -> reopen on the new target
            {
                _streamer.Dispose();
                _streamer = NewStreamer();
                _seamStamp = SafeStamp();
            }
        }
        Console.WriteLine($"[NET] reconfigured -> {_options.Host}:{_options.Port}, {_options.ColorBits}-bit, " +
                          $"paced to {_options.TargetMbps:0.#} Mbit/s.");
    }

    /// <summary>
    ///     Live 8/14-bit switch driven by visible canvases. Stops UDP, asks the panel for <c>livemode</c>
    ///     (firmware 1.7+, no reboot), then reopens the streamer. No-op when already at <paramref name="colorBits"/>.
    ///     Does not persist to appsettings — Network.ColorBits remains the boot default.
    /// </summary>
    public void SyncLiveColorBits(int colorBits)
    {
        var bits = colorBits >= 14 ? 14 : 8;
        if (bits == _options.ColorBits && Volatile.Read(ref _switching) == 0) return;
        _desiredBits = bits;
        if (Interlocked.CompareExchange(ref _switching, 1, 0) != 0) return;
        _ = Task.Run(() => SwitchLiveAsync(bits));
    }

    public void RenderFrame(SKBitmap bitmap)
    {
        if (bitmap.Width != Width || bitmap.Height != Height)
            throw new ArgumentException($"Bitmap size ({bitmap.Width}x{bitmap.Height}) != wall ({Width}x{Height})");

        if (Volatile.Read(ref _switching) != 0) return; // don't send across an 8/14 live switch

        lock (_lock)   // don't let a live Reconfigure() dispose the streamer mid-send
        {
            if (_streamer == null) return;
            if ((_frameCounter++ & 31) == 0) MaybeReloadSeam();

            var src = bitmap.GetPixels();
            if (src == IntPtr.Zero) return;
            var rowBytes = bitmap.RowBytes;
            var needed = rowBytes * Height;
            if (_srcBuf.Length != needed) _srcBuf = new byte[needed];
            Marshal.Copy(src, _srcBuf, 0, needed);

            _streamer.SendFrameBgra(_srcBuf, rowBytes); // SKBitmap is BGRA8888, exactly what the DLL wants
        }
    }

    private async Task SwitchLiveAsync(int bits)
    {
        var previous = _options.ColorBits;
        try
        {
            lock (_lock)
            {
                _streamer?.Dispose();
                _streamer = null;
            }

            Console.WriteLine($"[NET] live colour depth {previous}-bit -> {bits}-bit (panel livemode)...");
            await PanelControl.SetColorModeLiveAsync(_options.Host, bits).ConfigureAwait(false);

            lock (_lock)
            {
                _options.ColorBits = bits;
                _streamer = NewStreamer();
                _seamStamp = SafeStamp();
            }
            Console.WriteLine($"[NET] live colour depth now {bits}-bit");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NET] live colour switch failed: {ex.Message}");
            lock (_lock)
            {
                if (_streamer == null)
                {
                    _options.ColorBits = previous;
                    _streamer = NewStreamer();
                    _seamStamp = SafeStamp();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _switching, 0);
            var want = _desiredBits >= 14 ? 14 : 8;
            if (want != _options.ColorBits) SyncLiveColorBits(want);
        }
    }

    /// <summary>Live update of the panel wiring R/B swap from the web GUI (no restart).</summary>
    public void SetSwapRedBlue(bool swapRedBlue)
    {
        lock (_lock)
        {
            _color.SwapRedBlue = swapRedBlue;
            _options.SwapRedBlue = swapRedBlue;
            _streamer?.UpdateColor(_color);
        }
    }

    /// <summary>Current per-column seam correction (read from the hot-reloaded file) for the web GUI.</summary>
    public IReadOnlyList<SeamColumn> GetSeamColumns() => LoadSeamColumns();

    /// <summary>
    ///     Live update of the per-column seam correction from the web GUI. Applies immediately to the
    ///     running streamer; when <paramref name="persist"/> is set it also rewrites seam_correction.json
    ///     (and bumps the hot-reload stamp so the file watcher won't re-apply our own write).
    /// </summary>
    public void SetSeam(IReadOnlyList<SeamColumn> columns, bool persist)
    {
        lock (_lock) _streamer?.UpdateSeam(columns);
        if (persist)
        {
            WriteSeamFile(columns);
            lock (_lock) _seamStamp = SafeStamp();
        }
    }

    // ---- seam correction hot-reload (verpixeld convenience) ---------------------------------

    private sealed class SeamCol
    {
        public int X { get; set; } = -1;
        public double GainR { get; set; } = 1.0;
        public double GainG { get; set; } = 1.0;
        public double GainB { get; set; } = 1.0;
        public double LiftR { get; set; }
        public double LiftG { get; set; }
        public double LiftB { get; set; }
    }

    private sealed class SeamConfig { public List<SeamCol> Columns { get; set; } = []; }

    private DateTime SafeStamp() { try { return File.GetLastWriteTimeUtc(_seamFile); } catch { return DateTime.MinValue; } }

    private List<SeamColumn> LoadSeamColumns()
    {
        try
        {
            if (!File.Exists(_seamFile)) { WriteSeamTemplate(); }
            var cfg = JsonSerializer.Deserialize<SeamConfig>(File.ReadAllText(_seamFile),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var list = new List<SeamColumn>();
            foreach (var c in cfg?.Columns ?? [])
                list.Add(new SeamColumn
                {
                    X = c.X, GainR = c.GainR, GainG = c.GainG, GainB = c.GainB,
                    LiftR = c.LiftR, LiftG = c.LiftG, LiftB = c.LiftB
                });
            return list;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NET] seam load failed: {ex.Message}");
            return [];
        }
    }

    private void MaybeReloadSeam()
    {
        var stamp = SafeStamp();
        if (stamp == _seamStamp) return;
        _seamStamp = stamp;
        _streamer?.UpdateSeam(LoadSeamColumns());
        Console.WriteLine($"[NET] seam correction reloaded from {_seamFile}");
    }

    private void WriteSeamFile(IReadOnlyList<SeamColumn> cols)
    {
        var cfg = new SeamConfig();
        foreach (var c in cols)
            cfg.Columns.Add(new SeamCol
            {
                X = c.X, GainR = c.GainR, GainG = c.GainG, GainB = c.GainB,
                LiftR = c.LiftR, LiftG = c.LiftG, LiftB = c.LiftB
            });
        File.WriteAllText(_seamFile, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[NET] seam written to {_seamFile} ({cfg.Columns.Count} columns)");
    }

    private void WriteSeamTemplate()
    {
        // Default the four scan-home boundary columns to the tuned gain/lift so a fresh install is already
        // corrected (contrast compression on cols 63/127/191/255). Hot-reloadable - edit to taste.
        SeamCol Col(int x) => new()
        {
            X = x, GainR = 0.85, GainG = 0.85, GainB = 0.85, LiftR = 0.004, LiftG = 0.004, LiftB = 0.004
        };
        var cfg = new SeamConfig { Columns = [Col(63), Col(127), Col(191), Col(255)] };
        File.WriteAllText(_seamFile, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[NET] wrote seam-correction template to {_seamFile}");
    }

    public void Shutdown() { Dispose(); Console.WriteLine("[NET] renderer shut down"); }
    public void Dispose()
    {
        if (_correction != null) _correction.Changed -= OnCorrectionChanged;
        lock (_lock)
        {
            _streamer?.Dispose();
            _streamer = null;
        }
    }
}

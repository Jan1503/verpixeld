using System.Runtime.InteropServices;
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
    private int _calibrateBits;              // 0 = canvas-driven; 8 or 14 = Settings tab lock
    private List<SeamColumn> _seam8 = [];
    private List<SeamColumn> _seam14 = [];
    private volatile int _previewGrey = -1;  // -1 = off; 0..255 = solid grey on the wall (curve matching)

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

    /// <summary>
    ///     When 8 or 14, the Settings seam tab owns the panel depth (canvas votes are ignored).
    ///     0 = follow visible canvases again.
    /// </summary>
    public int SeamCalibrateBits => _calibrateBits;

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
            LoadSeamUnlocked();
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
        Seam = ActiveSeam()
    });

    private List<SeamColumn> ActiveSeam() =>
        SeamCorrectionStore.NormalizeBits(_options.ColorBits) == SeamCorrectionStore.Bits14
            ? (_seam14.Count > 0 ? _seam14 : SeamCorrectionStore.DefaultColumns())
            : (_seam8.Count > 0 ? _seam8 : SeamCorrectionStore.IdentityColumns());

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
            var preview = _previewGrey;
            if (preview >= 0)
            {
                var g = (byte)preview;
                for (var y = 0; y < Height; y++)
                {
                    var row = y * rowBytes;
                    for (var x = 0; x < Width; x++)
                    {
                        var o = row + x * 4;
                        _srcBuf[o] = g;
                        _srcBuf[o + 1] = g;
                        _srcBuf[o + 2] = g;
                        _srcBuf[o + 3] = 255;
                    }
                }
            }
            else
            {
                Marshal.Copy(src, _srcBuf, 0, needed);
            }

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

    /// <summary>Seam profile for <paramref name="bits"/> (8 or 14). Null bits → calibrate lock, else live panel depth.</summary>
    public IReadOnlyList<SeamColumn> GetSeamColumns(int? bits = null)
    {
        lock (_lock)
        {
            EnsureSeamLoaded();
            var b = bits ?? (_calibrateBits is 8 or 14 ? _calibrateBits : _options.ColorBits);
            return GetProfileUnlocked(b);
        }
    }

    /// <summary>
    ///     Pin the panel to 8- or 14-bit while the Settings seam tab is open, or pass 0 to
    ///     hand depth back to visible canvases. Does not persist.
    /// </summary>
    public void SetSeamCalibrateBits(int bits) =>
        _calibrateBits = bits is 8 or 14 ? bits : 0;

    /// <summary>Wait until the panel streamer is at <paramref name="colorBits"/> (live <c>livemode</c>).</summary>
    public async Task EnsureColorBitsAsync(int colorBits, CancellationToken ct = default)
    {
        var bits = SeamCorrectionStore.NormalizeBits(colorBits);
        _desiredBits = bits;
        var start = DateTime.UtcNow;
        while (Volatile.Read(ref _switching) != 0)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow - start > TimeSpan.FromSeconds(8)) break;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        if (bits == _options.ColorBits && Volatile.Read(ref _switching) == 0) return;
        if (Interlocked.CompareExchange(ref _switching, 1, 0) != 0)
        {
            while (Volatile.Read(ref _switching) != 0)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            return;
        }

        await SwitchLiveAsync(bits).ConfigureAwait(false);
    }

    /// <summary>
    ///     Solid grey fill on the wall (0..255) so the seam curve can be matched against the neighbour
    ///     column. −1 turns it off and the composed frame is sent again. Host-side only — firmware test
    ///     patterns bypass this LUT.
    /// </summary>
    public int SeamPreviewGrey => _previewGrey;

    public void SetSeamPreview(int grey8) =>
        _previewGrey = grey8 < 0 ? -1 : Math.Clamp(grey8, 0, 255);

    /// <summary>
    ///     Live update of one bit-depth profile. Applies immediately when that depth is on the wall;
    ///     when <paramref name="persist"/> is set it rewrites both profiles in seam_correction.json.
    /// </summary>
    public void SetSeam(IReadOnlyList<SeamColumn> columns, bool persist, int? bits = null)
    {
        lock (_lock)
        {
            EnsureSeamLoaded();
            var b = bits ?? (_calibrateBits is 8 or 14 ? _calibrateBits : _options.ColorBits);
            if (SeamCorrectionStore.NormalizeBits(b) == SeamCorrectionStore.Bits14)
                _seam14 = columns.ToList();
            else
                _seam8 = columns.ToList();
            if (SeamCorrectionStore.NormalizeBits(_options.ColorBits) == SeamCorrectionStore.NormalizeBits(b))
                _streamer?.UpdateSeam(columns);
        }
        if (persist)
        {
            WriteSeamFile();
            lock (_lock) _seamStamp = SafeStamp();
        }
    }

    // ---- seam correction hot-reload (verpixeld convenience) ---------------------------------

    private DateTime SafeStamp() { try { return File.GetLastWriteTimeUtc(_seamFile); } catch { return DateTime.MinValue; } }

    private void EnsureSeamLoaded()
    {
        if (_seam8.Count > 0 || _seam14.Count > 0) return;
        LoadSeamUnlocked();
    }

    private List<SeamColumn> GetProfileUnlocked(int bits) =>
        SeamCorrectionStore.NormalizeBits(bits) == SeamCorrectionStore.Bits14
            ? (_seam14.Count > 0 ? _seam14 : SeamCorrectionStore.DefaultColumns())
            : (_seam8.Count > 0 ? _seam8 : SeamCorrectionStore.IdentityColumns());

    private void LoadSeamUnlocked()
    {
        if (!File.Exists(_seamFile))
        {
            _seam8 = SeamCorrectionStore.IdentityColumns();
            _seam14 = SeamCorrectionStore.DefaultColumns();
            WriteSeamUnlocked();
            Console.WriteLine($"[NET] wrote seam-correction template to {_seamFile}");
            return;
        }

        var store = SeamCorrectionStore.Load(_seamFile);
        _seam8 = store.Profile8;
        _seam14 = store.Profile14;
    }

    private void MaybeReloadSeam()
    {
        var stamp = SafeStamp();
        if (stamp == _seamStamp) return;
        _seamStamp = stamp;
        LoadSeamUnlocked();
        _streamer?.UpdateSeam(ActiveSeam());
        Console.WriteLine($"[NET] seam correction reloaded from {_seamFile}");
    }

    private void WriteSeamFile()
    {
        lock (_lock) WriteSeamUnlocked();
        Console.WriteLine($"[NET] seam written to {_seamFile} (8-bit {_seam8.Count} / 14-bit {_seam14.Count} columns)");
    }

    private void WriteSeamUnlocked()
    {
        var store = new SeamCorrectionStore();
        store.Set(8, _seam8.Count > 0 ? _seam8 : SeamCorrectionStore.IdentityColumns());
        store.Set(14, _seam14.Count > 0 ? _seam14 : SeamCorrectionStore.DefaultColumns());
        store.Save(_seamFile);
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

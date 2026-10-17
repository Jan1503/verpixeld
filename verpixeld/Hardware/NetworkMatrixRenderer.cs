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
///     Pack + paced UDP run on a dedicated sender thread (latest-wins) so the compositor is not
///     blocked by TargetMbps sleeps.
/// </summary>
public sealed class NetworkMatrixRenderer : IMatrixRenderer, IDisposable
{
    private readonly NetworkOptions _options;
    private readonly ImageCorrectionService? _correction;
    private readonly string _seamFile;
    private DateTime _seamStamp = DateTime.MinValue;
    private long _frameCounter;

    private readonly List<PanelStreamer> _streamers = [];
    private readonly List<NetworkPanelTarget> _openTargets = [];
    private byte[][] _crops = [];
    private PanelColorSettings _color;
    private byte[] _srcBuf = [];             // latest BGRA from the compositor (overwritten = drop)
    private byte[] _workBuf = [];            // stable copy the sender thread packs
    private int _pendingStride;
    private bool _hasPending;
    private bool _sending;
    private volatile bool _stopSender;
    private int _checkSeam;
    private Thread? _sender;
    private readonly ManualResetEventSlim _frameReady = new(false);
    private readonly object _lock = new();   // streamer lifetime, pending handoff, LUT updates
    private int _switching;                  // 0 idle, 1 live depth switch in flight
    private int _desiredBits;
    private int _calibrateBits;              // 0 = canvas-driven; 8 or 14 = Settings tab lock
    private List<SeamColumn> _seam8 = [];
    private List<SeamColumn> _seam14 = [];
    private volatile int _previewGrey = -1;  // -1 = off; 0..255 = solid grey on the wall (curve matching)

    public int Width { get; private set; }
    public int Height { get; private set; }

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
        _seamFile = AppPaths.ResolveWritableConfigFile(sf, "seam_correction.json");

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
            WaitSenderIdleUnlocked();
            _color = ColorFrom(_options, c);
            _color.SwapRedBlue = _options.SwapRedBlue; // preserve the live swap
            foreach (var s in _streamers) s.UpdateColor(_color);
        }
    }

    public void Initialize()
    {
        var wall = NetworkWall.Measure(_options.StreamTargets());
        Width = wall.Width;
        Height = wall.Height;

        lock (_lock)
        {
            if (_streamers.Count != 0) return;
            LoadSeamUnlocked();
            OpenStreamersUnlocked();
            _seamStamp = SafeStamp();
            StartSenderUnlocked();
        }

        Console.WriteLine($"[NET] UDP -> {DescribeTargets()}, {Width}x{Height} {_options.ColorBits}-bit, " +
                          $"paced to {_options.TargetMbps:0.#} Mbit/s each (pack/send off render thread).");
    }

    private string DescribeTargets()
    {
        var targets = _options.StreamTargets();
        return targets.Count == 0
            ? "(no panel)"
            : string.Join(", ", targets.Select(p => p.Host + ":" + p.Port));
    }

    private void OpenStreamersUnlocked()
    {
        CloseStreamersUnlocked();
        var wall = NetworkWall.Measure(_options.StreamTargets());
        Width = wall.Width;
        Height = wall.Height;
        foreach (var target in _options.StreamTargets())
        {
            try
            {
                _streamers.Add(NewStreamer(target));
                _openTargets.Add(target);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NET] skip {target.Host}:{target.Port}: {ex.Message}");
            }
        }
    }

    private void CloseStreamersUnlocked()
    {
        foreach (var s in _streamers) s.Dispose();
        _streamers.Clear();
        _openTargets.Clear();
    }

    private PanelStreamer NewStreamer(NetworkPanelTarget target) => new(new PanelStreamOptions
    {
        Host = target.Host,
        Port = target.Port,
        TargetMbps = _options.TargetMbps,
        ColorBits = _options.ColorBits,
        // Each stream is one physical panel. The virtual wall is cropped before this call.
        WallWidth = NetworkWall.PanelWidth,
        WallHeight = NetworkWall.PanelHeight,
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
    /// <summary>
    ///     Replace the clone list. Every enabled host gets the same frame. The first target is mirrored
    ///     into Host / Port / PanelId.
    /// </summary>
    public void Reconfigure(IReadOnlyList<NetworkPanelTarget> panels, double targetMbps, int colorBits)
    {
        lock (_lock)
        {
            var placed = panels.Select(p => new NetworkPanelTarget
            {
                Host = p.Host?.Trim() ?? "",
                Port = p.Port,
                PanelId = p.PanelId?.Trim() ?? "",
                Enabled = p.Enabled,
                X = p.X,
                Y = p.Y
            }).ToList();
            NetworkWall.Normalize(placed);
            _options.Panels = placed;
            _options.SyncPrimary();
            var wall = NetworkWall.Measure(_options.StreamTargets());
            _options.WallWidth = wall.Width;
            _options.WallHeight = wall.Height;
            _options.TargetMbps = targetMbps is > 0 and <= 1000 ? targetMbps : _options.TargetMbps;
            _options.ColorBits = colorBits is 8 or 10 or 13 or 14 ? colorBits : _options.ColorBits;

            if (_streamers.Count != 0)
            {
                WaitSenderIdleUnlocked();
                OpenStreamersUnlocked();
                _seamStamp = SafeStamp();
            }
        }
        Console.WriteLine($"[NET] reconfigured -> {DescribeTargets()}, {_options.ColorBits}-bit, " +
                          $"paced to {_options.TargetMbps:0.#} Mbit/s each.");
    }

    public void Reconfigure(string host, int port, double targetMbps, int colorBits)
    {
        var panels = _options.StreamTargets();
        if (panels.Count == 0)
            panels.Add(new NetworkPanelTarget { Host = host, Port = port, Enabled = true });
        else
        {
            panels[0].Host = string.IsNullOrWhiteSpace(host) ? panels[0].Host : host.Trim();
            panels[0].Port = port is > 0 and < 65536 ? port : panels[0].Port;
        }
        Reconfigure(panels, targetMbps, colorBits);
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
        if (Volatile.Read(ref _switching) != 0) return; // don't send across an 8/14 live switch

        var src = bitmap.GetPixels();
        if (src == IntPtr.Zero) return;
        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width <= 0 || height <= 0) return;
        var rowBytes = bitmap.RowBytes;
        var needed = rowBytes * height;

        lock (_lock)
        {
            if (_streamers.Count == 0) return;
            // The bitmap is the compositor output. Follow it so a wall resize cannot drop every frame.
            Width = width;
            Height = height;
            if ((_frameCounter++ & 31) == 0) _checkSeam = 1;

            if (_srcBuf.Length != needed) _srcBuf = new byte[needed];
            var preview = _previewGrey;
            if (preview >= 0)
                FillGrey(_srcBuf, width, height, rowBytes, (byte)preview);
            else
                Marshal.Copy(src, _srcBuf, 0, needed);

            _hasPending = true;
            _pendingStride = rowBytes;
        }

        _frameReady.Set();
    }

    private void StartSenderUnlocked()
    {
        if (_sender != null) return;
        _stopSender = false;
        _sender = new Thread(SenderLoop)
        {
            IsBackground = true,
            Name = "pixplane-send"
        };
        _sender.Start();
    }

    private void SenderLoop()
    {
        while (!_stopSender)
        {
            try
            {
                _frameReady.Wait();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (_stopSender) return;

            PanelStreamer[] batch = [];
            NetworkPanelTarget[] targets = [];
            byte[]? work = null;
            var stride = 0;
            var frameW = 0;
            var frameH = 0;

            lock (_lock)
            {
                if (_checkSeam != 0)
                {
                    _checkSeam = 0;
                    MaybeReloadSeam();
                }

                if (_hasPending && _streamers.Count != 0)
                {
                    if (_workBuf.Length != _srcBuf.Length) _workBuf = new byte[_srcBuf.Length];
                    Buffer.BlockCopy(_srcBuf, 0, _workBuf, 0, _srcBuf.Length);
                    work = _workBuf;
                    stride = _pendingStride;
                    frameW = Width;
                    frameH = Height;
                    batch = _streamers.ToArray();
                    targets = _openTargets.ToArray();
                    EnsureCrops(batch.Length);
                    _hasPending = false;
                    _sending = true;
                }
                else if (_streamers.Count == 0)
                {
                    _hasPending = false; // drop queued frames across livemode / dispose
                }

                if (!_hasPending)
                    _frameReady.Reset();
            }

            if (batch.Length != 0 && work != null)
            {
                try
                {
                    var crops = _crops;
                    if (targets.Length == batch.Length && crops.Length >= batch.Length)
                    {
                        if (batch.Length == 1)
                        {
                            try { SendCrop(batch[0], targets[0], work, stride, frameW, frameH, crops[0]); }
                            catch (ObjectDisposedException) { }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[NET] send to panel 1 failed: {ex.Message}");
                            }
                        }
                        else
                            Parallel.For(0, batch.Length, i =>
                            {
                                try { SendCrop(batch[i], targets[i], work, stride, frameW, frameH, crops[i]); }
                                catch (ObjectDisposedException) { }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[NET] send to panel {i + 1} failed: {ex.Message}");
                                }
                            });
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Reconfigure/dispose raced a send that already held the instance.
                }
                finally
                {
                    lock (_lock)
                    {
                        _sending = false;
                        Monitor.PulseAll(_lock);
                    }
                }
            }
        }
    }

    private void EnsureCrops(int count)
    {
        if (_crops.Length == count) return;
        _crops = new byte[count][];
        var bytes = NetworkWall.PanelWidth * NetworkWall.PanelHeight * 4;
        for (var i = 0; i < count; i++)
            _crops[i] = new byte[bytes];
    }

    private static void SendCrop(PanelStreamer streamer, NetworkPanelTarget target, byte[] frame,
        int stride, int frameW, int frameH, byte[] crop)
    {
        CropBgra(frame, stride, frameW, frameH, target.X, target.Y, crop);
        streamer.SendFrameBgra(crop, NetworkWall.PanelWidth * 4);
    }

    private static void CropBgra(byte[] src, int srcStride, int srcW, int srcH, int originX, int originY, byte[] dst)
    {
        var pw = NetworkWall.PanelWidth;
        var ph = NetworkWall.PanelHeight;
        Array.Clear(dst, 0, dst.Length);
        for (var row = 0; row < ph; row++)
        {
            var sy = originY + row;
            if (sy < 0 || sy >= srcH) continue;
            var sx0 = Math.Max(0, originX);
            var sx1 = Math.Min(srcW, originX + pw);
            if (sx1 <= sx0) continue;
            var bytes = (sx1 - sx0) * 4;
            var dstOff = row * pw * 4 + (sx0 - originX) * 4;
            var srcOff = sy * srcStride + sx0 * 4;
            if (dstOff < 0 || srcOff < 0 || dstOff + bytes > dst.Length || srcOff + bytes > src.Length)
                continue;
            Buffer.BlockCopy(src, srcOff, dst, dstOff, bytes);
        }
    }

    private void WaitSenderIdleUnlocked()
    {
        while (_sending) Monitor.Wait(_lock);
    }

    private static void FillGrey(byte[] buf, int width, int height, int rowBytes, byte grey)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var o = row + x * 4;
                buf[o] = grey;
                buf[o + 1] = grey;
                buf[o + 2] = grey;
                buf[o + 3] = 255;
            }
        }
    }

    private async Task SwitchLiveAsync(int bits)
    {
        var previous = _options.ColorBits;
        try
        {
            List<string> hosts;
            lock (_lock)
            {
                WaitSenderIdleUnlocked();
                hosts = _options.StreamTargets().Select(p => p.Host).ToList();
                CloseStreamersUnlocked();
            }

            Console.WriteLine($"[NET] live colour depth {previous}-bit -> {bits}-bit on {hosts.Count} panel(s)...");
            var switched = new List<string>();
            var failed = new List<string>();
            foreach (var host in hosts)
            {
                try
                {
                    await PanelControl.SetColorModeLiveAsync(host, bits).ConfigureAwait(false);
                    switched.Add(host);
                }
                catch (Exception ex)
                {
                    failed.Add(host);
                    Console.WriteLine($"[NET] livemode on {host} failed: {ex.Message}");
                }
            }

            if (failed.Count > 0)
            {
                foreach (var host in switched)
                {
                    try { await PanelControl.SetColorModeLiveAsync(host, previous).ConfigureAwait(false); }
                    catch (Exception ex) { Console.WriteLine($"[NET] could not revert {host} to {previous}-bit: {ex.Message}"); }
                }
                throw new InvalidOperationException(
                    "livemode failed on " + string.Join(", ", failed));
            }

            lock (_lock)
            {
                WaitSenderIdleUnlocked();
                _options.ColorBits = bits;
                OpenStreamersUnlocked();
                _seamStamp = SafeStamp();
            }
            Console.WriteLine($"[NET] live colour depth now {bits}-bit");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NET] live colour switch failed: {ex.Message}");
            lock (_lock)
            {
                if (_streamers.Count == 0)
                {
                    WaitSenderIdleUnlocked();
                    _options.ColorBits = previous;
                    OpenStreamersUnlocked();
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
            WaitSenderIdleUnlocked();
            _color.SwapRedBlue = swapRedBlue;
            _options.SwapRedBlue = swapRedBlue;
            foreach (var s in _streamers) s.UpdateColor(_color);
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
            WaitSenderIdleUnlocked();
            EnsureSeamLoaded();
            var b = bits ?? (_calibrateBits is 8 or 14 ? _calibrateBits : _options.ColorBits);
            if (SeamCorrectionStore.NormalizeBits(b) == SeamCorrectionStore.Bits14)
                _seam14 = columns.ToList();
            else
                _seam8 = columns.ToList();
            if (SeamCorrectionStore.NormalizeBits(_options.ColorBits) == SeamCorrectionStore.NormalizeBits(b))
                foreach (var s in _streamers) s.UpdateSeam(columns);
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
        foreach (var s in _streamers) s.UpdateSeam(ActiveSeam());
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
        _stopSender = true;
        try { _frameReady.Set(); } catch (ObjectDisposedException) { }
        _sender?.Join(3000);
        _sender = null;
        lock (_lock)
        {
            CloseStreamersUnlocked();
        }
        _frameReady.Dispose();
    }
}

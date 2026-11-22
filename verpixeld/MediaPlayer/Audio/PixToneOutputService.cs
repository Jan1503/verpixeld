using System.Diagnostics;
using System.Text.Json.Nodes;
using SkiaSharp;
using verpixeld.Configuration;
using PixTone;

namespace verpixeld.MediaPlayer.Audio;

/// <summary>
///     The selected verpixeld audio board. FFmpeg stays here; packets go out through <see cref="PixToneSender"/>.
/// </summary>
public sealed class PixToneOutputService
{
    private readonly object _gate = new();
    private TaskCompletionSource _audioStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PixToneSender? _liveSender;
    private List<AudioAnnouncement> _seen = [];

    public bool WallPreview { get; private set; }
    public int PreviewFps { get; private set; } = 20;
    public int PreviewWidth { get; private set; } = 480;
    public bool PreviewFill { get; private set; }

    private readonly object _snapGate = new();
    private readonly byte[] _encode = new byte[PixToneProtocol.PreviewWidthMax * PixToneProtocol.PreviewHeightMax * 2];
    private readonly byte[] _snap = new byte[PixToneProtocol.PreviewWidthMax * PixToneProtocol.PreviewHeightMax * 2];
    private long _lastNote;
    private int _snapSerial;
    private int _sentSerial;
    private int _snapY;
    private int _snapRows;
    private int _snapW;
    private int _snapH;

    public PixToneOutputService()
    {
        Load();
        _ = Task.Run(FeedBoard);
        _ = Task.Run(WatchBoardVolume);
    }

    /// <summary>Raised when the board's own volume or mute (panel, board web page) changed.</summary>
    public event Action? VolumeChanged;

    /// <summary>What the host is playing, for the panel's transport row (<c>VPLY</c>). Set by the media player.</summary>
    public Func<PanelPlayInfo>? PlayStateSource { get; set; }

    /// <summary>
    ///     A button on the board's panel (<c>VKEY</c>): key, argument, list generation.
    ///     Repeats of one press are already dropped.
    /// </summary>
    public event Action<PanelKey, ushort, byte>? PanelKeyPressed;

    /// <summary>More panel datagrams (<c>VSET</c>, <c>VLST</c>), asked for about once a second.</summary>
    public Func<IEnumerable<byte[]>>? PanelPackets { get; set; }

    private volatile bool _panelKick;

    /// <summary>True while the wall picture should not go to the board (music, radio).</summary>
    public Func<bool>? SuppressPreview { get; set; }

    /// <summary>The board answered its /status poll within the last few seconds.</summary>
    public bool BoardOnline => IsSelected && DateTime.UtcNow - _lastSeen < TimeSpan.FromSeconds(5);
    private DateTime _lastSeen = DateTime.MinValue;

    // What the board should be showing. A board that restarts has forgotten both, so they go
    // out again when its uptime drops or it comes back after being unreachable.
    private string? _shownTitle;
    private byte[]? _shownCover;
    private int _lastUptime = -1;
    private DateTime _nextTitle = DateTime.MinValue;

    private float[]? _bands;
    private DateTime _bandsAt = DateTime.MinValue;

    /// <summary>The board's spectrum (0–1, low to high), when it came within the last half second.</summary>
    public float[]? LatestBands
    {
        get
        {
            var b = _bands;
            return b != null && DateTime.UtcNow - _bandsAt < TimeSpan.FromMilliseconds(500) ? b : null;
        }
    }

    /// <summary>Send the play state and the panel datagrams on the next pass, not in a second.</summary>
    public void KickPanel() => _panelKick = true;

    public readonly record struct PanelPlayInfo(PlayState State, PlayFlags Flags, TimeSpan Position, TimeSpan Duration);

    private DateTime _localVolumeAt = DateTime.MinValue;

    public bool IsSelected { get; private set; }
    public string Host { get; private set; } = "";
    public int Volume { get; private set; } = 70;
    public bool Muted { get; private set; }

    public IReadOnlyList<AudioAnnouncement> Seen
    {
        get { lock (_gate) return _seen.ToList(); }
    }

    public void Select(string host)
    {
        host = host.Trim();
        if (!System.Net.IPAddress.TryParse(host, out _))
            throw new ArgumentException("IPv4 address required");
        Host = host;
        IsSelected = true;
        Save();
    }

    public void Deselect()
    {
        if (!IsSelected) return;
        IsSelected = false;
        Save();
    }

    public void SetVolume(int volumePercent, bool? mute = null)
    {
        Volume = Math.Clamp(volumePercent, 0, 100);
        if (mute.HasValue) Muted = mute.Value;
        _localVolumeAt = DateTime.UtcNow;
        Save();
        PushVolume();
    }

    public void ToggleMute()
    {
        Muted = !Muted;
        _localVolumeAt = DateTime.UtcNow;
        Save();
        PushVolume();
    }

    public async Task<IReadOnlyList<AudioAnnouncement>> ScanAsync(CancellationToken ct = default)
    {
        var found = await AudioDiscovery.ScanAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        lock (_gate) _seen = found.ToList();
        if (!string.IsNullOrWhiteSpace(Host))
        {
            var known = await AudioDiscovery.QueryStatusAsync(Host, ct: ct).ConfigureAwait(false);
            if (known != null)
            {
                lock (_gate)
                {
                    _seen.RemoveAll(b => b.Host == known.Host);
                    _seen.Insert(0, known);
                }
            }
        }
        return Seen;
    }

    /// <summary>
    ///     Starts FFmpeg writing 48 kHz stereo s16le to stdout and pumps it to the board.
    ///     The returned process is the caller's to kill; closing it ends the pump.
    /// </summary>
    public Process? StartDecoder(string argsBeforePcm, string? title, CancellationToken ct)
    {
        if (!IsSelected || string.IsNullOrWhiteSpace(Host)) return null;
        var args = argsBeforePcm.Trim() + " -c:a pcm_s16le -ar 48000 -ac 2 -f s16le pipe:1";
        var psi = new ProcessStartInfo("ffmpeg", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        _audioStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = _audioStarted;
        var proc = Process.Start(psi);
        if (proc == null) return null;

        var host = Host;
        var stdout = proc.StandardOutput.BaseStream;
        var stderr = proc.StandardError;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await stderr.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Failed", StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine("[PIXTONE] " + line);
                }
            }
            catch { /* process gone */ }
        }, ct);
        _ = Task.Run(async () =>
        {
        var sender = new PixToneSender(host);
        _liveSender = sender;
        IsStreaming = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(title)) sender.SetTitle(title);
            await sender.PumpAsync(stdout, ct, () => started.TrySetResult()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine("[PIXTONE] " + ex.Message); }
        finally
        {
            IsStreaming = false;
            if (ReferenceEquals(_liveSender, sender)) _liveSender = null;
            sender.Dispose();
        }
        }, ct);
        Console.WriteLine($"[PIXTONE] streaming to {host}:{PixToneProtocol.Port}");
        return proc;
    }

    /// <summary>True while a decoder is open, including while it is paused.</summary>
    public bool IsStreaming { get; private set; }

    /// <summary>True while a movie is actually feeding the board. A paused movie does not count.</summary>
    public bool BlocksExtensions => IsStreaming && _liveSender?.IsPaused != true;

    public void PauseClock() => _liveSender?.PauseClock();

    public void ResumeClock() => _liveSender?.ResumeClock();

    /// <summary>Completes when the first PCM packet has been handed to the board.</summary>
    public async Task WaitForAudioStartAsync(TimeSpan timeout, CancellationToken ct)
    {
        var started = _audioStarted.Task;
        await Task.WhenAny(started, Task.Delay(timeout, ct)).ConfigureAwait(false);
    }

    /// <summary>
    ///     The panel sets volume on its own. Read it back once a second so verpixeld shows what the
    ///     board plays. Skipped for 2 s after a change from here, so a VVOL in flight is not undone.
    /// </summary>
    private async Task WatchBoardVolume()
    {
        while (true)
        {
            await Task.Delay(1000).ConfigureAwait(false);
            try
            {
                var host = Host;
                if (!IsSelected || string.IsNullOrWhiteSpace(host)) continue;
                var board = await AudioDiscovery.QueryStatusAsync(host).ConfigureAwait(false);
                if (board == null || host != Host) continue;
                var wasAway = _lastSeen != DateTime.MinValue && DateTime.UtcNow - _lastSeen > TimeSpan.FromSeconds(5);
                var restarted = _lastUptime >= 0 && board.Uptime + 3 < _lastUptime;
                _lastUptime = board.Uptime;
                _lastSeen = DateTime.UtcNow;
                if (restarted || wasAway)
                {
                    Console.WriteLine(restarted
                        ? "[PIXTONE] Board restarted: sending title and cover again"
                        : "[PIXTONE] Board is back: sending title and cover again");
                    _ = Task.Run(() => ResendShownAsync(host));
                }
                else if (DateTime.UtcNow >= _nextTitle && _shownTitle != null)
                {
                    // A lost VTIT costs nothing to repeat; the board ignores an unchanged title.
                    _nextTitle = DateTime.UtcNow.AddSeconds(15);
                    SendTitle(host, _shownTitle);
                }
                if (DateTime.UtcNow - _localVolumeAt < TimeSpan.FromSeconds(2)) continue;
                var vol = Math.Clamp(board.Volume, 0, 100);
                if (vol == Volume && board.Muted == Muted) continue;
                Volume = vol;
                Muted = board.Muted;
                Save();
                Console.WriteLine($"[PIXTONE] board volume {vol}{(Muted ? " muted" : "")}");
                VolumeChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[PIXTONE] volume read: " + ex.Message);
            }
        }
    }

    private void PushVolume()
    {
        if (string.IsNullOrWhiteSpace(Host)) return;
        try
        {
            using var sender = new PixToneSender(Host);
            sender.SetVolume(Volume, Muted);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[PIXTONE] volume: " + ex.Message);
        }
    }

    private void Load()
    {
        var o = AppSettingsStore.Get<PixToneOptions>("PixTone");
        if (string.IsNullOrWhiteSpace(o.Host))
            o = AppSettingsStore.Get<PixToneOptions>("Vap1");
        Host = o.Host ?? "";
        IsSelected = o.Selected && !string.IsNullOrWhiteSpace(Host);
        Volume = Math.Clamp(o.Volume, 0, 100);
        Muted = o.Muted;
        WallPreview = o.WallPreview;
        var fps = o.PreviewFps;
        if (fps is 0 or 8 or 10) fps = 20;
        PreviewFps = Math.Clamp(fps, 1, 30);
        PreviewWidth = NormalizeWidth(o.PreviewWidth == 0 ? 480 : o.PreviewWidth);
        PreviewFill = o.PreviewFill;
    }

    private void Save()
    {
        var root = AppSettingsStore.Load();
        var section = AppSettingsStore.Section(root, "PixTone");
        AppSettingsStore.Set(section, "Host", JsonValue.Create(Host));
        AppSettingsStore.Set(section, "Selected", JsonValue.Create(IsSelected));
        AppSettingsStore.Set(section, "Volume", JsonValue.Create(Volume));
        AppSettingsStore.Set(section, "Muted", JsonValue.Create(Muted));
        AppSettingsStore.Set(section, "WallPreview", JsonValue.Create(WallPreview));
        AppSettingsStore.Set(section, "PreviewFps", JsonValue.Create(PreviewFps));
        AppSettingsStore.Set(section, "PreviewWidth", JsonValue.Create(PreviewWidth));
        AppSettingsStore.Set(section, "PreviewFill", JsonValue.Create(PreviewFill));
        AppSettingsStore.Save(root);
    }

    public void SetWallPreview(bool on)
    {
        WallPreview = on;
        Save();
        Console.WriteLine("[PIXTONE] wall preview " + (on ? "on" : "off"));
    }

    public void SetPreviewFps(int fps)
    {
        PreviewFps = Math.Clamp(fps, 1, 30);
        Save();
        Console.WriteLine("[PIXTONE] preview fps " + PreviewFps);
    }

    public void SetPreviewWidth(int width)
    {
        PreviewWidth = NormalizeWidth(width);
        Save();
        Console.WriteLine("[PIXTONE] preview width " + PreviewWidth);
    }

    public void SetPreviewFill(bool on)
    {
        PreviewFill = on;
        Save();
        PushFill();
        Console.WriteLine("[PIXTONE] preview fill " + (on ? "on" : "off"));
    }

    private void PushFill()
    {
        if (string.IsNullOrWhiteSpace(Host) || !IsSelected) return;
        try
        {
            using var sender = new PixToneSender(Host);
            sender.SendControl(PixToneProtocol.FillPacket(PreviewFill));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[PIXTONE] fill: " + ex.Message);
        }
    }

    private static int NormalizeWidth(int width)
    {
        if (width <= 160) return 160;
        if (width <= 240) return 240;
        return 480;
    }

    public void SetTrackTitle(string? title)
    {
        _shownTitle = title;
        _nextTitle = DateTime.UtcNow.AddSeconds(15);
        if (string.IsNullOrWhiteSpace(Host) || !IsSelected) return;
        SendTitle(Host, title);
    }

    private static void SendTitle(string host, string? title)
    {
        try
        {
            using var sender = new PixToneSender(host);
            sender.SetTitle(title);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[PIXTONE] title: " + ex.Message);
        }
    }

    /// <summary>After a board restart: title, cover, volume and the panel data once it is ready.</summary>
    private async Task ResendShownAsync(string host)
    {
        try
        {
            // The board answers /status before its audio socket has settled; give it a moment.
            await Task.Delay(1500).ConfigureAwait(false);
            if (host != Host || !IsSelected) return;
            if (_shownTitle != null) SendTitle(host, _shownTitle);
            var cover = _shownCover;
            if (cover != null) SendCover(host, cover);
            KickPanel();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[PIXTONE] resend: " + ex.Message);
        }
    }

    /// <summary>
    ///     Called after the panel renderer has already taken the frame. Returns immediately when the
    ///     preview is off. Sampling follows the selected rate and does not touch the panel sender.
    /// </summary>
    public void NoteFrame(SKBitmap bitmap)
    {
        if (!WallPreview || !IsSelected) return;
        var now = Environment.TickCount64;
        if (now - _lastNote < 1000 / Math.Clamp(PreviewFps, 1, 30)) return;
        var src = bitmap.GetPixels();
        if (src == IntPtr.Zero || bitmap.Width < 2 || bitmap.Height < 2) return;
        _lastNote = now;
        Downscale(bitmap, src);
    }

    private void Downscale(SKBitmap bitmap, IntPtr src)
    {
        var dw = NormalizeWidth(PreviewWidth);
        var dh = PixToneProtocol.PreviewHeightMax;
        var sw = bitmap.Width;
        var sh = bitmap.Height;
        var stride = bitmap.RowBytes;
        var scale = Math.Min(dw / (float)sw, dh / (float)sh);
        var tw = Math.Max(1, (int)(sw * scale));
        var th = Math.Max(1, (int)(sh * scale));
        if (tw > dw) tw = dw;
        if (th > dh) th = dh;
        // Above 160 the frame is as wide as the picture: a 2:1 wall at 480 is 320×160, and
        // black side bars would be a third of every frame on the wire and on the board's SPI.
        // The 3.5" board centres a narrower frame. The 2.8" board expects exactly 160.
        if (dw > PixToneProtocol.PreviewWidth) dw = Math.Max(2, tw);
        var ox = (dw - tw) / 2;
        var oy = (dh - th) / 2;
        var local = _encode;
        var frameBytes = dw * dh * 2;
        Array.Clear(local, 0, frameBytes);
        for (var y = 0; y < th; y++)
        {
            var sy = y * sh / th;
            for (var x = 0; x < tw; x++)
            {
                var sx = x * sw / tw;
                var o = sy * stride + sx * 4;
                var px = System.Runtime.InteropServices.Marshal.ReadInt32(src, o);
                var b = (byte)px;
                var g = (byte)(px >> 8);
                var r = (byte)(px >> 16);
                var p = (ushort)((r >> 3 << 11) | (g >> 2 << 5) | (b >> 3));
                var i = ((oy + y) * dw + (ox + x)) * 2;
                local[i] = (byte)(p >> 8);
                local[i + 1] = (byte)p;
            }
        }
        lock (_snapGate)
        {
            Buffer.BlockCopy(local, 0, _snap, 0, _snap.Length);
            _snapY = oy;
            _snapRows = th;
            _snapW = dw;
            _snapH = dh;
            _snapSerial++;
        }
    }

    private async Task FeedBoard()
    {
        var chunk = new byte[PixToneProtocol.PictureChunkBytesFast];
        PixToneSender? sender = null;
        PixToneSender? picSender = null;
        var senderHost = "";
        var picPort = PixToneProtocol.Port;
        var nextClock = DateTime.UtcNow;
        var nextStatus = DateTime.MinValue;
        PixToneSender? listening = null;
        var nextPlay = DateTime.MinValue;
        var lastPlay = default(PanelPlayInfo);
        while (true)
        {
            try
            {
                if (!IsSelected || string.IsNullOrWhiteSpace(Host))
                {
                    sender?.Dispose();
                    sender = null;
                    picSender?.Dispose();
                    picSender = null;
                    await Task.Delay(250).ConfigureAwait(false);
                    continue;
                }
                if (sender == null || senderHost != Host)
                {
                    sender?.Dispose();
                    sender = new PixToneSender(Host);
                    senderHost = Host;
                    nextStatus = DateTime.MinValue;
                }
                // Newer boards take the preview on a port of its own ("pic" in /status), so a
                // burst of picture chunks cannot crowd PCM out of the board's receive buffer.
                if (DateTime.UtcNow >= nextStatus)
                {
                    var board = await AudioDiscovery.QueryStatusAsync(senderHost).ConfigureAwait(false);
                    var port = board is { PicturePort: > 0 } ? board.PicturePort : PixToneProtocol.Port;
                    nextStatus = DateTime.UtcNow.AddSeconds(board != null ? 30 : 10);
                    if (port != picPort || picSender == null)
                    {
                        picSender?.Dispose();
                        picSender = new PixToneSender(senderHost, port);
                        if (port != picPort) Console.WriteLine($"[PIXTONE] preview on UDP {port}");
                        picPort = port;
                    }
                }
                var now = DateTime.UtcNow;
                if (now >= nextClock)
                {
                    var local = BoardLocalNow();
                    sender.SendControl(PixToneProtocol.ClockPacket(local.Hour, local.Minute, local.Second));
                    sender.SendControl(PixToneProtocol.FillPacket(PreviewFill));
                    nextClock = now.AddSeconds(2);
                }
                // Play state for the panel: once a second, and at once when it changes. The board
                // answers panel keys to this sender's port, so listen on it after its first send.
                var play = ReadPlayState();
                if (now >= nextPlay || _panelKick || play.State != lastPlay.State || play.Flags != lastPlay.Flags ||
                    (play.Position - lastPlay.Position).Duration() > TimeSpan.FromSeconds(3))
                {
                    _panelKick = false;
                    sender.SendControl(PixToneProtocol.PlayPacket(play.State, play.Flags,
                        (uint)Math.Max(0, play.Position.TotalMilliseconds),
                        (uint)Math.Max(0, play.Duration.TotalMilliseconds)));
                    lastPlay = play;
                    nextPlay = now.AddSeconds(1);
                    try
                    {
                        var extra = PanelPackets?.Invoke();
                        if (extra != null)
                            foreach (var datagram in extra)
                                sender.SendControl(datagram);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[PIXTONE] panel data: " + ex.Message);
                    }
                }
                if (!ReferenceEquals(listening, sender))
                {
                    listening = sender;
                    _ = Task.Run(() => ListenPanel(sender));
                }
                int serial, originY, rows, width, frameH;
                byte[]? picture = null;
                lock (_snapGate)
                {
                    serial = _snapSerial;
                    var suppress = false;
                    try { suppress = SuppressPreview?.Invoke() == true; } catch { /* keep sending */ }
                    if (WallPreview && !suppress && serial != _sentSerial && serial != 0)
                    {
                        picture = (byte[])_snap.Clone();
                        originY = _snapY;
                        rows = _snapRows;
                        width = _snapW;
                        frameH = _snapH;
                        _sentSerial = serial;
                    }
                    else
                    {
                        originY = 0;
                        rows = 0;
                        width = 0;
                        frameH = 0;
                    }
                }
                if (picture == null)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    continue;
                }
                if (width < 2) width = PixToneProtocol.PreviewWidth;
                if (rows < 1) rows = 1;
                var rowBytes = width * 2;
                var packed = rows * rowBytes;
                var chunkSize = width <= 160 ? PixToneProtocol.PictureChunkBytes : PixToneProtocol.PictureChunkBytesFast;
                var frame = (byte)serial;
                var chunks = (packed + chunkSize - 1) / chunkSize;
                if (chunks > 160) chunks = 160;
                var count = (byte)chunks;
                var gap = Math.Max(1, 1000 / Math.Clamp(PreviewFps, 1, 30) / Math.Max(1, (int)count));
                for (byte i = 0; i < count; i++)
                {
                    var off = i * chunkSize;
                    var n = Math.Min(chunkSize, packed - off);
                    picture.AsSpan(originY * rowBytes + off, n).CopyTo(chunk);
                    picSender!.SendControl(PixToneProtocol.PictureChunk(
                        frame, i, count, (byte)originY, (byte)rows, (ushort)width, (ushort)frameH,
                        (ushort)chunkSize, chunk.AsSpan(0, n)));
                    if (chunkSize <= PixToneProtocol.PictureChunkBytes)
                    {
                        if (gap > 0) await Task.Delay(gap).ConfigureAwait(false);
                    }
                    else if ((i & 3) == 3)
                        await Task.Delay(1).ConfigureAwait(false);
                }
                await Task.Delay(5).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[PIXTONE] idle feed: " + ex.Message);
                sender?.Dispose();
                sender = null;
                picSender?.Dispose();
                picSender = null;
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }

    private PanelPlayInfo ReadPlayState()
    {
        try
        {
            return PlayStateSource?.Invoke() ?? default;
        }
        catch
        {
            return default;
        }
    }

    private async Task ListenPanel(PixToneSender sender)
    {
        byte? lastSeq = null;
        var lastAt = DateTime.MinValue;
        while (true)
        {
            byte[] d;
            try
            {
                d = await sender.ReceiveAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Windows reports an ICMP "port unreachable" here. Keep listening.
                await Task.Delay(100).ConfigureAwait(false);
                continue;
            }
            catch
            {
                return;
            }
            if (PixToneProtocol.TryParseSpectrum(d, out var levels))
            {
                _bands = levels;
                _bandsAt = DateTime.UtcNow;
                continue;
            }
            if (!PixToneProtocol.TryParseKey(d, out var key, out var seq, out var arg, out var gen)) continue;
            var now = DateTime.UtcNow;
            if (seq == lastSeq && now - lastAt < TimeSpan.FromSeconds(3)) continue;
            lastSeq = seq;
            lastAt = now;
            Console.WriteLine($"[PIXTONE] panel {key}{(key == PanelKey.Seek ? $" {arg / 10.0:0.#}%" : "")}");
            try
            {
                PanelKeyPressed?.Invoke(key, arg, gen);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[PIXTONE] panel key: " + ex.Message);
            }
        }
    }

    private int _coverGen;
    private static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>
    ///     Shows an 80×80 cover on the audio board. An http(s) address is downloaded as an image.
    ///     Anything else is a media file, and ffmpeg grabs one frame. Null clears the slot.
    /// </summary>
    public void ShowCover(string? source)
    {
        var gen = Interlocked.Increment(ref _coverGen);
        if (!IsSelected || string.IsNullOrWhiteSpace(Host)) return;
        if (string.IsNullOrWhiteSpace(source))
        {
            _shownCover = null;
            SendCoverClear();
            return;
        }
        var host = Host;
        _ = Task.Run(async () =>
        {
            try
            {
                var encoded = await LoadCoverBytes(source).ConfigureAwait(false);
                if (gen != _coverGen || encoded == null) return;
                var rgb = ToCoverRgb565(encoded);
                if (gen != _coverGen || rgb == null) return;
                _shownCover = rgb;
                SendCover(host, rgb);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[PIXTONE] cover: " + ex.Message);
            }
        });
    }

    private static async Task<byte[]?> LoadCoverBytes(string source)
    {
        if (source.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',');
            if (comma < 0) return null;
            return Convert.FromBase64String(source[(comma + 1)..]);
        }
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (LooksLikePicture(source))
                return await CoverHttp.GetByteArrayAsync(source).ConfigureAwait(false);
        }
        var thumb = await MediaProbeService.ExtractThumbnailAsync(source, 160, 160).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(thumb)) return null;
        var comma2 = thumb.IndexOf(',');
        if (comma2 < 0) return null;
        return Convert.FromBase64String(thumb[(comma2 + 1)..]);
    }

    private static bool LooksLikePicture(string url)
    {
        var path = url.Split('?', 2)[0];
        return path.Contains("ytimg.com", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[]? ToCoverRgb565(byte[] encoded)
    {
        using var bmp = SKBitmap.Decode(encoded);
        if (bmp == null || bmp.Width < 2 || bmp.Height < 2) return null;
        var side = Math.Min(bmp.Width, bmp.Height);
        var cropX = (bmp.Width - side) / 2;
        var cropY = (bmp.Height - side) / 2;
        var info = new SKImageInfo(PixToneProtocol.CoverSize, PixToneProtocol.CoverSize, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var scaled = new SKBitmap(info);
        using (var canvas = new SKCanvas(scaled))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(bmp,
                new SKRect(cropX, cropY, cropX + side, cropY + side),
                new SKRect(0, 0, info.Width, info.Height));
        }
        var rgb = new byte[PixToneProtocol.CoverSize * PixToneProtocol.CoverSize * 2];
        var n = 0;
        for (var y = 0; y < info.Height; y++)
        {
            for (var x = 0; x < info.Width; x++)
            {
                var c = scaled.GetPixel(x, y);
                var p = (ushort)((c.Red >> 3 << 11) | (c.Green >> 2 << 5) | (c.Blue >> 3));
                rgb[n++] = (byte)(p >> 8);
                rgb[n++] = (byte)p;
            }
        }
        return rgb;
    }

    private void SendCoverClear()
    {
        if (string.IsNullOrWhiteSpace(Host)) return;
        try
        {
            using var sender = new PixToneSender(Host);
            sender.SendControl(PixToneProtocol.CoverClear());
        }
        catch (Exception ex)
        {
            Console.WriteLine("[PIXTONE] cover clear: " + ex.Message);
        }
    }

    private static void SendCover(string host, byte[] rgb)
    {
        using var sender = new PixToneSender(host);
        const int chunk = PixToneProtocol.PictureChunkBytes;
        var count = (byte)((rgb.Length + chunk - 1) / chunk);
        var buf = new byte[chunk];
        for (byte i = 0; i < count; i++)
        {
            var off = i * chunk;
            var n = Math.Min(chunk, rgb.Length - off);
            rgb.AsSpan(off, n).CopyTo(buf);
            sender.SendControl(PixToneProtocol.CoverChunk(i, count, buf.AsSpan(0, n)));
            Thread.Sleep(2);
        }
    }

    private sealed class PixToneOptions
    {
        public string? Host { get; set; }
        public bool Selected { get; set; }
        public int Volume { get; set; } = 70;
        public bool Muted { get; set; }
        public bool WallPreview { get; set; }
        public int PreviewFps { get; set; } = 20;
        public int PreviewWidth { get; set; }
        public bool PreviewFill { get; set; }
    }

    /// <summary>Berlin local time. The NAS image is UTC unless TZ is set, and the idle clock must follow the wall.</summary>
    private static DateTime BoardLocalNow()
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateTime.Now;
        }
    }
}

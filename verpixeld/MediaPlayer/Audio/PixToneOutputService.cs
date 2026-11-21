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
    }

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
        Save();
        PushVolume();
    }

    public void ToggleMute()
    {
        Muted = !Muted;
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

    private static int NormalizeWidth(int width)
    {
        if (width <= 160) return 160;
        if (width <= 240) return 240;
        return 480;
    }

    public void SetTrackTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(Host) || !IsSelected) return;
        try
        {
            using var sender = new PixToneSender(Host);
            sender.SetTitle(title);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[PIXTONE] title: " + ex.Message);
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
        var senderHost = "";
        var nextClock = DateTime.UtcNow;
        while (true)
        {
            try
            {
                if (!IsSelected || string.IsNullOrWhiteSpace(Host))
                {
                    sender?.Dispose();
                    sender = null;
                    await Task.Delay(250).ConfigureAwait(false);
                    continue;
                }
                if (sender == null || senderHost != Host)
                {
                    sender?.Dispose();
                    sender = new PixToneSender(Host);
                    senderHost = Host;
                }
                var now = DateTime.UtcNow;
                if (now >= nextClock)
                {
                    var local = BoardLocalNow();
                    sender.SendControl(PixToneProtocol.ClockPacket(local.Hour, local.Minute, local.Second));
                    nextClock = now.AddSeconds(2);
                }
                int serial, originY, rows, width, frameH;
                byte[]? picture = null;
                lock (_snapGate)
                {
                    serial = _snapSerial;
                    if (WallPreview && serial != _sentSerial && serial != 0)
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
                    sender.SendControl(PixToneProtocol.PictureChunk(
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
                await Task.Delay(250).ConfigureAwait(false);
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

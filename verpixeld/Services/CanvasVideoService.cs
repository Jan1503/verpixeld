using CanvasManagement;
using verpixeld.MediaPlayer;

namespace verpixeld.Services;

/// <summary>
///     Lightweight, MULTI-INSTANCE video player for Studio content steps. Unlike the singleton
///     <see cref="MediaPlayerService" /> (one playback + media bar + audio), this runs one FFmpeg pipeline
///     per canvas (video-only, looping), so several canvases can play different videos at the same time.
/// </summary>
public class CanvasVideoService : IDisposable
{
    private readonly CanvasManager _cm;
    private readonly object _lock = new();
    private readonly Dictionary<string, FfmpegFrameStreamer> _streams = new(StringComparer.OrdinalIgnoreCase);

    public CanvasVideoService(CanvasManager cm)
    {
        _cm = cm;
    }

    public bool IsPlaying(string canvasName)
    {
        lock (_lock)
        {
            return _streams.ContainsKey(canvasName);
        }
    }

    /// <summary>Plays (video-only) a file into the given canvas, replacing any video already on it.</summary>
    public bool Play(Canvas canvas, string filePath, bool loop)
    {
        if (!MediaPlayerService.FFmpegAvailable)
        {
            Console.WriteLine("[CVID] FFmpeg not available");
            return false;
        }

        if (!File.Exists(filePath))
        {
            Console.WriteLine($"[CVID] File not found: {filePath}");
            return false;
        }

        var w = canvas.Width;
        var h = canvas.Height;
        var loopArg = loop ? "-stream_loop -1 " : "";
        // -re paces the file at its native rate; -an drops audio (multiple simultaneous videos = no audio mix).
        var args = $"-hide_banner -loglevel warning -re {loopArg}-i \"{filePath}\" " +
                   $"-f rawvideo -pix_fmt {FfmpegRawVideo.PixFmt} -vf \"scale={w}:{h}:flags=area\" -fps_mode cfr -an pipe:1";

        var streamer = new FfmpegFrameStreamer(w, h);
        FfmpegFrameStreamer? old;
        lock (_lock)
        {
            _streams.TryGetValue(canvas.Name, out old);
            _streams[canvas.Name] = streamer;
        }

        // Tear down a previous stream on this canvas outside the lock (Stop can block briefly).
        if (old != null) { old.Stop("[CVID]"); old.Dispose(); }

        streamer.Start(args, canvas, displayFps: 20, logPrefix: "[CVID]");
        Console.WriteLine($"[CVID] Playing {Path.GetFileName(filePath)} on '{canvas.Name}' ({w}x{h})");
        return true;
    }

    public void Stop(string canvasName)
    {
        FfmpegFrameStreamer? s;
        lock (_lock)
        {
            if (!_streams.Remove(canvasName, out s)) return;
        }

        s.Stop("[CVID]");
        s.Dispose();
        try { _cm.GetCanvasByName(canvasName)?.Clear(); } catch { /* ignore */ }
        Console.WriteLine($"[CVID] Stopped on '{canvasName}'");
    }

    public void StopAll()
    {
        List<KeyValuePair<string, FfmpegFrameStreamer>> all;
        lock (_lock)
        {
            all = _streams.ToList();
            _streams.Clear();
        }

        foreach (var kv in all)
        {
            kv.Value.Stop("[CVID]");
            kv.Value.Dispose();
            try { _cm.GetCanvasByName(kv.Key)?.Clear(); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        StopAll();
        GC.SuppressFinalize(this);
    }
}

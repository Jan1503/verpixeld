using System.Diagnostics;
using CanvasManagement;
using verpixeld.MediaPlayer;
using verpixeld.MediaPlayer.Audio;

namespace verpixeld.Services;

/// <summary>
///     Lightweight, MULTI-INSTANCE video player for Studio content steps. Unlike the singleton
///     <see cref="MediaPlayerService" /> (one playback + media bar + audio), this runs one FFmpeg pipeline
///     per canvas (video-only, looping), so several canvases can play different videos at the same time.
/// </summary>
[LogTag("CVID")]
public class CanvasVideoService : IDisposable
{
    private readonly ILogger<CanvasVideoService> _log;

    private readonly CanvasManager _cm;
    private readonly PixToneOutputService _vap1;
    private readonly object _lock = new();
    private readonly Dictionary<string, FfmpegFrameStreamer> _streams = new(StringComparer.OrdinalIgnoreCase);
    private Process? _audio;
    private CancellationTokenSource? _audioCts;
    private string? _audioCanvas;

    public CanvasVideoService(CanvasManager cm, PixToneOutputService vap1, ILogger<CanvasVideoService> log)
    {
        _log = log;
        _cm = cm;
        _vap1 = vap1;
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
            _log.LogInformation("FFmpeg not available");
            return false;
        }

        if (!File.Exists(filePath))
        {
            _log.LogInformation("File not found: {FilePath}", filePath);
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
        StartAudio(canvas.Name, filePath, loop);
        _log.LogInformation("Playing {Value} on '{Name}' ({W}x{H})", Path.GetFileName(filePath), canvas.Name, w, h);
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
        if (string.Equals(_audioCanvas, canvasName, StringComparison.OrdinalIgnoreCase))
            StopAudio();
        try { _cm.GetCanvasByName(canvasName)?.Clear(); } catch { /* ignore */ }
        _log.LogInformation("Stopped on '{CanvasName}'", canvasName);
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
        StopAudio();
    }

    public void Dispose()
    {
        StopAll();
        GC.SuppressFinalize(this);
    }

    // One board, one stream. The canvas that just started owns the output.
    // The picture process stays -an so several videos can still draw at once.
    private void StartAudio(string canvasName, string filePath, bool loop)
    {
        StopAudio();
        if (!_vap1.IsSelected)
        {
            _log.LogInformation("No audio board selected — video only");
            return;
        }

        var loopArg = loop ? "-stream_loop -1 " : "";
        var args = $"-hide_banner -loglevel warning -re {loopArg}-i \"{filePath}\" -vn";
        _audioCts = new CancellationTokenSource();
        _audio = _vap1.StartDecoder(args, Path.GetFileName(filePath), _audioCts.Token);
        if (_audio == null)
        {
            _audioCts.Dispose();
            _audioCts = null;
            _log.LogInformation("Audio decoder did not start");
            return;
        }

        _audioCanvas = canvasName;
        _log.LogInformation("Audio {Host} for '{CanvasName}'", _vap1.Host, canvasName);
    }

    private void StopAudio()
    {
        try { _audioCts?.Cancel(); } catch { /* already done */ }
        _audioCts?.Dispose();
        _audioCts = null;
        if (_audio != null)
        {
            try { if (!_audio.HasExited) _audio.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
            _audio.Dispose();
            _audio = null;
        }
        _audioCanvas = null;
    }
}

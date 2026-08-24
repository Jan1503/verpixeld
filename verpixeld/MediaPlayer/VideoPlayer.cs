using System.Diagnostics;
using CanvasManagement;
using SkiaSharp;
using verpixeld.MediaPlayer.Audio;
using verpixeld.Services;

namespace verpixeld.MediaPlayer;

/// <summary>
///     Video player for LED matrix using FFmpeg for decoding
///     Supports local files, network shares (SMB), and HTTP streaming
/// </summary>
public class VideoPlayer : IDisposable
{
    // Number of frames to buffer before displaying (to let ALSA audio buffer fill)
    // Adjust this if audio/video is out of sync
    private const int PreBufferFrames = 5;

    private readonly int _defaultHeight;
    private readonly int _defaultWidth;
    private readonly IAudioOutputService _audioOutputService;

    // Audio sync offset in milliseconds
    // Positive = delay audio (use when audio is ahead of video)
    // Negative = delay video display (use when video is ahead of audio)
    private int _audioSyncOffsetMs;

    private bool _canvasDisposed;
    private CancellationTokenSource? _cts;
    
    // Separate audio process for adaptive streams (YouTube)
    private Process? _audioProcess;

    // Store for seeking - we need to restart playback
    private Canvas? _currentCanvas;

    // Cached bitmap for DrawFrame to avoid memory allocation per frame
    private SKBitmap? _cachedBitmap;
    private int _cachedBitmapWidth;
    private int _cachedBitmapHeight;

    // Media metadata (extracted from file)
    private bool _currentPlayAudio;
    private string? _currentVideoPath;
    private TimeSpan _duration;
    private Process? _ffmpegProcess;
    private TimeSpan _pausedDuration;
    private DateTime _pauseStartTime;

    // Time-based position tracking (works for both video and audio)
    private DateTime _playbackStartTime;
    private Task? _playbackTask;
    private TimeSpan _seekPosition; // For seeking support
    private int _targetHeight;
    private int _targetWidth;
    private int _firstFrameNotified;

    public VideoPlayer(int width, int height, IAudioOutputService audioOutputService)
    {
        _defaultWidth = width;
        _defaultHeight = height;
        _targetWidth = width;
        _targetHeight = height;
        _audioOutputService = audioOutputService;
    }

    // Playback state
    public bool IsPlaying { get; private set; }

    public bool IsPaused { get; private set; }

    public bool IsLooping { get; private set; }

    public string? CurrentVideo => _currentVideoPath != null ? Path.GetFileName(_currentVideoPath) : null;
    public double Fps { get; private set; } = 30;

    public TimeSpan Duration => _duration;
    public MediaMetadata? Metadata { get; private set; }

    /// <summary>
    ///     Current playback position (time-based, works for video and audio)
    /// </summary>
    public TimeSpan Position
    {
        get
        {
            if (!IsPlaying) return TimeSpan.Zero;

            var elapsed = DateTime.UtcNow - _playbackStartTime - _pausedDuration;
            if (IsPaused) elapsed -= DateTime.UtcNow - _pauseStartTime;

            var position = _seekPosition + elapsed;

            // Clamp to duration
            if (position > _duration && _duration > TimeSpan.Zero) position = _duration;

            return position;
        }
    }

    /// <summary>
    ///     Whether seeking is supported for the current playback
    ///     Always true since we use FFmpeg with native SMB support
    /// </summary>
    public bool SeekingSupported => true;

    /// <summary>
    ///     FFmpeg scaling filter/algorithm. Affects how video is downscaled to the LED matrix.
    ///     Options: fast_bilinear, bilinear, bicubic, lanczos, area, gauss, sinc
    ///     Default: "auto" (fast_bilinear for streams, lanczos for local files)
    /// </summary>
    public string ScaleFilter { get; set; } = "auto";

    public int AudioSyncOffsetMs
    {
        get => _audioSyncOffsetMs;
        set => _audioSyncOffsetMs = Math.Clamp(value, -5000, 5000);
    }

    public void Dispose()
    {
        StopAsync().Wait();
        _cachedBitmap?.Dispose();
        _cachedBitmap = null;
    }

    // Events
    public event Action? OnPlaybackStarted;
    public event Action? OnPlaybackStopped;
    public event Action<string>? OnError;
    /// <summary>First decoded frame has been drawn. Overlay should stay hidden until this fires.</summary>
    public event Action? OnFirstFrame;

    /// <summary>
    ///     Play a video file or URL on the canvas
    ///     Supports local files and SMB URLs (smb://server/share/path)
    /// </summary>
    public async Task PlayAsync(string videoSource, Canvas canvas, bool loop = true, bool playAudio = true)
    {
        if (IsPlaying) await StopAsync();

        // Check if it's a URL or local file
        var isUrl = MediaProbeService.IsUrl(videoSource);
        var isSmbUrl = videoSource.StartsWith("smb://", StringComparison.OrdinalIgnoreCase);

        // SMB is supported via FFmpeg with libsmbclient
        if (isSmbUrl)
        {
            if (!FfmpegCapabilities.IsFFmpegSmbSupported())
            {
                Console.WriteLine("[VIDEO] FFmpeg SMB support not available!");
                Console.WriteLine("[VIDEO] Compile FFmpeg with --enable-libsmbclient");
                OnError?.Invoke("FFmpeg SMB support required. Compile FFmpeg with --enable-libsmbclient.");
                return;
            }

            Console.WriteLine("[VIDEO] Using FFmpeg native SMB (seeking supported)");
        }

        if (!isUrl && !File.Exists(videoSource))
        {
            OnError?.Invoke($"Video file not found: {videoSource}");
            return;
        }

        // Get video info (works for both local files and URLs via FFprobe)
        var info = await MediaProbeService.GetVideoInfoAsync(videoSource);
        if (info == null)
        {
            OnError?.Invoke("Failed to get video information. Check path/URL and network connectivity.");
            return;
        }

        // Extract metadata (title, artist, etc.) in background - don't block playback
        _ = Task.Run(async () => { Metadata = await MediaProbeService.ExtractMetadataAsync(videoSource); });

        _currentVideoPath = videoSource;
        Fps = info.Fps;
        _duration = info.Duration;
        IsLooping = loop;
        IsPlaying = true;
        IsPaused = false;
        _seekPosition = TimeSpan.Zero;
        _canvasDisposed = false; // Reset canvas disposed flag
        Interlocked.Exchange(ref _firstFrameNotified, 0);

        // Use canvas dimensions for scaling (allows overlay canvases to work)
        _targetWidth = canvas.Width;
        _targetHeight = canvas.Height;
        Console.WriteLine($"[VIDEO] Target canvas: {canvas.Name} ({_targetWidth}x{_targetHeight})");

        // Initialize time-based position tracking
        _playbackStartTime = DateTime.UtcNow;
        _pausedDuration = TimeSpan.Zero;

        // Store for seeking
        _currentCanvas = canvas;
        _currentPlayAudio = playAudio;

        _cts = new CancellationTokenSource();

        var sourceType = isUrl ? "Network" : "Local";
        Console.WriteLine(
            $"[VIDEO] Playing ({sourceType}): {info.FileName} ({info.Width}x{info.Height} @ {info.Fps:F2}fps, {info.Duration:mm\\:ss})");

        // Start synchronized video+audio playback
        _playbackTask = PlayVideoWithSyncedAudioAsync(videoSource, canvas, playAudio, TimeSpan.Zero, _cts.Token);
        OnPlaybackStarted?.Invoke();

        await Task.CompletedTask;
    }

    /// <summary>
    ///     Play an audio file (audio only - no video decoding, no canvas required)
    ///     This is more efficient for MP3/FLAC/etc as it doesn't decode embedded cover art
    /// </summary>
    public async Task PlayAudioOnlyAsync(string audioSource, bool loop = false)
    {
        if (IsPlaying) await StopAsync();

        var isUrl = MediaProbeService.IsUrl(audioSource);

        if (!isUrl && !File.Exists(audioSource))
        {
            OnError?.Invoke($"Audio file not found: {audioSource}");
            return;
        }

        // Get audio info (duration, etc.)
        var info = await MediaProbeService.GetVideoInfoAsync(audioSource);
        if (info == null)
        {
            if (isUrl)
            {
                // For network streams (e.g. internet radio), ffprobe may fail because
                // the stream is infinite or uses an unsupported container. Play anyway.
                Console.WriteLine("[AUDIO] Probe failed for stream — playing without duration info (live/radio stream)");
                info = new VideoInfo
                {
                    Path = audioSource,
                    FileName = Path.GetFileName(audioSource),
                    Width = 0,
                    Height = 0,
                    Fps = 0,
                    Duration = TimeSpan.Zero
                };
            }
            else
            {
                OnError?.Invoke("Failed to get audio information.");
                return;
            }
        }

        // Extract metadata (title, artist, etc.) in background
        _ = Task.Run(async () => { Metadata = await MediaProbeService.ExtractMetadataAsync(audioSource); });

        _currentVideoPath = audioSource;
        Fps = 0; // No video
        _duration = info.Duration;
        IsLooping = loop;
        IsPlaying = true;
        IsPaused = false;
        _seekPosition = TimeSpan.Zero;

        // No canvas for audio-only playback
        _currentCanvas = null;
        _currentPlayAudio = true;

        // Initialize time-based position tracking
        _playbackStartTime = DateTime.UtcNow;
        _pausedDuration = TimeSpan.Zero;

        _cts = new CancellationTokenSource();

        Console.WriteLine($"[AUDIO] Playing: {Path.GetFileName(audioSource)} ({info.Duration:mm\\:ss})");

        // Start audio-only playback
        _playbackTask = PlayAudioOnlyInternalAsync(audioSource, TimeSpan.Zero, _cts.Token);
        OnPlaybackStarted?.Invoke();

        await Task.CompletedTask;
    }

    /// <summary>
    ///     Play adaptive stream with separate video and audio URLs (YouTube DASH/adaptive streams)
    ///     FFmpeg will mux both streams together during playback
    /// </summary>
    /// <summary>
    ///     Set duration externally (e.g. from yt-dlp metadata when ffprobe can't determine it)
    /// </summary>
    public void SetDuration(TimeSpan duration)
    {
        if (duration > TimeSpan.Zero)
        {
            _duration = duration;
            Console.WriteLine($"[VIDEO] Duration set externally: {duration:mm\\:ss}");
        }
    }

    public async Task PlayAdaptiveStreamAsync(string videoUrl, string audioUrl, Canvas canvas, bool loop = false, bool playAudio = true, TimeSpan? knownDuration = null)
    {
        if (IsPlaying) await StopAsync();

        Console.WriteLine("[VIDEO] Playing adaptive stream (video + audio URLs)");

        // Get video info from the video URL
        var info = await MediaProbeService.GetVideoInfoAsync(videoUrl);
        if (info == null)
        {
            // Try with minimal info - YouTube URLs might timeout for probe
            Console.WriteLine("[VIDEO] Could not probe video URL, using defaults");
            info = new VideoInfo
            {
                Path = videoUrl,
                FileName = "YouTube Video",
                Width = canvas.Width,
                Height = canvas.Height,
                Fps = 30,
                Duration = knownDuration ?? TimeSpan.Zero
            };
        }
        
        // Use known duration if probe returned zero
        if (info.Duration == TimeSpan.Zero && knownDuration.HasValue && knownDuration.Value > TimeSpan.Zero)
        {
            info.Duration = knownDuration.Value;
        }

        _currentVideoPath = videoUrl;
        _currentAudioUrl = audioUrl; // Store for seeking
        Fps = info.Fps > 0 ? info.Fps : 30;
        _duration = info.Duration;
        IsLooping = loop;
        IsPlaying = true;
        IsPaused = false;
        _seekPosition = TimeSpan.Zero;
        _canvasDisposed = false;
        Interlocked.Exchange(ref _firstFrameNotified, 0);

        // Use canvas dimensions for scaling
        _targetWidth = canvas.Width;
        _targetHeight = canvas.Height;

        // Initialize time-based position tracking
        _playbackStartTime = DateTime.UtcNow;
        _pausedDuration = TimeSpan.Zero;

        _currentCanvas = canvas;
        _currentPlayAudio = playAudio;

        _cts = new CancellationTokenSource();

        Console.WriteLine($"[VIDEO] Adaptive stream: {info.Width}x{info.Height} -> {_targetWidth}x{_targetHeight}");

        // Start adaptive stream playback
        _playbackTask = PlayAdaptiveStreamInternalAsync(videoUrl, audioUrl, canvas, playAudio, TimeSpan.Zero, _cts.Token);
        OnPlaybackStarted?.Invoke();

        await Task.CompletedTask;
    }

    // Store audio URL for adaptive streams (for seeking)
    private string? _currentAudioUrl;

    /// <summary>
    ///     Internal adaptive stream playback with separate video and audio inputs
    /// </summary>
    private async Task PlayAdaptiveStreamInternalAsync(string videoUrl, string audioUrl, Canvas canvas, 
        bool playAudio, TimeSpan startPosition, CancellationToken ct)
    {
        do
        {
            var seekArg = startPosition > TimeSpan.Zero
                ? $"-ss {startPosition:hh\\:mm\\:ss\\.fff} "
                : "";

            // Network stream options for YouTube
            var networkOpts = "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 ";

            // Build FFmpeg command for adaptive stream
            // YouTube adaptive streams: video URL + audio URL combined
            var adaptiveScaleFlags = (ScaleFilter != "auto" && FfmpegCapabilities.AvailableScaleFilters.ContainsKey(ScaleFilter))
                ? ScaleFilter : "fast_bilinear";
            var videoFilter = $"scale={_targetWidth}:{_targetHeight}:flags={adaptiveScaleFlags},format={FfmpegRawVideo.PixFmt}";

            // Audio output setup — skip Pulse when this host has no audio device (Docker/NAS)
            string audioOutput = "";
            var defaultSink = _audioOutputService.CurrentSinkName;
            if (playAudio)
            {
                var ffmpegAudio = _audioOutputService.GetFFmpegAudioOutput();
                if (string.IsNullOrWhiteSpace(ffmpegAudio))
                {
                    playAudio = false;
                    Console.WriteLine("[VIDEO] Adaptive: no audio device, video only");
                }
                else if (!string.IsNullOrEmpty(defaultSink) && defaultSink != "auto_null")
                {
                    audioOutput = defaultSink;
                    Console.WriteLine($"[VIDEO] Adaptive: Using PulseAudio sink: {defaultSink}");
                }
                else
                {
                    audioOutput = "default";
                    Console.WriteLine("[VIDEO] Adaptive: Using default PulseAudio");
                }
            }

            // For adaptive streams, use SEPARATE FFmpeg processes for video and audio
            // This ensures both start at the same time and run independently
            // The combined approach was causing audio to lag due to FFmpeg's internal buffering
            
            // Video-only FFmpeg command
            var videoFfmpegArgs = $"-hide_banner -loglevel warning " +
                        $"{seekArg}{networkOpts}-i \"{videoUrl}\" " +
                        $"-vf \"{videoFilter}\" -f rawvideo -pix_fmt {FfmpegRawVideo.PixFmt} -an pipe:1";

            Console.WriteLine($"[VIDEO] FFmpeg video command (truncated): ffmpeg {videoFfmpegArgs.Substring(0, Math.Min(200, videoFfmpegArgs.Length))}...");
            
            // Audio command prepared but NOT started yet - will start after first video frame
            string? audioFfmpegArgs = null;
            if (playAudio && !string.IsNullOrEmpty(audioOutput))
            {
                audioFfmpegArgs = $"-hide_banner -loglevel warning " +
                            $"{seekArg}{networkOpts}-i \"{audioUrl}\" " +
                            $"-vn -c:a pcm_s16le -ar 48000 -ac 2 -f pulse \"{audioOutput}\"";
                
                Console.WriteLine($"[VIDEO] FFmpeg audio command (will start after first frame): ffmpeg {audioFfmpegArgs.Substring(0, Math.Min(150, audioFfmpegArgs.Length))}...");
            }

            // Start video process
            var psi = new ProcessStartInfo("ffmpeg", videoFfmpegArgs)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                OnError?.Invoke("Failed to start FFmpeg for adaptive stream");
                return;
            }

            _ffmpegProcess = proc;

            // Start reading stderr in background to capture errors
            var stderrTask = Task.Run(async () =>
            {
                var stderr = await proc.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    Console.WriteLine($"[VIDEO] FFmpeg stderr: {stderr.Substring(0, Math.Min(500, stderr.Length))}");
                    if (stderr.Contains("403") || stderr.Contains("429") ||
                        stderr.Contains("HTTP error") || stderr.Contains("Server returned"))
                        OnError?.Invoke(stderr.Length > 200 ? stderr[..200] : stderr);
                }
                return stderr;
            });

            // Read video frames from stdout
            var stdout = proc.StandardOutput.BaseStream;
            var frameSize = FfmpegRawVideo.FrameBytes(_targetWidth, _targetHeight);
            var buffer = new byte[frameSize];
            var frameCount = 0;
            
            // Track when playback started (will be reset when audio starts)
            var playbackStart = DateTime.UtcNow;

            // Reset timing for this playback attempt
            _playbackStartTime = DateTime.UtcNow - startPosition;
            startPosition = TimeSpan.Zero;

            // Audio will be started after first video frame is received
            bool audioStarted = false;
            
            try
            {
                while (!ct.IsCancellationRequested && !_canvasDisposed)
                {
                    // Handle pause - adjust timing reference
                    if (IsPaused)
                    {
                        var pauseStart = DateTime.UtcNow;
                        while (IsPaused && !ct.IsCancellationRequested)
                        {
                            await Task.Delay(50, ct);
                        }
                        // Shift playback start forward by pause duration
                        playbackStart += (DateTime.UtcNow - pauseStart);
                    }

                    if (ct.IsCancellationRequested || _canvasDisposed) break;

                    // Read frame
                    var bytesRead = 0;
                    while (bytesRead < frameSize)
                    {
                        var read = await stdout.ReadAsync(buffer.AsMemory(bytesRead, frameSize - bytesRead), ct);
                        if (read == 0) break; // End of stream
                        bytesRead += read;
                    }

                    if (bytesRead < frameSize)
                    {
                        if (frameCount == 0)
                        {
                            Console.WriteLine($"[VIDEO] Adaptive: No frames received (read {bytesRead} of {frameSize} bytes)");
                            // Wait for stderr to get the error message
                            var stderr = await stderrTask;
                            if (!string.IsNullOrEmpty(stderr))
                            {
                                OnError?.Invoke($"FFmpeg error: {stderr.Substring(0, Math.Min(200, stderr.Length))}");
                            }
                        }
                        break; // EOF or incomplete frame
                    }
                    
                    frameCount++;
                    
                    // Start audio when first video frame arrives
                    if (frameCount == 1 && !audioStarted && audioFfmpegArgs != null)
                    {
                        Console.WriteLine("[VIDEO] Adaptive: First video frame received, starting audio...");
                        
                        var audioPsi = new ProcessStartInfo("ffmpeg", audioFfmpegArgs)
                        {
                            RedirectStandardOutput = false,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        PulseAudioHelper.ApplyPulseEnv(audioPsi);
                        
                        _audioProcess = Process.Start(audioPsi);
                        if (_audioProcess != null)
                        {
                            Console.WriteLine($"[VIDEO] Audio process started (PID: {_audioProcess.Id})");
                        }
                        audioStarted = true;
                        
                        // Wait for audio to buffer in PulseAudio before displaying video
                        // Audio lags behind video, so we need to let audio get a head start
                        // Default: 500ms, adjustable via AV sync slider (negative = more audio delay)
                        var audioBufferDelayMs = 500 - _audioSyncOffsetMs; // Negative offset = more delay
                        if (audioBufferDelayMs > 0)
                        {
                            Console.WriteLine($"[VIDEO] Adaptive: Waiting {audioBufferDelayMs}ms for audio to buffer...");
                            await Task.Delay(audioBufferDelayMs, ct);
                        }
                        
                        // NOW start video timing (after audio has buffered)
                        playbackStart = DateTime.UtcNow;
                        Console.WriteLine("[VIDEO] Adaptive: Starting video display");
                    }
                    else if (frameCount == 1)
                    {
                        Console.WriteLine("[VIDEO] Adaptive: First frame displayed (no audio)");
                    }
                    else if (frameCount % 300 == 0) // Every 10 seconds at 30fps
                    {
                        var elapsed = DateTime.UtcNow - playbackStart;
                        var expectedPos = TimeSpan.FromSeconds(frameCount / Fps);
                        var drift = (elapsed - expectedPos).TotalMilliseconds;
                        Console.WriteLine($"[VIDEO] Adaptive: {frameCount} frames, elapsed={elapsed.TotalSeconds:F1}s, expected={expectedPos.TotalSeconds:F1}s, drift={drift:+0;-0}ms");
                    }

                    // Render to canvas
                    if (!_canvasDisposed && canvas != null)
                    {
                        try
                        {
                            DrawFrame(canvas, buffer);
                            NotifyFirstFrame();
                        }
                        catch (ObjectDisposedException)
                        {
                            Console.WriteLine("[VIDEO] Canvas disposed during adaptive playback");
                            _canvasDisposed = true;
                            break;
                        }
                    }

                    // Frame timing: Display at correct FPS (e.g., 30fps = 33ms per frame)
                    // Without this, video plays as fast as FFmpeg can decode (way too fast)
                    var targetDisplayTime = playbackStart.AddMilliseconds(frameCount * (1000.0 / Fps));
                    var now = DateTime.UtcNow;
                    
                    if (now < targetDisplayTime)
                    {
                        var waitMs = (targetDisplayTime - now).TotalMilliseconds;
                        if (waitMs > 1)
                        {
                            await Task.Delay((int)waitMs, ct);
                        }
                    }
                    // If behind schedule, display immediately to catch up
                }
                
                Console.WriteLine($"[VIDEO] Adaptive: Playback ended after {frameCount} frames");
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VIDEO] Adaptive playback error: {ex.Message}");
            }
            finally
            {
                // Stop video process
                try
                {
                    if (proc != null && !proc.HasExited)
                    {
                        proc.Kill();
                    }
                }
                catch
                {
                    // Process already disposed or exited - ignore
                }
                _ffmpegProcess = null;
                
                // Stop audio process
                try
                {
                    if (_audioProcess != null && !_audioProcess.HasExited)
                    {
                        _audioProcess.Kill();
                        Console.WriteLine("[VIDEO] Audio process stopped");
                    }
                }
                catch
                {
                    // Process already disposed or exited - ignore
                }
                _audioProcess = null;
            }

            // Check if we should loop
            if (!IsLooping || ct.IsCancellationRequested || _canvasDisposed) break;

            Console.WriteLine("[VIDEO] Looping adaptive stream...");

        } while (IsLooping && !ct.IsCancellationRequested && !_canvasDisposed);

        IsPlaying = false;
        OnPlaybackStopped?.Invoke();
    }

    /// <summary>
    ///     Internal audio-only playback using FFmpeg
    ///     Only decodes and outputs audio - no video processing
    /// </summary>
    private async Task PlayAudioOnlyInternalAsync(string audioPath, TimeSpan startPosition, CancellationToken ct)
    {
        var isNetworkStream = MediaProbeService.IsUrl(audioPath);

        do
        {
            var seekArg = startPosition > TimeSpan.Zero
                ? $"-ss {startPosition:hh\\:mm\\:ss\\.fff} "
                : "";

            // Network stream options
            var networkOpts = "";
            if (isNetworkStream)
            {
                networkOpts = "-thread_queue_size 4096 -fflags +igndts ";
                if (audioPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    networkOpts += "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 ";
            }

            // Get audio output (PulseAudio / ALSA)
            var audioOutput = _audioOutputService.GetFFmpegAudioOutput();
            if (string.IsNullOrWhiteSpace(audioOutput))
            {
                Console.WriteLine("[AUDIO] No audio output device — cannot play audio-only here");
                OnError?.Invoke("No audio output device (Pulse/ALSA)");
                break;
            }

            // Pace local files at native duration. Without -re, FFmpeg dumps decoded audio into
            // Pulse as fast as it can, bloating the sink buffer — the visualizer then lags/stutters.
            var realtimeFlag = isNetworkStream ? "" : "-re ";

            // FFmpeg for audio-only: use -vn to explicitly disable video decoding
            // This prevents FFmpeg from decoding embedded cover art
            var ffmpegArgs =
                $"-hide_banner -loglevel error -nostats {seekArg}{networkOpts}{realtimeFlag}-i \"{audioPath}\" -vn {audioOutput}";

            Console.WriteLine($"[AUDIO] FFmpeg args: {ffmpegArgs}");

            var psi = new ProcessStartInfo("ffmpeg", ffmpegArgs)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Same Pulse daemon as parec, with a short buffer so the visualizer stays in sync.
            PulseAudioHelper.ApplyPulseEnv(psi, 30);

            try
            {
                _ffmpegProcess = Process.Start(psi);
                if (_ffmpegProcess == null)
                {
                    OnError?.Invoke("Failed to start FFmpeg");
                    break;
                }

                var ffmpegProc = _ffmpegProcess;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var stderr = ffmpegProc.StandardError;
                        while (await stderr.ReadLineAsync() is { } line)
                            if (line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                                line.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
                                line.Contains("pulse", StringComparison.OrdinalIgnoreCase))
                                Console.WriteLine($"[FFMPEG] {line}");
                    }
                    catch
                    {
                        // Process exited or stderr closed
                    }
                });

                // Monitor for completion or cancellation
                while (!ct.IsCancellationRequested && !_ffmpegProcess.HasExited)
                {
                    // Handle pause by checking flag
                    while (IsPaused && !ct.IsCancellationRequested && !_ffmpegProcess.HasExited)
                        await Task.Delay(100, ct);

                    await Task.Delay(100, ct);
                }

                // Check if playback ended naturally (not cancelled)
                if (!ct.IsCancellationRequested && _ffmpegProcess.HasExited && _ffmpegProcess.ExitCode == 0)
                    // Playback finished
                    if (IsLooping)
                    {
                        Console.WriteLine("[AUDIO] Loop: restarting");
                        startPosition = TimeSpan.Zero;
                        _playbackStartTime = DateTime.UtcNow;
                        _pausedDuration = TimeSpan.Zero;
                        _seekPosition = TimeSpan.Zero;
                        continue;
                    }

                break; // Exit the loop
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUDIO] Playback error: {ex.Message}");
                OnError?.Invoke(ex.Message);
                break;
            }
            finally
            {
                StopProcesses();
            }
        } while (IsLooping && !ct.IsCancellationRequested);

        IsPlaying = false;
        OnPlaybackStopped?.Invoke();
    }

    /// <summary>
    ///     Seek to a specific position in the video/audio
    ///     This will restart the FFmpeg process from the new position
    /// </summary>
    public async Task SeekAsync(TimeSpan position)
    {
        if (!IsPlaying || _currentVideoPath == null)
        {
            Console.WriteLine("[MEDIA] Cannot seek - not playing");
            return;
        }

        // Clamp position to valid range
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > _duration) position = _duration - TimeSpan.FromSeconds(1);

        Console.WriteLine($"[MEDIA] Seeking to {position:mm\\:ss}");

        // Stop current playback
        _cts?.Cancel();
        StopProcesses();

        if (_playbackTask != null)
            try
            {
                await _playbackTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VIDEO] Seek: Previous playback task error: {ex.Message}");
            }

        // Restart from new position with fresh timing
        _seekPosition = position;
        _playbackStartTime = DateTime.UtcNow;
        _pausedDuration = TimeSpan.Zero;
        IsPlaying = true; // Must set this before starting new task (old task sets it to false on cancel)
        IsPaused = false;
        _cts = new CancellationTokenSource();

        // Choose playback method based on stream type
        if (_currentAudioUrl != null && _currentCanvas != null)
        {
            // Adaptive stream (YouTube) - separate video and audio URLs
            _playbackTask = PlayAdaptiveStreamInternalAsync(_currentVideoPath, _currentAudioUrl, _currentCanvas, 
                _currentPlayAudio, position, _cts.Token);
        }
        else if (_currentCanvas != null)
        {
            // Regular video with canvas
            _playbackTask = PlayVideoWithSyncedAudioAsync(_currentVideoPath, _currentCanvas, _currentPlayAudio,
                position, _cts.Token);
        }
        else
        {
            // Audio-only playback
            _playbackTask = PlayAudioOnlyInternalAsync(_currentVideoPath, position, _cts.Token);
        }
    }

    /// <summary>
    ///     Seek to a percentage of the video (0-100)
    /// </summary>
    public async Task SeekPercentAsync(double percent)
    {
        var position = TimeSpan.FromSeconds(_duration.TotalSeconds * (percent / 100.0));
        await SeekAsync(position);
    }

    /// <summary>
    ///     Stop playback
    /// </summary>
    public async Task StopAsync()
    {
        if (!IsPlaying) return;

        Console.WriteLine("[VIDEO] Stopping playback...");

        _cts?.Cancel();

        // Stop FFmpeg process (handles both video and audio)
        StopFFmpegProcess();

        if (_playbackTask != null)
            try
            {
                await _playbackTask;
            }
            catch (OperationCanceledException)
            {
            }

        IsPlaying = false;
        IsPaused = false;
        _currentVideoPath = null;
        _currentAudioUrl = null; // Clear adaptive stream audio URL
        Metadata = null;

        OnPlaybackStopped?.Invoke();
        Console.WriteLine("[VIDEO] Playback stopped");
    }

    /// <summary>
    ///     Pause/Resume playback using process signals (works for both video and audio)
    /// </summary>
    public void TogglePause()
    {
        if (!IsPaused)
        {
            // Starting pause - record when we paused
            _pauseStartTime = DateTime.UtcNow;

            // Send SIGSTOP to FFmpeg to pause the process (pauses both video and audio)
            PauseFFmpegProcess();
        }
        else
        {
            // Ending pause - add time spent paused to total
            _pausedDuration += DateTime.UtcNow - _pauseStartTime;

            // Send SIGCONT to FFmpeg to resume the process
            ResumeFFmpegProcess();
        }

        IsPaused = !IsPaused;
        Console.WriteLine($"[VIDEO] {(IsPaused ? "Paused" : "Resumed")}");
    }

    /// <summary>
    ///     Pause the FFmpeg process using SIGSTOP
    /// </summary>
    private void PauseFFmpegProcess()
    {
        try
        {
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                // Use kill -STOP to pause the process (Linux)
                var killProc = Process.Start(new ProcessStartInfo("kill", $"-STOP {_ffmpegProcess.Id}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                killProc?.WaitForExit(1000);
                Console.WriteLine($"[VIDEO] Paused FFmpeg process (PID: {_ffmpegProcess.Id})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIDEO] Failed to pause process: {ex.Message}");
        }
    }

    /// <summary>
    ///     Resume the FFmpeg process using SIGCONT
    /// </summary>
    private void ResumeFFmpegProcess()
    {
        try
        {
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                var killProc = Process.Start(new ProcessStartInfo("kill", $"-CONT {_ffmpegProcess.Id}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                killProc?.WaitForExit(1000);
                Console.WriteLine($"[VIDEO] Resumed FFmpeg process (PID: {_ffmpegProcess.Id})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIDEO] Failed to resume process: {ex.Message}");
        }
    }

    /// <summary>
    ///     Play video with synchronized audio using a single FFmpeg command
    ///     The -re flag makes FFmpeg output at native frame rate, keeping A/V in sync
    ///     Uses V4L2 M2M hardware decoding on Raspberry Pi when available
    /// </summary>
    private async Task PlayVideoWithSyncedAudioAsync(string videoPath, Canvas canvas, bool playAudio,
        TimeSpan startPosition, CancellationToken ct)
    {
        var frameBuffer = new byte[FfmpegRawVideo.FrameBytes(_targetWidth, _targetHeight)];

        // Detect if this is a network stream
        var isHttpStream = videoPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                           videoPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var isFtpStream = videoPath.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
        var isSmbStream = videoPath.StartsWith("smb://", StringComparison.OrdinalIgnoreCase);
        var isNetworkStream = isHttpStream || isFtpStream || isSmbStream;

        // Check for hardware acceleration
        var useHwAccel = FfmpegCapabilities.IsHwAccelAvailable();

        do
        {
            // Build FFmpeg command with synchronized audio+video output
            // -ss: Seek to start position (before -i for fast seeking)
            // -re: Read input at native frame rate (for local files - NOT for network streams!)
            // -hwaccel: Use hardware decoding if available
            // -map 0:v:0: First video stream to raw video output
            // -map 0:a:0?: First audio stream to ALSA (? makes it optional)

            var seekArg = startPosition > TimeSpan.Zero
                ? $"-ss {startPosition:hh\\:mm\\:ss\\.fff} "
                : "";

            // Hardware acceleration (Raspberry Pi V4L2 M2M)
            // -hwaccel auto: Let FFmpeg choose the best available hardware decoder
            // This dramatically improves performance on Pi
            var hwAccelArg = useHwAccel ? "-hwaccel auto " : "";

            // Choose scaling algorithm - user-configurable or automatic
            string scaleFlags;
            if (ScaleFilter != "auto" && FfmpegCapabilities.AvailableScaleFilters.ContainsKey(ScaleFilter))
            {
                scaleFlags = ScaleFilter;
            }
            else
            {
                // Auto mode: fast_bilinear for streams/HW, lanczos for local files
                scaleFlags = isNetworkStream || useHwAccel ? "fast_bilinear" : "lanczos";
            }

            // Network stream options for FFmpeg:
            var networkOpts = "";
            var realtimeFlag = "-re "; // Use realtime for local files

            if (isNetworkStream)
            {
                realtimeFlag = ""; // Don't use -re for network - we'll handle frame timing

                // Key optimizations for network streams:
                // - thread_queue_size: Large input buffer to absorb network jitter
                // - fflags: nobuffer reduces latency, igndts handles timestamp issues
                // - threads: Use all CPU cores for decoding
                networkOpts = "-thread_queue_size 4096 -fflags +igndts -threads 0 ";

                if (isHttpStream)
                {
                    // HTTP-specific: reconnection and larger probing
                    networkOpts += "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 -probesize 5M ";
                    Console.WriteLine($"[VIDEO] HTTP stream: HW={useHwAccel}, fast_bilinear scaling");
                }
                else if (isFtpStream)
                {
                    networkOpts += "-probesize 5M ";
                    Console.WriteLine($"[VIDEO] FTP stream: HW={useHwAccel}, fast_bilinear scaling");
                }
                else if (isSmbStream)
                {
                    networkOpts += "-probesize 5M ";
                    Console.WriteLine($"[VIDEO] SMB stream: HW={useHwAccel}, fast_bilinear scaling");
                }
            }
            else if (useHwAccel)
            {
                Console.WriteLine("[VIDEO] Local file with hardware decoding");
            }

            string ffmpegArgs;

            // A/V SYNC STRATEGY:
            // The fundamental problem: FFmpeg outputs audio to ALSA immediately while we buffer video.
            // 
            // For local files with -re: FFmpeg paces both audio and video at real-time, sync is natural.
            // For network streams without -re: Audio plays immediately, video is delayed by:
            //   1. Network buffering in FFmpeg (probesize, thread_queue_size)
            //   2. Our pre-buffering of video frames
            //   3. Frame decoding time
            //
            // Solution: Use -itsoffset to delay the AUDIO INPUT stream, giving video time to catch up.
            // The user's _audioSyncOffsetMs is added on top for fine-tuning.
            //
            // Base delay estimates:
            // - Local files: minimal (50ms for ALSA buffer startup)
            // - Network streams: ~500-1500ms depending on network speed and buffering

            var baseAudioDelayMs = 0;
            if (isNetworkStream)
                // Network streams need significant base delay due to buffering
                // FFmpeg probesize (5M) + thread_queue_size + our pre-buffer
                baseAudioDelayMs = 1500; // 1.5 second base delay for network
            else if (!string.IsNullOrEmpty(realtimeFlag))
                // Local files with -re: minimal delay, FFmpeg handles sync
                baseAudioDelayMs = 0;

            // Total audio delay = base + user adjustment
            var totalAudioDelayMs = baseAudioDelayMs + _audioSyncOffsetMs;

            // Empty when there is no device — do not pass `-f alsa default` or FFmpeg
            // dies before the first video frame (Docker/NAS has no sound card).
            var audioOutput = playAudio ? _audioOutputService.GetFFmpegAudioOutput() : "";
            var emitAudio = playAudio && !string.IsNullOrWhiteSpace(audioOutput);
            if (playAudio && !emitAudio)
                Console.WriteLine("[VIDEO] No audio device — decoding video only");

            var audioFilter = "";
            if (totalAudioDelayMs > 0 && emitAudio)
            {
                audioFilter = $"-af \"adelay={totalAudioDelayMs}:all=1\" ";
                Console.WriteLine(
                    $"[VIDEO] Audio sync: delaying audio by {totalAudioDelayMs}ms (base: {baseAudioDelayMs}ms + user: {_audioSyncOffsetMs}ms)");
            }
            else if (totalAudioDelayMs < 0 && emitAudio)
            {
                Console.WriteLine($"[VIDEO] Audio sync: delaying video by {-totalAudioDelayMs}ms (user adjustment)");
            }

            if (emitAudio)
                // Single FFmpeg process handles both video (to pipe) and audio (to PulseAudio/ALSA)
                // hwaccel must come BEFORE -i
                ffmpegArgs = $"{seekArg}{hwAccelArg}{networkOpts}{realtimeFlag}-i \"{videoPath}\" " +
                             $"-map 0:v:0 -f rawvideo -pix_fmt {FfmpegRawVideo.PixFmt} -s {_targetWidth}x{_targetHeight} " +
                             $"-vf \"scale={_targetWidth}:{_targetHeight}:flags={scaleFlags}\" pipe:1 " +
                             $"-map 0:a:0? {audioFilter}{audioOutput}";
            else
                // Video only (no audio) - still use hardware acceleration
                ffmpegArgs = $"{seekArg}{hwAccelArg}{networkOpts}{realtimeFlag}-i \"{videoPath}\" " +
                             $"-f rawvideo -pix_fmt {FfmpegRawVideo.PixFmt} -s {_targetWidth}x{_targetHeight} " +
                             $"-vf \"scale={_targetWidth}:{_targetHeight}:flags={scaleFlags}\" -";

            var psi = new ProcessStartInfo("ffmpeg", ffmpegArgs)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // For system-mode PulseAudio, set PULSE_SERVER so FFmpeg can connect
            PulseAudioHelper.ApplyPulseEnv(psi);

            var seekInfo = startPosition > TimeSpan.Zero ? $" from {startPosition:mm\\:ss}" : "";
            var streamInfo = isNetworkStream ? " (network stream)" : "";
            Console.WriteLine(
                $"[VIDEO] Starting FFmpeg{seekInfo}{streamInfo}: {(emitAudio ? "audio+video" : "video only")}");
            Console.WriteLine($"[VIDEO] FFmpeg args: {ffmpegArgs}");

            _ffmpegProcess = Process.Start(psi);
            if (_ffmpegProcess == null)
            {
                OnError?.Invoke("Failed to start FFmpeg");
                break;
            }

            // Start reading stderr in background to capture FFmpeg errors
            _ = Task.Run(async () =>
            {
                try
                {
                    var stderr = _ffmpegProcess?.StandardError;
                    if (stderr == null) return;

                    string? line;
                    while ((line = await stderr.ReadLineAsync()) != null)
                    {
                        if (line.Contains("Error") || line.Contains("error") ||
                            line.Contains("Failed") || line.Contains("failed") ||
                            line.Contains("Invalid") || line.Contains("Unable") ||
                            line.Contains("Connection refused") || line.Contains("pulse"))
                            Console.WriteLine($"[FFMPEG] {line}");
                        // Do not treat FFmpeg progress (`frame=  668 ... bitrate=...`) as HTTP 403/429.
                        if (line.Contains("frame=", StringComparison.Ordinal))
                            continue;
                        if (line.Contains("HTTP error", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("Server returned", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("429 Too", StringComparison.OrdinalIgnoreCase))
                            OnError?.Invoke(line.Trim());
                    }
                }
                catch
                {
                }
            });

            var stream = _ffmpegProcess.StandardOutput.BaseStream;
            var frameCount = 0;

            // Pre-buffer: Minimal for network (sync handled by adelay), small for local
            // We only need enough frames to smooth out jitter, not for A/V sync
            // A/V sync is now handled by adelay filter in FFmpeg
            var preBufferCount = isNetworkStream ? 3 : PreBufferFrames; // Minimal for network
            if (playAudio && preBufferCount > 0)
            {
                var preBuffer = new Queue<byte[]>();
                var preBufferStart = DateTime.UtcNow;
                Console.WriteLine($"[VIDEO] Pre-buffering {preBufferCount} frames for smooth playback...");

                for (var i = 0; i < preBufferCount && !ct.IsCancellationRequested; i++)
                {
                    var frame = new byte[FfmpegRawVideo.FrameBytes(_targetWidth, _targetHeight)];
                    var bytesRead = 0;
                    while (bytesRead < frame.Length)
                    {
                        var read = await stream.ReadAsync(frame.AsMemory(bytesRead, frame.Length - bytesRead), ct);
                        if (read == 0) break;
                        bytesRead += read;
                    }

                    if (bytesRead == frame.Length) preBuffer.Enqueue(frame);
                }

                var preBufferTime = (DateTime.UtcNow - preBufferStart).TotalMilliseconds;
                Console.WriteLine($"[VIDEO] Pre-buffer complete in {preBufferTime:F0}ms ({preBuffer.Count} frames)");

                // If we got 0 frames, FFmpeg likely crashed - check if process exited
                if (preBuffer.Count == 0 && _ffmpegProcess != null && _ffmpegProcess.HasExited)
                {
                    Console.WriteLine($"[VIDEO] FFmpeg exited with code {_ffmpegProcess.ExitCode} during pre-buffer");
                    OnError?.Invoke($"FFmpeg exited with code {_ffmpegProcess.ExitCode} before the first frame");
                    await Task.Delay(200);
                    if (!IsLooping) break;
                    continue;
                }

                // Display the pre-buffered frames immediately
                while (preBuffer.Count > 0 && !ct.IsCancellationRequested)
                {
                    var frame = preBuffer.Dequeue();
                    DrawFrame(canvas, frame);
                    frameCount++;
                    NotifyFirstFrame();
                }
            }

            // Audio sync: negative offset = video is ahead, delay video start
            // This is a one-time delay before playback begins (positive offset is handled by adelay filter)
            if (_audioSyncOffsetMs < 0 && !ct.IsCancellationRequested)
            {
                var videoDelayMs = -_audioSyncOffsetMs;
                Console.WriteLine($"[VIDEO] Audio sync: delaying video start by {videoDelayMs}ms (audio is behind)");
                await Task.Delay(videoDelayMs, ct);
            }

            // For network streams without -re, we need manual frame timing
            var frameInterval = TimeSpan.FromSeconds(1.0 / Fps);
            var playbackStart = DateTime.UtcNow;
            var lastFrameTime = DateTime.UtcNow;

            // Diagnostic timing for network streams
            var diagStart = DateTime.UtcNow;
            var lastDiagReport = DateTime.UtcNow;
            var totalReadMs = 0.0;
            var totalDrawMs = 0.0;

            while (!ct.IsCancellationRequested && !_canvasDisposed)
            {
                // Handle pause - note: this will cause A/V desync during pause
                // For proper pause, we'd need to pause FFmpeg too (not easily doable)
                while (IsPaused && !ct.IsCancellationRequested && !_canvasDisposed)
                {
                    await Task.Delay(100, ct);
                    // Adjust timing references after pause
                    playbackStart = DateTime.UtcNow - TimeSpan.FromSeconds(frameCount / Fps);
                    lastFrameTime = DateTime.UtcNow;
                }

                if (ct.IsCancellationRequested || _canvasDisposed) break;

                // Read one frame with timing
                var readStart = DateTime.UtcNow;
                var bytesRead = 0;
                while (bytesRead < frameBuffer.Length)
                {
                    var read = await stream.ReadAsync(frameBuffer.AsMemory(bytesRead, frameBuffer.Length - bytesRead),
                        ct);
                    if (read == 0) break; // End of stream
                    bytesRead += read;
                }

                var readMs = (DateTime.UtcNow - readStart).TotalMilliseconds;
                totalReadMs += readMs;

                if (bytesRead < frameBuffer.Length)
                {
                    if (frameCount == 0)
                        OnError?.Invoke("No video frames received (stream refused or empty)");
                    break;
                }

                // Convert BGRA to SKBitmap and draw with timing
                var drawStart = DateTime.UtcNow;
                DrawFrame(canvas, frameBuffer);
                NotifyFirstFrame();
                var drawMs = (DateTime.UtcNow - drawStart).TotalMilliseconds;
                totalDrawMs += drawMs;

                frameCount++;
                // Position is now time-based, automatically calculated

                // Diagnostic report every 5 seconds for network streams (if enabled)
                if (FfmpegCapabilities.DiagnosticsEnabled && isNetworkStream && (DateTime.UtcNow - lastDiagReport).TotalSeconds >= 5)
                {
                    var elapsed = (DateTime.UtcNow - diagStart).TotalSeconds;
                    var actualFps = frameCount / elapsed;
                    var avgReadMs = totalReadMs / frameCount;
                    var avgDrawMs = totalDrawMs / frameCount;
                    Console.WriteLine(
                        $"[VIDEO DIAG] {frameCount} frames in {elapsed:F1}s = {actualFps:F1} FPS (target: {Fps:F1})");
                    Console.WriteLine($"[VIDEO DIAG] Avg read: {avgReadMs:F1}ms, Avg draw: {avgDrawMs:F1}ms");
                    if (avgReadMs > 30)
                        Console.WriteLine("[VIDEO DIAG] ⚠️ SLOW READ - FFmpeg decode/network is the bottleneck");
                    if (avgDrawMs > 10)
                        Console.WriteLine("[VIDEO DIAG] ⚠️ SLOW DRAW - Canvas rendering is the bottleneck");
                    lastDiagReport = DateTime.UtcNow;
                }

                // Frame timing:
                // - With -re flag (local files): FFmpeg controls timing, no delay needed
                // - Without -re (network streams): We control timing to match FPS
                if (isNetworkStream)
                {
                    // Calculate when this frame should be displayed
                    var targetTime = playbackStart + TimeSpan.FromSeconds(frameCount / Fps);
                    var now = DateTime.UtcNow;

                    if (targetTime > now)
                    {
                        // We're ahead of schedule - wait
                        var delay = targetTime - now;
                        if (delay.TotalMilliseconds > 1) await Task.Delay(delay, ct);
                    }
                    // If we're behind schedule, just continue (frames will play as fast as possible to catch up)
                }
                // For local files with -re flag, no delay needed - FFmpeg handles timing
            }

            StopFFmpegProcess();

            // Reset for loop (start from beginning)
            startPosition = TimeSpan.Zero;
            _seekPosition = TimeSpan.Zero;
            _playbackStartTime = DateTime.UtcNow;
            _pausedDuration = TimeSpan.Zero;
        } while (IsLooping && !ct.IsCancellationRequested && !_canvasDisposed);

        IsPlaying = false;
        OnPlaybackStopped?.Invoke();
    }

    private void NotifyFirstFrame()
    {
        if (Interlocked.Exchange(ref _firstFrameNotified, 1) == 0)
            OnFirstFrame?.Invoke();
    }

    private void DrawFrame(Canvas canvas, byte[] bgra)
    {
        // Check if canvas is still valid
        if (canvas == null || _canvasDisposed) return;

        try
        {
            if (_cachedBitmap == null || _cachedBitmapWidth != _targetWidth || _cachedBitmapHeight != _targetHeight)
            {
                _cachedBitmap?.Dispose();
                _cachedBitmap = new SKBitmap(_targetWidth, _targetHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
                _cachedBitmapWidth = _targetWidth;
                _cachedBitmapHeight = _targetHeight;
            }

            FfmpegRawVideo.CopyToBitmap(_cachedBitmap, bgra);
            canvas.DrawBitmap(_cachedBitmap, 0, 0);
        }
        catch (ObjectDisposedException)
        {
            // Canvas was disposed while we were trying to draw
            Console.WriteLine("[VIDEO] Canvas disposed during draw - stopping playback");
            _canvasDisposed = true;
            _cts?.Cancel();
        }
    }

    private void StopFFmpegProcess()
    {
        StopProcesses();
    }

    private void StopProcesses()
    {
        // Stop FFmpeg video process
        if (_ffmpegProcess != null)
            try
            {
                if (!_ffmpegProcess.HasExited) _ffmpegProcess.Kill();
            }
            catch
            {
            }
            finally
            {
                _ffmpegProcess?.Dispose();
                _ffmpegProcess = null;
            }
            
        // Stop FFmpeg audio process (for adaptive streams)
        if (_audioProcess != null)
            try
            {
                if (!_audioProcess.HasExited) 
                {
                    _audioProcess.Kill();
                    Console.WriteLine("[VIDEO] Stopped separate audio process");
                }
            }
            catch
            {
            }
            finally
            {
                _audioProcess?.Dispose();
                _audioProcess = null;
            }
    }
}

/// <summary>
///     Video file information
/// </summary>
public class VideoInfo
{
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
///     Media metadata (ID3 tags, container metadata)
/// </summary>
public class MediaMetadata
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public string? Year { get; set; }
    public int? TrackNumber { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Composer { get; set; }

    /// <summary>
    ///     Display artist - returns artist or "Unknown Artist"
    /// </summary>
    public string DisplayArtist => !string.IsNullOrWhiteSpace(Artist) ? Artist : "";

    public bool HasMetadata => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Artist);

    /// <summary>
    ///     Display title - uses metadata title if available, otherwise filename
    /// </summary>
    public string DisplayTitle(string? fallbackFilename)
    {
        return !string.IsNullOrWhiteSpace(Title) ? Title : fallbackFilename ?? "Unknown";
    }

    /// <summary>
    ///     Combined display string: "Artist - Title" or just "Title"
    /// </summary>
    public string DisplayString(string? fallbackFilename)
    {
        var title = DisplayTitle(fallbackFilename);
        return !string.IsNullOrWhiteSpace(Artist) ? $"{Artist} - {title}" : title;
    }
}

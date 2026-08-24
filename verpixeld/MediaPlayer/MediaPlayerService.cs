using CanvasManagement;
using SkiaSharp;
using verpixeld.Configuration;
using verpixeld.MediaPlayer.Audio;

namespace verpixeld.MediaPlayer;

/// <summary>
///     Media player service for playing video and audio content on the LED matrix
///     Supports local files, network shares (SMB), and streaming
/// </summary>
public class MediaPlayerService : IDisposable
{
    private readonly AlsaAudioService _audio;
    private readonly CanvasManager _canvasManager;
    private readonly IAudioOutputService _audioOutputService;
    private VideoPlayer _videoPlayer;
    private bool _audioEnabled = true;

    // Playlist/queue state
    private List<string> _audioPlaylist = new();
    private int _height;

    // Audio playback state

    // State
    private bool _isRunning;
    private Canvas? _mediaCanvas;

    // Network video state
    private readonly Random _shuffleRandom = new();
    
    // Auto-play favorites state
    private List<AutoPlayItem> _autoPlayQueue = new();
    private int _autoPlayCurrentIndex = -1;
    private int _userInitiatedStop;      // 0 = false, 1 = true (Interlocked for thread safety)
    private int _autoPlayAdvancing;      // 0 = false, 1 = true (Interlocked for thread safety)
    private int _handlingStop;           // Re-entry guard for OnPlaybackStopped
    private int _gotFirstFrame;
    private TaskCompletionSource<bool>? _startGate;

    private int _width;

    public MediaPlayerService(CanvasManager canvasManager, IAudioOutputService audioOutputService, int width, int height)
    {
        _canvasManager = canvasManager;
        _audioOutputService = audioOutputService;
        _width = width;
        _height = height;
        _audio = new AlsaAudioService();
        _videoPlayer = new VideoPlayer(width, height, audioOutputService);

        _videoPlayer.OnPlaybackStarted += () => Console.WriteLine("[MEDIA] Video playback started");
        _videoPlayer.OnPlaybackStopped += OnPlaybackStopped;
        _videoPlayer.OnFirstFrame += OnFirstFrame;
        _videoPlayer.OnError += OnVideoError;

        Console.WriteLine($"[MEDIA] Initialized for {width}x{height} display");
        Console.WriteLine($"[MEDIA] FFmpeg available: {FFmpegAvailable}");
        Console.WriteLine($"[MEDIA] FFmpeg HW accel (V4L2 M2M): {FfmpegCapabilities.IsHwAccelAvailable()}");
        Console.WriteLine($"[MEDIA] FFmpeg SMB support: {FFmpegSmbSupported}");
        Console.WriteLine($"[MEDIA] yt-dlp available: {YtDlpAvailable}");
        Console.WriteLine($"[MEDIA] MOD player available: {ModPlayerAvailable}");
    }

    // Public properties
    public bool IsRunning => _isRunning || _videoPlayer.IsPlaying;
    public bool IsPaused => _videoPlayer.IsPaused;
    public string? CurrentVideo => _videoPlayer.CurrentVideo;
    public string? CurrentAudio { get; private set; }

    public string? LastPlayedAudio { get; private set; }

    public string? LastPlayedAudioUrl { get; private set; }

    public bool LastPlayedWasNetwork { get; private set; }
    
    /// <summary>
    ///     Last played YouTube URL (original URL, not HLS manifest) for replay
    /// </summary>
    public string? LastPlayedYouTubeUrl { get; private set; }
    
    /// <summary>
    ///     Last played YouTube video title for display
    /// </summary>
    public string? LastPlayedYouTubeTitle { get; private set; }

    /// <summary>
    ///     Last error message from playback attempt
    /// </summary>
    public string? LastPlaybackError { get; set; }

    public bool IsAudioPlayback { get; private set; }

    public bool HasAudioPlaylist => _audioPlaylist.Count > 0;

    // Playlist properties
    public IReadOnlyList<string> AudioPlaylist => _audioPlaylist.AsReadOnly();
    public int CurrentPlaylistIndex { get; private set; } = -1;

    public bool AutoAdvance { get; set; } = true;
    public bool ShuffleMode { get; set; }

    public bool RepeatMode { get; set; }

    public bool HasNextTrack =>
        _audioPlaylist.Count > 0 && (RepeatMode || CurrentPlaylistIndex < _audioPlaylist.Count - 1);

    public bool HasPreviousTrack => _audioPlaylist.Count > 0 && (RepeatMode || CurrentPlaylistIndex > 0);

    // Radio stream state
    public bool IsRadioPlaying { get; private set; }
    public string? RadioStationName { get; private set; }

    // Auto-play favorites properties
    public bool AutoPlayFavorites { get; private set; }
    public string? AutoPlayCurrentId => _autoPlayCurrentIndex >= 0 && _autoPlayCurrentIndex < _autoPlayQueue.Count 
        ? _autoPlayQueue[_autoPlayCurrentIndex].FavoriteId : null;
    public string? AutoPlayCurrentName => _autoPlayCurrentIndex >= 0 && _autoPlayCurrentIndex < _autoPlayQueue.Count 
        ? _autoPlayQueue[_autoPlayCurrentIndex].Name : null;
    public int AutoPlayCurrentIndex => _autoPlayCurrentIndex;
    public int AutoPlayTotal => _autoPlayQueue.Count;

    public TimeSpan VideoDuration => _videoPlayer.Duration;
    public TimeSpan VideoPosition => _videoPlayer.Position;
    public double VideoFps => _videoPlayer.Fps;
    public bool IsNetworkVideo => NetworkShareName != null;
    public string? NetworkShareName { get; private set; }

    public string? NetworkFilePath { get; private set; }

    public string? NetworkProtocol { get; private set; }
    
    /// <summary>
    ///     The actual URL used for network video playback (for saving as favorite)
    /// </summary>
    public string? NetworkVideoUrl { get; private set; }

    public MediaMetadata? Metadata => _videoPlayer.Metadata;

    /// <summary>
    ///     Whether seeking is supported for the current video
    ///     Always true since we use FFmpeg with native SMB support
    /// </summary>
    public bool SeekingSupported => _videoPlayer.SeekingSupported;

    public bool AudioEnabled
    {
        get => _audioEnabled && _audio.IsEnabled;
        set => _audioEnabled = value;
    }

    public bool AudioAvailable => _audio.IsEnabled;

    /// <summary>
    ///     FFmpeg video scaling filter. "auto" = automatic selection.
    /// </summary>
    public string ScaleFilter
    {
        get => _videoPlayer.ScaleFilter;
        set => _videoPlayer.ScaleFilter = value;
    }
    public bool ModPlayerAvailable => _audio.ModPlayerAvailable;
    public bool IsModPlaying => _audio.IsModPlaying;
    public string? CurrentModFile => _audio.CurrentModFile;
    public string? ModFilePath { get; private set; }

    public bool IsMuted => _audio.IsMuted;

    /// <summary>
    ///     Target canvas name for video/audio playback
    ///     If null or "Main", creates a full-screen canvas
    ///     Otherwise, uses the existing canvas with matching name
    /// </summary>
    public string? TargetCanvasName { get; set; }

    /// <summary>
    ///     Canvas that is actually receiving video frames right now.
    ///     Target "Main" (the default) is remapped to a dedicated MediaPlayer overlay.
    /// </summary>
    public string? PlaybackCanvasName => _mediaCanvas?.Name;

    public float Volume
    {
        get => _audio.GetSystemVolume();
        set => _audio.Volume = value;
    }

    /// <summary>
    ///     Audio sync offset in milliseconds
    ///     Positive = delay audio (use when audio is ahead of video)
    ///     Negative = delay video (use when video is ahead of audio)
    /// </summary>
    public int AudioSyncOffsetMs
    {
        get => _videoPlayer.AudioSyncOffsetMs;
        set => _videoPlayer.AudioSyncOffsetMs = value;
    }

    public static bool FFmpegAvailable => FfmpegCapabilities.IsFFmpegAvailable();
    public static bool FFmpegSmbSupported => FfmpegCapabilities.IsFFmpegSmbSupported();
    public static bool YtDlpAvailable => YouTubeService.IsYtDlpAvailable();

    /// <summary>
    ///     Whether SMB network streaming is supported (via FFmpeg with libsmbclient)
    /// </summary>
    public static bool NetworkStreamingSupported => FFmpegSmbSupported;

    /// <summary>
    ///     Current YouTube video info (if playing YouTube)
    /// </summary>
    public YouTubePlaybackInfo? CurrentYouTubeInfo { get; private set; }

    public void Dispose()
    {
        StopAsync().Wait();
        _videoPlayer.Dispose();
        _audio.Dispose();
    }

    /// <summary>
    ///     Mute all audio
    /// </summary>
    public void Mute()
    {
        _audio.Mute();
    }

    /// <summary>
    ///     Unmute all audio
    /// </summary>
    public void Unmute()
    {
        _audio.Unmute();
    }

    /// <summary>
    ///     Toggle mute state
    /// </summary>
    public void ToggleMute()
    {
        _audio.ToggleMute();
    }

    /// <summary>
    ///     Set volume (0-100)
    /// </summary>
    public void SetVolume(int percent)
    {
        _audio.Volume = percent / 100f;
    }

    /// <summary>
    ///     Play a local video file
    /// </summary>
    public async Task PlayVideoAsync(string videoPath, bool loop = true)
    {
        if (!File.Exists(videoPath))
        {
            Console.WriteLine($"[MEDIA] Video not found: {videoPath}");
            return;
        }

        // Clear network/audio/YouTube state
        NetworkShareName = null;
        NetworkFilePath = null;
        NetworkProtocol = null;
        IsAudioPlayback = false;
        CurrentAudio = null;
        LastPlayedYouTubeUrl = null;
        LastPlayedYouTubeTitle = null;

        await PlayVideoInternalAsync(videoPath, Path.GetFileName(videoPath), loop);
    }


    /// <summary>
    ///     Play a video from a network share
    /// </summary>
    /// <param name="url">The full URL (smb://, http://, ftp://)</param>
    /// <param name="shareName">Display name of the share</param>
    /// <param name="filePath">Path within the share</param>
    /// <param name="protocol">Protocol: "smb", "http", or "ftp"</param>
    /// <param name="loop">Whether to loop playback</param>
    public async Task PlayNetworkVideoAsync(string url, string shareName, string filePath, string protocol,
        bool loop = true)
    {
        // Store network video info, clear audio/YouTube state
        NetworkShareName = shareName;
        NetworkFilePath = filePath;
        NetworkProtocol = protocol.ToLowerInvariant();
        NetworkVideoUrl = url;
        IsAudioPlayback = false;
        CurrentAudio = null;
        LastPlayedYouTubeUrl = null;
        LastPlayedYouTubeTitle = null;

        Console.WriteLine(
            $"[MEDIA] Playing network video: {shareName}/{filePath} (protocol: {NetworkProtocol}, seeking: {SeekingSupported})");
        await PlayVideoInternalAsync(url, Path.GetFileName(filePath), loop);
    }

    /// <summary>
    ///     Play a YouTube video using yt-dlp for URL extraction
    ///     Automatically selects best format based on canvas dimensions to save bandwidth
    /// </summary>
    /// <param name="youtubeUrl">YouTube video URL</param>
    /// <param name="loop">Whether to loop playback</param>
    /// <param name="audioOnly">If true, play audio only without showing video on display</param>
    public async Task<bool> PlayYouTubeVideoAsync(string youtubeUrl, bool loop = false, bool audioOnly = false)
    {
        LastPlaybackError = null;
        
        if (!YtDlpAvailable)
        {
            LastPlaybackError = "yt-dlp not available. Install with: pip install yt-dlp";
            Console.WriteLine($"[MEDIA] {LastPlaybackError}");
            return false;
        }

        if (!YouTubeService.IsYouTubeUrl(youtubeUrl))
        {
            LastPlaybackError = $"Not a valid YouTube URL: {youtubeUrl}";
            Console.WriteLine($"[MEDIA] {LastPlaybackError}");
            return false;
        }

        var ytService = new YouTubeService();

        // ── Audio-only path: extract best audio URL, no canvas ──
        if (audioOnly)
        {
            Console.WriteLine($"[YOUTUBE] Audio-only mode for: {youtubeUrl}");
            var audioInfo = await ytService.GetAudioOnlyPlaybackInfoAsync(youtubeUrl);
            if (audioInfo == null || string.IsNullOrEmpty(audioInfo.AudioUrl))
            {
                LastPlaybackError = "Failed to get audio URL.";
                Console.WriteLine($"[YOUTUBE] {LastPlaybackError}");
                return false;
            }

            await StopAsync();

            CurrentYouTubeInfo = audioInfo;
            LastPlayedYouTubeUrl = youtubeUrl;
            LastPlayedYouTubeTitle = audioInfo.Title;
            NetworkShareName = "YouTube";
            NetworkFilePath = audioInfo.Title;
            NetworkProtocol = "youtube";
            IsAudioPlayback = true;
            CurrentAudio = null;
            _mediaCanvas = null;
            _isRunning = true;

            Console.WriteLine($"[YOUTUBE] Playing audio-only: {audioInfo.Title}");
            await _videoPlayer.PlayAudioOnlyAsync(audioInfo.AudioUrl!, loop);

            if (_videoPlayer.Duration == TimeSpan.Zero && audioInfo.Duration > TimeSpan.Zero)
                _videoPlayer.SetDuration(audioInfo.Duration);

            return true;
        }

        // ── Standard video+audio path ──

        // Get canvas DIMENSIONS before stopping (so current video keeps playing
        // while yt-dlp extracts the URL). Don't acquire the actual canvas yet —
        // StopAsync() will dispose it and cause a race condition.
        var canvasWidth = _mediaCanvas?.Width ?? _width;
        var canvasHeight = _mediaCanvas?.Height ?? _height;

        Console.WriteLine($"[YOUTUBE] Getting playback info for canvas {canvasWidth}x{canvasHeight} (current playback continues)...");

        // Get playback info with optimal format selection - this is the slow yt-dlp call
        // Current video keeps playing during this time
        var playbackInfo = await ytService.GetPlaybackInfoAsync(youtubeUrl, canvasWidth, canvasHeight);

        if (playbackInfo == null || string.IsNullOrEmpty(playbackInfo.VideoUrl))
        {
            LastPlaybackError = "Failed to get video URL. The video may be restricted, private, or unavailable in your region.";
            Console.WriteLine($"[YOUTUBE] {LastPlaybackError}");
            return false;
        }

        // NOW stop current playback - URL is ready, minimize the gap
        await StopAsync();

        ResetStartGate();
        _mediaCanvas = GetOrCreateTargetCanvas();
        HideOwnedOverlayUntilReady();

        CurrentYouTubeInfo = playbackInfo;
        
        // Store original YouTube URL for replay (HLS manifests expire, but original URL can be re-queried)
        LastPlayedYouTubeUrl = youtubeUrl;
        LastPlayedYouTubeTitle = playbackInfo.Title;

        // Clear network/audio state
        NetworkShareName = "YouTube";
        NetworkFilePath = playbackInfo.Title;
        NetworkProtocol = "youtube";
        IsAudioPlayback = false;
        CurrentAudio = null;

        Console.WriteLine($"[YOUTUBE] Playing: {playbackInfo.Title}");
        Console.WriteLine($"[YOUTUBE] Format: {playbackInfo.Width}x{playbackInfo.Height} ({(playbackInfo.IsAdaptive ? "adaptive" : "combined")})");

        _isRunning = true;

        Console.WriteLine($"[MEDIA] Using canvas: {_mediaCanvas.Name} ({_mediaCanvas.Width}x{_mediaCanvas.Height}) (hidden until first frame)");

        // Start MOD music if set
        if (_audioEnabled && ModFilePath != null) _audio.PlayMod(ModFilePath);

        // Play video (audio from video will be handled by the player unless MOD is playing)
        var playVideoAudio = ModFilePath == null && _audioEnabled;

        // For adaptive streams, we need to pass both URLs to FFmpeg
        if (playbackInfo.IsAdaptive && playbackInfo.VideoUrl != playbackInfo.AudioUrl)
        {
            await _videoPlayer.PlayAdaptiveStreamAsync(
                playbackInfo.VideoUrl!, 
                playbackInfo.AudioUrl!, 
                _mediaCanvas, 
                loop, 
                playVideoAudio,
                playbackInfo.Duration);
        }
        else
        {
            await _videoPlayer.PlayAsync(playbackInfo.VideoUrl!, _mediaCanvas, loop, playVideoAudio);
        }

        // Ensure duration is set from yt-dlp metadata if ffprobe couldn't determine it
        if (_videoPlayer.Duration == TimeSpan.Zero && playbackInfo.Duration > TimeSpan.Zero)
        {
            _videoPlayer.SetDuration(playbackInfo.Duration);
        }

        if (!await WaitForPlaybackStartAsync(TimeSpan.FromSeconds(20)))
        {
            LastPlaybackError ??= "Playback failed to start (403/429 or empty stream)";
            Console.WriteLine($"[YOUTUBE] {LastPlaybackError}");
            await StopAsync();
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Internal method to play video from any source
    /// </summary>
    private async Task PlayVideoInternalAsync(string source, string displayName, bool loop)
    {
        // Stop any current playback (but preserve extensions on other canvases)
        await StopAsync();

        Console.WriteLine($"[MEDIA] Playing video: {displayName}");
        ResetStartGate();
        _isRunning = true;

        _mediaCanvas = GetOrCreateTargetCanvas();
        HideOwnedOverlayUntilReady();

        Console.WriteLine($"[MEDIA] Using canvas: {_mediaCanvas.Name} ({_mediaCanvas.Width}x{_mediaCanvas.Height})");

        if (_audioEnabled && ModFilePath != null) _audio.PlayMod(ModFilePath);

        var playVideoAudio = ModFilePath == null && _audioEnabled;
        await _videoPlayer.PlayAsync(source, _mediaCanvas, loop, playVideoAudio);

        if (!await WaitForPlaybackStartAsync(TimeSpan.FromSeconds(15)))
        {
            LastPlaybackError ??= "Playback failed to start";
            Console.WriteLine($"[MEDIA] {LastPlaybackError}");
            await StopAsync();
        }
    }

    // When set (by PlayVideoOnCanvasAsync), playback renders into this exact caller-owned canvas
    // (e.g. a Studio content/rotation step) — bypassing the "MediaPlayer" overlay creation.
    private Canvas? _forcedCanvas;

    /// <summary>
    ///     Plays a video into a specific existing canvas (e.g. a Studio canvas), rather than creating the
    ///     dedicated full-screen "MediaPlayer" overlay. The canvas is not owned/removed by the service.
    /// </summary>
    public async Task PlayVideoOnCanvasAsync(Canvas canvas, string videoPath, bool loop = true)
    {
        _forcedCanvas = canvas;
        try { await PlayVideoAsync(videoPath, loop); }
        finally { _forcedCanvas = null; }
    }

    /// <summary>
    ///     Get existing canvas by name or create a default MediaPlayer canvas
    /// </summary>
    private Canvas GetOrCreateTargetCanvas()
    {
        // A caller forced an exact canvas (Studio content/rotation) — render into it directly.
        if (_forcedCanvas != null) return _forcedCanvas;

        if (MediaPlaybackTarget.UsesOwnedOverlay(TargetCanvasName))
        {
            var existing = _canvasManager.GetCanvasByName(MediaPlaybackTarget.OverlayName);
            if (existing != null)
            {
                Console.WriteLine("[MEDIA] Reusing existing MediaPlayer canvas");
                existing.Hide();
                return existing;
            }
            var created = _canvasManager.GetCanvas(0, 0, _width, _height, 200, MediaPlaybackTarget.OverlayName);
            created.Hide();
            return created;
        }

        // Try to get existing canvas by name
        var existingCanvas = _canvasManager.GetCanvasesByZOrder()
            .FirstOrDefault(c => c.Name == TargetCanvasName);

        if (existingCanvas != null)
        {
            Console.WriteLine($"[MEDIA] Using existing canvas: {TargetCanvasName}");
            return existingCanvas;
        }

        // Canvas not found, fall back to full-screen (reuse existing MediaPlayer canvas)
        Console.WriteLine($"[MEDIA] Canvas '{TargetCanvasName}' not found, using full-screen");
        var fallback = _canvasManager.GetCanvasByName("MediaPlayer");
        if (fallback != null)
        {
            fallback.Hide();
            return fallback;
        }
        var overlay = _canvasManager.GetCanvas(0, 0, _width, _height, 200, "MediaPlayer");
        overlay.Hide();
        return overlay;
    }

    /// <summary>
    ///     Stop playback
    ///     Note: Preserves playlist and last played info for restart/skip functionality
    /// </summary>
    public async Task StopAsync(bool userInitiated = true)
    {
        if (!_isRunning && !_videoPlayer.IsPlaying && _mediaCanvas == null) return;

        Interlocked.Exchange(ref _userInitiatedStop, userInitiated ? 1 : 0);
        Console.WriteLine($"[MEDIA] Stopping... (userInitiated={userInitiated})");

        await _videoPlayer.StopAsync();
        _audio.StopMod();

        TeardownOwnedOverlay();

        // Save last played before clearing (for restart functionality)
        if (CurrentAudio != null)
        {
            LastPlayedAudio = CurrentAudio;
            LastPlayedWasNetwork = NetworkShareName != null;
            // Note: We can't rebuild the URL here as we don't have access to NetworkShareService
            // The URL is stored when PlayNetworkAudioAsync is called
        }
        
        // NOTE: LastPlayedYouTubeUrl and LastPlayedYouTubeTitle are preserved for replay
        // They are set in PlayYouTubeVideoAsync and only cleared when playing non-YouTube content
        
        // Clear CurrentYouTubeInfo but keep LastPlayed values
        CurrentYouTubeInfo = null;

        _isRunning = false;
        IsAudioPlayback = false;
        IsRadioPlaying = false;
        RadioStationName = null;
        CurrentAudio = null;
        NetworkShareName = null;
        NetworkFilePath = null;
        NetworkProtocol = null;
        // NOTE: Don't clear _audioPlaylist or _currentPlaylistIndex
        // This allows skip buttons to work after stop
        Console.WriteLine("[MEDIA] Stopped");
    }

    /// <summary>
    ///     Play an internet radio stream (direct HTTP audio stream URL).
    ///     Starts almost instantly — no yt-dlp extraction needed.
    /// </summary>
    public async Task PlayRadioStreamAsync(string streamUrl, string stationName)
    {
        Console.WriteLine($"[MEDIA] Starting radio stream: \"{stationName}\" → {streamUrl}");

        // Stop any current playback first
        if (_isRunning || _videoPlayer.IsPlaying)
            await StopAsync(userInitiated: false);

        // Stop auto-play if active (radio replaces it)
        if (AutoPlayFavorites)
            StopAutoPlay();

        IsRadioPlaying = true;
        RadioStationName = stationName;
        _isRunning = true;

        // Clear previous state
        CurrentAudio = stationName;
        LastPlayedYouTubeUrl = null;
        LastPlayedYouTubeTitle = null;
        NetworkShareName = null;
        NetworkFilePath = null;
        NetworkProtocol = null;

        // Play directly via the video player's audio-only path (handles HTTP streams with reconnect)
        await _videoPlayer.PlayAudioOnlyAsync(streamUrl, loop: false);
    }

    /// <summary>
    ///     Stop radio stream playback specifically.
    /// </summary>
    public void StopRadio()
    {
        IsRadioPlaying = false;
        RadioStationName = null;
    }

    /// <summary>
    ///     Notify that a canvas was removed - stop playback if it was our target canvas
    /// </summary>
    public async Task NotifyCanvasRemovedAsync(string canvasName)
    {
        // Check if we're playing to this canvas
        if (_mediaCanvas != null && _mediaCanvas.Name == canvasName)
        {
            Console.WriteLine($"[MEDIA] Target canvas '{canvasName}' was removed - stopping playback");
            await StopAsync();
        }
        else if (TargetCanvasName == canvasName)
        {
            Console.WriteLine($"[MEDIA] Target canvas '{canvasName}' was removed - stopping playback");
            await StopAsync();
        }
    }

    /// <summary>
    ///     Pause/Resume playback
    /// </summary>
    public void TogglePause()
    {
        _videoPlayer.TogglePause();
    }

    /// <summary>
    ///     Seek to a specific position
    /// </summary>
    public async Task SeekAsync(TimeSpan position)
    {
        if (!IsRunning)
        {
            Console.WriteLine("[MEDIA] Cannot seek - not playing");
            return;
        }

        // Seeking restarts the FFmpeg process which fires OnPlaybackStopped.
        // Set the advancing guard so auto-play doesn't interpret it as a natural end.
        Interlocked.Exchange(ref _autoPlayAdvancing, 1);
        try
        {
            await _videoPlayer.SeekAsync(position);
        }
        finally
        {
            Interlocked.Exchange(ref _autoPlayAdvancing, 0);
        }
    }

    /// <summary>
    ///     Seek to a percentage of the video (0-100)
    /// </summary>
    public async Task SeekPercentAsync(double percent)
    {
        if (!IsRunning)
        {
            Console.WriteLine("[MEDIA] Cannot seek - not playing");
            return;
        }

        await _videoPlayer.SeekPercentAsync(percent);
    }

    /// <summary>
    ///     Get video info
    /// </summary>
    public async Task<VideoInfo?> GetVideoInfoAsync(string videoPath)
    {
        return await MediaProbeService.GetVideoInfoAsync(videoPath);
    }

    /// <summary>
    ///     Set MOD file to play alongside video (replaces video audio)
    /// </summary>
    public bool SetModFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"[MEDIA] MOD file not found: {filePath}");
            return false;
        }

        ModFilePath = filePath;
        Console.WriteLine($"[MEDIA] MOD file set: {Path.GetFileName(filePath)}");

        // If already playing, start the MOD
        if (_isRunning && _audioEnabled) _audio.PlayMod(ModFilePath);

        return true;
    }

    /// <summary>
    ///     Clear MOD file
    /// </summary>
    public void ClearModFile()
    {
        ModFilePath = null;
        _audio.StopMod();
        Console.WriteLine("[MEDIA] MOD file cleared");
    }

    /// <summary>
    ///     Get list of available video files
    /// </summary>
    public IEnumerable<string> GetAvailableVideos() =>
        MediaLibrary.ListRelative(MediaLibrary.VideoRoot, MediaLibrary.VideoExtensions);

    public IEnumerable<string> GetAvailableModFiles() =>
        MediaLibrary.ListRelative(MediaLibrary.ModRoot, MediaLibrary.ModExtensions);

    public IEnumerable<string> GetAvailableAudioFiles() =>
        MediaLibrary.ListRelative(MediaLibrary.AudioRoot, MediaLibrary.AudioExtensions);

    /// <summary>
    ///     Play an audio file (audio only - no canvas created, no cover art display)
    /// </summary>
    public async Task PlayAudioAsync(string audioPath, bool loop = false)
    {
        if (!File.Exists(audioPath))
        {
            Console.WriteLine($"[MEDIA] Audio not found: {audioPath}");
            return;
        }

        // Stop any current playback
        await StopAsync();

        IsAudioPlayback = true;
        CurrentAudio = Path.GetFileName(audioPath);
        NetworkShareName = null;
        NetworkFilePath = null;
        NetworkProtocol = null;
        _isRunning = true;

        // Store for replay functionality (local audio - no URL needed)
        LastPlayedAudio = CurrentAudio;
        LastPlayedAudioUrl = null;
        LastPlayedWasNetwork = false;
        LastPlayedYouTubeUrl = null;
        LastPlayedYouTubeTitle = null;

        // Build playlist from the current audio folder, starting with this file
        BuildPlaylistFromFile(CurrentAudio);

        Console.WriteLine($"[MEDIA] Playing audio: {CurrentAudio} ({CurrentPlaylistIndex + 1}/{_audioPlaylist.Count})");

        // Audio playback: NO canvas needed - don't show embedded cover art
        // Pass null canvas to VideoPlayer to indicate audio-only mode
        _mediaCanvas = null;

        await _videoPlayer.PlayAudioOnlyAsync(audioPath, loop);
    }

    /// <summary>
    ///     Play audio from a network share (audio only - no canvas)
    /// </summary>
    public async Task PlayNetworkAudioAsync(string url, string shareName, string filePath, bool loop = false)
    {
        // Stop any current playback
        await StopAsync();

        IsAudioPlayback = true;
        CurrentAudio = Path.GetFileName(filePath);
        NetworkShareName = shareName;
        NetworkFilePath = filePath;
        NetworkProtocol = "smb";
        _isRunning = true;

        // Store the URL for replay functionality
        LastPlayedAudioUrl = url;
        LastPlayedWasNetwork = true;
        LastPlayedAudio = CurrentAudio;
        
        // Clear YouTube state - we're playing network audio now
        LastPlayedYouTubeUrl = null;
        LastPlayedYouTubeTitle = null;

        Console.WriteLine($"[MEDIA] Playing network audio: {shareName}/{filePath}");

        // Audio playback: NO canvas needed
        _mediaCanvas = null;

        await _videoPlayer.PlayAudioOnlyAsync(url, loop);
    }

    private bool IsOwnedOverlay(Canvas? canvas) =>
        canvas != null && canvas.Name == "MediaPlayer" && _forcedCanvas == null;

    private void HideOwnedOverlayUntilReady()
    {
        if (!IsOwnedOverlay(_mediaCanvas)) return;
        _mediaCanvas!.Hide();
        _mediaCanvas.Clear();
        Console.WriteLine("[MEDIA] MediaPlayer overlay hidden until the first frame");
    }

    private void RevealOwnedOverlay()
    {
        if (!IsOwnedOverlay(_mediaCanvas)) return;
        _mediaCanvas!.Show();
        Console.WriteLine("[MEDIA] MediaPlayer overlay shown (first frame)");
    }

    private void TeardownOwnedOverlay()
    {
        var canvas = _mediaCanvas;
        if (canvas == null) return;
        try
        {
            canvas.Clear();
            if (IsOwnedOverlay(canvas))
            {
                canvas.Hide();
                _canvasManager.RemoveCanvas(canvas);
                Console.WriteLine("[MEDIA] MediaPlayer overlay removed");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEDIA] Overlay teardown: {ex.Message}");
        }
        _mediaCanvas = null;
    }

    private void ResetStartGate()
    {
        Interlocked.Exchange(ref _gotFirstFrame, 0);
        _startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task<bool> WaitForPlaybackStartAsync(TimeSpan timeout)
    {
        var gate = _startGate;
        if (gate == null) return Interlocked.CompareExchange(ref _gotFirstFrame, 0, 0) == 1;
        var done = await Task.WhenAny(gate.Task, Task.Delay(timeout));
        if (done != gate.Task)
        {
            LastPlaybackError ??= "Playback did not produce a frame in time";
            return false;
        }
        return gate.Task.Result;
    }

    private void OnFirstFrame()
    {
        if (Interlocked.Exchange(ref _gotFirstFrame, 1) == 0)
        {
            RevealOwnedOverlay();
            _startGate?.TrySetResult(true);
        }
    }

    private void OnVideoError(string msg)
    {
        LastPlaybackError = msg;
        Console.WriteLine($"[MEDIA] Error: {msg}");
        if (Interlocked.CompareExchange(ref _gotFirstFrame, 0, 0) == 0)
            _startGate?.TrySetResult(false);
    }

    /// <summary>
    ///     Handle playback stopped event - auto-advance to next track if enabled
    /// </summary>
    private void OnPlaybackStopped()
    {
        // Re-entry guard: prevent concurrent handling from rapid stop events
        if (Interlocked.CompareExchange(ref _handlingStop, 1, 0) != 0)
            return;
        
        try
        {
            var wasUserInitiated = Interlocked.Exchange(ref _userInitiatedStop, 0) != 0;
            var isAdvancing = Interlocked.CompareExchange(ref _autoPlayAdvancing, 0, 0) != 0;
            
            Console.WriteLine($"[MEDIA] Video playback stopped (userInitiated={wasUserInitiated}, autoPlay={AutoPlayFavorites}, advancing={isAdvancing})");
            _isRunning = false;

            // Load never produced a frame (403/429/empty stream): drop the overlay so prior content stays.
            if (Interlocked.CompareExchange(ref _gotFirstFrame, 0, 0) == 0)
            {
                _startGate?.TrySetResult(false);
                TeardownOwnedOverlay();
            }

            // Auto-play favorites takes priority
            if (AutoPlayFavorites && !wasUserInitiated && !isAdvancing)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(800); // Brief pause between tracks
                        await AdvanceAutoPlayAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MEDIA] Auto-play advance error: {ex.Message}");
                        StopAutoPlay();
                    }
                });
                return;
            }
            
            // Stop auto-play if user initiated stop
            if (AutoPlayFavorites && wasUserInitiated)
            {
                Console.WriteLine("[MEDIA] User stopped playback, disabling auto-play");
                StopAutoPlay();
            }

            // Auto-advance to next track for audio playback (not looping videos)
            if (IsAudioPlayback && AutoAdvance && _audioPlaylist.Count > 0 && !_videoPlayer.IsLooping)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500); // Brief pause between tracks
                    await PlayNextAsync();
                });
        }
        finally
        {
            Interlocked.Exchange(ref _handlingStop, 0);
        }
    }

    /// <summary>
    ///     Build playlist from available audio files, starting with the specified file
    /// </summary>
    private void BuildPlaylistFromFile(string startFile)
    {
        var allFiles = GetAvailableAudioFiles().ToList();

        if (ShuffleMode)
        {
            // Shuffle the playlist but start with the selected file
            _audioPlaylist = allFiles.Where(f => f != startFile).OrderBy(_ => _shuffleRandom.Next()).ToList();
            _audioPlaylist.Insert(0, startFile);
        }
        else
        {
            _audioPlaylist = allFiles;
        }

        CurrentPlaylistIndex = _audioPlaylist.IndexOf(startFile);
        Console.WriteLine(
            $"[MEDIA] Playlist built with {_audioPlaylist.Count} tracks, starting at index {CurrentPlaylistIndex}");
    }

    /// <summary>
    ///     Play the next track in the playlist
    /// </summary>
    public async Task<bool> PlayNextAsync()
    {
        if (_audioPlaylist.Count == 0)
        {
            Console.WriteLine("[MEDIA] No playlist - cannot skip to next");
            return false;
        }

        int nextIndex;
        if (ShuffleMode && !RepeatMode)
        {
            // In shuffle mode, pick a random track (excluding current)
            var availableIndices = Enumerable.Range(0, _audioPlaylist.Count)
                .Where(i => i != CurrentPlaylistIndex)
                .ToList();

            if (availableIndices.Count == 0)
            {
                Console.WriteLine("[MEDIA] No more tracks in shuffle mode");
                return false;
            }

            nextIndex = availableIndices[_shuffleRandom.Next(availableIndices.Count)];
        }
        else
        {
            nextIndex = CurrentPlaylistIndex + 1;

            if (nextIndex >= _audioPlaylist.Count)
            {
                if (RepeatMode)
                {
                    nextIndex = 0; // Loop back to start
                }
                else
                {
                    Console.WriteLine("[MEDIA] End of playlist");
                    return false;
                }
            }
        }

        var nextFile = _audioPlaylist[nextIndex];
        var filePath = Path.Combine(AppPaths.AudioDir, nextFile);

        Console.WriteLine($"[MEDIA] Playing next track: {nextFile} (index {nextIndex})");

        // Update index before playing to avoid re-triggering
        CurrentPlaylistIndex = nextIndex;
        CurrentAudio = nextFile;

        // Stop current and play next
        await _videoPlayer.StopAsync();

        IsAudioPlayback = true;
        _isRunning = true;
        _mediaCanvas = null; // Audio-only: no canvas needed

        await _videoPlayer.PlayAudioOnlyAsync(filePath);
        return true;
    }

    /// <summary>
    ///     Play the previous track in the playlist
    /// </summary>
    public async Task<bool> PlayPreviousAsync()
    {
        if (_audioPlaylist.Count == 0)
        {
            Console.WriteLine("[MEDIA] No playlist - cannot skip to previous");
            return false;
        }

        // If more than 3 seconds into the track, restart it instead of going to previous
        if (_videoPlayer.Position.TotalSeconds > 3)
        {
            Console.WriteLine("[MEDIA] Restarting current track");
            await _videoPlayer.SeekAsync(TimeSpan.Zero);
            return true;
        }

        var prevIndex = CurrentPlaylistIndex - 1;

        if (prevIndex < 0)
        {
            if (RepeatMode)
            {
                prevIndex = _audioPlaylist.Count - 1; // Go to last track
            }
            else
            {
                // Just restart the current track
                Console.WriteLine("[MEDIA] Start of playlist - restarting track");
                await _videoPlayer.SeekAsync(TimeSpan.Zero);
                return true;
            }
        }

        var prevFile = _audioPlaylist[prevIndex];
        var filePath = Path.Combine(AppPaths.AudioDir, prevFile);

        Console.WriteLine($"[MEDIA] Playing previous track: {prevFile} (index {prevIndex})");

        // Update index before playing
        CurrentPlaylistIndex = prevIndex;
        CurrentAudio = prevFile;

        // Stop current and play previous
        await _videoPlayer.StopAsync();

        IsAudioPlayback = true;
        _isRunning = true;
        _mediaCanvas = null; // Audio-only: no canvas needed

        await _videoPlayer.PlayAudioOnlyAsync(filePath);
        return true;
    }

    /// <summary>
    ///     Clear the playlist
    /// </summary>
    public void ClearPlaylist()
    {
        _audioPlaylist.Clear();
        CurrentPlaylistIndex = -1;
    }

    #region Auto-Play Favorites

    /// <summary>
    ///     Start auto-playing through a list of favorites
    /// </summary>
    public void StartAutoPlay(List<AutoPlayItem> items, bool shuffle = false)
    {
        if (items.Count == 0)
        {
            Console.WriteLine("[AUTOPLAY] No items to play");
            return;
        }

        _autoPlayQueue = shuffle 
            ? items.OrderBy(_ => _shuffleRandom.Next()).ToList() 
            : new List<AutoPlayItem>(items);
        _autoPlayCurrentIndex = -1; // Will be set to 0 when first item starts
        AutoPlayFavorites = true;
        Interlocked.Exchange(ref _userInitiatedStop, 0);
        
        Console.WriteLine($"[AUTOPLAY] Started with {_autoPlayQueue.Count} items (shuffle={shuffle})");
    }

    /// <summary>
    ///     Stop auto-play mode
    /// </summary>
    public void StopAutoPlay()
    {
        AutoPlayFavorites = false;
        AutoPlayRefillCallback = null;
        _autoPlayQueue.Clear();
        _autoPlayCurrentIndex = -1;
        Console.WriteLine("[AUTOPLAY] Stopped");
    }

    /// <summary>
    ///     Advance to the next item in the auto-play queue.
    ///     Called internally when a track ends naturally.
    /// </summary>
    private async Task AdvanceAutoPlayAsync()
    {
        var nextIndex = _autoPlayCurrentIndex + 1;
        if (nextIndex >= _autoPlayQueue.Count)
        {
            // Try to refill the queue (radio/endless mode)
            if (AutoPlayRefillCallback != null)
            {
                try
                {
                    Console.WriteLine("[AUTOPLAY] Queue empty — requesting refill...");
                    var newItems = await AutoPlayRefillCallback();
                    if (newItems.Count > 0)
                    {
                        _autoPlayQueue.AddRange(newItems);
                        Console.WriteLine($"[AUTOPLAY] Refilled with {newItems.Count} items (total: {_autoPlayQueue.Count})");
                        // nextIndex is still valid — continue below
                    }
                    else
                    {
                        Console.WriteLine("[AUTOPLAY] Refill returned empty — stopping");
                        StopAutoPlay();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AUTOPLAY] Refill error: {ex.Message}");
                    StopAutoPlay();
                    return;
                }
            }
            else
            {
                Console.WriteLine("[AUTOPLAY] Reached end of queue");
                StopAutoPlay();
                return;
            }
        }

        _autoPlayCurrentIndex = nextIndex;
        Interlocked.Exchange(ref _autoPlayAdvancing, 1); // Guard against re-entry from StopAsync inside play methods
        
        var item = _autoPlayQueue[nextIndex];
        Console.WriteLine($"[AUTOPLAY] Advancing to #{nextIndex + 1}/{_autoPlayQueue.Count}: {item.Name}");

        // Show loading screen on canvas
        ShowAutoPlayLoadingScreen(item.Name, nextIndex + 1, _autoPlayQueue.Count);

        try
        {
            // Signal the PlayAutoPlayItemCallback to play this item
            if (PlayAutoPlayItemCallback != null)
            {
                await PlayAutoPlayItemCallback(item);
            }
            else
            {
                Console.WriteLine("[AUTOPLAY] No play callback registered, stopping auto-play");
                StopAutoPlay();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUTOPLAY] Track failed: {ex.Message} — skipping to next");
            Interlocked.Exchange(ref _autoPlayAdvancing, 0);

            // Skip to next track (with a small limit to avoid infinite loops of failures)
            const int maxConsecutiveFailures = 5;
            var failures = 1;
            while (AutoPlayFavorites && failures < maxConsecutiveFailures)
            {
                await Task.Delay(500);
                var skipIndex = _autoPlayCurrentIndex + 1;
                if (skipIndex >= _autoPlayQueue.Count)
                {
                    // Try refill, or stop
                    if (AutoPlayRefillCallback != null)
                    {
                        try
                        {
                            var newItems = await AutoPlayRefillCallback();
                            if (newItems.Count > 0)
                                _autoPlayQueue.AddRange(newItems);
                            else
                                break;
                        }
                        catch { break; }
                    }
                    else break;

                    if (skipIndex >= _autoPlayQueue.Count) break;
                }

                _autoPlayCurrentIndex = skipIndex;
                var nextItem = _autoPlayQueue[skipIndex];
                Console.WriteLine($"[AUTOPLAY] Retry #{failures + 1}: {nextItem.Name}");

                try
                {
                    Interlocked.Exchange(ref _autoPlayAdvancing, 1);
                    if (PlayAutoPlayItemCallback != null)
                        await PlayAutoPlayItemCallback(nextItem);
                    // Success — exit the retry loop
                    Interlocked.Exchange(ref _autoPlayAdvancing, 0);
                    return;
                }
                catch (Exception retryEx)
                {
                    Console.WriteLine($"[AUTOPLAY] Track failed: {retryEx.Message} — skipping");
                    Interlocked.Exchange(ref _autoPlayAdvancing, 0);
                    failures++;
                }
            }

            if (failures >= maxConsecutiveFailures)
            {
                Console.WriteLine($"[AUTOPLAY] {maxConsecutiveFailures} consecutive failures — stopping");
                StopAutoPlay();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _autoPlayAdvancing, 0);
        }
    }

    /// <summary>
    ///     Skip to next track in auto-play queue
    /// </summary>
    public async Task SkipAutoPlayAsync()
    {
        if (!AutoPlayFavorites)
        {
            Console.WriteLine("[AUTOPLAY] Not in auto-play mode");
            return;
        }

        Console.WriteLine("[AUTOPLAY] Skipping to next track");
        Interlocked.Exchange(ref _userInitiatedStop, 0);
        Interlocked.Exchange(ref _autoPlayAdvancing, 1); // Prevent OnPlaybackStopped from also advancing
        
        // Stop current playback without marking as user-initiated
        if (_isRunning || _videoPlayer.IsPlaying)
        {
            await _videoPlayer.StopAsync();
            _audio.StopMod();
            _isRunning = false;
        }

        Interlocked.Exchange(ref _autoPlayAdvancing, 0);
        await AdvanceAutoPlayAsync();
    }

    /// <summary>
    ///     Callback to play a specific auto-play item. Set by the API layer.
    /// </summary>
    public Func<AutoPlayItem, Task>? PlayAutoPlayItemCallback { get; set; }

    /// <summary>
    ///     Callback to refill the auto-play queue when it empties (for radio/endless mode).
    ///     Should return new items to append. Return empty list to stop.
    /// </summary>
    public Func<Task<List<AutoPlayItem>>>? AutoPlayRefillCallback { get; set; }

    /// <summary>
    ///     Show a loading screen on the LED canvas during auto-play transitions
    /// </summary>
    private void ShowAutoPlayLoadingScreen(string trackName, int currentNum, int total)
    {
        try
        {
            // Reuse existing media canvas if available, otherwise find or create one
            var canvas = _mediaCanvas
                ?? _canvasManager.GetCanvasByName("MediaPlayer")
                ?? _canvasManager.GetCanvas(0, 0, _width, _height, 200, "MediaPlayer");
            canvas.Clear();

            // Dark background
            canvas.DrawRect(0, 0, _width, _height, new SKColor(10, 10, 30), SKPaintStyle.Fill);

            // "Up Next" label
            var labelColor = new SKColor(100, 200, 255);
            canvas.DrawBdfText("Up Next", 2, 2, labelColor);

            // Track name - truncate if too long
            var nameColor = SKColors.White;
            var displayName = trackName.Length > 20 ? trackName[..17] + "..." : trackName;
            canvas.DrawBdfText(displayName, 2, 16, nameColor);

            // Progress counter
            var counterColor = new SKColor(150, 150, 150);
            canvas.DrawBdfText($"{currentNum} / {total}", 2, _height - 12, counterColor);

            // Small loading indicator bar
            var barWidth = (int)((float)currentNum / total * (_width - 4));
            canvas.DrawRect(2, _height - 3, barWidth, 2, new SKColor(0, 180, 255), SKPaintStyle.Fill);
            
            canvas.Show();
            _mediaCanvas = canvas;

            Console.WriteLine("[AUTOPLAY] Loading screen displayed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUTOPLAY] Failed to show loading screen: {ex.Message}");
        }
    }

    /// <summary>
    ///     Set the auto-play index (used when starting playback of first item)
    /// </summary>
    public void SetAutoPlayIndex(int index)
    {
        _autoPlayCurrentIndex = index;
    }

    #endregion
}

/// <summary>
///     Represents a single item in the auto-play queue
/// </summary>
public class AutoPlayItem
{
    public string FavoriteId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = ""; // "youtube", "network-audio", "network-video", "local-video", "local-audio", "mod"
    public string? Url { get; set; }
    public string? FilePath { get; set; }
    public double AvSyncOffset { get; set; }
    public string? ScaleFilter { get; set; }
}

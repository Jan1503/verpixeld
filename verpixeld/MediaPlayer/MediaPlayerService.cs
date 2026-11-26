using CanvasManagement.Interfaces;
using CanvasManagement;
using SkiaSharp;
using verpixeld.Configuration;
using PixTone;
using verpixeld.MediaPlayer.Audio;

namespace verpixeld.MediaPlayer;

/// <summary>
///     Media player service for playing video and audio content on the LED matrix
///     Supports local files, network shares (SMB), and streaming
/// </summary>
[LogTag("MEDIA")]
public class MediaPlayerService : IDisposable
{
    private readonly ILogger<MediaPlayerService> _log;

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

    public MediaPlayerService(CanvasManager canvasManager, IAudioOutputService audioOutputService,
        PixToneOutputService vap1, int width, int height, ILogger<MediaPlayerService> log)
    {
        _log = log;
        _canvasManager = canvasManager;
        _audioOutputService = audioOutputService;
        _width = width;
        _height = height;
        _audio = new AlsaAudioService();
        _videoPlayer = new VideoPlayer(width, height, audioOutputService, vap1);

        _videoPlayer.OnPlaybackStarted += () => _log.LogInformation("Video playback started");
        _videoPlayer.OnPlaybackStopped += OnPlaybackStopped;
        _videoPlayer.OnFirstFrame += OnFirstFrame;
        _videoPlayer.OnError += OnVideoError;
        _canvasManager.DisplayResized += OnDisplayResized;
        LoadLayerRect();
        _ = Task.Run(WatchCanvasAsync);

        _log.LogInformation("Initialized for {Width}x{Height} display", width, height);
        _log.LogInformation("FFmpeg available: {FFmpegAvailable}", FFmpegAvailable);
        _log.LogInformation("FFmpeg HW accel (V4L2 M2M): {IsHwAccelAvailable}", FfmpegCapabilities.IsHwAccelAvailable());
        _log.LogInformation("FFmpeg SMB support: {FFmpegSmbSupported}", FFmpegSmbSupported);
        _log.LogInformation("yt-dlp available: {YtDlpAvailable}", YtDlpAvailable);
        _log.LogInformation("MOD player available: {ModPlayerAvailable}", ModPlayerAvailable);
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
    /// <summary>File names of the playlist (the folder of the track that was started).</summary>
    public IReadOnlyList<string> AudioPlaylist => _audioPlaylist.Select(Path.GetFileName).ToList()!;
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
    /// <summary>Stream address of the radio station that is playing, for the now-playing reader.</summary>
    public string? RadioStreamUrl { get; private set; }

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
    ///     Target canvas for video playback. Empty (the default) or "MediaPlayer" is the media
    ///     player's own full-wall layer above the layout. Any other name, including Main, is that canvas.
    /// </summary>
    public string? TargetCanvasName { get; set; }

    /// <summary>
    ///     Canvas that is actually receiving video frames right now.
    /// </summary>
    public string? PlaybackCanvasName => _mediaCanvas?.Name;

    public float Volume
    {
        get => _audio.GetSystemVolume();
        set => _audio.Volume = value;
    }

    /// <summary>
    ///     Audio sync offset in milliseconds.
    ///     Positive = delay audio (use when audio is ahead of video).
    ///     Negative = delay video (use when video is ahead of audio).
    /// </summary>
    public int AudioSyncOffsetMs
    {
        get => _videoPlayer.AudioSyncOffsetMs;
        set => _videoPlayer.AudioSyncOffsetMs = value;
    }

    public int AudioStreamIndex
    {
        get => _videoPlayer.AudioStreamIndex;
        set => _videoPlayer.AudioStreamIndex = Math.Clamp(value, 0, 63);
    }

    /// <summary>Full path or address of what is playing (for favorites and the track list).</summary>
    public string? CurrentSource => _videoPlayer.CurrentSource;

    // How to start the last thing again (Play on a stopped player): any source, not just a library file.
    private Func<Task>? _replay;

    public bool CanReplay => _replay != null;

    /// <summary>Starts what played last, from the beginning, with the same audio track.</summary>
    public async Task<bool> ReplayAsync()
    {
        var replay = _replay;
        if (replay == null) return false;
        PrepareAudioTrack(AudioStreamIndex);
        await replay();
        return true;
    }

    /// <summary>
    ///     Studio moved or resized the media layer. The layer is the media player's own canvas (the
    ///     layout manager does not know it), so it is rebuilt here; a playing video follows within a second.
    /// </summary>
    public void ResizeLayer(int x, int y, int width, int height)
    {
        var name = MediaPlaybackTarget.OverlayName;
        var old = _canvasManager.GetCanvasByName(name);
        if (old == null)
        {
            _layerRect = null;
            RememberRect(x, y, width, height);
            return;
        }
        if (old.Width == width && old.Height == height)
        {
            _canvasManager.MoveCanvas(old, x, y);
            RememberRect(x, y, width, height);
            return;
        }
        var zOrder = old.ZOrder;
        var opacity = old.Opacity;
        var brightness = old.Brightness;
        var hidden = old.IsHidden;
        var transparent = old.TransparentBackground;
        var created = _canvasManager.GetCanvas(x, y, width, height, zOrder, name + "~");
        _canvasManager.RemoveCanvas(old);
        _canvasManager.RenameCanvas(created, name);
        created.Opacity = opacity;
        created.Brightness = brightness;
        created.TransparentBackground = transparent;
        if (hidden) created.Hide();
        else created.Show();
        if (ReferenceEquals(_mediaCanvas, old)) _mediaCanvas = created;
        RememberRect(x, y, width, height);
    }

    private void RememberRect(int x, int y, int width, int height)
    {
        var r = (x, y, width, height);
        if (_layerRect == r) return;
        _layerRect = r;
        try
        {
            var root = AppSettingsStore.Load();
            var section = AppSettingsStore.Section(root, "MediaLayer");
            AppSettingsStore.Set(section, "X", System.Text.Json.Nodes.JsonValue.Create(x));
            AppSettingsStore.Set(section, "Y", System.Text.Json.Nodes.JsonValue.Create(y));
            AppSettingsStore.Set(section, "Width", System.Text.Json.Nodes.JsonValue.Create(width));
            AppSettingsStore.Set(section, "Height", System.Text.Json.Nodes.JsonValue.Create(height));
            AppSettingsStore.Save(root);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not save the media layer: {Message}", ex.Message);
        }
        _log.LogInformation("Media layer at {X},{Y} {Width}x{Height}", x, y, width, height);
    }

    private string? _tracksFor;
    private IReadOnlyList<AudioTrackChoice> _tracks = Array.Empty<AudioTrackChoice>();

    /// <summary>Audio tracks of what is playing now (local file, share or stream), probed once per file.</summary>
    public async Task<IReadOnlyList<AudioTrackChoice>> CurrentAudioTracksAsync()
    {
        var source = _videoPlayer.CurrentSource;
        if (string.IsNullOrWhiteSpace(source) || !IsRunning) return Array.Empty<AudioTrackChoice>();
        if (source == _tracksFor) return _tracks;
        var tracks = await MediaProbeService.ListAudioTracksAsync(source);
        _tracksFor = source;
        _tracks = tracks;
        _log.LogInformation("{Count} audio tracks", tracks.Count);
        return tracks;
    }

    /// <summary>Switch audio stream. Restarts the current file at the same position when one is playing.</summary>
    public async Task SetAudioStreamIndexAsync(int index)
    {
        AudioStreamIndex = index;
        _log.LogInformation("Audio track {AudioStreamIndex}", AudioStreamIndex);
        if (_videoPlayer.IsPlaying && _videoPlayer.CurrentVideo != null)
            await _videoPlayer.SeekAsync(_videoPlayer.Position);
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
        _canvasManager.DisplayResized -= OnDisplayResized;
        StopAsync().Wait();
        _videoPlayer.Dispose();
        _audio.Dispose();
    }

    private void OnDisplayResized(object? sender, DisplayResizedEventArgs e)
    {
        _width = e.Width;
        _height = e.Height;
        if (_mediaCanvas == null || _forcedCanvas != null) return;
        if (!MediaPlaybackTarget.UsesOwnedOverlay(TargetCanvasName)) return;
        if (_isRunning || _videoPlayer.IsPlaying) return;
        _mediaCanvas = FullWallOverlay();
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
        _replay = () => PlayVideoAsync(videoPath, loop);
        if (!File.Exists(videoPath))
        {
            _log.LogWarning("Video not found: {VideoPath}", videoPath);
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
        _replay = () => PlayNetworkVideoAsync(url, shareName, filePath, protocol, loop);
        // Store network video info, clear audio/YouTube state
        NetworkShareName = shareName;
        NetworkFilePath = filePath;
        NetworkProtocol = protocol.ToLowerInvariant();
        NetworkVideoUrl = url;
        IsAudioPlayback = false;
        CurrentAudio = null;
        LastPlayedYouTubeUrl = null;
        LastPlayedYouTubeTitle = null;

        _log.LogInformation("Playing network video: {ShareName}/{FilePath} (protocol: {NetworkProtocol}, seeking: {SeekingSupported})", shareName, filePath, NetworkProtocol, SeekingSupported);
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
        _replay = () => PlayYouTubeVideoAsync(youtubeUrl, loop, audioOnly);
        LastPlaybackError = null;
        
        if (!YtDlpAvailable)
        {
            LastPlaybackError = "yt-dlp not available. Install with: pip install yt-dlp";
            _log.LogError("{LastPlaybackError}", LastPlaybackError);
            return false;
        }

        if (!YouTubeService.IsYouTubeUrl(youtubeUrl))
        {
            LastPlaybackError = $"Not a valid YouTube URL: {youtubeUrl}";
            _log.LogError("{LastPlaybackError}", LastPlaybackError);
            return false;
        }

        var ytService = new YouTubeService();

        // ── Audio-only path: extract best audio URL, no canvas ──
        if (audioOnly)
        {
            _log.LogInformation("[YOUTUBE] Audio-only mode for: {YoutubeUrl}", youtubeUrl);
            var audioInfo = await ytService.GetAudioOnlyPlaybackInfoAsync(youtubeUrl);
            if (audioInfo == null || string.IsNullOrEmpty(audioInfo.AudioUrl))
            {
                LastPlaybackError = "Failed to get audio URL.";
                _log.LogError("[YOUTUBE] {LastPlaybackError}", LastPlaybackError);
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

            _log.LogInformation("[YOUTUBE] Playing audio-only: {Title}", audioInfo.Title);
            _videoPlayer.SetBoardTitle(audioInfo.Title);
            await _videoPlayer.PlayAudioOnlyAsync(audioInfo.AudioUrl!, loop);
            _videoPlayer.SetBoardCover(audioInfo.Thumbnail);

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

        _log.LogInformation("[YOUTUBE] Getting playback info for canvas {CanvasWidth}x{CanvasHeight} (current playback continues)...", canvasWidth, canvasHeight);

        // Get playback info with optimal format selection - this is the slow yt-dlp call
        // Current video keeps playing during this time
        var playbackInfo = await ytService.GetPlaybackInfoAsync(youtubeUrl, canvasWidth, canvasHeight);

        if (playbackInfo == null || string.IsNullOrEmpty(playbackInfo.VideoUrl))
        {
            LastPlaybackError = "Failed to get video URL. The video may be restricted, private, or unavailable in your region.";
            _log.LogError("[YOUTUBE] {LastPlaybackError}", LastPlaybackError);
            return false;
        }

        // NOW stop current playback - URL is ready, minimize the gap
        await StopAsync(userInitiated: false, keepOverlay: true);

        ResetStartGate();
        var previousCanvas = _mediaCanvas;
        _mediaCanvas = GetOrCreateTargetCanvas();
        ReleasePreviousCanvas(previousCanvas);
        HideOwnedOverlayUntilReady(keepLastFrame: true);

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

        _log.LogInformation("[YOUTUBE] Playing: {Title}", playbackInfo.Title);
        _log.LogInformation("[YOUTUBE] Format: {Width}x{Height} ({Value})", playbackInfo.Width, playbackInfo.Height, (playbackInfo.IsAdaptive ? "adaptive" : "combined"));

        _isRunning = true;
        _videoPlayer.SetBoardTitle(playbackInfo.Title);

        _log.LogInformation("Using canvas: {Name} ({Width}x{Height}) (hidden until first frame)", _mediaCanvas.Name, _mediaCanvas.Width, _mediaCanvas.Height);

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
        _videoPlayer.SetBoardCover(playbackInfo.Thumbnail);

        // Ensure duration is set from yt-dlp metadata if ffprobe couldn't determine it
        if (_videoPlayer.Duration == TimeSpan.Zero && playbackInfo.Duration > TimeSpan.Zero)
        {
            _videoPlayer.SetDuration(playbackInfo.Duration);
        }

        if (!await WaitForPlaybackStartAsync(TimeSpan.FromSeconds(20)))
        {
            LastPlaybackError ??= "Playback failed to start (403/429 or empty stream)";
            _log.LogError("[YOUTUBE] {LastPlaybackError}", LastPlaybackError);
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
        await StopAsync(userInitiated: false, keepOverlay: true);

        _log.LogInformation("Playing video: {DisplayName}", displayName);
        ResetStartGate();
        _isRunning = true;

        var previousCanvas = _mediaCanvas;
        _mediaCanvas = GetOrCreateTargetCanvas();
        ReleasePreviousCanvas(previousCanvas);
        HideOwnedOverlayUntilReady(keepLastFrame: true);

        _log.LogInformation("Using canvas: {Name} ({Width}x{Height})", _mediaCanvas.Name, _mediaCanvas.Width, _mediaCanvas.Height);

        if (_audioEnabled && ModFilePath != null) _audio.PlayMod(ModFilePath);

        var playVideoAudio = ModFilePath == null && _audioEnabled;
        _videoPlayer.SetBoardTitle(null);
        await _videoPlayer.PlayAsync(source, _mediaCanvas, loop, playVideoAudio);
        _videoPlayer.SetBoardCover(source);

        if (!await WaitForPlaybackStartAsync(TimeSpan.FromSeconds(15)))
        {
            LastPlaybackError ??= "Playback failed to start";
            _log.LogError("{LastPlaybackError}", LastPlaybackError);
            await StopAsync();
        }
    }

    // When set (by PlayVideoOnCanvasAsync), playback renders into this exact caller-owned canvas
    // (e.g. a Studio content/rotation step) — bypassing the "MediaPlayer" overlay creation.
    private Canvas? _forcedCanvas;

    // The video plays into a canvas a caller handed over (Studio step): its life is the caller's.
    private bool _canvasFromCaller;

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
        _canvasFromCaller = _forcedCanvas != null;
        if (_forcedCanvas != null) return _forcedCanvas;

        var wanted = string.IsNullOrWhiteSpace(TargetCanvasName) ? MediaPlaybackTarget.OverlayName : TargetCanvasName.Trim();
        if (!MediaPlaybackTarget.UsesOwnedOverlay(wanted))
        {
            var existingCanvas = _canvasManager.GetCanvasesByZOrder()
                .FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase));

            if (existingCanvas != null)
            {
                _log.LogInformation("Using canvas {Name} {Width}x{Height} at {XPos},{YPos}", existingCanvas.Name, existingCanvas.Width, existingCanvas.Height, existingCanvas.XPos, existingCanvas.YPos);
                return existingCanvas;
            }

            _log.LogWarning("Canvas '{Wanted}' not found, using full-screen", wanted);
        }

        return FullWallOverlay();
    }

    /// <summary>
    ///     The Media tab overlay follows the live wall. A canvas created at the old 256×128 size
    ///     would otherwise stay on the first panel after the wall grows.
    /// </summary>
    private Canvas FullWallOverlay()
    {
        var (x, y, w, h) = LayerRect();
        var name = MediaPlaybackTarget.OverlayName;
        var existing = _canvasManager.GetCanvasByName(name);
        if (existing != null && existing.Width == w && existing.Height == h)
        {
            if (existing.XPos != x || existing.YPos != y) _canvasManager.MoveCanvas(existing, x, y);
            existing.Hide();
            return existing;
        }

        if (existing != null)
        {
            _log.LogInformation("Replacing {Width}x{Height} media layer with {W}x{H}", existing.Width, existing.Height, w, h);
            _canvasManager.RemoveCanvas(existing);
        }

        var created = _canvasManager.GetCanvas(x, y, w, h, 200, name);
        created.Hide();
        return created;
    }

    /// <summary>
    ///     Stop playback
    ///     Note: Preserves playlist and last played info for restart/skip functionality
    /// </summary>
    public async Task StopAsync(bool userInitiated = true, bool keepOverlay = false)
    {
        if (!_isRunning && !_videoPlayer.IsPlaying && _mediaCanvas == null) return;

        Interlocked.Exchange(ref _userInitiatedStop, userInitiated ? 1 : 0);
        _log.LogInformation("Stopping... (userInitiated={UserInitiated}, keepOverlay={KeepOverlay})", userInitiated, keepOverlay);

        await _videoPlayer.StopAsync();
        _audio.StopMod();

        if (!keepOverlay)
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
        RadioStreamUrl = null;
        CurrentAudio = null;
        NetworkShareName = null;
        NetworkFilePath = null;
        NetworkProtocol = null;
        // NOTE: Don't clear _audioPlaylist or _currentPlaylistIndex
        // This allows skip buttons to work after stop
        _log.LogInformation("Stopped");
    }

    /// <summary>
    ///     Play an internet radio stream (direct HTTP audio stream URL).
    ///     Starts almost instantly — no yt-dlp extraction needed.
    /// </summary>
    public async Task PlayRadioStreamAsync(string streamUrl, string stationName, string? coverUrl = null)
    {
        _replay = () => PlayRadioStreamAsync(streamUrl, stationName, coverUrl);
        _log.LogInformation("Starting radio stream: \"{StationName}\" → {StreamUrl}", stationName, streamUrl);

        // Stop any current playback first
        if (_isRunning || _videoPlayer.IsPlaying)
            await StopAsync(userInitiated: false);

        // Stop auto-play if active (radio replaces it)
        if (AutoPlayFavorites)
            StopAutoPlay();

        IsRadioPlaying = true;
        RadioStationName = stationName;
        RadioStreamUrl = streamUrl;
        _isRunning = true;

        // Clear previous state
        CurrentAudio = stationName;
        LastPlayedYouTubeUrl = null;
        LastPlayedYouTubeTitle = null;
        NetworkShareName = null;
        NetworkFilePath = null;
        NetworkProtocol = null;

        // Play directly via the video player's audio-only path (handles HTTP streams with reconnect)
        _videoPlayer.SetBoardTitle(stationName);
        _videoPlayer.SetBoardCover(string.IsNullOrWhiteSpace(coverUrl) ? null : coverUrl);
        await _videoPlayer.PlayAudioOnlyAsync(streamUrl, loop: false);
    }

    /// <summary>
    ///     Stop radio stream playback specifically.
    /// </summary>
    public void StopRadio()
    {
        IsRadioPlaying = false;
        RadioStationName = null;
        RadioStreamUrl = null;
    }

    /// <summary>
    ///     Notify that a canvas was removed - stop playback if it was our target canvas
    /// </summary>
    public async Task NotifyCanvasRemovedAsync(string canvasName)
    {
        if (TargetCanvasName == canvasName) TargetCanvasName = null;
        if (_mediaCanvas != null && _mediaCanvas.Name == canvasName && !IsOwnedOverlay(_mediaCanvas))
        {
            if (_canvasFromCaller)
            {
                _log.LogInformation("Canvas '{CanvasName}' was removed - stopping playback", canvasName);
                await StopAsync();
            }
            else
            {
                await MoveToOwnLayerAsync($"Canvas '{canvasName}' was removed");
            }
        }
    }

    /// <summary>
    ///     Every second while a video plays: is its canvas still on the wall? A layout load, a new
    ///     Main or a deleted canvas used to leave the film running without a picture. Now the video
    ///     moves to the media player's own layer at the same position (or that layer comes back).
    /// </summary>
    private async Task WatchCanvasAsync()
    {
        while (true)
        {
            await Task.Delay(1000).ConfigureAwait(false);
            try
            {
                var canvas = _mediaCanvas;
                if (canvas == null || _canvasFromCaller || !_videoPlayer.IsPlaying) continue;
                var current = _canvasManager.GetCanvasByName(canvas.Name);
                if (ReferenceEquals(current, canvas))
                {
                    // Moved in Studio: remember where. Resized in place: scale the video to it.
                    if (IsOwnedOverlay(canvas) && !canvas.IsHidden) RememberLayer(canvas);
                    if (canvas.Width != _videoPlayer.TargetWidth || canvas.Height != _videoPlayer.TargetHeight)
                        await _videoPlayer.MoveToCanvasAsync(canvas);
                    continue;
                }
                if (current != null && IsOwnedOverlay(canvas))
                {
                    // Studio resizes by building a new canvas with the same name: follow it.
                    _log.LogInformation("Media layer resized in Studio to {Width}x{Height}", current.Width, current.Height);
                    RememberLayer(current);
                    _mediaCanvas = current;
                    current.Show();
                    await _videoPlayer.MoveToCanvasAsync(current);
                    continue;
                }
                await MoveToOwnLayerAsync(IsOwnedOverlay(canvas)
                    ? "The media layer was removed"
                    : $"Canvas '{canvas.Name}' is gone");
            }
            catch (Exception ex)
            {
                _log.LogWarning("canvas watch: {Message}", ex.Message);
            }
        }
    }

    private readonly SemaphoreSlim _moveGate = new(1, 1);

    // Where the media layer sits on the wall. Studio can move and resize it; verpixeld keeps
    // that (appsettings "MediaLayer") and uses the full wall when nothing is set or it no longer fits.
    private (int X, int Y, int W, int H)? _layerRect;

    private sealed class MediaLayerOptions
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    private void LoadLayerRect()
    {
        var o = AppSettingsStore.Get<MediaLayerOptions>("MediaLayer");
        if (o.Width > 0 && o.Height > 0) _layerRect = (o.X, o.Y, o.Width, o.Height);
    }

    private (int X, int Y, int W, int H) LayerRect()
    {
        var w = _canvasManager.Width;
        var h = _canvasManager.Height;
        if (_layerRect is { } r && r.W >= 8 && r.H >= 8 && r.X >= 0 && r.Y >= 0 && r.X + r.W <= w && r.Y + r.H <= h)
            return r;
        return (0, 0, w, h);
    }

    private void RememberLayer(Canvas c) => RememberRect(c.XPos, c.YPos, c.Width, c.Height);

    private async Task MoveToOwnLayerAsync(string why)
    {
        if (!await _moveGate.WaitAsync(0)) return;
        try
        {
            TargetCanvasName = null;
            var overlay = FullWallOverlay();
            _mediaCanvas = overlay;
            overlay.Show();
            _log.LogInformation("{Why}: the video continues on its own layer", why);
            await _videoPlayer.MoveToCanvasAsync(overlay);
        }
        finally
        {
            _moveGate.Release();
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
            _log.LogWarning("Cannot seek - not playing");
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
            _log.LogWarning("Cannot seek - not playing");
            return;
        }

        Interlocked.Exchange(ref _autoPlayAdvancing, 1);
        try
        {
            await _videoPlayer.SeekPercentAsync(percent);
        }
        finally
        {
            Interlocked.Exchange(ref _autoPlayAdvancing, 0);
        }
    }

    /// <summary>The next video that starts uses this audio track (a favorite's saved choice).</summary>
    public void PrepareAudioTrack(int? track) => _videoPlayer.NextAudioTrack = track is >= 0 and < 64 ? track : null;

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
            _log.LogWarning("MOD file not found: {FilePath}", filePath);
            return false;
        }

        ModFilePath = filePath;
        _log.LogInformation("MOD file set: {Value}", Path.GetFileName(filePath));

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
        _log.LogInformation("MOD file cleared");
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
        _replay = () => PlayAudioAsync(audioPath, loop);
        if (!File.Exists(audioPath))
        {
            _log.LogWarning("Audio not found: {AudioPath}", audioPath);
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
        BuildPlaylistFromFile(audioPath);

        _log.LogInformation("Playing audio: {CurrentAudio} ({Value}/{Count})", CurrentAudio, CurrentPlaylistIndex + 1, _audioPlaylist.Count);

        // Audio playback: NO canvas needed - don't show embedded cover art
        // Pass null canvas to VideoPlayer to indicate audio-only mode
        _mediaCanvas = null;

        _videoPlayer.SetBoardTitle(null);
        await _videoPlayer.PlayAudioOnlyAsync(audioPath, loop);
        _videoPlayer.SetBoardCover(audioPath);
    }

    /// <summary>
    ///     Play audio from a network share (audio only - no canvas)
    /// </summary>
    public async Task PlayNetworkAudioAsync(string url, string shareName, string filePath, bool loop = false)
    {
        _replay = () => PlayNetworkAudioAsync(url, shareName, filePath, loop);
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

        _log.LogInformation("Playing network audio: {ShareName}/{FilePath}", shareName, filePath);

        // Audio playback: NO canvas needed
        _mediaCanvas = null;

        _videoPlayer.SetBoardTitle(null);
        await _videoPlayer.PlayAudioOnlyAsync(url, loop);
        _videoPlayer.SetBoardCover(url);
    }

    private bool IsOwnedOverlay(Canvas? canvas) =>
        canvas != null && canvas.Name == "MediaPlayer" && _forcedCanvas == null;

    /// <summary>
    ///     The last video stays on its canvas during a switch so the wall does not flash black. When
    ///     the next video plays on a different canvas, that old frame must go: the media layer sits
    ///     above everything and would hide the new video, and any other canvas would keep a frozen picture.
    /// </summary>
    private void ReleasePreviousCanvas(Canvas? previous)
    {
        if (previous == null || ReferenceEquals(previous, _mediaCanvas)) return;
        try
        {
            if (previous.Name == MediaPlaybackTarget.OverlayName &&
                ReferenceEquals(_canvasManager.GetCanvasByName(previous.Name), previous))
            {
                previous.Clear();
                previous.Hide();
                _canvasManager.RemoveCanvas(previous);
                _log.LogInformation("Previous media layer removed (new video plays elsewhere)");
            }
            else if (!_canvasFromCaller)
            {
                previous.Clear();
                _log.LogInformation("Cleared the last frame on '{Name}'", previous.Name);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Previous canvas: {Message}", ex.Message);
        }
    }

    private void HideOwnedOverlayUntilReady(bool keepLastFrame = false)
    {
        if (!IsOwnedOverlay(_mediaCanvas)) return;
        if (keepLastFrame && !_mediaCanvas!.IsHidden)
        {
            _log.LogInformation("Keeping last frame on MediaPlayer overlay during switch");
            return;
        }
        _mediaCanvas!.Hide();
        _mediaCanvas.Clear();
        _log.LogInformation("MediaPlayer overlay hidden until the first frame");
    }

    private void RevealOwnedOverlay()
    {
        if (!IsOwnedOverlay(_mediaCanvas)) return;
        _mediaCanvas!.Show();
        _log.LogInformation("MediaPlayer overlay shown (first frame)");
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
                _log.LogInformation("MediaPlayer overlay removed");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Overlay teardown: {Message}", ex.Message);
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
        _log.LogError("Error: {Msg}", msg);
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
            
            _log.LogInformation("Video playback stopped (userInitiated={WasUserInitiated}, autoPlay={AutoPlayFavorites}, advancing={IsAdvancing})", wasUserInitiated, AutoPlayFavorites, isAdvancing);
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
                        _log.LogError("Auto-play advance error: {Message}", ex.Message);
                        StopAutoPlay();
                    }
                });
                return;
            }
            
            // Stop auto-play if user initiated stop
            if (AutoPlayFavorites && wasUserInitiated)
            {
                _log.LogInformation("User stopped playback, disabling auto-play");
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
    ///     The playlist is the folder the started track is in (an album, a mix), as full paths,
    ///     sorted by name; shuffle keeps the started track first.
    /// </summary>
    private void BuildPlaylistFromFile(string startFile)
    {
        var dir = Path.GetDirectoryName(startFile);
        var allFiles = new List<string>();
        try
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                allFiles = Directory.EnumerateFiles(dir)
                    .Where(f => MediaLibrary.AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase)
                    .Take(MediaLibrary.MaxFiles)
                    .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Playlist folder: {Message}", ex.Message);
        }
        if (!allFiles.Contains(startFile)) allFiles.Insert(0, startFile);

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
        _log.LogInformation("Playlist built with {Count} tracks, starting at index {CurrentPlaylistIndex}", _audioPlaylist.Count, CurrentPlaylistIndex);
    }

    /// <summary>
    ///     Play the next track in the playlist
    /// </summary>
    public async Task<bool> PlayNextAsync()
    {
        if (_audioPlaylist.Count == 0)
        {
            _log.LogWarning("No playlist - cannot skip to next");
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
                _log.LogInformation("No more tracks in shuffle mode");
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
                    _log.LogInformation("End of playlist");
                    return false;
                }
            }
        }

        var filePath = _audioPlaylist[nextIndex];
        var nextFile = Path.GetFileName(filePath);

        _log.LogInformation("Playing next track: {NextFile} (index {NextIndex})", nextFile, nextIndex);

        // Update index before playing to avoid re-triggering
        CurrentPlaylistIndex = nextIndex;
        CurrentAudio = nextFile;
        LastPlayedAudio = nextFile;

        // Stop current and play next
        await _videoPlayer.StopAsync();

        IsAudioPlayback = true;
        _isRunning = true;
        _mediaCanvas = null; // Audio-only: no canvas needed

        _videoPlayer.SetBoardTitle(null);
        await _videoPlayer.PlayAudioOnlyAsync(filePath);
        _videoPlayer.SetBoardCover(filePath);
        return true;
    }

    /// <summary>
    ///     Play the previous track in the playlist
    /// </summary>
    public async Task<bool> PlayPreviousAsync()
    {
        if (_audioPlaylist.Count == 0)
        {
            _log.LogWarning("No playlist - cannot skip to previous");
            return false;
        }

        // If more than 3 seconds into the track, restart it instead of going to previous
        if (_videoPlayer.Position.TotalSeconds > 3)
        {
            _log.LogInformation("Restarting current track");
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
                _log.LogInformation("Start of playlist - restarting track");
                await _videoPlayer.SeekAsync(TimeSpan.Zero);
                return true;
            }
        }

        var filePath = _audioPlaylist[prevIndex];
        var prevFile = Path.GetFileName(filePath);

        _log.LogInformation("Playing previous track: {PrevFile} (index {PrevIndex})", prevFile, prevIndex);

        // Update index before playing
        CurrentPlaylistIndex = prevIndex;
        CurrentAudio = prevFile;
        LastPlayedAudio = prevFile;

        // Stop current and play previous
        await _videoPlayer.StopAsync();

        IsAudioPlayback = true;
        _isRunning = true;
        _mediaCanvas = null; // Audio-only: no canvas needed

        _videoPlayer.SetBoardTitle(null);
        await _videoPlayer.PlayAudioOnlyAsync(filePath);
        _videoPlayer.SetBoardCover(filePath);
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
            _log.LogInformation("[AUTOPLAY] No items to play");
            return;
        }

        _autoPlayQueue = shuffle 
            ? items.OrderBy(_ => _shuffleRandom.Next()).ToList() 
            : new List<AutoPlayItem>(items);
        _autoPlayCurrentIndex = -1; // Will be set to 0 when first item starts
        AutoPlayFavorites = true;
        Interlocked.Exchange(ref _userInitiatedStop, 0);
        
        _log.LogInformation("[AUTOPLAY] Started with {Count} items (shuffle={Shuffle})", _autoPlayQueue.Count, shuffle);
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
        _log.LogInformation("[AUTOPLAY] Stopped");
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
                    _log.LogInformation("[AUTOPLAY] Queue empty — requesting refill...");
                    var newItems = await AutoPlayRefillCallback();
                    if (newItems.Count > 0)
                    {
                        _autoPlayQueue.AddRange(newItems);
                        _log.LogInformation("[AUTOPLAY] Refilled with {Count} items (total: {Count2})", newItems.Count, _autoPlayQueue.Count);
                        // nextIndex is still valid — continue below
                    }
                    else
                    {
                        _log.LogInformation("[AUTOPLAY] Refill returned empty — stopping");
                        StopAutoPlay();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError("[AUTOPLAY] Refill error: {Message}", ex.Message);
                    StopAutoPlay();
                    return;
                }
            }
            else
            {
                _log.LogInformation("[AUTOPLAY] Reached end of queue");
                StopAutoPlay();
                return;
            }
        }

        _autoPlayCurrentIndex = nextIndex;
        Interlocked.Exchange(ref _autoPlayAdvancing, 1); // Guard against re-entry from StopAsync inside play methods
        
        var item = _autoPlayQueue[nextIndex];
        _log.LogInformation("[AUTOPLAY] Advancing to #{Value}/{Count}: {Name}", nextIndex + 1, _autoPlayQueue.Count, item.Name);

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
                _log.LogInformation("[AUTOPLAY] No play callback registered, stopping auto-play");
                StopAutoPlay();
            }
        }
        catch (Exception ex)
        {
            _log.LogError("[AUTOPLAY] Track failed: {Message} — skipping to next", ex.Message);
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
                _log.LogInformation("[AUTOPLAY] Retry #{Value}: {Name}", failures + 1, nextItem.Name);

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
                    _log.LogError("[AUTOPLAY] Track failed: {Message} — skipping", retryEx.Message);
                    Interlocked.Exchange(ref _autoPlayAdvancing, 0);
                    failures++;
                }
            }

            if (failures >= maxConsecutiveFailures)
            {
                _log.LogInformation("[AUTOPLAY] {MaxConsecutiveFailures} consecutive failures — stopping", maxConsecutiveFailures);
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
            _log.LogInformation("[AUTOPLAY] Not in auto-play mode");
            return;
        }

        _log.LogInformation("[AUTOPLAY] Skipping to next track");
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
            var target = _mediaCanvas ?? FullWallOverlay();
            using var canvas = new CanvasFrame(target); // drawn off-screen, shown in one piece
            canvas.Clear();

            // Dark background
            canvas.DrawRect(0, 0, canvas.Width, canvas.Height, new SKColor(10, 10, 30), SKPaintStyle.Fill);

            // "Up Next" label
            var labelColor = new SKColor(100, 200, 255);
            canvas.DrawBdfText("Up Next", 2, 2, labelColor);

            // Track name - truncate if too long
            var nameColor = SKColors.White;
            var displayName = trackName.Length > 20 ? trackName[..17] + "..." : trackName;
            canvas.DrawBdfText(displayName, 2, 16, nameColor);

            // Progress counter
            var counterColor = new SKColor(150, 150, 150);
            canvas.DrawBdfText($"{currentNum} / {total}", 2, canvas.Height - 12, counterColor);

            // Small loading indicator bar
            var barWidth = (int)((float)currentNum / total * (canvas.Width - 4));
            canvas.DrawRect(2, canvas.Height - 3, barWidth, 2, new SKColor(0, 180, 255), SKPaintStyle.Fill);
            
            canvas.Present();
            target.Show();
            _mediaCanvas = target;

            _log.LogInformation("[AUTOPLAY] Loading screen displayed");
        }
        catch (Exception ex)
        {
            _log.LogError("[AUTOPLAY] Failed to show loading screen: {Message}", ex.Message);
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

namespace verpixeld.Configuration;

/// <summary>
///     Centralised path definitions for all persistent data, configuration, and media.
///     Every service should use these paths instead of building its own.
///     
///     Directory layout:
///       {Base}/Config/       — service configuration files
///       {Base}/Data/         — user-generated content and state
///       {Base}/Data/Gallery/ — saved AI-generated images
///       {Base}/Data/Layouts/ — saved layout profiles
///       {Base}/Data/Schedules/ — layout schedules
///       {Base}/Media/Videos/ — uploaded/local video files
///       {Base}/Media/Music/  — uploaded/local music files
///       {Base}/Media/Audio/  — uploaded/local audio files
/// </summary>
public static class AppPaths
{
    private static readonly string Base = AppContext.BaseDirectory;

    // ── Root directories ──
    public static readonly string ConfigDir = Path.Combine(Base, "Config");
    public static readonly string DataDir = Path.Combine(Base, "Data");
    public static readonly string MediaDir = Path.Combine(Base, "Media");

    // ── Config files ──
    public static readonly string VoiceConfig = Path.Combine(ConfigDir, "voice.json");
    public static readonly string AiConfig = Path.Combine(ConfigDir, "ai.json");
    public static readonly string AlertConfig = Path.Combine(ConfigDir, "alert.json");
    public static readonly string LocalCamConfig = Path.Combine(ConfigDir, "localcam.json");
    public static readonly string NetworkSharesConfig = Path.Combine(ConfigDir, "network_shares.json");
    public static readonly string NightModeConfig = Path.Combine(ConfigDir, "nightmode.json");
    public static readonly string ShareEncryptionKey = Path.Combine(ConfigDir, ".share_key");
    public static readonly string Certificate = Path.Combine(ConfigDir, "server.pfx");

    // ── Data files ──
    public static readonly string Favorites = Path.Combine(DataDir, "favorites.json");
    public static readonly string Playlist = Path.Combine(DataDir, "playlist.json");
    public static readonly string CanvasRotations = Path.Combine(DataDir, "canvas_rotations.json");
    public static readonly string PlayHistory = Path.Combine(DataDir, "play_history.json");
    public static readonly string AiHistory = Path.Combine(DataDir, "ai_history.json");
    public static readonly string Drawings = Path.Combine(DataDir, "drawings.json");
    public static readonly string KeywordModel = Path.Combine(DataDir, "keyword_model.table");

    // ── Data directories ──
    public static readonly string GalleryDir = Path.Combine(DataDir, "Gallery");
    public static readonly string LayoutsDir = Path.Combine(DataDir, "Layouts");
    public static readonly string SchedulesDir = Path.Combine(DataDir, "Schedules");

    // ── Media directories ──
    public static readonly string VideosDir = Path.Combine(MediaDir, "Videos");
    public static readonly string MusicDir = Path.Combine(MediaDir, "Music");
    public static readonly string AudioDir = Path.Combine(MediaDir, "Audio");

    /// <summary>
    ///     Ensure all required directories exist. Called once at startup.
    /// </summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(GalleryDir);
        Directory.CreateDirectory(LayoutsDir);
        Directory.CreateDirectory(SchedulesDir);
        Directory.CreateDirectory(VideosDir);
        Directory.CreateDirectory(MusicDir);
        Directory.CreateDirectory(AudioDir);
    }
}

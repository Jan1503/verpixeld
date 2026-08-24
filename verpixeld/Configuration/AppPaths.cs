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
    public static readonly string SeamCorrection = Path.Combine(ConfigDir, "seam_correction.json");
    public static readonly string AppSettingsOverlay = Path.Combine(ConfigDir, "appsettings.json");
    public static readonly string AppSettingsBundled = Path.Combine(Base, "appsettings.json");

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

    /// <summary>
    ///     Plugins and BDFs live next to the DLL on the Pi. In Docker they live on the Data
    ///     volume so extras can be dropped in without extra mounts.
    /// </summary>
    public static string ExtensionsDir => Path.Combine(ContentRoot, "Extensions");
    public static string FiltersDir => Path.Combine(ContentRoot, "Filters");
    public static string FontsDir => Path.Combine(ContentRoot, "Fonts");
    private static string ContentRoot => RunningInContainer() ? DataDir : Base;

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
        if (RunningInContainer())
        {
            Directory.CreateDirectory(MediaDir);
        }
        else
        {
            Directory.CreateDirectory(VideosDir);
            Directory.CreateDirectory(MusicDir);
            Directory.CreateDirectory(AudioDir);
        }
        Directory.CreateDirectory(ExtensionsDir);
        Directory.CreateDirectory(FiltersDir);
        Directory.CreateDirectory(FontsDir);
        MigrateLegacySeamFile();
        SeedSharePlugins();
    }

    /// <summary>
    ///     TrueNAS/Docker set this. The Pi host does not — persist paths stay next to the DLL there.
    /// </summary>
    public static bool RunningInContainer() =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true",
            StringComparison.OrdinalIgnoreCase)
        || File.Exists("/.dockerenv");

    /// <summary>
    ///     Older builds wrote seam_correction.json next to the DLL (/app in Docker). Copy into
    ///     Config once when running in a container. The Pi keeps the file next to the app.
    /// </summary>
    private static void MigrateLegacySeamFile()
    {
        if (!RunningInContainer()) return;
        var legacy = Path.Combine(Base, "seam_correction.json");
        if (File.Exists(SeamCorrection) || !File.Exists(legacy)) return;
        try
        {
            File.Copy(legacy, SeamCorrection);
            Console.WriteLine($"[SEAM] migrated {legacy} -> {SeamCorrection}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SEAM] migrate failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Copy image Extensions/Filters/Fonts onto the Data volume when a file or plugin
    ///     folder is missing. Never overwrites user files. No-op on the Pi.
    /// </summary>
    private static void SeedSharePlugins()
    {
        if (!RunningInContainer()) return;
        SeedTree(Path.Combine(Base, "Extensions"), ExtensionsDir);
        SeedTree(Path.Combine(Base, "Filters"), FiltersDir);
        SeedTree(Path.Combine(Base, "Fonts"), FontsDir);
    }

    private static void SeedTree(string src, string dest)
    {
        if (!Directory.Exists(src)) return;
        Directory.CreateDirectory(dest);
        var n = 0;
        foreach (var dir in Directory.GetDirectories(src))
        {
            var target = Path.Combine(dest, Path.GetFileName(dir));
            if (Directory.Exists(target)) continue;
            CopyDirectory(dir, target);
            n++;
        }

        foreach (var file in Directory.GetFiles(src))
        {
            var target = Path.Combine(dest, Path.GetFileName(file));
            if (File.Exists(target)) continue;
            File.Copy(file, target);
            n++;
        }

        if (n > 0)
            Console.WriteLine($"[DATA] seeded {n} item(s) into {dest}");
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    /// <summary>
    ///     Writable config file. Existing Config or Base copies win; new files go to
    ///     Config in a container and next to the DLL on the Pi.
    /// </summary>
    public static string ResolveWritableConfigFile(string? relativeOrAbsolute, string fallbackFileName)
    {
        if (!string.IsNullOrWhiteSpace(relativeOrAbsolute) && Path.IsPathRooted(relativeOrAbsolute))
            return relativeOrAbsolute;

        var name = Path.GetFileName(string.IsNullOrWhiteSpace(relativeOrAbsolute)
            ? fallbackFileName
            : relativeOrAbsolute);
        var inConfig = Path.Combine(ConfigDir, name);
        var inBase = Path.Combine(Base, name);
        if (File.Exists(inConfig)) return inConfig;
        if (File.Exists(inBase)) return inBase;
        return RunningInContainer() ? inConfig : inBase;
    }
}

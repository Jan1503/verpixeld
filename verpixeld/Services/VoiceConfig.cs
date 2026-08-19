using System.Text.Json;
using verpixeld.Configuration;

namespace verpixeld.Services;

/// <summary>
///     JSON-serialisable configuration for the voice command service.
///     Handles load/save with atomic writes.
/// </summary>
public class VoiceConfig
{
    public string? SpeechKey { get; set; }
    public string? SpeechRegion { get; set; }
    public string? KeywordModelPath { get; set; }
    public string? AudioDevice { get; set; }
    public string? VideoDevice { get; set; }
    public string? DefaultStyle { get; set; }
    public string? SpeechLanguage { get; set; }
    public int DisplayDurationSeconds { get; set; }
    public int SilenceTimeoutMs { get; set; }
    public string? ProfanityFilter { get; set; }
    public string? SegmentationStrategy { get; set; }
    public bool TtsEnabled { get; set; }
    public string? TtsVoiceName { get; set; }
    public bool MusicAudioOnly { get; set; } = true;
    public bool SaveGeneratedImages { get; set; } = true;
    public bool PlayConfirmationSound { get; set; }
    public bool Enabled { get; set; }
    public bool TtsDuckingEnabled { get; set; } = true;
    public int TtsDuckVolumePercent { get; set; } = 15;

    // ───────────────────────────────────────────────────────────
    // Persistence
    // ───────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    ///     Load configuration from disk. Returns a default instance if the file doesn't exist or is corrupt.
    /// </summary>
    public static VoiceConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new VoiceConfig();
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<VoiceConfig>(json);
            if (config != null)
            {
                Console.WriteLine($"[VOICE] Config loaded: Enabled={config.Enabled}, Region={config.SpeechRegion}, Keyword={config.KeywordModelPath ?? "none"}");
                return config;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VOICE] Failed to load config: {ex.Message}");
        }
        return new VoiceConfig();
    }

    /// <summary>
    ///     Save configuration to disk atomically.
    /// </summary>
    public void Save(string path)
    {
        try
        {
            var json = JsonSerializer.Serialize(this, WriteOptions);
            FileHelper.AtomicWriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VOICE] Failed to save config: {ex.Message}");
        }
    }
}

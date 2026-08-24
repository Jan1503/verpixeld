using System.Text.Json;
using System.Text.Json.Nodes;

namespace verpixeld.Configuration;

/// <summary>
///     Patch helper for appsettings.json. Updates individual sections in-place so saving one group
///     (e.g. HDMI) cannot wipe Network / HomeAssistant / ImageCorrection the way a full rewrite would.
///     On the Pi this is the file next to the DLL. In Docker, saves go to Config/appsettings.json
///     (the volume) as an overlay so deleting the container does not drop GUI settings.
/// </summary>
public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string BundledPath => AppPaths.AppSettingsBundled;
    public static string OverlayPath => AppPaths.AppSettingsOverlay;

    /// <summary>Where GUI saves go. Config volume in a container; next to the DLL on the Pi.</summary>
    public static string ConfigPath =>
        AppPaths.RunningInContainer() ? OverlayPath : BundledPath;

    /// <summary>File used to merge unsent fields (Pi bundled, or overlay once it exists).</summary>
    public static string LoadPath =>
        AppPaths.RunningInContainer() && File.Exists(OverlayPath) ? OverlayPath : BundledPath;

    public static JsonNode Load()
    {
        var path = AppPaths.RunningInContainer()
            ? (File.Exists(OverlayPath) ? OverlayPath : null)
            : BundledPath;
        if (path == null || !File.Exists(path))
            return new JsonObject();
        return Parse(path) ?? new JsonObject();
    }

    public static void Save(JsonNode node)
    {
        var path = ConfigPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (File.Exists(path))
            File.Copy(path, path + ".backup", true);
        File.WriteAllText(path, node.ToJsonString(JsonOpts));
        Console.WriteLine($"[CONFIG] saved {path}");
    }

    public static JsonObject Section(JsonNode root, string name)
    {
        if (root[name] is JsonObject existing)
            return existing;
        var created = new JsonObject();
        root[name] = created;
        return created;
    }

    public static void Set(JsonObject section, string key, JsonNode? value) => section[key] = value;

    public static T Get<T>(string sectionName) where T : class, new()
    {
        try
        {
            var root = Load();
            var node = root[sectionName];
            if (node == null) return new T();
            return node.Deserialize<T>(JsonOpts) ?? new T();
        }
        catch
        {
            return new T();
        }
    }

    private static JsonNode? Parse(string path) =>
        JsonNode.Parse(File.ReadAllText(path), null,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
}

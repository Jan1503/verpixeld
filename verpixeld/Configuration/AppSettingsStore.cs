using System.Text.Json;
using System.Text.Json.Nodes;

namespace verpixeld.Configuration;

/// <summary>
///     Patch helper for appsettings.json. Updates individual sections in-place so saving one group
///     (e.g. HDMI) cannot wipe Network / HomeAssistant / ImageCorrection the way a full rewrite would.
/// </summary>
public static class AppSettingsStore
{
    public static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static JsonNode Load()
    {
        if (!File.Exists(ConfigPath))
            return new JsonObject();
        return JsonNode.Parse(File.ReadAllText(ConfigPath), null,
                   new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })
               ?? new JsonObject();
    }

    public static void Save(JsonNode node)
    {
        if (File.Exists(ConfigPath))
            File.Copy(ConfigPath, ConfigPath + ".backup", true);
        File.WriteAllText(ConfigPath, node.ToJsonString(JsonOpts));
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
}

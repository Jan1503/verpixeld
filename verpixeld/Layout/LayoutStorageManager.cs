using System.Text.Json;
using System.Text.Json.Serialization;
using verpixeld.Configuration;
using verpixeld.Services;

namespace verpixeld.Layout;

/// <summary>
///     Manages saving and loading of layout configurations to/from disk
/// </summary>
[LogTag("LAYOUT STORAGE")]
public class LayoutStorageManager
{
    private readonly ILogger<LayoutStorageManager> _log;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _layoutsDirectory;

    public LayoutStorageManager(string? layoutsDirectory = null, ILogger<LayoutStorageManager>? log = null)
    {
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LayoutStorageManager>.Instance;
        _layoutsDirectory = layoutsDirectory ?? AppPaths.LayoutsDir;
    }

    /// <summary>
    ///     Saves a layout configuration to disk
    /// </summary>
    public void SaveLayout(SavedLayout layout)
    {
        if (string.IsNullOrWhiteSpace(layout.Name)) throw new ArgumentException("Layout name cannot be empty");

        // Sanitize filename
        var fileName = SanitizeFileName(layout.Name) + ".json";
        var filePath = Path.Combine(_layoutsDirectory, fileName);

        layout.LastModified = DateTime.UtcNow;

        // If marking as default, clear default flag from other layouts
        if (layout.IsDefault) ClearDefaultFlag(layout.Name);

        var json = JsonSerializer.Serialize(layout, _jsonOptions);
        FileHelper.AtomicWriteAllText(filePath, json);

        _log.LogInformation("Saved layout '{Name}' to {FilePath}", layout.Name, filePath);
        if (layout.IsDefault) _log.LogInformation("'{Name}' is now the default layout", layout.Name);
    }

    /// <summary>
    ///     Clears the default flag from all layouts except the specified one
    /// </summary>
    private void ClearDefaultFlag(string exceptLayoutName)
    {
        if (!Directory.Exists(_layoutsDirectory)) return;

        var files = Directory.GetFiles(_layoutsDirectory, "*.json");

        foreach (var file in files)
            try
            {
                var json = File.ReadAllText(file);
                var layout = JsonSerializer.Deserialize<SavedLayout>(json, _jsonOptions);

                if (layout != null && layout.IsDefault && layout.Name != exceptLayoutName)
                {
                    layout.IsDefault = false;
                    var updatedJson = JsonSerializer.Serialize(layout, _jsonOptions);
                    FileHelper.AtomicWriteAllText(file, updatedJson);
                    _log.LogInformation("Cleared default flag from '{Name}'", layout.Name);
                }
            }
            catch (Exception ex)
            {
                _log.LogError("Error clearing default flag in {File}: {Message}", file, ex.Message);
            }
    }

    /// <summary>
    ///     Loads a layout configuration from disk
    /// </summary>
    public SavedLayout? LoadLayout(string layoutName)
    {
        var fileName = SanitizeFileName(layoutName) + ".json";
        var filePath = Path.Combine(_layoutsDirectory, fileName);

        if (!File.Exists(filePath))
        {
            _log.LogWarning("Layout '{LayoutName}' not found", layoutName);
            return null;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var layout = JsonSerializer.Deserialize<SavedLayout>(json, _jsonOptions);

            if (layout != null)
            {
                // Convert JsonElement values in configurations to actual types
                foreach (var canvas in layout.Canvases.Values)
                    if (canvas.Configuration != null)
                    {
                        var convertedConfig = new Dictionary<string, object>();

                        foreach (var (key, value) in canvas.Configuration)
                            convertedConfig[key] = ConvertJsonElement(value);

                        canvas.Configuration = convertedConfig;
                    }

                // Convert filter parameters
                foreach (var filter in layout.Filters)
                    if (filter.Parameters != null)
                    {
                        var convertedParams = new Dictionary<string, object>();
                        foreach (var (key, value) in filter.Parameters)
                            convertedParams[key] = ConvertJsonElement(value);
                        filter.Parameters = convertedParams;
                    }
            }

            _log.LogInformation("Loaded layout '{LayoutName}'", layoutName);
            return layout;
        }
        catch (Exception ex)
        {
            _log.LogError("Error loading layout '{LayoutName}': {Message}", layoutName, ex.Message);
            return null;
        }
    }

    /// <summary>
    ///     Converts a JsonElement to its actual type
    /// </summary>
    private static object ConvertJsonElement(object value)
    {
        if (value is not JsonElement jsonElement) return value; // Already converted or not a JsonElement

        return jsonElement.ValueKind switch
        {
            JsonValueKind.Number => ConvertNumber(jsonElement),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => jsonElement.GetString() ?? string.Empty,
            JsonValueKind.Null => null!,
            _ => value // Return as-is for arrays/objects
        };
    }

    /// <summary>
    ///     Converts a JSON number to the most appropriate numeric type
    /// </summary>
    private static object ConvertNumber(JsonElement jsonElement)
    {
        // Try int first
        if (jsonElement.TryGetInt32(out var intVal)) return intVal;

        // Try long
        if (jsonElement.TryGetInt64(out var longVal)) return longVal;

        // Get as double
        var doubleVal = jsonElement.GetDouble();

        // Check if it's actually a whole number that fits in int32
        if (doubleVal >= int.MinValue && doubleVal <= int.MaxValue && Math.Abs(doubleVal % 1) < double.Epsilon)
            return (int)doubleVal;

        // Check if it's a whole number that fits in int64
        if (doubleVal >= long.MinValue && doubleVal <= long.MaxValue && Math.Abs(doubleVal % 1) < double.Epsilon)
            return (long)doubleVal;

        return doubleVal;
    }

    /// <summary>
    ///     Gets a list of all saved layouts
    /// </summary>
    public List<SavedLayoutInfo> GetAllLayouts()
    {
        var layouts = new List<SavedLayoutInfo>();

        if (!Directory.Exists(_layoutsDirectory)) return layouts;

        var files = Directory.GetFiles(_layoutsDirectory, "*.json");

        foreach (var file in files)
            try
            {
                var json = File.ReadAllText(file);
                var layout = JsonSerializer.Deserialize<SavedLayout>(json, _jsonOptions);

                if (layout != null)
                    layouts.Add(new SavedLayoutInfo
                    {
                        Name = layout.Name,
                        Description = layout.Description,
                        Profile = layout.Profile,
                        CreatedAt = layout.CreatedAt,
                        LastModified = layout.LastModified,
                        CanvasCount = layout.Canvases.Count,
                        HasExtensions = layout.Canvases.Any(c => !string.IsNullOrEmpty(c.Value.ExtensionName)),
                        IsDefault = layout.IsDefault,
                        FilterCount = layout.Filters?.Count ?? 0
                    });
            }
            catch (Exception ex)
            {
                _log.LogError("Error reading layout file {File}: {Message}", file, ex.Message);
            }

        return layouts.OrderByDescending(l => l.LastModified).ToList();
    }

    /// <summary>
    ///     Deletes a saved layout from disk
    /// </summary>
    public bool DeleteLayout(string layoutName)
    {
        var fileName = SanitizeFileName(layoutName) + ".json";
        var filePath = Path.Combine(_layoutsDirectory, fileName);

        if (!File.Exists(filePath))
        {
            _log.LogWarning("Layout '{LayoutName}' not found for deletion", layoutName);
            return false;
        }

        try
        {
            File.Delete(filePath);
            _log.LogInformation("Deleted layout '{LayoutName}'", layoutName);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError("Error deleting layout '{LayoutName}': {Message}", layoutName, ex.Message);
            return false;
        }
    }

    /// <summary>
    ///     Checks if a layout with the given name exists
    /// </summary>
    public bool LayoutExists(string layoutName)
    {
        var fileName = SanitizeFileName(layoutName) + ".json";
        var filePath = Path.Combine(_layoutsDirectory, fileName);
        return File.Exists(filePath);
    }

    /// <summary>
    ///     Sanitizes a layout name to be used as a filename
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        // Remove invalid filename characters
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));

        // Limit length
        if (sanitized.Length > 200) sanitized = sanitized.Substring(0, 200);

        return sanitized;
    }

    /// <summary>
    ///     Gets the default layout, if one is set
    /// </summary>
    public SavedLayout? GetDefaultLayout()
    {
        if (!Directory.Exists(_layoutsDirectory)) return null;

        var files = Directory.GetFiles(_layoutsDirectory, "*.json");

        foreach (var file in files)
            try
            {
                var json = File.ReadAllText(file);
                var layout = JsonSerializer.Deserialize<SavedLayout>(json, _jsonOptions);

                if (layout != null && layout.IsDefault)
                {
                    // Convert JsonElement values
                    foreach (var canvas in layout.Canvases.Values)
                        if (canvas.Configuration != null)
                        {
                            var convertedConfig = new Dictionary<string, object>();
                            foreach (var (key, value) in canvas.Configuration)
                                convertedConfig[key] = ConvertJsonElement(value);
                            canvas.Configuration = convertedConfig;
                        }

                    // Convert filter parameters
                    foreach (var filter in layout.Filters)
                        if (filter.Parameters != null)
                        {
                            var convertedParams = new Dictionary<string, object>();
                            foreach (var (key, value) in filter.Parameters)
                                convertedParams[key] = ConvertJsonElement(value);
                            filter.Parameters = convertedParams;
                        }

                    _log.LogInformation("Found default layout: '{Name}'", layout.Name);
                    return layout;
                }
            }
            catch (Exception ex)
            {
                _log.LogError("Error reading layout file {File}: {Message}", file, ex.Message);
            }

        return null;
    }

    /// <summary>
    ///     Sets a layout as the default
    /// </summary>
    public bool SetDefaultLayout(string layoutName)
    {
        var layout = LoadLayout(layoutName);
        if (layout == null) return false;

        layout.IsDefault = true;
        SaveLayout(layout);
        return true;
    }

    /// <summary>
    ///     Clears the default layout setting
    /// </summary>
    public bool ClearDefaultLayout(string layoutName)
    {
        var layout = LoadLayout(layoutName);
        if (layout == null) return false;

        layout.IsDefault = false;
        SaveLayout(layout);
        return true;
    }
}

/// <summary>
///     Summary information about a saved layout (for listing)
/// </summary>
public class SavedLayoutInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;

    [JsonPropertyName("profile")] public string Profile { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }

    [JsonPropertyName("lastModified")] public DateTime LastModified { get; set; }

    [JsonPropertyName("canvasCount")] public int CanvasCount { get; set; }

    [JsonPropertyName("hasExtensions")] public bool HasExtensions { get; set; }

    [JsonPropertyName("isDefault")] public bool IsDefault { get; set; }

    [JsonPropertyName("filterCount")] public int FilterCount { get; set; }
}

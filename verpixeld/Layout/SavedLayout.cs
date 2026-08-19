using System.Text.Json.Serialization;

namespace verpixeld.Layout;

/// <summary>
///     Represents a saved layout configuration that can be stored and restored
/// </summary>
public class SavedLayout
{
    /// <summary>
    ///     User-defined name for this layout
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Optional description of the layout
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    ///     The layout profile (FullScreen, SplitView, etc.)
    /// </summary>
    [JsonPropertyName("profile")]
    public string Profile { get; set; } = string.Empty;

    /// <summary>
    ///     Whether this is the default layout to load on startup
    /// </summary>
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; } = false;

    /// <summary>
    ///     When this layout was created
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     When this layout was last modified
    /// </summary>
    [JsonPropertyName("lastModified")]
    public DateTime LastModified { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Global brightness level for the layout (0.0 - 1.0)
    /// </summary>
    [JsonPropertyName("globalBrightness")]
    public double GlobalBrightness { get; set; } = 1.0;

    /// <summary>
    ///     Whether this layout should override the global brightness setting.
    ///     If false, the current global brightness (including night mode) will be preserved.
    /// </summary>
    [JsonPropertyName("overrideGlobalBrightness")]
    public bool OverrideGlobalBrightness { get; set; } = true;

    /// <summary>
    ///     Canvas configurations (canvas name ? extension and settings)
    /// </summary>
    [JsonPropertyName("canvases")]
    public Dictionary<string, CanvasConfiguration> Canvases { get; set; } = new();

    /// <summary>
    ///     Active filters and their configurations
    /// </summary>
    [JsonPropertyName("filters")]
    public List<FilterConfiguration> Filters { get; set; } = new();
}

/// <summary>
///     Configuration for a single canvas
/// </summary>
public class CanvasConfiguration
{
    /// <summary>
    ///     Name of the extension assigned to this canvas
    /// </summary>
    [JsonPropertyName("extensionName")]
    public string? ExtensionName { get; set; }

    /// <summary>
    ///     Brightness level for the canvas (0.0 - 1.0)
    /// </summary>
    [JsonPropertyName("brightness")]
    public double Brightness { get; set; } = 1.0;

    /// <summary>
    ///     Z-order (layer position) of the canvas
    /// </summary>
    [JsonPropertyName("zOrder")]
    public int ZOrder { get; set; } = 1;

    /// <summary>
    ///     Opacity level (0.0 = fully transparent, 1.0 = fully opaque)
    /// </summary>
    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 1.0;

    /// <summary>
    ///     X position (for custom overlay canvases)
    /// </summary>
    [JsonPropertyName("x")]
    public int? X { get; set; }

    /// <summary>
    ///     Y position (for custom overlay canvases)
    /// </summary>
    [JsonPropertyName("y")]
    public int? Y { get; set; }

    /// <summary>
    ///     Width (for custom overlay canvases)
    /// </summary>
    [JsonPropertyName("width")]
    public int? Width { get; set; }

    /// <summary>
    ///     Height (for custom overlay canvases)
    /// </summary>
    [JsonPropertyName("height")]
    public int? Height { get; set; }

    /// <summary>
    ///     Whether this is a custom overlay canvas (not part of standard layout)
    /// </summary>
    [JsonPropertyName("isOverlay")]
    public bool IsOverlay { get; set; } = false;

    /// <summary>
    ///     Whether this canvas composites with per-pixel alpha (transparent background reveals layers beneath).
    /// </summary>
    [JsonPropertyName("transparentBackground")]
    public bool TransparentBackground { get; set; }

    /// <summary>
    ///     Extension parameters/settings
    /// </summary>
    [JsonPropertyName("configuration")]
    public Dictionary<string, object> Configuration { get; set; } = new();

    /// <summary>
    ///     Optional per-canvas content rotation (cycles this canvas through several contents). Only present
    ///     when the canvas has rotation steps configured.
    /// </summary>
    [JsonPropertyName("rotation")]
    public CanvasRotationConfig? Rotation { get; set; }
}

/// <summary>
///     Configuration for a filter
/// </summary>
public class FilterConfiguration
{
    /// <summary>
    ///     Type name of the filter
    /// </summary>
    [JsonPropertyName("filterType")]
    public string FilterType { get; set; } = string.Empty;

    /// <summary>
    ///     Filter parameters/settings
    /// </summary>
    [JsonPropertyName("parameters")]
    public Dictionary<string, object> Parameters { get; set; } = new();
}

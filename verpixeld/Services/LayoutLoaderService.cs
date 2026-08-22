using System.Text.Json;
using CanvasManagement;
using CanvasManagement.Interfaces;
using SkiaSharp;
using verpixeld.Interfaces;
using verpixeld.Layout;

namespace verpixeld.Services;

/// <summary>
///     Service responsible for loading and applying saved layouts.
///     Centralizes the layout restoration logic previously duplicated in Program.cs,
///     scheduler handler, and API endpoints.
/// </summary>
public class LayoutLoaderService : ILayoutLoaderService
{
    private readonly CanvasManager _canvasManager;
    private readonly ICanvasContentManager _contentManager;
    private readonly IFilterDiscovery _filterDiscovery;
    private readonly IDisplayLayoutManager _layoutManager;
    private readonly INightModeManager _nightModeManager;
    private readonly CanvasRotationService? _rotationService;

    public LayoutLoaderService(
        CanvasManager canvasManager,
        IDisplayLayoutManager layoutManager,
        ICanvasContentManager contentManager,
        INightModeManager nightModeManager,
        IFilterDiscovery filterDiscovery,
        CanvasRotationService? rotationService = null)
    {
        _canvasManager = canvasManager;
        _layoutManager = layoutManager;
        _contentManager = contentManager;
        _nightModeManager = nightModeManager;
        _filterDiscovery = filterDiscovery;
        _rotationService = rotationService;
    }

    /// <summary>
    ///     Current layout profile after loading
    /// </summary>
    public LayoutProfile CurrentProfile { get; private set; } = LayoutProfile.FullScreen;

    /// <summary>
    ///     Main canvas after layout is loaded (Main, Content, or first available)
    /// </summary>
    public Canvas? PrimaryCanvas { get; private set; }

    /// <summary>
    ///     Loads a saved layout, applying all settings including extensions, filters, and canvas properties.
    /// </summary>
    public async Task<LayoutLoadResult> LoadLayoutAsync(SavedLayout layout, string source = "LAYOUT")
    {
        var result = new LayoutLoadResult { LayoutName = layout.Name };

        try
        {
            // Parse layout profile
            if (!Enum.TryParse<LayoutProfile>(layout.Profile, true, out var profile))
            {
                result.Success = false;
                result.ErrorMessage = $"Invalid profile '{layout.Profile}'";
                Console.WriteLine($"[{source}] {result.ErrorMessage}");
                return result;
            }

            Console.WriteLine($"[{source}] Loading layout: '{layout.Name}'");

            // Stop all current content
            _contentManager.StopAllContent();
            // The layout fully defines the display, including any per-canvas rotations. Clear existing
            // rotations so a stale one can't keep firing on a canvas this layout doesn't define.
            _rotationService?.ClearAll();
            await Task.Delay(100);

            // Clear existing layout and apply new one
            _layoutManager.ClearAllCanvases();
            await Task.Delay(50);

            _layoutManager.ApplyLayout(profile);
            CurrentProfile = profile;

            // Get primary canvas
            PrimaryCanvas = _layoutManager.GetCanvas("Main")
                            ?? _layoutManager.GetCanvas("Content")
                            ?? _layoutManager.GetAllCanvases().FirstOrDefault().Canvas;

            if (PrimaryCanvas == null)
            {
                result.Success = false;
                result.ErrorMessage = "No canvas available after layout change";
                Console.WriteLine($"[{source}] ERROR: {result.ErrorMessage}");
                return result;
            }

            // Handle global brightness
            ApplyGlobalBrightness(layout, source);

            await Task.Delay(50);

            // Restore all canvas configurations
            foreach (var (canvasName, canvasConfig) in layout.Canvases)
                try
                {
                    RestoreCanvas(canvasName, canvasConfig, source);
                    result.CanvasesRestored++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{source}] Failed to restore '{canvasName}': {ex.Message}");
                    result.CanvasesFailed++;
                }

            // Restore filters
            result.FiltersRestored = RestoreFilters(layout, source);

            result.Success = true;
            Console.WriteLine($"[{source}] Successfully loaded '{layout.Name}' " +
                              $"(canvases: {result.CanvasesRestored}, filters: {result.FiltersRestored})");
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            Console.WriteLine($"[{source}] Error loading layout: {ex.Message}");
            return result;
        }
    }

    private void ApplyGlobalBrightness(SavedLayout layout, string source)
    {
        var nightModeConfig = _nightModeManager.GetConfiguration();

        if (layout.OverrideGlobalBrightness)
        {
            _canvasManager.Brightness = (float)layout.GlobalBrightness;
            Console.WriteLine($"[{source}] Brightness set to {_canvasManager.Brightness:F2}");

            if (nightModeConfig.Enabled)
                Console.WriteLine($"[{source}] Note: Night mode is enabled but layout brightness override was applied");
        }
        else
        {
            Console.WriteLine($"[{source}] Brightness preserved: {_canvasManager.Brightness:F2}");
            if (nightModeConfig.Enabled) Console.WriteLine($"[{source}] Night mode remains in control of brightness");
        }
    }

    private void RestoreCanvas(string canvasName, CanvasConfiguration canvasConfig, string source)
    {
        var canvas = _layoutManager.GetCanvas(canvasName);

        // The canvas isn't part of the applied profile (e.g. an overlay, or a second full-screen layer
        // such as a clock stacked over an effect). Recreate it from the saved geometry so multi-canvas
        // layouts restore fully instead of silently dropping the extra canvases. Older layouts saved
        // without geometry fall back to a full-screen canvas so they still load.
        if (canvas == null)
        {
            var w = canvasConfig.Width is > 0 ? canvasConfig.Width.Value : _canvasManager.Width;
            var h = canvasConfig.Height is > 0 ? canvasConfig.Height.Value : _canvasManager.Height;
            Console.WriteLine($"[{source}] Creating canvas '{canvasName}' ({w}x{h}) — not in profile...");
            canvas = _layoutManager.CreateCustomCanvas(
                canvasName,
                canvasConfig.X ?? 0,
                canvasConfig.Y ?? 0,
                w,
                h,
                canvasConfig.ZOrder
            );
        }
        else if (canvasConfig.Width is > 0 && canvasConfig.Height is > 0)
        {
            // The canvas already exists from the applied profile, but the saved layout positions/sizes it
            // differently (e.g. a repositioned "Main"). Resize it to the saved geometry so profile canvases
            // restore like overlays do (previously their saved x/y/w/h was ignored).
            var nx = canvasConfig.X ?? canvas.XPos;
            var ny = canvasConfig.Y ?? canvas.YPos;
            if (canvas.XPos != nx || canvas.YPos != ny ||
                canvas.Width != canvasConfig.Width.Value || canvas.Height != canvasConfig.Height.Value)
            {
                Console.WriteLine($"[{source}] Resizing '{canvasName}' to saved geometry " +
                                  $"({canvasConfig.Width}x{canvasConfig.Height} @ {nx},{ny})...");
                var resized = _layoutManager.ResizeCanvas(canvasName, nx, ny,
                    canvasConfig.Width.Value, canvasConfig.Height.Value);
                if (resized != null) canvas = resized;
            }
        }

        if (canvas != null)
        {
            // Restore properties
            canvas.Brightness = (float)canvasConfig.Brightness;
            canvas.Opacity = (float)canvasConfig.Opacity;
            canvas.PanelColorBits = canvasConfig.PanelColorBits;
            canvas.TransparentBackground = canvasConfig.TransparentBackground;
            if (canvasConfig.Hidden) canvas.Hide();
            else canvas.Show();
            _canvasManager.SetCanvasZOrder(canvas, canvasConfig.ZOrder);

            Console.WriteLine($"[{source}] Restored '{canvasName}': " +
                              $"Brightness={canvas.Brightness:F2}, Opacity={canvas.Opacity:F2}, Z={canvas.ZOrder}");
        }

        // Restore extension if configured
        if (!string.IsNullOrEmpty(canvasConfig.ExtensionName))
        {
            _contentManager.AssignExtension(
                canvasName,
                canvasConfig.ExtensionName,
                NormalizeConfig(canvasConfig.Configuration)
            );
            Console.WriteLine($"[{source}] Assigned '{canvasConfig.ExtensionName}' to '{canvasName}'");
        }

        // Restore per-canvas content rotation (and start it if it was enabled).
        if (canvasConfig.Rotation is { Steps.Count: > 0 })
        {
            _rotationService?.ImportConfig(canvasName, canvasConfig.Rotation);
            Console.WriteLine($"[{source}] Restored rotation for '{canvasName}' ({canvasConfig.Rotation.Steps.Count} steps)");
        }
    }

    /// <summary>
    ///     Saved layouts round-trip through JSON, so dictionary values arrive as <see cref="JsonElement" />.
    ///     Convert them to the CLR primitives the parameter binder expects (the same shapes the live API path
    ///     produces); leave object/array kinds as raw JSON so the binder can deserialize structured parameters.
    /// </summary>
    private static Dictionary<string, object>? NormalizeConfig(Dictionary<string, object>? config)
    {
        if (config == null) return null;

        var result = new Dictionary<string, object>(config.Count);
        foreach (var (key, value) in config) result[key] = NormalizeValue(value);
        return result;
    }

    private static object NormalizeValue(object value)
    {
        if (value is not JsonElement je) return value;

        return je.ValueKind switch
        {
            JsonValueKind.String => je.GetString() ?? string.Empty,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => je.TryGetInt32(out var i)
                ? i
                : je.TryGetInt64(out var l)
                    ? l
                    : je.GetDouble(),
            JsonValueKind.Object or JsonValueKind.Array => je.GetRawText(),
            _ => je.ToString()
        };
    }

    private int RestoreFilters(SavedLayout layout, string source)
    {
        if (layout.Filters == null || layout.Filters.Count == 0)
            return 0;

        _canvasManager.ClearFilters();
        var restoredCount = 0;

        foreach (var filterConfig in layout.Filters)
            try
            {
                var filter = _filterDiscovery.Create(filterConfig.FilterType)
                             ?? _filterDiscovery.CreateByDisplayName(filterConfig.FilterType);

                if (filter != null && filterConfig.Parameters != null)
                {
                    ApplyFilterParameters(filter, filterConfig.Parameters);
                    _canvasManager.AddFilter(filter);
                    restoredCount++;
                    Console.WriteLine($"[{source}] Restored filter: {filterConfig.FilterType}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{source}] Failed to restore filter '{filterConfig.FilterType}': {ex.Message}");
            }

        return restoredCount;
    }

    private static void ApplyFilterParameters(object filter, Dictionary<string, object> parameters)
    {
        var filterType = filter.GetType();

        foreach (var kvp in parameters)
        {
            var prop = filterType.GetProperty(kvp.Key);
            if (prop == null || !prop.CanWrite) continue;

            try
            {
                var value = ConvertParameterValue(kvp.Value, prop.PropertyType);
                prop.SetValue(filter, value);
            }
            catch
            {
                // Silently skip parameters that can't be set
            }
        }
    }

    private static object? ConvertParameterValue(object value, Type targetType)
    {
        if (targetType.IsEnum && value is string enumStr)
            return Enum.Parse(targetType, enumStr, true);

        if (targetType == typeof(SKColor) && value is string colorStr)
        {
            SKColor.TryParse(colorStr, out var skColor);
            return skColor;
        }

        if (targetType == typeof(int) && value is long longVal)
            return (int)longVal;

        if (targetType == typeof(float) && value is double doubleVal)
            return (float)doubleVal;

        return value;
    }
}

/// <summary>
///     Result of a layout load operation
/// </summary>
public class LayoutLoadResult
{
    public string LayoutName { get; set; } = "";
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int CanvasesRestored { get; set; }
    public int CanvasesFailed { get; set; }
    public int FiltersRestored { get; set; }
}

/// <summary>
///     Interface for layout loading service
/// </summary>
public interface ILayoutLoaderService
{
    LayoutProfile CurrentProfile { get; }
    Canvas? PrimaryCanvas { get; }
    Task<LayoutLoadResult> LoadLayoutAsync(SavedLayout layout, string source = "LAYOUT");
}

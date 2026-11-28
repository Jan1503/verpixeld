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
    private readonly ILogger<LayoutLoaderService> _log;

    private readonly CanvasManager _canvasManager;
    private readonly ICanvasContentManager _contentManager;
    private readonly FilterFactory _filters;
    private readonly IDisplayLayoutManager _layoutManager;
    private readonly INightModeManager _nightModeManager;
    private readonly CanvasRotationService? _rotationService;

    public LayoutLoaderService(CanvasManager canvasManager, IDisplayLayoutManager layoutManager, ICanvasContentManager contentManager, INightModeManager nightModeManager, FilterFactory filters, ILogger<LayoutLoaderService> log, CanvasRotationService? rotationService = null)
    {
        _log = log;
        _canvasManager = canvasManager;
        _layoutManager = layoutManager;
        _contentManager = contentManager;
        _nightModeManager = nightModeManager;
        _filters = filters;
        _rotationService = rotationService;
    }

    /// <summary>
    ///     Current layout profile after loading
    /// </summary>
    public LayoutProfile CurrentProfile { get; private set; } = LayoutProfile.FullScreen;

    /// <summary>
    ///     Name of the saved layout (scene) the display currently shows: set when one is loaded or the current
    ///     state is saved under a name, cleared when a blank profile replaces it. Null when none.
    /// </summary>
    public string? CurrentLayoutName { get; private set; }

    /// <inheritdoc />
    public void SetCurrentLayoutName(string? name) => CurrentLayoutName = string.IsNullOrWhiteSpace(name) ? null : name;

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
                _log.LogInformation("[{Source}] {ErrorMessage}", source, result.ErrorMessage);
                return result;
            }

            _log.LogInformation("[{Source}] Loading layout: '{Name}'", source, layout.Name);

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
                _log.LogError("[{Source}] ERROR: {ErrorMessage}", source, result.ErrorMessage);
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
                    _log.LogError("[{Source}] Failed to restore '{CanvasName}': {Message}", source, canvasName, ex.Message);
                    result.CanvasesFailed++;
                }

            // Restore filters
            result.FiltersRestored = RestoreFilters(layout, source);

            result.Success = true;
            CurrentLayoutName = layout.Name;
            Console.WriteLine($"[{source}] Successfully loaded '{layout.Name}' " +
                              $"(canvases: {result.CanvasesRestored}, filters: {result.FiltersRestored})");
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            _log.LogError("[{Source}] Error loading layout: {Message}", source, ex.Message);
            return result;
        }
    }

    private void ApplyGlobalBrightness(SavedLayout layout, string source)
    {
        var nightModeConfig = _nightModeManager.GetConfiguration();

        if (layout.OverrideGlobalBrightness && _nightModeManager.WallOff && _canvasManager.Brightness <= 0.005f)
        {
            _log.LogInformation("[{Source}] Wall is switched off - layout brightness not applied", source);
        }
        else if (layout.OverrideGlobalBrightness)
        {
            _canvasManager.Brightness = (float)layout.GlobalBrightness;
            _log.LogInformation("[{Source}] Brightness set to {Brightness:F2}", source, _canvasManager.Brightness);

            if (nightModeConfig.Enabled)
                _log.LogInformation("[{Source}] Note: Night mode is enabled but layout brightness override was applied", source);
        }
        else
        {
            _log.LogInformation("[{Source}] Brightness preserved: {Brightness:F2}", source, _canvasManager.Brightness);
            if (nightModeConfig.Enabled) _log.LogInformation("[{Source}] Night mode remains in control of brightness", source);
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
            var (x, y, cw, ch) = DisplayGeometry.Clamp(
                canvasConfig.X ?? 0, canvasConfig.Y ?? 0, w, h,
                _canvasManager.Width, _canvasManager.Height);
            _log.LogInformation("[{Source}] Creating canvas '{CanvasName}' ({Cw}x{Ch} @ {X},{Y}) — not in profile...", source, canvasName, cw, ch, x, y);
            canvas = _layoutManager.CreateCustomCanvas(canvasName, x, y, cw, ch, canvasConfig.ZOrder);
        }
        else if (canvasConfig.Width is > 0 && canvasConfig.Height is > 0)
        {
            // The canvas already exists from the applied profile, but the saved layout positions/sizes it
            // differently (e.g. a repositioned "Main"). Resize it to the saved geometry so profile canvases
            // restore like overlays do (previously their saved x/y/w/h was ignored). Clamp so a 256×128
            // scene still lands on a larger wall; never auto-stretch user canvases.
            var (nx, ny, nw, nh) = DisplayGeometry.Clamp(
                canvasConfig.X ?? canvas.XPos, canvasConfig.Y ?? canvas.YPos,
                canvasConfig.Width.Value, canvasConfig.Height.Value,
                _canvasManager.Width, _canvasManager.Height);
            if (canvas.XPos != nx || canvas.YPos != ny || canvas.Width != nw || canvas.Height != nh)
            {
                _log.LogInformation("[{Source}] Resizing '{CanvasName}' to saved geometry ({Nw}x{Nh} @ {Nx},{Ny})...", source, canvasName, nw, nh, nx, ny);
                var resized = _layoutManager.ResizeCanvas(canvasName, nx, ny, nw, nh);
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
            // Effects of this canvas only (an older scene has none: the chain is cleared).
            canvas.SetFilters(_filters.CreateAll(canvasConfig.Filters));
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
            _log.LogInformation("[{Source}] Assigned '{ExtensionName}' to '{CanvasName}'", source, canvasConfig.ExtensionName, canvasName);
        }

        // Restore per-canvas content rotation (and start it if it was enabled).
        if (canvasConfig.Rotation is { Steps.Count: > 0 })
        {
            _rotationService?.ImportConfig(canvasName, canvasConfig.Rotation);
            _log.LogInformation("[{Source}] Restored rotation for '{CanvasName}' ({Count} steps)", source, canvasName, canvasConfig.Rotation.Steps.Count);
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

        foreach (var filter in _filters.CreateAll(layout.Filters))
        {
            _canvasManager.AddFilter(filter);
            restoredCount++;
            _log.LogInformation("[{Source}] Restored filter: {FilterType}", source, filter.GetType().Name);
        }

        return restoredCount;
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
    string? CurrentLayoutName { get; }

    /// <summary>Records which saved layout the display shows now (after saving it, or null after applying a blank profile).</summary>
    void SetCurrentLayoutName(string? name);

    Canvas? PrimaryCanvas { get; }
    Task<LayoutLoadResult> LoadLayoutAsync(SavedLayout layout, string source = "LAYOUT");
}

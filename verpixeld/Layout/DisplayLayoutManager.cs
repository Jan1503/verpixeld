using CanvasManagement;
using verpixeld.Interfaces;
using verpixeld.MediaPlayer.Audio;
using LayoutProfile = verpixeld.Interfaces.LayoutProfile;
using CanvasInfo = verpixeld.Interfaces.CanvasInfo;

namespace verpixeld.Layout;

/// <summary>
///     Manages multiple named canvases with predefined layout profiles
/// </summary>
[LogTag("LAYOUT")]
public class DisplayLayoutManager : IDisplayLayoutManager
{
    private readonly ILogger<DisplayLayoutManager> _log;

    private readonly CanvasManager _canvasManager;
    private readonly IAudioVisualizerService? _audioVisualizerService;
    private readonly Dictionary<string, Canvas> _namedCanvases = new();

    public DisplayLayoutManager(CanvasManager canvasManager, ILogger<DisplayLayoutManager> log, IAudioVisualizerService? audioVisualizerService = null)
    {
        _log = log;
        _canvasManager = canvasManager ?? throw new ArgumentNullException(nameof(canvasManager));
        _audioVisualizerService = audioVisualizerService;
    }

    /// <summary>
    ///     Gets the current layout profile
    /// </summary>
    public LayoutProfile CurrentProfile { get; private set; }

    /// <summary>
    ///     Gets the number of active canvases
    /// </summary>
    public int CanvasCount => _namedCanvases.Count;

    /// <summary>
    ///     Applies a predefined layout profile
    /// </summary>
    public void ApplyLayout(LayoutProfile profile)
    {
        _log.LogInformation("Applying layout: {Profile}", profile);

        // Clear existing canvases
        ClearAllCanvases();

        // Create new layout
        switch (profile)
        {
            case LayoutProfile.FullScreen:
                CreateFullScreenLayout();
                break;
            case LayoutProfile.HeaderContent:
                CreateHeaderContentLayout();
                break;
            case LayoutProfile.ThreePanel:
                CreateThreePanelLayout();
                break;
            case LayoutProfile.SplitView:
                CreateSplitViewLayout();
                break;
            case LayoutProfile.Dashboard:
                CreateDashboardLayout();
                break;
            case LayoutProfile.Custom:
                // Custom layouts handled separately
                break;
        }

        CurrentProfile = profile;
        _log.LogInformation("Layout applied successfully. Active canvases: {Count}", _namedCanvases.Count);
    }

    /// <summary>
    ///     Gets a canvas by name
    /// </summary>
    public Canvas? GetCanvas(string name)
    {
        return _namedCanvases.TryGetValue(name, out var canvas) ? canvas : null;
    }

    /// <summary>
    ///     Gets all active canvases with their names
    /// </summary>
    public IEnumerable<(string Name, Canvas Canvas)> GetAllCanvases()
    {
        return _namedCanvases.Select(kvp => (kvp.Key, kvp.Value));
    }

    /// <summary>
    ///     Gets names of all active canvases
    /// </summary>
    public IEnumerable<string> GetCanvasNames()
    {
        return _namedCanvases.Keys;
    }

    /// <summary>
    ///     Clears and hides all canvases
    /// </summary>
    public void ClearAllCanvases()
    {
        foreach (var (name, canvas) in _namedCanvases)
            try
            {
                // Notify visualizer before clearing (in case it's using this canvas)
                try
                {
                    _audioVisualizerService?.NotifyCanvasRemoved(name);
                }
                catch
                {
                    /* Ignore if service not available */
                }

                canvas.Clear();
                canvas.Hide();


                // CRITICAL: Remove canvas from CanvasManager to prevent duplicates
                _canvasManager.RemoveCanvas(canvas);

                _log.LogInformation("Cleared and removed: {Name}", name);
            }
            catch (Exception ex)
            {
                _log.LogError("Error clearing {Name}: {Message}", name, ex.Message);
            }

        _namedCanvases.Clear();
    }

    /// <summary>
    ///     Creates a custom canvas with specific dimensions
    /// </summary>
    public Canvas CreateCustomCanvas(string name, int x, int y, int width, int height, int zOrder)
    {
        if (_namedCanvases.ContainsKey(name)) throw new InvalidOperationException($"Canvas '{name}' already exists");

        var canvas = _canvasManager.GetCanvas(x, y, width, height, zOrder, name);
        canvas.Show(); // CRITICAL: Show the canvas
        _namedCanvases[name] = canvas;
        _log.LogInformation("Created custom: {Name} ({Width}x{Height} @ {X},{Y} z:{ZOrder})", name, width, height, x, y, zOrder);

        return canvas;
    }

    /// <summary>
    ///     Removes a custom canvas (overlay canvases only, not standard layout canvases)
    /// </summary>
    public bool RemoveCustomCanvas(string name)
    {
        if (!_namedCanvases.ContainsKey(name)) return false;

        // Notify visualizer before removing (in case it's using this canvas)
        try
        {
            _audioVisualizerService?.NotifyCanvasRemoved(name);
        }
        catch
        {
            /* Ignore if service not available */
        }

        _namedCanvases.Remove(name);
        _log.LogInformation("Removed custom canvas: {Name}", name);
        return true;
    }

    /// <summary>
    ///     Recreates an existing named canvas at a new position/size, preserving its z-order. The old canvas
    ///     (and its backbuffer) is removed from the manager; callers must re-assign any extension afterwards so
    ///     it re-reads the new dimensions. Stop the existing content BEFORE calling this.
    /// </summary>
    public Canvas? ResizeCanvas(string name, int x, int y, int width, int height)
    {
        if (!_namedCanvases.TryGetValue(name, out var old)) return null;

        // Preserve the canvas's visual state across the recreate (the new backbuffer would otherwise
        // reset to defaults — most visibly opacity snapping back to 100%).
        var zOrder = old.ZOrder;
        var opacity = old.Opacity;
        var brightness = old.Brightness;
        var panelBits = old.PanelColorBits;
        var hidden = old.IsHidden;
        var transparent = old.TransparentBackground;
        var effects = old.Filters; // filters follow the size of whatever bitmap they get

        try { _audioVisualizerService?.NotifyCanvasRemoved(name); }
        catch { /* Ignore if service not available */ }

        _canvasManager.RemoveCanvas(old);
        _namedCanvases.Remove(name);

        var canvas = _canvasManager.GetCanvas(x, y, width, height, zOrder, name);
        canvas.Opacity = opacity;
        canvas.Brightness = brightness;
        canvas.PanelColorBits = panelBits;
        canvas.TransparentBackground = transparent;
        canvas.SetFilters(effects);
        if (hidden) canvas.Hide();
        else canvas.Show();
        _namedCanvases[name] = canvas;
        _log.LogInformation("Resized canvas: {Name} ({Width}x{Height} @ {X},{Y} z:{ZOrder})", name, width, height, x, y, zOrder);
        return canvas;
    }

    /// <summary>
    ///     Renames an existing canvas. Returns false if the old name doesn't exist or the new name is taken.
    /// </summary>
    public bool RenameCanvas(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || oldName == newName) return false;
        if (!_namedCanvases.TryGetValue(oldName, out var canvas)) return false;
        if (_namedCanvases.ContainsKey(newName)) return false;

        _canvasManager.RenameCanvas(canvas, newName);
        _namedCanvases.Remove(oldName);
        _namedCanvases[newName] = canvas;
        _log.LogInformation("Renamed canvas: {OldName} -> {NewName}", oldName, newName);
        return true;
    }

    /// <summary>
    ///     Checks if a canvas exists
    /// </summary>
    public bool HasCanvas(string name)
    {
        return _namedCanvases.ContainsKey(name);
    }

    /// <summary>
    ///     Gets canvas information for all active canvases
    /// </summary>
    public IEnumerable<CanvasInfo> GetCanvasInfo()
    {
        return _namedCanvases.Select(kvp => new CanvasInfo
        {
            Name = kvp.Key,
            Canvas = kvp.Value
        });
    }

    private void CreateFullScreenLayout()
    {
        var w = _canvasManager.Width;
        var h = _canvasManager.Height;

        var main = _canvasManager.GetCanvas(0, 0, w, h, 1, "Main");
        main.Show(); // CRITICAL: Show the canvas so it's visible
        _namedCanvases["Main"] = main;
        _log.LogInformation("Created: Main ({W}x{H} @ z:1)", w, h);
    }

    private void CreateHeaderContentLayout()
    {
        var w = _canvasManager.Width;
        var h = _canvasManager.Height;
        var headerH = Math.Max(1, h / 6); // ~32px on a 192px display

        var header = _canvasManager.GetCanvas(0, 0, w, headerH, 2, "Header");
        var content = _canvasManager.GetCanvas(0, headerH, w, h - headerH, 1, "Content");

        header.Show();
        content.Show();

        _namedCanvases["Header"] = header;
        _namedCanvases["Content"] = content;

        _log.LogInformation("Created: Header ({W}x{HeaderH} @ z:2)", w, headerH);
        _log.LogInformation("Created: Content ({W}x{Value} @ z:1)", w, h - headerH);
    }

    private void CreateThreePanelLayout()
    {
        var w = _canvasManager.Width;
        var h = _canvasManager.Height;
        var barH = Math.Max(1, h / 6); // header & footer ~1/6 each
        var contentH = Math.Max(1, h - barH * 2);

        var header = _canvasManager.GetCanvas(0, 0, w, barH, 3, "Header");
        var content = _canvasManager.GetCanvas(0, barH, w, contentH, 2, "Content");
        var footer = _canvasManager.GetCanvas(0, barH + contentH, w, barH, 1, "Footer");

        header.Show();
        content.Show();
        footer.Show();

        _namedCanvases["Header"] = header;
        _namedCanvases["Content"] = content;
        _namedCanvases["Footer"] = footer;

        _log.LogInformation("Created: Header ({W}x{BarH} @ z:3)", w, barH);
        _log.LogInformation("Created: Content ({W}x{ContentH} @ z:2)", w, contentH);
        _log.LogInformation("Created: Footer ({W}x{BarH} @ z:1)", w, barH);
    }

    private void CreateSplitViewLayout()
    {
        var w = _canvasManager.Width;
        var h = _canvasManager.Height;
        var halfW = Math.Max(1, w / 2);

        var left = _canvasManager.GetCanvas(0, 0, halfW, h, 1, "Left");
        var right = _canvasManager.GetCanvas(halfW, 0, w - halfW, h, 1, "Right");

        left.Show();
        right.Show();

        _namedCanvases["Left"] = left;
        _namedCanvases["Right"] = right;

        _log.LogInformation("Created: Left ({HalfW}x{H} @ z:1)", halfW, h);
        _log.LogInformation("Created: Right ({Value}x{H} @ z:1)", w - halfW, h);
    }

    private void CreateDashboardLayout()
    {
        var w = _canvasManager.Width;
        var h = _canvasManager.Height;
        var halfW = Math.Max(1, w / 2);
        var halfH = Math.Max(1, h / 2);
        var rightW = w - halfW;
        var bottomH = h - halfH;

        // 2x2 grid of widgets
        var topLeft = _canvasManager.GetCanvas(0, 0, halfW, halfH, 1, "TopLeft");
        var topRight = _canvasManager.GetCanvas(halfW, 0, rightW, halfH, 1, "TopRight");
        var bottomLeft = _canvasManager.GetCanvas(0, halfH, halfW, bottomH, 1, "BottomLeft");
        var bottomRight = _canvasManager.GetCanvas(halfW, halfH, rightW, bottomH, 1, "BottomRight");

        topLeft.Show();
        topRight.Show();
        bottomLeft.Show();
        bottomRight.Show();

        _namedCanvases["TopLeft"] = topLeft;
        _namedCanvases["TopRight"] = topRight;
        _namedCanvases["BottomLeft"] = bottomLeft;
        _namedCanvases["BottomRight"] = bottomRight;

        _log.LogInformation("Created: TopLeft ({HalfW}x{HalfH} @ z:1)", halfW, halfH);
        _log.LogInformation("Created: TopRight ({RightW}x{HalfH} @ z:1)", rightW, halfH);
        _log.LogInformation("Created: BottomLeft ({HalfW}x{BottomH} @ z:1)", halfW, bottomH);
        _log.LogInformation("Created: BottomRight ({RightW}x{BottomH} @ z:1)", rightW, bottomH);
    }

    /// <summary>
    ///     Gets layout description for a profile
    /// </summary>
    public static string GetLayoutDescription(LayoutProfile profile)
    {
        return profile switch
        {
            LayoutProfile.FullScreen => "Single full-screen canvas (384x192)",
            LayoutProfile.HeaderContent => "Header bar (32px) with content area below (160px)",
            LayoutProfile.ThreePanel => "Header (32px), content (128px), and footer (32px) sections",
            LayoutProfile.SplitView => "Two vertical panels side-by-side (192px each)",
            LayoutProfile.Dashboard => "2x2 grid of widgets (96px each)",
            LayoutProfile.Custom => "User-defined custom layout",
            _ => "Unknown layout"
        };
    }
}

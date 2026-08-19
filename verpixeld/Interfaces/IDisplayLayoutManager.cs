using CanvasManagement;

namespace verpixeld.Interfaces;

/// <summary>
///     Manages multiple named canvases with predefined layout profiles
/// </summary>
public interface IDisplayLayoutManager
{
    /// <summary>
    ///     Gets the current layout profile
    /// </summary>
    LayoutProfile CurrentProfile { get; }

    /// <summary>
    ///     Gets the number of active canvases
    /// </summary>
    int CanvasCount { get; }

    /// <summary>
    ///     Applies a predefined layout profile
    /// </summary>
    void ApplyLayout(LayoutProfile profile);

    /// <summary>
    ///     Gets a canvas by name
    /// </summary>
    Canvas? GetCanvas(string name);

    /// <summary>
    ///     Gets all active canvases with their names
    /// </summary>
    IEnumerable<(string Name, Canvas Canvas)> GetAllCanvases();

    /// <summary>
    ///     Gets names of all active canvases
    /// </summary>
    IEnumerable<string> GetCanvasNames();

    /// <summary>
    ///     Clears and hides all canvases
    /// </summary>
    void ClearAllCanvases();

    /// <summary>
    ///     Creates a custom canvas with specific dimensions
    /// </summary>
    Canvas CreateCustomCanvas(string name, int x, int y, int width, int height, int zOrder);

    /// <summary>
    ///     Removes a custom canvas (overlay canvases only)
    /// </summary>
    bool RemoveCustomCanvas(string name);

    /// <summary>
    ///     Recreates an existing named canvas at a new position/size, preserving its z-order. Returns the new
    ///     canvas, or null if no canvas with that name exists. The backbuffer is reallocated, so any extension
    ///     must be re-assigned afterwards to pick up the new dimensions.
    /// </summary>
    Canvas? ResizeCanvas(string name, int x, int y, int width, int height);

    /// <summary>
    ///     Renames an existing canvas. Returns false if the old name doesn't exist or the new name is taken.
    /// </summary>
    bool RenameCanvas(string oldName, string newName);

    /// <summary>
    ///     Checks if a canvas exists
    /// </summary>
    bool HasCanvas(string name);

    /// <summary>
    ///     Gets canvas information for all active canvases
    /// </summary>
    IEnumerable<CanvasInfo> GetCanvasInfo();
}

/// <summary>
///     Defines predefined layout configurations for multi-canvas displays
/// </summary>
public enum LayoutProfile
{
    /// <summary>Single full-screen canvas</summary>
    FullScreen,

    /// <summary>Header bar + content area below</summary>
    HeaderContent,

    /// <summary>Header + content + footer sections</summary>
    ThreePanel,

    /// <summary>Two vertical panels side-by-side</summary>
    SplitView,

    /// <summary>2x2 grid of widgets</summary>
    Dashboard,

    /// <summary>User-defined custom layout</summary>
    Custom
}

/// <summary>
///     Information about a canvas
/// </summary>
public class CanvasInfo
{
    public string Name { get; init; } = string.Empty;
    public Canvas Canvas { get; init; } = null!;
}

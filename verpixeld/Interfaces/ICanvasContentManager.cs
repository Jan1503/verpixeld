using verpixeld.Layout;

namespace verpixeld.Interfaces;

/// <summary>
///     Manages content (extensions, static content) on named canvases
/// </summary>
public interface ICanvasContentManager
{
    /// <summary>
    ///     Gets the number of canvases with active content
    /// </summary>
    int ActiveContentCount { get; }

    /// <summary>
    ///     Assigns a dynamic extension to a specific canvas
    /// </summary>
    CanvasContent AssignExtension(string canvasName, string extensionDisplayName,
        Dictionary<string, object>? config = null);

    /// <summary>
    ///     Resizes/repositions a canvas, rebuilding any hosted extension at the new dimensions while
    ///     preserving its parameter values. Returns false if the canvas does not exist.
    /// </summary>
    bool ResizeCanvas(string canvasName, int x, int y, int width, int height);

    /// <summary>
    ///     Re-keys tracked content from an old canvas name to a new one (used when a canvas is renamed).
    /// </summary>
    void RenameCanvasContent(string oldName, string newName);

    /// <summary>
    ///     Stops content on a specific canvas
    /// </summary>
    void StopContent(string canvasName);

    /// <summary>
    ///     Stops all active content
    /// </summary>
    void StopAllContent();

    /// <summary>
    ///     Gets content info for a canvas
    /// </summary>
    CanvasContent? GetContent(string canvasName);

    /// <summary>
    ///     Gets all active content
    /// </summary>
    IEnumerable<CanvasContent> GetAllContents();

    /// <summary>
    ///     Updates the configuration/parameters for content on a canvas
    /// </summary>
    void UpdateConfiguration(string canvasName, Dictionary<string, object> config);

    /// <summary>
    ///     Restarts content on a specific canvas
    /// </summary>
    void RestartContent(string canvasName);

    /// <summary>
    ///     Invokes a method on the extension running on a canvas
    /// </summary>
    object? InvokeMethod(string canvasName, string methodName, object[]? args = null);

    /// <summary>
    ///     Gets available methods for the extension on a canvas
    /// </summary>
    IEnumerable<ExtensionMethodInfo> GetAvailableMethods(string canvasName);
}

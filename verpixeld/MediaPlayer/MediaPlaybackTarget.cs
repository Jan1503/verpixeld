namespace verpixeld.MediaPlayer;

/// <summary>
///     Full-wall playback uses a dedicated overlay named <see cref="OverlayName"/>.
///     Any other name, including "Main", is that canvas.
/// </summary>
public static class MediaPlaybackTarget
{
    public const string OverlayName = "MediaPlayer";

    /// <summary>
    ///     No target, or "MediaPlayer": the media player's own full-wall layer (z 200), above the
    ///     layout's canvases and below alerts and toasts. Any other name plays into that canvas.
    /// </summary>
    public static bool UsesOwnedOverlay(string? targetCanvasName) =>
        string.IsNullOrWhiteSpace(targetCanvasName) ||
        string.Equals(targetCanvasName.Trim(), OverlayName, StringComparison.OrdinalIgnoreCase);

    public static string ResolveName(string? targetCanvasName) =>
        UsesOwnedOverlay(targetCanvasName) ? OverlayName : targetCanvasName!;
}

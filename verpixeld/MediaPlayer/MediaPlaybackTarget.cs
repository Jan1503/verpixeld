namespace verpixeld.MediaPlayer;

/// <summary>
///     Full-wall playback uses a dedicated overlay named <see cref="OverlayName"/>.
///     Any other name, including "Main", is that canvas.
/// </summary>
public static class MediaPlaybackTarget
{
    public const string OverlayName = "MediaPlayer";

    public static bool UsesOwnedOverlay(string? targetCanvasName) =>
        string.Equals(targetCanvasName, OverlayName, StringComparison.OrdinalIgnoreCase);

    public static string ResolveName(string? targetCanvasName) =>
        UsesOwnedOverlay(targetCanvasName) ? OverlayName : targetCanvasName!;
}

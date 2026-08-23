namespace verpixeld.MediaPlayer;

/// <summary>
///     Where video frames go when the media player target is the default "Main" canvas.
///     Main stays free for extensions; playback uses a dedicated overlay named <see cref="OverlayName"/>.
/// </summary>
public static class MediaPlaybackTarget
{
    public const string OverlayName = "MediaPlayer";

    public static bool UsesOwnedOverlay(string? targetCanvasName) =>
        string.IsNullOrEmpty(targetCanvasName) || targetCanvasName == "Main";

    public static string ResolveName(string? targetCanvasName) =>
        UsesOwnedOverlay(targetCanvasName) ? OverlayName : targetCanvasName!;
}

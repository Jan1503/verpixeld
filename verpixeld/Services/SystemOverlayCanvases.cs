namespace verpixeld.Services;

/// <summary>
///     Host-owned overlay canvases (toast, voice, camera alert). Visible in Studio so they can be
///     positioned; not valid targets for extensions, media, draw, AI, etc.
/// </summary>
public static class SystemOverlayCanvases
{
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "HaToast",
        "VoiceFeedback",
        "CameraAlert"
    };

    public static bool IsSystem(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Names.Contains(name);
}

using System.Text.Json.Serialization;

namespace verpixeld.Layout;

/// <summary>Transition used when a canvas swaps to its next rotation step.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CanvasTransition
{
    Instant,
    Fade
}

/// <summary>
///     One step in a canvas content rotation. <see cref="Type" /> is "extension" (default) or "media".
///     For an extension step, <see cref="Extension" /> + <see cref="Config" /> are the params snapshot.
///     For a media step, <see cref="Config" /> carries { file, loop }.
/// </summary>
public class RotationStep
{
    public string Type { get; set; } = "extension";
    public string Extension { get; set; } = string.Empty;
    public Dictionary<string, object>? Config { get; set; }
}

/// <summary>
///     Per-canvas content rotation: cycles a single canvas through a list of contents on a timer,
///     optionally with a fade transition. Independent of the scene (whole-layout) playlist.
/// </summary>
public class CanvasRotationConfig
{
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 12;
    public bool Loop { get; set; } = true;
    public CanvasTransition Transition { get; set; } = CanvasTransition.Fade;
    public List<RotationStep> Steps { get; set; } = new();
}

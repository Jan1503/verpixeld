using System.Text.Json.Serialization;

namespace verpixeld.Layout;

/// <summary>Transition used between playlist steps.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlaylistTransition
{
    Instant,
    Fade
}

/// <summary>
///     Configuration for the layout (scene) playlist: an ordered list of saved layouts cycled on an interval,
///     optionally looping, with a transition between steps.
/// </summary>
public class PlaylistConfiguration
{
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 15;
    public bool Loop { get; set; } = true;
    public PlaylistTransition Transition { get; set; } = PlaylistTransition.Fade;
    public List<string> Layouts { get; set; } = new();
}

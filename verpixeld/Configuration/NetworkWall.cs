namespace verpixeld.Configuration;

/// <summary>
///     Virtual wall made of 256×128 panels placed at pixel origins. Origins are normalized so the
///     top-left of the bounding box is (0,0). Gaps stay black; overlap sends the same pixels to both.
/// </summary>
public static class NetworkWall
{
    public const int PanelWidth = 256;
    public const int PanelHeight = 128;
    public const int MaxWidth = 2048;
    public const int MaxHeight = 1024;

    public static void Normalize(IList<NetworkPanelTarget> panels)
    {
        var live = panels.Where(p => p is { Enabled: true } && !string.IsNullOrWhiteSpace(p.Host)).ToList();
        if (live.Count == 0) return;

        var minX = live.Min(p => p.X);
        var minY = live.Min(p => p.Y);
        foreach (var p in live)
        {
            p.X = Math.Clamp(p.X - minX, 0, MaxWidth - PanelWidth);
            p.Y = Math.Clamp(p.Y - minY, 0, MaxHeight - PanelHeight);
        }
    }

    public static (int Width, int Height) Measure(IReadOnlyList<NetworkPanelTarget> panels)
    {
        var live = panels.Where(p => p is { Enabled: true } && !string.IsNullOrWhiteSpace(p.Host)).ToList();
        if (live.Count == 0) return (PanelWidth, PanelHeight);

        var width = live.Max(p => p.X + PanelWidth);
        var height = live.Max(p => p.Y + PanelHeight);
        return (Math.Clamp(width, PanelWidth, MaxWidth), Math.Clamp(height, PanelHeight, MaxHeight));
    }
}

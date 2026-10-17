using verpixeld.Configuration;

namespace verpixeld.Tests;

public class NetworkWallTests
{
    private static NetworkPanelTarget P(int x, int y, string host = "10.0.0.1") =>
        new() { Host = host, Port = 7777, Enabled = true, X = x, Y = y };

    [Fact]
    public void Same_origin_stays_one_panel()
    {
        var panels = new List<NetworkPanelTarget> { P(0, 0, "10.0.0.1"), P(0, 0, "10.0.0.2") };
        var size = NetworkWall.Measure(panels);
        Assert.Equal(256, size.Width);
        Assert.Equal(128, size.Height);
    }

    [Fact]
    public void Side_by_side_is_512_wide()
    {
        var panels = new List<NetworkPanelTarget> { P(0, 0, "10.0.0.1"), P(256, 0, "10.0.0.2") };
        var size = NetworkWall.Measure(panels);
        Assert.Equal(512, size.Width);
        Assert.Equal(128, size.Height);
    }

    [Fact]
    public void Stacked_is_256_tall()
    {
        var panels = new List<NetworkPanelTarget> { P(0, 0, "10.0.0.1"), P(0, 128, "10.0.0.2") };
        var size = NetworkWall.Measure(panels);
        Assert.Equal(256, size.Width);
        Assert.Equal(256, size.Height);
    }

    [Fact]
    public void Normalize_pulls_the_origin_to_zero()
    {
        var panels = new List<NetworkPanelTarget> { P(100, 40, "10.0.0.1"), P(356, 40, "10.0.0.2") };
        NetworkWall.Normalize(panels);
        Assert.Equal(0, panels[0].X);
        Assert.Equal(0, panels[0].Y);
        Assert.Equal(256, panels[1].X);
        Assert.Equal(0, panels[1].Y);
        var size = NetworkWall.Measure(panels);
        Assert.Equal(512, size.Width);
        Assert.Equal(128, size.Height);
    }

    [Fact]
    public void A_gap_is_part_of_the_wall()
    {
        var panels = new List<NetworkPanelTarget> { P(0, 0), P(16, 0, "10.0.0.2") };
        var size = NetworkWall.Measure(panels);
        Assert.Equal(272, size.Width);
        Assert.Equal(128, size.Height);
    }
}

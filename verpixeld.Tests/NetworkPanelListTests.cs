using verpixeld.Configuration;

namespace verpixeld.Tests;

public class NetworkPanelListTests
{
    [Fact]
    public void Empty_list_uses_the_single_host()
    {
        var net = new NetworkOptions { Host = "192.168.1.50", Port = 7777, PanelId = "abc" };
        var targets = net.StreamTargets();
        Assert.Single(targets);
        Assert.Equal("192.168.1.50", targets[0].Host);
        Assert.Equal(7777, targets[0].Port);
        Assert.Equal("abc", targets[0].PanelId);
    }

    [Fact]
    public void Clone_list_drops_blanks_disabled_and_duplicates()
    {
        var net = new NetworkOptions
        {
            Host = "10.0.0.1",
            Panels =
            [
                new NetworkPanelTarget { Host = " 192.168.1.50 ", Port = 7777, PanelId = "aaa", Enabled = true },
                new NetworkPanelTarget { Host = "", Port = 7777, Enabled = true },
                new NetworkPanelTarget { Host = "192.168.1.51", Port = 7777, Enabled = false },
                new NetworkPanelTarget { Host = "192.168.1.50", Port = 7777, PanelId = "dup" },
                new NetworkPanelTarget { Host = "192.168.1.52", Port = 0, PanelId = "bbb" }
            ]
        };

        var targets = net.StreamTargets();
        Assert.Equal(2, targets.Count);
        Assert.Equal("192.168.1.50", targets[0].Host);
        Assert.Equal("aaa", targets[0].PanelId);
        Assert.Equal("192.168.1.52", targets[1].Host);
        Assert.Equal(7777, targets[1].Port);
    }

    [Fact]
    public void SyncPrimary_mirrors_the_first_enabled_panel()
    {
        var net = new NetworkOptions
        {
            Host = "10.0.0.9",
            Panels =
            [
                new NetworkPanelTarget { Host = "192.168.1.50", Port = 7777, PanelId = "one" },
                new NetworkPanelTarget { Host = "192.168.1.51", Port = 7778, PanelId = "two" }
            ]
        };

        net.SyncPrimary();
        Assert.Equal("192.168.1.50", net.Host);
        Assert.Equal(7777, net.Port);
        Assert.Equal("one", net.PanelId);
    }
}

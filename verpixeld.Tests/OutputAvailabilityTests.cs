using verpixeld.Hardware;

namespace verpixeld.Tests;

public class OutputAvailabilityTests
{
    [Theory]
    [InlineData("network", true)]
    [InlineData("simulation", true)]
    [InlineData("hdmi", false)]
    [InlineData("spi", false)]
    [InlineData("gpio", false)]
    [InlineData("HDMI", false)]
    public void Container_blocks_host_device_outputs(string mode, bool allowed) =>
        Assert.Equal(allowed, OutputAvailability.Allows(mode, inContainer: true));

    [Theory]
    [InlineData("hdmi")]
    [InlineData("gpio")]
    [InlineData("spi")]
    public void Host_allows_every_backend(string mode) =>
        Assert.True(OutputAvailability.Allows(mode, inContainer: false));
}

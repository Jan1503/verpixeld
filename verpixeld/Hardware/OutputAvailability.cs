namespace verpixeld.Hardware;

/// <summary>
///     HDMI, SPI and GPIO need host devices the NAS container does not have.
/// </summary>
public static class OutputAvailability
{
    public static readonly string[] HostOnlyModes = ["hdmi", "spi", "gpio"];

    public static bool Allows(string? mode, bool inContainer)
    {
        if (!inContainer) return true;
        var m = (mode ?? "").Trim().ToLowerInvariant();
        return m is not ("hdmi" or "spi" or "gpio");
    }

    public static string ContainerBlockMessage =>
        "HDMI, SPI and GPIO need the Pi host. In Docker use Network (UDP panel) or Simulation.";
}

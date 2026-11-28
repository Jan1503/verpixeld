using CanvasManagement;
using Microsoft.Extensions.Logging.Abstractions;
using verpixeld.Layout;
using verpixeld.Services;

namespace verpixeld.Tests;

public class NightModeWallOffTests
{
    private static NightModeManager Create(CanvasManager canvases, string dir)
    {
        var night = new NightModeManager(canvases, NullLogger<NightModeManager>.Instance, dir);
        night.UpdateConfiguration(new NightModeConfiguration
        {
            Enabled = true, StartTime = "00:00", EndTime = "23:59", NightBrightness = 0.3, DayBrightness = 0.3,
            TransitionMinutes = 0
        });
        return night;
    }

    [Fact]
    public void Schedule_leaves_a_wall_that_was_switched_off_dark()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var canvases = new CanvasManager(64, 32);
        using var night = Create(canvases, dir);

        night.WallOff = true;
        canvases.Brightness = 0f;
        night.ForceUpdate();

        Assert.Equal(0f, canvases.Brightness);
        Assert.True(night.WallOff);
    }

    [Fact]
    public void A_brightness_set_elsewhere_ends_the_switched_off_state()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var canvases = new CanvasManager(64, 32);
        using var night = Create(canvases, dir);

        night.WallOff = true;
        canvases.Brightness = 0.8f;
        night.ForceUpdate();

        Assert.False(night.WallOff);
        Assert.Equal(0.3f, canvases.Brightness, 2);
    }

    [Fact]
    public void Repeats_differ_only_in_numbers_share_one_key() =>
        Assert.Equal(LogService.RepeatKey("[CAM] down for 12s"), LogService.RepeatKey("[CAM] down for 345s"));
}

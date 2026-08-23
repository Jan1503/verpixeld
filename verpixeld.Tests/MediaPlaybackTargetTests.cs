using verpixeld.MediaPlayer;

namespace verpixeld.Tests;

public class MediaPlaybackTargetTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Main")]
    public void Empty_or_Main_uses_owned_MediaPlayer_overlay(string? target)
    {
        Assert.True(MediaPlaybackTarget.UsesOwnedOverlay(target));
        Assert.Equal("MediaPlayer", MediaPlaybackTarget.ResolveName(target));
        Assert.Equal(MediaPlaybackTarget.OverlayName, MediaPlaybackTarget.ResolveName(target));
    }

    [Theory]
    [InlineData("MediaPlayer")]
    [InlineData("Overlay")]
    [InlineData("Camera")]
    public void Named_targets_keep_their_canvas(string target)
    {
        Assert.False(MediaPlaybackTarget.UsesOwnedOverlay(target));
        Assert.Equal(target, MediaPlaybackTarget.ResolveName(target));
    }
}

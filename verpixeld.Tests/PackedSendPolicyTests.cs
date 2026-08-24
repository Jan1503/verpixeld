using PixPlane;

namespace verpixeld.Tests;

public class PackedSendPolicyTests
{
    [Fact]
    public void First_frame_is_always_a_key()
    {
        Assert.True(PackedSendPolicy.TrySend(havePrev: false, dueKey: true, nChanged: 160, dirtySinceKey: false, out var key));
        Assert.True(key);
    }

    [Fact]
    public void Unchanged_frame_is_skipped_when_key_is_not_due()
    {
        Assert.False(PackedSendPolicy.TrySend(havePrev: true, dueKey: false, nChanged: 0, dirtySinceKey: true, out _));
    }

    [Fact]
    public void Unchanged_frame_sends_a_recovery_key_once_per_interval()
    {
        Assert.True(PackedSendPolicy.TrySend(havePrev: true, dueKey: true, nChanged: 0, dirtySinceKey: true, out var key));
        Assert.True(key);
    }

    [Fact]
    public void High_motion_without_due_key_stays_a_delta()
    {
        // 80% dirty used to force a full key (nChanged * 2 >= nFrags) and crush 14-bit video FPS.
        Assert.True(PackedSendPolicy.TrySend(havePrev: true, dueKey: false, nChanged: 128, dirtySinceKey: true, out var key));
        Assert.False(key);
    }

    [Fact]
    public void Due_key_sends_all_fragments_even_when_only_some_are_dirty()
    {
        Assert.True(PackedSendPolicy.TrySend(havePrev: true, dueKey: true, nChanged: 12, dirtySinceKey: true, out var key));
        Assert.True(key);
    }
}

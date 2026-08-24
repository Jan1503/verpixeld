using verpixeld.MediaPlayer;

namespace verpixeld.Tests;

public class MediaProbeTests
{
    [Fact]
    public void ParseProbeCsv_uses_format_duration_when_stream_duration_is_na()
    {
        var csv = "1920,800,24000/1001,N/A\n6984.032000\n";
        var info = MediaProbeService.ParseProbeCsv(csv, "/app/Media/movie.mkv", "movie.mkv");
        Assert.NotNull(info);
        Assert.Equal(1920, info.Width);
        Assert.Equal(800, info.Height);
        Assert.InRange(info.Fps, 23.9, 24.1);
        Assert.InRange(info.Duration.TotalSeconds, 6984, 6985);
    }

    [Fact]
    public void ParseProbeCsv_ignores_na_without_format_line()
    {
        var info = MediaProbeService.ParseProbeCsv("1920,800,24000/1001,N/A", "x", "x");
        Assert.NotNull(info);
        Assert.Equal(TimeSpan.Zero, info.Duration);
    }

    [Fact]
    public void TryParseSeconds_rejects_na_and_accepts_invariant_floats()
    {
        Assert.False(MediaProbeService.TryParseSeconds("N/A", out _));
        Assert.True(MediaProbeService.TryParseSeconds("6984.032", out var seconds));
        Assert.InRange(seconds, 6984, 6985);
    }
}

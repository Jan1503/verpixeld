using verpixeld.MediaPlayer.Audio;

namespace verpixeld.Tests;

/// <summary>Which audio outputs the installed FFmpeg has (a self-built one may lack ALSA).</summary>
public class FFmpegMuxerTests
{
    private const string Linux = """
        File formats:
         D. = Demuxing supported
         .E = Muxing supported
         ..d = Is a device
         ---
         DEd alsa            ALSA audio output
          E  mp4             MP4 (MPEG-4 Part 14)
         DE  matroska,webm   Matroska / WebM
          Ed pulse           Pulse audio output
         D d v4l2            Video4Linux2 device grab
        """;

    [Fact]
    public void Reads_muxers_including_devices_and_aliases()
    {
        var muxers = AudioOutputService.ParseMuxers(Linux);

        Assert.Contains("alsa", muxers);
        Assert.Contains("pulse", muxers);
        Assert.Contains("mp4", muxers);
        Assert.Contains("webm", muxers);
        Assert.DoesNotContain("v4l2", muxers); // demuxer only
    }

    [Fact]
    public void An_ffmpeg_without_alsa_output_is_recognised()
    {
        var muxers = AudioOutputService.ParseMuxers("""
              E  mp4             MP4 (MPEG-4 Part 14)
             D d v4l2            Video4Linux2 device grab
            """);

        Assert.DoesNotContain("alsa", muxers);
        Assert.DoesNotContain("pulse", muxers);
    }
}

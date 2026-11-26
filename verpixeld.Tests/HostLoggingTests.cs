using Microsoft.Extensions.Logging;
using verpixeld.Services;

namespace verpixeld.Tests;

public class HostLoggingTests
{
    [Theory]
    [InlineData("verpixeld.Services.AlertService", "ALERT")]
    [InlineData("verpixeld.WebApi.SomethingEndpoints", "SOMETHING")]
    [InlineData("verpixeld.WebApi.CanvasEndpoints", "API")] // [LogTag("API")]
    [InlineData("verpixeld.Layout.SomethingManager", "SOMETHING")]
    [InlineData("verpixeld.Layout.CanvasContentManager", "CONTENT")] // [LogTag("CONTENT")]
    [InlineData("verpixeld.Services.FrameBudgetMonitor", "PERF")] // [LogTag("PERF")]
    [InlineData("verpixeld.Services.CanvasRotationService", "ROTATE")] // [LogTag("ROTATE")]
    [InlineData("verpixeld.Services.Outer+Inner", "OUTER")]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", "Microsoft.AspNetCore.Hosting.Diagnostics")]
    public void Tag_is_the_short_class_name(string category, string expected) =>
        Assert.Equal(expected, HostLoggerProvider.Tag(category));

    [Theory]
    [InlineData("[AUDIO] Failed to open device", LogLevel.Error)]
    [InlineData("[VIDEO] Seek error: timeout", LogLevel.Error)]
    [InlineData("[X] InvalidOperationException: boom", LogLevel.Error)]
    [InlineData("[PERF] canvas Main over budget", LogLevel.Warning)]
    [InlineData("[NET] WARN panel slow", LogLevel.Warning)]
    [InlineData("[MEDIA] Playing song.mp3", LogLevel.Information)]
    public void Console_lines_get_a_level_from_their_wording(string line, LogLevel expected) =>
        Assert.Equal(expected, LogService.GuessLevel(line));

    [Fact]
    public void Logger_messages_reach_the_buffer_with_level_and_tag()
    {
        var log = new LogService();
        using var provider = new HostLoggerProvider(log);
        var logger = provider.CreateLogger("verpixeld.Services.AlertService");

        logger.LogWarning("Stream {Url} slow", "rtsp://cam");
        logger.LogError(new InvalidOperationException("boom"), "Dismiss failed");

        var (entries, _) = log.GetLatest();
        Assert.Collection(entries,
            e =>
            {
                Assert.Equal("warn", e.Level);
                Assert.Equal("ALERT", e.Category);
                Assert.Equal("[ALERT] WARN Stream rtsp://cam slow", e.Message);
            },
            e =>
            {
                Assert.Equal("error", e.Level);
                Assert.Equal("[ALERT] ERROR Dismiss failed: InvalidOperationException: boom", e.Message);
            });
    }

    [Fact]
    public void Console_tag_is_read_from_the_line()
    {
        Assert.Equal("MEDIA", LogService.Category("[MEDIA] Playing"));
        Assert.Equal("", LogService.Category("no tag here"));
    }
}

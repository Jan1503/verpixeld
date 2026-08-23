using CanvasManagement;
using verpixeld.Interfaces;
using verpixeld.Layout;
using verpixeld.Services;

namespace verpixeld.Tests;

public class HostOrchestratorSchedulerTests
{
    [Fact]
    public async Task HandleScheduleTriggered_loads_saved_layout_via_loader()
    {
        var dir = Path.Combine(Path.GetTempPath(), "verpixeld-sched-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var storage = new LayoutStorageManager(dir);
            storage.SaveLayout(new SavedLayout { Name = "Evening", Description = "scheduled" });
            var loader = new RecordingLoader();

            await HostOrchestrator.HandleScheduleTriggeredAsync("Evening", storage, loader);

            Assert.NotNull(loader.LastLayout);
            Assert.Equal("Evening", loader.LastLayout.Name);
            Assert.Equal("SCHEDULER", loader.LastSource);
            Assert.Equal(1, loader.CallCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task HandleScheduleTriggered_skips_loader_when_layout_is_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "verpixeld-sched-miss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var loader = new RecordingLoader();

            await HostOrchestrator.HandleScheduleTriggeredAsync("Missing", new LayoutStorageManager(dir), loader);

            Assert.Equal(0, loader.CallCount);
            Assert.Null(loader.LastLayout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class RecordingLoader : ILayoutLoaderService
    {
        public SavedLayout? LastLayout { get; private set; }
        public string? LastSource { get; private set; }
        public int CallCount { get; private set; }

        public LayoutProfile CurrentProfile => LayoutProfile.FullScreen;
        public string? CurrentLayoutName => LastLayout?.Name;
        public Canvas? PrimaryCanvas => null;

        public Task<LayoutLoadResult> LoadLayoutAsync(SavedLayout layout, string source = "LAYOUT")
        {
            CallCount++;
            LastLayout = layout;
            LastSource = source;
            return Task.FromResult(new LayoutLoadResult { Success = true });
        }
    }
}

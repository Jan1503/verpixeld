using verpixeld.Layout;

namespace verpixeld.Tests;

public class LayoutStorageManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "verpixeld-layout-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LayoutStorageManager _storage;

    public LayoutStorageManagerTests()
    {
        Directory.CreateDirectory(_dir);
        _storage = new LayoutStorageManager(_dir);
    }

    [Fact]
    public void Save_then_Load_round_trips_name_and_settings()
    {
        _storage.SaveLayout(new SavedLayout
        {
            Name = "Night",
            Description = "Dim scene",
            Profile = "FullScreen",
            GlobalBrightness = 0.4,
            Canvases =
            {
                ["Main"] = new CanvasConfiguration
                {
                    ExtensionName = "Clock",
                    Brightness = 0.8,
                    Configuration = { ["speed"] = 12, ["fade"] = true }
                }
            }
        });

        var loaded = _storage.LoadLayout("Night");

        Assert.NotNull(loaded);
        Assert.Equal("Night", loaded.Name);
        Assert.Equal("Dim scene", loaded.Description);
        Assert.Equal("FullScreen", loaded.Profile);
        Assert.Equal(0.4, loaded.GlobalBrightness);
        Assert.Equal("Clock", loaded.Canvases["Main"].ExtensionName);
        Assert.Equal(12, Convert.ToInt32(loaded.Canvases["Main"].Configuration["speed"]));
        Assert.Equal(true, loaded.Canvases["Main"].Configuration["fade"]);
        Assert.True(_storage.LayoutExists("Night"));
    }

    [Fact]
    public void SetDefaultLayout_is_the_only_default()
    {
        _storage.SaveLayout(new SavedLayout { Name = "A", IsDefault = true });
        _storage.SaveLayout(new SavedLayout { Name = "B" });

        Assert.True(_storage.SetDefaultLayout("B"));

        var def = _storage.GetDefaultLayout();
        Assert.NotNull(def);
        Assert.Equal("B", def.Name);
        Assert.False(_storage.LoadLayout("A")!.IsDefault);
        Assert.True(_storage.LoadLayout("B")!.IsDefault);
    }

    [Fact]
    public void DeleteLayout_removes_the_file()
    {
        _storage.SaveLayout(new SavedLayout { Name = "Gone" });

        Assert.True(_storage.DeleteLayout("Gone"));
        Assert.False(_storage.LayoutExists("Gone"));
        Assert.Null(_storage.LoadLayout("Gone"));
    }

    [Fact]
    public void LoadLayout_returns_null_for_missing_name()
    {
        Assert.Null(_storage.LoadLayout("does-not-exist"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // temp cleanup is best-effort
        }
    }
}

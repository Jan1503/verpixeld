using verpixeld.Configuration;

namespace verpixeld.Tests;

public class AppSettingsStoreTests
{
    [Fact]
    public void ConfigPath_on_windows_is_the_bundled_file_next_to_the_app()
    {
        Assert.False(AppPaths.RunningInContainer());
        Assert.Equal(AppPaths.AppSettingsBundled, AppSettingsStore.ConfigPath);
        Assert.Equal(AppPaths.AppSettingsBundled, AppSettingsStore.LoadPath);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), AppSettingsStore.BundledPath);
    }

    [Fact]
    public void Plugin_dirs_on_windows_sit_next_to_the_app()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Extensions"), AppPaths.ExtensionsDir);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Filters"), AppPaths.FiltersDir);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Fonts"), AppPaths.FontsDir);
    }
}

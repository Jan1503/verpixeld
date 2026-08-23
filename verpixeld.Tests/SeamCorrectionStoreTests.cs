using PixPlane;
using verpixeld.Hardware;

namespace verpixeld.Tests;

public class SeamCorrectionStoreTests
{
    [Fact]
    public void Save_then_Load_round_trips_both_bit_profiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "seam-roundtrip-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SeamCorrectionStore();
            store.Set(8, [Col(63, 0.9)]);
            store.Set(14, [Col(127, 0.7)]);
            store.Save(path);

            var loaded = SeamCorrectionStore.Load(path);

            Assert.Equal(63, loaded.Get(8).Single().X);
            Assert.Equal(0.9, loaded.Get(8).Single().GainR);
            Assert.Equal(127, loaded.Get(14).Single().X);
            Assert.Equal(0.7, loaded.Get(14).Single().GainR);
            Assert.Equal(loaded.Profile8.Single().GainR, loaded.Get(SeamCorrectionStore.Bits8).Single().GainR);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_legacy_columns_become_the_14_bit_profile()
    {
        var path = Path.Combine(Path.GetTempPath(), "seam-legacy-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                { "columns": [ { "x": 191, "gainR": 0.5, "gainG": 0.5, "gainB": 0.5 } ] }
                """);

            var loaded = SeamCorrectionStore.Load(path);

            Assert.Equal(191, loaded.Profile14.Single().X);
            Assert.Equal(0.5, loaded.Profile14.Single().GainR);
            Assert.Equal(4, loaded.Profile8.Count);
            Assert.All(loaded.Profile8, col => Assert.Equal(1.0, col.GainR));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_missing_file_uses_identity_8_and_default_14()
    {
        var loaded = SeamCorrectionStore.Load(Path.Combine(Path.GetTempPath(), "no-such-seam-" + Guid.NewGuid() + ".json"));

        Assert.Equal(SeamCorrectionStore.IdentityColumns().Select(c => c.X), loaded.Profile8.Select(c => c.X));
        Assert.Equal(SeamCorrectionStore.DefaultColumns().Select(c => c.X), loaded.Profile14.Select(c => c.X));
    }

    private static SeamColumn Col(int x, double gain) => new()
    {
        X = x, GainR = gain, GainG = gain, GainB = gain
    };
}

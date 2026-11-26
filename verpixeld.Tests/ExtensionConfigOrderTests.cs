using CanvasManagement.Interfaces;
using verpixeld.Layout;

namespace verpixeld.Tests;

/// <summary>A preset that sets other parameters is applied before them, so the user's values win.</summary>
public class ExtensionConfigOrderTests
{
    private sealed class Themed
    {
        [ExtensionParameter("Speed", "")] public int Speed { get; set; }

        [ExtensionParameter("Theme", "", AppliesFirst = true)]
        public string Theme { get; set; } = "None";

        [ExtensionParameter("Colour", "")] public string Colour { get; set; } = "";
    }

    [Fact]
    public void Applies_first_parameters_lead_and_the_rest_keep_their_order()
    {
        var config = new Dictionary<string, object> { ["Colour"] = "red", ["Speed"] = 3, ["Theme"] = "Party", ["Unknown"] = 1 };

        var keys = ExtensionConfigOrder.For(typeof(Themed), config).Select(kv => kv.Key).ToList();

        Assert.Equal(["Theme", "Colour", "Speed", "Unknown"], keys);
    }
}

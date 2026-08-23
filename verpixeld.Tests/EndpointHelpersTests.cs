using System.Text.Json;
using verpixeld.WebApi;

namespace verpixeld.Tests;

public class EndpointHelpersTests
{
    [Fact]
    public void Extract_then_Apply_round_trips_int_float_bool()
    {
        var source = new DummyFilter { Count = 7, Strength = 1.25f, Enabled = true };
        var dest = new DummyFilter();

        EndpointHelpers.ApplyFilterParameters(dest, EndpointHelpers.ExtractFilterParameters(source));

        Assert.Equal(7, dest.Count);
        Assert.Equal(1.25f, dest.Strength);
        Assert.True(dest.Enabled);
    }

    [Fact]
    public void ApplyFilterParameters_reads_json_elements()
    {
        var json = JsonSerializer.Serialize(new DummyFilter { Count = 3, Strength = 0.5f, Enabled = false });
        using var doc = JsonDocument.Parse(json);
        var parameters = new Dictionary<string, object>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            parameters[prop.Name] = prop.Value;

        var dest = new DummyFilter { Count = 99, Strength = 9f, Enabled = true };
        EndpointHelpers.ApplyFilterParameters(dest, parameters);

        Assert.Equal(3, dest.Count);
        Assert.Equal(0.5f, dest.Strength);
        Assert.False(dest.Enabled);
    }

    [Fact]
    public void ToCamelCase_lowers_only_the_first_character()
    {
        Assert.Equal("count", EndpointHelpers.ToCamelCase("Count"));
        Assert.Equal("alreadyCamel", EndpointHelpers.ToCamelCase("alreadyCamel"));
        Assert.Equal("", EndpointHelpers.ToCamelCase(""));
    }

    private sealed class DummyFilter
    {
        public int Count { get; set; }
        public float Strength { get; set; }
        public bool Enabled { get; set; }
    }
}

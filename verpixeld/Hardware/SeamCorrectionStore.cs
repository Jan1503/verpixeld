using System.Text.Json;
using System.Text.Json.Serialization;
using PixPlane;

namespace verpixeld.Hardware;

/// <summary>
///     Dual 8/14-bit seam profiles in <c>seam_correction.json</c>. Legacy files with a single
///     <c>columns</c> array become the 14-bit profile (that is the curve people already tuned);
///     8-bit starts as identity so it can be matched separately.
/// </summary>
public sealed class SeamCorrectionStore
{
    public const int Bits8 = 8;
    public const int Bits14 = 14;

    private static readonly JsonSerializerOptions JsonRead = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonWrite = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public List<SeamColumn> Profile8 { get; private set; } = IdentityColumns();
    public List<SeamColumn> Profile14 { get; private set; } = DefaultColumns();

    public static int NormalizeBits(int bits) => bits >= Bits14 ? Bits14 : Bits8;

    public List<SeamColumn> Get(int bits) =>
        NormalizeBits(bits) == Bits14 ? Profile14 : Profile8;

    public void Set(int bits, IReadOnlyList<SeamColumn> columns)
    {
        var copy = columns.ToList();
        if (NormalizeBits(bits) == Bits14) Profile14 = copy;
        else Profile8 = copy;
    }

    public static SeamCorrectionStore Load(string path)
    {
        var store = new SeamCorrectionStore();
        try
        {
            if (!File.Exists(path))
            {
                store.Profile8 = IdentityColumns();
                store.Profile14 = DefaultColumns();
                return store;
            }

            var dto = JsonSerializer.Deserialize<SeamFileDto>(File.ReadAllText(path), JsonRead);
            var legacy = dto?.Columns is { Count: > 0 } cols ? cols : null;
            var p8 = ReadProfile(dto, "8") ?? ReadProfile(dto, "bit8");
            var p14 = ReadProfile(dto, "14") ?? ReadProfile(dto, "bit14");

            if (p14 != null) store.Profile14 = p14;
            else if (legacy != null) store.Profile14 = legacy;
            else store.Profile14 = DefaultColumns();

            if (p8 != null) store.Profile8 = p8;
            else store.Profile8 = IdentityColumns();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NET] seam load failed: {ex.Message}");
            store.Profile8 = IdentityColumns();
            store.Profile14 = DefaultColumns();
        }

        return store;
    }

    public void Save(string path)
    {
        var dto = new SeamFileDto
        {
            // Legacy readers (DeskCast, older hosts) still see a single columns array = 14-bit.
            Columns = Profile14.ToList(),
            Profiles = new Dictionary<string, SeamProfileDto>
            {
                ["8"] = new() { Columns = Profile8.ToList() },
                ["14"] = new() { Columns = Profile14.ToList() }
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonWrite));
    }

    public static List<SeamColumn> DefaultColumns()
    {
        SeamColumn Col(int x) => new()
        {
            X = x, GainR = 0.85, GainG = 0.85, GainB = 0.85, LiftR = 0.004, LiftG = 0.004, LiftB = 0.004
        };
        return [Col(63), Col(127), Col(191), Col(255)];
    }

    /// <summary>Fresh 8-bit starting point: identity curve, no gain/lift.</summary>
    public static List<SeamColumn> IdentityColumns()
    {
        var knots = SeamColumn.IdentityKnots();
        SeamColumn Col(int x) => new()
        {
            X = x,
            GainR = 1, GainG = 1, GainB = 1,
            LiftR = 0, LiftG = 0, LiftB = 0,
            Knots = knots.Select(k => new SeamKnot { In = k.In, Out = k.Out }).ToList()
        };
        return [Col(63), Col(127), Col(191), Col(255)];
    }

    private static List<SeamColumn>? ReadProfile(SeamFileDto? dto, string key)
    {
        if (dto?.Profiles == null) return null;
        foreach (var (k, v) in dto.Profiles)
        {
            if (!k.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return v.Columns is { Count: > 0 } cols ? cols : null;
        }

        return null;
    }

    private sealed class SeamFileDto
    {
        public List<SeamColumn>? Columns { get; set; }
        public Dictionary<string, SeamProfileDto>? Profiles { get; set; }
    }

    private sealed class SeamProfileDto
    {
        public List<SeamColumn>? Columns { get; set; }
    }
}

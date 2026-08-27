namespace verpixeld.Layout;

/// <summary>
///     Studio helpers for duplicating a canvas without colliding with existing names.
/// </summary>
public static class CanvasCopy
{
    /// <summary>
    ///     <c>Main</c> → <c>Main copy</c>, then <c>Main copy 2</c>, <c>Main copy 3</c>, …
    /// </summary>
    public static string UniqueName(string source, IEnumerable<string> existing)
    {
        var stem = (source ?? "").Trim();
        if (stem.Length == 0) stem = "Overlay";

        var taken = new HashSet<string>(existing ?? [], StringComparer.OrdinalIgnoreCase);
        var n = 1;
        while (true)
        {
            var candidate = n == 1 ? $"{stem} copy" : $"{stem} copy {n}";
            if (!taken.Contains(candidate)) return candidate;
            n++;
        }
    }
}

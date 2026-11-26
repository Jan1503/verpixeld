using System.Diagnostics.CodeAnalysis;

namespace verpixeld.Layout;

/// <summary>Rules for canvas names chosen by the user.</summary>
public static class CanvasNames
{
    public const int MaxLength = 64;

    /// <summary>
    ///     Null when <paramref name="name"/> can be used, else why not. A name is a path segment of the canvas
    ///     API (<c>/api/canvas/{name}/…</c>), and ASP.NET does not decode an encoded slash there, so a name
    ///     with '/' or '\' could never be addressed again; control characters and over-long names are refused too.
    /// </summary>
    public static string? Problem(string? name) => IsValid(name, out var problem) ? null : problem;

    /// <summary>True when <paramref name="name"/> can be used; otherwise <paramref name="problem"/> says why.</summary>
    public static bool IsValid([NotNullWhen(true)] string? name, [NotNullWhen(false)] out string? problem)
    {
        problem = Check(name);
        return problem == null;
    }

    private static string? Check(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A name is required";
        if (name.Length > MaxLength) return $"At most {MaxLength} characters";
        if (name.IndexOfAny(['/', '\\']) >= 0) return @"A name cannot contain / or \";
        if (name.Any(char.IsControl)) return "A name cannot contain control characters";
        return null;
    }
}

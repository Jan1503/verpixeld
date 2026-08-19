using System.Text.Json;

namespace verpixeld.WebApi;

/// <summary>
///     Standardised API response helpers.
///     All endpoints should use these to ensure a consistent JSON envelope:
///     <code>{ "success": bool, "message"?: string, "error"?: string, "data"?: T }</code>
/// </summary>
public static class ApiResponse
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    // ── Success responses ──

    /// <summary>Success with no payload.</summary>
    public static IResult Ok(string? message = null) =>
        Results.Json(new { success = true, message }, CamelCase);

    /// <summary>Success with a data payload.</summary>
    public static IResult Ok<T>(T data, string? message = null) =>
        Results.Json(new { success = true, message, data }, CamelCase);

    // ── Failure responses ──

    /// <summary>Failure with an error message.</summary>
    public static IResult Fail(string error) =>
        Results.Json(new { success = false, error }, CamelCase);

    /// <summary>Failure with an error message and additional context.</summary>
    public static IResult Fail(string error, object context) =>
        Results.Json(new { success = false, error, detail = context }, CamelCase);

    /// <summary>Failure wrapping an exception.</summary>
    public static IResult Error(Exception ex) =>
        Results.Json(new { success = false, error = ex.Message }, CamelCase);
}

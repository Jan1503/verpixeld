using CanvasManagement;
using CanvasManagement.Interfaces;

namespace verpixeld.WebApi;

/// <summary>
///     Extension-related API endpoints
/// </summary>
public static class ExtensionEndpoints
{
    public static void MapExtensionEndpoints(this WebApplication app, EndpointContext ctx)
    {
        var extensionDiscovery = ctx.ExtensionDiscovery;

        // Get all available extensions
        app.MapGet("/api/extensions/available", () =>
        {
            try
            {
                IEnumerable<ExtensionTypeInfo>? extensionInfo = null;

                try
                {
                    extensionInfo = extensionDiscovery?.GetAvailableInfo();
                }
                catch (Exception discoveryEx)
                {
                    Console.WriteLine($"ERROR in ExtensionDiscovery: {discoveryEx.Message}");
                    return Results.Json(new ApiResponse<object[]>(true, Array.Empty<object>()));
                }

                if (extensionInfo == null || !extensionInfo.Any())
                    return Results.Json(new ApiResponse<object[]>(true, Array.Empty<object>()));

                var results = new List<object>();

                foreach (var info in extensionInfo)
                    try
                    {
                        var extDict = new Dictionary<string, object>
                        {
                            ["name"] = info.Name ?? "",
                            ["displayName"] = info.DisplayName ?? "",
                            ["category"] = info.Category ?? "Other",
                            ["description"] = info.Description ?? "",
                            ["iconData"] = info.IconData ?? ""
                        };

                        var paramList = new List<object>();
                        if (info.Parameters != null)
                            foreach (var param in info.Parameters)
                            {
                                if (param == null) continue;
                                paramList.Add(SerializeParam(param));
                            }

                        extDict["parameters"] = paramList;

                        var methodList = new List<object>();
                        if (info.Methods != null)
                            foreach (var method in info.Methods)
                            {
                                if (method == null) continue;
                                var methodDict = new Dictionary<string, object>
                                {
                                    ["name"] = method.Name ?? "",
                                    ["displayName"] = method.DisplayName ?? method.Name ?? "",
                                    ["category"] = method.Category ?? "General",
                                    ["description"] = method.Description ?? ""
                                };

                                var methodParams = new List<object>();
                                if (method.Parameters != null)
                                    foreach (var param in method.Parameters)
                                    {
                                        if (param == null) continue;
                                        var methodParamDict = new Dictionary<string, object>
                                        {
                                            ["name"] = param.Name ?? "",
                                            ["parameterType"] = param.TypeName ?? "Object",
                                            ["isOptional"] = param.IsOptional,
                                            ["isParams"] = param.IsParams
                                        };
                                        if (param.DefaultValue != null)
                                            methodParamDict["defaultValue"] = param.DefaultValue;
                                        methodParams.Add(methodParamDict);
                                    }

                                methodDict["parameters"] = methodParams;
                                methodList.Add(methodDict);
                            }

                        extDict["methods"] = methodList;
                        results.Add(extDict);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ERROR processing extension {info?.Name}: {ex.Message}");
                    }

                return Results.Json(new ApiResponse<object[]>(true, results.ToArray()));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<object[]>(false, Error: "Extension discovery failed"));
            }
        });

        // Get extensions by category
        app.MapGet("/api/extensions/by-category", () =>
        {
            try
            {
                var extensionsByCategory = extensionDiscovery?.GetByCategory()
                                           ?? new Dictionary<string, List<ExtensionTypeInfo>>();

                var results = extensionsByCategory.Select(kvp => new
                {
                    category = kvp.Key,
                    extensions = kvp.Value.Select(info => new Dictionary<string, object>
                    {
                        ["name"] = info.Name ?? "",
                        ["displayName"] = info.DisplayName ?? "",
                        ["description"] = info.Description ?? "",
                        ["parameterCount"] = info.Parameters?.Count ?? 0
                    }).ToArray()
                }).ToArray();

                return Results.Json(new ApiResponse<object[]>(true, results));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<object[]>(false, Error: ex.Message));
            }
        });

        // Debug endpoint
        app.MapGet("/api/extensions/debug", () =>
        {
            try
            {
                var types = extensionDiscovery?.GetAvailableTypes();
                var info = extensionDiscovery?.GetAvailableInfo();
                var byCategory = extensionDiscovery?.GetByCategory();

                var canvasType = typeof(Canvas);
                var extensionMethods = canvasType.GetMethods()
                    .Where(m => m.Name.StartsWith("Get") && !m.IsStatic && m.GetParameters().Length <= 1)
                    .Select(m => new
                    {
                        name = m.Name,
                        returnType = m.ReturnType.Name,
                        isStatic = m.IsStatic
                    })
                    .ToArray();

                return Results.Json(new
                {
                    success = true,
                    data = new
                    {
                        typeNames = types?.Select(t => new { name = t.Name, fullName = t.FullName }).ToArray(),
                        extensionInfo = info?.Select(e => new
                        {
                            name = e.Name,
                            displayName = e.DisplayName,
                            category = e.Category,
                            parameterCount = e.Parameters?.Count ?? 0
                        }).ToArray(),
                        categorizedExtensions = byCategory?.Select(kvp => new
                        {
                            category = kvp.Key,
                            count = kvp.Value.Count,
                            extensions = kvp.Value.Select(e => e.DisplayName).ToArray()
                        }).ToArray(),
                        canvasExtensionMethods = extensionMethods
                    }
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });
    }

    /// <summary>
    ///     Serializes one parameter's schema for the GUI, recursing into nested object/list field schemas so
    ///     the frontend can render structured parameters (e.g. a list of lanes) without any bespoke code.
    /// </summary>
    private static Dictionary<string, object> SerializeParam(ExtensionParameterInfo param)
    {
        var dict = new Dictionary<string, object>
        {
            ["name"] = param.Name ?? "",
            ["displayName"] = string.IsNullOrWhiteSpace(param.DisplayName) ? param.Name ?? "" : param.DisplayName,
            ["parameterType"] = param.ParameterType?.Name ?? "String",
            ["kind"] = param.Kind.ToString(),
            ["defaultValue"] = param.DefaultValue ?? 0,
            ["description"] = param.Description ?? "",
            ["isReadOnly"] = param.IsReadOnly,
            ["order"] = param.Order
        };

        if (param.MinValue != null) dict["minValue"] = param.MinValue;
        if (param.MaxValue != null) dict["maxValue"] = param.MaxValue;
        if (!string.IsNullOrEmpty(param.Unit)) dict["unit"] = param.Unit!;

        var isEnum = param.Kind == ExtensionParameterKind.Enum;
        dict["isEnum"] = isEnum;
        if (isEnum)
            dict["enumValues"] = param.EnumValues ??
                                 (param.ParameterType is { IsEnum: true }
                                     ? Enum.GetNames(param.ParameterType)
                                     : Array.Empty<string>());

        // Nested schema for object groups and list items.
        if (param.Fields is { Count: > 0 })
            dict["fields"] = param.Fields.Select(SerializeParam).ToList();

        return dict;
    }
}

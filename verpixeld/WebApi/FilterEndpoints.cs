using CanvasManagement.Interfaces;

namespace verpixeld.WebApi;

/// <summary>
///     Filter-related API endpoints
/// </summary>
public static class FilterEndpoints
{
    public static void MapFilterEndpoints(this WebApplication app, EndpointContext ctx)
    {
        var canvasManager = ctx.CanvasManager;
        var filterDiscovery = ctx.FilterDiscovery;

        // Get available filter types
        app.MapGet("/api/filters/available", () =>
        {
            try
            {
                var filterInfo = filterDiscovery?.GetAvailableInfo();
                if (filterInfo == null || !filterInfo.Any())
                    return Results.Json(new ApiResponse<object[]>(true, Array.Empty<object>()));

                var results = new List<object>();
                foreach (var info in filterInfo)
                    try
                    {
                        var filterDict = new Dictionary<string, object>
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
                                var paramDict = new Dictionary<string, object>
                                {
                                    ["name"] = param.Name ?? "",
                                    ["parameterType"] = param.ParameterType?.Name ?? "String",
                                    ["defaultValue"] = param.DefaultValue ?? 0,
                                    ["description"] = param.Description ?? "",
                                    ["isReadOnly"] = param.IsReadOnly
                                };
                                if (param.MinValue != null) paramDict["minValue"] = param.MinValue;
                                if (param.MaxValue != null) paramDict["maxValue"] = param.MaxValue;
                                if (param.ParameterType != null && param.ParameterType.IsEnum)
                                {
                                    paramDict["enumValues"] = Enum.GetNames(param.ParameterType);
                                    paramDict["isEnum"] = true;
                                }
                                else
                                {
                                    paramDict["isEnum"] = false;
                                }

                                paramList.Add(paramDict);
                            }

                        filterDict["parameters"] = paramList;
                        results.Add(filterDict);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ERROR processing filter {info?.Name}: {ex.Message}");
                    }

                return Results.Json(new ApiResponse<object[]>(true, results.ToArray()));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<object[]>(false, Error: ex.Message));
            }
        });

        // Get all active filters
        app.MapGet("/api/filters", () =>
        {
            try
            {
                var filters = new List<FilterInfo>();
                for (var i = 0; i < canvasManager.GetFilterCount(); i++)
                {
                    var filter = canvasManager.GetFilterAt(i);
                    if (filter != null)
                        filters.Add(new FilterInfo(
                            $"filter-{i}",
                            filter.GetType().Name,
                            true,
                            EndpointHelpers.ExtractFilterParameters(filter)
                        ));
                }

                return Results.Json(new ApiResponse<FilterInfo[]>(true, filters.ToArray()));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<FilterInfo[]>(false, Error: ex.Message));
            }
        });

        // Clear all filters
        app.MapPost("/api/filters/clear", () =>
        {
            try
            {
                canvasManager.ClearFilters();
                return Results.Json(new ApiResponse<string>(true, "All filters cleared"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Add filter
        app.MapPost("/api/filters/add", async (HttpContext context) =>
        {
            try
            {
                var request = await context.Request.ReadFromJsonAsync<AddFilterRequest>();
                if (request == null || string.IsNullOrEmpty(request.FilterType))
                    return Results.Json(new ApiResponse<string>(false, Error: "Filter type is required"));

                var filter = filterDiscovery?.Create(request.FilterType);
                if (filter == null)
                    filter = filterDiscovery?.CreateByDisplayName(request.FilterType);
                if (filter == null)
                {
                    var typeName = filterDiscovery?.GetByDisplayName(request.FilterType);
                    if (!string.IsNullOrEmpty(typeName.Name))
                        filter = filterDiscovery?.Create(typeName.Name);
                }

                if (filter == null)
                {
                    var availableInfo = filterDiscovery?.GetAvailableInfo();
                    var displayNames = string.Join(", ",
                        availableInfo?.Select(f => $"'{f.DisplayName}'") ?? Array.Empty<string>());
                    return Results.Json(new ApiResponse<string>(false,
                        Error: $"Unknown filter: '{request.FilterType}'. Available: {displayNames}"));
                }

                if (request.Parameters != null && request.Parameters.Count > 0)
                    EndpointHelpers.ApplyFilterParameters(filter, request.Parameters);

                canvasManager.AddFilter(filter);
                return Results.Json(new ApiResponse<string>(true, $"Filter '{filter.GetType().Name}' added"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Update filter parameters
        app.MapPut("/api/filters/{index:int}", async (HttpContext context, int index) =>
        {
            try
            {
                if (index < 0 || index >= canvasManager.GetFilterCount())
                    return Results.Json(new ApiResponse<string>(false, Error: "Invalid filter index"));

                var request = await context.Request.ReadFromJsonAsync<UpdateFilterRequest>();
                if (request?.Parameters == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Parameters required"));

                var filter = canvasManager.GetFilterAt(index);
                if (filter == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Filter not found"));

                EndpointHelpers.ApplyFilterParameters(filter, request.Parameters);
                return Results.Json(new ApiResponse<string>(true, "Filter updated"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Remove filter
        app.MapDelete("/api/filters/{index:int}", (int index) =>
        {
            try
            {
                if (index < 0 || index >= canvasManager.GetFilterCount())
                    return Results.Json(new ApiResponse<string>(false, Error: "Invalid filter index"));

                var filters = new List<ICanvasFilter>();
                for (var i = 0; i < canvasManager.GetFilterCount(); i++)
                    if (i != index)
                    {
                        var filter = canvasManager.GetFilterAt(i);
                        if (filter is ICanvasFilter canvasFilter)
                            filters.Add(canvasFilter);
                    }

                canvasManager.ClearFilters();
                foreach (var filter in filters)
                    canvasManager.AddFilter(filter);

                return Results.Json(new ApiResponse<string>(true, $"Filter {index} removed"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Get filter by name
        app.MapGet("/api/filters/by-name/{name}", (string name) =>
        {
            try
            {
                var filter = canvasManager.GetFilterByName(name);
                if (filter == null)
                    return Results.Json(new ApiResponse<FilterInfo>(false, Error: $"Filter '{name}' not found"));

                var filterInfo = new FilterInfo(
                    name,
                    filter.GetType().Name,
                    true,
                    EndpointHelpers.ExtractFilterParameters(filter)
                );
                return Results.Json(new ApiResponse<FilterInfo>(true, filterInfo));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<FilterInfo>(false, Error: ex.Message));
            }
        });

        // Debug endpoints
        app.MapGet("/api/filters/debug", () =>
        {
            try
            {
                var types = filterDiscovery?.GetAvailableTypes();
                var info = filterDiscovery?.GetAvailableInfo();
                return Results.Json(new
                {
                    success = true,
                    data = new
                    {
                        typeNames = types?.Select(t => new { name = t.Name, fullName = t.FullName }).ToArray(),
                        filterInfo = info?.Select(f => new
                        {
                            name = f.Name,
                            displayName = f.DisplayName,
                            category = f.Category,
                            parameterCount = f.Parameters?.Count ?? 0
                        }).ToArray()
                    }
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        app.MapPost("/api/filters/test", async (HttpContext context) =>
        {
            try
            {
                var request = await context.Request.ReadFromJsonAsync<AddFilterRequest>();
                return Results.Json(new
                {
                    success = true,
                    received = new
                    {
                        filterType = request?.FilterType,
                        parameterCount = request?.Parameters?.Count ?? 0,
                        parameters = request?.Parameters?.Select(kvp => new
                        {
                            key = kvp.Key,
                            value = kvp.Value,
                            valueType = kvp.Value?.GetType().Name
                        }).ToArray()
                    }
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });
    }
}

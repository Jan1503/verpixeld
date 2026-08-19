using System.Text.Json;
using CanvasManagement;
using SkiaSharp;

namespace verpixeld.WebApi;

/// <summary>
///     Shared helper methods for API endpoints
/// </summary>
public static class EndpointHelpers
{
    public static Dictionary<string, object> ExtractFilterParameters(object filter)
    {
        var parameters = new Dictionary<string, object>();
        var properties = filter.GetType().GetProperties();

        foreach (var prop in properties)
            if (prop.CanRead && prop.GetIndexParameters().Length == 0)
                try
                {
                    var value = prop.GetValue(filter);
                    if (value != null) parameters[prop.Name] = value;
                }
                catch
                {
                    // Skip properties that throw exceptions
                }

        return parameters;
    }

    public static void ApplyFilterParameters(object filter, Dictionary<string, object> parameters)
    {
        var type = filter.GetType();

        foreach (var (key, value) in parameters)
        {
            var prop = type.GetProperty(key);
            if (prop != null && prop.CanWrite)
                try
                {
                    object? convertedValue = null;

                    if (value is JsonElement jsonElement)
                    {
                        if (prop.PropertyType == typeof(int))
                            convertedValue = jsonElement.GetInt32();
                        else if (prop.PropertyType == typeof(float))
                            convertedValue = (float)jsonElement.GetDouble();
                        else if (prop.PropertyType == typeof(double))
                            convertedValue = jsonElement.GetDouble();
                        else if (prop.PropertyType == typeof(bool))
                            convertedValue = jsonElement.GetBoolean();
                        else if (prop.PropertyType == typeof(byte))
                            convertedValue = jsonElement.GetByte();
                        else if (prop.PropertyType == typeof(string))
                            convertedValue = jsonElement.GetString();
                    }
                    else if (prop.PropertyType == typeof(int))
                    {
                        if (value is long longVal)
                            convertedValue = (int)longVal;
                        else if (value is double doubleVal)
                            convertedValue = (int)doubleVal;
                        else if (value is string strVal && int.TryParse(strVal, out var intVal))
                            convertedValue = intVal;
                        else
                            convertedValue = Convert.ToInt32(value);
                    }
                    else if (prop.PropertyType == typeof(float))
                    {
                        if (value is double doubleVal)
                            convertedValue = (float)doubleVal;
                        else if (value is long longVal)
                            convertedValue = (float)longVal;
                        else if (value is string strVal && float.TryParse(strVal, out var floatVal))
                            convertedValue = floatVal;
                        else
                            convertedValue = Convert.ToSingle(value);
                    }
                    else if (prop.PropertyType == typeof(double))
                    {
                        if (value is long longVal)
                            convertedValue = (double)longVal;
                        else if (value is string strVal && double.TryParse(strVal, out var doubleVal))
                            convertedValue = doubleVal;
                        else
                            convertedValue = Convert.ToDouble(value);
                    }
                    else if (prop.PropertyType == typeof(byte))
                    {
                        if (value is int intVal)
                            convertedValue = (byte)intVal;
                        else if (value is long longVal)
                            convertedValue = (byte)longVal;
                        else if (value is double doubleVal)
                            convertedValue = (byte)doubleVal;
                        else
                            convertedValue = Convert.ToByte(value);
                    }
                    else if (prop.PropertyType == typeof(bool))
                    {
                        if (value is string strVal)
                            convertedValue = bool.Parse(strVal);
                        else
                            convertedValue = Convert.ToBoolean(value);
                    }
                    else
                    {
                        convertedValue = Convert.ChangeType(value, prop.PropertyType);
                    }

                    prop.SetValue(filter, convertedValue);
                }
                catch
                {
                    // Skip properties that can't be set
                }
        }
    }

    public static Dictionary<string, object> ExtractParameterInfo(object parameter)
    {
        var result = new Dictionary<string, object>();
        var paramType = parameter.GetType();

        foreach (var prop in paramType.GetProperties())
            try
            {
                var value = prop.GetValue(parameter);
                if (value != null)
                {
                    if (value is Type typeValue)
                        result[ToCamelCase(prop.Name)] = typeValue.FullName ?? typeValue.Name;
                    else if (value is not Type)
                        result[ToCamelCase(prop.Name)] = value;
                }
            }
            catch
            {
                // Skip properties that can't be read
            }

        return result;
    }

    public static string ToCamelCase(string str)
    {
        if (string.IsNullOrEmpty(str) || char.IsLower(str[0]))
            return str;

        return char.ToLowerInvariant(str[0]) + str.Substring(1);
    }

    public static void DrawLineOnCanvas(Canvas canvas, int x1, int y1, int x2, int y2, SKColor color, int strokeWidth)
    {
        canvas.DrawLine(x1, y1, x2, y2, color, strokeWidth);
    }

    public static void DrawRectOnCanvas(Canvas canvas, int x1, int y1, int x2, int y2, SKColor color, int strokeWidth,
        bool filled)
    {
        var left = Math.Min(x1, x2);
        var top = Math.Min(y1, y2);
        var width = Math.Abs(x2 - x1);
        var height = Math.Abs(y2 - y1);

        if (width == 0 || height == 0) return;

        using var path = new SKPath();
        path.AddRect(new SKRect(left, top, left + width, top + height));

        var style = filled ? SKPaintStyle.Fill : SKPaintStyle.Stroke;
        canvas.DrawPath(path, color, style, strokeWidth);
    }

    public static void DrawEllipseOnCanvas(Canvas canvas, int x1, int y1, int x2, int y2, SKColor color,
        int strokeWidth, bool filled)
    {
        var left = Math.Min(x1, x2);
        var top = Math.Min(y1, y2);
        var width = Math.Abs(x2 - x1);
        var height = Math.Abs(y2 - y1);

        if (width < 2 || height < 2) return;

        using var path = new SKPath();
        path.AddOval(new SKRect(left, top, left + width, top + height));

        var style = filled ? SKPaintStyle.Fill : SKPaintStyle.Stroke;
        canvas.DrawPath(path, color, style, strokeWidth);
    }
}

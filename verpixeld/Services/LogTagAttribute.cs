namespace verpixeld;

/// <summary>
///     The tag a class's log lines start with (<c>[ROTATE]</c>), when it should not be derived from the class
///     name: kept stable so existing log searches keep working.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class LogTagAttribute(string tag) : Attribute
{
    public string Tag { get; } = tag;
}

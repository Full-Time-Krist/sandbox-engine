using System.Text.RegularExpressions;

namespace Shared;

/// <summary>Постоянные имена блоков и предметов: вида <c>namespace:name</c>, например <c>base:stone</c>.</summary>
public static partial class ContentNames
{
    public static bool IsValid(string name) => Pattern().IsMatch(name);

    [GeneratedRegex("^[a-z0-9_]+:[a-z0-9_]+$")]
    private static partial Regex Pattern();
}

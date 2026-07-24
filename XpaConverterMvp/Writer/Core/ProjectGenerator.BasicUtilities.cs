using System;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static bool StartsWithAt(string text, int index, string value)
{
    return index >= 0 &&
           index + value.Length <= text.Length &&
           string.Compare(text, index, value, 0, value.Length, StringComparison.Ordinal) == 0;
}

private static bool IsZeroLikeLiteral(string value)
{
    var trimmed = value.Trim();
    return trimmed == "0" || trimmed == "0000";
}

}


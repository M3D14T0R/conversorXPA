using System;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static bool ContainsLogicalConjunctionOutsideQuotes(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return false;

    for (var i = 0; i < expr.Length; i++)
    {
        var ch = expr[i];
        if (ch == '\'' || ch == '"')
        {
            if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
                return false;
            i = quoteEnd - 1;
            continue;
        }

        if (i + 1 < expr.Length && ((expr[i] == '&' && expr[i + 1] == '&') || (expr[i] == '|' && expr[i + 1] == '|')))
            return true;
    }

    return false;
}

}


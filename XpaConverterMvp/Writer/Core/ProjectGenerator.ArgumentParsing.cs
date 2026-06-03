using System.Collections.Generic;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static int FindMatchingParen(string text, int openParenIndex)
{
    var depth = 0;
    for (var i = openParenIndex; i < text.Length; i++)
    {
        if (IsQuotedSegmentStart(text, i))
        {
            if (!TryReadQuotedSegmentEnd(text, i, out var quoteEnd))
                return -1;

            i = quoteEnd;
            continue;
        }

        if (text[i] == '(') depth++;
        else if (text[i] == ')')
        {
            depth--;
            if (depth == 0)
                return i;
        }
    }
    return -1;
}

private static List<string> SplitTopLevelArguments(string argsText)
{
    var result = new List<string>();
    var start = 0;
    var depth = 0;
    for (var i = 0; i < argsText.Length; i++)
    {
        if (IsQuotedSegmentStart(argsText, i))
        {
            if (!TryReadQuotedSegmentEnd(argsText, i, out var quoteEnd))
                break;

            i = quoteEnd;
            continue;
        }

        var ch = argsText[i];
        if (ch == '(') depth++;
        else if (ch == ')') depth--;
        else if (ch == ',' && depth == 0)
        {
            result.Add(argsText[start..i].Trim());
            start = i + 1;
        }
    }
    result.Add(argsText[start..].Trim());
    return result;
}
}


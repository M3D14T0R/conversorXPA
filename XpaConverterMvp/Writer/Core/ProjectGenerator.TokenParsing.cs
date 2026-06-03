using System;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static bool TryReadQuotedSegmentEnd(string text, int startIndex, out int endIndex)
{
    endIndex = startIndex;
    if (startIndex < 0 || startIndex >= text.Length)
        return false;

    var verbatim = false;
    if (text[startIndex] == '@' && startIndex + 1 < text.Length && text[startIndex + 1] == '"')
    {
        verbatim = true;
        startIndex++;
    }

    var quote = text[startIndex];
    if (quote is not ('"' or '\''))
        return false;

    for (var i = startIndex + 1; i < text.Length; i++)
    {
        if (verbatim && quote == '"' && text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"')
        {
            i++;
            continue;
        }

        if (!verbatim && quote == '"' && text[i] == '\\' && i + 1 < text.Length)
        {
            i++;
            continue;
        }

        if (text[i] == quote && i + 1 < text.Length && text[i + 1] == quote)
        {
            i++;
            continue;
        }

        if (text[i] == quote)
        {
            endIndex = i;
            return true;
        }
    }

    return false;
}

private static bool TryReadIdentifierToken(string text, int startIndex, out int endIndex)
{
    endIndex = startIndex;
    if (startIndex >= text.Length || !(char.IsLetter(text[startIndex]) || text[startIndex] == '_'))
        return false;

    var i = startIndex + 1;
    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
        i++;

    endIndex = i;
    return true;
}

private static bool IsAllLettersToken(string token)
{
    return !string.IsNullOrWhiteSpace(token) && token.All(char.IsLetter);
}

private static bool IsBareIdentifier(string value)
{
    if (string.IsNullOrWhiteSpace(value))
        return false;

    if (!(char.IsLetter(value[0]) || value[0] == '_'))
        return false;

    for (var i = 1; i < value.Length; i++)
    {
        if (!(char.IsLetterOrDigit(value[i]) || value[i] == '_'))
            return false;
    }

    return true;
}

private static bool TryReadIdentifierPath(string text, int startIndex, out int endIndex)
{
    endIndex = startIndex;
    if (!TryReadIdentifierToken(text, startIndex, out var tokenEnd))
        return false;

    var i = tokenEnd;
    while (i < text.Length && text[i] == '.')
    {
        if (!TryReadIdentifierToken(text, i + 1, out var nextEnd))
            break;
        i = nextEnd;
    }

    endIndex = i;
    return true;
}

private static bool IsQuotedSegmentStart(string text, int index)
{
    if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length)
        return false;

    var ch = text[index];
    if (ch == '\'' || ch == '"')
        return true;

    return ch == '@' && index + 1 < text.Length && text[index + 1] == '"';
}

private static bool HasOpaqueInlineComment(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
        return false;

    return text.Contains("/*", StringComparison.Ordinal) &&
           text.Contains("*/", StringComparison.Ordinal);
}

private static bool TrySplitLeadingOpaqueInlineComment(string? text, out string comment, out string remainder)
{
    comment = "";
    remainder = text?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(remainder))
        return false;

    if (!remainder.StartsWith("/*", StringComparison.Ordinal))
        return false;

    var end = remainder.IndexOf("*/", StringComparison.Ordinal);
    if (end < 0)
        return false;

    comment = remainder[..(end + 2)].Trim();
    remainder = remainder[(end + 2)..].Trim();
    return !string.IsNullOrWhiteSpace(remainder);
}
}


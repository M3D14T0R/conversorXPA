using System;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static bool LooksLikeTaskRunConstruction(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return false;

    var trimmed = expr.Trim();
    return trimmed.StartsWith("new ", StringComparison.Ordinal) &&
           ContainsMemberCallOutsideQuotes(trimmed, ".Run");
}

private static bool ContainsRuntimeHelperReference(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return false;

    for (var i = 0; i < expr.Length; i++)
    {
        var ch = expr[i];
        if (IsQuotedSegmentStart(expr, i))
        {
            if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
                return false;
            i = quoteEnd - 1;
            continue;
        }

        if (ch != 'u')
            continue;

        var prevOk = i == 0 || !(char.IsLetterOrDigit(expr[i - 1]) || expr[i - 1] == '_');
        var nextIndex = i + 1;
        if (prevOk && nextIndex < expr.Length && expr[nextIndex] == '.')
            return true;
    }

    return false;
}

private static bool ContainsIdentifierToken(string expr, string token)
{
    if (string.IsNullOrWhiteSpace(expr) || string.IsNullOrWhiteSpace(token))
        return false;

    for (var i = 0; i < expr.Length; i++)
    {
        var ch = expr[i];
        if (IsQuotedSegmentStart(expr, i))
        {
            if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
                return false;
            i = quoteEnd - 1;
            continue;
        }

        if (!TryReadIdentifierToken(expr, i, out var tokenEnd))
            continue;

        var identifier = expr[i..tokenEnd];
        if (string.Equals(identifier, token, StringComparison.Ordinal))
            return true;

        i = tokenEnd - 1;
    }

    return false;
}

private static bool ContainsFunctionCallOutsideQuotes(string expr, string functionName)
{
    if (string.IsNullOrWhiteSpace(expr) || string.IsNullOrWhiteSpace(functionName))
        return false;

    for (var i = 0; i < expr.Length; i++)
    {
        var ch = expr[i];
        if (IsQuotedSegmentStart(expr, i))
        {
            if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
                return false;
            i = quoteEnd - 1;
            continue;
        }

        if (!StartsWithAt(expr, i, functionName))
            continue;

        var prevOk = i == 0 || !(char.IsLetterOrDigit(expr[i - 1]) || expr[i - 1] == '_');
        var nextIndex = i + functionName.Length;
        if (prevOk && nextIndex < expr.Length && expr[nextIndex] == '(')
            return true;
    }

    return false;
}

private static bool ContainsMemberCallOutsideQuotes(string expr, string memberName)
{
    if (string.IsNullOrWhiteSpace(expr) || string.IsNullOrWhiteSpace(memberName))
        return false;

    for (var i = 0; i < expr.Length; i++)
    {
        var ch = expr[i];
        if (IsQuotedSegmentStart(expr, i))
        {
            if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
                return false;
            i = quoteEnd - 1;
            continue;
        }

        if (!StartsWithAt(expr, i, memberName))
            continue;

        var nextIndex = i + memberName.Length;
        if (nextIndex < expr.Length && expr[nextIndex] == '(')
            return true;
    }

    return false;
}
}


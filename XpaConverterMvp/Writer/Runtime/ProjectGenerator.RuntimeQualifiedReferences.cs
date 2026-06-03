using System;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static string RewriteWholeMemberReference(string expr, string memberName, string replacement)
{
    if (string.IsNullOrWhiteSpace(expr) || string.IsNullOrWhiteSpace(memberName))
        return expr;

    var result = new StringBuilder(expr.Length);
    for (var i = 0; i < expr.Length;)
    {
        if (i + memberName.Length <= expr.Length &&
            string.Compare(expr, i, memberName, 0, memberName.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
            !IsMemberReferenceBoundary(expr, i - 1) &&
            !IsMemberReferenceBoundary(expr, i + memberName.Length))
        {
            result.Append(replacement);
            i += memberName.Length;
            continue;
        }

        result.Append(expr[i]);
        i++;
    }

    return result.ToString();
}

private static string RewriteQualifiedPrefixOutsideQuotes(string expr, string prefix, string replacement)
{
    if (string.IsNullOrWhiteSpace(expr) || string.IsNullOrWhiteSpace(prefix))
        return expr;

    var result = new StringBuilder(expr.Length);
    for (var i = 0; i < expr.Length;)
    {
        if (IsQuotedSegmentStart(expr, i))
        {
            var quoteStart = i;
            if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
                break;
            result.Append(expr, quoteStart, (quoteEnd - quoteStart) + 1);
            i = quoteEnd + 1;
            continue;
        }

        if (StartsWithAt(expr, i, prefix) &&
            (i == 0 || !(char.IsLetterOrDigit(expr[i - 1]) || expr[i - 1] == '_' || expr[i - 1] == '.')))
        {
            result.Append(replacement);
            i += prefix.Length;
            continue;
        }

        result.Append(expr[i]);
        i++;
    }

    return result.ToString();
}

private static bool IsMemberReferenceBoundary(string text, int index)
{
    if (index < 0 || index >= text.Length)
        return false;

    var ch = text[index];
    return char.IsLetterOrDigit(ch) || ch == '_' || ch == '.';
}

private static string RewriteMalformedFunctionQualifier(string expr, string outerFunction, string innerFunction, string qualifiedInnerFunction)
{
    if (string.IsNullOrWhiteSpace(expr) ||
        string.IsNullOrWhiteSpace(outerFunction) ||
        string.IsNullOrWhiteSpace(innerFunction) ||
        string.IsNullOrWhiteSpace(qualifiedInnerFunction))
        return expr;

    var malformedCall = $"{outerFunction}({innerFunction})(";
    var qualifiedCall = $"{outerFunction}({qualifiedInnerFunction}(";
    return expr.Replace(malformedCall, qualifiedCall, StringComparison.OrdinalIgnoreCase);
}
}


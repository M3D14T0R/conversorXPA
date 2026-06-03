using System;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static string ApplyExpressionOperatorNormalization(string expr)
{
    expr = RepairAttachedBooleanOperatorsOutsideQuotes(expr);
    expr = RepairMalformedBooleanFunctionOperatorGlueOutsideQuotes(expr);
    expr = ReplaceWholeWordOutsideQuotes(expr, "AND", "&&");
    expr = ReplaceWholeWordOutsideQuotes(expr, "OR", "||");
    expr = ReplaceWholeWordOutsideQuotes(expr, "MOD", "%");
    expr = RewriteAmpersandConcatenationOutsideQuotes(expr);
    expr = RewriteComparisonOperatorsOutsideQuotes(expr);
    expr = RewriteNotOperatorsOutsideQuotes(expr);
    return expr;
}

private static string RepairAttachedBooleanOperatorsOutsideQuotes(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    var sb = new StringBuilder(expr.Length + 16);
    for (var i = 0; i < expr.Length;)
    {
        if (IsQuotedSegmentStart(expr, i))
        {
            var quoteStart = i;
            if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
                break;
            sb.Append(expr, quoteStart, (quoteEnd - quoteStart) + 1);
            i = quoteEnd + 1;
            continue;
        }

        if (TryMatchAttachedBooleanOperator(expr, i, "AND", out var andEnd) ||
            TryMatchAttachedBooleanOperator(expr, i, "OR", out andEnd))
        {
            if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1]))
                sb.Append(' ');
            sb.Append(expr, i, andEnd - i);
            if (andEnd < expr.Length && !char.IsWhiteSpace(expr[andEnd]))
                sb.Append(' ');
            i = andEnd;
            continue;
        }

        sb.Append(expr[i]);
        i++;
    }

    return sb.ToString();
}

private static bool TryMatchAttachedBooleanOperator(string expression, int startIndex, string token, out int endIndex)
{
    endIndex = startIndex;
    if (startIndex <= 0 ||
        startIndex + token.Length >= expression.Length ||
        !expression.AsSpan(startIndex, token.Length).Equals(token, StringComparison.OrdinalIgnoreCase))
        return false;

    if (!HasCompleteSourceBooleanLeftOperand(expression, startIndex) ||
        !HasCompleteSourceBooleanRightOperand(expression, startIndex + token.Length))
        return false;

    var previous = expression[startIndex - 1];
    var next = expression[startIndex + token.Length];
    var attachedToLeft = !char.IsWhiteSpace(previous);
    var attachedToRight = !char.IsWhiteSpace(next);
    if (!attachedToLeft && !attachedToRight)
        return false;

    if (attachedToLeft && !CanSplitBooleanOperatorAfterLeftOperand(expression, startIndex))
        return false;

    endIndex = startIndex + token.Length;
    return true;
}

private static bool CanSplitBooleanOperatorAfterLeftOperand(string expression, int operatorStart)
{
    var previous = operatorStart - 1;
    while (previous >= 0 && char.IsWhiteSpace(expression[previous]))
        previous--;
    if (previous < 0)
        return false;

    var ch = expression[previous];
    if (ch is ')' or ']' or '"' or '\'')
        return true;

    if (!char.IsDigit(ch))
        return false;

    var cursor = previous;
    while (cursor >= 0 && (char.IsDigit(expression[cursor]) || expression[cursor] == '.' || expression[cursor] == ','))
        cursor--;

    return cursor < 0 ||
           !(char.IsLetterOrDigit(expression[cursor]) || expression[cursor] == '_' || expression[cursor] == '.');
}

private static string RepairMalformedBooleanFunctionOperatorGlueOutsideQuotes(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    var sb = new StringBuilder(expr.Length + 16);
    for (var i = 0; i < expr.Length;)
    {
        if (IsQuotedSegmentStart(expr, i))
        {
            var quoteStart = i;
            if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
                break;
            sb.Append(expr, quoteStart, (quoteEnd - quoteStart) + 1);
            i = quoteEnd + 1;
            continue;
        }

        if (TryMatchMalformedBooleanFunctionOperator(expr, i, "AND", out var andEnd))
        {
            sb.Append("AND ");
            i = andEnd;
            continue;
        }

        if (TryMatchMalformedBooleanFunctionOperator(expr, i, "OR", out var orEnd))
        {
            sb.Append("OR ");
            i = orEnd;
            continue;
        }

        if (TryMatchMalformedBooleanFunctionOperator(expr, i, "NOT", out var notEnd))
        {
            sb.Append("NOT ");
            i = notEnd;
            continue;
        }

        sb.Append(expr[i]);
        i++;
    }

    return sb.ToString();
}

private static bool TryMatchMalformedBooleanFunctionOperator(string expression, int startIndex, string token, out int endIndex)
{
    endIndex = startIndex;
    if (string.IsNullOrWhiteSpace(expression) ||
        startIndex < 0 ||
        startIndex + token.Length >= expression.Length ||
        !expression.AsSpan(startIndex).StartsWith(token, StringComparison.OrdinalIgnoreCase))
        return false;

    var previous = startIndex - 1;
    if (previous >= 0 && (char.IsLetterOrDigit(expression[previous]) || expression[previous] == '_'))
        return false;

    var previousSignificant = previous;
    while (previousSignificant >= 0 && char.IsWhiteSpace(expression[previousSignificant]))
        previousSignificant--;
    if (previousSignificant < 0)
        return false;

    var previousChar = expression[previousSignificant];
    var previousLooksLikeOperand =
        char.IsLetterOrDigit(previousChar) ||
        previousChar is ')' or ']' or '"' or '\'';
    if (!previousLooksLikeOperand)
        return false;

    var cursor = startIndex + token.Length;
    if (cursor >= expression.Length || !TryReadIdentifierPath(expression, cursor, out var identifierEnd))
        return false;

    var lookahead = identifierEnd;
    while (lookahead < expression.Length && char.IsWhiteSpace(expression[lookahead]))
        lookahead++;

    if (lookahead >= expression.Length || expression[lookahead] != '(')
        return false;

    endIndex = cursor;
    return true;
}

private static string RewriteAmpersandConcatenationOutsideQuotes(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    var sb = new StringBuilder(expr.Length + 16);
    for (var i = 0; i < expr.Length; i++)
    {
        if (IsQuotedSegmentStart(expr, i))
        {
            var quoteStart = i;
            if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
                break;
            sb.Append(expr, quoteStart, (quoteEnd - quoteStart) + 1);
            i = quoteEnd;
            continue;
        }

        if (expr[i] == '&')
        {
            var prev = i > 0 ? expr[i - 1] : '\0';
            var next = i + 1 < expr.Length ? expr[i + 1] : '\0';
            if (prev != '&' && next != '&')
            {
                sb.Append(" + ");
                continue;
            }
        }

        sb.Append(expr[i]);
    }

    return sb.ToString();
}

private static string RewriteComparisonOperatorsOutsideQuotes(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    var sb = new StringBuilder(expr.Length + 16);
    for (var i = 0; i < expr.Length; i++)
    {
        if (IsQuotedSegmentStart(expr, i))
        {
            var quoteStart = i;
            if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
                break;
            sb.Append(expr, quoteStart, (quoteEnd - quoteStart) + 1);
            i = quoteEnd;
            continue;
        }

        if (expr[i] == '<' && i + 1 < expr.Length && expr[i + 1] == '>')
        {
            sb.Append("!=");
            i++;
            continue;
        }

        if (expr[i] == '=')
        {
            var prev = i > 0 ? expr[i - 1] : '\0';
            var next = i + 1 < expr.Length ? expr[i + 1] : '\0';
            if (prev != '<' && prev != '>' && prev != '!' && prev != '=' && next != '=')
            {
                sb.Append("==");
                continue;
            }
        }

        sb.Append(expr[i]);
    }

    return sb.ToString();
}

private static string RewriteNotOperatorsOutsideQuotes(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    var sb = new StringBuilder(expr.Length + 16);
    for (var i = 0; i < expr.Length;)
    {
        if (IsQuotedSegmentStart(expr, i))
        {
            var quoteStart = i;
            if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
                break;
            sb.Append(expr, quoteStart, (quoteEnd - quoteStart) + 1);
            i = quoteEnd + 1;
            continue;
        }

        if (MatchesWholeWord(expr, i, "NOT", out var notEnd))
        {
            var cursor = notEnd;
            while (cursor < expr.Length && char.IsWhiteSpace(expr[cursor]))
                cursor++;

            if (cursor < expr.Length && expr[cursor] == '(')
            {
                sb.Append("u.Not(");
                i = cursor + 1;
                continue;
            }

            if (TryReadIdentifierPath(expr, cursor, out var idEnd))
            {
                if (TryReadComparisonTail(expr, idEnd, out var comparisonEnd))
                {
                    sb.Append("u.Not(");
                    sb.Append(expr, cursor, comparisonEnd - cursor);
                    sb.Append(')');
                    i = comparisonEnd;
                    continue;
                }

                sb.Append("u.Not(");
                sb.Append(expr, cursor, idEnd - cursor);
                sb.Append(')');
                i = idEnd;
                continue;
            }

            sb.Append('!');
            i = notEnd;
            continue;
        }

        if (expr[i] == '!')
        {
            var cursor = i + 1;
            while (cursor < expr.Length && char.IsWhiteSpace(expr[cursor]))
                cursor++;

            if (cursor < expr.Length && expr[cursor] == '(')
            {
                sb.Append("u.Not(");
                i = cursor + 1;
                continue;
            }

            if (TryReadIdentifierPath(expr, cursor, out var idEnd))
            {
                if (TryReadComparisonTail(expr, idEnd, out var comparisonEnd))
                {
                    sb.Append("u.Not(");
                    sb.Append(expr, cursor, comparisonEnd - cursor);
                    sb.Append(')');
                    i = comparisonEnd;
                    continue;
                }

                sb.Append("u.Not(");
                sb.Append(expr, cursor, idEnd - cursor);
                sb.Append(')');
                i = idEnd;
                continue;
            }
        }

        sb.Append(expr[i]);
        i++;
    }

    return sb.ToString();
}

private static bool TryReadComparisonTail(string expression, int identifierEnd, out int comparisonEnd)
{
    comparisonEnd = identifierEnd;
    var cursor = identifierEnd;
    while (cursor < expression.Length && char.IsWhiteSpace(expression[cursor]))
        cursor++;

    var opLength = 0;
    if (cursor + 1 < expression.Length)
    {
        var pair = expression.Substring(cursor, 2);
        if (pair is "==" or "!=" or "<=" or ">=" or "<>")
            opLength = 2;
    }

    if (opLength == 0 && cursor < expression.Length && expression[cursor] is '<' or '>' or '=')
        opLength = 1;

    if (opLength == 0)
        return false;

    cursor += opLength;
    while (cursor < expression.Length && char.IsWhiteSpace(expression[cursor]))
        cursor++;

    var depth = 0;
    for (var i = cursor; i < expression.Length; i++)
    {
        if (IsQuotedSegmentStart(expression, i))
        {
            if (!TryReadQuotedSegmentEnd(expression, i, out var quoteEnd))
                return false;
            i = quoteEnd;
            comparisonEnd = i + 1;
            continue;
        }

        var ch = expression[i];
        if (ch == '(')
        {
            depth++;
            comparisonEnd = i + 1;
            continue;
        }

        if (ch == ')')
        {
            if (depth == 0)
                break;

            depth--;
            comparisonEnd = i + 1;
            continue;
        }

        if (depth == 0 && i + 1 < expression.Length)
        {
            var pair = expression.Substring(i, 2);
            if (pair is "&&" or "||")
                break;
        }

        comparisonEnd = i + 1;
    }

    return comparisonEnd > cursor;
}

private static bool MatchesWholeWord(string text, int startIndex, string word, out int endIndex)
{
    endIndex = startIndex;
    if (startIndex < 0 || startIndex + word.Length > text.Length)
        return false;

    if (!string.Equals(text.Substring(startIndex, word.Length), word, StringComparison.OrdinalIgnoreCase))
        return false;

    var prev = startIndex > 0 ? text[startIndex - 1] : '\0';
    var nextIndex = startIndex + word.Length;
    var next = nextIndex < text.Length ? text[nextIndex] : '\0';
    if ((char.IsLetterOrDigit(prev) || prev == '_') || (char.IsLetterOrDigit(next) || next == '_'))
        return false;

    endIndex = nextIndex;
    return true;
}
}


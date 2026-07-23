using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static string ApplyExpressionLiteralNormalization(string expr, IReadOnlyList<DataObjectDef> dataObjects, string? expressionAttr = null)
{
    expr = RewriteLiteralPostfixTokens(expr, dataObjects, expressionAttr);
    expr = RewriteUnquotedEmptyDateLiteralTokens(expr);
    expr = NormalizeRightsExpressions(expr);
    expr = ReplaceXpaSingleQuotedLiterals(expr);
    expr = NormalizeRightsExpressions(expr);
    expr = NormalizeBooleanLiteralTokens(expr);
    return expr;
}

private static string RewriteUnquotedEmptyDateLiteralTokens(string expr)
{
    if (string.IsNullOrWhiteSpace(expr) ||
        expr.IndexOf("00/00/0000", StringComparison.Ordinal) < 0)
        return expr;

    const string token = "00/00/0000";
    var sb = new StringBuilder(expr.Length + 24);
    for (var i = 0; i < expr.Length;)
    {
        if (IsQuotedSegmentStart(expr, i))
        {
            if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
            {
                sb.Append(expr[i]);
                i++;
                continue;
            }

            sb.Append(expr, i, quoteEnd - i + 1);
            i = quoteEnd + 1;
            continue;
        }

        if (i + token.Length <= expr.Length &&
            string.Equals(expr.Substring(i, token.Length), token, StringComparison.Ordinal) &&
            IsEmptyDateLiteralBoundary(expr, i - 1) &&
            IsEmptyDateLiteralBoundary(expr, i + token.Length))
        {
            sb.Append("XPARuntimeCore.Box.Date.Empty");
            i += token.Length;
            continue;
        }

        sb.Append(expr[i]);
        i++;
    }

    return sb.ToString();
}

private static bool IsEmptyDateLiteralBoundary(string text, int index)
{
    if (index < 0 || index >= text.Length)
        return true;

    var ch = text[index];
    return !char.IsLetterOrDigit(ch) && ch != '_' && ch != '/';
}

private static string NormalizeBooleanLiteralTokens(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    expr = ReplaceWholeWordOutsideQuotes(expr, "TRUE", "true");
    expr = ReplaceWholeWordOutsideQuotes(expr, "FALSE", "false");
    return expr;
}

private static string ReplaceXpaSingleQuotedLiterals(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    var sb = new StringBuilder(expr.Length + 16);
    for (var i = 0; i < expr.Length; i++)
    {
        var ch = expr[i];
        if (ch != '\'')
        {
            sb.Append(ch);
            continue;
        }

        var literal = new StringBuilder();
        i++;
        while (i < expr.Length)
        {
            if (expr[i] == '\'')
            {
                if (i + 1 < expr.Length && expr[i + 1] == '\'')
                {
                    literal.Append('\'');
                    i += 2;
                    continue;
                }
                break;
            }

            literal.Append(expr[i]);
            i++;
        }

        sb.Append(ToCSharpLiteral(literal.ToString()));
    }

    return sb.ToString();
}

private static string RewriteLiteralPostfixTokens(string expr, IReadOnlyList<DataObjectDef> dataObjects, string? expressionAttr = null)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    var sb = new StringBuilder(expr.Length + 16);
    var i = 0;
    while (i < expr.Length)
    {
        if (expr[i] is not ('"' or '\''))
        {
            sb.Append(expr[i]);
            i++;
            continue;
        }

        if (!TryReadXpaLiteral(expr, i, out var endIndex, out var literalValue, out var literalCode))
        {
            sb.Append(expr[i]);
            i++;
            continue;
        }

        var cursor = endIndex + 1;
        while (cursor < expr.Length && char.IsWhiteSpace(expr[cursor]))
            cursor++;

        if (TryReadLiteralPostfixToken(expr, cursor, out var token, out var tokenEnd))
        {
            var replacement = RewriteLiteralPostfixToken(token, literalValue, dataObjects, expressionAttr);
            if (!string.IsNullOrWhiteSpace(replacement))
            {
                sb.Append(replacement);
                i = tokenEnd + 1;
                continue;
            }
        }

        sb.Append(literalCode);
        i = endIndex + 1;
    }

    return sb.ToString();
}

private static string? RewriteLiteralPostfixToken(string token, string literalValue, IReadOnlyList<DataObjectDef> dataObjects, string? expressionAttr = null)
{
    if (string.Equals(token, "DSOURCE", StringComparison.OrdinalIgnoreCase))
        return ResolveDataSourceLiteralExpression(literalValue, dataObjects, expressionAttr);

    if (string.Equals(token, "TIME", StringComparison.OrdinalIgnoreCase))
        return RewriteTimeLiteralExpression(literalValue);

    if (string.Equals(token, "DATE", StringComparison.OrdinalIgnoreCase))
        return RewriteDateLiteralExpression(literalValue);

    if (string.Equals(token, "LOG", StringComparison.OrdinalIgnoreCase))
    {
        if (string.Equals(literalValue, "TRUE", StringComparison.OrdinalIgnoreCase))
            return "true";
        if (string.Equals(literalValue, "FALSE", StringComparison.OrdinalIgnoreCase))
            return "false";
    }

    if (string.Equals(token, "MODE", StringComparison.OrdinalIgnoreCase))
        return ToCSharpLiteral(literalValue);

    if (string.Equals(token, "INDEX", StringComparison.OrdinalIgnoreCase))
    {
        if (int.TryParse(literalValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var indexId))
            return indexId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ToCSharpLiteral(literalValue);
    }

    if (string.Equals(token, "HEB", StringComparison.OrdinalIgnoreCase))
        return ToCSharpLiteral(literalValue);

    if (string.Equals(token, "KBD", StringComparison.OrdinalIgnoreCase))
        return ToCSharpLiteral($"<{literalValue}>");

    if (string.Equals(token, "EVENT", StringComparison.OrdinalIgnoreCase))
        return ToCSharpLiteral($"[{literalValue}]");

    if (string.Equals(token, "EXP", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(literalValue, out var expressionOrdinal) &&
        expressionOrdinal > 0)
        return $"Exp_{expressionOrdinal}()";

    if (string.Equals(token, "RIGHT", StringComparison.OrdinalIgnoreCase))
    {
        if (_componentRightLiteralMap.TryGetValue(literalValue, out var mapped))
            return mapped;
        return $"u.Rights({ToCSharpLiteral(literalValue)})";
    }

    if (string.Equals(token, "MENU", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(literalValue, out var menuId))
        return menuId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    if (string.Equals(token, "FORM", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(literalValue, out var formId))
        return formId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    return null;
}

private static string? RewriteDateLiteralExpression(string literalValue)
{
    if (string.IsNullOrWhiteSpace(literalValue))
        return null;

    var parts = literalValue.Split('/');
    if (parts.Length != 3 ||
        !int.TryParse(parts[0], out var day) ||
        !int.TryParse(parts[1], out var month) ||
        !int.TryParse(parts[2], out var year))
        return null;

    if (day <= 0 || month <= 0 || year <= 0)
        return "XPARuntimeCore.Box.Date.Empty";

    return $"new Date({year}, {month}, {day})";
}

private static string RewriteTimeLiteralExpression(string literalValue)
{
    var picture = InferTimeLiteralPicture(literalValue);
    if (string.IsNullOrWhiteSpace(picture))
        return EnsureTimeCallArgumentCentral(ToCSharpLiteral(literalValue));
    return $"u.TVal({ToCSharpLiteral(literalValue)}, {ToCSharpLiteral(picture)})";
}

private static string? InferTimeLiteralPicture(string literalValue)
{
    if (string.IsNullOrWhiteSpace(literalValue))
        return null;

    if (Regex.IsMatch(literalValue, @"^\d{2}:\d{2}:\d{2}$"))
        return "HH:MM:SS";
    if (Regex.IsMatch(literalValue, @"^\d{2}:\d{2}$"))
        return "HH:MM";
    if (Regex.IsMatch(literalValue, @"^\d{6}$"))
        return "HHMMSS";
    if (Regex.IsMatch(literalValue, @"^\d{4}$"))
        return "HHMM";

    return null;
}

private static bool TryReadLiteralPostfixToken(string text, int start, out string token, out int tokenEnd)
{
    token = "";
    tokenEnd = start - 1;
    if (start >= text.Length || !char.IsLetter(text[start]))
        return false;

    var i = start;
    while (i < text.Length && char.IsLetter(text[i]))
        i++;

    token = text[start..i];
    tokenEnd = i - 1;
    return token.Length > 0;
}

private static bool TryReadXpaLiteral(string text, int startIndex, out int endIndex, out string literalValue, out string literalCode)
{
    endIndex = startIndex;
    literalValue = "";
    literalCode = "";
    if (startIndex < 0 || startIndex >= text.Length)
        return false;

    var quote = text[startIndex];
    if (quote is not ('"' or '\''))
        return false;

    var sb = new StringBuilder();
    var i = startIndex + 1;
    while (i < text.Length)
    {
        if (text[i] == quote)
        {
            if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
            {
                sb.Append('\'');
                i += 2;
                continue;
            }

            if (quote == '"' && i > startIndex && text[i - 1] == '\\')
            {
                sb.Append('"');
                i++;
                continue;
            }

            endIndex = i;
            literalValue = sb.ToString();
            literalCode = text[startIndex..(endIndex + 1)];
            return true;
        }

        sb.Append(text[i]);
        i++;
    }

    return false;
}

private static string ResolveDataSourceLiteralExpression(string literal, IReadOnlyList<DataObjectDef> dataObjects, string? expressionAttr = null)
{
    if (string.IsNullOrWhiteSpace(literal))
        return "\"\"";
    var firstPart = literal.Split(',')[0].Trim();
    if (int.TryParse(firstPart, out var objectOrdinal))
    {
        if (string.Equals(expressionAttr, "N", StringComparison.OrdinalIgnoreCase))
            return objectOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var direct = ResolveDataObjectByOrdinal(dataObjects, objectOrdinal);
        if (direct is not null)
            return $"typeof({ResolveEntityTypeReferenceForRegistry(direct)})";
        if (_dataSourceTypeByObjectOrdinal.TryGetValue(objectOrdinal, out var mapped))
            return mapped;
    }
    return $"\"{Escape(literal)}\"";
}
}


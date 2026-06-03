using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static readonly ConcurrentDictionary<string, bool> DotNetConstructorEvidenceCache = new(StringComparer.Ordinal);

private static string NormalizeDotNetArrayConstructorMappings(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    expr = RewriteDotNetArrayConstructors(expr);
    expr = RewriteDotNetObjectConstructors(expr);
    expr = RewriteQualifiedPrefixOutsideQuotes(expr, "DotNet.System.", "System.");
    expr = RewriteQualifiedPrefixOutsideQuotes(expr, "DotNet.PushSharp.", "PushSharp.");
    expr = RewriteQualifiedPrefixOutsideQuotes(expr, "DotNet.", "");
    expr = RewriteClrObjectConstructors(expr);
    expr = RewriteQualifiedConstructorInvocations(expr, "System.Uri", args => $"new System.Uri({string.Join(", ", args)})");
    expr = RewriteQualifiedConstructorInvocations(expr, "System.Drawing.Font", BuildSystemDrawingFontConstructor);
    expr = RewriteQualifiedConstructorInvocations(expr, "System.Windows.Forms.ColorDialog", args => $"new System.Windows.Forms.ColorDialog({string.Join(", ", args)})");
    expr = RewriteQualifiedConstructorInvocations(expr, "System.Windows.Forms.FontDialog", args => $"new System.Windows.Forms.FontDialog({string.Join(", ", args)})");
    return expr;
}

private static string? BuildSystemDrawingFontConstructor(List<string> args)
{
    if (args.Count < 2)
        return null;

    var renderedArgs = args.Select(a => a.Trim()).ToList();
    renderedArgs[1] = EnsureFloatLiteralForSystemDrawingFontSize(renderedArgs[1]);
    return $"new System.Drawing.Font({string.Join(", ", renderedArgs)})";
}

private static string EnsureFloatLiteralForSystemDrawingFontSize(string expression)
{
    var trimmed = expression.Trim();
    if (string.IsNullOrWhiteSpace(trimmed) ||
        trimmed.EndsWith("F", StringComparison.OrdinalIgnoreCase) ||
        trimmed.EndsWith("D", StringComparison.OrdinalIgnoreCase) ||
        trimmed.EndsWith("M", StringComparison.OrdinalIgnoreCase))
        return expression;

    if (!double.TryParse(
            trimmed,
            System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture,
            out _) ||
        trimmed.IndexOf('.') < 0)
    {
        return expression;
    }

    return trimmed + "F";
}

private static string RewriteClrObjectConstructors(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    var index = 0;
    while (index < expr.Length)
    {
        if (index > 0 && string.Equals(expr.Substring(Math.Max(0, index - 4), Math.Min(4, index)), "new ", StringComparison.Ordinal))
        {
            index++;
            continue;
        }

        if (!(char.IsLetter(expr[index]) || expr[index] == '_'))
        {
            index++;
            continue;
        }

        var typeStart = index;
        var cursor = index;
        var sawDot = false;
        while (cursor < expr.Length && (char.IsLetterOrDigit(expr[cursor]) || expr[cursor] == '_' || expr[cursor] == '.'))
        {
            sawDot |= expr[cursor] == '.';
            cursor++;
        }

        if (!sawDot ||
            typeStart > 0 && (char.IsLetterOrDigit(expr[typeStart - 1]) || expr[typeStart - 1] == '_' || expr[typeStart - 1] == '.'))
        {
            index = cursor + 1;
            continue;
        }

        var typeName = expr[typeStart..cursor].Trim();
        while (cursor < expr.Length && char.IsWhiteSpace(expr[cursor]))
            cursor++;

        if (cursor >= expr.Length || expr[cursor] != '(')
        {
            index = cursor + 1;
            continue;
        }

        var closeParen = FindMatchingParen(expr, cursor);
        if (closeParen < 0)
            break;

        var args = SplitTopLevelArguments(expr[(cursor + 1)..closeParen]);
        if (!HasClrConstructorOrMemberAccessEvidence(expr, closeParen, typeName, args.Count))
        {
            index = closeParen + 1;
            continue;
        }

        var replacement = $"new {typeName}({string.Join(", ", args)})";
        expr = expr[..typeStart] + replacement + expr[(closeParen + 1)..];
        index = typeStart + replacement.Length;
    }

    return expr;
}

private static string RewriteDotNetObjectConstructors(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    const string marker = "DotNet.";
    var index = expr.IndexOf(marker, StringComparison.Ordinal);
    while (index >= 0)
    {
        if (index > 0 && (char.IsLetterOrDigit(expr[index - 1]) || expr[index - 1] == '_' || expr[index - 1] == '.'))
        {
            index = expr.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
            continue;
        }

        var typeStart = index + marker.Length;
        var cursor = typeStart;
        if (cursor >= expr.Length || !(char.IsLetter(expr[cursor]) || expr[cursor] == '_'))
        {
            index = expr.IndexOf(marker, typeStart, StringComparison.Ordinal);
            continue;
        }

        while (cursor < expr.Length && (char.IsLetterOrDigit(expr[cursor]) || expr[cursor] == '_' || expr[cursor] == '.'))
            cursor++;

        var typeName = expr[typeStart..cursor].Trim();
        while (cursor < expr.Length && char.IsWhiteSpace(expr[cursor]))
            cursor++;

        if (cursor >= expr.Length || expr[cursor] != '(')
        {
            index = expr.IndexOf(marker, cursor, StringComparison.Ordinal);
            continue;
        }

        var closeParen = FindMatchingParen(expr, cursor);
        if (closeParen < 0)
            break;

        var args = SplitTopLevelArguments(expr[(cursor + 1)..closeParen]);
        if (!HasClrConstructorOrMemberAccessEvidence(expr, closeParen, typeName, args.Count))
        {
            index = expr.IndexOf(marker, closeParen + 1, StringComparison.Ordinal);
            continue;
        }

        var replacement = $"new {typeName}({string.Join(", ", args)})";
        var replacementStart = index >= "new ".Length &&
                               string.Equals(expr[(index - "new ".Length)..index], "new ", StringComparison.Ordinal)
            ? index - "new ".Length
            : index;
        expr = expr[..replacementStart] + replacement + expr[(closeParen + 1)..];
        index = expr.IndexOf(marker, replacementStart + replacement.Length, StringComparison.Ordinal);
    }

    return expr;
}

private static bool HasClrConstructorOrMemberAccessEvidence(
    string expression,
    int closeParen,
    string typeName,
    int argumentCount)
{
    if (HasClrConstructorEvidence(typeName, argumentCount))
        return true;

    return TryReadMemberPathAfterConstructor(expression, closeParen, out var memberPath) &&
           HasDotNetMemberEvidence(typeName, memberPath);
}

private static bool TryReadMemberPathAfterConstructor(string expression, int closeParen, out string memberPath)
{
    memberPath = "";
    var cursor = closeParen + 1;
    if (cursor >= expression.Length || expression[cursor] != '.')
        return false;

    cursor++;
    var start = cursor;
    while (cursor < expression.Length &&
           (char.IsLetterOrDigit(expression[cursor]) || expression[cursor] == '_' || expression[cursor] == '.'))
    {
        cursor++;
    }

    memberPath = expression[start..cursor].Trim('.');
    return !string.IsNullOrWhiteSpace(memberPath);
}

private static bool HasClrConstructorEvidence(string typeName, int argumentCount)
{
    if (string.IsNullOrWhiteSpace(typeName) || argumentCount < 0)
        return false;

    var normalizedTypeName = typeName.Trim();
    var assemblyPath = FindMappedAssemblyPathForDotNetType(normalizedTypeName);
    var cacheKey = BuildDotNetEvidenceCacheKey(
        normalizedTypeName,
        "ctor|" + argumentCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        assemblyPath);
    return DotNetConstructorEvidenceCache.GetOrAdd(cacheKey, _ =>
    {
        if (HasClrConstructorEvidenceFromMetadata(normalizedTypeName, argumentCount))
            return true;

        if (!TryLoadClrTypeFromMappedReference(normalizedTypeName, out var clrType) || clrType is null)
            return false;

        return clrType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Any(ctor => ctor.GetParameters().Length == argumentCount);
    });
}

private static string RewriteDotNetArrayConstructors(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    const string marker = "DotNet.";
    var index = expr.IndexOf(marker, StringComparison.Ordinal);
    while (index >= 0)
    {
        if (index > 0 && (char.IsLetterOrDigit(expr[index - 1]) || expr[index - 1] == '_' || expr[index - 1] == '.'))
        {
            index = expr.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
            continue;
        }

        var typeStart = index + marker.Length;
        var cursor = typeStart;
        if (cursor >= expr.Length || !(char.IsLetter(expr[cursor]) || expr[cursor] == '_'))
        {
            index = expr.IndexOf(marker, typeStart, StringComparison.Ordinal);
            continue;
        }

        while (cursor < expr.Length && (char.IsLetterOrDigit(expr[cursor]) || expr[cursor] == '_' || expr[cursor] == '.'))
            cursor++;

        while (cursor < expr.Length && char.IsWhiteSpace(expr[cursor]))
            cursor++;

        if (cursor >= expr.Length || expr[cursor] != '[')
        {
            index = expr.IndexOf(marker, cursor, StringComparison.Ordinal);
            continue;
        }

        var closeBracket = FindMatchingBracket(expr, cursor);
        if (closeBracket < 0)
            break;

        var typeName = expr[typeStart..cursor].Trim();
        var sizeExpr = expr[(cursor + 1)..closeBracket].Trim();
        var replacement = $"new {typeName}[{sizeExpr}]";
        expr = expr[..index] + replacement + expr[(closeBracket + 1)..];
        index = expr.IndexOf(marker, index + replacement.Length, StringComparison.Ordinal);
    }

    return expr;
}

private static int FindMatchingBracket(string text, int openBracketIndex)
{
    var depth = 0;
    for (var i = openBracketIndex; i < text.Length; i++)
    {
        if (text[i] == '[') depth++;
        else if (text[i] == ']')
        {
            depth--;
            if (depth == 0)
                return i;
        }
    }
    return -1;
}
}


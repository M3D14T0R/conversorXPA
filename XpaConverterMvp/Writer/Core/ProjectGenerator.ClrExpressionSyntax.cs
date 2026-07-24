using System;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool TryResolveNewClrExpressionReturnType(string expression, out string returnType)
    {
        returnType = "";
        var value = expression?.Trim() ?? "";
        if (!value.StartsWith("new ", StringComparison.Ordinal))
            return false;

        var start = 4;
        var end = value.IndexOfAny(['(', '{', '[', ' '], start);
        if (end < 0)
            end = value.Length;
        if (end <= start)
            return false;

        returnType = value[start..end].Trim();
        return returnType.Length > 0;
    }

    private static string StripLeadingDotNetQualifier(string functionName)
    {
        var value = functionName?.Trim() ?? "";
        while (value.StartsWith("global::", StringComparison.Ordinal))
            value = value["global::".Length..];
        while (value.StartsWith("System.", StringComparison.Ordinal) &&
               value.Count(c => c == '.') > 1)
            value = value["System.".Length..];
        return value;
    }

    private static bool LooksLikeClrConstructorName(string functionName)
    {
        var value = StripLeadingDotNetQualifier(functionName);
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var leaf = value[(value.LastIndexOf('.') + 1)..];
        return leaf.Length > 0 && char.IsUpper(leaf[0]);
    }

    private static string StripDotNetQualifierOutsideQuotes(string expression)
        => expression?.Replace("global::", "", StringComparison.Ordinal) ?? "";

    private static string EmitDotNetAssignmentExpression(string expression, string? objectType)
        => expression;

    private static bool TryEmitBlobWrappedNewClrExpression(string expression, out string emitted)
    {
        emitted = expression;
        return false;
    }

    private static bool TryBuildBlobVariantAssignment(
        string target,
        string value,
        string? attrObj,
        bool preferValue,
        out string assignment)
    {
        assignment = "";
        return false;
    }

    private static string? TryEmitDnCast(string valueExpression, string typeExpression)
        => null;

    private static string? TryEmitUnsupportedEnvironmentExpression(ExpressionEntrySemantic expression)
        => null;

    private static bool TryUnwrapDirectParameterBindingExpression(
        string expression,
        out string directBinding)
    {
        directBinding = "";
        var value = expression?.Trim() ?? "";
        if (!value.StartsWith("() =>", StringComparison.Ordinal))
            return false;
        directBinding = value["() =>".Length..].Trim();
        return directBinding.Length > 0;
    }
}

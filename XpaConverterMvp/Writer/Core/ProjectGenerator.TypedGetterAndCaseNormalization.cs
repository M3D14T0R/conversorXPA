using System;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string RewriteTypedGetterWrapperPatterns(string? attr, string expression)
    {
        if (string.IsNullOrWhiteSpace(attr) || string.IsNullOrWhiteSpace(expression))
            return expression;

        expression = attr.ToUpperInvariant() switch
        {
            "N" => CollapseTypedGetterWrapperPatterns(
                expression,
                "u.GetNumberParam",
                "u.SharedValGetNumber"),
            "T" => CollapseTypedGetterWrapperPatterns(
                expression,
                "u.GetTimeParam",
                "u.SharedValGetTime"),
            "D" => CollapseTypedGetterWrapperPatterns(
                expression,
                "u.GetDateParam",
                "u.SharedValGetDate"),
            "B" => CollapseTypedGetterWrapperPatterns(
                expression,
                "u.GetBoolParam",
                "u.SharedValGetBool"),
            _ => expression
        };

        return expression;
    }

    private static string CollapseTypedGetterWrapperPatterns(string expression, params string[] getterNames)
    {
        if (string.IsNullOrWhiteSpace(expression) || getterNames is null || getterNames.Length == 0)
            return expression;

        var result = expression;
        foreach (var getterName in getterNames)
        {
            result = RewriteFunctionCalls(result, "u.Val", args =>
            {
                if (args.Count >= 1 && TryCollapseTrimmedTypedGetter(args[0], getterName, out var collapsed))
                    return collapsed;
                return null;
            });

            result = RewriteFunctionCalls(result, "u.Trim", args =>
            {
                if (args.Count == 1 && TryCollapseTrimmedTypedGetter(args[0], getterName, out var collapsed))
                    return collapsed;
                return null;
            });
        }

        return result;
    }

    private static bool TryCollapseTrimmedTypedGetter(string expression, string getterName, out string collapsed)
    {
        collapsed = "";
        var trimmed = expression?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        if (TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            string.Equals(functionName, "u.Trim", StringComparison.OrdinalIgnoreCase) &&
            args.Count == 1)
            trimmed = args[0].Trim();

        if (!TryParseFunctionCall(trimmed, out functionName, out args) ||
            !string.Equals(functionName, getterName, StringComparison.OrdinalIgnoreCase))
            return false;

        collapsed = $"{getterName}({string.Join(", ", args)})";
        return true;
    }

    private static string NormalizeAlphaCaseExpression(string translated)
    {
        if (!TryParseFunctionCall(translated, out var functionName, out var args))
            return translated;

        if (!string.Equals(functionName, "u.Case", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(functionName, "u.CaseUntyped", StringComparison.OrdinalIgnoreCase))
            return translated;

        for (var i = 2; i < args.Count; i++)
            args[i] = NormalizeAlphaCaseBranch(args[i]);

        return $"u.CaseUntyped({string.Join(", ", args)})";
    }

    private static string NormalizeVariantCaseExpression(string translated)
    {
        if (!TryParseFunctionCall(translated, out var functionName, out var args))
            return translated;

        if (!string.Equals(functionName, "u.Case", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(functionName, "u.CaseUntyped", StringComparison.OrdinalIgnoreCase))
            return translated;

        for (var i = 2; i < args.Count; i++)
        {
            var discriminator = i >= 2 ? args[i - 1] : null;
            args[i] = NormalizeVariantCaseBranchForDiscriminatorCentral(discriminator, args[i]);
        }

        return $"u.CaseUntyped({string.Join(", ", args)})";
    }

    private static string NormalizeAlphaCaseBranch(string expression)
        => NormalizeAlphaCaseBranchCentral(expression);

    private static string NormalizeVariantCaseBranch(string expression)
        => NormalizeVariantCaseBranchCentral(expression);

    private static bool IsVarCurrentLikeExpression(string expression)
    {
        var trimmed = expression.Trim();
        return trimmed.StartsWith("u.VarCurr(", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("u.VarCurrN(", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("u.VarPrev(", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVarPictureExpression(string expression)
    {
        return expression.Trim().StartsWith("u.VarPic(", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVariantGetExpression(string expression)
    {
        return expression.Trim().StartsWith("u.VariantGet(", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVariantRuntimeFunctionArguments(string expression)
        => NormalizeVariantRuntimeFunctionArgumentsCentral(expression);

    private static bool NeedsVariantRuntimeCast(string expression)
    {
        var trimmed = expression?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        return IsVarCurrentLikeExpression(trimmed) || IsVariantGetExpression(trimmed);
    }

    private static string RewriteStaticUserMethodCalls(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        return expression;
    }
}


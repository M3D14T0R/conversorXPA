using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ApplyXpaFunctionMap(string expr, TaskSemantic task, string? expressionAttr = null)
    {
        var localFunctionNames = task.FunctionOverridesSemantic
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accessibleParentFunctions = ResolveAccessibleParentFunctionTargets(task);
        var accessibleApplicationFunctions = ResolveAccessibleApplicationFunctionTargets(task);
        var userMethodsMap = GetUserMethodsPublicNameMap();
        expr = RewriteBareFunctionInvocations(expr, functionName =>
        {
            if (localFunctionNames.Contains(functionName))
                return null;

            if (_xpaFunctionMap.TryGetValue(functionName, out var xpaTarget) &&
                !string.Equals(functionName, "Level", StringComparison.OrdinalIgnoreCase))
                return xpaTarget;

            if (string.Equals(functionName, "ImageReload", StringComparison.OrdinalIgnoreCase))
                return "ViewCompat.ImageReload";

            if (userMethodsMap.TryGetValue(functionName, out var canonicalUserMethod))
                return $"u.{canonicalUserMethod}";

            if (accessibleParentFunctions.TryGetValue(functionName, out var parentTarget))
                return parentTarget;

            if (accessibleApplicationFunctions.TryGetValue(functionName, out var applicationTarget))
                return applicationTarget;

            if (TryGetComponentFunctionCallContract(functionName, out var componentContract))
                return componentContract.TargetName;

            return null;
        });

        expr = RewriteBareFunctionInvocations(expr, functionName =>
        {
            if (localFunctionNames.Contains(functionName))
                return null;
            return userMethodsMap.TryGetValue(functionName, out var canonical) ? $"u.{canonical}" : null;
        });

        expr = RewriteBareFunctionInvocations(expr, functionName =>
        {
            if (localFunctionNames.Contains(functionName))
                return null;
            if (TryGetComponentFunctionCallContract(functionName, out var componentContract))
                return componentContract.TargetName;
            if (!_componentFunctionReturnTypeByName.ContainsKey(functionName))
                return null;
            var methodName = ToCodeIdentifierPreservingCase(functionName);
            return $"ComponentFunctionCompat.{methodName}";
        });

        expr = RewriteFunctionCalls(expr, "u.RepStr", args =>
        {
            if (args.Count != 3 ||
                !TryParseFunctionCall(
                    StripRedundantOuterParentheses(args[0].Trim()),
                    out var sourceFunction,
                    out _) ||
                !IsTopLevelCall(sourceFunction, "u.FileInfo"))
                return null;

            return $"u.RepStr(u.CastToText({args[0].Trim()}), {args[1].Trim()}, {args[2].Trim()})";
        });

        expr = RewriteFunctionCalls(expr, "u.Fill", args =>
        {
            if (args.Count != 2)
                return null;

            var numericLength = ApplyAttributeCastCentral(args[1].Trim(), "FIELD_NUMERIC");
            return $"u.Fill({args[0].Trim()}, {numericLength})";
        });

        expr = NormalizeMalformedDateCastBooleanGrouping(expr);

        expr = Regex.Replace(
            expr,
            @"\.(?<method>PadLeft|PadRight)\(\s*(?<width>\d+)\s*,\s*""(?<padding>[^""\\])""\s*\)",
            m => $".{m.Groups["method"].Value}({m.Groups["width"].Value}, '{m.Groups["padding"].Value}')",
            RegexOptions.CultureInvariant);

        expr = Regex.Replace(
            expr,
            @"\.AddCategoria\(\s*""(?<category>[^""\\])""\s*,",
            m => $".AddCategoria('{m.Groups["category"].Value}',",
            RegexOptions.CultureInvariant);

        // Magic represents an empty date with 00/00/00 (or 00/00/0000).
        // Once translated as C# tokens this becomes constant integer division by
        // zero. Its scalar representation is zero; the surrounding typed context
        // will materialize Date.Empty/Time.Empty when required.
        expr = Regex.Replace(
            expr,
            @"(?<!\d)00\s*/\s*00\s*/\s*0{2,4}(?!\d)",
            "0",
            RegexOptions.CultureInvariant);

        expr = RewriteFunctionCalls(expr, "u.DBName", args =>
        {
            if (args.Count == 0 ||
                args[0].IndexOf("typeof(", StringComparison.Ordinal) < 0 ||
                SplitTopLevelArithmeticExpression(args[0].Trim()) is null)
                return null;

            var fileIndex = args[0].Trim();
            foreach (var pair in _dataSourceTypeByObjectOrdinal)
            {
                fileIndex = fileIndex.Replace(
                    pair.Value,
                    pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal);
            }

            if (string.Equals(fileIndex, args[0].Trim(), StringComparison.Ordinal))
                return null;

            args[0] = fileIndex;
            return $"u.DBName({string.Join(", ", args.Select(static arg => arg.Trim()))})";
        });

        return expr;
    }

    private static string NormalizeMalformedDateCastBooleanGrouping(string expr)
    {
        expr = Regex.Replace(
            expr,
            @"u\.CastToDate\(\s*(?<left>u\.IsNull\s*\(\s*[^()]+\s*\))\s*&&\s*(?<right>[A-Za-z_][A-Za-z0-9_\.]*)\s*\)",
            match => $"({match.Groups["left"].Value.Trim()}) && u.CastToDate({match.Groups["right"].Value.Trim()})",
            RegexOptions.CultureInvariant);

        return RewriteFunctionCalls(expr, "u.CastToDate", args =>
        {
            if (args.Count != 1)
                return null;

            var boolean = SplitTopLevelBooleanBinaryExpression(
                StripRedundantOuterParentheses(args[0].Trim()));
            if (boolean is null || !string.Equals(boolean.Value.Operator, "&&", StringComparison.Ordinal))
                return null;

            // A malformed XPA grouping such as CastToDate(IsNull(D) AND D) is
            // intended to cast only the date operand.
            return $"({boolean.Value.Left.Trim()}) && u.CastToDate({boolean.Value.Right.Trim()})";
        });
    }
}


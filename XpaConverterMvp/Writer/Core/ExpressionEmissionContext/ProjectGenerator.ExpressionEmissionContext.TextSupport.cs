using System.Globalization;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string NormalizeTextSinkArgumentCentral(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = expression.Trim();
        if (IsNullCallExpression(trimmed))
            return trimmed;

        if (TryParseFunctionCall(trimmed, out var scalarCastName, out var scalarCastArgs) &&
            scalarCastArgs.Count == 1 &&
            (IsTopLevelCall(scalarCastName, "u.CastToNumber") ||
             IsTopLevelCall(scalarCastName, "u.CastToBool") ||
             IsTopLevelCall(scalarCastName, "u.CastToDate") ||
             IsTopLevelCall(scalarCastName, "u.CastToTime")) &&
            TryGetWholeCSharpStringLiteral(scalarCastArgs[0].Trim(), out _))
        {
            return scalarCastArgs[0].Trim();
        }

        var comparison = SplitTopLevelComparisonExpression(trimmed);
        if (comparison is not null &&
            (string.Equals(comparison.Value.Operator, "==", System.StringComparison.Ordinal) ||
             string.Equals(comparison.Value.Operator, "!=", System.StringComparison.Ordinal)) &&
            (IsWholeCSharpEmptyStringLiteral(comparison.Value.Left.Trim()) ||
             IsWholeCSharpEmptyStringLiteral(comparison.Value.Right.Trim())))
        {
            var scalarSide = IsWholeCSharpEmptyStringLiteral(comparison.Value.Left.Trim())
                ? comparison.Value.Right.Trim()
                : comparison.Value.Left.Trim();
            if (IsSimpleIdentifier(scalarSide) || IsSimpleIdentifierPath(scalarSide))
                return NormalizeTextSinkArgumentCentral(scalarSide);
        }

        if (IsVarCurrentLikeExpression(trimmed))
            return ApplyAttributeCastCentral(trimmed, "FIELD_ALPHA");

        var topLevelCall = TryGetTopLevelFunctionName(trimmed);
        if (IsTopLevelCall(topLevelCall, "u.EditGet"))
            return ApplyAttributeCastCentral(trimmed, "FIELD_ALPHA");
        if (IsSharedValGetExpression(topLevelCall))
            return RewriteSharedValGetText(trimmed);
        if (IsTopLevelCall(topLevelCall, "u.VecGet"))
            return trimmed;
        if (IsTopLevelCall(topLevelCall, "u.Str") &&
            TryParseFunctionCall(trimmed, out var strName, out var strArgs) &&
            strArgs.Count >= 1)
        {
            strArgs[0] = EnsureNumberCallArgumentCentral(
                StripExpectedAttributeCastWrappers(
                    NormalizeNumericConditionalBranchExpressionCentral(strArgs[0].Trim()),
                    "FIELD_NUMERIC"));
            if (strArgs.Count > 1)
                strArgs[1] = NormalizeTextSinkArgumentCentral(strArgs[1].Trim());
            return $"{strName}({string.Join(", ", strArgs)})";
        }
        if (IsTopLevelCall(topLevelCall, "u.DStr") &&
            TryParseFunctionCall(trimmed, out var dstrName, out var dstrArgs) &&
            dstrArgs.Count >= 1)
        {
            dstrArgs[0] = EnsureDateCallArgumentCentral(
                StripExpectedAttributeCastWrappers(
                    NormalizeDateConditionalExpression(dstrArgs[0].Trim()),
                    "FIELD_DATE"));
            if (dstrArgs.Count > 1)
                dstrArgs[1] = NormalizeTextSinkArgumentCentral(dstrArgs[1].Trim());
            return $"{dstrName}({string.Join(", ", dstrArgs)})";
        }
        if (IsTopLevelCall(topLevelCall, "u.TStr") &&
            TryParseFunctionCall(trimmed, out var tstrName, out var tstrArgs) &&
            tstrArgs.Count >= 1)
        {
            tstrArgs[0] = NormalizeTimeFormattingArgumentCentral(tstrArgs[0].Trim());
            if (tstrArgs.Count > 1)
                tstrArgs[1] = NormalizeTextSinkArgumentCentral(tstrArgs[1].Trim());
            return $"{tstrName}({string.Join(", ", tstrArgs)})";
        }
        if (IsTopLevelCall(topLevelCall, "u.MTStr") &&
            TryParseFunctionCall(trimmed, out var mtstrName, out var mtstrArgs) &&
            mtstrArgs.Count >= 1)
        {
            mtstrArgs[0] = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    NormalizeNumericConditionalBranchExpressionCentral(mtstrArgs[0].Trim()),
                    "FIELD_NUMERIC"),
                "Number");
            if (mtstrArgs.Count > 1)
                mtstrArgs[1] = NormalizeTextSinkArgumentCentral(mtstrArgs[1].Trim());
            return $"{mtstrName}({string.Join(", ", mtstrArgs)})";
        }
        if (IsTopLevelCall(topLevelCall, "u.RepStr") &&
            TryParseFunctionCall(trimmed, out var repStrName, out var repStrArgs) &&
            repStrArgs.Count >= 1)
        {
            repStrArgs[0] = NormalizeTextSinkArgumentCentral(repStrArgs[0].Trim());
            if (repStrArgs.Count > 1)
                repStrArgs[1] = NormalizeTextSinkArgumentCentral(repStrArgs[1].Trim());
            if (repStrArgs.Count > 2)
                repStrArgs[2] = NormalizeTextSinkArgumentCentral(repStrArgs[2].Trim());
            return $"{repStrName}({string.Join(", ", repStrArgs)})";
        }

        if (TryParseFunctionCall(trimmed, out var getNumberParamName, out var getNumberParamArgs) &&
            IsTopLevelCall(getNumberParamName, "u.GetNumberParam"))
            return $"u.GetTextParam({string.Join(", ", getNumberParamArgs)})";

        if (IsTopLevelCall(topLevelCall, "u.FileInfo") ||
            IsTopLevelCall(topLevelCall, "FileInfo") ||
            IsTopLevelCall(topLevelCall, "u.CallDLL") ||
            IsTopLevelCall(topLevelCall, "CallDLL") ||
            IsTopLevelCall(topLevelCall, "u.CallDLLF") ||
            IsTopLevelCall(topLevelCall, "CallDLLF") ||
            IsSimpleIdentifierPath(trimmed))
            return trimmed;

        if (TryParseFunctionCall(trimmed, out var byteArrayCastName, out var byteArrayCastArgs) &&
            IsTopLevelCall(byteArrayCastName, "u.CastToByteArray") &&
            byteArrayCastArgs.Count == 1)
            return NormalizeTextSinkArgumentCentral(byteArrayCastArgs[0].Trim());

        if (TryParseFunctionCall(trimmed, out var byteArrayToTextName, out var byteArrayToTextArgs) &&
            IsTopLevelCall(byteArrayToTextName, "u.ByteArrayToText") &&
            byteArrayToTextArgs.Count == 1)
        {
            var inner = byteArrayToTextArgs[0].Trim();
            var innerTopLevelCall = TryGetTopLevelFunctionName(inner);
            if (IsKnownTextProducingFunction(innerTopLevelCall) ||
                IsSharedValGetExpression(innerTopLevelCall) ||
                IsWholeStringLiteralExpression(inner) ||
                IsSimpleIdentifierPath(inner) ||
                IsVarCurrentLikeExpression(inner))
                return NormalizeTextSinkArgumentCentral(inner);
        }

        if (TryParseFunctionCall(trimmed, out var functionName, out var args))
        {
            if (IsTopLevelCall(functionName, "u.If") && args.Count == 3)
            {
                args[1] = NormalizeTextSinkArgumentCentral(StripExpectedAttributeCastWrappers(args[1].Trim(), "FIELD_NUMERIC"));
                args[2] = NormalizeTextSinkArgumentCentral(StripExpectedAttributeCastWrappers(args[2].Trim(), "FIELD_NUMERIC"));
                return $"u.If({string.Join(", ", args)})";
            }

            if ((string.Equals(functionName, "u.Case", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(functionName, "u.CaseUntyped", StringComparison.OrdinalIgnoreCase)) &&
                args.Count >= 3)
            {
                for (var i = 2; i < args.Count; i++)
                {
                    var discriminator = i >= 2 ? args[i - 1] : null;
                    args[i] = NormalizeTextSinkArgumentCentral(args[i]);
                    args[i] = NormalizeAlphaCaseBranchCentral(args[i]);
                    args[i] = NormalizeVariantCaseBranchForDiscriminatorCentral(discriminator, args[i]);
                }
                var normalizedCase = $"u.CaseUntyped({string.Join(", ", args)})";
                normalizedCase = NormalizeVariantCaseExpression(normalizedCase);
                normalizedCase = NormalizeAlphaCaseExpression(normalizedCase);
                return ApplyAttributeCastCentral(normalizedCase, "FIELD_ALPHA");
            }
        }

        return trimmed;
    }

    private static bool TryUnwrapCastToTextExpressionCentral(string expression, out string innerExpression)
    {
        innerExpression = "";
        if (TryParseFunctionCall(expression, out var functionName, out var args) &&
            string.Equals(functionName, "u.CastToText", StringComparison.OrdinalIgnoreCase) &&
            args.Count == 1)
        {
            innerExpression = args[0].Trim();
            return !string.IsNullOrWhiteSpace(innerExpression);
        }

        var trimmed = expression.Trim();
        const string prefix = "u.CastToText(";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var openParen = trimmed.IndexOf('(');
        if (openParen < 0)
            return false;

        var closeParen = FindMatchingParen(trimmed, openParen);
        if (closeParen != trimmed.Length - 1)
            return false;

        innerExpression = trimmed[(openParen + 1)..closeParen].Trim();
        return !string.IsNullOrWhiteSpace(innerExpression);
    }

    private static bool IsIntegerLiteralExpressionCentral(string expression)
    {
        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (int.TryParse(trimmed, out _))
            return true;

        return TryGetUnaryNegatedParenthesizedNumericLiteralInner(trimmed, out var inner) &&
               int.TryParse(inner, out _);
    }

    private static bool IsNumericLiteralExpressionCentral(string expression)
    {
        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            return true;

        return TryGetUnaryNegatedParenthesizedNumericLiteralInner(trimmed, out var inner) &&
               decimal.TryParse(inner, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
    }

    private static bool TryGetUnaryNegatedParenthesizedNumericLiteralInner(string expression, out string inner)
    {
        inner = string.Empty;
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = expression.Trim();
        if (!trimmed.StartsWith("-", StringComparison.Ordinal))
            return false;

        var cursor = 1;
        while (cursor < trimmed.Length && char.IsWhiteSpace(trimmed[cursor]))
            cursor++;

        if (cursor >= trimmed.Length || trimmed[cursor] != '(')
            return false;

        var closeParen = FindMatchingParen(trimmed, cursor);
        if (closeParen != trimmed.Length - 1)
            return false;

        inner = trimmed[(cursor + 1)..closeParen].Trim();
        return !string.IsNullOrWhiteSpace(inner);
    }

    private static bool ConsumesTextArgumentCentral(string functionName)
    {
        return string.Equals(functionName, "u.Trim", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "Trim", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.Upper", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "Upper", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.Lower", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "Lower", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.LTrim", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "LTrim", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.RTrim", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "RTrim", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.Left", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "Left", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.Right", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "Right", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.Mid", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "Mid", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.StrToken", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "StrToken", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.StrTokenCnt", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "StrTokenCnt", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.Translate", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "Translate", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.Flip", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "Flip", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.FileInfo", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "FileInfo", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.DragSetData", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "DragSetData", StringComparison.OrdinalIgnoreCase);
    }
}

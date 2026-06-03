using System;
using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool HasNumericCoercionWrapping(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = expression.Trim();
        var topLevelCall = TryGetTopLevelFunctionName(trimmed);
        if (IsScalarCastFunctionName(topLevelCall ?? "", XpaType.Number) ||
            IsTopLevelCall(topLevelCall, GetNumericProjectionFunctionName()) ||
            IsTopLevelCall(topLevelCall, "u.Val"))
            return true;

        if (TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            args.Count == 1 &&
            (IsScalarCastFunctionName(functionName, XpaType.Text) ||
             IsScalarCastFunctionName(functionName, XpaType.Bool) ||
             IsScalarCastFunctionName(functionName, XpaType.Date) ||
             IsScalarCastFunctionName(functionName, XpaType.Time)))
        {
            return HasNumericCoercionWrapping(args[0].Trim());
        }

        return false;
    }

    private static string WrapNumericProjectionExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var normalized = expression.Trim();
        return HasNumericCoercionWrapping(normalized)
            ? normalized
            : LooksLikeTextualNumericProjectionExpression(normalized)
                ? $"u.Val({normalized}, \"\")"
                : $"{GetNumericProjectionFunctionName()}({normalized})";
    }

    private static string GetNumericProjectionFunctionName()
        => "u.ToNumber";

    private static bool IsNumericProjectionFunctionName(string functionName)
        => IsTopLevelCall(functionName, GetNumericProjectionFunctionName());

    private static string NormalizeTimeArithmeticExpressionCentral(string expression)
    {
        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeTimeArithmeticExpressionCentral));
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var normalized = expression.Trim();

        normalized = RewriteFunctionCalls(normalized, "u.CastToNumber", args =>
        {
            if (args.Count != 1)
                return null;

            var candidate = args[0].Trim();
            var candidateTopLevelCall = TryGetTopLevelFunctionName(candidate);
            if (IsObjectProducingExpression(candidateTopLevelCall) ||
                IsRuntimeUntypedScalarExpression(candidateTopLevelCall) ||
                IsNumericCallDllExpression(candidateTopLevelCall) ||
                IsTopLevelCall(candidateTopLevelCall, "u.FileInfo") ||
                IsTopLevelCall(candidateTopLevelCall, "u.ClientFileInfo") ||
                IsTopLevelCall(candidateTopLevelCall, "u.Rights") ||
                IsTopLevelCall(candidateTopLevelCall, "u.XMLGet"))
                return $"u.CastToNumber({EmitScalarArgumentFromEvidence(candidate, "Text")})";

            return ShouldUnwrapNumericTimeProjectionCentral(candidate)
                ? NormalizeTimeArithmeticExpressionCentral(candidate)
                : null;
        });

        normalized = RewriteFunctionCalls(normalized, "u.ToNumber", args =>
        {
            if (args.Count != 1)
                return null;

            var candidate = args[0].Trim();
            return ShouldUnwrapNumericTimeProjectionCentral(candidate)
                ? NormalizeTimeArithmeticExpressionCentral(candidate)
                : null;
        });

        normalized = RewriteFunctionCalls(normalized, "u.Val", args =>
        {
            if (args.Count == 0)
                return null;

            var candidate = args[0].Trim();
            return ShouldUnwrapNumericTimeProjectionCentral(candidate)
                ? NormalizeTimeArithmeticExpressionCentral(candidate)
                : null;
        });

        var arithmetic = SplitTopLevelArithmeticExpression(normalized);
        if (arithmetic is not null)
        {
            var left = NormalizeTimeArithmeticExpressionCentral(arithmetic.Value.Left.Trim());
            var right = NormalizeTimeArithmeticExpressionCentral(arithmetic.Value.Right.Trim());
            return $"{left} {arithmetic.Value.Operator} {right}";
        }

        return NormalizeTimeTypedExpression(normalized);
    }

    private static bool ShouldUnwrapNumericTimeProjectionCentral(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = expression.Trim();
        if (IsTimeLikeExpression(trimmed) ||
            LooksLikeTimeAssignmentExpression(trimmed) ||
            ContainsTimeArithmeticExpression(trimmed))
            return true;

        if (TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            IsTopLevelCall(functionName, "u.If") &&
            args.Count == 3)
        {
            return ShouldUnwrapNumericTimeProjectionCentral(args[1].Trim()) &&
                   ShouldUnwrapNumericTimeProjectionCentral(args[2].Trim());
        }

        return false;
    }
}

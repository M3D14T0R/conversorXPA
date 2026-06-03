using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static readonly ConditionalWeakTable<TaskSemantic, ConcurrentDictionary<string, ExpectedTypeContext>> _expectedTypeInferenceCacheByTask = new();

    private readonly record struct ExpectedTypeContext(
        string AttrObj,
        string ReturnType,
        bool IsBooleanCondition)
    {
        internal bool HasExpectation =>
            !string.IsNullOrWhiteSpace(AttrObj) ||
            !string.IsNullOrWhiteSpace(ReturnType) ||
            IsBooleanCondition;
    }

    private static ExpectedTypeContext ExpectedTypeForAttrObj(string? attrObj)
    {
        var normalizedAttrObj = NormalizeAttrObjKind(attrObj);
        return new ExpectedTypeContext(
            normalizedAttrObj,
            MapAttrObjToReturnType(normalizedAttrObj),
            false);
    }

    private static ExpectedTypeContext ExpectedTypeForReturnType(string? returnType)
    {
        var normalizedReturnType = NormalizeReturnTypeToken(returnType);
        return new ExpectedTypeContext(
            MapReturnTypeToAttrObj(GetValueReturnType(normalizedReturnType)),
            normalizedReturnType,
            false);
    }

    private static ExpectedTypeContext ExpectedBooleanCondition()
        => new("", "", true);

    private static ExpectedTypeContext ExpectedTypeForTarget(TargetValueInfo targetInfo)
    {
        if (targetInfo.IsDotNet &&
            targetInfo.Resource is not null &&
            !string.IsNullOrWhiteSpace(targetInfo.Resource.ObjectType))
        {
            var objectType = NormalizeDotNetObjectType(targetInfo.Resource.ObjectType);
            if (!string.IsNullOrWhiteSpace(objectType))
                return ExpectedTypeForReturnType(objectType);
        }

        if (targetInfo.IsArray)
        {
            if (targetInfo.Resource is not null)
            {
                var itemType = ResolveArrayColumnItemType(targetInfo.Resource, _allFieldModels, ResolveOwningTaskForResource(targetInfo.Resource));
                var arrayReturnType = itemType switch
                {
                    "Text" => "Text[]",
                    "Number" => "Number[]",
                    "Date" => "Date[]",
                    "Time" => "Time[]",
                    "Bool" => "Bool[]",
                    "byte[]" => "byte[][]",
                    _ => "Text[]"
                };
                return ExpectedTypeForReturnType(arrayReturnType);
            }

            return ExpectedTypeForReturnType("Text[]");
        }

        if (targetInfo.IsBlob)
            return ExpectedTypeForReturnType("byte[]");

        if (targetInfo.IsDotNet)
            return ExpectedTypeForReturnType("object");

        var attrObj = !string.IsNullOrWhiteSpace(targetInfo.AttrObj)
            ? targetInfo.AttrObj
            : targetInfo.ModelAttrObj ?? "";
        if (targetInfo.IsNumeric &&
            !string.Equals(NormalizeAttrObjKind(attrObj), "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase))
            attrObj = "FIELD_NUMERIC";
        return ExpectedTypeForAttrObj(attrObj);
    }

    private static ExpectedTypeContext ExpectedTypeForExpressionAttribute(string? attribute)
        => ExpectedTypeForAttrObj(NormalizeAttrObjKind(attribute));

    private static ExpectedTypeContext ExpectedTypeForParameterType(string? parameterType)
    {
        return NormalizeReturnTypeToken(parameterType) switch
        {
            "TextParameter" or "Text" or "string" or "String" or "System.String" => ExpectedTypeForReturnType("Text"),
            "NumberParameter" or "Number" => ExpectedTypeForReturnType("Number"),
            "DateParameter" or "Date" => ExpectedTypeForReturnType("Date"),
            "TimeParameter" or "Time" => ExpectedTypeForReturnType("Time"),
            "BoolParameter" or "Bool" => ExpectedTypeForReturnType("Bool"),
            "ByteArrayParameter" or "byte[]" => ExpectedTypeForReturnType("byte[]"),
            "System.IntPtr" or "IntPtr" => ExpectedTypeForReturnType("System.IntPtr"),
            "Text[]" => ExpectedTypeForReturnType("Text[]"),
            "Number[]" => ExpectedTypeForReturnType("Number[]"),
            "Date[]" => ExpectedTypeForReturnType("Date[]"),
            "Time[]" => ExpectedTypeForReturnType("Time[]"),
            "Bool[]" => ExpectedTypeForReturnType("Bool[]"),
            "byte[][]" => ExpectedTypeForReturnType("byte[][]"),
            "object" => ExpectedTypeForReturnType("object"),
            var token when token.StartsWith("Func<", StringComparison.Ordinal) => ExpectedTypeForReturnType(token),
            _ => default
        };
    }

    private static string ApplyExpectedTypeContext(
        string code,
        TaskSemantic task,
        ExpectedTypeContext expected)
    {
        if (IsKnownExpressionReturnTypeCompatible(code, ResolveReturnTypeForExpectedContext(expected), task))
            return StripRedundantOuterParentheses(code.Trim());

        return TrackLegacyExpressionTreatmentIfChanged(
            "Context",
            nameof(ApplyExpectedTypeContext),
            code,
            ApplyExpectedTypeContextCentral(code, task, expected));
    }

    private static string BuildContextualInferenceCacheKey(TaskSemantic task, string code, ExpectedTypeContext expected)
    {
        return string.Join("|",
            task.Ordinal.ToString(CultureInfo.InvariantCulture),
            expected.AttrObj ?? "",
            expected.ReturnType ?? "",
            expected.IsBooleanCondition ? "1" : "0",
            code);
    }

    private static bool CanBypassContextualInference(string expression, TaskSemantic task, ExpectedTypeContext expected)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return true;

        if (expected.IsBooleanCondition &&
            IsBooleanContextReadyExpression(task, expression) &&
            SplitTopLevelComparisonExpression(StripRedundantOuterParentheses(expression.Trim())) is null)
            return true;

        if (!expected.IsBooleanCondition &&
            IsExpressionAlreadyCompatible(expression, task, expected) &&
            !RequiresExplicitTypedCast(expression, expected) &&
            !RequiresMaterializedScalarCast(expression, expected))
            return true;

        return false;
    }

    private static bool RequiresMaterializedScalarCast(string expression, ExpectedTypeContext expected)
    {
        if (string.IsNullOrWhiteSpace(expression) || expected.IsBooleanCondition)
            return false;

        var expectedAttrObj = NormalizeAttrObjKind(expected.AttrObj);
        if (string.IsNullOrWhiteSpace(expectedAttrObj) ||
            string.Equals(expectedAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return false;

        var topLevelCall = TryGetTopLevelFunctionName(StripRedundantOuterParentheses(expression.Trim()));
        if (string.IsNullOrWhiteSpace(topLevelCall))
            return false;

        return IsCallDllExpression(topLevelCall) ||
               IsRuntimeUntypedScalarExpression(topLevelCall) ||
               IsObjectProducingExpression(topLevelCall) ||
               IsTopLevelCall(topLevelCall, "u.FileInfo") ||
               IsTopLevelCall(topLevelCall, "u.ClientFileInfo");
    }

    private static bool RequiresExplicitTypedCast(string expression, ExpectedTypeContext expected)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        var valueReturnType = GetValueReturnType(expected.ReturnType);
        if (TryParseFunctionCall(trimmed, out var castName, out var castArgs) &&
            castArgs.Count == 1 &&
            IsExplicitCastFunctionForReturnType(castName, valueReturnType))
            return RequiresExplicitTypedCast(castArgs[0].Trim(), expected);

        if (!TryParseFunctionCall(trimmed, out var functionName, out _))
            return false;

        if ((IsTopLevelCall(functionName, "u.FileInfo") ||
             IsTopLevelCall(functionName, "u.ClientFileInfo") ||
             IsRuntimeUntypedScalarExpression(functionName) ||
             IsObjectProducingExpression(functionName)) &&
            !string.IsNullOrWhiteSpace(expected.AttrObj))
            return true;

        return (IsTopLevelCall(functionName, "u.FileInfo") ||
                IsTopLevelCall(functionName, "u.ClientFileInfo") ||
                IsRuntimeUntypedScalarExpression(functionName) ||
                IsObjectProducingExpression(functionName)) &&
               !string.IsNullOrWhiteSpace(valueReturnType) &&
               !string.Equals(valueReturnType, "object", StringComparison.Ordinal);
    }

    private static bool IsRuntimeUntypedScalarExpression(string? functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return false;

        return IsTopLevelCall(functionName, "u.CaseUntyped") ||
               IsTopLevelCall(functionName, "u.CtxGetName") ||
               IsVarCurrentLikeFunctionName(functionName) ||
               functionName.EndsWith(".RunByPublicName", StringComparison.OrdinalIgnoreCase);
    }

    private static string ApplyExpectedTypeToInternalOperators(
        string expression,
        TaskSemantic task,
        ExpectedTypeContext expected)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (HasOpaqueInlineComment(trimmed))
            return trimmed;

        if (TryNormalizeWrappedScalarExpression(trimmed, task, expected, out var normalizedWrapped))
            return normalizedWrapped;

        if (!expected.IsBooleanCondition &&
            string.Equals(GetValueReturnType(expected.ReturnType), "Number", StringComparison.Ordinal) &&
            trimmed.Contains("u.CastToNumber(", StringComparison.Ordinal))
        {
            trimmed = RewriteFunctionCalls(trimmed, "u.CastToNumber", args =>
            {
                if (args.Count != 1)
                    return null;

                var rewritten = NormalizeCastToNumberInputUsingResolvedTypeCentral(args[0].Trim(), task);
                return $"u.CastToNumber({rewritten})";
            });
        }

        if (TryParseFunctionCall(trimmed, out var conditionalName, out var conditionalArgs) &&
            IsTopLevelCall(conditionalName, "u.CndRange") &&
            conditionalArgs.Count >= 2 &&
            expected.IsBooleanCondition)
        {
            trimmed = ApplyExpectedTypeContext(conditionalArgs[0].Trim(), task, ExpectedBooleanCondition());
        }

        if (TryParseFunctionCall(trimmed, out conditionalName, out conditionalArgs) &&
            IsTopLevelCall(conditionalName, "u.If") &&
            conditionalArgs.Count == 3)
        {
            var condition = ApplyExpectedTypeContext(conditionalArgs[0].Trim(), task, ExpectedBooleanCondition());
            var branchExpected = GetBranchExpectedTypeContext(expected);
            var whenTrue = ApplyExpectedTypeToConditionalBranch(conditionalArgs[1].Trim(), task, branchExpected);
            var whenFalse = ApplyExpectedTypeToConditionalBranch(conditionalArgs[2].Trim(), task, branchExpected);
            trimmed = $"u.If({condition}, {whenTrue}, {whenFalse})";
        }

        var booleanSplit = SplitTopLevelBooleanBinaryExpression(trimmed);
        if (booleanSplit is not null)
        {
            var left = ApplyExpectedTypeContext(booleanSplit.Value.Left, task, ExpectedBooleanCondition());
            var right = ApplyExpectedTypeContext(booleanSplit.Value.Right, task, ExpectedBooleanCondition());
            return $"({left}) {booleanSplit.Value.Operator} ({right})";
        }

        var comparison = SplitTopLevelComparisonExpression(trimmed);
        if (comparison is not null)
        {
            if (TryNormalizeBooleanTextEmptyComparison(trimmed, task, out var normalizedBooleanTextComparison))
                return normalizedBooleanTextComparison;

            var left = comparison.Value.Left.Trim();
            var right = comparison.Value.Right.Trim();
            var comparisonExpected = InferExpectedTypeFromComparisonOperands(task, left, right);
            if (comparisonExpected.HasExpectation)
            {
                left = ApplyExpectedTypeContext(left, task, comparisonExpected with { IsBooleanCondition = false });
                right = ApplyExpectedTypeContext(right, task, comparisonExpected with { IsBooleanCondition = false });
                left = NormalizeComparisonOperandForExpected(left, comparisonExpected, task);
                right = NormalizeComparisonOperandForExpected(right, comparisonExpected, task);
            }

            return $"{left} {comparison.Value.Operator} {right}";
        }

        var arithmetic = SplitTopLevelArithmeticExpression(trimmed);
        if (arithmetic is not null)
        {
            var left = arithmetic.Value.Left.Trim();
            var right = arithmetic.Value.Right.Trim();
            var arithmeticExpected = NormalizeArithmeticExpectedType(expected, task, left, arithmetic.Value.Operator, right);
            if (arithmeticExpected.Left.HasExpectation || arithmeticExpected.Right.HasExpectation)
            {
                if (arithmeticExpected.Left.HasExpectation)
                    left = ApplyExpectedTypeContext(left, task, arithmeticExpected.Left);
                if (arithmeticExpected.Right.HasExpectation)
                    right = ApplyExpectedTypeContext(right, task, arithmeticExpected.Right);

                if (arithmeticExpected.Left.HasExpectation)
                {
                    left = NormalizeArithmeticOperandForExpected(left, arithmeticExpected.Left, task);
                    if (ShouldForceNumericTemporalArithmeticOperand(task, arithmetic.Value.Left, arithmeticExpected.Left))
                    {
                        var temporalLeft = StripExpectedAttributeCastWrappers(
                            StripExpectedAttributeCastWrappers(left, "FIELD_TIME"),
                            "FIELD_DATE");
                        left = $"u.ToNumber({temporalLeft})";
                    }
                }
                if (arithmeticExpected.Right.HasExpectation)
                {
                    right = NormalizeArithmeticOperandForExpected(right, arithmeticExpected.Right, task);
                    if (ShouldForceNumericTemporalArithmeticOperand(task, arithmetic.Value.Right, arithmeticExpected.Right))
                    {
                        var temporalRight = StripExpectedAttributeCastWrappers(
                            StripExpectedAttributeCastWrappers(right, "FIELD_TIME"),
                            "FIELD_DATE");
                        right = $"u.ToNumber({temporalRight})";
                    }
                }

                var isDateAddOrSubtract =
                    (string.Equals(expected.AttrObj, "FIELD_DATE", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(GetValueReturnType(expected.ReturnType), "Date", StringComparison.Ordinal)) &&
                    (string.Equals(arithmetic.Value.Operator, "+", StringComparison.Ordinal) ||
                     string.Equals(arithmetic.Value.Operator, "-", StringComparison.Ordinal));
                if (isDateAddOrSubtract &&
                    (string.Equals(arithmeticExpected.Right.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(GetValueReturnType(arithmeticExpected.Right.ReturnType), "Number", StringComparison.Ordinal)))
                {
                    var normalizedLeftDate = EmitScalarArgumentFromEvidence(
                        StripExpectedAttributeCastWrappers(left, "FIELD_DATE"),
                        "Date");
                    right = StripExpectedAttributeCastWrappers(right, "FIELD_DATE");
                    right = EmitScalarArgumentFromEvidence(
                        StripExpectedAttributeCastWrappers(right, "FIELD_NUMERIC"),
                        "Number");
                    if (string.Equals(arithmetic.Value.Operator, "-", StringComparison.Ordinal))
                        right = $"(-({right}))";
                    return $"u.AddDate({normalizedLeftDate}, 0, 0, {right})";
                }

                return $"{left} {arithmetic.Value.Operator} {right}";
            }
        }

        return expression;
    }

    private static bool ShouldForceNumericTemporalArithmeticOperand(
        TaskSemantic task,
        string originalOperand,
        ExpectedTypeContext expected)
    {
        var expectedValueReturnType = GetValueReturnType(expected.ReturnType);
        if (!string.Equals(expected.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(expectedValueReturnType, "Number", StringComparison.Ordinal))
            return false;

        var resolved = ResolveExpressionTypeFromEvidence(task, originalOperand.Trim());
        var resolvedReturnType = GetValueReturnType(resolved.ReturnType);
        if (string.Equals(resolvedReturnType, "Date", StringComparison.Ordinal) ||
            string.Equals(resolvedReturnType, "Time", StringComparison.Ordinal))
            return true;

        var inferred = ResolveExpectedTypeFromExpressionEvidence(task, originalOperand.Trim());
        return string.Equals(inferred.AttrObj, "FIELD_TIME", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(inferred.AttrObj, "FIELD_DATE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(GetValueReturnType(inferred.ReturnType), "Time", StringComparison.Ordinal) ||
               string.Equals(GetValueReturnType(inferred.ReturnType), "Date", StringComparison.Ordinal);
    }

    private static bool TryNormalizeBooleanTextEmptyComparison(string expression, TaskSemantic task, out string normalized)
    {
        normalized = expression;
        var comparison = SplitTopLevelComparisonExpression(expression);
        if (comparison is null)
            return false;

        if (!string.Equals(comparison.Value.Operator, "==", StringComparison.Ordinal) &&
            !string.Equals(comparison.Value.Operator, "!=", StringComparison.Ordinal))
            return false;

        var left = comparison.Value.Left.Trim();
        var right = comparison.Value.Right.Trim();

        var emptyOnRight = IsEmptyStringLiteral(right);
        var emptyOnLeft = IsEmptyStringLiteral(left);
        if (!emptyOnRight && !emptyOnLeft)
            return false;

        var candidate = emptyOnRight ? left : right;
        while (TryParseFunctionCall(candidate, out var castName, out var castArgs) &&
               castArgs.Count == 1 &&
               IsTopLevelCall(castName, "u.CastToText"))
        {
            candidate = castArgs[0].Trim();
        }

        var booleanBinary = SplitTopLevelBooleanBinaryExpression(candidate);
        if (booleanBinary is null || !string.Equals(booleanBinary.Value.Operator, "&&", StringComparison.Ordinal))
            return false;

        var leftBool = RewriteBooleanOperand(task, booleanBinary.Value.Left.Trim());
        var rightBool = RewriteBooleanOperand(task, booleanBinary.Value.Right.Trim());
        var leftReady = IsBooleanContextReadyExpression(task, leftBool);
        var rightReady = IsBooleanContextReadyExpression(task, rightBool);

        string boolSide;
        string textSide;
        if (leftReady && !rightReady)
        {
            boolSide = leftBool;
            textSide = booleanBinary.Value.Right.Trim();
        }
        else if (!leftReady && rightReady)
        {
            boolSide = rightBool;
            textSide = booleanBinary.Value.Left.Trim();
        }
        else
        {
            return false;
        }

        textSide = EmitScalarArgumentFromEvidence(
            StripExpectedAttributeCastWrappers(textSide, "FIELD_ALPHA"),
            "Text");
        var emptyComparison = $"{textSide} {comparison.Value.Operator} \"\"";
        normalized = $"(({boolSide}) && ({emptyComparison}))";
        return true;
    }

    private static bool IsEmptyStringLiteral(string expression)
        => string.Equals(expression.Trim(), "\"\"", StringComparison.Ordinal);

    private static string NormalizeArithmeticOperandForExpected(string operand, ExpectedTypeContext expected, TaskSemantic? task)
    {
        var expectedValueReturnType = GetValueReturnType(expected.ReturnType);
        if (IsKnownExpressionReturnTypeCompatible(operand, expectedValueReturnType, task))
            return StripRedundantOuterParentheses(operand.Trim());

        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeArithmeticOperandForExpected));
        if (string.Equals(expected.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(expectedValueReturnType, "Number", StringComparison.Ordinal))
        {
            var normalized = StripExpectedAttributeCastWrappers(operand, "FIELD_NUMERIC");
            normalized = StripExpectedAttributeCastWrappers(normalized, "FIELD_DATE");
            normalized = StripExpectedAttributeCastWrappers(normalized, "FIELD_TIME");
            return NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(normalized, "Number", task);
        }

        if (string.Equals(expected.AttrObj, "FIELD_TIME", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(expectedValueReturnType, "Time", StringComparison.Ordinal))
        {
            var normalized = StripExpectedAttributeCastWrappers(operand, "FIELD_TIME");
            normalized = StripExpectedAttributeCastWrappers(normalized, "FIELD_NUMERIC");
            return NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(normalized, "Time", task);
        }

        return operand;
    }

    private static string NormalizeComparisonOperandForExpected(string operand, ExpectedTypeContext expected, TaskSemantic? task)
    {
        var expectedValueReturnType = GetValueReturnType(expected.ReturnType);
        if (IsKnownExpressionReturnTypeCompatible(operand, expectedValueReturnType, task))
            return StripRedundantOuterParentheses(operand.Trim());

        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeComparisonOperandForExpected));
        if (string.Equals(expected.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(expectedValueReturnType, "Number", StringComparison.Ordinal))
        {
            var normalized = StripExpectedAttributeCastWrappers(operand, "FIELD_NUMERIC");
            return NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(normalized, "Number", task);
        }

        if (string.Equals(expected.AttrObj, "FIELD_DATE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(expectedValueReturnType, "Date", StringComparison.Ordinal))
        {
            var normalized = StripExpectedAttributeCastWrappers(operand, "FIELD_DATE");
            return NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(normalized, "Date", task);
        }

        if (string.Equals(expected.AttrObj, "FIELD_TIME", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(expectedValueReturnType, "Time", StringComparison.Ordinal))
        {
            var normalized = StripExpectedAttributeCastWrappers(operand, "FIELD_TIME");
            return NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(normalized, "Time", task);
        }

        if (IsTextLikeExpectedType(expected))
        {
            var normalized = StripExpectedAttributeCastWrappers(operand, "FIELD_ALPHA");
            return NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(normalized, "Text", task);
        }

        if (IsBoolLikeExpectedType(expected))
        {
            var normalized = StripExpectedAttributeCastWrappers(operand, "FIELD_LOGICAL");
            return NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(normalized, "Bool", task);
        }

        return operand;
    }

    private static ExpectedTypeContext GetBranchExpectedTypeContext(ExpectedTypeContext expected)
        => expected.IsBooleanCondition ? ExpectedBooleanCondition() : expected;

    private static string ApplyExpectedTypeToConditionalBranch(string expression, TaskSemantic task, ExpectedTypeContext expected)
    {
        if (!expected.HasExpectation)
            return ApplyExpectedTypeContext(expression, task, expected);

        var normalized = ApplyExpectedTypeContext(expression, task, expected);
        if (expected.IsBooleanCondition)
            return normalized;

        return ApplyExpectedTypeToInternalOperators(normalized, task, expected);
    }

    private static (ExpectedTypeContext Left, ExpectedTypeContext Right) NormalizeArithmeticExpectedType(
        ExpectedTypeContext expected,
        TaskSemantic task,
        string left,
        string @operator,
        string right)
    {
        var leftExpected = ResolveExpectedTypeFromExpressionEvidence(task, left);
        var rightExpected = ResolveExpectedTypeFromExpressionEvidence(task, right);

        if (expected.HasExpectation && !expected.IsBooleanCondition)
        {
            var expectedValueReturnType = GetValueReturnType(expected.ReturnType);
            var isDateExpected = string.Equals(expected.AttrObj, "FIELD_DATE", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(expectedValueReturnType, "Date", StringComparison.Ordinal);
            var isTimeExpected = string.Equals(expected.AttrObj, "FIELD_TIME", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(expectedValueReturnType, "Time", StringComparison.Ordinal);
            var isDateAddOrSubtract =
                isDateExpected &&
                (string.Equals(@operator, "+", StringComparison.Ordinal) ||
                 string.Equals(@operator, "-", StringComparison.Ordinal));

            if (!isDateAddOrSubtract &&
                (isTimeExpected || isDateExpected) &&
                HasNumericArithmeticAffinity(leftExpected, rightExpected))
            {
                var numericExpected = ExpectedTypeForReturnType("Number");
                return (numericExpected, numericExpected);
            }

            return (expected, expected);
        }

        if (HasNumericArithmeticAffinity(leftExpected, rightExpected))
            return (ExpectedTypeForReturnType("Number"), ExpectedTypeForReturnType("Number"));

        return (leftExpected, rightExpected);
    }

    private static string NormalizeReturnTypeToken(string? returnType)
    {
        if (string.IsNullOrWhiteSpace(returnType))
            return "";

        var trimmed = returnType.Trim();
        if (trimmed.StartsWith("Func<", StringComparison.Ordinal) && trimmed.EndsWith(">", StringComparison.Ordinal))
        {
            var inner = trimmed["Func<".Length..^1].Trim();
            return $"Func<{NormalizeReturnTypeToken(inner)}>";
        }

        var unqualified = trimmed;
        var lastDot = unqualified.LastIndexOf('.');
        if (lastDot >= 0 && lastDot + 1 < unqualified.Length)
            unqualified = unqualified[(lastDot + 1)..];

        return unqualified switch
        {
            "TextParameter" => "Text",
            "NumberParameter" => "Number",
            "DateParameter" => "Date",
            "TimeParameter" => "Time",
            "BoolParameter" => "Bool",
            "ByteArrayParameter" => "byte[]",
            "TextColumn" => "Text",
            "NumberColumn" => "Number",
            "DateColumn" => "Date",
            "TimeColumn" => "Time",
            "BoolColumn" => "Bool",
            "ByteArrayColumn" => "byte[]",
            "MVariableIndex" => "Number",
            "Text" => "Text",
            "Number" => "Number",
            "Date" => "Date",
            "Time" => "Time",
            "Bool" => "Bool",
            _ => trimmed
        };
    }

    private static string GetValueReturnType(string? returnType)
    {
        var normalized = NormalizeReturnTypeToken(returnType);
        if (normalized.StartsWith("Func<", StringComparison.Ordinal) && normalized.EndsWith(">", StringComparison.Ordinal))
            return normalized["Func<".Length..^1].Trim();
        return normalized;
    }

    private static string MapAttrObjToReturnType(string? attrObj)
    {
        return NormalizeAttrObjKind(attrObj) switch
        {
            "FIELD_ALPHA" => "Text",
            "FIELD_UNICODE" => "Text",
            "FIELD_NUMERIC" => "Number",
            "FIELD_DATE" => "Date",
            "FIELD_TIME" => "Time",
            "FIELD_BOOLEAN" => "Bool",
            "FIELD_LOGICAL" => "Bool",
            "FIELD_BLOB" => "byte[]",
            _ => ""
        };
    }

    private static string MapReturnTypeToAttrObj(string? returnType)
    {
        return GetValueReturnType(returnType) switch
        {
            "Text" => "FIELD_ALPHA",
            "Number" => "FIELD_NUMERIC",
            "Date" => "FIELD_DATE",
            "Time" => "FIELD_TIME",
            "Bool" => "FIELD_LOGICAL",
            "byte[]" => "FIELD_BLOB",
            _ => ""
        };
    }

    private static ExpectedTypeContext InferExpectedTypeFromComparisonOperands(TaskSemantic task, string left, string right)
    {
        var leftExpected = ResolveExpectedTypeFromExpressionEvidence(task, left);
        var rightExpected = ResolveExpectedTypeFromExpressionEvidence(task, right);

        if (leftExpected.HasExpectation || rightExpected.HasExpectation)
        {
            if (IsGenericObjectExpectation(leftExpected) && !IsGenericObjectExpectation(rightExpected) && rightExpected.HasExpectation)
                return rightExpected;

            if (IsGenericObjectExpectation(rightExpected) && !IsGenericObjectExpectation(leftExpected) && leftExpected.HasExpectation)
                return leftExpected;

            var leftPriority = GetComparisonExpectedTypePriority(leftExpected);
            var rightPriority = GetComparisonExpectedTypePriority(rightExpected);
            if (leftPriority > rightPriority && leftExpected.HasExpectation)
                return leftExpected;
            if (rightPriority > leftPriority && rightExpected.HasExpectation)
                return rightExpected;

            if (leftExpected.HasExpectation)
                return leftExpected;

            if (rightExpected.HasExpectation)
                return rightExpected;
        }

        return default;
    }

    private static bool IsCounterExpression(string expression)
        => string.Equals(StripRedundantOuterParentheses(expression.Trim()), "Counter", StringComparison.Ordinal);

    private static bool TryGetCachedExpectedTypeContext(TaskSemantic task, string expression, out ExpectedTypeContext expected)
    {
        if (_expectedTypeInferenceCacheByTask.TryGetValue(task, out var cache) &&
            cache.TryGetValue(expression, out expected))
            return true;

        expected = default;
        return false;
    }

    private static ExpectedTypeContext CacheExpectedTypeContext(TaskSemantic task, string expression, ExpectedTypeContext expected)
    {
        var cache = _expectedTypeInferenceCacheByTask.GetValue(
            task,
            static _ => new ConcurrentDictionary<string, ExpectedTypeContext>(StringComparer.Ordinal));
        cache[expression] = expected;
        return expected;
    }

    private static bool TryInferKnownFunctionReturnType(string functionName, IReadOnlyList<string> args, out string returnType)
    {
        returnType = "";

        if ((IsTopLevelCall(functionName, "u.Case") ||
             IsTopLevelCall(functionName, "u.CaseUntyped")) &&
            TryInferCaseReturnType(args, out returnType))
        {
            return true;
        }

        return TryResolveKnownXpaFunctionReturnType(functionName, args, out returnType);
    }

    private static bool TryInferCaseReturnType(IReadOnlyList<string> args, out string returnType)
    {
        returnType = "";
        if (args.Count < 3)
            return false;

        string? candidate = null;
        for (var i = 2; i < args.Count; i++)
        {
            var branchExpected = TryResolveLiteralOrIntrinsicExpectedType(args[i].Trim());
            if (!branchExpected.HasExpectation)
                return false;

            var branchReturnType = GetValueReturnType(branchExpected.ReturnType);
            if (string.IsNullOrWhiteSpace(branchReturnType))
                branchReturnType = MapAttrObjToReturnType(branchExpected.AttrObj);
            if (string.IsNullOrWhiteSpace(branchReturnType) ||
                string.Equals(branchReturnType, "object", StringComparison.Ordinal))
                return false;

            if (candidate is null)
            {
                candidate = branchReturnType;
                continue;
            }

            if (!string.Equals(candidate, branchReturnType, StringComparison.Ordinal))
                return false;
        }

        returnType = candidate ?? "";
        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static ExpectedTypeContext TryResolveLiteralOrIntrinsicExpectedType(string expression)
    {
        if (TryResolveLiteralExpectedType(expression, out var literalExpected))
            return literalExpected;

        if (TryParseFunctionCall(expression, out var functionName, out var args) &&
            TryInferKnownFunctionReturnType(functionName, args, out var knownReturnType))
            return ExpectedTypeForReturnType(knownReturnType);

        return default;
    }

    private static bool TryGetLiteralNumberValue(string expression, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        if (TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            args.Count == 1 &&
            IsTopLevelCall(functionName, "u.CastToNumber"))
            return int.TryParse(args[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        return false;
    }

    private static bool IsExpressionAlreadyCompatible(string expression, TaskSemantic task, ExpectedTypeContext expected)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return true;

        var trimmed = StripRedundantOuterParentheses(expression.Trim());

        if (!expected.IsBooleanCondition &&
            string.Equals(GetValueReturnType(expected.ReturnType), "Number", StringComparison.Ordinal) &&
            TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            IsTopLevelCall(functionName, "u.CastToNumber") &&
            args.Count == 1)
        {
            var inner = StripRedundantOuterParentheses(args[0].Trim());
            if ((TryParseFunctionCall(inner, out var innerFunctionName, out var innerArgs) &&
                 IsTopLevelCall(innerFunctionName, "u.If") &&
                 innerArgs.Count == 3) ||
                SplitTopLevelArithmeticExpression(inner) is not null)
            {
                return false;
            }
        }

        if (TryResolveDeclaredSimpleExpressionExpectedType(task, trimmed, out var declaredSimpleExpected))
            return ExpectedTypesMatch(declaredSimpleExpected, expected);

        if (TryResolveLiteralExpectedType(expression, out var literalExpected))
            return ExpectedTypesMatch(literalExpected, expected);

        var inferred = ResolveExpectedTypeFromExpressionEvidence(task, expression);
        return inferred.HasExpectation && ExpectedTypesMatch(inferred, expected);
    }

    private static bool TryResolveDeclaredSimpleExpressionExpectedType(
        TaskSemantic task,
        string expression,
        out ExpectedTypeContext expected)
    {
        expected = default;
        if (string.IsNullOrWhiteSpace(expression) || !IsSimpleIdentifierPath(expression))
            return false;

        var declaredReturnType = ResolveExpressionReturnType(null, expression.Trim(), task);
        var normalizedReturnType = NormalizeReturnTypeToken(declaredReturnType);
        if (string.IsNullOrWhiteSpace(normalizedReturnType) ||
            string.Equals(normalizedReturnType, "object", StringComparison.Ordinal))
            return false;

        expected = ExpectedTypeForReturnType(normalizedReturnType);
        return expected.HasExpectation;
    }

    private static bool ExpectedTypesMatch(ExpectedTypeContext actual, ExpectedTypeContext expected)
    {
        if (expected.IsBooleanCondition)
            return actual.IsBooleanCondition ||
                   string.Equals(actual.AttrObj, "FIELD_BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(actual.AttrObj, "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(actual.ReturnType, "Bool", StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(expected.AttrObj) &&
            string.Equals(actual.AttrObj, expected.AttrObj, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(expected.ReturnType) &&
            string.Equals(NormalizeReturnTypeToken(actual.ReturnType), NormalizeReturnTypeToken(expected.ReturnType), StringComparison.Ordinal))
            return true;

        if (!string.IsNullOrWhiteSpace(expected.AttrObj) &&
            string.Equals(MapReturnTypeToAttrObj(actual.ReturnType), expected.AttrObj, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(expected.ReturnType) &&
            string.Equals(MapAttrObjToReturnType(actual.AttrObj), GetValueReturnType(expected.ReturnType), StringComparison.Ordinal))
            return true;

        return false;
    }

    private static bool IsGenericObjectExpectation(ExpectedTypeContext expected)
    {
        return string.Equals(NormalizeReturnTypeToken(expected.ReturnType), "object", StringComparison.Ordinal) &&
               string.IsNullOrWhiteSpace(expected.AttrObj) &&
               !expected.IsBooleanCondition;
    }

    private static bool HasNumericArithmeticAffinity(ExpectedTypeContext leftExpected, ExpectedTypeContext rightExpected)
    {
        return IsNumericLikeExpectedType(leftExpected) || IsNumericLikeExpectedType(rightExpected);
    }

    private static int GetComparisonExpectedTypePriority(ExpectedTypeContext expected)
    {
        if (!expected.HasExpectation || IsGenericObjectExpectation(expected))
            return 0;

        if (IsTextLikeExpectedType(expected))
            return 1;

        if (IsNumericLikeExpectedType(expected) || IsBoolLikeExpectedType(expected) || IsBlobLikeExpectedType(expected))
            return 3;

        return 2;
    }

    private static bool IsTextLikeExpectedType(ExpectedTypeContext expected)
    {
        return string.Equals(NormalizeAttrObjKind(expected.AttrObj), "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(GetValueReturnType(NormalizeReturnTypeToken(expected.ReturnType)), "Text", StringComparison.Ordinal);
    }

    private static bool IsNumericLikeExpectedType(ExpectedTypeContext expected)
    {
        var attrObj = NormalizeAttrObjKind(expected.AttrObj);
        var returnType = GetValueReturnType(NormalizeReturnTypeToken(expected.ReturnType));
        return string.Equals(attrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(attrObj, "FIELD_DATE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(attrObj, "FIELD_TIME", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(returnType, "Number", StringComparison.Ordinal) ||
               string.Equals(returnType, "Date", StringComparison.Ordinal) ||
               string.Equals(returnType, "Time", StringComparison.Ordinal);
    }

    private static bool IsBoolLikeExpectedType(ExpectedTypeContext expected)
    {
        var attrObj = NormalizeAttrObjKind(expected.AttrObj);
        var returnType = GetValueReturnType(NormalizeReturnTypeToken(expected.ReturnType));
        return string.Equals(attrObj, "FIELD_BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(attrObj, "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(returnType, "Bool", StringComparison.Ordinal);
    }

    private static bool IsBlobLikeExpectedType(ExpectedTypeContext expected)
    {
        var attrObj = NormalizeAttrObjKind(expected.AttrObj);
        var returnType = GetValueReturnType(NormalizeReturnTypeToken(expected.ReturnType));
        return string.Equals(attrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(returnType, "byte[]", StringComparison.Ordinal);
    }

    private static bool IsBooleanContextReadyExpression(TaskSemantic task, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return true;

        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
            return true;

        if (TryParseFunctionCall(trimmed, out var functionName, out _))
        {
            if (IsTopLevelCall(functionName, "u.Not") ||
                IsTopLevelCall(functionName, "u.CastToBool") ||
                IsTopLevelCall(functionName, "u.GetBoolParam"))
                return true;
        }

        var booleanBinary = SplitTopLevelBooleanBinaryExpression(trimmed);
        if (booleanBinary is not null)
        {
            return IsBooleanContextReadyExpression(task, booleanBinary.Value.Left) &&
                   IsBooleanContextReadyExpression(task, booleanBinary.Value.Right);
        }

        var comparison = SplitTopLevelComparisonExpression(trimmed);
        if (comparison is null)
            return false;

        var left = comparison.Value.Left.Trim();
        var right = comparison.Value.Right.Trim();
        var comparisonExpected = InferExpectedTypeFromComparisonOperands(task, left, right);
        if (!comparisonExpected.HasExpectation)
            return true;

        return IsExpressionAlreadyCompatible(left, task, comparisonExpected) &&
               IsExpressionAlreadyCompatible(right, task, comparisonExpected);
    }

    private static bool TryResolveLiteralExpectedType(string expression, out ExpectedTypeContext expected)
    {
        expected = default;
        var trimmed = expression.Trim();
        if (trimmed.Length == 0)
            return false;

        if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
        {
            expected = ExpectedTypeForAttrObj("FIELD_LOGICAL");
            return true;
        }

        if ((trimmed.StartsWith("\"", StringComparison.Ordinal) && trimmed.EndsWith("\"", StringComparison.Ordinal)) ||
            (trimmed.StartsWith("@\"", StringComparison.Ordinal) && trimmed.EndsWith("\"", StringComparison.Ordinal)))
        {
            expected = ExpectedTypeForAttrObj("FIELD_ALPHA");
            return true;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            expected = ExpectedTypeForAttrObj("FIELD_NUMERIC");
            return true;
        }

        return false;
    }

    private static bool TryNormalizeWrappedScalarExpression(
        string expression,
        TaskSemantic task,
        ExpectedTypeContext expected,
        out string normalized)
    {
        normalized = expression;
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        if (!TryParseFunctionCall(expression, out var functionName, out var args) || args.Count != 1)
            return false;

        var inner = args[0].Trim();

        if (IsTopLevelCall(functionName, "u.Not"))
        {
            normalized = $"u.Not({ApplyExpectedTypeContext(inner, task, ExpectedBooleanCondition())})";
            return true;
        }

        var expectedAttrObj = !string.IsNullOrWhiteSpace(expected.AttrObj)
            ? expected.AttrObj
            : MapReturnTypeToAttrObj(GetValueReturnType(expected.ReturnType));
        var wrapperAttrObj = ResolveWrapperAttrObj(functionName);

        if (expected.IsBooleanCondition)
        {
            if (string.Equals(wrapperAttrObj, "FIELD_BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(wrapperAttrObj, "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase))
            {
                normalized = $"{functionName}({ApplyExpectedTypeContext(inner, task, ExpectedBooleanCondition())})";
                return true;
            }

            if (ShouldReanchorExplicitTypedCast(expression, "FIELD_LOGICAL"))
            {
                normalized = RewriteBooleanOperand(task, ApplyExpectedTypeContext(inner, task, ExpectedBooleanCondition()));
                return true;
            }

            return false;
        }

        if (string.IsNullOrWhiteSpace(wrapperAttrObj) || string.IsNullOrWhiteSpace(expectedAttrObj))
            return false;

        if (ShouldReanchorExplicitTypedCast(expression, expectedAttrObj))
        {
            var innerNormalized = ApplyExpectedTypeContext(inner, task, expected);
            normalized = ApplyAttributeCast(innerNormalized, expectedAttrObj);
            return true;
        }

        if (string.Equals(wrapperAttrObj, NormalizeAttrObjKind(expectedAttrObj), StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"{functionName}({ApplyExpectedTypeContext(inner, task, expected)})";
            return true;
        }

        return false;
    }

    private static string ResolveWrapperAttrObj(string functionName)
    {
        if (IsTopLevelCall(functionName, "u.CastToText"))
            return "FIELD_ALPHA";
        if (IsTopLevelCall(functionName, "u.CastToNumber"))
            return "FIELD_NUMERIC";
        if (IsTopLevelCall(functionName, "u.CastToDate"))
            return "FIELD_DATE";
        if (IsTopLevelCall(functionName, "u.CastToTime") || IsTopLevelCall(functionName, "UserMethods.ToTime"))
            return "FIELD_TIME";
        if (IsTopLevelCall(functionName, "u.CastToBool"))
            return "FIELD_LOGICAL";
        if (IsTopLevelCall(functionName, "u.CastToByteArray"))
            return "FIELD_BLOB";

        return "";
    }

    private static (string Left, string Operator, string Right)? SplitTopLevelComparisonExpression(string expression)
    {
        TrackLegacyExpressionTreatment("Parser", nameof(SplitTopLevelComparisonExpression));
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var operators = new[] { "==", "!=", ">=", "<=", ">", "<" };
        var depth = 0;
        for (var i = 0; i < expression.Length; i++)
        {
            if (IsQuotedSegmentStart(expression, i))
            {
                if (!TryReadQuotedSegmentEnd(expression, i, out var quoteEnd))
                    return null;
                i = quoteEnd;
                continue;
            }

            var ch = expression[i];
            if (ch == '(')
            {
                depth++;
                continue;
            }

            if (ch == ')')
            {
                depth--;
                continue;
            }

            if (depth != 0)
                continue;

            foreach (var op in operators)
            {
                if (!expression.AsSpan(i).StartsWith(op.AsSpan(), StringComparison.Ordinal))
                    continue;

                var left = expression[..i].Trim();
                var right = expression[(i + op.Length)..].Trim();
                if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                    return null;

                return (left, op, right);
            }
        }

        return null;
    }

    private static (string Left, string Operator, string Right)? SplitTopLevelArithmeticExpression(string expression)
    {
        TrackLegacyExpressionTreatment("Parser", nameof(SplitTopLevelArithmeticExpression));
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var depth = 0;
        var operatorIndex = -1;
        char operatorChar = '\0';
        for (var i = 0; i < expression.Length; i++)
        {
            if (IsQuotedSegmentStart(expression, i))
            {
                if (!TryReadQuotedSegmentEnd(expression, i, out var quoteEnd))
                    return null;

                i = quoteEnd;
                continue;
            }

            var ch = expression[i];
            if (ch == '(')
            {
                depth++;
                continue;
            }

            if (ch == ')')
            {
                depth--;
                continue;
            }

            if (depth != 0 || (ch is not ('+' or '-' or '*' or '/' or '%')))
                continue;

            if (ch is '+' or '-')
            {
                var previous = i - 1;
                while (previous >= 0 && char.IsWhiteSpace(expression[previous]))
                    previous--;

                if (previous < 0 || "+-*/%(<>=!&|,".Contains(expression[previous]))
                    continue;
            }

            var leftCandidate = expression[..i].Trim();
            var rightCandidate = expression[(i + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(leftCandidate) || string.IsNullOrWhiteSpace(rightCandidate))
                continue;

            operatorIndex = i;
            operatorChar = ch;
        }

        if (operatorIndex < 0)
            return null;

        var left = expression[..operatorIndex].Trim();
        var right = expression[(operatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return null;

        return (left, operatorChar.ToString(), right);
    }

    private static string NormalizeByteArrayExpectedExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = expression.Trim();

        if (TryNormalizeByteArrayConcatenationExpression(trimmed, out var normalizedConcat))
            return $"u.CastToByteArray({normalizedConcat})";

        if (TryParseFunctionCall(expression.Trim(), out var outerFunctionName, out var outerArgs) &&
            outerArgs.Count == 1 &&
            IsTopLevelCall(outerFunctionName, "u.CastToByteArray"))
        {
            var normalizedInner = NormalizeByteArrayTextPayloadExpression(outerArgs[0].Trim());

            return $"u.CastToByteArray({normalizedInner})";
        }

        expression = NormalizeByteArrayTextPayloadExpression(expression);
        trimmed = expression.Trim();

        return HasByteArrayWrapping(expression)
            ? expression
            : IsTextualBlobAssignmentExpression(trimmed)
                ? $"u.CastToByteArray({trimmed})"
                : $"u.CastToByteArray({NormalizeTextSinkArgumentCentral(expression)})";
    }

    private static bool TryNormalizeByteArrayConcatenationExpression(string expression, out string normalized)
    {
        normalized = expression;
        var pieces = new List<string>();
        if (!TryCollectByteArrayConcatTextPieces(expression, pieces))
            return false;

        normalized = string.Join(" + ", pieces);
        return true;
    }

    private static bool TryCollectByteArrayConcatTextPieces(string expression, List<string> pieces)
    {
        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        var arithmetic = SplitTopLevelArithmeticExpression(trimmed);
        if (arithmetic is not null)
        {
            if (!string.Equals(arithmetic.Value.Operator, "+", StringComparison.Ordinal))
                return false;

            return TryCollectByteArrayConcatTextPieces(arithmetic.Value.Left, pieces) &&
                   TryCollectByteArrayConcatTextPieces(arithmetic.Value.Right, pieces);
        }

        pieces.Add(NormalizeByteArrayConcatLeafToText(trimmed));
        return true;
    }

    private static string NormalizeByteArrayConcatLeafToText(string expression)
    {
        var trimmed = StripRedundantOuterParentheses(expression.Trim());

        if (TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            args.Count == 1 &&
            IsTopLevelCall(functionName, "u.CastToByteArray"))
        {
            return NormalizeByteArrayConcatLeafToText(args[0].Trim());
        }

        if (TryGetWholeCSharpStringLiteral(trimmed, out _))
            return trimmed;

        if (trimmed.Contains("u.DStr(", StringComparison.Ordinal) ||
            trimmed.Contains("u.TStr(", StringComparison.Ordinal) ||
            trimmed.Contains("u.MTStr(", StringComparison.Ordinal) ||
            trimmed.Contains("u.Str(", StringComparison.Ordinal) ||
            trimmed.Contains("u.If(", StringComparison.Ordinal))
            trimmed = NormalizeFormattingFunctionInputsCentral(trimmed);

        if (trimmed.Contains("u.StrBuild(", StringComparison.Ordinal) ||
            trimmed.Contains("StrBuild(", StringComparison.Ordinal))
            trimmed = NormalizeTextFunctionInputsCentral(trimmed);

        trimmed = StripExpectedAttributeCastWrappers(StripExpectedAttributeCastWrappers(trimmed, "FIELD_BLOB"), "FIELD_ALPHA");
        return EmitScalarArgumentFromEvidence(trimmed, "Text");
    }

    private static string NormalizeByteArrayTextPayloadExpression(string expression)
    {
        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeByteArrayTextPayloadExpression));
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = expression.Trim();

        if (TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            args.Count == 1 &&
            IsTopLevelCall(functionName, "u.CastToByteArray"))
        {
            var inner = NormalizeByteArrayTextPayloadExpression(args[0].Trim());
            return IsTextualBlobAssignmentExpression(inner)
                ? inner
                : $"u.CastToByteArray({inner})";
        }

        if (TryFlattenByteArrayTextConcatenation(trimmed, out var flattened))
            return flattened;

        if (trimmed.Contains("u.StrBuild(", StringComparison.Ordinal) ||
            trimmed.Contains("StrBuild(", StringComparison.Ordinal))
            return NormalizeTextFunctionInputsCentral(trimmed);

        if (trimmed.Contains("u.DStr(", StringComparison.Ordinal) ||
            trimmed.Contains("u.TStr(", StringComparison.Ordinal) ||
            trimmed.Contains("u.MTStr(", StringComparison.Ordinal) ||
            trimmed.Contains("u.Str(", StringComparison.Ordinal) ||
            trimmed.Contains("u.If(", StringComparison.Ordinal))
            return NormalizeFormattingFunctionInputsCentral(trimmed);

        return trimmed;
    }

    private static bool TryFlattenByteArrayTextConcatenation(string expression, out string normalized)
    {
        normalized = expression;
        var pieces = new List<string>();
        if (!TryCollectByteArrayTextConcatPieces(expression, pieces))
            return false;

        normalized = string.Join(" + ", pieces);
        return true;
    }

    private static bool TryCollectByteArrayTextConcatPieces(string expression, List<string> pieces)
    {
        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        var arithmetic = SplitTopLevelArithmeticExpression(trimmed);
        if (arithmetic is not null)
        {
            if (!string.Equals(arithmetic.Value.Operator, "+", StringComparison.Ordinal))
                return false;

            return TryCollectByteArrayTextConcatPieces(arithmetic.Value.Left, pieces) &&
                   TryCollectByteArrayTextConcatPieces(arithmetic.Value.Right, pieces);
        }

        if (TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            args.Count == 1 &&
            IsTopLevelCall(functionName, "u.CastToByteArray"))
        {
            var payload = args[0].Trim();
            if (payload.Contains("u.StrBuild(", StringComparison.Ordinal) ||
                payload.Contains("StrBuild(", StringComparison.Ordinal))
                payload = NormalizeTextFunctionInputsCentral(payload);

            if (!IsTextualBlobAssignmentExpression(payload))
                return false;

            pieces.Add(payload);
            return true;
        }

        if (!IsTextualBlobAssignmentExpression(trimmed))
            return false;

        pieces.Add(trimmed);
        return true;
    }

    private static bool IsByteArrayLikeExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = expression.Trim();
        if (HasByteArrayWrapping(trimmed))
            return true;

        if (trimmed.StartsWith("Array.Empty<byte>()", StringComparison.Ordinal) ||
            trimmed.StartsWith("new byte[", StringComparison.Ordinal) ||
            trimmed.StartsWith("new byte[]", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static bool IsByteArrayArrayLikeExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = expression.Trim();
        return trimmed.StartsWith("new byte[][]", StringComparison.Ordinal) ||
               trimmed.StartsWith("Array.Empty<byte[]>()", StringComparison.Ordinal);
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    [ThreadStatic]
    private static int _centralExpressionEmissionDepth;

    private enum ExpressionSinkKind
    {
        Default,
        ExpectedValue,
        BooleanCondition,
        BindValue,
        FilterComparison,
        RunArgument,
        ReturnValue,
        Assignment,
        ViewBinding,
        DisplayExpression,
        CallArgument,
        ProgramReference,
        ProgramIndex,
        IoArgument,
        MessageText,
        EvaluateStatement,
        SqlExpression,
    }

    private readonly record struct ExpressionEmissionContext(
        ExpressionSinkKind SinkKind,
        ExpectedTypeContext Expected,
        string ParameterType,
        bool PreserveBinding,
        TargetValueInfo? TargetInfo,
        string BlobTarget)
    {
        internal bool HasExpectation => Expected.HasExpectation;
    }

    private static ExpressionEmissionContext CreateBooleanConditionEmissionContext()
        => new(ExpressionSinkKind.BooleanCondition, ExpectedBooleanCondition(), "", false, null, "");

    private static ExpressionEmissionContext CreateBindValueEmissionContext(TargetValueInfo targetInfo, string? blobTarget = null)
        => new(ExpressionSinkKind.BindValue, ExpectedTypeForTarget(targetInfo), "", false, targetInfo, blobTarget ?? targetInfo.TargetMember);

    private static ExpressionEmissionContext CreateFilterComparisonEmissionContext(TargetValueInfo targetInfo)
        => new(ExpressionSinkKind.FilterComparison, ExpectedTypeForTarget(targetInfo), "", false, targetInfo, "");

    private static ExpressionEmissionContext CreateRunArgumentEmissionContext(string parameterType, bool preserveBinding)
        => new(ExpressionSinkKind.RunArgument, ExpectedTypeForParameterType(parameterType), NormalizeReturnTypeToken(parameterType), preserveBinding, null, "");

    private static ExpressionEmissionContext CreateReturnValueEmissionContext(string returnType)
        => new(ExpressionSinkKind.ReturnValue, ExpectedTypeForReturnType(returnType), NormalizeReturnTypeToken(returnType), false, null, "");

    private static ExpressionEmissionContext CreateAssignmentEmissionContext(TargetValueInfo targetInfo, string blobTarget)
        => new(ExpressionSinkKind.Assignment, ExpectedTypeForTarget(targetInfo), "", false, targetInfo, blobTarget);

    private static ExpressionEmissionContext CreateViewBindingEmissionContext(string? expressionAttr)
        => new(ExpressionSinkKind.ViewBinding, ExpectedTypeForExpressionAttribute(expressionAttr), "", false, null, "");

    private static ExpressionEmissionContext CreateDisplayExpressionEmissionContext()
        => new(ExpressionSinkKind.DisplayExpression, ExpectedTypeForReturnType("Number"), "Number", false, null, "");

    private static ExpressionEmissionContext CreateCallArgumentEmissionContext()
        => new(ExpressionSinkKind.CallArgument, default, "", false, null, "");

    private static ExpressionEmissionContext CreateProgramReferenceEmissionContext()
        => new(ExpressionSinkKind.ProgramReference, ExpectedTypeForReturnType("Text"), "Text", false, null, "");

    private static ExpressionEmissionContext CreateProgramIndexEmissionContext()
        => new(ExpressionSinkKind.ProgramIndex, ExpectedTypeForReturnType("Number"), "Number", false, null, "");

    private static ExpressionEmissionContext CreateIoArgumentEmissionContext()
        => new(ExpressionSinkKind.IoArgument, ExpectedTypeForReturnType("Text"), "Text", false, null, "");

    private static ExpressionEmissionContext CreateMessageTextEmissionContext()
        => new(ExpressionSinkKind.MessageText, ExpectedTypeForReturnType("Text"), "Text", false, null, "");

    private static ExpressionEmissionContext CreateEvaluateStatementEmissionContext()
        => new(ExpressionSinkKind.EvaluateStatement, default, "", false, null, "");

    private static ExpressionEmissionContext CreateSqlExpressionEmissionContext()
        => new(ExpressionSinkKind.SqlExpression, default, "", false, null, "");

    private static ExpressionEmissionContext CreateExpectedEmissionContext(ExpectedTypeContext expected)
        => new(ExpressionSinkKind.ExpectedValue, expected, NormalizeReturnTypeToken(expected.ReturnType), false, null, "");

    private static string EmitExpressionForExpectedType(string code, TaskSemantic task, ExpectedTypeContext expected)
    {
        if (string.IsNullOrWhiteSpace(code) || !expected.HasExpectation)
            return code;

        return ExecuteWithinCentralExpressionEmission(() =>
            EmitExpressionForContext(code, task, CreateExpectedEmissionContext(expected)));
    }

    private static string EmitExpressionForAttrObj(string code, TaskSemantic task, string? attrObj)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(attrObj))
            return code;

        return EmitExpressionForExpectedType(code, task, ExpectedTypeForAttrObj(attrObj));
    }

    private static string CoerceExpressionForAttrObjWithTypeEngine(string expression, string? attrObj)
    {
        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(attrObj))
            return expression;

        var normalizedAttrObj = NormalizeAttrObjKind(attrObj);
        if (string.IsNullOrWhiteSpace(normalizedAttrObj))
            return expression;

        var trimmed = expression.Trim();
        if (string.Equals(normalizedAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
        {
            var topLevelCall = TryGetTopLevelFunctionName(trimmed);
            if (HasByteArrayWrapping(trimmed))
                return NormalizeByteArrayExpectedExpression(trimmed);

            if (IsTopLevelCall(topLevelCall, "u.File2Blb") ||
                IsTextArrayProducingExpression(topLevelCall))
                return trimmed;

            if (IsTextualBlobAssignmentExpression(trimmed))
                return $"u.CastToByteArray({trimmed})";
        }

        if (IsNullCallExpression(expression))
        {
            return normalizedAttrObj switch
            {
                "FIELD_NUMERIC" => "u.CastToNumber(u.Null())",
                "FIELD_BOOLEAN" or "FIELD_LOGICAL" => "u.CastToBool(u.Null())",
                "FIELD_DATE" => CoerceExpressionWithTypeEngine("u.Null()", null, ExpectedTypeForAttrObj("FIELD_DATE"), alwaysCoerceWholeExpression: true),
                "FIELD_TIME" => CoerceExpressionWithTypeEngine("u.Null()", null, ExpectedTypeForAttrObj("FIELD_TIME"), alwaysCoerceWholeExpression: true),
                "FIELD_ALPHA" => CoerceExpressionWithTypeEngine("u.Null()", null, ExpectedTypeForAttrObj("FIELD_ALPHA"), alwaysCoerceWholeExpression: true),
                "FIELD_BLOB" => CoerceExpressionWithTypeEngine("u.Null()", null, ExpectedTypeForAttrObj("FIELD_BLOB"), alwaysCoerceWholeExpression: true),
                _ => expression
            };
        }

        return CoerceExpressionWithTypeEngine(expression, null, ExpectedTypeForAttrObj(normalizedAttrObj), alwaysCoerceWholeExpression: true);
    }

    private static string BuildCompilableGapFallbackExpressionCentral(string? attrObj)
    {
        return NormalizeAttrObjKind(attrObj) switch
        {
            "FIELD_NUMERIC" => "0",
            "FIELD_BOOLEAN" or "FIELD_LOGICAL" => "false",
            "FIELD_DATE" => CoerceExpressionForAttrObjWithTypeEngine("u.Null()", "FIELD_DATE"),
            "FIELD_TIME" => CoerceExpressionForAttrObjWithTypeEngine("u.Null()", "FIELD_TIME"),
            "FIELD_BLOB" => CoerceExpressionForAttrObjWithTypeEngine("u.Null()", "FIELD_BLOB"),
            _ => EmitScalarArgumentFromEvidence("\"\"", "Text")
        };
    }

    private static string CoerceExpressionForReturnTypeWithTypeEngine(string expression, string returnType)
    {
        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(returnType))
            return expression;

        var normalizedReturnType = NormalizeReturnTypeToken(returnType);
        if (string.Equals(normalizedReturnType, "byte[]", StringComparison.Ordinal))
        {
            var trimmed = expression.Trim();
            if (HasByteArrayWrapping(trimmed))
                return trimmed;

            var topLevelCall = TryGetTopLevelFunctionName(trimmed);
            if (TryGetWholeCSharpStringLiteral(trimmed, out _) ||
                IsTextualBlobAssignmentExpression(trimmed) ||
                IsObjectProducingExpression(topLevelCall))
                return NormalizeByteArrayExpectedExpression(trimmed);

            return CoerceExpressionWithTypeEngine(trimmed, null, ExpectedTypeForReturnType("byte[]"), alwaysCoerceWholeExpression: true);
        }

        return normalizedReturnType switch
        {
            "Text" => EmitScalarArgumentFromEvidence(expression, "Text"),
            "Number" => EmitScalarArgumentFromEvidence(expression, "Number"),
            "Date" => EmitScalarArgumentFromEvidence(expression, "Date"),
            "Time" => EmitScalarArgumentFromEvidence(expression, "Time"),
            "Bool" => EmitScalarArgumentFromEvidence(expression, "Bool"),
            _ => CoerceExpressionWithTypeEngine(expression, null, ExpectedTypeForReturnType(returnType), alwaysCoerceWholeExpression: true)
        };
    }

    private static string CoerceExpressionWithTypeEngine(string expression, TaskSemantic? task, ExpectedTypeContext expected, bool alwaysCoerceWholeExpression = true)
    {
        if (string.IsNullOrWhiteSpace(expression) || !expected.HasExpectation)
            return expression;

        var expectedXpaType = ResolveExpectedXpaType(expected);
        if (expectedXpaType == XpaType.Unknown)
            return expression;

        var trimmed = expression.Trim();
        XpaType ResolveType(string candidate) =>
            task is null
                ? ResolveExpressionXpaTypeWithoutTask(candidate)
                : ResolveExpressionXpaType(task, candidate);

        if (TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            IsTopLevelCall(functionName, "u.If") &&
            args.Count == 3)
        {
            trimmed = XpaTypeEngine.NormalizeIf(
                args[0].Trim(),
                args[1].Trim(),
                args[2].Trim(),
                ResolveType);
        }

        if (alwaysCoerceWholeExpression ||
            (TryParseFunctionCall(trimmed, out functionName, out args) &&
             IsTopLevelCall(functionName, "u.If") &&
             args.Count == 3))
        {
            trimmed = XpaTypeEngine.CoerceToContext(trimmed, ResolveType, expectedXpaType);
        }

        return trimmed;
    }

    private static bool IsCentralExpressionEmissionActive()
        => _centralExpressionEmissionDepth > 0;

    private static string ExecuteWithinCentralExpressionEmission(Func<string> action)
    {
        _centralExpressionEmissionDepth++;
        try
        {
            return action();
        }
        finally
        {
            _centralExpressionEmissionDepth--;
        }
    }

    private static string ApplyExpectedTypeContextCentral(
        string code,
        TaskSemantic task,
        ExpectedTypeContext expected)
    {
        if (string.IsNullOrWhiteSpace(code) || !expected.HasExpectation)
            return code;

        var normalized = code.Trim();
        var original = normalized;
        var expectedReturnType = ResolveReturnTypeForExpectedContext(expected);
        if ((string.Equals(GetValueReturnType(expectedReturnType), "byte[]", StringComparison.Ordinal) ||
             string.Equals(NormalizeAttrObjKind(expected.AttrObj), "FIELD_BLOB", StringComparison.OrdinalIgnoreCase)) &&
            TryResolveNewClrExpressionReturnType(normalized, out var newClrReturnType))
            return NormalizeDotNetAssignmentExpression(normalized, newClrReturnType);

        if (IsKnownExpressionReturnTypeCompatible(normalized, expectedReturnType, task))
            return normalized;

        if (TrySplitLeadingOpaqueInlineComment(normalized, out var leadingComment, out var uncommented))
        {
            var rewritten = ApplyExpectedTypeContextCentral(uncommented, task, expected);
            return TrackLegacyExpressionTreatmentIfChanged(
                "Context",
                nameof(ApplyExpectedTypeContextCentral),
                original,
                string.IsNullOrWhiteSpace(rewritten) ? normalized : $"{leadingComment} {rewritten}");
        }
        if (HasOpaqueInlineComment(normalized))
            return normalized;

        var cacheKey = BuildContextualInferenceCacheKey(task, normalized, expected);
        if (_contextualInferenceCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (CanBypassContextualInference(normalized, task, expected))
        {
            _contextualInferenceCache[cacheKey] = normalized;
            return normalized;
        }

        _contextualInferenceCache[cacheKey] = normalized;
        return normalized;

#pragma warning disable CS0162
        normalized = ApplyExpectedTypeToInternalOperators(normalized, task, expected);

        if (expected.IsBooleanCondition)
            normalized = RewriteBooleanOperand(task, normalized);

        var valueReturnType = GetValueReturnType(expected.ReturnType);
        if (string.Equals(valueReturnType, "byte[]", StringComparison.Ordinal))
            normalized = NormalizeByteArrayExpectedExpression(normalized);
        else if (!string.IsNullOrWhiteSpace(expected.AttrObj))
        {
            normalized = StripRedundantOuterParentheses(normalized.Trim());
            if (ShouldReanchorExplicitTypedCast(normalized, expected.AttrObj))
                normalized = ApplyAttributeCast(normalized, expected.AttrObj);
        }

        var normalizedExpectedAttrObj = NormalizeAttrObjKind(expected.AttrObj);
        var attrReturnType = GetValueReturnType(MapAttrObjToReturnType(normalizedExpectedAttrObj));
        var shouldApplyDeclaredReturnTypePass =
            !string.IsNullOrWhiteSpace(valueReturnType) &&
            !string.Equals(valueReturnType, "object", StringComparison.Ordinal) &&
            !string.Equals(valueReturnType, "byte[][]", StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(attrReturnType) ||
             !string.Equals(attrReturnType, GetValueReturnType(valueReturnType), StringComparison.Ordinal));

        if (shouldApplyDeclaredReturnTypePass)
            normalized = NormalizeExpressionForDeclaredReturnType(normalized, valueReturnType, null);

        _contextualInferenceCache[cacheKey] = normalized;
        return TrackLegacyExpressionTreatmentIfChanged(
            "Context",
            nameof(ApplyExpectedTypeContextCentral),
            original,
            normalized);
#pragma warning restore CS0162
    }

    private static string NormalizeVariantGetExpression(string value)
        => NormalizeVariantGetExpressionCentral(value);

    private static string ApplyAttributeCastCentral(string expression, string? attrObj)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = expression.Trim();
        if (trimmed.StartsWith("() =>", StringComparison.Ordinal))
        {
            var body = trimmed["() =>".Length..].Trim();
            return $"() => {ApplyAttributeCastCentral(body, attrObj)}";
        }

        var normalizedAttrObj = NormalizeAttrObjKind(attrObj);
        var scalarReturnType = MapAttrObjToReturnType(normalizedAttrObj);
        var strippedConditionalRoot = StripExpectedAttributeCastWrappers(trimmed, normalizedAttrObj);
        if (!string.Equals(strippedConditionalRoot, trimmed, StringComparison.Ordinal) &&
            TryParseFunctionCall(strippedConditionalRoot, out var strippedConditionalName, out var strippedConditionalArgs) &&
            strippedConditionalArgs.Count == 3 &&
            IsTopLevelCall(strippedConditionalName, "u.If"))
        {
            var whenTrueSource = strippedConditionalArgs[1].Trim();
            var whenFalseSource = strippedConditionalArgs[2].Trim();
            var whenTrue = ApplyAttributeCastCentral(whenTrueSource, attrObj);
            var whenFalse = ApplyAttributeCastCentral(whenFalseSource, attrObj);
            return $"u.If({strippedConditionalArgs[0].Trim()}, {whenTrue}, {whenFalse})";
        }

        if (TryParseFunctionCall(trimmed, out var explicitCastName, out var explicitCastArgs) &&
            explicitCastArgs.Count == 1 &&
            IsAnyAttributeCastFunction(explicitCastName))
        {
            var inner = explicitCastArgs[0].Trim();
            if (IsExpectedAttrCastFunction(explicitCastName, attrObj))
            {
                if (TryParseFunctionCall(inner, out var conditionalName, out var conditionalArgs) &&
                    conditionalArgs.Count == 3 &&
                    IsTopLevelCall(conditionalName, "u.If"))
                {
                    var whenTrueSource = conditionalArgs[1].Trim();
                    var whenFalseSource = conditionalArgs[2].Trim();
                    var whenTrue = ApplyAttributeCastCentral(whenTrueSource, attrObj);
                    var whenFalse = ApplyAttributeCastCentral(whenFalseSource, attrObj);
                    return $"u.If({conditionalArgs[0].Trim()}, {whenTrue}, {whenFalse})";
                }

                return trimmed;
            }

            return ApplyAttributeCastCentral(inner, attrObj);
        }

        if (TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            args.Count == 3 &&
            IsTopLevelCall(functionName, "u.If"))
        {
            var whenTrue = ApplyAttributeCastCentral(args[1].Trim(), attrObj);
            var whenFalse = ApplyAttributeCastCentral(args[2].Trim(), attrObj);
            return $"u.If({args[0].Trim()}, {whenTrue}, {whenFalse})";
        }

        if (TryParseFunctionCall(trimmed, out functionName, out args) &&
            args.Count == 1 &&
            IsExpectedAttrCastFunction(functionName, attrObj))
        {
            var inner = args[0].Trim();
            if (TryParseFunctionCall(inner, out var nestedCastName, out var nestedCastArgs) &&
                nestedCastArgs.Count == 1 &&
                IsExpectedAttrCastFunction(nestedCastName, attrObj))
                return ApplyAttributeCastCentral(inner, attrObj);

            return $"{functionName}({ApplyAttributeCastCentral(inner, attrObj)})";
        }

        return CoerceAttributeCastWithTypeEngineOrFallbackCentral(trimmed, normalizedAttrObj);
    }

    private static string CoerceAttributeCastWithTypeEngineOrFallbackCentral(string expression, string? normalizedAttrObj)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var coerced = CoerceExpressionWithTypeEngine(expression, null, ExpectedTypeForAttrObj(normalizedAttrObj), alwaysCoerceWholeExpression: true);
        if (!string.Equals(coerced, expression, StringComparison.Ordinal))
            return coerced;

        if (string.Equals(normalizedAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return NormalizeByteArrayExpectedExpression(expression);

        var rewrittenGetter = NormalizeParameterGetterForAttributeCentral(expression, normalizedAttrObj);
        if (!string.Equals(rewrittenGetter, expression, StringComparison.Ordinal))
            return rewrittenGetter;

        if (!string.Equals(normalizedAttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalizedAttrObj, "FIELD_DATE", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalizedAttrObj, "FIELD_TIME", StringComparison.OrdinalIgnoreCase))
        {
            var coercedScalar = EnsureScalarAttrObjCallArgumentCentral(expression, normalizedAttrObj);
            if (!string.Equals(coercedScalar, expression, StringComparison.Ordinal))
                return coercedScalar;
        }

        var explicitCastFunction = GetExpectedAttrCastFunctionName(normalizedAttrObj ?? "");
        return string.IsNullOrWhiteSpace(explicitCastFunction)
            ? expression
            : $"{explicitCastFunction}({expression})";
    }

    private static string AdjustBindValueExpressionForTargetCentral(TaskLogicSelectDef sel, string targetExpr, string bindExpr, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(bindExpr))
            return bindExpr;

        var targetInfo = ResolveBindValueTargetInfoFromEvidence(task, sel, targetExpr);
        if (targetInfo.Resource is not null && targetInfo.IsDotNet)
            return bindExpr;

        var targetMember = targetInfo.TargetMember;
        var attrObj = targetInfo.AttrObj;
        if (string.IsNullOrWhiteSpace(attrObj))
            return bindExpr;

        var context = CreateBindValueEmissionContext(targetInfo, targetMember);
        return TryEmitThroughStrictEmittedExpression(bindExpr, task, context, out var emittedBindExpr)
            ? emittedBindExpr
            : bindExpr;
    }

    private static TargetValueInfo ResolveBindValueTargetInfoFromEvidence(TaskSemantic task, TaskLogicSelectDef sel, string targetExpr)
    {
        var targetPath = targetExpr?.Trim() ?? "";
        var generatedTargetAttrObj = ResolveGeneratedBindTargetAttrObj(task, targetPath, allowNameInference: false);
        if (string.IsNullOrWhiteSpace(generatedTargetAttrObj) &&
            TryResolveDataViewMemberColumn(task, targetPath, out _, out var dataViewColumn))
        {
            generatedTargetAttrObj = ResolveEffectiveDataColumnAttrObj(dataViewColumn);
        }

        if (!string.IsNullOrWhiteSpace(generatedTargetAttrObj))
        {
            var explicitTargetInfo = ResolveTargetValueInfo(task, null, targetPath);
            explicitTargetInfo = RecalibrateTargetInfoFromAttrObj(explicitTargetInfo, generatedTargetAttrObj);
            return string.IsNullOrWhiteSpace(explicitTargetInfo.TargetMember)
                ? explicitTargetInfo with { TargetMember = targetPath }
                : explicitTargetInfo;
        }

        var targetResource = ResolveTaskResourceColumn(task, sel.ColumnId);
        var targetInfo = ResolveTargetValueInfo(task, null, targetPath, targetResource);
        if (targetResource is null || string.IsNullOrWhiteSpace(targetInfo.AttrObj))
            targetInfo = RecalibrateTargetInfoFromDeclaredTargetType(task, targetInfo, targetPath);

        generatedTargetAttrObj = ResolveGeneratedBindTargetAttrObj(task, targetInfo.TargetMember, allowNameInference: false);
        if (!string.IsNullOrWhiteSpace(generatedTargetAttrObj))
            targetInfo = RecalibrateTargetInfoFromAttrObj(targetInfo, generatedTargetAttrObj);

        return targetInfo;
    }

    private static string NormalizeComparisonRightExpression(TaskSemantic task, string leftExpr, string rightExpr)
    {
        if (string.IsNullOrWhiteSpace(rightExpr))
            return rightExpr;

        var original = rightExpr;
        var targetInfo = ResolveTargetValueInfo(task, null, leftExpr);
        var trimmedRight = rightExpr.Trim();
        if (string.Equals(trimmedRight, "u.Null()", StringComparison.OrdinalIgnoreCase))
        {
            var targetAttrObj = ResolveEffectiveTargetAttrObj(targetInfo.AttrObj ?? "", targetInfo.ModelAttrObj ?? "");
            if (string.Equals(targetAttrObj, "FIELD_DATE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetAttrObj, "FIELD_TIME", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) ||
                IsNumericAttrObj(targetAttrObj) ||
                string.Equals(targetAttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase))
            {
                var expectedTargetInfo = RecalibrateTargetInfoFromAttrObj(targetInfo, targetAttrObj);
                if (string.IsNullOrWhiteSpace(expectedTargetInfo.TargetMember))
                    expectedTargetInfo = expectedTargetInfo with { TargetMember = leftExpr.Trim() };

                return EmitExpressionForContext(
                    trimmedRight,
                    task,
                    CreateFilterComparisonEmissionContext(expectedTargetInfo));
            }
        }
        if (string.IsNullOrWhiteSpace(targetInfo.AttrObj) &&
            string.IsNullOrWhiteSpace(targetInfo.ModelAttrObj) &&
            leftExpr.Trim().EndsWith(".Hora", StringComparison.OrdinalIgnoreCase))
        {
            return TrackLegacyExpressionTreatmentIfChanged(
                "NormalizeType",
                nameof(NormalizeComparisonRightExpression),
                original,
                EmitExpressionForContext(
                rightExpr,
                task,
                new ExpressionEmissionContext(ExpressionSinkKind.FilterComparison, ExpectedTypeForAttrObj("FIELD_TIME"), "", false, null, "")));
        }
        return TrackLegacyExpressionTreatmentIfChanged(
            "NormalizeType",
            nameof(NormalizeComparisonRightExpression),
            original,
            EmitExpressionForContext(rightExpr, task, CreateFilterComparisonEmissionContext(targetInfo)));
    }

    private static string RewriteSimpleBlobNullComparisons(string expression, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(expression) ||
            expression.IndexOf("u.Null()", StringComparison.OrdinalIgnoreCase) < 0)
            return expression;

        const string identifierPath = @"(?:[A-Za-z_][A-Za-z0-9_]*)(?:\.[A-Za-z_][A-Za-z0-9_]*)*";

        expression = Regex.Replace(
            expression,
            $@"(?<left>{identifierPath})\s*==\s*u\.Null\(\)",
            m => RewriteBlobNullComparisonMatch(m, task, equalsNull: true, operandGroup: "left"),
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        expression = Regex.Replace(
            expression,
            $@"u\.Null\(\)\s*==\s*(?<right>{identifierPath})",
            m => RewriteBlobNullComparisonMatch(m, task, equalsNull: true, operandGroup: "right"),
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        expression = Regex.Replace(
            expression,
            $@"(?<left>{identifierPath})\s*!=\s*u\.Null\(\)",
            m => RewriteBlobNullComparisonMatch(m, task, equalsNull: false, operandGroup: "left"),
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        expression = Regex.Replace(
            expression,
            $@"u\.Null\(\)\s*!=\s*(?<right>{identifierPath})",
            m => RewriteBlobNullComparisonMatch(m, task, equalsNull: false, operandGroup: "right"),
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        return expression;
    }

    private static string RewriteBlobNullComparisonMatch(Match match, TaskSemantic task, bool equalsNull, string operandGroup)
    {
        var operand = match.Groups[operandGroup].Value.Trim();
        if (!IsBlobComparableExpression(task, operand))
            return match.Value;

        var equalsExpr = $"u.Equals({operand}, null)";
        return equalsNull ? equalsExpr : "!" + equalsExpr;
    }

    private static bool IsBlobComparableExpression(TaskSemantic task, string expression)
    {
        if (!IsSimpleIdentifierPath(expression))
            return false;

        var targetInfo = ResolveTargetValueInfo(task, null, expression);
        return targetInfo.IsBlob;
    }

    private static string NormalizeNumericOperands(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        expression = RewriteFunctionCalls(expression, "u.ToNumber", args =>
        {
            if (args.Count != 1)
                return null;

            var original = args[0].Trim();
            if (!LooksLikeTextualNumericProjectionExpression(original))
                return null;

            return $"u.ToNumber({NormalizeTextualNumericProjectionArgumentCentral(original)})";
        });

        expression = Regex.Replace(
            expression,
            @"(?<!u\.ToNumber\()(?<![\w.])XPARuntimeCore\.Box\.Time\.Now(?=\s*[%+\-*/])",
            "u.ToNumber(XPARuntimeCore.Box.Time.Now)",
            RegexOptions.CultureInvariant);

        expression = Regex.Replace(
            expression,
            @"(?<!u\.ToNumber\()(?<![\w.])Time\.Now(?=\s*[%+\-*/])",
            "u.ToNumber(Time.Now)",
            RegexOptions.CultureInvariant);

        expression = Regex.Replace(
            expression,
            @"(?<!u\.ToNumber\()(?<![\w.])u\.Time\(\)(?=\s*[%+\-*/])",
            "u.ToNumber(XPARuntimeCore.Box.Time.Now)",
            RegexOptions.CultureInvariant);

        expression = Regex.Replace(
            expression,
            @"(?<!u\.ToNumber\()(?<![\w.])u\.TVal\((?>[^()]+|(?<o>\()|(?<-o>\)))+(?(o)(?!))\)(?=\s*[%+\-*/])",
            m => $"u.ToNumber({m.Value})",
            RegexOptions.CultureInvariant);

        expression = Regex.Replace(
            expression,
            @"(?<!u\.ToNumber\()(?<![\w.])u\.DVal\((?>[^()]+|(?<o>\()|(?<-o>\)))+(?(o)(?!))\)(?=\s*[%+\-*/])",
            m => $"u.ToNumber({m.Value})",
            RegexOptions.CultureInvariant);

        expression = RewriteNumericObjectOperandsOutsideQuotes(expression);

        return expression;
    }

    private static string RewriteNumericObjectOperandsOutsideQuotes(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return expr;

        var sb = new StringBuilder(expr.Length + 16);
        for (var i = 0; i < expr.Length;)
        {
            if (IsQuotedSegmentStart(expr, i))
            {
                if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
                    break;
                sb.Append(expr, i, quoteEnd - i + 1);
                i = quoteEnd + 1;
                continue;
            }

            if (TryReadIdentifierPath(expr, i, out var idEnd) &&
                idEnd < expr.Length &&
                expr[idEnd] == '(')
            {
                var functionName = expr[i..idEnd];
                var closeParen = FindMatchingParen(expr, idEnd);
                if (closeParen > idEnd &&
                    IsVarCurrentLikeFunctionName(functionName) &&
                    IsArithmeticOperatorContext(expr, i, closeParen + 1))
                {
                    sb.Append(EmitScalarArgumentFromEvidence(expr[i..(closeParen + 1)], "Number"));
                    i = closeParen + 1;
                    continue;
                }
            }

            sb.Append(expr[i]);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsArithmeticOperatorContext(string expr, int start, int endExclusive)
    {
        static bool IsArithmetic(char ch) => ch is '+' or '-' or '*' or '/' or '%';

        for (var i = start - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(expr[i]))
                continue;
            return IsArithmetic(expr[i]);
        }

        for (var i = endExclusive; i < expr.Length; i++)
        {
            if (char.IsWhiteSpace(expr[i]))
                continue;
            return IsArithmetic(expr[i]);
        }

        return false;
    }

    private static string NormalizeNumericConditionalBindingExpressionCentral(string expression)
    {
        if (!TryParseFunctionCall(expression, out var functionName, out var args) ||
            !string.Equals(functionName, "u.If", StringComparison.OrdinalIgnoreCase) ||
            args.Count != 3)
            return expression;

        if (IsTreeValueExpression(args[1]) || IsVecGetExpression(args[1]))
            args[1] = ApplyAttributeCastCentral(args[1].Trim(), "FIELD_NUMERIC");

        return TrackLegacyExpressionTreatmentIfChanged(
            "NormalizeType",
            nameof(NormalizeNumericConditionalBindingExpressionCentral),
            expression,
            $"{functionName}({string.Join(", ", args)})");
    }

    private static string NormalizeNumericConditionalBindingExpression(string expression)
        => NormalizeNumericConditionalBindingExpressionCentral(expression);

    private static string NormalizeDateConditionalExpressionCentral(string expression)
    {
        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeDateConditionalExpressionCentral));
        if (!TryParseFunctionCall(expression, out var functionName, out var args) ||
            !string.Equals(functionName, "u.If", StringComparison.OrdinalIgnoreCase) ||
            args.Count != 3)
            return expression;

        args[1] = NormalizeTypedConditionalBranchCentral(args[1], "Date");
        args[2] = NormalizeTypedConditionalBranchCentral(args[2], "Date");
        return $"{functionName}({string.Join(", ", args)})";
    }

    private static string NormalizeDateConditionalExpression(string expression)
        => NormalizeDateConditionalExpressionCentral(expression);

    private static string NormalizeTimeConditionalExpressionCentral(string expression)
    {
        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeTimeConditionalExpressionCentral));
        if (!TryParseFunctionCall(expression, out var functionName, out var args) ||
            !string.Equals(functionName, "u.If", StringComparison.OrdinalIgnoreCase) ||
            args.Count != 3)
            return expression;

        args[1] = NormalizeTypedConditionalBranchCentral(args[1], "Time");
        args[2] = NormalizeTypedConditionalBranchCentral(args[2], "Time");
        return $"{functionName}({string.Join(", ", args)})";
    }

    private static string NormalizeTimeConditionalExpression(string expression)
        => NormalizeTimeConditionalExpressionCentral(expression);

    private static string NormalizeConditionalByTypedBranchEvidenceCentral(string expression)
    {
        if (!TryParseFunctionCall(expression, out var functionName, out var args) ||
            !string.Equals(functionName, "u.If", StringComparison.OrdinalIgnoreCase) ||
            args.Count != 3)
            return expression;

        var whenTrue = args[1].Trim();
        var whenFalse = args[2].Trim();
        var trueCall = TryGetTopLevelFunctionName(whenTrue);
        var falseCall = TryGetTopLevelFunctionName(whenFalse);

        var hasDateEvidence =
            (IsNullCallExpression(whenTrue) && (IsTopLevelCall(falseCall, "u.DVal") || IsDateLikeExpression(whenFalse))) ||
            (IsNullCallExpression(whenFalse) && (IsTopLevelCall(trueCall, "u.DVal") || IsDateLikeExpression(whenTrue)));
        if (hasDateEvidence)
            return TrackLegacyExpressionTreatmentIfChanged(
                "NormalizeType",
                nameof(NormalizeConditionalByTypedBranchEvidenceCentral),
                expression,
                NormalizeDateConditionalExpressionCentral(expression));

        var hasTimeEvidence =
            (IsNullCallExpression(whenTrue) && (IsTopLevelCall(falseCall, "u.TVal") || IsTimeLikeExpression(whenFalse))) ||
            (IsNullCallExpression(whenFalse) && (IsTopLevelCall(trueCall, "u.TVal") || IsTimeLikeExpression(whenTrue)));
        if (hasTimeEvidence)
            return TrackLegacyExpressionTreatmentIfChanged(
                "NormalizeType",
                nameof(NormalizeConditionalByTypedBranchEvidenceCentral),
                expression,
                NormalizeTimeConditionalExpressionCentral(expression));

        return expression;
    }

    private static string NormalizeConditionalByTypedBranchEvidence(string expression)
        => NormalizeConditionalByTypedBranchEvidenceCentral(expression);

    private static string NormalizeConditionalInsideTypedCastCentral(string expression, string castFunctionName, string targetType)
    {
        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeConditionalInsideTypedCastCentral));
        if (!TryParseFunctionCall(expression, out var functionName, out var args) ||
            !string.Equals(functionName, castFunctionName, StringComparison.OrdinalIgnoreCase) ||
            args.Count != 1)
            return expression;

        var normalized = string.Equals(targetType, "Date", StringComparison.OrdinalIgnoreCase)
            ? NormalizeDateConditionalExpressionCentral(args[0])
            : NormalizeTimeConditionalExpressionCentral(args[0]);

        if (string.Equals(normalized, args[0], StringComparison.Ordinal))
            return expression;

        return $"{functionName}({normalized})";
    }

    private static string NormalizeConditionalInsideTypedCast(string expression, string castFunctionName, string targetType)
        => NormalizeConditionalInsideTypedCastCentral(expression, castFunctionName, targetType);

    private static string NormalizeTypedConditionalBranchCentral(string expression, string targetType)
    {
        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeTypedConditionalBranchCentral));
        var trimmed = expression.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return trimmed;

        trimmed = StripAccidentalScalarWrapperForExpectedTypeCentral(trimmed, targetType);
        var resolvedType = ResolveExpressionTypeFromEvidence(null, trimmed);

        if (string.Equals(targetType, "Date", StringComparison.OrdinalIgnoreCase))
        {
            if (IsNullCallExpression(trimmed))
                return CoerceExpressionForReturnTypeWithTypeEngine("u.Null()", "Date");
            if (resolvedType.IsResolved)
                return NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(trimmed, "Date");
            trimmed = StripExpectedAttributeCastWrappers(StripExpectedAttributeCastWrappers(trimmed, "FIELD_BLOB"), "FIELD_ALPHA");
            return NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(
                StripExpectedAttributeCastWrappers(trimmed, "FIELD_DATE"),
                "Date");
        }

        if (string.Equals(targetType, "Time", StringComparison.OrdinalIgnoreCase))
        {
            if (IsNullCallExpression(trimmed))
                return CoerceExpressionForReturnTypeWithTypeEngine("u.Null()", "Time");
            if (resolvedType.IsResolved)
                return NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(trimmed, "Time");
            trimmed = StripExpectedAttributeCastWrappers(StripExpectedAttributeCastWrappers(trimmed, "FIELD_BLOB"), "FIELD_ALPHA");
            return NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(
                StripExpectedAttributeCastWrappers(trimmed, "FIELD_TIME"),
                "Time");
        }

        return trimmed;
    }

    private static string EnsureTemporalCallArgumentCentral(
        string expression,
        string expectedAttrObj,
        string explicitNullCastExpression,
        string intrinsicFunctionName)
    {
        var normalized = StripRedundantOuterParentheses(UnwrapAccidentalValueLambdaCentral(expression).Trim());
        if (IsNullCallExpression(normalized))
            return explicitNullCastExpression;

        var topLevelCall = TryGetTopLevelFunctionName(normalized);
        if (IsTopLevelCall(topLevelCall, $"u.{intrinsicFunctionName}") || IsTopLevelCall(topLevelCall, intrinsicFunctionName))
            return $"u.CastTo{intrinsicFunctionName}({normalized})";

        return CoerceScalarCallArgumentWithTypeEngineCentral(normalized, expectedAttrObj);
    }

    private static string EnsureDateCallArgumentCentral(string expression)
    {
        return EnsureTemporalCallArgumentCentral(
            expression,
            "FIELD_DATE",
            "u.CastToDate(u.Null())",
            "Date");
    }

    private static string EnsureTimeCallArgumentCentral(string expression)
    {
        return EnsureTemporalCallArgumentCentral(
            expression,
            "FIELD_TIME",
            "u.CastToTime(u.Null())",
            "Time");
    }

    private static string EnsureNumberCallArgumentCentral(string expression)
    {
        var normalized = StripRedundantOuterParentheses(UnwrapAccidentalValueLambdaCentral(expression).Trim());
        if (string.IsNullOrWhiteSpace(normalized))
            return normalized;

        return StripRedundantOuterParentheses(normalized.Trim());
    }

    private static string EnsureTextCallArgumentCentral(string expression)
    {
        var normalized = NormalizeTextSinkArgumentCentral(UnwrapAccidentalValueLambdaCentral(expression));
        if (string.IsNullOrWhiteSpace(normalized))
            return normalized;

        if (IsNullCallExpression(normalized))
            return "u.CastToText(u.Null())";

        var topLevelCall = TryGetTopLevelFunctionName(normalized);
        if (IsKnownTextProducingFunction(topLevelCall) ||
            IsSharedValGetExpression(topLevelCall) ||
            IsWholeStringLiteralExpression(normalized))
            return normalized;

        return CoerceScalarCallArgumentWithTypeEngineCentral(normalized, "FIELD_ALPHA");
    }

    private static string CoerceScalarCallArgumentWithTypeEngineCentral(string expression, string expectedAttrObj)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        return CoerceExpressionWithTypeEngine(
            expression,
            null,
            ExpectedTypeForAttrObj(expectedAttrObj),
            alwaysCoerceWholeExpression: true);
    }

    private static string EnsureBoolCallArgumentCentral(string expression)
    {
        var normalized = UnwrapAccidentalValueLambdaCentral(expression).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return normalized;

        return CoerceScalarCallArgumentWithTypeEngineCentral(normalized, "FIELD_LOGICAL");
    }

    private static string EnsureScalarAttrObjCallArgumentCentral(string expression, string? attrObj)
    {
        var normalizedAttrObj = NormalizeAttrObjKind(attrObj);
        if (string.Equals(normalizedAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return NormalizeByteArrayExpectedExpression(expression);

        var scalarReturnType = MapAttrObjToReturnType(normalizedAttrObj);
        return string.IsNullOrWhiteSpace(scalarReturnType)
            ? expression
            : EmitScalarArgumentFromEvidence(expression, scalarReturnType);
    }

    private static string EnsureKnownFunctionScalarArgumentForReturnTypeCentral(string expression, string? valueReturnType, TaskSemantic? task)
    {
        var normalizedValueReturnType = GetValueReturnType(valueReturnType);
        return normalizedValueReturnType switch
        {
            "Number" => task is not null
                ? MaterializeKnownFunctionArgumentForReturnType(
                    NormalizeNumericFormattingArgumentCentral(
                        StripExpectedAttributeCastWrappers(
                            UnwrapExplicitScalarCastLayers(expression),
                            "FIELD_NUMERIC"),
                        task),
                    "Number",
                    task)
                : EmitScalarArgumentFromEvidence(
                    StripExpectedAttributeCastWrappers(
                        UnwrapExplicitScalarCastLayers(expression),
                        "FIELD_NUMERIC"),
                    "Number"),
            "Date" => EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    UnwrapExplicitScalarCastLayers(expression),
                    "FIELD_DATE"),
                "Date"),
            "Time" => EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    UnwrapExplicitScalarCastLayers(expression),
                    "FIELD_TIME"),
                "Time"),
            "Text" => task is not null
                ? MaterializeKnownFunctionArgumentForReturnType(expression, "Text", task)
                : expression,
            "Bool" => task is not null
                ? MaterializeKnownFunctionArgumentForReturnType(expression, "Bool", task)
                : expression,
            _ => expression
        };
    }

    private static string MaterializeKnownFunctionArgumentForReturnType(string expression, string expectedReturnType, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(expectedReturnType))
            return expression;

        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (!IsSimpleIdentifierPath(trimmed))
            return expression;

        var expectedAttrObj = MapReturnTypeToAttrObj(expectedReturnType);
        if (string.IsNullOrWhiteSpace(expectedAttrObj))
            return expression;

        var sourceReturnType = "";
        if (TryResolveSimpleExpressionValueInfo(task, trimmed, out var sourceInfo))
        {
            if (sourceInfo.IsArray || sourceInfo.IsBlob || sourceInfo.IsDotNet)
                return expression;

            var sourceAttrObj = ResolveEffectiveTargetAttrObj(
                NormalizeAttrObjKind(sourceInfo.AttrObj),
                NormalizeAttrObjKind(sourceInfo.ModelAttrObj));
            if (string.IsNullOrWhiteSpace(sourceAttrObj))
            {
                if (sourceInfo.IsBoolean)
                    sourceAttrObj = "FIELD_BOOLEAN";
                else if (sourceInfo.IsNumeric)
                    sourceAttrObj = "FIELD_NUMERIC";
            }

            sourceReturnType = MapAttrObjToReturnType(sourceAttrObj);
        }

        if (string.IsNullOrWhiteSpace(sourceReturnType))
            sourceReturnType = GetValueReturnType(NormalizeReturnTypeToken(ResolveExpressionReturnType(null, trimmed, task)));

        if (string.IsNullOrWhiteSpace(sourceReturnType) ||
            string.Equals(sourceReturnType, "object", StringComparison.Ordinal))
            return expression;

        var sourceType = XpaTypeEngine.MapExpectedToXpaType(sourceReturnType);
        var expectedType = XpaTypeEngine.MapExpectedToXpaType(expectedReturnType);
        if (sourceType == XpaType.Unknown || expectedType == XpaType.Unknown)
            return expression;

        if (sourceType != expectedType && XpaTypeEngine.CanCoerce(sourceType, expectedType))
            return XpaTypeEngine.Coerce(trimmed, sourceType, expectedType);

        if (sourceType == expectedType)
            return ApplyAttributeCastCentral(trimmed, expectedAttrObj);

        return CollapseRedundantScalarCastWrappersDeep(expression);
    }

    private static string StripAccidentalScalarWrapperForExpectedTypeCentral(string expression, string targetType)
    {
        var current = expression?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(current))
            return current;

        while (TryParseFunctionCall(current, out var functionName, out var args) && args.Count == 1)
        {
            var normalizedTargetType = targetType?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(normalizedTargetType))
                break;

            var unwrap =
                string.Equals(functionName, "u.CastToText", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(functionName, "u.CastToBool", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(functionName, "u.CastToNumber", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(functionName, "u.CastToByteArray", StringComparison.OrdinalIgnoreCase);

            if (!unwrap)
                break;

            var inner = args[0].Trim();
            if (string.IsNullOrWhiteSpace(inner))
                break;

            if (string.Equals(normalizedTargetType, "Number", StringComparison.OrdinalIgnoreCase) &&
                TryGetWholeCSharpStringLiteral(inner, out _))
                break;

            current = inner;
        }

        return current;
    }

    private static string UnwrapAccidentalValueLambdaCentral(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = expression.Trim();
        if (!trimmed.StartsWith("() =>", StringComparison.Ordinal))
            return expression;

        return trimmed["() =>".Length..].Trim();
    }

    private static string NormalizeExpressionForDeclaredReturnTypeCentral(string code, string returnType, string? attr)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(returnType))
            return code;

        var normalizedDeclaredReturnType = NormalizeReturnTypeToken(returnType);
        var isClrObjectReturnType =
            (normalizedDeclaredReturnType.Contains('.', StringComparison.Ordinal) &&
             !string.Equals(normalizedDeclaredReturnType, "byte[]", StringComparison.Ordinal) &&
             !string.Equals(normalizedDeclaredReturnType, "byte[][]", StringComparison.Ordinal)) ||
            string.Equals(normalizedDeclaredReturnType, "System.String[]", StringComparison.Ordinal);

        if (string.Equals(returnType, "System.Uri", StringComparison.Ordinal) &&
            TryParseFunctionCall(code.Trim(), out var wrappedFunctionName, out var wrappedArgs) &&
            wrappedArgs.Count == 1 &&
            IsTopLevelCall(wrappedFunctionName, "u.CastToByteArray"))
        {
            var inner = wrappedArgs[0].Trim();
            if (inner.StartsWith("new System.Uri(", StringComparison.Ordinal) &&
                inner.EndsWith(")", StringComparison.Ordinal))
                return inner;
        }

        if (returnType.Contains('.', StringComparison.Ordinal) &&
            !returnType.EndsWith("[]", StringComparison.Ordinal))
            return code;

        if (TryParseFunctionCall(code.Trim(), out var explicitCastName, out var explicitCastArgs) &&
            explicitCastArgs.Count == 1 &&
            IsExplicitCastFunctionForReturnType(explicitCastName, returnType))
        {
            if (string.Equals(returnType, "Text", StringComparison.Ordinal) &&
                IsTopLevelCall(explicitCastName, "u.ByteArrayToText"))
            {
                var explicitInner = explicitCastArgs[0].Trim();
                if (IsKnownTextualExpressionForByteArrayToTextUnwrap(explicitInner))
                    return NormalizeExpressionForDeclaredReturnTypeCentral(explicitInner, returnType, attr);
            }

            var normalizedInner = NormalizeExpressionForDeclaredReturnTypeCentral(explicitCastArgs[0].Trim(), returnType, attr);
            var rebuilt = $"{explicitCastName}({normalizedInner})";
            if (string.Equals(returnType, "Text", StringComparison.Ordinal) &&
                TryUnwrapNestedCastToTextExpressionCentral(rebuilt, out var explicitTextInner))
                rebuilt = $"u.CastToText({explicitTextInner})";

            return CollapseRedundantScalarCastWrappers(rebuilt);
        }

        if (isClrObjectReturnType &&
            TryParseFunctionCall(code.Trim(), out var explicitBlobCastName, out var explicitBlobCastArgs) &&
            explicitBlobCastArgs.Count == 1 &&
            IsTopLevelCall(explicitBlobCastName, "u.CastToByteArray"))
        {
            var inner = explicitBlobCastArgs[0].Trim();
            var innerTopLevelCall = TryGetTopLevelFunctionName(inner);
            if (inner.StartsWith("new ", StringComparison.Ordinal) ||
                IsTopLevelCall(innerTopLevelCall, "u.DataViewToDNDataTable"))
                return NormalizeExpressionForDeclaredReturnTypeCentral(inner, returnType, attr);
        }

        if (ExpressionAttributeMatchesReturnType(attr, returnType) &&
            !RequiresExplicitNormalizationForDeclaredReturnType(code, returnType))
            return code;

        code = NormalizeConditionalBranchesForDeclaredReturnType(code, returnType, attr);

        if (string.Equals(returnType, "Text", StringComparison.Ordinal))
        {
            if (TryNormalizeWrappedAlphaCaseExpressionCentral(code, out var normalizedAlphaWrapped))
                code = normalizedAlphaWrapped;
            if (TryNormalizeWrappedVariantCaseExpressionCentral(code, out var normalizedVariantWrapped))
                code = normalizedVariantWrapped;
            code = NormalizeVariantCaseExpression(code);
            code = NormalizeAlphaCaseExpression(code);
        }

        var normalized = returnType switch
        {
            "Number" => EmitScalarArgumentFromEvidence(code, "Number"),
            "Date" => EmitScalarArgumentFromEvidence(code, "Date"),
            "Time" => EmitScalarArgumentFromEvidence(code, "Time"),
            "Bool" => EmitScalarArgumentFromEvidence(code, "Bool"),
            "Text" => EmitScalarArgumentFromEvidence(code, "Text"),
            "byte[]" => CoerceExpressionWithTypeEngine(code, null, ExpectedTypeForReturnType("byte[]"), alwaysCoerceWholeExpression: true),
            "Text[]" => NormalizeArrayExpressionForDeclaredReturnType(code, "Text"),
            "Number[]" => NormalizeArrayExpressionForDeclaredReturnType(code, "Number"),
            "Date[]" => NormalizeArrayExpressionForDeclaredReturnType(code, "Date"),
            "Time[]" => NormalizeArrayExpressionForDeclaredReturnType(code, "Time"),
            "Bool[]" => NormalizeArrayExpressionForDeclaredReturnType(code, "Bool"),
            "byte[][]" => NormalizeArrayExpressionForDeclaredReturnType(code, "byte[]"),
            _ => code
        };

        var final = returnType is "Number" or "Date" or "Time" or "Bool" or "Text" or "byte[]"
            ? ApplyRequiredExplicitSurfaceCastForExpectedType(normalized, ExpectedTypeForReturnType(returnType))
            : normalized;

        if (string.Equals(returnType, "Text", StringComparison.Ordinal) &&
            TryUnwrapNestedCastToTextExpressionCentral(final, out var textInner))
            final = $"u.CastToText({textInner})";

        return CollapseRedundantScalarCastWrappers(final);
    }

    private static bool IsKnownTextualExpressionForByteArrayToTextUnwrap(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (TryGetWholeCSharpStringLiteral(trimmed, out _))
            return true;

        if (trimmed.EndsWith(".Message", StringComparison.Ordinal) ||
            trimmed.EndsWith(".ToString()", StringComparison.Ordinal))
            return true;

        var topLevelCall = TryGetTopLevelFunctionName(trimmed);
        return IsTopLevelCall(topLevelCall, "u.CastToText") ||
               IsTopLevelCall(topLevelCall, "u.Trim") ||
               IsTopLevelCall(topLevelCall, "u.Str") ||
               IsTopLevelCall(topLevelCall, "u.DStr") ||
               IsTopLevelCall(topLevelCall, "u.TStr") ||
               IsTopLevelCall(topLevelCall, "u.MTStr");
    }

    private static bool IsExplicitCastFunctionForReturnType(string functionName, string returnType)
    {
        return returnType switch
        {
            "Text" => IsTopLevelCall(functionName, "u.CastToText") ||
                      IsTopLevelCall(functionName, "u.ByteArrayToText"),
            "Number" => IsTopLevelCall(functionName, "u.CastToNumber"),
            "Date" => IsTopLevelCall(functionName, "u.CastToDate"),
            "Time" => IsTopLevelCall(functionName, "u.CastToTime") || IsTopLevelCall(functionName, "UserMethods.ToTime"),
            "Bool" => IsTopLevelCall(functionName, "u.CastToBool"),
            _ => false
        };
    }

    private static bool ExpressionAttributeMatchesReturnType(string? attr, string returnType)
    {
        return (attr ?? "").ToUpperInvariant() switch
        {
            "N" => string.Equals(returnType, "Number", StringComparison.Ordinal),
            "D" => string.Equals(returnType, "Date", StringComparison.Ordinal),
            "T" => string.Equals(returnType, "Time", StringComparison.Ordinal),
            "B" => string.Equals(returnType, "Bool", StringComparison.Ordinal),
            "A" or "U" => string.Equals(returnType, "Text", StringComparison.Ordinal),
            "O" => string.Equals(returnType, "byte[]", StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool RequiresExplicitNormalizationForDeclaredReturnType(string code, string returnType)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var trimmed = StripRedundantOuterParentheses(code.Trim());
        if (!TryParseFunctionCall(trimmed, out var functionName, out _))
            return false;

        if (string.Equals(returnType, "byte[]", StringComparison.Ordinal) ||
            string.Equals(returnType, "byte[][]", StringComparison.Ordinal))
            return false;

        return IsTopLevelCall(functionName, "u.FileInfo") ||
               IsTopLevelCall(functionName, "u.ClientFileInfo") ||
               IsRuntimeUntypedScalarExpression(functionName) ||
               IsObjectProducingExpression(functionName);
    }

    private static string ResolveExpressionReturnType(string? attr, string? code = null, TaskSemantic? task = null)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            var trimmed = code.Trim();
            if (string.Equals(trimmed, "__TARGET__", StringComparison.Ordinal))
                return attr switch
                {
                    "N" => "Number",
                    "D" => "Date",
                    "T" => "Time",
                    "B" => "Bool",
                    "O" => "byte[]",
                    _ => "Text"
                };

            if (task is not null &&
                TryResolveRegisteredTypedExpressionInfo(task, trimmed, out var registeredTypeInfo) &&
                !string.IsNullOrWhiteSpace(registeredTypeInfo.ReturnType))
                return registeredTypeInfo.ReturnType;

            if (TryParseFunctionCall(trimmed, out var wrappedFunctionName, out var wrappedArgs) &&
                wrappedArgs.Count == 1 &&
                IsTopLevelCall(wrappedFunctionName, "u.CastToByteArray"))
            {
                var inner = wrappedArgs[0].Trim();
                if (inner.StartsWith("new System.Uri(", StringComparison.Ordinal) &&
                    inner.EndsWith(")", StringComparison.Ordinal))
                    return "System.Uri";
            }

            if (trimmed.EndsWith(".FullDbName", StringComparison.Ordinal) ||
                trimmed.EndsWith(".DbName", StringComparison.Ordinal))
                return "Text";

            if (task is not null)
            {
                if (IsSimpleIdentifierPath(trimmed))
                {
                    var directTarget = ResolveTargetValueInfo(task, null, trimmed);
                    if (directTarget.Resource is not null)
                    {
                        if (IsDotNetTaskResource(directTarget.Resource))
                        {
                            var objectReturnType = NormalizeDotNetObjectType(directTarget.Resource.ObjectType!);
                            if (!string.IsNullOrWhiteSpace(objectReturnType))
                                return objectReturnType;
                        }

                        var columnType = ResolveTaskResourceColumnType(directTarget.Resource, _allFieldModels, task);
                        var columnReturnType = columnType switch
                        {
                            "TextColumn" => "Text",
                            "NumberColumn" => "Number",
                            "DateColumn" => "Date",
                            "TimeColumn" => "Time",
                            "BoolColumn" => "Bool",
                            "ByteArrayColumn" => "byte[]",
                            "ArrayColumn<Text>" => "Text[]",
                            "ArrayColumn<Number>" => "Number[]",
                            "ArrayColumn<Date>" => "Date[]",
                            "ArrayColumn<Time>" => "Time[]",
                            "ArrayColumn<Bool>" => "Bool[]",
                            "ArrayColumn<byte[]>" => "byte[][]",
                            _ => ""
                        };
                        if (!string.IsNullOrWhiteSpace(columnReturnType))
                            return columnReturnType;
                    }

                    var directReturnType = !string.IsNullOrWhiteSpace(directTarget.AttrObj)
                        ? MapAttrObjToReturnType(directTarget.AttrObj)
                        : MapAttrObjToReturnType(directTarget.ModelAttrObj);
                    if (!string.IsNullOrWhiteSpace(directReturnType))
                        return directReturnType;
                    if (directTarget.IsBlob && !directTarget.IsArray)
                        return "byte[]";
                    if (directTarget.IsArray && directTarget.Resource is not null)
                    {
                        var itemType = ResolveArrayColumnItemType(directTarget.Resource, _allFieldModels, task);
                        return itemType switch
                        {
                            "Text" => "Text[]",
                            "Number" => "Number[]",
                            "Date" => "Date[]",
                            "Time" => "Time[]",
                            "Bool" => "Bool[]",
                            "byte[]" => "byte[][]",
                            _ => "Text[]"
                        };
                    }
                }

                return "";
            }

            if (task is not null)
            {
                var resource = ResolveResourceByTargetPath(task, trimmed, _allTasks ?? Array.Empty<TaskSemantic>());
                if (resource is not null && IsDotNetTaskResource(resource))
                {
                    var objectType = NormalizeDotNetObjectType(resource.ObjectType!);
                    if (!string.IsNullOrWhiteSpace(objectType))
                        return objectType;
                }
            }

            if (TryResolveClrExpressionReturnType(trimmed, out var clrReturnType))
                return clrReturnType;

            if (TryResolveKnownStatementExpressionReturnType(trimmed, out var statementReturnType))
                return statementReturnType;

            return "";
        }

        if (string.IsNullOrWhiteSpace(code))
            return attr switch
            {
                "N" => "Number",
                "D" => "Date",
                "T" => "Time",
                "B" => "Bool",
                "O" => "byte[]",
                _ => "Text"
            };

        return attr switch
        {
            "N" => "Number",
            "D" => "Date",
            "T" => "Time",
            "B" => "Bool",
            "O" => "byte[]",
            _ => "Text"
        };
    }

    private static string NormalizeExpressionForDeclaredReturnType(string code, string returnType, string? attr)
        => NormalizeExpressionForDeclaredReturnTypeCentral(code, returnType, attr);

    private static string NormalizeConditionalBranchesForDeclaredReturnType(string code, string returnType, string? attr)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        var attrObj = !string.IsNullOrWhiteSpace(attr)
            ? NormalizeAttrObjKind(attr) switch
            {
                "N" => "FIELD_NUMERIC",
                "D" => "FIELD_DATE",
                "T" => "FIELD_TIME",
                "B" => "FIELD_LOGICAL",
                "A" or "U" => "FIELD_ALPHA",
                "O" => "FIELD_BLOB",
                _ => MapReturnTypeToAttrObj(returnType)
            }
            : MapReturnTypeToAttrObj(returnType);
        var useCentralConditionalNormalization =
            !string.IsNullOrWhiteSpace(attrObj) &&
            !string.Equals(returnType, "byte[]", StringComparison.Ordinal) &&
            !returnType.EndsWith("[]", StringComparison.Ordinal);

        var trimmed = code.Trim();
        if (!TryParseFunctionCall(trimmed, out var functionName, out var args) ||
            !IsTopLevelCall(functionName, "u.If") ||
            args.Count != 3)
            return code;

        var whenTrue = useCentralConditionalNormalization
            ? NormalizeConditionalBranchForContext(args[1].Trim(), attrObj!, returnType)
            : NormalizeExpressionForDeclaredReturnType(args[1].Trim(), returnType, attr);
        var whenFalse = useCentralConditionalNormalization
            ? NormalizeConditionalBranchForContext(args[2].Trim(), attrObj!, returnType)
            : NormalizeExpressionForDeclaredReturnType(args[2].Trim(), returnType, attr);
        return TrackLegacyExpressionTreatmentIfChanged(
            "NormalizeType",
            nameof(NormalizeConditionalBranchesForDeclaredReturnType),
            code,
            $"u.If({args[0].Trim()}, {whenTrue}, {whenFalse})");
    }

    private static string NormalizeArrayExpressionForDeclaredReturnType(string code, string itemType)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        var trimmed = code.Trim();
        if (IsNullCallExpression(trimmed))
            return "null";

        if (TryGetWholeCSharpStringLiteral(trimmed, out var literalValue) &&
            string.IsNullOrEmpty(literalValue))
            return "null";

        if (TryParseFunctionCall(trimmed, out var wrappedFunctionName, out var wrappedArgs) &&
            wrappedArgs.Count == 1 &&
            IsTopLevelCall(wrappedFunctionName, "u.CastToByteArray"))
        {
            var inner = wrappedArgs[0].Trim();
            if (TryGetWholeCSharpStringLiteral(inner, out var wrappedLiteralValue) &&
                string.IsNullOrEmpty(wrappedLiteralValue))
                return "null";
            if (IsNullCallExpression(inner))
                return "null";
            if (string.Equals(itemType, "Text", StringComparison.Ordinal) &&
                IsTextArrayProducingExpression(TryGetTopLevelFunctionName(inner)))
                return inner;
        }

        if (string.Equals(itemType, "Text", StringComparison.Ordinal) &&
            TryParseFunctionCall(trimmed, out var functionName, out var args))
        {
            if (IsTopLevelCall(functionName, "u.SharedValGet"))
                return $"u.SharedValGetTextArray({string.Join(", ", args)})";

            if (IsTopLevelCall(functionName, "u.GetTextParam"))
                return $"u.CastToTextArray(u.GetParam({string.Join(", ", args)}))";

            if (IsTextArrayProducingExpression(functionName))
                return trimmed;
        }

        return code;
    }

    private static bool TryResolveClrExpressionReturnType(string code, out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var trimmed = code.Trim();
        if (TryGetConstructedTypeName(trimmed, out var constructedType))
        {
            returnType = constructedType;
            return true;
        }

        var topLevelCall = TryGetTopLevelFunctionName(trimmed);
        if (IsColorFactoryExpression(topLevelCall))
        {
            returnType = "System.Drawing.Color";
            return true;
        }

        return false;
    }

    private static bool TryGetConstructedTypeName(string code, out string typeName)
    {
        typeName = "";
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var trimmed = code.Trim();
        const string prefix = "new ";
        if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var i = prefix.Length;
        var start = i;
        while (i < trimmed.Length && (char.IsLetterOrDigit(trimmed[i]) || trimmed[i] == '_' || trimmed[i] == '.'))
            i++;

        if (i <= start)
            return false;

        var candidate = trimmed[start..i];
        if (i >= trimmed.Length)
            return false;

        if (trimmed[i] == '[')
        {
            typeName = candidate + "[]";
            return true;
        }

        if (trimmed[i] == '(')
        {
            typeName = candidate;
            return true;
        }

        return false;
    }

    private static string EnsureTextIoControllerBinding(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = expression.Trim();
        if (trimmed.StartsWith("_controller.", StringComparison.Ordinal) ||
            trimmed.StartsWith("XPARuntimeCore.", StringComparison.Ordinal) ||
            trimmed.StartsWith("ENV.", StringComparison.Ordinal) ||
            trimmed.StartsWith("System.", StringComparison.Ordinal) ||
            trimmed.StartsWith("global::", StringComparison.Ordinal) ||
            trimmed.StartsWith("this.", StringComparison.Ordinal))
            return expression;

        if (trimmed.StartsWith("_parent.", StringComparison.Ordinal))
            return "_controller." + trimmed;

        if (Regex.IsMatch(trimmed, @"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+$"))
            return "_controller." + trimmed;

        return expression;
    }

    private static string BuildPresenceConditionCentral(TaskSemantic task, string expression, string? attrObj = null, string? returnType = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return "";

        var scalarType = GetValueReturnType(returnType);
        if (string.IsNullOrWhiteSpace(scalarType))
            scalarType = MapAttrObjToReturnType(NormalizeAttrObjKind(attrObj));

        if (string.IsNullOrWhiteSpace(scalarType))
        {
            var inferred = ResolveExpectedTypeFromExpressionEvidence(task, expression.Trim());
            scalarType = GetValueReturnType(inferred.ReturnType);
            if (string.IsNullOrWhiteSpace(scalarType))
                scalarType = MapAttrObjToReturnType(NormalizeAttrObjKind(inferred.AttrObj));
        }

        if (string.IsNullOrWhiteSpace(scalarType))
            return "";

        return BuildBooleanTruthinessExpressionCentral(task, expression.Trim(), scalarType);
    }

    private static string NormalizeVariantGetExpressionCentral(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var trimmed = value.Trim();
        if (!TryParseFunctionCall(trimmed, out var functionName, out var args) ||
            !string.Equals(functionName, "u.VariantGet", StringComparison.OrdinalIgnoreCase))
            return value;

        if (!TryGetTrailingVariantTypeCode(args, out var typeCode))
            return value;

        var expectedReturnType = char.ToUpperInvariant(typeCode) switch
        {
            'N' => "Number",
            'D' => "Date",
            'T' => "Time",
            'A' or 'U' => "Text",
            'L' => "Bool",
            'B' => "byte[]",
            _ => ""
        };

        return string.IsNullOrWhiteSpace(expectedReturnType)
            ? value
            : CoerceExpressionWithTypeEngine(
                trimmed,
                null,
                ExpectedTypeForReturnType(expectedReturnType),
                alwaysCoerceWholeExpression: true);
    }

    private static string CoerceInvokeReturnValueCentral(
        string valueExpr,
        string? mgAttr,
        string? targetAttrObj,
        bool isNumericTarget,
        bool isBooleanTarget,
        bool isBlobTarget,
        bool preferTextTarget)
    {
        if (string.IsNullOrWhiteSpace(valueExpr))
            return valueExpr;

        var expected = mgAttr switch
        {
            "A" or "U" => ExpectedTypeForReturnType("Text"),
            "N" => ExpectedTypeForReturnType("Number"),
            "D" => ExpectedTypeForReturnType("Date"),
            "T" => ExpectedTypeForReturnType("Time"),
            "L" => ExpectedTypeForReturnType("Bool"),
            "B" => ExpectedTypeForReturnType("byte[]"),
            _ => default
        };

        if (expected.HasExpectation)
            return EmitScalarArgumentFromEvidence(valueExpr, GetValueReturnType(expected.ReturnType));

        if (!string.IsNullOrWhiteSpace(targetAttrObj))
            return EnsureScalarAttrObjCallArgumentCentral(valueExpr, targetAttrObj);

        if (isNumericTarget)
            return EmitScalarArgumentFromEvidence(valueExpr, "Number");

        if (isBooleanTarget)
            return EmitScalarArgumentFromEvidence(valueExpr, "Bool");

        if (isBlobTarget)
            return EmitScalarArgumentFromEvidence(valueExpr, "byte[]");

        if (preferTextTarget)
            return EmitScalarArgumentFromEvidence(valueExpr, "Text");

        return valueExpr;
    }

    private static string NormalizeConditionalBranchesForAttributeCentral(string expression, string? attrObj)
    {
        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(attrObj))
            return expression;

        var trimmed = expression.Trim();
        if (!TryParseFunctionCall(trimmed, out var functionName, out var args) ||
            !IsTopLevelCall(functionName, "u.If") ||
            args.Count != 3)
            return expression;

        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeConditionalBranchesForAttributeCentral));
        var whenTrue = NormalizeConditionalBranchForAttributeCentral(args[1], attrObj);
        var whenFalse = NormalizeConditionalBranchForAttributeCentral(args[2], attrObj);
        return $"u.If({args[0].Trim()}, {whenTrue}, {whenFalse})";
    }

    private static string NormalizeConditionalBranchForAttributeCentral(string expression, string? attrObj)
    {
        var trimmed = expression?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmed))
            return trimmed;

        if (TryParseFunctionCall(trimmed, out var functionName, out _) &&
            string.Equals(functionName, "u.If", StringComparison.OrdinalIgnoreCase))
        {
            TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeConditionalBranchForAttributeCentral));
            return NormalizeConditionalBranchesForAttributeCentral(trimmed, attrObj);
        }

        return StripRedundantOuterParentheses(trimmed.Trim());
    }

    private static string RewriteSharedValGetByFieldAttributeCentral(string? attrObj, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var attr = NormalizeAttrObjKind(attrObj) switch
        {
            "FIELD_ALPHA" => "A",
            "FIELD_NUMERIC" => "N",
            "FIELD_DATE" => "D",
            "FIELD_TIME" => "T",
            "FIELD_BOOLEAN" or "FIELD_LOGICAL" => "B",
            _ => ""
        };

        return string.IsNullOrWhiteSpace(attr)
            ? value
            : RewriteSharedValGetByAttribute(attr, value);
    }

    private static string RewriteSharedValGetByAttribute(string? attr, string expression)
    {
        if (string.IsNullOrWhiteSpace(attr) || string.IsNullOrWhiteSpace(expression))
            return expression;

        var topLevelCall = TryGetTopLevelFunctionName(expression.Trim());
        if (!IsSharedValGetExpression(topLevelCall))
            return expression;

        string? replacement = attr.ToUpperInvariant() switch
        {
            "A" => "u.SharedValGetText",
            "N" => "u.SharedValGetNumber",
            "D" => "u.SharedValGetDate",
            "T" => "u.SharedValGetTime",
            "B" => "u.SharedValGetBool",
            _ => null
        };

        if (string.IsNullOrWhiteSpace(replacement))
            return expression;

        var rewritten = RewriteFunctionCalls(expression, "SharedValGet", args => $"{replacement}({string.Join(", ", args)})");
        rewritten = RewriteFunctionCalls(rewritten, "u.SharedValGet", args => $"{replacement}({string.Join(", ", args)})");
        return rewritten;
    }

    private static string RewriteSharedValGetComparisonOperands(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression) ||
            expression.IndexOf("SharedValGet", StringComparison.OrdinalIgnoreCase) < 0)
            return expression;

        expression = Regex.Replace(
            expression,
            @"(?<left>u\.SharedValGet\s*\([^)]*\)|SharedValGet\s*\([^)]*\))\s*(?<op><=|>=|<|>)\s*(?<right>[^&|]+)",
            RewriteSharedValGetComparisonMatch,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        expression = Regex.Replace(
            expression,
            @"(?<left>[^&|]+)\s*(?<op><=|>=|<|>)\s*(?<right>u\.SharedValGet\s*\([^)]*\)|SharedValGet\s*\([^)]*\))",
            RewriteSharedValGetComparisonMatch,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        return expression;
    }

    private static string RewriteSharedValGetComparisonMatch(Match match)
    {
        var left = match.Groups["left"].Value.Trim();
        var right = match.Groups["right"].Value.Trim();
        var op = match.Groups["op"].Value;

        if (IsSharedValGetExpression(TryGetTopLevelFunctionName(left)))
            left = RewriteSharedValGetOperandByComparisonContext(left, right);

        if (IsSharedValGetExpression(TryGetTopLevelFunctionName(right)))
            right = RewriteSharedValGetOperandByComparisonContext(right, left);

        return $"{left} {op} {right}";
    }

    private static string RewriteSharedValGetOperandByComparisonContext(string operand, string counterpart)
    {
        var trimmedCounterpart = counterpart.Trim();
        if (TryResolveLiteralExpectedType(trimmedCounterpart, out var literalExpected))
            return RewriteSharedValGetOperandByExpectedType(operand, literalExpected);

        if (IsTimeLikeExpression(trimmedCounterpart))
            return RewriteSharedValGetOperandByExpectedType(operand, ExpectedTypeForReturnType("Time"));

        if (IsDateLikeExpression(trimmedCounterpart))
            return RewriteSharedValGetOperandByExpectedType(operand, ExpectedTypeForReturnType("Date"));

        var topLevelCall = TryGetTopLevelFunctionName(trimmedCounterpart);
        if (IsTopLevelCall(topLevelCall, "u.CastToNumber") ||
            IsTopLevelCall(topLevelCall, "u.ToNumber") ||
            IsTopLevelCall(topLevelCall, "u.SharedValGetNumber"))
            return RewriteSharedValGetOperandByExpectedType(operand, ExpectedTypeForReturnType("Number"));

        if (IsTopLevelCall(topLevelCall, "u.CastToText") ||
            IsTopLevelCall(topLevelCall, "u.Trim") ||
            IsTopLevelCall(topLevelCall, "u.LTrim") ||
            IsTopLevelCall(topLevelCall, "u.RTrim") ||
            IsTopLevelCall(topLevelCall, "u.Upper") ||
            IsTopLevelCall(topLevelCall, "u.Lower") ||
            IsTopLevelCall(topLevelCall, "u.SharedValGetText"))
            return RewriteSharedValGetOperandByExpectedType(operand, ExpectedTypeForReturnType("Text"));

        if (IsTopLevelCall(topLevelCall, "u.CastToBool") ||
            IsTopLevelCall(topLevelCall, "u.GetBoolParam") ||
            IsTopLevelCall(topLevelCall, "u.SharedValGetBool"))
            return RewriteSharedValGetOperandByExpectedType(operand, ExpectedTypeForReturnType("Bool"));

        return operand;
    }

    private static string RewriteSharedValGetOperandByExpectedType(string operand, ExpectedTypeContext expected)
    {
        string? replacement = NormalizeReturnTypeToken(GetValueReturnType(expected.ReturnType)) switch
        {
            "Number" => "u.SharedValGetNumber",
            "Date" => "u.SharedValGetDate",
            "Time" => "u.SharedValGetTime",
            "Bool" => "u.SharedValGetBool",
            "Text" => "u.SharedValGetText",
            _ => null
        };

        if (string.IsNullOrWhiteSpace(replacement))
        {
            replacement = NormalizeAttrObjKind(expected.AttrObj) switch
            {
                "FIELD_NUMERIC" => "u.SharedValGetNumber",
                "FIELD_DATE" => "u.SharedValGetDate",
                "FIELD_TIME" => "u.SharedValGetTime",
                "FIELD_BOOLEAN" or "FIELD_LOGICAL" => "u.SharedValGetBool",
                "FIELD_ALPHA" or "FIELD_TEXT" or "FIELD_UNICODE" => "u.SharedValGetText",
                _ => null
            };
        }

        if (string.IsNullOrWhiteSpace(replacement))
            return operand;

        var rewritten = RewriteFunctionCalls(operand, "SharedValGet", args => $"{replacement}({string.Join(", ", args)})");
        rewritten = RewriteFunctionCalls(rewritten, "u.SharedValGet", args => $"{replacement}({string.Join(", ", args)})");
        return rewritten;
    }

    private static bool IsSimpleMemberPath(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var parts = expression.Split('.');
        return parts.Length == 2 && parts.All(IsSimpleIdentifier);
    }

    private static bool IsSimpleIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (!(char.IsLetter(value[0]) || value[0] == '_'))
            return false;
        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
                return false;
        }
        return true;
    }

    internal readonly record struct TargetValueInfo(
        TaskResourceColumnDef? Resource,
        string? ModelAttrObj,
        string AttrObj,
        string TargetMember,
        bool IsDotNet,
        bool IsArray,
        bool IsBlob,
        bool IsNumeric,
        bool IsBoolean);

    private static TargetValueInfo RecalibrateTargetInfoFromResolvedColumnType(TargetValueInfo targetInfo, string? resolvedColumnType)
    {
        var normalizedColumnType = NormalizeReturnTypeToken(resolvedColumnType);
        var normalizedValueType = GetValueReturnType(normalizedColumnType);
        if (!string.IsNullOrWhiteSpace(normalizedColumnType) &&
            ((normalizedColumnType.Contains('.', StringComparison.Ordinal) &&
              !normalizedColumnType.StartsWith("Types.", StringComparison.Ordinal) &&
              !string.Equals(normalizedValueType, "byte[]", StringComparison.Ordinal) &&
              !string.Equals(normalizedValueType, "byte[][]", StringComparison.Ordinal)) ||
             string.Equals(normalizedColumnType, "System.String[]", StringComparison.Ordinal)))
        {
            return targetInfo with
            {
                AttrObj = "",
                IsBlob = false,
                IsArray = false,
                IsDotNet = true,
                IsNumeric = false,
                IsBoolean = false
            };
        }

        var attrObj = ResolveAttrObjForPrimitiveColumnType(resolvedColumnType);
        if (string.IsNullOrWhiteSpace(attrObj))
            return targetInfo;

        return RecalibrateTargetInfoFromAttrObj(targetInfo, attrObj);
    }

    private static TargetValueInfo RecalibrateTargetInfoFromAttrObj(TargetValueInfo targetInfo, string? attrObj)
    {
        attrObj = NormalizeAttrObjKind(attrObj);
        if (string.IsNullOrWhiteSpace(attrObj))
            return targetInfo;

        return targetInfo with
        {
            AttrObj = attrObj,
            IsBlob = string.Equals(attrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase),
            IsNumeric = IsNumericAttrObj(attrObj),
            IsBoolean =
                string.Equals(attrObj, "FIELD_BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attrObj, "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase),
            IsDotNet = false
        };
    }

    private static string ResolveAttrObjForPrimitiveColumnType(string? resolvedColumnType)
    {
        if (string.IsNullOrWhiteSpace(resolvedColumnType))
            return "";

        return resolvedColumnType.Trim() switch
        {
            "TextColumn" => "FIELD_ALPHA",
            "NumberColumn" => "FIELD_NUMERIC",
            "DateColumn" => "FIELD_DATE",
            "TimeColumn" => "FIELD_TIME",
            "BoolColumn" => "FIELD_BOOLEAN",
            "ByteArrayColumn" => "FIELD_BLOB",
            _ => ""
        };
    }

    private static string NormalizeAttrObjKind(string? attrObj)
    {
        if (string.IsNullOrWhiteSpace(attrObj))
            return "";

        return attrObj.Trim().ToUpperInvariant() switch
        {
            "FIELD_ALPHA" or "FIELD_NUMERIC" or "FIELD_DATE" or "FIELD_TIME" or "FIELD_BOOLEAN" or "FIELD_LOGICAL" or "FIELD_BLOB" => attrObj.Trim(),
            "FIELD_UNICODE" => "FIELD_ALPHA",
            "A" or "U" => "FIELD_ALPHA",
            "N" => "FIELD_NUMERIC",
            "D" => "FIELD_DATE",
            "T" => "FIELD_TIME",
            "L" or "B" => "FIELD_BOOLEAN",
            "O" => "FIELD_BLOB",
            _ => attrObj.Trim()
        };
    }

    private static string ResolveEffectiveTaskResourceAttrObj(TaskResourceColumnDef? resource, TaskSemantic? ownerTask = null)
    {
        if (resource is null)
            return "";

        if (IsDotNetTaskResource(resource))
            return "";

        ownerTask ??= _allTasks.FirstOrDefault(t => t.ResourcesSemantic.Ordered.Any(r => r.Id == resource.Id));
        if (ownerTask is not null &&
            string.Equals(resource.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) &&
            HasStrongTextScalarUsageEvidence(resource, ownerTask))
            return "FIELD_ALPHA";

        if (ownerTask is not null &&
            string.Equals(resource.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
            HasStrongLogicalBlobUsageEvidence(resource, ownerTask))
            return "FIELD_LOGICAL";

        if (IsBlobTaskResourceCandidate(resource))
            return "FIELD_BLOB";

        if (!string.IsNullOrWhiteSpace(resource.AttrObj))
            return NormalizeAttrObjKind(resource.AttrObj);

        if (!string.IsNullOrWhiteSpace(resource.CellModelAttrObj))
            return NormalizeAttrObjKind(resource.CellModelAttrObj);

        return "";
    }

    private static bool IsBlobTaskResourceCandidate(TaskResourceColumnDef? resource)
    {
        if (resource is null)
            return false;

        if (IsDotNetTaskResource(resource))
            return false;

        if (string.Equals(resource.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(resource.CellModelAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return true;

        return ContainsBlobTypeHint(resource.ObjectType) ||
               ContainsBlobTypeHint(resource.AttrObj) ||
               ContainsBlobTypeHint(resource.ModelRefObj);
    }

    private static bool ContainsBlobTypeHint(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               (value.IndexOf("BlobAnsi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("BlobBinary", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("FIELD_BLOB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("MOD_Blob", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("CLOB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("BLOB", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string ResolveAttrObjForColumnType(string? resolvedType, IReadOnlyList<FieldModelDef> fieldModels, TaskSemantic currentTask)
    {
        if (string.IsNullOrWhiteSpace(resolvedType))
            return "";

        var normalizedType = resolvedType.Trim();
        var directAttrObj = normalizedType switch
        {
            "TextColumn" => "FIELD_ALPHA",
            "NumberColumn" => "FIELD_NUMERIC",
            "DateColumn" => "FIELD_DATE",
            "TimeColumn" => "FIELD_TIME",
            "BoolColumn" => "FIELD_BOOLEAN",
            "ByteArrayColumn" => "FIELD_BLOB",
            _ => ""
        };
        if (!string.IsNullOrWhiteSpace(directAttrObj))
            return directAttrObj;

        var matchingFieldModel = fieldModels.FirstOrDefault(fm =>
        {
            var resolvedReference = ResolveFieldTypeReference(fm, currentTask);
            if (string.Equals(resolvedReference, normalizedType, StringComparison.Ordinal))
                return true;

            var resolvedTypeName = resolvedReference.Split('.').LastOrDefault();
            var normalizedTypeName = normalizedType.Split('.').LastOrDefault();
            return !string.IsNullOrWhiteSpace(resolvedTypeName) &&
                   !string.IsNullOrWhiteSpace(normalizedTypeName) &&
                   string.Equals(resolvedTypeName, normalizedTypeName, StringComparison.Ordinal);
        });
        if (matchingFieldModel is not null)
            return NormalizeAttrObjKind(matchingFieldModel.AttrObj);

        return "";
    }

    private static string ResolveTaskResourceColumnType(TaskResourceColumnDef c, IReadOnlyList<FieldModelDef> fieldModels, TaskSemantic currentTask)
    {
        if (IsDotNetTaskResource(c))
            return NormalizeDotNetObjectType(c.ObjectType!);
        var normalizedAttrObj = (c.AttrObj ?? "").Trim();
        var mayOverrideToIntrinsicText =
            string.IsNullOrWhiteSpace(normalizedAttrObj) ||
            string.Equals(normalizedAttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase);
        if (!IsDeclaredTaskParameterResource(c, currentTask) &&
            mayOverrideToIntrinsicText &&
            HasKeyLikeTextScalarIdentity(c, currentTask))
            return "TextColumn";
        if (!IsDeclaredTaskParameterResource(c, currentTask) &&
            mayOverrideToIntrinsicText &&
            HasIntrinsicTextScalarIdentity(c, currentTask))
            return "TextColumn";
        if (!IsDeclaredTaskParameterResource(c, currentTask) &&
            mayOverrideToIntrinsicText &&
            !string.IsNullOrWhiteSpace(c.InputRange) &&
            BuildTaskResourceInferenceNames(c, currentTask).Any(name =>
                Regex.IsMatch(NormalizeSemanticNameForInference(name), @"(?:^|_)(alpha|alfa)(?:_|$)", RegexOptions.IgnoreCase)))
            return "TextColumn";
        if (string.IsNullOrWhiteSpace(normalizedAttrObj) &&
            !IsDeclaredTaskParameterResource(c, currentTask) &&
            HasStrongNumericScalarUsageEvidence(c, currentTask))
            return "NumberColumn";
        if (string.Equals(normalizedAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
            HasStrongLogicalBlobUsageEvidence(c, currentTask))
            return "BoolColumn";
        var collectionType = ResolveArrayCollectionTypeReference(c, fieldModels, currentTask);
        var hasNameOnlyStructuredBlobHint =
            !IsDeclaredTaskParameterResource(c, currentTask) &&
            LooksLikeCollectionNamedBlobResource(c, currentTask);
        var hasExplicitStructuredBlobShape =
            HasExplicitArrayCellModel(c) ||
            LooksLikeStructuredVectorCollectionType(collectionType) ||
            hasNameOnlyStructuredBlobHint;
        if (string.Equals(normalizedAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
            HasScalarBlobUsageEvidence(c, currentTask))
            return "ByteArrayColumn";

        if (string.Equals(normalizedAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
            !hasExplicitStructuredBlobShape)
            return "ByteArrayColumn";

        if (string.Equals(normalizedAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
            (hasExplicitStructuredBlobShape ||
             HasStructuredBlobVectorEvidence(c, currentTask)))
        {
            var itemType = ResolveArrayColumnItemType(c, fieldModels, currentTask);
            return $"ArrayColumn<{itemType}>";
        }
        if (!string.IsNullOrWhiteSpace(c.ModelRefObj) && int.TryParse(c.ModelRefObj, out var modelOrdinal))
        {
            var fm = fieldModels.FirstOrDefault(x => x.Ordinal == modelOrdinal);
            if (fm is not null)
            {
                if (string.Equals(ResolveFieldTypeReference(fm, currentTask), "NumberColumn", StringComparison.Ordinal) &&
                    !IsDeclaredTaskParameterResource(c, currentTask) &&
                    HasStrongTextScalarUsageEvidence(c, currentTask))
                    return "TextColumn";
                return ResolveFieldTypeReference(fm, currentTask);
            }
        }
        return normalizedAttrObj switch
        {
            "FIELD_NUMERIC" => "NumberColumn",
            "FIELD_DATE" => "DateColumn",
            "FIELD_TIME" => "TimeColumn",
            "FIELD_BOOLEAN" => "BoolColumn",
            "FIELD_LOGICAL" => "BoolColumn",
            "FIELD_BLOB" => "ByteArrayColumn",
            _ => "TextColumn"
        };
    }

    private static bool IsPrimitiveColumnType(string type)
        => type is "TextColumn" or "NumberColumn" or "DateColumn" or "TimeColumn" or "BoolColumn" or "ByteArrayColumn";

    private static bool IsDotNetTaskResource(TaskResourceColumnDef c)
        => !string.IsNullOrWhiteSpace(c.ObjectType);

    private static bool IsArrayTaskResource(TaskResourceColumnDef c)
        => string.Equals(c.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
           Regex.IsMatch(c.CellModelAttrObj ?? "", "TABLE|GRID|VECTOR|OLE", RegexOptions.IgnoreCase);

    private static bool IsDeclaredTaskParameterResource(TaskResourceColumnDef c, TaskSemantic currentTask)
    {
        return currentTask.SelectsSemantic.Items.Any(select =>
            select.IsParameter &&
            !select.IsFunctionSelect &&
            select.ColumnId == c.Id);
    }

    private static string NormalizeDotNetObjectType(string objectType)
    {
        var normalized = NormalizeRawDotNetObjectType(objectType);
        if (string.Equals(normalized, "Cigam.Controls.WebBrowser.WebBrowser", StringComparison.Ordinal))
            return "Shared.Theme.Controls.WebBrowser";
        if (RequiresExternalTypeCompat(normalized))
            return "dynamic";
        return normalized switch
        {
            "String[]" => "System.String[]",
            "string[]" => "System.String[]",
            _ => normalized
        };
    }

    private static string NormalizeRawDotNetObjectType(string objectType)
    {
        var trimmed = objectType.Trim();
        return trimmed.StartsWith("DotNet.", StringComparison.Ordinal)
            ? trimmed["DotNet.".Length..]
            : trimmed;
    }

    private static bool RequiresExternalTypeCompat(string? objectType)
    {
        if (string.IsNullOrWhiteSpace(objectType))
            return false;

        var normalized = NormalizeRawDotNetObjectType(objectType);
        return normalized.StartsWith("Cigam.Utils.Upgrade.Mail.", StringComparison.Ordinal) ||
               normalized.StartsWith("Cigam.WebServices.Apis.Upgrade.", StringComparison.Ordinal);
    }

    private static string BuildExternalTypeCompatCreationExpression(string typeName, IReadOnlyList<string> args)
    {
        var escapedTypeName = Escape(typeName);
        var normalizedArgs = args
            .Where(arg => !string.IsNullOrWhiteSpace(arg))
            .Select(arg => arg.Trim())
            .ToArray();
        return normalizedArgs.Length == 0
            ? $"ExternalTypeCompat.Create(\"{escapedTypeName}\")"
            : $"ExternalTypeCompat.Create(\"{escapedTypeName}\", {string.Join(", ", normalizedArgs)})";
    }

    private static bool ShouldSuppressExplicitRowLocking(TaskSemantic task, string? rowLocking)
    {
        if (task.ResourceDbs.Count == 0)
            return true;
        return string.Equals(rowLocking, "LockingStrategy.OnUserEdit", StringComparison.Ordinal) &&
               string.IsNullOrWhiteSpace(task.Execution.TransactionScope) &&
               task.ResourceDbs.Count == 1 &&
               string.Equals(task.ResourceDbs[0].Access, "W", StringComparison.OrdinalIgnoreCase) &&
               task.ResourceDbs[0].Cache == true;
    }

    private static bool HasKeyLikeTextScalarIdentity(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        if (string.Equals(c.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return false;

        var names = BuildTaskResourceInferenceNames(c, currentTask, memberName);
        return names.Any(name =>
        {
            var normalized = NormalizeSemanticNameForInference(name);
            return !string.IsNullOrWhiteSpace(normalized) &&
                   Regex.IsMatch(normalized, @"(?:^|_)(chave|key|pin|versao|version)(?:_|$)", RegexOptions.IgnoreCase);
        });
    }

    private static string ResolveArrayColumnElementPrototypeExpression(string itemType)
    {
        return itemType switch
        {
            "Number" => "new NumberColumn()",
            "Date" => "new DateColumn()",
            "Time" => "new TimeColumn()",
            "Bool" => "new BoolColumn()",
            "byte[]" => "new ByteArrayColumn()",
            _ => "new TextColumn()"
        };
    }

    private static string ResolveArrayCollectionTypeReference(TaskResourceColumnDef c, IReadOnlyList<FieldModelDef> fieldModels, TaskSemantic currentTask)
    {
        if (UsesFileListVectorSource(c, currentTask))
            return "Types.VectorString";

        if (c.CellModelObj.HasValue)
        {
            var fm = fieldModels.FirstOrDefault(x => x.Ordinal == c.CellModelObj.Value);
            if (fm is not null)
                return ResolveFieldTypeReference(fm, currentTask);
        }
        if (!string.IsNullOrWhiteSpace(c.ModelRefObj) && int.TryParse(c.ModelRefObj, out var modelOrdinal))
        {
            var fm = fieldModels.FirstOrDefault(x => x.Ordinal == modelOrdinal);
            if (fm is not null)
                return ResolveFieldTypeReference(fm, currentTask);
        }
        return "";
    }

    private static string ResolveArrayColumnItemType(TaskResourceColumnDef c, IReadOnlyList<FieldModelDef> fieldModels, TaskSemantic? currentTask = null)
    {
        if (currentTask is not null && UsesFileListVectorSource(c, currentTask))
            return "Text";

        if (currentTask is not null &&
            TryInferArrayColumnItemTypeFromSemanticEvidence(c, currentTask, out var inferredItemType))
            return inferredItemType;

        var attrObj = c.CellModelAttrObj;
        if (string.IsNullOrWhiteSpace(attrObj) && c.CellModelObj.HasValue)
        {
            var fm = fieldModels.FirstOrDefault(x => x.Ordinal == c.CellModelObj.Value);
            attrObj = fm?.AttrObj;
        }

        var collectionType = currentTask is null ? "" : ResolveArrayCollectionTypeReference(c, fieldModels, currentTask);
        if (collectionType.EndsWith("VectorString", StringComparison.Ordinal))
            return "Text";
        if (collectionType.EndsWith("VectorInteger", StringComparison.Ordinal) ||
            collectionType.EndsWith("VectorNumber", StringComparison.Ordinal))
            return "Number";
        if (collectionType.EndsWith("VectorDate", StringComparison.Ordinal))
            return "Date";
        if (collectionType.EndsWith("VectorTime", StringComparison.Ordinal))
            return "Time";
        if (collectionType.EndsWith("VectorLogical", StringComparison.Ordinal) ||
            collectionType.EndsWith("VectorBool", StringComparison.Ordinal))
            return "Bool";
        if (LooksLikeTextScalarCollectionType(collectionType))
            return "Text";
        if (LooksLikeBlobScalarCollectionType(collectionType))
            return "byte[]";

        if (string.IsNullOrWhiteSpace(attrObj) || string.Equals(attrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return "byte[]";

        return attrObj switch
        {
            "FIELD_NUMERIC" => "Number",
            "FIELD_DATE" => "Date",
            "FIELD_TIME" => "Time",
            "FIELD_BOOLEAN" => "Bool",
            "FIELD_LOGICAL" => "Bool",
            _ => "Text"
        };
    }

    private static bool TryInferArrayColumnItemTypeFromSemanticEvidence(TaskResourceColumnDef c, TaskSemantic currentTask, out string itemType)
    {
        itemType = "";

        var names = BuildTaskResourceInferenceNames(c, currentTask);

        if (names.Length == 0)
            return false;

        var normalizedNameTokens = names
            .SelectMany(name => NormalizeSemanticNameForInference(name)
                .Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var compactNames = names
            .Select(name => NormalizeSemanticNameForInference(name).Replace("_", "", StringComparison.Ordinal))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        if (compactNames.Any(name =>
                name.Contains("varsindex", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("fieldsindex", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("variableindex", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("fieldindex", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("controlindex", StringComparison.OrdinalIgnoreCase)))
        {
            itemType = "Number";
            return true;
        }

        var texts = EnumerateTaskTextsForArrayItemInference(currentTask).ToArray();
        var updates = EnumerateTaskUpdatesForArrayItemInference(currentTask).ToArray();
        foreach (var name in names)
        {
            var escapedName = Regex.Escape(name);
            if (updates.Any(update =>
                    string.Equals(update.Variable, name, StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(ResolveUpdateValueSourceSyntax(update, currentTask), @"\bDataViewVarsIndex\s*\(", RegexOptions.IgnoreCase)))
            {
                itemType = "Number";
                return true;
            }

            if (texts.Any(text => Regex.IsMatch(text, $@"\bDataViewVarsIndex\s*\(", RegexOptions.IgnoreCase) &&
                                  Regex.IsMatch(text, $@"\b{escapedName}\b", RegexOptions.IgnoreCase)))
            {
                itemType = "Number";
                return true;
            }

            if (texts.Any(text => Regex.IsMatch(text, $@"\b(VarName|VarCurr|VarCurrN|VarAttr|VarPic|VarControlID|GetVarName)\s*\(\s*VecGet\s*\(\s*{escapedName}\b", RegexOptions.IgnoreCase)))
            {
                itemType = "Number";
                return true;
            }
        }

        if (normalizedNameTokens.Any(token =>
                token.Equals("alpha", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("alfa", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("array", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("arquivo", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("arquivos", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("file", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("files", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("lista", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("list", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("vec", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("vetor", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("vector", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("xml", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("string", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("texto", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("tipo", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("tipos", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("parametro", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("parametros", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("inout", StringComparison.OrdinalIgnoreCase)))
        {
            itemType = "Text";
            return true;
        }

        if (normalizedNameTokens.Any(token =>
                token.Equals("numeric", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("numerico", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("numero", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("numeros", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("double", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("decimal", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("float", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("integer", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("inteiro", StringComparison.OrdinalIgnoreCase)))
        {
            itemType = "Number";
            return true;
        }

        foreach (var name in names)
        {
            var escapedName = Regex.Escape(name);
            if (texts.Any(text => Regex.IsMatch(text, $@"\bVecGet\s*\(\s*{escapedName}\b", RegexOptions.IgnoreCase) &&
                                  Regex.IsMatch(text, @"\b(CastToText|Trim|LTrim|RTrim|RepStr|StrToken|Translate|InStr|Left|Right|Mid)\s*\(", RegexOptions.IgnoreCase)))
            {
                itemType = "Text";
                return true;
            }

            if (texts.Any(text => Regex.IsMatch(text, $@"\bVecGet\s*\(\s*{escapedName}\b", RegexOptions.IgnoreCase) &&
                                  Regex.IsMatch(text, @"\b(CastToNumber|ToNumber|Str)\s*\(|!=\s*0\b|==\s*0\b|<=\s*0\b|>=\s*0\b|<\s*0\b|>\s*0\b", RegexOptions.IgnoreCase)))
            {
                itemType = "Number";
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<TaskUpdateDef> EnumerateTaskUpdatesForArrayItemInference(TaskSemantic currentTask)
    {
        return currentTask.StartLogics.SelectMany(x => x.Actions).Select(a => a.Update)
            .Concat(currentTask.EndLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.RowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.SavingRowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.GroupLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.HandlersSemantic.Bodies.SelectMany(b => b.OrderedActions).Select(a => a.Update))
            .Where(x => x is not null)
            .Cast<TaskUpdateDef>();
    }

    private static string ResolveUpdateValueSourceSyntax(TaskUpdateDef update, TaskSemantic currentTask)
    {
        if (!int.TryParse(update.WithValue, out var expressionId) ||
            !currentTask.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expressionId, out var expression) ||
            expression is null)
        {
            return update.WithValue;
        }

        return ResolveExpressionEntrySourceSyntax(expression);
    }

    private static string NormalizeSemanticNameForInference(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var withWordBoundaries = Regex.Replace(name.Trim(), "([a-z0-9])([A-Z])", "$1_$2");
        return withWordBoundaries.Replace('-', '_');
    }

    private static IEnumerable<string> EnumerateTaskTextsForArrayItemInference(TaskSemantic currentTask)
    {
        foreach (var expression in currentTask.Expressions)
        {
            if (!string.IsNullOrWhiteSpace(expression.Syntax))
                yield return expression.Syntax;
        }

        foreach (var update in EnumerateTaskUpdatesForArrayItemInference(currentTask))
        {
            if (!string.IsNullOrWhiteSpace(update.WithValue))
                yield return update.WithValue;

            var sourceSyntax = ResolveUpdateValueSourceSyntax(update, currentTask);
            if (!string.IsNullOrWhiteSpace(sourceSyntax) &&
                !string.Equals(sourceSyntax, update.WithValue, StringComparison.Ordinal))
            {
                yield return sourceSyntax;
            }
        }
    }

    private static bool HasStructuredBlobVectorEvidence(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        if (HasScalarBlobUsageEvidence(c, currentTask))
            return false;

        if (HasExplicitArrayCellModel(c))
            return true;

        if (!IsDeclaredTaskParameterResource(c, currentTask) &&
            LooksLikeCollectionNamedBlobResource(c, currentTask, memberName))
            return true;

        if (c.DefinitionId.HasValue &&
            Regex.IsMatch(c.CellModelAttrObj ?? "", "TABLE|GRID|VECTOR|OLE", RegexOptions.IgnoreCase))
            return true;

        var names = BuildTaskResourceInferenceNames(c, currentTask, memberName);

        if (names.Length == 0)
            return false;

        bool UsesVectorApiForAnyName(string text) => names.Any(name =>
            Regex.IsMatch(text ?? "",
                $@"\b(VecSet|VecGet|VecCellAttr|VecSize|VariantGetVector|BufSetVector|BufGetVector)\s*\(\s*{Regex.Escape(name)}\b",
                RegexOptions.IgnoreCase));

        if (currentTask.Expressions.Any(e => UsesVectorApiForAnyName(e.Syntax)))
            return true;

        IEnumerable<string> updateExpressions =
            currentTask.StartLogics.SelectMany(x => x.Actions).Select(a => a.Update?.WithValue)
            .Concat(currentTask.EndLogics.SelectMany(x => x.Actions).Select(a => a.Update?.WithValue))
            .Concat(currentTask.RowLogics.SelectMany(x => x.Actions).Select(a => a.Update?.WithValue))
            .Concat(currentTask.SavingRowLogics.SelectMany(x => x.Actions).Select(a => a.Update?.WithValue))
            .Concat(currentTask.GroupLogics.SelectMany(x => x.Actions).Select(a => a.Update?.WithValue))
            .Concat(currentTask.HandlersSemantic.Bodies.SelectMany(b => b.OrderedActions).Select(a => a.Update?.WithValue))
            .Where(x => !string.IsNullOrWhiteSpace(x))!;

        if (updateExpressions.Any(UsesVectorApiForAnyName))
            return true;

        IEnumerable<TaskUpdateDef> updates =
            currentTask.StartLogics.SelectMany(x => x.Actions).Select(a => a.Update)
            .Concat(currentTask.EndLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.RowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.SavingRowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.GroupLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.HandlersSemantic.Bodies.SelectMany(b => b.OrderedActions).Select(a => a.Update))
            .Where(x => x is not null)!;

        return updates.Any(update =>
            names.Any(name => string.Equals(update!.Variable, name, StringComparison.OrdinalIgnoreCase)) &&
            UsesVectorApiForAnyName(update.WithValue));
    }

    private static bool LooksLikeCollectionNamedBlobResource(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        if (!string.Equals(c.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return false;

        var names = new[]
            {
                c.Name,
                memberName,
                ResolveTaskResourceMemberName(currentTask, c)
            }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>();

        return names.Any(name => Regex.IsMatch(name, @"(?:^|_)(list|lista|vetor|vector|vec|selecionados|camposchave|indexes?|arquivos?|files?)(?:_|$)", RegexOptions.IgnoreCase));
    }

    private static bool HasExplicitArrayCellModel(TaskResourceColumnDef c)
        => c.CellModelObj.HasValue || !string.IsNullOrWhiteSpace(c.CellModelAttrObj);

    private static bool HasScalarBlobUsageEvidence(TaskResourceColumnDef c, TaskSemantic currentTask)
    {
        if (!string.Equals(c.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return false;

        var names = BuildTaskResourceInferenceNames(c, currentTask);

        if (names.Length == 0)
            return false;

        static bool UsesVectorApi(string text) =>
            Regex.IsMatch(text ?? "", @"\b(VecSet|VecGet|VecCellAttr|VecSize|VariantGetVector|BufSetVector|BufGetVector|FileListGet|ClientFileListGet)\s*\(", RegexOptions.IgnoreCase);

        static bool UsesScalarBlobApi(string text) =>
            Regex.IsMatch(text ?? "", @"\b(VariantGet|VariantCreate|Blb2File|Blob2Req|File2Blb|BlobToBase64|BlobFromBase64|Buffer)\b", RegexOptions.IgnoreCase);

        bool MentionsAnyName(string text) => names.Any(name =>
            Regex.IsMatch(text ?? "", $@"(?<![\w.]){Regex.Escape(name)}(?![\w.])", RegexOptions.IgnoreCase));

        foreach (var text in EnumerateTaskTextsForArrayItemInference(currentTask))
        {
            if (!MentionsAnyName(text))
                continue;
            if (UsesVectorApi(text))
                return false;
            if (UsesScalarBlobApi(text))
                return true;
        }

        IEnumerable<TaskUpdateDef> updates =
            currentTask.StartLogics.SelectMany(x => x.Actions).Select(a => a.Update)
            .Concat(currentTask.EndLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.RowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.SavingRowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.GroupLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.HandlersSemantic.Bodies.SelectMany(b => b.OrderedActions).Select(a => a.Update))
            .Where(x => x is not null)!;

        if (updates.Any(update =>
                names.Any(name => string.Equals(update!.Variable, name, StringComparison.OrdinalIgnoreCase)) &&
                UsesScalarBlobApi(update.WithValue)))
            return true;

        return false;
    }

    private static bool UsesFileListVectorSource(TaskResourceColumnDef c, TaskSemantic currentTask)
    {
        var names = BuildTaskResourceInferenceNames(c, currentTask);

        if (names.Length == 0)
            return false;

        bool TargetsResource(TaskUpdateDef? update) =>
            update is not null &&
            names.Any(name => string.Equals(update.Variable, name, StringComparison.OrdinalIgnoreCase));

        bool IsFileListExpr(string? expr)
        {
            var fn = TryGetTopLevelFunctionName(expr ?? "");
            return IsTopLevelCall(fn, "u.FileListGet") || IsTopLevelCall(fn, "u.ClientFileListGet");
        }

        IEnumerable<TaskUpdateDef?> updates =
            currentTask.StartLogics.SelectMany(x => x.Actions).Select(a => a.Update)
            .Concat(currentTask.EndLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.RowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.SavingRowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.GroupLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.HandlersSemantic.Bodies.SelectMany(b => b.OrderedActions).Select(a => a.Update));

        return updates.Any(update => TargetsResource(update) && IsFileListExpr(update?.WithValue));
    }

    private static bool HasStrongTextScalarUsageEvidence(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        var numericLikeResource =
            string.Equals(c.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.CellModelAttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(c.Picture?.Trim() ?? "", @"^\d+(\.\d+)?$", RegexOptions.CultureInvariant);

        if (!numericLikeResource)
            return false;

        var cacheKey = string.Join("|",
            currentTask.Ordinal.ToString(),
            c.Id.ToString(),
            c.Name ?? "",
            memberName ?? "");
        if (_textualNumericResourceOverrideCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var names = BuildTaskResourceInferenceNames(c, currentTask, memberName);
        if (names.Length == 0)
            return _textualNumericResourceOverrideCache[cacheKey] = false;

        bool MentionsAnyName(string text) => names.Any(name =>
            Regex.IsMatch(text ?? "", $@"(?<![\w.]){Regex.Escape(name)}(?![\w.])", RegexOptions.IgnoreCase));

        static bool UsesStrongTextApi(string text) =>
            Regex.IsMatch(text ?? "",
                @"\b(Trim|LTrim|RTrim|RepStr|StrToken|Translate|Left|Right|Mid|Flip|FileInfo|FileExist|Upper|Lower|InStr)\s*\(",
                RegexOptions.IgnoreCase);

        static bool UsesPathLiteral(string text) =>
            Regex.IsMatch(text ?? "",
                @"@""[^""]*[\\/][^""]*""|""[^""]*[\\/][^""]*""|""[^""]*\.[A-Za-z0-9]{1,6}""",
                RegexOptions.IgnoreCase);

        static bool UsesTextEmptyComparison(string text) =>
            Regex.IsMatch(text ?? "", @"==\s*""""|!=\s*""""", RegexOptions.IgnoreCase);

        static bool UsesStrongNumericApi(string text) =>
            Regex.IsMatch(text ?? "",
                @"\b(CastToNumber|ToNumber|Val|Abs|Round|Fix|Mod|Pow|Log|Sqrt|DBName|DifDateTime|DateAdd|TimeAdd)\s*\(",
                RegexOptions.IgnoreCase) ||
            Regex.IsMatch(text ?? "", @"(?:==|!=|<=|>=|<|>)\s*-?\d+(?:\D|$)");

        static bool LooksLikeIntrinsicNumericResourceName(string name)
        {
            var normalized = NormalizeSemanticNameForInference(name);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            return Regex.IsMatch(
                normalized,
                @"(?:^|_)(qtd|qtde|quantidade|contador|count|indice|index|posicao|position|ponteiro|pointer|sequencia|seq|numero|num|nro|linha|line|coluna|column|ordem|order|tamanho|maximo|minimo|limite|inicio|fim|start|end)(?:_|$)",
                RegexOptions.IgnoreCase);
        }

        static bool LooksLikeIntrinsicTextResourceName(string name)
        {
            var normalized = NormalizeSemanticNameForInference(name);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            return Regex.IsMatch(
                normalized,
                @"(?:^|_)(senha|password|passwd|passphrase|charset|mailcharset|token|bearer|oauth|auth|login|extensao|extensÃ£o|suffix|sufixo|mascara|mask|pattern|padrao|padrÃ£o)(?:_|$)",
                RegexOptions.IgnoreCase);
        }

        bool UsesStrongNumericRole(string text)
        {
            foreach (var name in names)
            {
                var escaped = Regex.Escape(name);
                if (Regex.IsMatch(text ?? "",
                        $@"(?<![\w.]){escaped}(?![\w.])\s*[-+*/]\s*\d|\d\s*[-+*/]\s*(?<![\w.]){escaped}(?![\w.])",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"\b(Mid|Left|Right|StrToken|VecGet|VecSet|IndexOf|Len|Pos|Del)\s*\([^)]*,\s*(?<![\w.]){escaped}(?![\w.])(?:\s*,|\s*\))",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"\b(Mid)\s*\([^)]*,[^)]*,\s*(?<![\w.]){escaped}(?![\w.])\s*\)",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"(?:==|!=|<=|>=|<|>)\s*(?<![\w.]){escaped}(?![\w.])|(?<![\w.]){escaped}(?![\w.])\s*(?:==|!=|<=|>=|<|>)",
                        RegexOptions.IgnoreCase))
                    return true;
            }

            return false;
        }

        var textHits = 0;
        var numericHits = 0;
        foreach (var text in EnumerateTaskTextsForArrayItemInference(currentTask))
        {
            if (!MentionsAnyName(text))
                continue;

            if (UsesStrongNumericApi(text) || UsesStrongNumericRole(text))
                numericHits++;

            if (UsesStrongTextApi(text) || UsesPathLiteral(text) || UsesTextEmptyComparison(text))
                textHits++;
        }

        if (names.Any(LooksLikeIntrinsicNumericResourceName) && numericHits > 0)
            return _textualNumericResourceOverrideCache[cacheKey] = false;

        if (names.Any(LooksLikeIntrinsicTextResourceName) && numericHits == 0)
            return _textualNumericResourceOverrideCache[cacheKey] = true;

        var result = textHits >= 2 && numericHits == 0;
        _textualNumericResourceOverrideCache[cacheKey] = result;
        return result;
    }

    private static bool HasIntrinsicTextScalarIdentity(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        var names = BuildTaskResourceInferenceNames(c, currentTask, memberName);
        return names.Any(name =>
        {
            var normalized = NormalizeSemanticNameForInference(name);
            if (!string.IsNullOrWhiteSpace(normalized) &&
                Regex.IsMatch(normalized, @"(?:^|_)(alpha|alfa)(?:_|$)", RegexOptions.IgnoreCase))
                return true;

            return !string.IsNullOrWhiteSpace(normalized) &&
                   Regex.IsMatch(
                       normalized,
                       @"(?:^|_)(extensao|extensÃ£o|suffix|sufixo|mascara|mask|pattern|padrao|padrÃ£o|letra|caracter|caractere|char)(?:_|$)",
                       RegexOptions.IgnoreCase);
        });
    }

    private static bool HasStrongNumericScalarUsageEvidence(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        if (string.Equals(c.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase))
            return false;

        var inputRange = c.InputRange?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(inputRange) &&
            Regex.IsMatch(inputRange, @"[A-Za-zÃ€-Ã¿]", RegexOptions.CultureInvariant))
            return false;

        var picture = c.Picture?.Trim() ?? "";
        var hasNumericPicture =
            picture.StartsWith("N", StringComparison.OrdinalIgnoreCase) ||
            picture.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(picture, @"^\d+(\.\d+)?$", RegexOptions.CultureInvariant);
        if (!hasNumericPicture)
            return false;

        var cacheKey = string.Join("|",
            currentTask.Ordinal.ToString(),
            c.Id.ToString(),
            c.Name ?? "",
            memberName ?? "");
        if (_numericTextResourceOverrideCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var names = BuildTaskResourceInferenceNames(c, currentTask, memberName);
        if (names.Length == 0)
            return _numericTextResourceOverrideCache[cacheKey] = false;

        bool MentionsAnyName(string text) => names.Any(name =>
            Regex.IsMatch(text ?? "", $@"(?<![\w.]){Regex.Escape(name)}(?![\w.])", RegexOptions.IgnoreCase));

        static bool UsesStrongNumericApi(string text) =>
            Regex.IsMatch(text ?? "",
                @"\b(CastToNumber|ToNumber|Val|Abs|Round|Fix|Mod|Pow|Log|Sqrt|Str|DenyUndoFor|SetParam)\s*\(",
                RegexOptions.IgnoreCase) ||
            Regex.IsMatch(text ?? "", @"(?:==|!=|<=|>=|<|>)\s*-?\d+(?:\D|$)");

        static bool UsesStrongTextApi(string text) =>
            Regex.IsMatch(text ?? "",
                @"\b(Trim|LTrim|RTrim|RepStr|StrToken|Translate|Left|Right|Mid|Flip|FileInfo|FileExist|Upper|Lower|InStr)\s*\(",
                RegexOptions.IgnoreCase) ||
            Regex.IsMatch(text ?? "", @"==\s*""""|!=\s*""""", RegexOptions.IgnoreCase);

        static bool LooksLikeIntrinsicTextResourceName(string name)
        {
            var normalized = NormalizeSemanticNameForInference(name);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            return Regex.IsMatch(
                normalized,
                @"(?:^|_)(senha|password|passwd|passphrase|charset|mailcharset|token|bearer|oauth|auth|login)(?:_|$)",
                RegexOptions.IgnoreCase);
        }

        bool UsesStrongNumericRole(string text)
        {
            foreach (var name in names)
            {
                var escaped = Regex.Escape(name);
                if (Regex.IsMatch(text ?? "",
                        $@"\b(Str)\s*\(\s*(?<![\w.]){escaped}(?![\w.])\s*,",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"(?<![\w.]){escaped}(?![\w.])\s*[-+*/]\s*\d|\d\s*[-+*/]\s*(?<![\w.]){escaped}(?![\w.])",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"\bCastToNumber\s*\(\s*(?<![\w.]){escaped}(?![\w.])\s*\)",
                        RegexOptions.IgnoreCase))
                    return true;
            }

            return false;
        }

        bool UsesStrongTextRole(string text)
        {
            foreach (var name in names)
            {
                var escaped = Regex.Escape(name);
                if (Regex.IsMatch(text ?? "",
                        $@"\bCastToText\s*\(\s*(?<![\w.]){escaped}(?![\w.])\s*\)",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"\bVal\s*\(\s*(?<![\w.]){escaped}(?![\w.])\s*,",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"\bRepStr\s*\(\s*(?<![\w.]){escaped}(?![\w.])\s*,",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"(?:==|!=)\s*""[^""]*""\s*$|(?<![\w.]){escaped}(?![\w.])\s*(?:==|!=)\s*""",
                        RegexOptions.IgnoreCase))
                    return true;
            }

            return false;
        }

        var numericHits = 0;
        var textHits = 0;
        foreach (var text in EnumerateTaskTextsForArrayItemInference(currentTask))
        {
            if (!MentionsAnyName(text))
                continue;

            if (UsesStrongNumericApi(text) || UsesStrongNumericRole(text))
                numericHits++;

            if (UsesStrongTextApi(text) || UsesStrongTextRole(text))
                textHits++;
        }

        if (names.Any(LooksLikeIntrinsicTextResourceName))
            return _numericTextResourceOverrideCache[cacheKey] = false;

        var result =
            (numericHits >= 2 && numericHits > textHits) ||
            (numericHits >= 1 && textHits == 0);
        _numericTextResourceOverrideCache[cacheKey] = result;
        return result;
    }

    private static bool HasStrongLogicalBlobUsageEvidence(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        if (!string.Equals(c.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return false;

        var cacheKey = string.Join("|",
            currentTask.Ordinal.ToString(),
            c.Id.ToString(),
            c.Name ?? "",
            memberName ?? "");
        if (_logicalBlobResourceOverrideCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var names = BuildTaskResourceInferenceNames(c, currentTask, memberName);
        if (names.Length == 0)
            return _logicalBlobResourceOverrideCache[cacheKey] = false;

        bool MatchesName(string? text) => names.Any(name =>
            Regex.IsMatch(text ?? "", $@"(?<![\w.]){Regex.Escape(name)}(?![\w.])", RegexOptions.IgnoreCase));

        var dataObjects = (IReadOnlyList<DataObjectDef>)_dataObjectsByOrdinal.Values.ToList();
        var checkBoxBindingHit = currentTask.View.SelectedSupportedControls.Any(ctrl =>
        {
            if (!IsViewCheckBoxControl(ctrl))
                return false;

            var directBinding = ResolveControlDataExpression(ctrl, currentTask, _allTasks, dataObjects);
            return MatchesName(directBinding) ||
                   (!string.IsNullOrWhiteSpace(ctrl.DataColumn) && MatchesName(ctrl.DataColumn)) ||
                   (!string.IsNullOrWhiteSpace(ctrl.ControlName) && MatchesName(ctrl.ControlName));
        });

        static bool UsesBooleanBlobShape(string text) =>
            Regex.IsMatch(text ?? "",
                @"CastToByteArray\s*\(\s*(true|false)\s*\)|CastToByteArray\s*\(\s*u\.Not\s*\(|CastToByteArray\s*\(\s*[A-Za-z_][A-Za-z0-9_\.]*\s*\)",
                RegexOptions.IgnoreCase) ||
            Regex.IsMatch(text ?? "",
                @"\b(Not|CastToBool)\s*\(",
                RegexOptions.IgnoreCase);

        static bool UsesStrongBlobApi(string text) =>
            Regex.IsMatch(text ?? "",
                @"\b(File2Blb|BlobToBase64|Base64ToBlob|ByteArrayToText|HTTPCall|SharedValSet|SetParam)\s*\(",
                RegexOptions.IgnoreCase);

        var booleanHits = 0;
        var blobHits = 0;
        foreach (var text in EnumerateTaskTextsForArrayItemInference(currentTask))
        {
            if (!MatchesName(text))
                continue;

            if (UsesBooleanBlobShape(text))
                booleanHits++;
            if (UsesStrongBlobApi(text))
                blobHits++;
        }

        var result = checkBoxBindingHit && booleanHits >= 1 && blobHits == 0;
        _logicalBlobResourceOverrideCache[cacheKey] = result;
        return result;
    }

    private static string[] BuildTaskResourceInferenceNames(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        var virtualSelects = currentTask.SelectsSemantic.Items.Where(s => s.Type == "V").ToList();
        var orderedResources = currentTask.ResourcesSemantic.Ordered.ToList();
        var resourceIndex = orderedResources.FindIndex(r => r.Id == c.Id);
        var positionalAliases = resourceIndex >= 0 && resourceIndex < virtualSelects.Count
            ? new[] { virtualSelects[resourceIndex].Name, virtualSelects[resourceIndex].RealVarName }
            : Array.Empty<string?>();

        return new[]
            {
                c.Name,
                memberName,
                ResolveTaskResourceMemberName(currentTask, c)
            }
            .Concat(currentTask.SelectsSemantic.Items
                .Where(s => s.ColumnId == c.Id)
                .SelectMany(s => new[] { s.Name, s.RealVarName }))
            .Concat(positionalAliases)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool LooksLikeTextScalarCollectionType(string? collectionType)
    {
        if (string.IsNullOrWhiteSpace(collectionType))
            return false;

        var trimmed = collectionType.Trim();
        if (!trimmed.StartsWith("Types.", StringComparison.Ordinal))
            return false;

        return !trimmed.Contains("Blob", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("ByteArray", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Date", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Time", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Number", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Numeric", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Bool", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Logical", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Vector", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeBlobScalarCollectionType(string? collectionType)
    {
        if (string.IsNullOrWhiteSpace(collectionType))
            return false;

        var trimmed = collectionType.Trim();
        return trimmed.Contains("Blob", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("ByteArray", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeStructuredVectorCollectionType(string? collectionType)
    {
        if (string.IsNullOrWhiteSpace(collectionType))
            return false;

        var trimmed = collectionType.Trim();
        return trimmed.EndsWith("VectorString", StringComparison.Ordinal) ||
               trimmed.EndsWith("VectorNumber", StringComparison.Ordinal) ||
               trimmed.EndsWith("VectorInteger", StringComparison.Ordinal) ||
               trimmed.EndsWith("VectorDate", StringComparison.Ordinal) ||
               trimmed.EndsWith("VectorTime", StringComparison.Ordinal) ||
               trimmed.EndsWith("VectorBool", StringComparison.Ordinal) ||
               trimmed.EndsWith("VectorLogical", StringComparison.Ordinal);
    }

    private static string BuildTypedResourceColumnInitializer(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null, string? actualColumnType = null, bool includeFormat = true, bool forceStructuredBlobVector = false)
    {
        var parts = new List<string>();
        if (includeFormat && !string.IsNullOrWhiteSpace(c.Picture))
            parts.Add($"Format = \"{Escape(c.Picture!)}\"");
        if (string.Equals(c.AttrObj, "FIELD_UNICODE", StringComparison.OrdinalIgnoreCase))
            parts.Add("StorageType = TextStorageType.Unicode");
        if (!string.IsNullOrWhiteSpace(c.InputRange))
            parts.Add($"InputRange = \"{Escape(c.InputRange!)}\"");
        if (c.AllowNull.HasValue)
            parts.Add($"AllowNull = {(c.AllowNull.Value ? "true" : "false")}");
        if (c.NullDisplayText is not null)
            parts.Add($"NullDisplayText = \"{Escape(c.NullDisplayText)}\"");
        parts.Add("OnChangeMarkRowAsChanged = false");
        if (string.Equals(actualColumnType?.Trim(), "ByteArrayColumn", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
            !HasStrongLogicalBlobUsageEvidence(c, currentTask, memberName) &&
            !(forceStructuredBlobVector || HasStructuredBlobVectorEvidence(c, currentTask, memberName)))
            parts.Add("ContentType = ByteArrayColumnContentType.Ansi");
        if (!string.IsNullOrWhiteSpace(c.DefaultValue))
        {
            var literal = ConvertDefaultValueLiteral(c, actualColumnType);
            if (!string.IsNullOrWhiteSpace(literal))
                parts.Add($"DefaultValue = {literal}");
        }
        if (parts.Count == 0)
            return "";
        return $" {{ {string.Join(", ", parts)} }}";
    }

    private static string ConvertDefaultValueLiteral(TaskResourceColumnDef c, string? actualColumnType = null)
    {
        var raw = c.DefaultValue?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var normalizedType = actualColumnType?.Trim() ?? "";
        if (normalizedType.StartsWith("TextColumn", StringComparison.Ordinal) ||
            string.Equals(normalizedType, "TextColumn", StringComparison.Ordinal))
            return $"\"{Escape(raw)}\"";

        if (normalizedType.StartsWith("NumberColumn", StringComparison.Ordinal) ||
            string.Equals(normalizedType, "NumberColumn", StringComparison.Ordinal))
            return raw;

        if (normalizedType.StartsWith("BoolColumn", StringComparison.Ordinal) ||
            string.Equals(normalizedType, "BoolColumn", StringComparison.Ordinal))
            return raw == "1" ? "true" : "false";

        if (normalizedType.StartsWith("DateColumn", StringComparison.Ordinal) ||
            string.Equals(normalizedType, "DateColumn", StringComparison.Ordinal))
        {
            if (raw == "0")
                return "XPARuntimeCore.Box.Date.Empty";
            if (int.TryParse(raw, out var typedSerial) && typedSerial > 0)
            {
                var dt = new DateTime(1, 1, 1).AddDays(typedSerial - 1);
                return $"new Date({dt.Year},{dt.Month},{dt.Day})";
            }
            if (raw.Length == 8 && raw.All(char.IsDigit))
                return $"new Date({raw[..4]},{raw[4..6]},{raw[6..8]})";
            return "XPARuntimeCore.Box.Date.Now";
        }

        if (c.AttrObj == "FIELD_DATE")
        {
            if (raw == "0")
                return "XPARuntimeCore.Box.Date.Empty";
            if (int.TryParse(raw, out var serial) && serial > 0)
            {
                var dt = new DateTime(1, 1, 1).AddDays(serial - 1);
                return $"new Date({dt.Year},{dt.Month},{dt.Day})";
            }
            if (raw.Length == 8 && raw.All(char.IsDigit))
                return $"new Date({raw[..4]},{raw[4..6]},{raw[6..8]})";
            return "XPARuntimeCore.Box.Date.Now";
        }
        if (c.AttrObj == "FIELD_NUMERIC")
            return raw;
        if (c.AttrObj is "FIELD_BOOLEAN" or "FIELD_LOGICAL")
            return raw == "1" ? "true" : "false";
        return $"\"{Escape(raw)}\"";
    }

    private static TargetValueInfo ResolveTargetValueInfo(
        TaskSemantic task,
        string? variableName,
        string target,
        TaskResourceColumnDef? resolvedResource = null)
    {
        var cacheKey = string.Join("|",
            task.Ordinal.ToString(CultureInfo.InvariantCulture),
            variableName ?? "",
            target ?? "",
            resolvedResource?.Id.ToString(CultureInfo.InvariantCulture) ?? "");
        if (_targetValueInfoCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (resolvedResource is null &&
            TryResolveDataViewMemberTargetValueInfo(task, target, out var dataViewInfo))
        {
            _targetValueInfoCache[cacheKey] = dataViewInfo;
            return dataViewInfo;
        }

        var resource = resolvedResource ?? ResolveTaskResourceForAssignment(task, variableName, target);
        var targetOwnerTask = ResolveTaskOwnerFromTargetPath(task, target);
        var resourceOwnerTask = resource is null
            ? null
            : targetOwnerTask is not null
                ? targetOwnerTask
                : task.ResourcesSemantic.Ordered.Any(r => ReferenceEquals(r, resource))
                ? task
                : ResolveOwningTaskForResource(resource);
        var modelAttrObj = NormalizeAttrObjKind(ResolveModelColumnAttrObj(task, target));
        var resourceAttrObj = ResolveEffectiveTaskResourceAttrObj(resource, resourceOwnerTask);
        var attrObj = ResolveEffectiveTargetAttrObj(resourceAttrObj, modelAttrObj);
        if (resource is not null && resourceOwnerTask is not null)
        {
            var resolvedType = ResolveTaskResourceColumnType(resource, _allFieldModels, resourceOwnerTask);
            if (string.Equals(resolvedType, "ByteArrayColumn", StringComparison.Ordinal))
                attrObj = "FIELD_BLOB";
            var resolvedAttrObj = ResolveAttrObjForColumnType(resolvedType, _allFieldModels, resourceOwnerTask);
            if (!string.IsNullOrWhiteSpace(resolvedAttrObj))
                attrObj = resolvedAttrObj;
        }
        var targetMember = resource is null
            ? ""
            : ResolveTaskResourceMemberName(resourceOwnerTask ?? task, resource);
        var isDotNet = resource is not null && IsDotNetTaskResource(resource);
        var isArray = resource is not null && IsTaskResourceArrayLike(resource, resourceOwnerTask ?? task);
        var allowResourceHints = string.IsNullOrWhiteSpace(modelAttrObj);
        var isBlob = string.Equals(attrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) ||
                     (allowResourceHints && IsBlobTaskResourceCandidate(resource));
        var isNumeric = IsNumericAttrObj(attrObj) ||
                        (allowResourceHints && IsNumericTaskResourceCandidate(resource, resourceOwnerTask ?? task));
        var isBoolean =
            string.Equals(attrObj, "FIELD_BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(attrObj, "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase);

        var info = new TargetValueInfo(resource, modelAttrObj, attrObj, targetMember, isDotNet, isArray, isBlob, isNumeric, isBoolean);
        _targetValueInfoCache[cacheKey] = info;
        return info;
    }

    private static TaskSemantic? ResolveTaskOwnerFromTargetPath(TaskSemantic task, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return null;

        var remaining = targetPath.Trim();
        if (remaining.StartsWith("Application.Instance.", StringComparison.Ordinal))
            return (_allTasks ?? Array.Empty<TaskSemantic>()).FirstOrDefault(t => t.MainProgram) ??
                   (_allTasks ?? Array.Empty<TaskSemantic>()).FirstOrDefault(t => t.ParentOrdinal is null);

        var currentTask = task;
        var sawParentPrefix = false;
        while (remaining.StartsWith("_parent.", StringComparison.Ordinal))
        {
            sawParentPrefix = true;
            if (!currentTask.ParentOrdinal.HasValue)
                return null;

            var parentTask = GetTaskByOrdinal(currentTask.ParentOrdinal, _allTasks ?? Array.Empty<TaskSemantic>());
            if (parentTask is null)
                return null;

            currentTask = parentTask;
            remaining = remaining["_parent.".Length..];
        }

        return sawParentPrefix ? currentTask : null;
    }

    private static TaskSemantic? ResolveOwningTaskForResource(TaskResourceColumnDef resource)
    {
        if (_allTasks is null)
            return null;

        var ownerByReference = _allTasks.FirstOrDefault(t => t.ResourcesSemantic.Ordered.Any(r => ReferenceEquals(r, resource)));
        if (ownerByReference is not null)
            return ownerByReference;

        return _allTasks.FirstOrDefault(t => t.ResourcesSemantic.Ordered.Any(r => r.Id == resource.Id));
    }

    private static bool TryResolveDataViewMemberTargetValueInfo(
        TaskSemantic task,
        string target,
        out TargetValueInfo info)
    {
        info = default;
        if (!TryResolveDataViewMemberColumn(task, target, out var normalizedTarget, out var column))
            return false;

        var attrObj = ResolveEffectiveDataColumnAttrObj(column);
        if (string.IsNullOrWhiteSpace(attrObj))
            return false;

        attrObj = NormalizeAttrObjKind(attrObj);
        var isBlob = string.Equals(attrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase);
        var isNumeric = IsNumericAttrObj(attrObj);
        var isBoolean =
            string.Equals(attrObj, "FIELD_BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(attrObj, "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase);

        info = new TargetValueInfo(
            Resource: null,
            ModelAttrObj: attrObj,
            AttrObj: attrObj,
            TargetMember: normalizedTarget,
            IsDotNet: false,
            IsArray: false,
            IsBlob: isBlob,
            IsNumeric: isNumeric,
            IsBoolean: isBoolean);
        return true;
    }

    private static bool TryResolveDataViewMemberColumn(
        TaskSemantic task,
        string target,
        out string normalizedTarget,
        out DataColumnDef column)
    {
        normalizedTarget = "";
        column = default!;
        if (string.IsNullOrWhiteSpace(target) || !target.Contains('.', StringComparison.Ordinal))
            return false;

        var targetPath = target.Trim();
        if (targetPath.EndsWith(".Value", StringComparison.Ordinal))
            targetPath = targetPath[..^".Value".Length];

        var segments = targetPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;

        var currentTask = task;
        var index = 0;
        while (index < segments.Length && string.Equals(segments[index], "_parent", StringComparison.Ordinal))
        {
            if (!currentTask.ParentOrdinal.HasValue || !_tasksByOrdinal.TryGetValue(currentTask.ParentOrdinal.Value, out var parentTask))
                return false;

            currentTask = parentTask;
            index++;
        }

        if (segments.Length - index != 2)
            return false;

        if (_dataObjectsByOrdinal.Count == 0)
            return false;

        var owner = segments[index];
        var member = segments[index + 1];
        var key = BuildDataViewMemberColumnKey(owner, member);
        if (!GetDataViewMemberColumnIndex(currentTask).TryGetValue(key, out column) &&
            !GetDataObjectMemberColumnIndex().TryGetValue(key, out column))
        {
            var ownerWithoutSequence = owner;
            var sequenceStart = ownerWithoutSequence.Length;
            while (sequenceStart > 0 && char.IsDigit(ownerWithoutSequence[sequenceStart - 1]))
                sequenceStart--;

            if (sequenceStart == ownerWithoutSequence.Length || sequenceStart == 0)
                return false;

            ownerWithoutSequence = ownerWithoutSequence[..sequenceStart];
            key = BuildDataViewMemberColumnKey(ownerWithoutSequence, member);
            if (!GetDataObjectMemberColumnIndex().TryGetValue(key, out column))
                return false;
        }

        normalizedTarget = targetPath;
        return true;
    }

    private static IReadOnlyDictionary<string, DataColumnDef> GetDataViewMemberColumnIndex(TaskSemantic task)
    {
        if (_dataViewMemberColumnIndexCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var built = BuildDataViewMemberColumnIndex(task);
        _dataViewMemberColumnIndexCache[task.Ordinal] = built;
        return built;
    }

    private static IReadOnlyDictionary<string, DataColumnDef> BuildDataViewMemberColumnIndex(TaskSemantic task)
    {
        var result = new Dictionary<string, DataColumnDef>(StringComparer.OrdinalIgnoreCase);
        if (_dataObjectsByOrdinal.Count == 0)
            return result;

        var dataObjectList = _dataObjectsByOrdinal.Values.ToList();
        var modelMembers = BuildModelMembers(task, dataObjectList);
        foreach (var mm in modelMembers)
            AddDataViewMemberColumnAliases(result, mm.MemberName, mm.DbObj);

        var primaryObj = task.PrimaryDbObj ?? task.InformationDbObj;
        var linkMembers = BuildLinkMembers(task, dataObjectList, modelMembers, primaryObj);
        foreach (var lm in linkMembers)
            AddDataViewMemberColumnAliases(result, lm.MemberName, lm.Link.DbObj);

        return result;
    }

    private static IReadOnlyDictionary<string, DataColumnDef> GetDataObjectMemberColumnIndex()
    {
        if (_dataObjectMemberColumnIndexCache is not null)
            return _dataObjectMemberColumnIndexCache;

        var built = BuildDataObjectMemberColumnIndex();
        _dataObjectMemberColumnIndexCache = built;
        return built;
    }

    private static IReadOnlyDictionary<string, DataColumnDef> BuildDataObjectMemberColumnIndex()
    {
        var result = new Dictionary<string, DataColumnDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var dataObject in _dataObjectsByOrdinal.Values)
        {
            var className = ResolveDataObjectTypeName(dataObject);
            AddDataViewMemberColumnAliases(result, className, dataObject.Ordinal);
            AddDataViewMemberColumnAliases(result, ToPascalIdentifier(dataObject.Name), dataObject.Ordinal);
            AddDataViewMemberColumnAliases(result, ToPascalIdentifier(dataObject.PhysicalName ?? ""), dataObject.Ordinal);
        }

        return result;
    }

    private static void AddDataViewMemberColumnAliases(
        Dictionary<string, DataColumnDef> result,
        string owner,
        int dataObjectOrdinal)
    {
        if (string.IsNullOrWhiteSpace(owner) || !_dataObjectsByOrdinal.TryGetValue(dataObjectOrdinal, out var dataObject))
            return;

        var className = ResolveDataObjectTypeName(dataObject);
        var columnMemberNames = ResolveDataObjectColumnMemberNames(dataObject, className);
        foreach (var column in dataObject.Columns)
        {
            if (columnMemberNames.TryGetValue(column.Id, out var emittedMemberName))
                AddDataViewMemberColumnAlias(result, owner, emittedMemberName, column);

            AddDataViewMemberColumnAlias(result, owner, ToPascalIdentifier(column.Name), column);
            AddDataViewMemberColumnAlias(result, owner, ToPascalIdentifier(column.DbColumnName ?? ""), column);
        }
    }

    private static void AddDataViewMemberColumnAlias(
        Dictionary<string, DataColumnDef> result,
        string owner,
        string member,
        DataColumnDef column)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(member))
            return;

        result.TryAdd(BuildDataViewMemberColumnKey(owner, member), column);
    }

    private static string BuildDataViewMemberColumnKey(string owner, string member)
        => owner + "." + member;

    private static bool IsDataObjectColumnMemberMatch(
        DataColumnDef column,
        string member,
        IReadOnlyDictionary<int, string> columnMemberNames)
    {
        if (columnMemberNames.TryGetValue(column.Id, out var emittedMemberName) &&
            string.Equals(emittedMemberName, member, StringComparison.Ordinal))
            return true;

        return string.Equals(ToPascalIdentifier(column.Name), member, StringComparison.Ordinal) ||
               string.Equals(ToPascalIdentifier(column.DbColumnName ?? ""), member, StringComparison.Ordinal);
    }

    private static string ResolveEffectiveTargetAttrObj(string resourceAttrObj, string modelAttrObj)
    {
        if (!string.IsNullOrWhiteSpace(resourceAttrObj))
            return resourceAttrObj;

        return modelAttrObj ?? "";
    }

    private static bool IsNumericTaskResourceCandidate(TaskResourceColumnDef? resource, TaskSemantic? ownerTask = null)
    {
        if (resource is null)
            return false;

        ownerTask ??= _allTasks?.FirstOrDefault(t => t.ResourcesSemantic.Ordered.Any(r => r.Id == resource.Id));
        if (ownerTask is not null)
        {
            var resolvedType = ResolveTaskResourceColumnType(resource, _allFieldModels, ownerTask);
            if (string.Equals(resolvedType, "TextColumn", StringComparison.Ordinal))
                return false;
        }

        if (ownerTask is not null &&
            string.Equals(resource.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) &&
            HasStrongTextScalarUsageEvidence(resource, ownerTask))
            return false;

        if (ownerTask is not null &&
            string.Equals(resource.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
            HasStrongLogicalBlobUsageEvidence(resource, ownerTask))
            return false;

        if (string.Equals(resource.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase))
            return true;

        var picture = resource.Picture?.Trim();
        if (string.IsNullOrWhiteSpace(picture))
            return false;

        return picture.EndsWith("N", StringComparison.OrdinalIgnoreCase) ||
               picture.EndsWith("C", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTaskResourceArrayLike(TaskResourceColumnDef resource, TaskSemantic task)
    {
        if (IsArrayTaskResource(resource))
            return true;

        var resolvedType = ResolveTaskResourceColumnType(resource, _allFieldModels, task);
        return resolvedType.StartsWith("ArrayColumn<", StringComparison.Ordinal);
    }

    private static string? ResolveModelColumnAttrObj(TaskSemantic task, string target)
    {
        return TryResolveDataViewMemberColumn(task, target, out _, out var column)
            ? ResolveEffectiveDataColumnAttrObj(column)
            : null;
    }

    private static string ResolveEffectiveDataColumnAttrObj(DataColumnDef column)
    {
        if (!string.IsNullOrWhiteSpace(column.AttrObj))
            return NormalizeAttrObjKind(column.AttrObj);

        if (!string.IsNullOrWhiteSpace(column.Attribute))
            return NormalizeAttrObjKind(column.Attribute);

        if (column.ModelRefObj is not null &&
            int.TryParse(column.ModelRefObj, out var modelObj))
        {
            var fieldModel = _allFieldModels.FirstOrDefault(x => x.Ordinal == modelObj);
            if (fieldModel is not null && !string.IsNullOrWhiteSpace(fieldModel.AttrObj))
                return NormalizeAttrObjKind(fieldModel.AttrObj);
        }

        return NormalizeAttrObjKind(column.Attribute ?? column.AttrObj);
    }

    private static TaskResourceColumnDef? ResolveTaskResourceForAssignment(TaskSemantic task, string? variableName, string target)
    {
        var cacheKey = string.Join("|",
            task.Ordinal.ToString(CultureInfo.InvariantCulture),
            variableName ?? "",
            target?.Trim() ?? "");
        if (_taskResourceForAssignmentCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var normalizedTarget = target?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedTarget))
        {
            var byPath = ResolveResourceByTargetPath(task, normalizedTarget, _allTasks ?? Array.Empty<TaskSemantic>());
            if (byPath is not null)
            {
                _taskResourceForAssignmentCache[cacheKey] = byPath;
                return byPath;
            }

            if (task.ResourcesSemantic.ByLegacyName.TryGetValue(normalizedTarget, out var byLegacy))
            {
                _taskResourceForAssignmentCache[cacheKey] = byLegacy;
                return byLegacy;
            }

            if (normalizedTarget.EndsWith(".Value", StringComparison.Ordinal))
            {
                normalizedTarget = normalizedTarget[..^".Value".Length];
                byPath = ResolveResourceByTargetPath(task, normalizedTarget, _allTasks ?? Array.Empty<TaskSemantic>());
                if (byPath is not null)
                {
                    _taskResourceForAssignmentCache[cacheKey] = byPath;
                    return byPath;
                }
            }

            foreach (var resource in task.ResourcesSemantic.Ordered)
            {
                var memberName = ResolveTaskResourceMemberName(task, resource);
                if (string.Equals(memberName, normalizedTarget, StringComparison.Ordinal))
                {
                    _taskResourceForAssignmentCache[cacheKey] = resource;
                    return resource;
                }

                var sanitizedCandidates = new[]
                {
                    ToCodeIdentifierPreservingCase(resource.Name ?? ""),
                    ToPascalIdentifier(resource.Name ?? ""),
                    ToLegacyVariableName(resource.Name ?? "")
                };

                if (sanitizedCandidates.Any(candidate => string.Equals(candidate, normalizedTarget, StringComparison.Ordinal)))
                {
                    _taskResourceForAssignmentCache[cacheKey] = resource;
                    return resource;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(variableName) && task.ResourcesSemantic.ByName.TryGetValue(variableName, out var byName))
        {
            _taskResourceForAssignmentCache[cacheKey] = byName;
            return byName;
        }

        _taskResourceForAssignmentCache[cacheKey] = null;
        return null;
    }

    private static TaskResourceColumnDef? ResolveAncestorTaskResourceForAssignment(TaskSemantic task, string? variableName, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (string.IsNullOrWhiteSpace(variableName))
            return null;

        var parentOrdinal = task.ParentOrdinal;
        while (parentOrdinal.HasValue)
        {
            if (!_tasksByOrdinal.TryGetValue(parentOrdinal.Value, out var parentTask))
                return null;
            if (parentTask is null)
                return null;

            if (parentTask.ResourcesSemantic.ByName.TryGetValue(variableName, out var parentResource))
                return parentResource;

            parentOrdinal = parentTask.ParentOrdinal;
        }

        return null;
    }

    private static TaskResourceColumnDef? ResolveResourceByTargetPath(TaskSemantic task, string? targetPath, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return null;

        var currentTask = task;
        var remaining = targetPath.Trim();
        const string applicationInstancePrefix = "Application.Instance.";
        if (remaining.StartsWith(applicationInstancePrefix, StringComparison.Ordinal))
        {
            var appTask = allTasks.FirstOrDefault(t => t.MainProgram) ?? allTasks.FirstOrDefault(t => t.ParentOrdinal is null);
            if (appTask is null)
                return null;

            currentTask = appTask;
            remaining = remaining[applicationInstancePrefix.Length..];
        }

        while (remaining.StartsWith("_parent.", StringComparison.Ordinal))
        {
            if (!currentTask.ParentOrdinal.HasValue)
                return null;

            var parentTask = GetTaskByOrdinal(currentTask.ParentOrdinal, allTasks);
            if (parentTask is null)
                return null;

            currentTask = parentTask;
            remaining = remaining["_parent.".Length..];
        }

        if (remaining.EndsWith(".Value", StringComparison.Ordinal))
            remaining = remaining[..^".Value".Length];

        foreach (var resource in currentTask.ResourcesSemantic.Ordered)
        {
            var memberName = ResolveTaskResourceMemberName(currentTask, resource);
            if (string.Equals(memberName, remaining, StringComparison.Ordinal))
                return resource;
        }

        var ancestorResource = ResolveAncestorResourceByMemberPath(task, targetPath);
        if (ancestorResource is not null)
            return ancestorResource;

        return null;
    }

    private static TaskResourceColumnDef? ResolveAncestorResourceByMemberPath(TaskSemantic task, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return null;

        var remaining = targetPath.Trim();
        var sawParentPrefix = false;
        while (remaining.StartsWith("_parent.", StringComparison.Ordinal))
        {
            sawParentPrefix = true;
            remaining = remaining["_parent.".Length..];
        }

        if (!sawParentPrefix || string.IsNullOrWhiteSpace(remaining) || remaining.Contains('.', StringComparison.Ordinal))
            return null;

        var parentOrdinal = task.ParentOrdinal;
        while (parentOrdinal.HasValue)
        {
            if (!_tasksByOrdinal.TryGetValue(parentOrdinal.Value, out var parentTask) || parentTask is null)
                return null;

            foreach (var resource in parentTask.ResourcesSemantic.Ordered)
            {
                var memberName = ResolveTaskResourceMemberName(parentTask, resource);
                if (string.Equals(memberName, remaining, StringComparison.Ordinal))
                    return resource;
            }

            parentOrdinal = parentTask.ParentOrdinal;
        }

        return null;
    }

    private static string NormalizeParameterGetterForAttributeCentral(string value, string? attrObj)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        if (TryParseFunctionCall(value.Trim(), out var wrapperName, out var wrapperArgs) &&
            wrapperArgs.Count == 1 &&
            IsTopLevelCall(wrapperName, "u.CastToText"))
        {
            var normalizedInner = NormalizeParameterGetterForAttributeCentral(wrapperArgs[0].Trim(), attrObj);
            return EnsureScalarAttrObjCallArgumentCentral(
                StripExpectedAttributeCastWrappers(normalizedInner, "FIELD_ALPHA"),
                attrObj);
        }

        if (string.Equals(attrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Contains("u.Val(", StringComparison.Ordinal) ||
                value.Contains("u.StrNum(", StringComparison.Ordinal))
                return value;

            var topLevelCall = TryGetTopLevelFunctionName(value);
            if (IsTopLevelCall(topLevelCall, "u.Val") || IsTopLevelCall(topLevelCall, "u.StrNum"))
                return value;
            return RewriteFunctionCalls(value, "u.GetTextParam", args => $"u.GetNumberParam({string.Join(", ", args)})");
        }

        if (string.Equals(attrObj, "FIELD_DATE", StringComparison.OrdinalIgnoreCase))
        {
            if (IsTopLevelCall(TryGetTopLevelFunctionName(value), "u.DVal"))
                return value;

            return RewriteFunctionCalls(value, "u.GetTextParam", args => $"u.GetDateParam({string.Join(", ", args)})");
        }

        if (string.Equals(attrObj, "FIELD_TIME", StringComparison.OrdinalIgnoreCase))
        {
            if (IsTopLevelCall(TryGetTopLevelFunctionName(value), "u.TVal"))
                return value;

            return RewriteFunctionCalls(value, "u.GetTextParam", args => $"u.GetTimeParam({string.Join(", ", args)})");
        }

        return value;
    }

    private static bool TryNormalizeBlobWrappedNewClrExpression(string value, out string normalized)
    {
        normalized = "";
        var trimmed = value.Trim();
        var directWrappedCtor = Regex.Match(
            trimmed,
            @"^u\.CastToByteArray\s*\(\s*(?<inner>new\s+[A-Za-z_][A-Za-z0-9_\.]*\s*\(\s*\))\s*\)$",
            RegexOptions.CultureInvariant);
        if (directWrappedCtor.Success &&
            TryResolveNewClrExpressionReturnType(directWrappedCtor.Groups["inner"].Value, out _))
        {
            normalized = directWrappedCtor.Groups["inner"].Value.Trim();
            return true;
        }

        var unwrapped = NormalizeDotNetAssignmentExpression(value, null);
        if (!TryResolveNewClrExpressionReturnType(unwrapped, out var newClrReturnType))
            return false;

        normalized = NormalizeDotNetAssignmentExpression(value, newClrReturnType);
        return true;
    }

    private static string NormalizeConditionalBranchesWithTypeEngineForAttr(string expression, TaskSemantic task, string? attrObj)
    {
        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(attrObj))
            return expression;

        var expectedReturnType = MapAttrObjToReturnType(attrObj);
        var expectedXpaType = XpaTypeEngine.MapExpectedToXpaType(expectedReturnType);
        if (expectedXpaType == XpaType.Unknown)
            return expression;

        var trimmed = expression.Trim();
        if (!TryParseFunctionCall(trimmed, out var functionName, out var args) ||
            !IsTopLevelCall(functionName, "u.If") ||
            args.Count != 3)
            return expression;

        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeConditionalBranchesWithTypeEngineForAttr));
        string NormalizeBranch(string branch)
        {
            var normalizedBranch = NormalizeConditionalBranchesWithTypeEngineForAttr(branch.Trim(), task, attrObj);
            var branchType = ResolveExpressionXpaType(task, normalizedBranch);
            return XpaTypeEngine.CanCoerce(branchType, expectedXpaType)
                ? XpaTypeEngine.Coerce(normalizedBranch, branchType, expectedXpaType)
                : normalizedBranch;
        }

        return $"u.If({args[0].Trim()}, {NormalizeBranch(args[1])}, {NormalizeBranch(args[2])})";
    }

    private static TargetValueInfo RecalibrateTargetInfoFromDeclaredTargetType(
        TaskSemantic task,
        TargetValueInfo targetInfo,
        string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return targetInfo;

        var targetPath = target.Trim();
        if (targetPath.EndsWith(".Value", StringComparison.Ordinal))
            targetPath = targetPath[..^6];

        var declaredReturnType = NormalizeReturnTypeToken(ResolveExpressionReturnType(null, targetPath, task));
        if (string.IsNullOrWhiteSpace(declaredReturnType) ||
            string.Equals(declaredReturnType, "object", StringComparison.Ordinal))
            return targetInfo;

        var normalizedTarget = targetInfo.TargetMember;
        if (string.IsNullOrWhiteSpace(normalizedTarget))
            normalizedTarget = targetPath;

        var normalizedValueReturnType = GetValueReturnType(declaredReturnType);
        if (targetInfo.IsNumeric &&
            string.Equals(NormalizeAttrObjKind(targetInfo.AttrObj), "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(normalizedValueReturnType, "Text", StringComparison.Ordinal))
        {
            return targetInfo with
            {
                TargetMember = normalizedTarget,
                AttrObj = "FIELD_NUMERIC",
                IsBlob = false,
                IsDotNet = false,
                IsNumeric = true,
                IsBoolean = false
            };
        }

        var isClrObjectReturnType =
            (declaredReturnType.Contains('.', StringComparison.Ordinal) &&
             !string.Equals(normalizedValueReturnType, "byte[]", StringComparison.Ordinal) &&
             !string.Equals(normalizedValueReturnType, "byte[][]", StringComparison.Ordinal)) ||
            string.Equals(declaredReturnType, "System.String[]", StringComparison.Ordinal);

        if (isClrObjectReturnType)
        {
            return targetInfo with
            {
                TargetMember = normalizedTarget,
                AttrObj = "",
                IsBlob = false,
                IsArray = false,
                IsDotNet = true,
                IsNumeric = false,
                IsBoolean = false
            };
        }

        if (targetInfo.Resource is not null &&
            !string.IsNullOrWhiteSpace(targetInfo.AttrObj) &&
            (targetInfo.IsBoolean || targetInfo.IsNumeric || targetInfo.IsBlob || targetInfo.IsArray || targetInfo.IsDotNet))
            return targetInfo;

        return normalizedValueReturnType switch
        {
            "byte[]" => targetInfo with
            {
                TargetMember = normalizedTarget,
                AttrObj = "FIELD_BLOB",
                IsBlob = true,
                IsArray = false,
                IsDotNet = false,
                IsNumeric = false,
                IsBoolean = false
            },
            "Number" => targetInfo with
            {
                TargetMember = normalizedTarget,
                AttrObj = "FIELD_NUMERIC",
                IsBlob = false,
                IsDotNet = false,
                IsNumeric = true,
                IsBoolean = false
            },
            "Bool" => targetInfo with
            {
                TargetMember = normalizedTarget,
                AttrObj = "FIELD_BOOLEAN",
                IsBlob = false,
                IsDotNet = false,
                IsNumeric = false,
                IsBoolean = true
            },
            "Date" => targetInfo with
            {
                TargetMember = normalizedTarget,
                AttrObj = "FIELD_DATE",
                IsBlob = false,
                IsDotNet = false,
                IsNumeric = false,
                IsBoolean = false
            },
            "Time" => targetInfo with
            {
                TargetMember = normalizedTarget,
                AttrObj = "FIELD_TIME",
                IsBlob = false,
                IsDotNet = false,
                IsNumeric = false,
                IsBoolean = false
            },
            "Text" => targetInfo with
            {
                TargetMember = normalizedTarget,
                AttrObj = "FIELD_ALPHA",
                IsBlob = false,
                IsDotNet = false,
                IsNumeric = false,
                IsBoolean = false
            },
            _ => targetInfo
        };
    }

    private static string NormalizeNumericFormattingArgumentCentral(string expression, TaskSemantic? task = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = expression.Trim();
        var original = trimmed;
        var topLevelCall = TryGetTopLevelFunctionName(trimmed);
        if (IsSharedValGetExpression(topLevelCall))
        {
            var rewritten = RewriteFunctionCalls(trimmed, "SharedValGet", args => $"u.SharedValGetNumber({string.Join(", ", args)})");
            rewritten = RewriteFunctionCalls(rewritten, "u.SharedValGet", args => $"u.SharedValGetNumber({string.Join(", ", args)})");
            return TrackLegacyExpressionTreatmentIfChanged(
                "NormalizeType",
                nameof(NormalizeNumericFormattingArgumentCentral),
                original,
                rewritten);
        }

        if (IsTopLevelCall(topLevelCall, "u.CallDLL") || IsTopLevelCall(topLevelCall, "CallDLL"))
            return TrackLegacyExpressionTreatmentIfChanged(
                "NormalizeType",
                nameof(NormalizeNumericFormattingArgumentCentral),
                original,
                EmitScalarArgumentFromEvidence(trimmed, "Number"));

        if (task is not null)
        {
            var expected = ExpectedTypeForReturnType("Number");
            var rewritten = EmitExpressionForExpectedType(trimmed, task, expected);
            if (!string.Equals(rewritten, trimmed, StringComparison.Ordinal))
                return TrackLegacyExpressionTreatmentIfChanged(
                    "NormalizeType",
                    nameof(NormalizeNumericFormattingArgumentCentral),
                    original,
                    rewritten);

            var inferred = ResolveExpectedTypeFromExpressionEvidence(task, trimmed);
            var inferredReturnType = GetValueReturnType(inferred.ReturnType);
            if (IsSimpleIdentifierPath(trimmed) &&
                !string.Equals(inferred.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase))
                return TrackLegacyExpressionTreatmentIfChanged(
                    "NormalizeType",
                    nameof(NormalizeNumericFormattingArgumentCentral),
                    original,
                    ApplyAttributeCastCentral(trimmed, "FIELD_NUMERIC"));

            if (IsSimpleIdentifierPath(trimmed) &&
                !string.Equals(inferredReturnType, "Number", StringComparison.Ordinal))
                return TrackLegacyExpressionTreatmentIfChanged(
                    "NormalizeType",
                    nameof(NormalizeNumericFormattingArgumentCentral),
                    original,
                    ApplyAttributeCastCentral(trimmed, "FIELD_NUMERIC"));
        }

        if (IsSimpleIdentifierPath(trimmed) &&
            !IsNumericLiteralExpressionCentral(trimmed) &&
            !IsTopLevelCall(topLevelCall, "u.CastToNumber") &&
            !IsTopLevelCall(topLevelCall, "CastToNumber") &&
            !IsTopLevelCall(topLevelCall, "u.ToNumber") &&
            !IsTopLevelCall(topLevelCall, "ToNumber") &&
            !IsTopLevelCall(topLevelCall, "u.Val") &&
            !IsTopLevelCall(topLevelCall, "Val"))
        {
            return TrackLegacyExpressionTreatmentIfChanged(
                "NormalizeType",
                nameof(NormalizeNumericFormattingArgumentCentral),
                original,
                EmitScalarArgumentFromEvidence(trimmed, "Number"));
        }

        return trimmed;
    }

    private static string NormalizeNumericSinkArgumentCentral(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = expression.Trim();
        var topLevelCall = TryGetTopLevelFunctionName(trimmed);
        if (IsTopLevelCall(topLevelCall, "u.DVal") ||
            IsTopLevelCall(topLevelCall, "DVal") ||
            IsTopLevelCall(topLevelCall, "u.CastToDate") ||
            IsTopLevelCall(topLevelCall, "CastToDate") ||
            IsTopLevelCall(topLevelCall, "u.ToDate") ||
            IsTopLevelCall(topLevelCall, "ToDate") ||
            IsTopLevelCall(topLevelCall, "u.CastToTime") ||
            IsTopLevelCall(topLevelCall, "CastToTime") ||
            IsTopLevelCall(topLevelCall, "u.ToTime") ||
            IsTopLevelCall(topLevelCall, "ToTime") ||
            IsTopLevelCall(topLevelCall, "UserMethods.ToTime"))
        {
            return $"u.ToNumber({trimmed})";
        }

        if (TryUnwrapCastToTextExpressionCentral(trimmed, out var castTextInner))
        {
            var innerTopLevelCall = TryGetTopLevelFunctionName(castTextInner);
            if (IsTopLevelCall(innerTopLevelCall, "u.InStr") ||
                IsTopLevelCall(innerTopLevelCall, "InStr") ||
                IsTopLevelCall(innerTopLevelCall, "u.Len") ||
                IsTopLevelCall(innerTopLevelCall, "Len") ||
                IsTopLevelCall(innerTopLevelCall, "u.If") ||
                IsTopLevelCall(innerTopLevelCall, "u.Val") ||
                IsTopLevelCall(innerTopLevelCall, "Val") ||
                IsTopLevelCall(innerTopLevelCall, "u.CastToNumber") ||
                IsTopLevelCall(innerTopLevelCall, "CastToNumber") ||
                SplitTopLevelArithmeticExpression(castTextInner) is not null ||
                IsNumericLiteralExpressionCentral(castTextInner))
            {
                trimmed = NormalizeNumericConditionalBranchExpressionCentral(castTextInner);
            }
        }

        if (!IsNumericProjectionFunctionName(topLevelCall ?? string.Empty) &&
            LooksLikeTextualNumericProjectionExpression(trimmed) &&
            (trimmed.Contains("u.DStr(", StringComparison.Ordinal) ||
             trimmed.Contains("u.TStr(", StringComparison.Ordinal) ||
             trimmed.Contains("u.Str(", StringComparison.Ordinal)))
        {
            return WrapNumericProjectionExpression(
                NormalizeTextualNumericProjectionArgumentCentral(trimmed));
        }

        var arithmetic = SplitTopLevelArithmeticExpression(trimmed);
        if (arithmetic is not null)
        {
            var left = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(NormalizeNumericSinkArgumentCentral(arithmetic.Value.Left.Trim()), "FIELD_NUMERIC"),
                "Number");
            var right = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(NormalizeNumericSinkArgumentCentral(arithmetic.Value.Right.Trim()), "FIELD_NUMERIC"),
                "Number");
            trimmed = $"{left} {arithmetic.Value.Operator} {right}";
        }
        if (TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            string.Equals(functionName, "u.If", StringComparison.OrdinalIgnoreCase) &&
            args.Count == 3)
        {
            var whenTrue = NormalizeConditionalBranchForContext(args[1].Trim(), "FIELD_NUMERIC", "Number");
            var whenFalse = NormalizeConditionalBranchForContext(args[2].Trim(), "FIELD_NUMERIC", "Number");
            whenTrue = UnwrapExplicitScalarCastLayers(StripExpectedAttributeCastWrappers(whenTrue, "FIELD_NUMERIC"));
            whenFalse = UnwrapExplicitScalarCastLayers(StripExpectedAttributeCastWrappers(whenFalse, "FIELD_NUMERIC"));
            trimmed = $"u.If({args[0].Trim()}, {whenTrue}, {whenFalse})";
        }

        var normalized = StripRedundantOuterParentheses(trimmed.Trim());
        normalized = NormalizeExpressionForDeclaredReturnTypeCentral(normalized, "Number", null);
        if (normalized.Contains("u.Left(", StringComparison.Ordinal) ||
            normalized.Contains("u.Right(", StringComparison.Ordinal) ||
            normalized.Contains("u.Mid(", StringComparison.Ordinal) ||
            normalized.Contains("u.Del(", StringComparison.Ordinal))
        {
            normalized = NormalizeTextFunctionInputsCentral(normalized);
        }

        if (TryParseFunctionCall(normalized, out var normalizedFunctionName, out var normalizedArgs) &&
            string.Equals(normalizedFunctionName, "u.If", StringComparison.OrdinalIgnoreCase) &&
            normalizedArgs.Count == 3)
        {
            var whenTrueSource = NormalizeNumericConditionalBranchExpressionCentral(normalizedArgs[1].Trim());
            var whenFalseSource = NormalizeNumericConditionalBranchExpressionCentral(normalizedArgs[2].Trim());
            whenTrueSource = UnwrapExplicitScalarCastLayers(StripExpectedAttributeCastWrappers(whenTrueSource, "FIELD_NUMERIC"));
            whenFalseSource = UnwrapExplicitScalarCastLayers(StripExpectedAttributeCastWrappers(whenFalseSource, "FIELD_NUMERIC"));
            var whenTrue = EmitScalarArgumentFromEvidence(whenTrueSource, "Number");
            var whenFalse = EmitScalarArgumentFromEvidence(whenFalseSource, "Number");
            normalized = $"u.If({normalizedArgs[0].Trim()}, {whenTrue}, {whenFalse})";
        }

        return normalized;
    }

    private static string NormalizeNumericConditionalBranchExpressionCentral(string expression)
    {
        var normalized = StripAccidentalScalarWrapperForExpectedTypeCentral(
            StripExpectedAttributeCastWrappers(expression.Trim(), "FIELD_ALPHA"),
            "Number");
        if (TryUnwrapCastToTextExpressionCentral(normalized, out var castTextInner))
        {
            var innerTrimmed = castTextInner.Trim();
            if (TryParseFunctionCall(innerTrimmed, out var innerFunctionName, out var innerArgs) &&
                IsTopLevelCall(innerFunctionName, "u.If") &&
                innerArgs.Count == 3)
            {
                var whenTrue = EmitScalarArgumentFromEvidence(
                    StripExpectedAttributeCastWrappers(
                        NormalizeNumericConditionalBranchExpressionCentral(innerArgs[1].Trim()),
                        "FIELD_NUMERIC"),
                    "Number");
                var whenFalse = EmitScalarArgumentFromEvidence(
                    StripExpectedAttributeCastWrappers(
                        NormalizeNumericConditionalBranchExpressionCentral(innerArgs[2].Trim()),
                        "FIELD_NUMERIC"),
                    "Number");
                normalized = $"u.If({innerArgs[0].Trim()}, {whenTrue}, {whenFalse})";
                return normalized;
            }

            var arithmetic = SplitTopLevelArithmeticExpression(innerTrimmed);
            if (arithmetic is not null)
            {
                var left = EmitScalarArgumentFromEvidence(
                    StripExpectedAttributeCastWrappers(
                        NormalizeNumericConditionalBranchExpressionCentral(arithmetic.Value.Left.Trim()),
                        "FIELD_NUMERIC"),
                    "Number");
                var right = EmitScalarArgumentFromEvidence(
                    StripExpectedAttributeCastWrappers(
                        NormalizeNumericConditionalBranchExpressionCentral(arithmetic.Value.Right.Trim()),
                        "FIELD_NUMERIC"),
                    "Number");
                normalized = $"{left} {arithmetic.Value.Operator} {right}";
                return normalized;
            }

            var innerTopLevelCall = TryGetTopLevelFunctionName(innerTrimmed);
            if (IsTopLevelCall(innerTopLevelCall, "u.InStr") ||
                IsTopLevelCall(innerTopLevelCall, "InStr") ||
                IsTopLevelCall(innerTopLevelCall, "u.Len") ||
                IsTopLevelCall(innerTopLevelCall, "Len") ||
                IsTopLevelCall(innerTopLevelCall, "u.Val") ||
                IsTopLevelCall(innerTopLevelCall, "Val") ||
                IsTopLevelCall(innerTopLevelCall, "u.ToNumber") ||
                IsTopLevelCall(innerTopLevelCall, "ToNumber") ||
                IsTopLevelCall(innerTopLevelCall, "u.CastToNumber") ||
                IsTopLevelCall(innerTopLevelCall, "CastToNumber") ||
                IsNumericLiteralExpressionCentral(innerTrimmed))
            {
                normalized = innerTrimmed;
            }
            else if (SplitTopLevelArithmeticExpression(innerTrimmed) is not null ||
                     IsSimpleIdentifierPath(innerTrimmed))
            {
                normalized = NormalizeNumericConditionalBranchExpressionCentral(innerTrimmed);
            }
        }
        else if (TryParseFunctionCall(normalized, out var conditionalFunctionName, out var conditionalArgs) &&
                 IsTopLevelCall(conditionalFunctionName, "u.If") &&
                 conditionalArgs.Count == 3)
        {
            var whenTrue = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    NormalizeNumericConditionalBranchExpressionCentral(conditionalArgs[1].Trim()),
                    "FIELD_NUMERIC"),
                "Number");
            var whenFalse = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    NormalizeNumericConditionalBranchExpressionCentral(conditionalArgs[2].Trim()),
                    "FIELD_NUMERIC"),
                "Number");
            normalized = $"u.If({conditionalArgs[0].Trim()}, {whenTrue}, {whenFalse})";
        }
        else if (TryParseFunctionCall(normalized, out var numericCastName, out var numericCastArgs) &&
                 IsTopLevelCall(numericCastName, "u.CastToBool") &&
                 numericCastArgs.Count == 1)
        {
            normalized = StripExpectedAttributeCastWrappers(
                NormalizeNumericConditionalBranchExpressionCentral(numericCastArgs[0].Trim()),
                "FIELD_NUMERIC");
        }

        return normalized;
    }

    private static string NormalizeTextFunctionInputsCentral(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;
        if (!TryParseFunctionCall(expression, out var functionName, out var args))
            return NormalizeNestedKnownTextFunctionArgumentsCentral(expression);

        for (var i = 0; i < args.Count; i++)
            args[i] = NormalizeTextFunctionInputsCentral(args[i].Trim());

        foreach (var argIndex in GetTimeArgumentIndexes(functionName, args.Count))
        {
            args[argIndex] = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    NormalizeNumericOperands(
                        NormalizeTimeConditionalExpression(args[argIndex])),
                    "FIELD_TIME"),
                "Time");
        }

        foreach (var argIndex in GetNumericArgumentIndexes(functionName, args.Count))
        {
            args[argIndex] = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    NormalizeNumericSinkArgumentCentral(args[argIndex]),
                    "FIELD_NUMERIC"),
                "Number");
        }

        foreach (var argIndex in GetTextArgumentIndexes(functionName, args.Count))
            args[argIndex] = NormalizeTextSinkArgumentCentral(args[argIndex]);

        if ((string.Equals(functionName, "u.MailSend", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "MailSend", StringComparison.OrdinalIgnoreCase)) &&
            args.Count > 6)
        {
            for (var i = 6; i < args.Count; i++)
            {
                var attachmentExpr = args[i].Trim();
                if (TryUnwrapCastToTextExpressionCentral(attachmentExpr, out var innerAttachmentExpr))
                    args[i] = innerAttachmentExpr;
            }
        }

        return TrackLegacyExpressionTreatmentIfChanged(
            "NormalizeType",
            nameof(NormalizeTextFunctionInputsCentral),
            expression,
            $"{functionName}({string.Join(", ", args)})");
    }

    private static string NormalizeFormattingFunctionInputsCentral(string expression, TaskSemantic? task = null)
    {
        if (!TryParseFunctionCall(expression, out var functionName, out var args))
            return expression;

        for (var i = 0; i < args.Count; i++)
            args[i] = NormalizeFormattingFunctionInputsCentral(args[i].Trim(), task);

        if (IsTopLevelCall(functionName, "u.DStr") && args.Count > 0)
            args[0] = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    StripRedundantOuterParentheses(args[0].Trim()),
                    "FIELD_DATE"),
                "Date");

        if (IsTopLevelCall(functionName, "u.TStr") && args.Count > 0)
            args[0] = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    StripRedundantOuterParentheses(args[0].Trim()),
                    "FIELD_TIME"),
                "Time");

        if (IsTopLevelCall(functionName, "u.MTStr") && args.Count > 0)
            args[0] = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    StripRedundantOuterParentheses(args[0].Trim()),
                    "FIELD_NUMERIC"),
                "Number");

        if (IsTopLevelCall(functionName, "u.Str") && args.Count > 0)
            args[0] = NormalizeNumericFormattingArgumentCentral(args[0], task);

        if (IsTopLevelCall(functionName, "u.ToNumber") && args.Count > 0)
            args[0] = NormalizeTextualNumericProjectionArgumentCentral(args[0], task);

        if (IsTopLevelCall(functionName, "u.DBName"))
        {
            if (args.Count == 1 &&
                TryGetWholeCSharpStringLiteral(args[0].Trim(), out var dbNameLiteral) &&
                TrySplitDbNameLiteral(dbNameLiteral, out var dbFileIndex, out var dbInfoType))
            {
                var normalizedDbFileIndex = EmitScalarArgumentFromEvidence(dbFileIndex, "Number");
                var normalizedDbInfoType = EmitScalarArgumentFromEvidence(dbInfoType, "Number");
                return TrackLegacyExpressionTreatmentIfChanged(
                    "NormalizeType",
                    nameof(NormalizeFormattingFunctionInputsCentral),
                    expression,
                    $"u.DBName({normalizedDbFileIndex}, {normalizedDbInfoType})");
            }

            if (args.Count > 0)
                args[0] = NormalizeNumericFormattingArgumentCentral(args[0], task);
            if (args.Count > 1)
                args[1] = NormalizeNumericFormattingArgumentCentral(args[1], task);
        }

        var rewritten = $"{functionName}({string.Join(", ", args)})";
        rewritten = NormalizeTextualNumericProjectionCallsCentral(rewritten, task);
        rewritten = RestoreTextGetterInsideNumericValueWrappersCentral(rewritten);
        rewritten = NormalizeNestedKnownTextFunctionArgumentsCentral(rewritten);
        rewritten = NormalizeTextualNumericProjectionCallsCentral(rewritten, task);
        return TrackLegacyExpressionTreatmentIfChanged(
            "NormalizeType",
            nameof(NormalizeFormattingFunctionInputsCentral),
            expression,
            RestoreTextGetterInsideNumericValueWrappersCentral(rewritten));
    }

    private static string NormalizeTextualNumericProjectionArgumentCentral(string expression, TaskSemantic? task = null)
    {
        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeTextualNumericProjectionArgumentCentral));
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = expression.Trim();
        trimmed = RewriteFunctionCalls(trimmed, "u.CastToNumber", args =>
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
            {
                return $"u.CastToNumber({EmitScalarArgumentFromEvidence(candidate, "Text")})";
            }

            return TryGetWholeCSharpStringLiteral(candidate, out _)
                ? candidate
                : null;
        });

        if (TryParseFunctionCall(trimmed, out var castName, out var castArgs) &&
            IsTopLevelCall(castName, "u.CastToNumber") &&
            castArgs.Count == 1 &&
            TryGetWholeCSharpStringLiteral(castArgs[0].Trim(), out _))
        {
            return castArgs[0].Trim();
        }

        if (TryParseFunctionCall(trimmed, out var functionName, out var args))
        {
            for (var i = 0; i < args.Count; i++)
                args[i] = NormalizeTextualNumericProjectionArgumentCentral(args[i].Trim(), task);

            if (IsTopLevelCall(functionName, "u.DStr") ||
                IsTopLevelCall(functionName, "u.TStr") ||
                IsTopLevelCall(functionName, "u.Str"))
            {
                return NormalizeFormattingFunctionInputsCentral($"{functionName}({string.Join(", ", args)})", task);
            }

            if (IsTopLevelCall(functionName, "u.If") && args.Count == 3)
                return $"u.If({args[0]}, {args[1]}, {args[2]})";
        }

        var arithmetic = SplitTopLevelArithmeticExpression(trimmed);
        if (arithmetic is not null)
        {
            var left = NormalizeTextualNumericProjectionArgumentCentral(arithmetic.Value.Left.Trim(), task);
            var right = NormalizeTextualNumericProjectionArgumentCentral(arithmetic.Value.Right.Trim(), task);
            return $"{left} {arithmetic.Value.Operator} {right}";
        }

        return trimmed;
    }

    private static string NormalizeTextualNumericProjectionCallsCentral(string expression, TaskSemantic? task = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var original = expression;
        foreach (var functionName in new[] { "u.ToNumber", "ToNumber" })
        {
            expression = RewriteFunctionCalls(expression, functionName, args =>
            {
                if (args.Count != 1)
                    return null;

                var original = args[0].Trim();
                if (!LooksLikeTextualNumericProjectionExpression(original))
                    return null;

                var rewritten = NormalizeTextualNumericProjectionArgumentCentral(original, task);
                return $"u.Val({rewritten}, \"\")";
            });
        }

        return TrackLegacyExpressionTreatmentIfChanged(
            "NormalizeType",
            nameof(NormalizeTextualNumericProjectionCallsCentral),
            original,
            expression);
    }

    private static bool LooksLikeTextualNumericProjectionExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = expression.Trim();
        if (trimmed.Contains("u.DStr(", StringComparison.Ordinal) ||
            trimmed.Contains("u.TStr(", StringComparison.Ordinal) ||
            trimmed.Contains("u.Str(", StringComparison.Ordinal))
            return true;

        var arithmetic = SplitTopLevelArithmeticExpression(trimmed);
        if (arithmetic is null)
            return false;

        return LooksLikeTextualNumericProjectionExpression(arithmetic.Value.Left.Trim()) ||
               LooksLikeTextualNumericProjectionExpression(arithmetic.Value.Right.Trim()) ||
               TryGetWholeCSharpStringLiteral(arithmetic.Value.Left.Trim(), out _) ||
               TryGetWholeCSharpStringLiteral(arithmetic.Value.Right.Trim(), out _);
    }

    private static string NormalizeNestedFormattingCallsCentral(string expression, TaskSemantic? task = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var normalized = expression;
        if (normalized.Contains("u.DBName(\"", StringComparison.Ordinal))
        {
            normalized = Regex.Replace(
                normalized,
                @"u\.DBName\s*\(\s*""(?<file>\d+)\s*,\s*(?<info>\d+)""\s*\)",
                m => $"u.DBName({m.Groups["file"].Value}, {m.Groups["info"].Value})",
                RegexOptions.CultureInvariant);
        }

        if (normalized.Contains("u.Str(", StringComparison.Ordinal))
        {
            normalized = RewriteFunctionCalls(normalized, "u.Str", args =>
            {
                if (args.Count == 0)
                    return null;

                args[0] = NormalizeNumericFormattingArgumentCentral(args[0].Trim(), task);
                return $"u.Str({string.Join(", ", args)})";
            });
        }

        if (normalized.Contains("u.DBName(", StringComparison.Ordinal))
        {
            normalized = RewriteFunctionCalls(normalized, "u.DBName", args =>
            {
                if (args.Count == 1 &&
                    TryGetWholeCSharpStringLiteral(args[0].Trim(), out var dbNameLiteral) &&
                    TrySplitDbNameLiteral(dbNameLiteral, out var dbFileIndex, out var dbInfoType))
                {
                    var normalizedDbFileIndex = EmitScalarArgumentFromEvidence(dbFileIndex, "Number");
                    var normalizedDbInfoType = EmitScalarArgumentFromEvidence(dbInfoType, "Number");
                    return $"u.DBName({normalizedDbFileIndex}, {normalizedDbInfoType})";
                }

                if (args.Count > 0)
                    args[0] = NormalizeNumericFormattingArgumentCentral(args[0].Trim(), task);
                if (args.Count > 1)
                    args[1] = NormalizeNumericFormattingArgumentCentral(args[1].Trim(), task);
                return $"u.DBName({string.Join(", ", args)})";
            });
        }

        return normalized;
    }

    private static string NormalizeNestedKnownTextFunctionArgumentsCentral(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var normalized = expression;

        normalized = RewriteFunctionCalls(normalized, "u.Trim", args =>
        {
            if (args.Count == 0)
                return null;
            args[0] = NormalizeTextSinkArgumentCentral(args[0].Trim());
            return $"u.Trim({string.Join(", ", args)})";
        });

        normalized = RewriteFunctionCalls(normalized, "u.RepStr", args =>
        {
            if (args.Count == 0)
                return null;
            args[0] = NormalizeTextSinkArgumentCentral(args[0].Trim());
            if (args.Count > 1)
                args[1] = NormalizeTextSinkArgumentCentral(args[1].Trim());
            if (args.Count > 2)
                args[2] = NormalizeTextSinkArgumentCentral(args[2].Trim());
            return $"u.RepStr({string.Join(", ", args)})";
        });

        normalized = RewriteFunctionCalls(normalized, "u.Str", args =>
        {
            if (args.Count == 0)
                return null;
            var numericArg = NormalizeNumericConditionalBranchExpressionCentral(args[0].Trim());
            numericArg = NormalizeNumericSinkArgumentCentral(numericArg);
            args[0] = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    numericArg,
                    "FIELD_NUMERIC"),
                "Number");
            if (args.Count > 1)
                args[1] = NormalizeTextSinkArgumentCentral(args[1].Trim());
            return $"u.Str({string.Join(", ", args)})";
        });

        normalized = RewriteFunctionCalls(normalized, "u.DStr", args =>
        {
            if (args.Count == 0)
                return null;
            args[0] = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    NormalizeDateConditionalExpression(args[0].Trim()),
                    "FIELD_DATE"),
                "Date");
            if (args.Count > 1)
                args[1] = NormalizeTextSinkArgumentCentral(args[1].Trim());
            return $"u.DStr({string.Join(", ", args)})";
        });

        normalized = RewriteFunctionCalls(normalized, "u.TStr", args =>
        {
            if (args.Count == 0)
                return null;
            args[0] = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    NormalizeTimeConditionalExpression(args[0].Trim()),
                    "FIELD_TIME"),
                "Time");
            if (args.Count > 1)
                args[1] = NormalizeTextSinkArgumentCentral(args[1].Trim());
            return $"u.TStr({string.Join(", ", args)})";
        });

        normalized = RewriteFunctionCalls(normalized, "u.MTStr", args =>
        {
            if (args.Count == 0)
                return null;
            args[0] = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    NormalizeNumericConditionalBranchExpressionCentral(args[0].Trim()),
                    "FIELD_NUMERIC"),
                "Number");
            if (args.Count > 1)
                args[1] = NormalizeTextSinkArgumentCentral(args[1].Trim());
            return $"u.MTStr({string.Join(", ", args)})";
        });

        normalized = RewriteFunctionCalls(normalized, "u.Left", args =>
        {
            if (args.Count == 0)
                return null;
            args[0] = NormalizeTextSinkArgumentCentral(args[0].Trim());
            if (args.Count > 1)
                args[1] = EmitScalarArgumentFromEvidence(
                    StripExpectedAttributeCastWrappers(NormalizeNumericSinkArgumentCentral(args[1].Trim()), "FIELD_NUMERIC"),
                    "Number");
            return $"u.Left({string.Join(", ", args)})";
        });

        normalized = RewriteFunctionCalls(normalized, "u.Right", args =>
        {
            if (args.Count == 0)
                return null;
            args[0] = NormalizeTextSinkArgumentCentral(args[0].Trim());
            if (args.Count > 1)
                args[1] = EmitScalarArgumentFromEvidence(
                    StripExpectedAttributeCastWrappers(NormalizeNumericSinkArgumentCentral(args[1].Trim()), "FIELD_NUMERIC"),
                    "Number");
            return $"u.Right({string.Join(", ", args)})";
        });

        normalized = RewriteFunctionCalls(normalized, "u.Mid", args =>
        {
            if (args.Count == 0)
                return null;
            args[0] = NormalizeTextSinkArgumentCentral(args[0].Trim());
            if (args.Count > 1)
                args[1] = EmitScalarArgumentFromEvidence(
                    StripExpectedAttributeCastWrappers(NormalizeNumericSinkArgumentCentral(args[1].Trim()), "FIELD_NUMERIC"),
                    "Number");
            if (args.Count > 2)
            {
                var rawLengthArg = StripExpectedAttributeCastWrappers(args[2].Trim(), "FIELD_ALPHA");
                var normalizedLengthArg = NormalizeNumericConditionalBranchExpressionCentral(rawLengthArg);
                normalizedLengthArg = NormalizeNumericSinkArgumentCentral(normalizedLengthArg);
                args[2] = EmitScalarArgumentFromEvidence(
                    StripExpectedAttributeCastWrappers(normalizedLengthArg, "FIELD_NUMERIC"),
                    "Number");
            }
            return $"u.Mid({string.Join(", ", args)})";
        });

        normalized = RewriteFunctionCalls(normalized, "u.Del", args =>
        {
            if (args.Count < 3)
                return null;
            args[1] = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(NormalizeNumericSinkArgumentCentral(args[1].Trim()), "FIELD_NUMERIC"),
                "Number");
            args[2] = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(NormalizeNumericSinkArgumentCentral(args[2].Trim()), "FIELD_NUMERIC"),
                "Number");
            return $"u.Del({string.Join(", ", args)})";
        });

        normalized = RewriteFunctionCalls(normalized, "u.StrToken", args =>
        {
            if (args.Count == 0)
                return null;
            args[0] = NormalizeTextSinkArgumentCentral(args[0].Trim());
            if (args.Count > 1)
                args[1] = EmitScalarArgumentFromEvidence(
                    StripExpectedAttributeCastWrappers(NormalizeNumericSinkArgumentCentral(args[1].Trim()), "FIELD_NUMERIC"),
                    "Number");
            if (args.Count > 2)
                args[2] = NormalizeTextSinkArgumentCentral(args[2].Trim());
            return $"u.StrToken({string.Join(", ", args)})";
        });

        normalized = RewriteFunctionCalls(normalized, "u.StrTokenCnt", args =>
        {
            if (args.Count == 0)
                return null;
            args[0] = NormalizeTextSinkArgumentCentral(args[0].Trim());
            if (args.Count > 1)
                args[1] = NormalizeTextSinkArgumentCentral(args[1].Trim());
            return $"u.StrTokenCnt({string.Join(", ", args)})";
        });

        normalized = RewriteFunctionCalls(normalized, "u.XMLInsert", args =>
        {
            if (args.Count == 0)
                return null;

            if (args.Count > 0)
                args[0] = EmitScalarArgumentFromEvidence(
                    StripExpectedAttributeCastWrappers(NormalizeNumericSinkArgumentCentral(args[0].Trim()), "FIELD_NUMERIC"),
                    "Number");
            if (args.Count > 1)
                args[1] = EmitScalarArgumentFromEvidence(
                    StripExpectedAttributeCastWrappers(NormalizeNumericSinkArgumentCentral(args[1].Trim()), "FIELD_NUMERIC"),
                    "Number");
            if (args.Count > 2)
                args[2] = EmitScalarArgumentFromEvidence(args[2].Trim(), "Text");
            if (args.Count > 3)
                args[3] = EmitScalarArgumentFromEvidence(args[3].Trim(), "Text");
            if (args.Count > 4)
                args[4] = EmitScalarArgumentFromEvidence(args[4].Trim(), "Text");

            return $"u.XMLInsert({string.Join(", ", args)})";
        });

        return TrackLegacyExpressionTreatmentIfChanged(
            "NormalizeType",
            nameof(NormalizeNestedKnownTextFunctionArgumentsCentral),
            expression,
            normalized);
    }

    private static string RewriteTextSinkCallsCentral(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        foreach (var functionName in new[]
                 {
                     "u.Trim", "Trim",
                     "u.Upper", "Upper",
                     "u.Lower", "Lower",
                     "u.LTrim", "LTrim",
                     "u.RTrim", "RTrim",
                     "u.Left", "Left",
                     "u.Right", "Right",
                     "u.Mid", "Mid",
                     "u.StrToken", "StrToken",
                     "u.StrTokenCnt", "StrTokenCnt",
                     "u.Translate", "Translate",
                     "u.Flip", "Flip",
                     "u.FileInfo", "FileInfo",
                     "u.StrBuild", "StrBuild",
                     "u.RepStr", "RepStr",
                     "u.InStr", "InStr",
                     "u.MailConnect", "MailConnect",
                     "ENV.Windows.OSCommand", "OSCommand",
                     "u.DragSetData", "DragSetData",
                     "TransmiteAux", "u.TransmiteAux"
                 })
        {
            expression = RewriteFunctionCalls(expression, functionName, args =>
            {
                for (var i = 0; i < args.Count; i++)
                    args[i] = RewriteTextSinkCallsCentral(args[i].Trim());

                foreach (var argIndex in GetTextArgumentIndexes(functionName, args.Count))
                    args[argIndex] = EmitScalarArgumentFromEvidence(args[argIndex], "Text");

                return $"{functionName}({string.Join(", ", args)})";
            });
        }

        return expression;
    }

    private static string NormalizeExpressionByAttributeCentral(TaskSemantic task, string? attr, string translated)
    {
        if (string.IsNullOrWhiteSpace(translated))
            return translated;

        translated = RewriteAncestorResourceMemberReferences(translated, task);
        translated = RewriteTextSinkCallsCentral(translated);
        translated = NormalizeTextFunctionInputsCentral(translated);
        translated = NormalizeFormattingFunctionInputsCentral(translated);
        translated = NormalizeNumericOperands(translated);
        translated = NormalizeConditionalByTypedBranchEvidence(translated);
        translated = RewriteSimpleBlobNullComparisons(translated, task);
        translated = RewriteSharedValGetComparisonOperands(translated);
        translated = RewriteSharedValGetByAttribute(attr, translated);
        translated = RewriteTypedGetterWrapperPatterns(attr, translated);
        translated = NormalizeVariantRuntimeFunctionArguments(translated);
        translated = NormalizeFormattingSurfaceCallsCentral(translated, task);
        translated = RewriteStaticUserMethodCalls(translated);
        translated = RewriteBooleanContextGetTextParamCalls(task, translated);

        if (string.Equals(attr, "A", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(attr, "U", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(translated.Trim(), "u.EditGet()", StringComparison.OrdinalIgnoreCase))
                return EmitScalarArgumentFromEvidence("u.EditGet()", "Text");

            if (IsVarCurrentLikeFunctionName(TryGetTopLevelFunctionName(translated)))
                return EmitScalarArgumentFromEvidence(translated, "Text");

            var normalized = NormalizeAlphaCaseExpression(translated);
            if (!string.Equals(normalized, translated, StringComparison.Ordinal))
                return EmitScalarArgumentFromEvidence(normalized, "Text");
        }

        if (string.Equals(attr, "N", StringComparison.OrdinalIgnoreCase))
        {
            var topLevelCall = TryGetTopLevelFunctionName(translated);
            if (IsTopLevelCall(topLevelCall, "u.Rights"))
                return EmitScalarArgumentFromEvidence(translated, "Number");
            translated = RestoreTextGetterInsideNumericValueWrappers(translated);
            translated = NormalizeNumericConditionalBindingExpression(translated);

            if ((TryParseFunctionCall(translated, out var numericFunctionName, out var numericArgs) &&
                 IsTopLevelCall(numericFunctionName, "u.If") &&
                 numericArgs.Count == 3) ||
                (TryParseFunctionCall(translated, out numericFunctionName, out numericArgs) &&
                 IsTopLevelCall(numericFunctionName, "u.CastToNumber") &&
                 numericArgs.Count == 1 &&
                 TryParseFunctionCall(numericArgs[0].Trim(), out var innerNumericFunctionName, out var innerNumericArgs) &&
                 IsTopLevelCall(innerNumericFunctionName, "u.If") &&
                 innerNumericArgs.Count == 3))
            {
                translated = EmitExpressionForContext(translated, task, CreateExpectedEmissionContext(ExpectedTypeForReturnType("Number")));
            }
        }

        if (string.Equals(attr, "D", StringComparison.OrdinalIgnoreCase))
            translated = NormalizeDateConditionalExpression(translated);

        if (string.Equals(attr, "T", StringComparison.OrdinalIgnoreCase))
        {
            translated = NormalizeTimeConditionalExpression(translated);
            if (SplitTopLevelArithmeticExpression(translated.Trim()) is not null)
                translated = EmitExpressionForContext(translated, task, CreateExpectedEmissionContext(ExpectedTypeForReturnType("Time")));
        }

        if (string.Equals(attr, "B", StringComparison.OrdinalIgnoreCase))
            translated = EmitExpressionForContext(translated, task, CreateBooleanConditionEmissionContext());

        return translated;
    }

    private static bool TryNormalizeWrappedVariantCaseExpressionCentral(string expression, out string normalizedExpression)
    {
        normalizedExpression = expression;
        if (!TryUnwrapNestedCastToTextExpressionCentral(expression, out var innerExpression))
            return false;
        if (!IsCaseExpressionWithVariantGet(innerExpression))
            return false;

        normalizedExpression = EmitScalarArgumentFromEvidence(
            NormalizeVariantCaseExpression(innerExpression),
            "Text");
        return true;
    }

    private static bool TryNormalizeWrappedAlphaCaseExpressionCentral(string expression, out string normalizedExpression)
    {
        normalizedExpression = expression;
        if (!TryUnwrapNestedCastToTextExpressionCentral(expression, out var innerExpression))
            return false;
        var normalizedInner = NormalizeAlphaCaseExpression(innerExpression);
        if (string.Equals(normalizedInner, innerExpression, StringComparison.Ordinal))
            return false;

        normalizedExpression = EmitScalarArgumentFromEvidence(
            normalizedInner,
            "Text");
        return true;
    }

    private static bool IsNullCallExpression(string expression)
        => IsTopLevelCall(TryGetTopLevelFunctionName(expression), "u.Null");

    private static bool IsTreeValueExpression(string expression)
        => IsTopLevelCall(TryGetTopLevelFunctionName(expression), "u.TreeValue");

    private static bool IsVecGetExpression(string expression)
        => IsTopLevelCall(TryGetTopLevelFunctionName(expression), "u.VecGet");

    private static bool IsDateLikeExpression(string expression)
    {
        var trimmed = expression.Trim();
        if (trimmed.StartsWith("u.ToDate(", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("u.CastToDate(", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("XPARuntimeCore.Box.Date.", StringComparison.Ordinal) ||
            trimmed.StartsWith("Date.", StringComparison.Ordinal))
            return true;

        return IsTopLevelCall(TryGetTopLevelFunctionName(trimmed), "Date");
    }

    private static bool IsTimeLikeExpression(string expression)
    {
        var trimmed = expression.Trim();
        if (trimmed.StartsWith("u.ToTime(", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("UserMethods.ToTime(", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("u.CastToTime(", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("XPARuntimeCore.Box.Time.", StringComparison.Ordinal) ||
            trimmed.StartsWith("Time.", StringComparison.Ordinal))
            return true;

        var topLevelCall = TryGetTopLevelFunctionName(trimmed);
        return IsTopLevelCall(topLevelCall, "Time") ||
               IsTopLevelCall(topLevelCall, "u.Time") ||
               IsTopLevelCall(topLevelCall, "u.AddTime");
    }

    private static string NormalizeTimeTypedExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        return expression
            .Replace("u.ToNumber(XPARuntimeCore.Box.Time.Now)", "Time.Now", StringComparison.OrdinalIgnoreCase)
            .Replace("u.ToNumber(Time.Now)", "Time.Now", StringComparison.OrdinalIgnoreCase)
            .Replace("u.ToNumber(u.Time())", "Time.Now", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeNumericTimeSourceExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (IsTimeLikeExpression(trimmed))
            return false;

        if (IsSimpleIdentifierPath(trimmed) || IsNumericLiteralExpressionCentral(trimmed))
            return true;

        if (SplitTopLevelArithmeticExpression(trimmed) is not null)
            return true;

        var topLevelCall = TryGetTopLevelFunctionName(trimmed);
        return IsTopLevelCall(topLevelCall, "u.Fix") ||
               IsTopLevelCall(topLevelCall, "Fix") ||
               IsTopLevelCall(topLevelCall, "u.Val") ||
               IsTopLevelCall(topLevelCall, "Val") ||
               IsTopLevelCall(topLevelCall, "u.ToNumber") ||
               IsTopLevelCall(topLevelCall, "ToNumber") ||
               IsTopLevelCall(topLevelCall, "u.CastToNumber") ||
               IsTopLevelCall(topLevelCall, "CastToNumber") ||
               IsTopLevelCall(topLevelCall, "u.GetNumberParam");
    }

    private static string NormalizeTimeFormattingArgumentCentral(string expression, TaskSemantic? task = null)
    {
        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeTimeFormattingArgumentCentral));
        var normalized = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(expression, "Time", task);
        if (TryParseFunctionCall(normalized.Trim(), out var functionName, out var args) &&
            args.Count == 1 &&
            (IsTopLevelCall(functionName, "u.ToTime") ||
             IsTopLevelCall(functionName, "UserMethods.ToTime") ||
             IsTopLevelCall(functionName, "u.CastToTime")))
        {
            var inner = args[0].Trim();
            var innerResolved = ResolveExpressionTypeFromEvidence(task, inner);
            if (string.Equals(GetValueReturnType(innerResolved.ReturnType), "Time", StringComparison.Ordinal))
                normalized = inner;
        }

        if (IsTimeLikeExpression(normalized))
            return normalized;

        if (!LooksLikeNumericTimeSourceExpression(expression))
            return normalized;

        var numericExpression = EmitScalarArgumentFromEvidence(
            StripExpectedAttributeCastWrappers(
                NormalizeNumericConditionalBranchExpressionCentral(expression.Trim()),
                "FIELD_NUMERIC"),
            "Number");
        return $"u.ToTime({numericExpression})";
    }

    private static bool IsCallDllExpression(string? functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return false;

        return functionName.StartsWith("u.CallDLL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumericCallDllExpression(string? functionName)
        => IsTopLevelCall(functionName, "u.CallDLL") || IsTopLevelCall(functionName, "u.CallDLLF");

    private static bool IsHttpPayloadExpression(string? functionName)
        => IsTopLevelCall(functionName, "u.HTTPCall") || IsTopLevelCall(functionName, "u.File2Blb");

    private static bool IsJavaInteropExpression(string? functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return false;

        return string.Equals(functionName, "u.JCall", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.JGet", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.JCallStatic", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "JavaCompat.JGetStatic", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVarCurrentLikeFunctionName(string? functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return false;

        return string.Equals(functionName, "u.VarCurr", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.VarCurrN", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "u.VarPrev", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSharedValGetExpression(string? functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return false;

        return string.Equals(functionName, "u.SharedValGet", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(functionName, "SharedValGet", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsObjectProducingExpression(string? functionName)
    {
        return IsSharedValGetExpression(functionName) ||
               IsJavaInteropExpression(functionName) ||
               IsTopLevelCall(functionName, "u.VariantGet") ||
               IsTopLevelCall(functionName, "u.XMLGet") ||
               IsTopLevelCall(functionName, "u.Rights") ||
               IsTopLevelCall(functionName, "JSONGet");
    }

    private static string RewriteSharedValGetText(string expression)
    {
        var rewritten = RewriteFunctionCalls(expression, "SharedValGet", args =>
            $"u.SharedValGetText({string.Join(", ", args)})");
        rewritten = RewriteFunctionCalls(rewritten, "u.SharedValGet", args =>
            $"u.SharedValGetText({string.Join(", ", args)})");
        return rewritten;
    }

    private static string ApplyAttributeCast(string expression, string? attrObj)
        => ApplyAttributeCastCentral(expression, attrObj);

    private static bool IsAttributeCastFunction(string functionName, string? attrObj)
    {
        return (attrObj ?? "").ToUpperInvariant() switch
        {
            "FIELD_NUMERIC" => IsTopLevelCall(functionName, "u.CastToNumber"),
            "FIELD_DATE" => IsTopLevelCall(functionName, "u.CastToDate"),
            "FIELD_TIME" => IsTopLevelCall(functionName, "u.CastToTime") || IsTopLevelCall(functionName, "UserMethods.ToTime"),
            "FIELD_ALPHA" => IsTopLevelCall(functionName, "u.CastToText"),
            "FIELD_BOOLEAN" or "FIELD_LOGICAL" => IsTopLevelCall(functionName, "u.CastToBool"),
            "FIELD_BLOB" => IsTopLevelCall(functionName, "u.CastToByteArray"),
            _ => false
        };
    }

    private static bool IsAnyAttributeCastFunction(string functionName)
    {
        return IsTopLevelCall(functionName, "u.CastToNumber") ||
               IsTopLevelCall(functionName, "u.CastToDate") ||
               IsTopLevelCall(functionName, "u.CastToTime") ||
               IsTopLevelCall(functionName, "UserMethods.ToTime") ||
               IsTopLevelCall(functionName, "u.CastToText") ||
               IsTopLevelCall(functionName, "u.CastToBool") ||
               IsTopLevelCall(functionName, "u.CastToByteArray");
    }

    private static string? TryGetTopLevelFunctionName(string expression)
    {
        return TryParseFunctionCall(expression, out var functionName, out _)
            ? functionName
            : null;
    }

    private static bool IsTopLevelCall(string? functionName, string expected)
        => !string.IsNullOrWhiteSpace(functionName) &&
           string.Equals(functionName, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsCaseExpressionWithVariantGet(string expression)
    {
        if (!TryParseFunctionCall(expression, out var functionName, out var args) ||
            (!string.Equals(functionName, "u.Case", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(functionName, "u.CaseUntyped", StringComparison.OrdinalIgnoreCase)))
            return false;

        return args.Any(ContainsVariantGetExpression);
    }

    private static bool TryNormalizeWrappedVariantCaseExpression(string expression, out string normalizedExpression)
        => TryNormalizeWrappedVariantCaseExpressionCentral(expression, out normalizedExpression);

    private static IEnumerable<int> GetTextArgumentIndexes(string functionName, int argCount)
    {
        if (argCount <= 0)
            yield break;

        if (ConsumesTextArgumentCentral(functionName))
            yield return 0;

        if ((string.Equals(functionName, "u.MailConnect", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "MailConnect", StringComparison.OrdinalIgnoreCase)) &&
            argCount > 1)
        {
            for (var i = 1; i < argCount; i++)
                yield return i;
        }

        if ((string.Equals(functionName, "u.StrBuild", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "StrBuild", StringComparison.OrdinalIgnoreCase)) &&
            argCount > 1)
        {
            for (var i = 0; i < argCount; i++)
                yield return i;
        }

        if ((string.Equals(functionName, "ENV.Windows.OSCommand", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "OSCommand", StringComparison.OrdinalIgnoreCase)) &&
            argCount > 0)
        {
            yield return 0;
        }

        if ((string.Equals(functionName, "u.RepStr", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "RepStr", StringComparison.OrdinalIgnoreCase)) &&
            argCount > 0)
        {
            for (var i = 0; i < Math.Min(argCount, 3); i++)
                yield return i;
        }

        if ((string.Equals(functionName, "u.InStr", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "InStr", StringComparison.OrdinalIgnoreCase)) &&
            argCount > 1)
        {
            yield return 0;
            yield return 1;
        }

        if ((string.Equals(functionName, "TransmiteAux", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "u.TransmiteAux", StringComparison.OrdinalIgnoreCase)) &&
            argCount > 2)
        {
            yield return 2;
        }
    }

    private static IEnumerable<int> GetNumericArgumentIndexes(string functionName, int argCount)
    {
        if (argCount <= 0)
            yield break;

        if ((string.Equals(functionName, "u.Left", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "Left", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "u.Right", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "Right", StringComparison.OrdinalIgnoreCase)) &&
            argCount > 1)
        {
            yield return 1;
        }

        if ((string.Equals(functionName, "u.Mid", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "Mid", StringComparison.OrdinalIgnoreCase)) &&
            argCount > 1)
        {
            yield return 1;
            if (argCount > 2)
                yield return 2;
        }

        if ((string.Equals(functionName, "u.Del", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "Del", StringComparison.OrdinalIgnoreCase)) &&
            argCount > 2)
        {
            yield return 1;
            yield return 2;
        }

        if ((string.Equals(functionName, "u.StrToken", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "StrToken", StringComparison.OrdinalIgnoreCase)) &&
            argCount > 1)
        {
            yield return 1;
        }

    }

    private static IEnumerable<int> GetTimeArgumentIndexes(string functionName, int argCount)
    {
        if (argCount <= 0)
            yield break;

        if ((string.Equals(functionName, "u.TStr", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "TStr", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "u.MTStr", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(functionName, "MTStr", StringComparison.OrdinalIgnoreCase)) &&
            argCount > 0)
        {
            yield return 0;
        }
    }

    private static bool TryUnwrapNestedCastToTextExpressionCentral(string expression, out string innerExpression)
    {
        innerExpression = expression.Trim();
        var unwrappedAny = false;

        while (TryUnwrapWholeFunctionEnvelope(innerExpression, "u.CastToText", out var inner))
        {
            innerExpression = inner;
            unwrappedAny = true;
        }

        return unwrappedAny && !string.IsNullOrWhiteSpace(innerExpression);
    }

    private static bool TryUnwrapWholeFunctionEnvelope(string expression, string functionName, out string innerExpression)
    {
        innerExpression = "";
        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(functionName))
            return false;

        var trimmed = expression.Trim();
        var prefix = functionName + "(";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !trimmed.EndsWith(")", StringComparison.Ordinal))
            return false;

        var depth = 0;
        for (var i = functionName.Length; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (ch == '(')
                depth++;
            else if (ch == ')')
                depth--;

            if (depth == 0 && i < trimmed.Length - 1)
                return false;
        }

        if (depth != 0)
            return false;

        innerExpression = trimmed[prefix.Length..^1].Trim();
        return !string.IsNullOrWhiteSpace(innerExpression);
    }

    private static string NormalizeAlphaCaseBranchCentral(string expression)
    {
        if (!TryParseFunctionCall(expression, out var functionName, out var args) || args.Count != 2)
            return expression.Trim();

        var valueExpr = args[0].Trim();
        var pictureExpr = args[1].Trim();
        if (!IsVarCurrentLikeExpression(valueExpr) || !IsVarPictureExpression(pictureExpr))
            return expression.Trim();

        if (string.Equals(functionName, "u.DStr", StringComparison.OrdinalIgnoreCase))
        {
            var coercedValue = EmitScalarArgumentFromEvidence(valueExpr, "Date");
            return $"u.DStr({coercedValue}, {pictureExpr})";
        }

        if (string.Equals(functionName, "u.Str", StringComparison.OrdinalIgnoreCase))
        {
            var coercedValue = EmitScalarArgumentFromEvidence(valueExpr, "Number");
            return $"u.Str({coercedValue}, {pictureExpr})";
        }

        return expression.Trim();
    }

    private static string NormalizeVariantCaseBranchCentral(string expression)
    {
        if (!TryParseFunctionCall(expression, out var functionName, out var args) || args.Count != 2)
            return expression.Trim();

        var valueExpr = args[0].Trim();
        var pictureExpr = args[1].Trim();
        if (!IsVariantGetExpression(valueExpr))
            return expression.Trim();

        if (string.Equals(functionName, "u.DStr", StringComparison.OrdinalIgnoreCase))
        {
            var coercedValue = EmitScalarArgumentFromEvidence(valueExpr, "Date");
            return $"u.DStr({coercedValue}, {pictureExpr})";
        }

        if (string.Equals(functionName, "u.Str", StringComparison.OrdinalIgnoreCase))
        {
            var coercedValue = EmitScalarArgumentFromEvidence(valueExpr, "Number");
            return $"u.Str({coercedValue}, {pictureExpr})";
        }

        return expression.Trim();
    }

    private static string NormalizeVariantCaseBranchForDiscriminatorCentral(string? discriminator, string expression)
    {
        var normalized = NormalizeVariantCaseBranchCentral(expression);
        var typeCode = NormalizeVariantDiscriminatorTypeCode(discriminator);
        if (typeCode is null)
            return normalized;

        return RewriteVariantGetTypeCodeCentral(normalized, typeCode.Value);
    }

    private static char? NormalizeVariantDiscriminatorTypeCode(string? discriminator)
    {
        if (string.IsNullOrWhiteSpace(discriminator))
            return null;

        if (!TryGetWholeCSharpStringLiteral(discriminator.Trim(), out var literal) ||
            string.IsNullOrWhiteSpace(literal))
            return null;

        var token = literal.Trim();
        if (token.Length != 1)
            return null;

        var typeCode = char.ToUpperInvariant(token[0]);
        return typeCode is 'A' or 'U' or 'N' or 'D' or 'T' or 'L' or 'B'
            ? typeCode
            : null;
    }

    private static string RewriteVariantGetTypeCodeCentral(string expression, char typeCode)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        return RewriteFunctionCalls(expression, "u.VariantGet", args =>
        {
            if (args.Count != 2)
                return null;

            var currentCode = args[1].Trim();
            var targetCodeLiteral = $"\"{typeCode}\"";
            if (string.Equals(currentCode, targetCodeLiteral, StringComparison.Ordinal))
                return null;

            return $"u.VariantGet({args[0].Trim()}, {targetCodeLiteral})";
        });
    }

    private static string NormalizeVariantRuntimeFunctionArgumentsCentral(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        expression = RewriteFunctionCalls(expression, "u.DStr", args =>
        {
            if (args.Count >= 1)
            {
                args[0] = NeedsVariantRuntimeCast(args[0])
                    ? CoerceScalarCallArgumentWithTypeEngineCentral(args[0].Trim(), "FIELD_DATE")
                    : EmitScalarArgumentFromEvidence(args[0].Trim(), "Date");
            }
            return $"u.DStr({string.Join(", ", args)})";
        });

        expression = RewriteFunctionCalls(expression, "u.TStr", args =>
        {
            if (args.Count >= 1)
            {
                args[0] = NeedsVariantRuntimeCast(args[0])
                    ? CoerceScalarCallArgumentWithTypeEngineCentral(args[0].Trim(), "FIELD_TIME")
                    : EmitScalarArgumentFromEvidence(args[0].Trim(), "Time");
            }
            return $"u.TStr({string.Join(", ", args)})";
        });

        expression = RewriteFunctionCalls(expression, "u.Str", args =>
        {
            if (args.Count >= 1)
            {
                args[0] = NeedsVariantRuntimeCast(args[0])
                    ? CoerceScalarCallArgumentWithTypeEngineCentral(args[0].Trim(), "FIELD_NUMERIC")
                    : EmitScalarArgumentFromEvidence(args[0].Trim(), "Number");
            }
            return $"u.Str({string.Join(", ", args)})";
        });

        expression = RewriteFunctionCalls(expression, "u.If", args =>
        {
            if (args.Count >= 1 && NeedsVariantRuntimeCast(args[0]))
                args[0] = EmitScalarArgumentFromEvidence(args[0].Trim(), "Bool");
            return $"u.If({string.Join(", ", args)})";
        });

        return expression;
    }

    private static string NormalizeFormattingSurfaceCallsCentral(string expression, TaskSemantic? task = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        expression = NormalizeResolvedConditionalBranchesCentral(expression, task);

        expression = RewriteFunctionCalls(expression, "u.DStr", args =>
        {
            if (args.Count >= 1)
                args[0] = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(args[0].Trim(), "Date", task);

            if (args.Count > 1)
                args[1] = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(args[1].Trim(), "Text", task);

            return $"u.DStr({string.Join(", ", args)})";
        });

        expression = RewriteFunctionCalls(expression, "u.TStr", args =>
        {
            if (args.Count >= 1)
                args[0] = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(args[0].Trim(), "Time", task);

            if (args.Count > 1)
                args[1] = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(args[1].Trim(), "Text", task);

            return $"u.TStr({string.Join(", ", args)})";
        });

        expression = RewriteFunctionCalls(expression, "u.MTStr", args =>
        {
            if (args.Count >= 1)
                args[0] = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(args[0].Trim(), "Number", task);

            if (args.Count > 1)
                args[1] = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(args[1].Trim(), "Text", task);

            return $"u.MTStr({string.Join(", ", args)})";
        });

        expression = RewriteFunctionCalls(expression, "u.Str", args =>
        {
            if (args.Count >= 1)
                args[0] = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(args[0].Trim(), "Number", task);

            if (args.Count > 1)
                args[1] = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(args[1].Trim(), "Text", task);

            return $"u.Str({string.Join(", ", args)})";
        });

        expression = RewriteFunctionCalls(expression, "u.StrBuild", args =>
        {
            if (args.Count <= 1)
                return null;

            for (var i = 1; i < args.Count; i++)
                args[i] = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(args[i].Trim(), "Text", task);

            return $"u.StrBuild({string.Join(", ", args)})";
        });

        return CollapseRedundantScalarCastWrappersDeep(expression);
    }

    private static string NormalizeResolvedConditionalBranchesCentral(string expression, TaskSemantic? task = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var rewritten = RewriteFunctionCalls(expression, "u.If", args =>
        {
            if (args.Count != 3)
                return null;

            var condition = args[0].Trim();
            var whenTrueSource = args[1].Trim();
            var whenFalseSource = args[2].Trim();

            if (NeedsVariantRuntimeCast(condition))
                condition = EmitScalarArgumentFromEvidence(condition, "Bool", task);

            if (!TryResolveConditionalBranchScalarReturnTypeCentral(whenTrueSource, whenFalseSource, task, out var branchReturnType))
                return $"u.If({condition}, {whenTrueSource}, {whenFalseSource})";

            if (string.Equals(branchReturnType, "Number", StringComparison.Ordinal))
            {
                whenTrueSource = UnwrapNumericTextBranchForCastToNumber(whenTrueSource, task);
                whenFalseSource = UnwrapNumericTextBranchForCastToNumber(whenFalseSource, task);
            }

            var whenTrue = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(whenTrueSource, branchReturnType, task);
            var whenFalse = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(whenFalseSource, branchReturnType, task);
            return $"u.If({condition}, {whenTrue}, {whenFalse})";
        });
        return TrackLegacyExpressionTreatmentIfChanged(
            "NormalizeType",
            nameof(NormalizeResolvedConditionalBranchesCentral),
            expression,
            rewritten);
    }

    private static bool TryResolveConditionalBranchScalarReturnTypeCentral(
        string whenTrue,
        string whenFalse,
        TaskSemantic? task,
        out string branchReturnType)
    {
        branchReturnType = "";

        var trueType = ResolveConditionalBranchScalarReturnTypeTokenCentral(whenTrue, task);
        var falseType = ResolveConditionalBranchScalarReturnTypeTokenCentral(whenFalse, task);
        var trueIsNull = IsConditionalNullBranchExpressionCentral(whenTrue);
        var falseIsNull = IsConditionalNullBranchExpressionCentral(whenFalse);

        if (!string.IsNullOrWhiteSpace(trueType) &&
            string.Equals(trueType, falseType, StringComparison.Ordinal))
        {
            branchReturnType = trueType;
            return true;
        }

        if (trueIsNull && !string.IsNullOrWhiteSpace(falseType))
        {
            branchReturnType = falseType;
            return true;
        }

        if (falseIsNull && !string.IsNullOrWhiteSpace(trueType))
        {
            branchReturnType = trueType;
            return true;
        }

        if (CanNormalizeCastToNumberBranchAsNumber(whenTrue, trueType, task) &&
            CanNormalizeCastToNumberBranchAsNumber(whenFalse, falseType, task))
        {
            branchReturnType = "Number";
            return true;
        }

        if (IsNumericLikeScalarReturnTypeCentral(trueType) &&
            IsNumericLikeScalarReturnTypeCentral(falseType))
        {
            branchReturnType = "Number";
            return true;
        }

        if ((string.Equals(trueType, "Text", StringComparison.Ordinal) && falseIsNull) ||
            (string.Equals(falseType, "Text", StringComparison.Ordinal) && trueIsNull))
        {
            branchReturnType = "Text";
            return true;
        }

        return false;
    }

    private static string ResolveConditionalBranchScalarReturnTypeTokenCentral(string expression, TaskSemantic? task)
    {
        var trimmed = StripRedundantOuterParentheses(expression?.Trim() ?? "");
        if (trimmed.Length == 0 || IsConditionalNullBranchExpressionCentral(trimmed))
            return "";

        var resolved = ResolveExpressionTypeFromEvidence(task, trimmed);
        var resolvedReturnType = GetValueReturnType(resolved.ReturnType);
        if (IsSupportedConditionalScalarReturnTypeCentral(resolvedReturnType))
            return resolvedReturnType;

        if (CanNormalizeCastToNumberBranchAsNumber(trimmed, resolvedReturnType, task))
            return "Number";

        if (task is not null)
        {
            var inferred = ResolveExpectedTypeFromExpressionEvidence(task, trimmed);
            var inferredReturnType = GetValueReturnType(inferred.ReturnType);
            if (IsSupportedConditionalScalarReturnTypeCentral(inferredReturnType))
                return inferredReturnType;

            var inferredByAttr = MapAttrObjToReturnType(inferred.AttrObj);
            if (IsSupportedConditionalScalarReturnTypeCentral(inferredByAttr))
                return inferredByAttr;
        }

        var declared = NormalizeReturnTypeToken(ResolveExpressionReturnType(null, trimmed, task));
        var declaredValueType = GetValueReturnType(declared);
        if (IsSupportedConditionalScalarReturnTypeCentral(declaredValueType))
            return declaredValueType;

        return "";
    }

    private static bool IsConditionalNullBranchExpressionCentral(string expression)
    {
        var trimmed = StripRedundantOuterParentheses(expression?.Trim() ?? "");
        return string.Equals(trimmed, "null", StringComparison.Ordinal) ||
               IsNullCallExpression(trimmed);
    }

    private static bool IsNumericLikeScalarReturnTypeCentral(string? returnType)
        => string.Equals(returnType, "Number", StringComparison.Ordinal) ||
           string.Equals(returnType, "Bool", StringComparison.Ordinal);

    private static bool IsSupportedConditionalScalarReturnTypeCentral(string? returnType)
        => string.Equals(returnType, "Text", StringComparison.Ordinal) ||
           string.Equals(returnType, "Number", StringComparison.Ordinal) ||
           string.Equals(returnType, "Date", StringComparison.Ordinal) ||
           string.Equals(returnType, "Time", StringComparison.Ordinal) ||
           string.Equals(returnType, "Bool", StringComparison.Ordinal) ||
           string.Equals(returnType, "byte[]", StringComparison.Ordinal);

    private static string RewriteBooleanContextGetTextParamCallsCentral(TaskSemantic task, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        return RewriteFunctionCalls(expression, "u.If", args =>
        {
            if (args.Count != 3)
                return null;

            var condition = RewriteBooleanOperandCentral(task, args[0]);
            return $"u.If({condition}, {args[1]}, {args[2]})";
        });
    }

    private static string RestoreTextGetterInsideNumericValueWrappersCentral(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        return RewriteFunctionCalls(expression, "u.Val", args =>
        {
            if (args.Count == 0)
                return null;

            args[0] = RewriteFunctionCalls(args[0], "u.GetNumberParam", innerArgs => $"u.GetTextParam({string.Join(", ", innerArgs)})");
            args[0] = RewriteFunctionCalls(args[0], "u.CastToNumber", innerArgs =>
            {
                if (innerArgs.Count != 1)
                    return null;

                var candidate = innerArgs[0].Trim();
                return TryGetWholeCSharpStringLiteral(candidate, out _)
                    ? candidate
                    : null;
            });
            return $"u.Val({string.Join(", ", args)})";
        });
    }

    private static bool TryRewriteBooleanTextComparisonCentral(
        TaskSemantic task,
        string left,
        string @operator,
        string right,
        out string rewritten)
    {
        rewritten = "";
        if (!string.Equals(@operator, "==", StringComparison.Ordinal) &&
            !string.Equals(@operator, "!=", StringComparison.Ordinal))
            return false;

        var emptyOnRight = IsWholeCSharpEmptyStringLiteral(right);
        var emptyOnLeft = IsWholeCSharpEmptyStringLiteral(left);
        if (!emptyOnRight && !emptyOnLeft)
            return false;

        var wrappedSide = emptyOnRight ? left : right;
        var comparisonSide = emptyOnRight ? right : left;
        var root = UnwrapBooleanTextComparisonRoot(wrappedSide);
        var resolvedRootType = ResolveExpressionXpaType(task, root);
        var booleanSplit = SplitTopLevelBooleanBinaryExpression(root);
        if (booleanSplit is null)
        {
            var actual = ResolveExpectedTypeFromExpressionEvidence(task, root);
            var isNonTextScalarByType =
                resolvedRootType == XpaType.Number ||
                resolvedRootType == XpaType.Date ||
                resolvedRootType == XpaType.Time;
            if (!IsBooleanLikeBooleanOperandCentral(task, root) &&
                ((actual.HasExpectation &&
                  !IsTextLikeBooleanOperandCentral(task, root, actual)) ||
                 isNonTextScalarByType))
            {
                var normalizedScalarText = EmitExpressionForExpectedType(root, task, ExpectedTypeForReturnType("Text"));
                rewritten = $"({normalizedScalarText} {@operator} {comparisonSide})";
                return true;
            }

            return false;
        }

        if (!string.Equals(booleanSplit.Value.Operator, "&&", StringComparison.Ordinal))
            return false;

        var leftOperand = booleanSplit.Value.Left.Trim();
        var rightOperand = booleanSplit.Value.Right.Trim();
        var leftActual = ResolveExpectedTypeFromExpressionEvidence(task, leftOperand);
        var rightActual = ResolveExpectedTypeFromExpressionEvidence(task, rightOperand);
        var leftBooleanLike = IsBooleanLikeBooleanOperandCentral(task, leftOperand);
        var rightBooleanLike = IsBooleanLikeBooleanOperandCentral(task, rightOperand);

        var leftIsText = leftBooleanLike ? false : IsTextLikeBooleanOperandCentral(task, leftOperand, leftActual);
        var rightIsText = rightBooleanLike ? false : IsTextLikeBooleanOperandCentral(task, rightOperand, rightActual);
        if (leftIsText == rightIsText)
            return false;

        var textExpr = leftIsText ? leftOperand : rightOperand;
        var boolExpr = leftIsText ? rightOperand : leftOperand;

        var normalizedBool = RewriteBooleanOperandCentral(task, boolExpr);
        var normalizedText = EmitExpressionForExpectedType(textExpr, task, ExpectedTypeForReturnType("Text"));
        rewritten = $"(({normalizedBool}) && ({normalizedText} {@operator} {comparisonSide}))";
        return true;
    }

    private static string? TryRewriteDnCastCentral(string valueExpr, string typeExpr)
    {
        if (string.IsNullOrWhiteSpace(valueExpr) || string.IsNullOrWhiteSpace(typeExpr))
            return null;

        var normalizedType = typeExpr.Trim();
        if (normalizedType.StartsWith("global::", StringComparison.OrdinalIgnoreCase))
            normalizedType = normalizedType["global::".Length..];
        if (normalizedType.StartsWith("DotNet.", StringComparison.OrdinalIgnoreCase))
            normalizedType = normalizedType["DotNet.".Length..];

        if (normalizedType.EndsWith(".String", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "String", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "DotNet.System.String", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "System.String", StringComparison.OrdinalIgnoreCase))
        {
            return EmitScalarArgumentFromEvidence(valueExpr, "Text");
        }

        if (normalizedType.EndsWith(".Boolean", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "Boolean", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "DotNet.System.Boolean", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "System.Boolean", StringComparison.OrdinalIgnoreCase))
        {
            return EmitScalarArgumentFromEvidence(valueExpr, "Bool");
        }

        if (normalizedType.EndsWith(".Int32", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "Int32", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "DotNet.System.Int32", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "System.Int32", StringComparison.OrdinalIgnoreCase))
        {
            var coerced = EmitScalarArgumentFromEvidence(valueExpr, "Number");
            return $"((int){coerced})";
        }

        if (normalizedType.EndsWith(".DateTime", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "DateTime", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "DotNet.System.DateTime", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "System.DateTime", StringComparison.OrdinalIgnoreCase))
        {
            return valueExpr.Trim();
        }

        return null;
    }

    private static string EmitExpressionForContext(
        string code,
        TaskSemantic task,
        ExpressionEmissionContext context)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        var normalized = code.Trim();
        if (TryEmitThroughStrictEmittedExpression(normalized, task, context, out var strictEmitted))
            return strictEmitted;

        ConversionTelemetry.Log(
            "EMITTED_EXPR_UNRESOLVED",
            string.Create(
                CultureInfo.InvariantCulture,
                $"task={task.Ordinal} sink={context.SinkKind} expected={QuoteTelemetry(ResolveReturnTypeForExpectedContext(context.Expected))} expr={QuoteTelemetry(TruncateTelemetryValue(normalized))}"));
        return normalized;
    }

    private static string GetExpressionContextTargetMemberCacheKey(ExpressionEmissionContext context)
    {
        if (context.TargetInfo is not TargetValueInfo targetInfo)
            return "";

        if (!targetInfo.IsBlob && !targetInfo.IsArray && !targetInfo.IsDotNet)
            return "";

        return targetInfo.TargetMember ?? "";
    }

    private static string GetExpressionContextBlobTargetCacheKey(ExpressionEmissionContext context)
    {
        if (context.TargetInfo is not TargetValueInfo targetInfo)
            return "";

        if (!targetInfo.IsBlob && !targetInfo.IsArray)
            return "";

        return context.BlobTarget ?? "";
    }

    private static string ApplyRequiredExplicitSurfaceCastForExpectedType(string expression, ExpectedTypeContext expected, TaskSemantic? task = null)
    {
        if (string.IsNullOrWhiteSpace(expression) || !expected.HasExpectation)
            return expression;

        var trimmed = expression.Trim();
        var expectedXpaType = ResolveExpectedXpaType(expected);
        if (expectedXpaType == XpaType.Unknown)
            return trimmed;

        if (TryParseFunctionCall(trimmed, out var castName, out var castArgs) &&
            castArgs.Count == 1)
        {
            var castInner = castArgs[0].Trim();
            if (expectedXpaType == XpaType.Text &&
                IsTopLevelCall(castName, "u.ByteArrayToText") &&
                IsRegisteredTypedTextExpression(task, castInner))
                return castInner;

            if (expectedXpaType == XpaType.Blob &&
                IsTopLevelCall(castName, "u.CastToByteArray") &&
                IsRegisteredTypedClrObjectExpression(task, castInner))
                return castInner;

            if (IsScalarCastFunctionName(castName, expectedXpaType) ||
                (expectedXpaType == XpaType.Blob && IsTopLevelCall(castName, "u.CastToByteArray")) ||
                (expectedXpaType == XpaType.Text && IsTopLevelCall(castName, "u.ByteArrayToText")))
                return trimmed;
        }

        var valueReturnType = GetValueReturnType(expected.ReturnType);
        var expectedAttrObj = !string.IsNullOrWhiteSpace(expected.AttrObj)
            ? expected.AttrObj
            : MapReturnTypeToAttrObj(valueReturnType);
        var expectedReturnType = !string.IsNullOrWhiteSpace(valueReturnType)
            ? valueReturnType
            : MapAttrObjToReturnType(expectedAttrObj);
        if (IsKnownExpressionReturnTypeCompatible(trimmed, expectedReturnType, task))
            return trimmed;

        var topLevelCall = TryGetTopLevelFunctionName(trimmed);
        var actualXpaType = task is not null
            ? ResolveExpressionXpaType(task, trimmed)
            : ResolveExpressionXpaTypeWithoutTask(trimmed);
        var isUnknownSurfaceCall =
            TryParseFunctionCall(trimmed, out _, out _) &&
            actualXpaType == XpaType.Unknown;
        var requiresExplicitSurfaceCast =
            IsRuntimeUntypedScalarExpression(topLevelCall) ||
            IsObjectProducingExpression(topLevelCall) ||
            IsTopLevelCall(topLevelCall, "u.FileInfo") ||
            IsTopLevelCall(topLevelCall, "u.ClientFileInfo") ||
            isUnknownSurfaceCall;

        if (!requiresExplicitSurfaceCast &&
            !(IsTopLevelCall(topLevelCall, "u.GetTextParam") &&
              !string.Equals(valueReturnType, "Text", StringComparison.Ordinal) &&
              !string.Equals(expectedAttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase)))
            return trimmed;

        if (!string.IsNullOrWhiteSpace(expectedAttrObj))
        {
            var normalizedForAttr = StripRedundantOuterParentheses(trimmed.Trim());
            if (!string.Equals(normalizedForAttr, trimmed, StringComparison.Ordinal))
                trimmed = normalizedForAttr;

            var rewrittenGetter = NormalizeParameterGetterForAttributeCentral(trimmed, expectedAttrObj);
            if (!string.Equals(rewrittenGetter, trimmed, StringComparison.Ordinal))
                return rewrittenGetter;

            if (string.Equals(NormalizeAttrObjKind(expectedAttrObj), "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
                TryNormalizeBlobWrappedNewClrExpression(trimmed, out var newClrExpression))
                return newClrExpression;

            if (string.Equals(NormalizeAttrObjKind(expectedAttrObj), "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
                TryResolveNewClrExpressionReturnType(trimmed, out _))
                return StripRedundantOuterParentheses(trimmed);

            if (string.Equals(NormalizeAttrObjKind(expectedAttrObj), "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
                return NormalizeByteArrayExpectedExpression(trimmed);

            return ApplyAttributeCast(trimmed, expectedAttrObj);
        }

        return EmitScalarArgumentFromEvidence(trimmed, valueReturnType, task);
    }

    private static bool IsRegisteredTypedTextExpression(TaskSemantic? task, string expression)
    {
        if (task is null || string.IsNullOrWhiteSpace(expression))
            return false;

        var normalized = StripRedundantOuterParentheses(expression.Trim());
        if (!TryResolveRegisteredTypedExpressionInfo(task, normalized, out var typeInfo))
            return false;

        return typeInfo.XpaType == XpaType.Text ||
               string.Equals(typeInfo.ReturnType, "Text", StringComparison.Ordinal);
    }

    private static bool IsRegisteredTypedClrObjectExpression(TaskSemantic? task, string expression)
    {
        if (task is null || string.IsNullOrWhiteSpace(expression))
            return false;

        var normalized = StripRedundantOuterParentheses(expression.Trim());
        if (!TryResolveRegisteredTypedExpressionInfo(task, normalized, out var typeInfo))
            return false;

        if (typeInfo.XpaType == XpaType.Blob ||
            string.Equals(typeInfo.ReturnType, "byte[]", StringComparison.Ordinal))
            return false;

        return typeInfo.IsObjectLike || IsClrObjectReturnTypeForTypedExpression(typeInfo.ReturnType);
    }

    private static XpaType ResolveExpectedXpaTypeForContext(ExpressionEmissionContext context)
    {
        var attrObj = !string.IsNullOrWhiteSpace(context.Expected.AttrObj)
            ? context.Expected.AttrObj
            : MapReturnTypeToAttrObj(GetValueReturnType(context.Expected.ReturnType));
        var valueReturnType = ResolveScalarReturnTypeForContext(context, attrObj);
        return XpaTypeEngine.MapExpectedToXpaType(valueReturnType);
    }

    private static XpaType ResolveExpectedXpaType(ExpectedTypeContext expected)
    {
        var valueReturnType = GetValueReturnType(expected.ReturnType);
        if (!string.IsNullOrWhiteSpace(valueReturnType))
            return XpaTypeEngine.MapExpectedToXpaType(valueReturnType);

        return XpaTypeEngine.MapExpectedToXpaType(MapAttrObjToReturnType(expected.AttrObj));
    }

    private static XpaType ResolveExpressionXpaType(TaskSemantic task, string expression)
    {
        if (!string.IsNullOrWhiteSpace(expression) &&
            TryResolveRegisteredTypedExpressionInfo(task, StripRedundantOuterParentheses(expression.Trim()), out var registered) &&
            registered.IsResolved)
            return registered.XpaType;

        if (TryResolveKnownExpressionReturnTypeWithoutLegacy(task, expression, out var knownReturnType))
            return XpaTypeEngine.MapExpectedToXpaType(knownReturnType);

        var resolved = ResolveExpressionTypeFromEvidence(task, expression);
        if (resolved.IsResolved)
            return resolved.XpaType;

        return XpaType.Unknown;
    }

    private static XpaType ResolveExpressionXpaTypeWithoutTask(string expression)
    {
        if (TryResolveKnownExpressionReturnTypeWithoutLegacy(null, expression, out var returnType))
            return XpaTypeEngine.MapExpectedToXpaType(returnType);

        var resolved = ResolveExpressionTypeFromEvidence(null, expression);
        return resolved.IsResolved ? resolved.XpaType : XpaType.Unknown;
    }

    private static XpaType ResolveXpaType(ExpectedTypeContext expected)
    {
        var valueReturnType = GetValueReturnType(expected.ReturnType);
        if (!string.IsNullOrWhiteSpace(valueReturnType))
            return XpaTypeEngine.MapExpectedToXpaType(valueReturnType);

        return XpaTypeEngine.MapExpectedToXpaType(MapAttrObjToReturnType(expected.AttrObj));
    }

    private static string NormalizeNestedConditionalsForContext(string expression, ExpressionEmissionContext context)
    {
        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeNestedConditionalsForContext));
        if (string.IsNullOrWhiteSpace(expression))
            return expression;
        if (!ShouldNormalizeConditionalExpressions(expression))
            return expression;

        var attrObj = !string.IsNullOrWhiteSpace(context.Expected.AttrObj)
            ? context.Expected.AttrObj
            : MapReturnTypeToAttrObj(GetValueReturnType(context.Expected.ReturnType));
        if (string.IsNullOrWhiteSpace(attrObj))
            return expression;

        var valueReturnType = ResolveScalarReturnTypeForContext(context, attrObj);
        var wrapConditionalBranchesAsLambda = ShouldWrapConditionalBranchesAsLambdaForContext(context);
        var branchContext = context with
        {
            Expected = new ExpectedTypeContext(attrObj, valueReturnType, false)
        };

        if (TryNormalizeConditionalRootForContext(expression, branchContext, attrObj, valueReturnType, wrapConditionalBranchesAsLambda, out var normalizedRoot))
            return normalizedRoot;

        var current = expression;
        for (var i = 0; i < 2; i++)
        {
            var rewritten = RewriteFunctionCallsInnermost(current, "u.If", args =>
            {
                if (args.Count != 3)
                    return null;

                var condition = args[0].Trim();
                var whenTrueSource = NormalizeNestedConditionalsForContext(args[1].Trim(), branchContext);
                var whenFalseSource = NormalizeNestedConditionalsForContext(args[2].Trim(), branchContext);
                var whenTrue = NormalizeConditionalBranchForContext(whenTrueSource, attrObj, valueReturnType, wrapConditionalBranchesAsLambda);
                var whenFalse = NormalizeConditionalBranchForContext(whenFalseSource, attrObj, valueReturnType, wrapConditionalBranchesAsLambda);
                return $"u.If({condition}, {whenTrue}, {whenFalse})";
            });

            if (string.Equals(rewritten, current, StringComparison.Ordinal))
                break;

            current = rewritten;
        }

        return current;
    }

    private static string NormalizeWrappedConditionalsForContext(string expression, string attrObj, string valueReturnType, bool wrapConditionalBranchesAsLambda = false)
    {
        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeWrappedConditionalsForContext));
        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(attrObj))
            return expression;
        if (!ShouldNormalizeWrappedConditionalExpressions(expression, attrObj))
            return expression;

        var castFunctionName = GetExpectedAttrCastFunctionName(attrObj);
        if (string.IsNullOrWhiteSpace(castFunctionName))
            return expression;

        var current = expression;
        for (var i = 0; i < 2; i++)
        {
            var rewritten = RewriteFunctionCallsInnermost(current, castFunctionName, args =>
            {
                if (args.Count != 1)
                    return null;

                var root = StripExpectedAttributeCastWrappers(args[0].Trim(), attrObj);
                if (!TryParseFunctionCall(root, out var functionName, out var conditionalArgs) ||
                    !IsTopLevelCall(functionName, "u.If") ||
                    conditionalArgs.Count != 3)
                    return null;

                var condition = conditionalArgs[0].Trim();
                var whenTrue = NormalizeConditionalBranchForContext(conditionalArgs[1].Trim(), attrObj, valueReturnType, wrapConditionalBranchesAsLambda);
                var whenFalse = NormalizeConditionalBranchForContext(conditionalArgs[2].Trim(), attrObj, valueReturnType, wrapConditionalBranchesAsLambda);
                return $"u.If({condition}, {whenTrue}, {whenFalse})";
            });

            if (string.Equals(rewritten, current, StringComparison.Ordinal))
                break;

            current = rewritten;
        }

        return current;
    }

    private static bool TryNormalizeConditionalRootForContext(
        string expression,
        ExpressionEmissionContext branchContext,
        string attrObj,
        string valueReturnType,
        bool wrapConditionalBranchesAsLambda,
        out string normalized)
    {
        normalized = expression;
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var root = StripExpectedAttributeCastWrappers(expression.Trim(), attrObj);
        if (!TryParseFunctionCall(root, out var functionName, out var args) ||
            !IsTopLevelCall(functionName, "u.If") ||
            args.Count != 3)
            return false;

        var condition = args[0].Trim();
        var whenTrueSource = NormalizeNestedConditionalsForContext(args[1].Trim(), branchContext);
        var whenFalseSource = NormalizeNestedConditionalsForContext(args[2].Trim(), branchContext);
        var whenTrue = NormalizeConditionalBranchForContext(whenTrueSource, attrObj, valueReturnType, wrapConditionalBranchesAsLambda);
        var whenFalse = NormalizeConditionalBranchForContext(whenFalseSource, attrObj, valueReturnType, wrapConditionalBranchesAsLambda);
        normalized = $"u.If({condition}, {whenTrue}, {whenFalse})";
        return true;
    }

    private static string ResolveScalarReturnTypeForContext(ExpressionEmissionContext context, string attrObj)
    {
        var valueReturnType = GetValueReturnType(context.Expected.ReturnType);
        if (!string.IsNullOrWhiteSpace(valueReturnType))
            return valueReturnType;

        return MapAttrObjToReturnType(attrObj);
    }

    private static bool ShouldWrapConditionalBranchesAsLambdaForContext(ExpressionEmissionContext context)
    {
        var normalized = NormalizeReturnTypeToken(context.Expected.ReturnType);
        return normalized.StartsWith("Func<Text>", StringComparison.Ordinal);
    }

    private static string NormalizeConditionalBranchForContext(string expression, string attrObj, string valueReturnType, bool wrapConditionalBranchesAsLambda = false)
    {
        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeConditionalBranchForContext));
        var trimmed = expression.Trim();
        if (trimmed.StartsWith("() =>", StringComparison.Ordinal))
        {
            var body = trimmed["() =>".Length..].Trim();
            var normalizedBody = NormalizeConditionalBranchForContext(body, attrObj, valueReturnType, wrapConditionalBranchesAsLambda);
            return normalizedBody.TrimStart().StartsWith("() =>", StringComparison.Ordinal)
                ? normalizedBody
                : $"() => {normalizedBody}";
        }
        if (!ShouldNormalizeConditionalExpressions(trimmed) &&
            !ShouldNormalizeWrappedConditionalExpressions(trimmed, attrObj))
        {
            var direct = ApplyAttributeCast(expression, attrObj);
            if (string.IsNullOrWhiteSpace(direct) ||
                direct.TrimStart().StartsWith("() =>", StringComparison.Ordinal) ||
                !ShouldWrapConditionalBranchAsLambda(expression, attrObj, valueReturnType, wrapConditionalBranchesAsLambda))
                return direct;
            return $"() => {direct}";
        }

        var strippedRoot = StripExpectedAttributeCastWrappers(expression.Trim(), attrObj);
        if (TryParseFunctionCall(strippedRoot, out var conditionalName, out var conditionalArgs) &&
            conditionalArgs.Count == 3 &&
            IsTopLevelCall(conditionalName, "u.If"))
        {
            var condition = conditionalArgs[0].Trim();
            var whenTrueSource = conditionalArgs[1].Trim();
            var whenFalseSource = conditionalArgs[2].Trim();
            var whenTrue = NormalizeConditionalBranchForContext(whenTrueSource, attrObj, valueReturnType, wrapConditionalBranchesAsLambda);
            var whenFalse = NormalizeConditionalBranchForContext(whenFalseSource, attrObj, valueReturnType, wrapConditionalBranchesAsLambda);
            var normalizedConditional = $"u.If({condition}, {whenTrue}, {whenFalse})";
            if (ShouldWrapConditionalBranchAsLambda(expression, attrObj, valueReturnType, wrapConditionalBranchesAsLambda))
                return $"() => {normalizedConditional}";
            return normalizedConditional;
        }

        var normalized = ApplyAttributeCast(expression, attrObj);
        if (string.IsNullOrWhiteSpace(normalized))
            return normalized;

        if (normalized.TrimStart().StartsWith("() =>", StringComparison.Ordinal))
            return normalized;

        if (!ShouldWrapConditionalBranchAsLambda(expression, attrObj, valueReturnType, wrapConditionalBranchesAsLambda))
            return normalized;

        return $"() => {normalized}";
    }

    private static bool ShouldNormalizeConditionalExpressions(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression) ||
            !expression.Contains("u.If(", StringComparison.Ordinal))
            return false;

        return ContainsPostContextConditionalEvidence(expression);
    }

    private static bool ShouldNormalizeWrappedConditionalExpressions(string expression, string attrObj)
    {
        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(attrObj))
            return false;

        var castFunctionName = GetExpectedAttrCastFunctionPrefix(attrObj);
        return !string.IsNullOrWhiteSpace(castFunctionName) &&
               expression.Contains(castFunctionName, StringComparison.Ordinal) &&
               expression.Contains("u.If(", StringComparison.Ordinal) &&
               ContainsPostContextConditionalEvidence(expression);
    }

    private static bool ContainsPostContextConditionalEvidence(string expression)
    {
        return expression.Contains("u.FileInfo(", StringComparison.Ordinal) ||
               expression.Contains("u.ClientFileInfo(", StringComparison.Ordinal) ||
               expression.Contains("u.SharedValGet(", StringComparison.Ordinal) ||
               expression.Contains("SharedValGet(", StringComparison.Ordinal) ||
               expression.Contains("u.VariantGet(", StringComparison.Ordinal) ||
               expression.Contains("JSONGet(", StringComparison.Ordinal) ||
               expression.Contains("u.JGet(", StringComparison.Ordinal) ||
               expression.Contains("u.JCall(", StringComparison.Ordinal) ||
               expression.Contains("u.JCallStatic(", StringComparison.Ordinal) ||
               expression.Contains("JavaCompat.JGetStatic(", StringComparison.Ordinal);
    }

    private static bool ShouldWrapConditionalBranchAsLambda(string expression, string attrObj, string valueReturnType, bool wrapConditionalBranchesAsLambda)
    {
        if (!wrapConditionalBranchesAsLambda)
            return false;

        if (string.IsNullOrWhiteSpace(expression))
            return false;

        if (string.IsNullOrWhiteSpace(valueReturnType) ||
            string.Equals(valueReturnType, "object", StringComparison.Ordinal) ||
            valueReturnType.EndsWith("[]", StringComparison.Ordinal) ||
            !string.Equals(valueReturnType, "Text", StringComparison.Ordinal))
            return false;

        var root = StripExpectedAttributeCastWrappers(expression.Trim(), attrObj);
        var topLevelCall = TryGetTopLevelFunctionName(root);
        if (string.IsNullOrWhiteSpace(topLevelCall))
            return false;

        return IsTopLevelCall(topLevelCall, "u.If") ||
               IsTopLevelCall(topLevelCall, "u.FileInfo") ||
               IsTopLevelCall(topLevelCall, "u.ClientFileInfo") ||
               IsObjectProducingExpression(topLevelCall);
    }

    private static string StripExpectedAttributeCastWrappers(string expression, string attrObj)
    {
        var current = expression;
        for (var i = 0; i < 4; i++)
        {
            if (!TryParseFunctionCall(current, out var functionName, out var args) || args.Count != 1)
                break;

            if (!IsExpectedAttrCastFunction(functionName, attrObj))
                break;

            current = args[0].Trim();
        }

        return current;
    }

    private static string CollapseRedundantScalarCastWrappers(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var current = expression.Trim();
        for (var i = 0; i < 8; i++)
        {
            var collapsed = false;
            foreach (var functionName in new[] { "u.CastToText", "u.CastToNumber", "u.CastToDate", "u.CastToTime", "u.CastToBool", "u.CastToByteArray", "u.ByteArrayToText" })
            {
                if (!TryUnwrapWholeFunctionEnvelope(current, functionName, out var outerInner))
                    continue;
                if (!TryUnwrapWholeFunctionEnvelope(outerInner, functionName, out var innerInner))
                    continue;

                current = $"{functionName}({innerInner})";
                collapsed = true;
                break;
            }

            if (!collapsed)
                break;
        }

        return current;
    }

    private static string CollapseRedundantScalarCastWrappersDeep(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression) ||
            expression.IndexOf("CastTo", StringComparison.Ordinal) < 0 &&
            expression.IndexOf("ByteArrayToText", StringComparison.Ordinal) < 0)
            return expression;

        var current = expression.Trim();
        for (var pass = 0; pass < 4; pass++)
        {
            var rewritten = current;
            foreach (var functionName in new[] { "u.CastToText", "u.CastToNumber", "u.CastToDate", "u.CastToTime", "u.CastToBool", "u.CastToByteArray", "u.ByteArrayToText" })
            {
                rewritten = RewriteFunctionCallsInnermost(rewritten, functionName, args =>
                {
                    if (args.Count != 1)
                        return $"{functionName}({string.Join(", ", args)})";

                    var inner = CollapseRedundantScalarCastWrappers(args[0].Trim());
                    if (TryUnwrapWholeFunctionEnvelope(inner, functionName, out var nestedInner))
                        return $"{functionName}({nestedInner})";

                    return $"{functionName}({inner})";
                });
            }

            if (string.Equals(rewritten, current, StringComparison.Ordinal))
                return current;

            current = rewritten;
        }

        return current;
    }

    private static string NormalizeKnownTextSpanFunctionArgumentsForTask(string expression, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(expression) ||
            (expression.IndexOf("u.Left(", StringComparison.Ordinal) < 0 &&
             expression.IndexOf("u.Right(", StringComparison.Ordinal) < 0 &&
             expression.IndexOf("u.Mid(", StringComparison.Ordinal) < 0))
            return expression;

        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeKnownTextSpanFunctionArgumentsForTask));
        string NormalizeTextArg(string arg)
        {
            var trimmedArg = arg.Trim();
            if (IsSimpleIdentifierPath(trimmedArg))
                return MaterializeKnownFunctionArgumentForReturnType(trimmedArg, "Text", task);
            return trimmedArg;
        }

        string NormalizeNumberArg(string arg)
        {
            var trimmedArg = arg.Trim();
            var normalized = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(trimmedArg, "Number", task);
            if (string.Equals(normalized, trimmedArg, StringComparison.Ordinal) &&
                IsSimpleIdentifierPath(trimmedArg))
                normalized = $"u.CastToNumber({trimmedArg})";
            return normalized;
        }

        string RewriteTextSpan(string name, List<string> args)
        {
            if (args.Count == 0)
                return $"{name}()";
            args[0] = NormalizeTextArg(args[0]);
            for (var i = 1; i < args.Count && i <= 2; i++)
                args[i] = NormalizeNumberArg(args[i]);
            return $"{name}({string.Join(", ", args)})";
        }

        var rewritten = RewriteFunctionCalls(expression, "u.Left", args => RewriteTextSpan("u.Left", args));
        rewritten = RewriteFunctionCalls(rewritten, "u.Right", args => RewriteTextSpan("u.Right", args));
        rewritten = RewriteFunctionCalls(rewritten, "u.Mid", args => RewriteTextSpan("u.Mid", args));
        return rewritten;
    }

    private static string RewriteConditionalNullObjectArgumentCentral(string expression, TaskSemantic task)
    {
        var trimmed = expression.Trim();
        if (!TryParseFunctionCall(trimmed, out var functionName, out var args) ||
            !IsTopLevelCall(functionName, "u.If") ||
            args.Count != 3)
            return expression;

        var condition = args[0].Trim();
        var whenTrue = args[1].Trim();
        var whenFalse = args[2].Trim();

        if (IsNullCallExpression(whenTrue))
            return $"(({condition}) ? null : (object){whenFalse})";

        if (IsNullCallExpression(whenFalse))
            return $"(({condition}) ? (object){whenTrue} : null)";

        return expression;
    }

    private static ExpectedTypeContext ExpectedTypeForKnownFunctionArgument(string functionName, int argumentIndex, int argumentCount)
    {
        return TryResolveXpaFunctionEmissionArgumentReturnTypeContract(functionName, argumentIndex, argumentCount, out var returnType)
            ? ExpectedTypeForReturnType(returnType)
            : default;
    }

    private static ExpectedTypeContext InferContextualExpectedTypeForKnownFunctionArgument(
        string functionName,
        int argumentIndex,
        IReadOnlyList<string> args,
        TaskSemantic task)
    {
        if ((IsTopLevelCall(functionName, "u.Range") || IsTopLevelCall(functionName, "Range")) &&
            args.Count >= 3 &&
            (argumentIndex == 1 || argumentIndex == 2))
        {
            var valueExpected = ResolveExpectedTypeFromExpressionEvidence(task, args[0].Trim());
            var valueReturnType = GetValueReturnType(valueExpected.ReturnType);
            if (string.Equals(valueReturnType, "Date", StringComparison.Ordinal) ||
                string.Equals(NormalizeAttrObjKind(valueExpected.AttrObj), "FIELD_DATE", StringComparison.OrdinalIgnoreCase))
                return ExpectedTypeForReturnType("Date");
            if (string.Equals(valueReturnType, "Time", StringComparison.Ordinal) ||
                string.Equals(NormalizeAttrObjKind(valueExpected.AttrObj), "FIELD_TIME", StringComparison.OrdinalIgnoreCase))
                return ExpectedTypeForReturnType("Time");
            if (IsNumericLikeExpectedType(valueExpected))
                return ExpectedTypeForReturnType("Number");
            if (IsTextLikeExpectedType(valueExpected))
                return ExpectedTypeForReturnType("Text");
        }

        return default;
    }

    private static bool TrySplitDbNameLiteral(string? value, out string fileIndex, out string infoType)
    {
        fileIndex = "";
        infoType = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split(',');
        if (parts.Length != 2)
            return false;

        var left = parts[0].Trim();
        var right = parts[1].Trim();
        if (!int.TryParse(left, out _) || !int.TryParse(right, out _))
            return false;

        fileIndex = left;
        infoType = right;
        return true;
    }

    private static string NormalizeTargetSpecificExpression(string expression, TaskSemantic task, ExpressionEmissionContext context)
    {
        if (context.TargetInfo is not TargetValueInfo targetInfo)
            return expression;

        if (targetInfo.IsArray)
            return TrackLegacyExpressionTreatmentIfChanged(
                "NormalizeType",
                nameof(NormalizeTargetSpecificExpression),
                expression,
                NormalizeArrayTargetExpression(expression, task, targetInfo));

        if (targetInfo.IsBlob && !targetInfo.IsArray)
            return TrackLegacyExpressionTreatmentIfChanged(
                "NormalizeType",
                nameof(NormalizeTargetSpecificExpression),
                expression,
                NormalizeBlobTargetExpression(expression, task, targetInfo, context.BlobTarget));

        var effectiveAttrObj = !string.IsNullOrWhiteSpace(targetInfo.AttrObj)
            ? targetInfo.AttrObj
            : targetInfo.ModelAttrObj ?? "";
        if (ShouldReanchorExplicitTypedCast(expression, effectiveAttrObj))
            return TrackLegacyExpressionTreatmentIfChanged(
                "NormalizeType",
                nameof(NormalizeTargetSpecificExpression),
                expression,
                ApplyAttributeCast(expression, effectiveAttrObj));

        return expression;
    }

    private static string NormalizeScalarExpectedExpressionForContext(string expression, TaskSemantic task, ExpressionEmissionContext context)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var attrObj = context.Expected.AttrObj;
        if (string.IsNullOrWhiteSpace(attrObj))
            return expression;

        if (string.Equals(attrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return expression;

        if (ShouldReanchorExplicitTypedCast(expression, attrObj))
            return TrackLegacyExpressionTreatmentIfChanged(
                "NormalizeType",
                nameof(NormalizeScalarExpectedExpressionForContext),
                expression,
                ApplyAttributeCast(expression, attrObj));

        if (string.Equals(NormalizeAttrObjKind(attrObj), "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) &&
            IsTextWrappedNumericExpression(expression))
            return TrackLegacyExpressionTreatmentIfChanged(
                "NormalizeType",
                nameof(NormalizeScalarExpectedExpressionForContext),
                expression,
                EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(expression, "FIELD_ALPHA"),
                "Number"));

        if (string.Equals(NormalizeAttrObjKind(attrObj), "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) &&
            TryParseFunctionCall(expression.Trim(), out var numericCastName, out var numericCastArgs) &&
            IsTopLevelCall(numericCastName, "u.CastToText") &&
            numericCastArgs.Count == 1)
        {
            var innerNumericExpr = numericCastArgs[0].Trim();
            if (IsTopLevelCall(TryGetTopLevelFunctionName(innerNumericExpr), "u.GetTextParam"))
                return TrackLegacyExpressionTreatmentIfChanged(
                    "NormalizeType",
                    nameof(NormalizeScalarExpectedExpressionForContext),
                    expression,
                    EmitScalarArgumentFromEvidence(
                    StripExpectedAttributeCastWrappers(innerNumericExpr, "FIELD_ALPHA"),
                    "Number"));
            var inferredNumericExpr = ResolveExpectedTypeFromExpressionEvidence(task, innerNumericExpr);
            if (IsNumericLikeExpectedType(inferredNumericExpr))
                return TrackLegacyExpressionTreatmentIfChanged(
                    "NormalizeType",
                    nameof(NormalizeScalarExpectedExpressionForContext),
                    expression,
                    EmitScalarArgumentFromEvidence(
                    StripExpectedAttributeCastWrappers(innerNumericExpr, "FIELD_ALPHA"),
                    "Number"));
        }

        if (string.Equals(NormalizeAttrObjKind(attrObj), "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) &&
            TryParseFunctionCall(expression.Trim(), out var conditionalName, out var conditionalArgs) &&
            IsTopLevelCall(conditionalName, "u.If") &&
            conditionalArgs.Count == 3)
        {
            var whenTrue = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    NormalizeNumericConditionalBranchExpressionCentral(conditionalArgs[1].Trim()),
                    "FIELD_NUMERIC"),
                "Number");
            var whenFalse = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    NormalizeNumericConditionalBranchExpressionCentral(conditionalArgs[2].Trim()),
                    "FIELD_NUMERIC"),
                "Number");
            return TrackLegacyExpressionTreatmentIfChanged(
                "NormalizeType",
                nameof(NormalizeScalarExpectedExpressionForContext),
                expression,
                $"u.If({conditionalArgs[0].Trim()}, {whenTrue}, {whenFalse})");
        }

        if (TryCoerceScalarMismatchForContext(expression, task, context, out var coercedScalar))
            return TrackLegacyExpressionTreatmentIfChanged(
                "NormalizeType",
                nameof(NormalizeScalarExpectedExpressionForContext),
                expression,
                coercedScalar);

        var topLevelCall = TryGetTopLevelFunctionName(expression);
        var shouldNormalize =
            IsNullCallExpression(expression) ||
            IsCallDllExpression(topLevelCall) ||
            IsRuntimeUntypedScalarExpression(topLevelCall) ||
            IsObjectProducingExpression(topLevelCall) ||
            IsTopLevelCall(topLevelCall, "u.FileInfo") ||
            IsTopLevelCall(topLevelCall, "u.ClientFileInfo") ||
            IsTopLevelCall(topLevelCall, "u.GetTextParam") ||
            IsTopLevelCall(topLevelCall, "u.SharedValGet") ||
            IsTopLevelCall(topLevelCall, "SharedValGet") ||
            IsTopLevelCall(topLevelCall, "u.VecGet") ||
            IsTopLevelCall(topLevelCall, "u.VariantGet") ||
            IsTopLevelCall(topLevelCall, "u.HTTPCall") ||
            IsTopLevelCall(topLevelCall, "JSONGet");

        if (!shouldNormalize)
            return expression;

        return TrackLegacyExpressionTreatmentIfChanged(
            "NormalizeType",
            nameof(NormalizeScalarExpectedExpressionForContext),
            expression,
            StripRedundantOuterParentheses(expression.Trim()));
    }

    private static bool TryCoerceScalarMismatchForContext(
        string expression,
        TaskSemantic task,
        ExpressionEmissionContext context,
        out string coerced)
    {
        coerced = expression;
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var expectedAttrObj = NormalizeAttrObjKind(context.Expected.AttrObj);
        if (string.IsNullOrWhiteSpace(expectedAttrObj) ||
            string.Equals(expectedAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return false;

        var expectedValueType = GetValueReturnType(context.Expected.ReturnType);
        if (!string.IsNullOrWhiteSpace(expectedValueType) &&
            (expectedValueType.EndsWith("[]", StringComparison.Ordinal) ||
             string.Equals(expectedValueType, "object", StringComparison.Ordinal)))
            return false;

        var actual = ResolveExpectedTypeFromExpressionEvidence(task, expression);
        if (!actual.HasExpectation || actual.IsBooleanCondition)
            return false;

        var actualAttrObj = NormalizeAttrObjKind(!string.IsNullOrWhiteSpace(actual.AttrObj)
            ? actual.AttrObj
            : MapReturnTypeToAttrObj(GetValueReturnType(actual.ReturnType)));

        if (string.IsNullOrWhiteSpace(actualAttrObj) ||
            string.Equals(actualAttrObj, expectedAttrObj, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actualAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return false;

        if (IsGenericObjectExpectation(actual))
            return false;

        coerced = ApplyAttributeCast(expression, expectedAttrObj);
        return !string.Equals(coerced, expression, StringComparison.Ordinal);
    }

    private static bool IsTextWrappedNumericExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        if (!TryParseFunctionCall(expression.Trim(), out var functionName, out var args) ||
            !IsTopLevelCall(functionName, "u.CastToText") ||
            args.Count != 1)
            return false;

        var inner = args[0].Trim();
        var innerCall = TryGetTopLevelFunctionName(inner);
        return IsTopLevelCall(innerCall, "u.Val") ||
               IsTopLevelCall(innerCall, "u.StrNum");
    }

    private static string NormalizeArrayTargetExpression(string expression, TaskSemantic? task, TargetValueInfo targetInfo)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = expression.Trim();
        if (IsNullCallExpression(trimmed))
            return "null";

        var itemType = ResolveArrayTargetItemType(targetInfo, task);

        if (TryParseFunctionCall(trimmed, out var arrayGetterName, out var arrayGetterArgs))
        {
            var sharedValueRewrite = TryRewriteSharedValueArrayGetter(arrayGetterName, arrayGetterArgs, itemType);
            if (!string.IsNullOrWhiteSpace(sharedValueRewrite))
                return sharedValueRewrite;

            var parameterRewrite = TryRewriteParameterArrayGetter(arrayGetterName, arrayGetterArgs, itemType);
            if (!string.IsNullOrWhiteSpace(parameterRewrite))
                return parameterRewrite;
        }

        if (TryGetWholeCSharpStringLiteral(trimmed, out var literalValue) &&
            string.IsNullOrEmpty(literalValue))
            return "null";

        if (TryParseFunctionCall(trimmed, out var wrappedFunctionName, out var wrappedArgs) &&
            wrappedArgs.Count == 1 &&
            IsTopLevelCall(wrappedFunctionName, "u.CastToByteArray"))
        {
            var inner = wrappedArgs[0].Trim();
            if (TryGetWholeCSharpStringLiteral(inner, out var wrappedLiteralValue) &&
                string.IsNullOrEmpty(wrappedLiteralValue))
                return "null";
            if (IsNullCallExpression(inner))
                return "null";
            var innerCall = TryGetTopLevelFunctionName(inner);
            if (IsArrayProducingExpression(inner, innerCall, itemType))
                return inner;
        }

        var arraySourceCall = TryGetTopLevelFunctionName(trimmed);
        if (IsArrayProducingExpression(trimmed, arraySourceCall, itemType))
            return trimmed;

        if (string.Equals(itemType, "byte[]", StringComparison.Ordinal) &&
            IsByteArrayLikeExpression(trimmed))
            return $"new byte[][] {{ {trimmed} }}";

        return expression;
    }

    private static string ResolveArrayTargetItemType(TargetValueInfo targetInfo, TaskSemantic? task)
    {
        if (targetInfo.Resource is not null)
            return ResolveArrayColumnItemType(targetInfo.Resource, _allFieldModels, task);

        var valueReturnType = GetValueReturnType(ExpectedTypeForTarget(targetInfo).ReturnType);
        return valueReturnType switch
        {
            "Text[]" => "Text",
            "Number[]" => "Number",
            "Date[]" => "Date",
            "Time[]" => "Time",
            "Bool[]" => "Bool",
            "byte[][]" => "byte[]",
            _ => "Text"
        };
    }

    private static bool IsArrayProducingExpression(string expression, string? topLevelCall, string itemType)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        if (IsNewArrayExpressionForItemType(expression, itemType))
            return true;

        if (string.Equals(itemType, "byte[]", StringComparison.Ordinal))
            return IsByteArrayArrayLikeExpression(expression);

        var itemXpaType = ResolveArrayItemXpaType(itemType);
        return itemXpaType switch
        {
            XpaType.Text => IsTextArrayProducingExpression(topLevelCall) || IsTopLevelCall(topLevelCall, GetSharedValueArrayFunctionName(XpaType.Text)),
            XpaType.Number => IsTopLevelCall(topLevelCall, GetArrayConversionFunctionName(XpaType.Number)) || IsTopLevelCall(topLevelCall, GetSharedValueArrayFunctionName(XpaType.Number)),
            XpaType.Date => IsTopLevelCall(topLevelCall, GetArrayConversionFunctionName(XpaType.Date)),
            XpaType.Time => IsTopLevelCall(topLevelCall, GetArrayConversionFunctionName(XpaType.Time)),
            XpaType.Bool => IsTopLevelCall(topLevelCall, GetArrayConversionFunctionName(XpaType.Bool)),
            _ => false
        };
    }

    private static bool IsNewArrayExpressionForItemType(string expression, string itemType)
    {
        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        var expectedClrType = itemType switch
        {
            "Text" => "System.String",
            "Number" => "Number",
            "Date" => "Date",
            "Time" => "Time",
            "Bool" => "Bool",
            "byte[]" => "byte[]",
            _ => ""
        };
        if (string.IsNullOrWhiteSpace(expectedClrType))
            return false;

        return trimmed.StartsWith($"new {expectedClrType}[", StringComparison.Ordinal) ||
               (string.Equals(itemType, "Text", StringComparison.Ordinal) &&
                trimmed.StartsWith("new string[", StringComparison.Ordinal));
    }

    private static string NormalizeBlobTargetExpression(string expression, TaskSemantic task, TargetValueInfo targetInfo, string? blobTarget)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = expression.Trim();
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            return "null";

        var effectiveTarget = string.IsNullOrWhiteSpace(blobTarget)
            ? targetInfo.TargetMember
            : blobTarget.Trim();
        if (string.IsNullOrWhiteSpace(effectiveTarget))
            return expression;

        if (HasByteArrayWrapping(trimmed))
            return NormalizeByteArrayExpectedExpression(trimmed);

        if (IsSimpleIdentifierPath(trimmed) &&
            TryResolveSimpleExpressionValueInfo(task, trimmed, out var sourceInfo) &&
            !sourceInfo.IsBlob &&
            !sourceInfo.IsArray &&
            !sourceInfo.IsDotNet &&
            !sourceInfo.IsNumeric &&
            !sourceInfo.IsBoolean &&
            !IsDateOrTimeValueInfo(sourceInfo))
            return $"{effectiveTarget}.ToByteArray({EmitScalarArgumentFromEvidence(trimmed, "Text")})";

        if (TryGetWholeCSharpStringLiteral(trimmed, out _) ||
            (IsTextualBlobAssignmentExpression(trimmed) && !HasByteArrayWrapping(trimmed)))
            return NormalizeBlobAssignmentValue(effectiveTarget, expression);

        return expression;
    }

    private static string CoerceRunArgumentExpression(string expression, string parameterType, TaskSemantic? task, bool preserveBinding)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        if (string.Equals(expression.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            return expression.Trim();

        var trimmed = expression.Trim();
        if (preserveBinding && ShouldPreserveDirectParameterBinding(trimmed))
            return trimmed;

        var normalizedParameterType = NormalizeReturnTypeToken(parameterType);
        if (normalizedParameterType.EndsWith("Parameter", StringComparison.Ordinal) &&
            TryUnwrapDirectParameterBindingExpression(trimmed, out var directBinding))
        {
            return directBinding;
        }

        if (string.Equals(normalizedParameterType, "System.IntPtr", StringComparison.Ordinal) ||
            string.Equals(normalizedParameterType, "IntPtr", StringComparison.Ordinal))
        {
            return CoerceIntPtrCallArgumentExpression(trimmed, task);
        }

        var expectedScalarReturnType = normalizedParameterType switch
        {
            "TextParameter" or "Text" => "Text",
            "NumberParameter" or "Number" => "Number",
            "DateParameter" or "Date" => "Date",
            "TimeParameter" or "Time" => "Time",
            "BoolParameter" or "Bool" => "Bool",
            _ => ""
        };
        if (!string.IsNullOrWhiteSpace(expectedScalarReturnType) &&
            TryUnwrapNonBlobByteArrayToTextForExpectedScalar(trimmed, expectedScalarReturnType, task, out var unwrappedNonBlob))
            trimmed = unwrappedNonBlob;

        return normalizedParameterType switch
        {
            "TextParameter" or "Text" => NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(trimmed, "Text", task),
            "NumberParameter" or "Number" => NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(trimmed, "Number", task),
            "DateParameter" or "Date" => NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(trimmed, "Date", task),
            "TimeParameter" or "Time" => NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(trimmed, "Time", task),
            "BoolParameter" or "Bool" => NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(trimmed, "Bool", task),
            "ByteArrayParameter" or "byte[]" => CoerceByteArrayRunArgumentExpression(trimmed, task),
            var token when token.StartsWith("Func<", StringComparison.Ordinal) => EnsureFuncCallArgument(trimmed, token, task),
            _ => trimmed
        };
    }

    private static string CoerceIntPtrCallArgumentExpression(string expression, TaskSemantic? task)
    {
        var trimmed = expression.Trim();
        if (trimmed.StartsWith("new System.IntPtr(", StringComparison.Ordinal) ||
            trimmed.StartsWith("new IntPtr(", StringComparison.Ordinal))
            return trimmed;

        var normalizedNumber = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(trimmed, "Number", task);
        return $"new System.IntPtr((int){normalizedNumber})";
    }

    private static string CoerceByteArrayRunArgumentExpression(string expression, TaskSemantic? task)
    {
        var trimmed = expression.Trim();
        if (HasByteArrayWrapping(trimmed))
            return trimmed;

        if (task is not null &&
            TryResolveSimpleExpressionValueInfo(task, trimmed, out var sourceInfo) &&
            sourceInfo.IsBlob)
            return trimmed;

        return NormalizeByteArrayExpectedExpression(trimmed);
    }

    private static string CoerceCallArgumentForParameter(string expression, string parameterType)
        => CoerceCallArgumentForParameter(expression, parameterType, null);

    private static string CoerceCallArgumentForParameter(string expression, string parameterType, TaskSemantic? task)
        => CoerceCallArgumentForParameter(expression, parameterType, task, false);

    private static string CoerceCallArgumentForParameter(string expression, string parameterType, TaskSemantic? task, bool preserveBinding)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        if (string.Equals(expression.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            return expression.Trim();

        var trimmed = expression.Trim();
        var normalizedParameterType = NormalizeReturnTypeToken(parameterType);
        var expectedScalarReturnType = normalizedParameterType switch
        {
            "TextParameter" or "Text" => "Text",
            "NumberParameter" or "Number" => "Number",
            "DateParameter" or "Date" => "Date",
            "TimeParameter" or "Time" => "Time",
            "BoolParameter" or "Bool" => "Bool",
            _ => ""
        };
        if (!string.IsNullOrWhiteSpace(expectedScalarReturnType) &&
            TryUnwrapNonBlobByteArrayToTextForExpectedScalar(trimmed, expectedScalarReturnType, task, out var unwrappedNonBlob))
            trimmed = unwrappedNonBlob;

        if (string.Equals(normalizedParameterType, "System.IntPtr", StringComparison.Ordinal) ||
            string.Equals(normalizedParameterType, "IntPtr", StringComparison.Ordinal))
            return CoerceIntPtrCallArgumentExpression(trimmed, task);

        var expected = ExpectedTypeForParameterType(normalizedParameterType);
        if (task is not null &&
            expected.HasExpectation &&
            !string.Equals(normalizedParameterType, "object", StringComparison.Ordinal) &&
            !normalizedParameterType.StartsWith("Func<", StringComparison.Ordinal) &&
            !trimmed.StartsWith("u.CaseUntyped(", StringComparison.Ordinal) &&
            IsExpressionAlreadyCompatible(trimmed, task, expected))
            return trimmed;

        return task is null
            ? CoerceRunArgumentExpression(trimmed, normalizedParameterType, null, preserveBinding)
            : EmitExpressionForContext(trimmed, task, CreateRunArgumentEmissionContext(normalizedParameterType, preserveBinding));
    }

    private static string EnsureFuncCallArgument(string expression, string parameterType, TaskSemantic? task)
    {
        var trimmed = expression.Trim();
        if (trimmed.StartsWith("() =>", StringComparison.Ordinal))
        {
            if (task is null)
                return trimmed;

            var expected = ExpectedTypeForParameterType(parameterType);
            return expected.HasExpectation
                ? EmitExpressionForExpectedType(trimmed, task, expected)
                : trimmed;
        }

        var innerReturnType = GetValueReturnType(parameterType);
        if (task is not null && !string.IsNullOrWhiteSpace(innerReturnType))
            trimmed = EmitExpressionForExpectedType(trimmed, task, ExpectedTypeForReturnType(innerReturnType));

        return $"() => {trimmed}";
    }

    private static string UnwrapExplicitScalarCastLayers(string value)
    {
        var current = value?.Trim() ?? "";
        while (TryParseFunctionCall(current, out var functionName, out var args) &&
               args.Count == 1 &&
               (IsTopLevelCall(functionName, "u.CastToText") ||
                IsTopLevelCall(functionName, "u.CastToBool") ||
                IsTopLevelCall(functionName, "u.CastToNumber")))
        {
            current = args[0].Trim();
        }

        return current;
    }

    private static bool TryGetTrailingVariantTypeCode(List<string> args, out char typeCode)
    {
        typeCode = '\0';
        if (args.Count == 0)
            return false;

        return TryGetMagicCharLiteral(args[^1], out typeCode);
    }

    private static bool IsColorFactoryExpression(string? functionName)
    {
        return IsTopLevelCall(functionName, "System.Drawing.Color.FromName") ||
               IsTopLevelCall(functionName, "System.Drawing.Color.FromArgb");
    }

    private static bool IsWholeStringLiteralExpression(string value)
    {
        return TryGetWholeCSharpStringLiteral(value, out _);
    }

    private static bool HasByteArrayWrapping(string value)
    {
        var topLevelCall = TryGetTopLevelFunctionName(value);
        if (IsTopLevelCall(topLevelCall, "u.CastToByteArray"))
            return true;

        if (TryParseFunctionCall(value, out var functionName, out _))
        {
            if (functionName.EndsWith(".ToByteArray", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(functionName, "TextColumn.ToByteArray", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return value.IndexOf(".ToByteArray(", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string NormalizeDotNetAssignmentExpression(string value, string? objectType)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        value = UnwrapTypedCastForDotNetAssignment(value.Trim());
        value = NormalizeDotNetArrayConstructorMappings(value);
        string? normalizedType = null;
        string? rawObjectType = null;
        if (!string.IsNullOrWhiteSpace(objectType))
        {
            rawObjectType = NormalizeRawDotNetObjectType(objectType);
            normalizedType = NormalizeDotNetObjectType(objectType);
        }

        if (TryNormalizeDotNetConstructorExpression(value, rawObjectType, normalizedType, out var normalizedCtor))
            return normalizedCtor;

        return StripDotNetQualifierOutsideQuotes(value);
    }

    private static bool TryResolveNewClrExpressionReturnType(string expression, out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (!trimmed.StartsWith("new ", StringComparison.Ordinal))
            return false;

        var typeStart = "new ".Length;
        var parenIndex = trimmed.IndexOf('(', typeStart);
        var bracketIndex = trimmed.IndexOf('[', typeStart);
        var terminatorIndex = parenIndex >= 0 && bracketIndex >= 0
            ? Math.Min(parenIndex, bracketIndex)
            : Math.Max(parenIndex, bracketIndex);
        if (terminatorIndex <= typeStart)
            return false;

        var typePart = trimmed[typeStart..terminatorIndex].Trim();
        if (string.IsNullOrWhiteSpace(typePart))
            return false;

        if (parenIndex >= 0 && parenIndex == terminatorIndex)
        {
            var closeParen = FindMatchingParen(trimmed, parenIndex);
            if (closeParen < 0)
                return false;

            if (SkipWhitespace(trimmed, closeParen + 1) < trimmed.Length)
                return false;
        }

        var normalizedType = NormalizeDotNetObjectType(typePart);
        if (string.IsNullOrWhiteSpace(normalizedType))
            return false;

        returnType = bracketIndex >= 0 && bracketIndex == terminatorIndex
            ? $"{normalizedType}[]"
            : normalizedType;
        return true;
    }

    private static string UnwrapTypedCastForDotNetAssignment(string value)
    {
        var current = value;
        while (TryParseFunctionCall(current, out var functionName, out var args) &&
               args.Count == 1 &&
               (string.Equals(functionName, "u.CastToByteArray", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(functionName, "u.CastToText", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(functionName, "u.CastToNumber", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(functionName, "u.CastToDate", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(functionName, "u.CastToTime", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(functionName, "u.CastToBool", StringComparison.OrdinalIgnoreCase)))
        {
            current = args[0].Trim();
        }

        return current;
    }

    private static bool TryNormalizeDotNetConstructorExpression(string value, string? rawObjectType, string? normalizedType, out string normalizedCtor)
    {
        normalizedCtor = "";
        if (!TryParseFunctionCall(value, out var functionName, out var args))
            return false;

        var normalizedFunctionName = StripLeadingDotNetQualifier(functionName);
        if (string.IsNullOrWhiteSpace(normalizedFunctionName))
            return false;

        if (!string.IsNullOrWhiteSpace(rawObjectType) &&
            string.Equals(normalizedFunctionName, rawObjectType, StringComparison.Ordinal))
        {
            if (string.Equals(normalizedType, "dynamic", StringComparison.Ordinal))
            {
                normalizedCtor = BuildExternalTypeCompatCreationExpression(rawObjectType, args);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(normalizedType) &&
                !string.Equals(normalizedType, rawObjectType, StringComparison.Ordinal))
            {
                normalizedCtor = $"new {normalizedType}({string.Join(", ", args)})";
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedType))
        {
            if (string.Equals(normalizedFunctionName, normalizedType, StringComparison.Ordinal))
            {
                normalizedCtor = $"new {normalizedType}({string.Join(", ", args)})";
                return true;
            }

            if (IsSameNamespaceCtorCall(normalizedFunctionName, normalizedType))
            {
                normalizedCtor = $"new {normalizedFunctionName}({string.Join(", ", args)})";
                return true;
            }
        }

        if (string.Equals(normalizedType, "dynamic", StringComparison.Ordinal) &&
            LooksLikeClrConstructorName(normalizedFunctionName))
        {
            normalizedCtor = BuildExternalTypeCompatCreationExpression(normalizedFunctionName, args);
            return true;
        }

        if (args.Count == 0 && LooksLikeClrConstructorName(normalizedFunctionName))
        {
            normalizedCtor = $"new {normalizedFunctionName}()";
            return true;
        }

        return false;
    }

    private static string StripLeadingDotNetQualifier(string functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return functionName;

        return functionName.StartsWith("DotNet.", StringComparison.Ordinal)
            ? functionName["DotNet.".Length..]
            : functionName;
    }

    private static bool IsSameNamespaceCtorCall(string functionName, string normalizedType)
    {
        var lastDot = normalizedType.LastIndexOf('.');
        if (lastDot <= 0)
            return false;

        var typeNamespace = normalizedType[..lastDot];
        return functionName.StartsWith(typeNamespace + ".", StringComparison.Ordinal) &&
               LooksLikeClrConstructorName(functionName);
    }

    private static bool LooksLikeClrConstructorName(string functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return false;

        var lastSegment = functionName.Split('.').LastOrDefault();
        if (string.IsNullOrWhiteSpace(lastSegment))
            return false;

        return char.IsUpper(lastSegment[0]);
    }

    private static string StripDotNetQualifierOutsideQuotes(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var result = new StringBuilder(expression.Length);
        var i = 0;
        while (i < expression.Length)
        {
            var ch = expression[i];
            if (IsQuotedSegmentStart(expression, i))
            {
                var quoteStart = i;
                if (!TryReadQuotedSegmentEnd(expression, i, out var quoteEnd))
                    break;
                result.Append(expression, quoteStart, (quoteEnd - quoteStart) + 1);
                i = quoteEnd + 1;
                continue;
            }

            if (StartsWithAt(expression, i, "DotNet.") &&
                (i == 0 || !(char.IsLetterOrDigit(expression[i - 1]) || expression[i - 1] == '_' || expression[i - 1] == '.')))
            {
                i += "DotNet.".Length;
                continue;
            }

            result.Append(ch);
            i++;
        }

        return result.ToString();
    }

    private static string NormalizeBlobAssignmentValue(string target, string value)
    {
        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeBlobAssignmentValue));
        if (string.IsNullOrWhiteSpace(value))
            return value;

        value = NormalizeTextFunctionInputsCentral(value);

        var trimmed = value.Trim();
        var resolvedType = ResolveExpressionTypeFromEvidence(null, trimmed);
        if (TryGetWholeCSharpStringLiteral(trimmed, out _))
            return $"{target}.ToByteArray({value})";

        var topLevelCall = TryGetTopLevelFunctionName(trimmed);
        if (IsSharedValGetExpression(topLevelCall))
        {
            var normalized = RewriteFunctionCalls(trimmed, "SharedValGet", args => $"u.SharedValGet({string.Join(", ", args)})");
            return $"{target}.Cast({normalized})";
        }

        if (HasByteArrayWrapping(trimmed))
            return NormalizeByteArrayExpectedExpression(trimmed);

        if (resolvedType.IsResolved &&
            string.Equals(GetValueReturnType(resolvedType.ReturnType), "byte[]", StringComparison.Ordinal))
            return NormalizeByteArrayExpectedExpression(trimmed);

        if (IsHttpPayloadExpression(topLevelCall) || IsTopLevelCall(topLevelCall, "u.File2Blb"))
            return value;

        if (resolvedType.IsResolved &&
            string.Equals(GetValueReturnType(resolvedType.ReturnType), "Text", StringComparison.Ordinal))
            return $"{target}.ToByteArray({NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(value, "Text")})";

        if (IsTextualBlobAssignmentExpression(value))
            return $"{target}.ToByteArray({value})";

        return value;
    }

    private static bool IsTextualBlobAssignmentExpression(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        var resolvedType = ResolveExpressionTypeFromEvidence(null, trimmed);
        if (resolvedType.IsResolved)
        {
            var resolvedReturnType = GetValueReturnType(resolvedType.ReturnType);
            if (string.Equals(resolvedReturnType, "Text", StringComparison.Ordinal))
                return true;
            if (string.Equals(resolvedReturnType, "byte[]", StringComparison.Ordinal))
                return false;
        }

        if (TryGetWholeCSharpStringLiteral(trimmed, out _))
            return true;

        if (trimmed.StartsWith("u.RepStr(", StringComparison.OrdinalIgnoreCase))
            return true;

        var topLevelCall = TryGetTopLevelFunctionName(trimmed);
        if (IsKnownTextProducingFunction(topLevelCall))
            return true;

        if (IsTextualConditionalExpression(trimmed))
            return true;

        return ContainsTopLevelConcatenation(trimmed);
    }

    private static bool IsTextualConditionalExpression(string expression)
    {
        if (!TryParseFunctionCall(expression, out var functionName, out var args))
            return false;

        if (IsTopLevelCall(functionName, "u.If"))
        {
            if (args.Count != 3)
                return false;
            return IsTextualBlobAssignmentExpression(args[1]) &&
                   IsTextualBlobAssignmentExpression(args[2]);
        }

        if (string.Equals(functionName, "u.Case", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(functionName, "u.CaseUntyped", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 3)
                return false;

            for (var i = 2; i < args.Count; i++)
            {
                if (!IsTextualBlobAssignmentExpression(args[i]))
                    return false;
            }

            return true;
        }

        return false;
    }

    private static bool IsKnownTextProducingFunction(string? functionName)
    {
        return IsTopLevelCall(functionName, "u.Mid") ||
               IsTopLevelCall(functionName, "u.Left") ||
               IsTopLevelCall(functionName, "u.Right") ||
               IsTopLevelCall(functionName, "u.Upper") ||
               IsTopLevelCall(functionName, "u.Lower") ||
               IsTopLevelCall(functionName, "u.Flip") ||
               IsTopLevelCall(functionName, "u.AStr") ||
               IsTopLevelCall(functionName, "u.RepStr") ||
               IsTopLevelCall(functionName, "u.Trim") ||
               IsTopLevelCall(functionName, "u.StrToken") ||
               IsTopLevelCall(functionName, "u.ClipRead") ||
               IsTopLevelCall(functionName, "u.BrowserGetContent") ||
               IsTopLevelCall(functionName, "u.HTTPLastHeader") ||
               IsTopLevelCall(functionName, "u.DStr") ||
               IsTopLevelCall(functionName, "u.TStr") ||
               IsTopLevelCall(functionName, "u.Str") ||
               IsTopLevelCall(functionName, "u.ASCIIChr") ||
               IsTopLevelCall(functionName, "u.Translate") ||
               IsTopLevelCall(functionName, "u.XMLGet") ||
               IsTopLevelCall(functionName, "u.ByteArrayToText") ||
               IsTopLevelCall(functionName, "u.CastToText") ||
               IsTopLevelCall(functionName, "u.GetTextParam") ||
               IsTopLevelCall(functionName, "u.JException") ||
               IsTopLevelCall(functionName, "u.JExceptionText") ||
               IsTopLevelCall(functionName, "Cigam.Utils.CryptaFR.EncryptString") ||
               IsTopLevelCall(functionName, "Cigam.Utils.CryptaFR.DecryptString") ||
               IsTopLevelCall(functionName, "Cigam.Utils.Mail.MailBody.ProcessLinks");
    }

    private static bool ContainsTopLevelConcatenation(string expression)
    {
        var depth = 0;
        char quote = '\0';
        for (var i = 0; i < expression.Length; i++)
        {
            var ch = expression[i];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    if (quote == '"' && i > 0 && expression[i - 1] == '\\')
                        continue;
                    if (quote == '\'' && i + 1 < expression.Length && expression[i + 1] == '\'')
                    {
                        i++;
                        continue;
                    }
                    quote = '\0';
                }
                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '(')
            {
                depth++;
                continue;
            }

            if (ch == ')')
            {
                if (depth > 0)
                    depth--;
                continue;
            }

            if (ch == '+' && depth == 0)
                return true;
        }

        return false;
    }

    private static bool TryBuildBlobVariantAssignment(string target, string value, string? attrObj, bool preferValue, out string assignment)
    {
        assignment = "";
        if (!string.Equals(attrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!IsTopLevelCall(TryGetTopLevelFunctionName(value), "u.VariantCreate"))
            return false;

        assignment = $"{target}.SilentSet({target}.Cast({value}));";
        return true;
    }

    private static bool IsTextArrayProducingExpression(string? functionName)
    {
        return IsTopLevelCall(functionName, "u.FileListGet") ||
               IsTopLevelCall(functionName, "u.ClientFileListGet") ||
               IsTopLevelCall(functionName, "u.CtxGetAllNames") ||
               IsTopLevelCall(functionName, "u.XMLValidationError") ||
               IsTopLevelCall(functionName, "u.DataViewVars");
    }

    private static bool TryResolveSimpleExpressionValueInfo(TaskSemantic task, string expression, out TargetValueInfo info)
    {
        info = default;
        if (string.IsNullOrWhiteSpace(expression) || !IsSimpleIdentifierPath(expression))
            return false;

        info = ResolveTargetValueInfo(task, null, expression.Trim());
        return !string.IsNullOrWhiteSpace(info.AttrObj) ||
               !string.IsNullOrWhiteSpace(info.ModelAttrObj) ||
               info.IsBlob ||
               info.IsArray ||
               info.IsDotNet ||
               info.IsNumeric ||
               info.IsBoolean;
    }

    private static bool IsDateOrTimeValueInfo(TargetValueInfo info)
    {
        return string.Equals(info.AttrObj, "FIELD_DATE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(info.ModelAttrObj, "FIELD_DATE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(info.AttrObj, "FIELD_TIME", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(info.ModelAttrObj, "FIELD_TIME", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDateAssignmentExpression(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("XPARuntimeCore.Box.Date.Empty", StringComparison.Ordinal) ||
               value.Contains("u.Date()", StringComparison.Ordinal) ||
               value.Contains("u.CastToDate(", StringComparison.Ordinal) ||
               value.Contains("Date.Empty", StringComparison.Ordinal);
    }

    private static bool LooksLikeTimeAssignmentExpression(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("Types.Time.StartOfDay", StringComparison.Ordinal) ||
               value.Contains("u.Time()", StringComparison.Ordinal) ||
               value.Contains("u.CastToTime(", StringComparison.Ordinal) ||
               value.Contains("Time.StartOfDay", StringComparison.Ordinal);
    }

    private static bool ContainsTimeArithmeticExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        if (expression.IndexOf("u.TVal(", StringComparison.OrdinalIgnoreCase) < 0 &&
            expression.IndexOf("u.Time()", StringComparison.OrdinalIgnoreCase) < 0 &&
            expression.IndexOf("Time.Now", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        return expression.Contains("-", StringComparison.Ordinal) ||
               expression.Contains("+", StringComparison.Ordinal) ||
               expression.Contains("*", StringComparison.Ordinal) ||
               expression.Contains("/", StringComparison.Ordinal);
    }

    private static bool ShouldCastInvokeReturnToText(string returnVariable, string target, TaskSemantic task)
    {
        if (task.ResourcesSemantic.ByName.TryGetValue(returnVariable, out var returnResource) &&
            string.Equals(returnResource.AttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase))
            return true;
        if (task.ResourcesSemantic.ByLegacyName.TryGetValue(target, out var targetResource) &&
            string.Equals(targetResource.AttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string CoerceInvokeReturnValue(string valueExpr, TaskReturnValueDef? returnValue, string returnVariable, string target, TaskSemantic task)
    {
        var mgAttr = returnValue?.MgAttr;
        var targetInfo = ResolveTargetValueInfo(task, returnVariable, target);
        return CoerceInvokeReturnValueCentral(
            valueExpr,
            mgAttr,
            targetInfo.AttrObj,
            targetInfo.IsNumeric,
            targetInfo.IsBoolean,
            targetInfo.IsBlob,
            ShouldCastInvokeReturnToText(returnVariable, target, task));
    }

    private static string BuildReturnAssignmentExpression(string returnVariable, string target, string valueExpr, TaskSemantic task, string? xmlTrace)
    {
        return BuildUpdateAssignment(
            new TaskUpdateDef(returnVariable, "", null, false, false, null, null, null, false, xmlTrace),
            target,
            valueExpr,
            task,
            preferValueForResourceAssignments: true);
    }

    private static bool TryUnwrapDirectParameterBindingExpression(string expression, out string directBinding)
    {
        directBinding = "";
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = expression.Trim();
        if (IsSimpleIdentifierPath(trimmed))
        {
            directBinding = trimmed;
            return true;
        }

        if (!TryParseFunctionCall(trimmed, out var functionName, out var args) || args.Count != 1)
            return false;

        if (!(IsTopLevelCall(functionName, "u.CastToBool") ||
              IsTopLevelCall(functionName, "u.CastToNumber") ||
              IsTopLevelCall(functionName, "u.CastToDate") ||
              IsTopLevelCall(functionName, "u.CastToTime") ||
              IsTopLevelCall(functionName, "u.CastToText")))
            return false;

        var inner = args[0].Trim();
        if (!IsSimpleIdentifierPath(inner))
            return false;

        directBinding = inner;
        return true;
    }

    private static string CoerceRunArgumentsForTarget(string runArgs, TaskSemantic currentTask, TaskSemantic targetTask)
    {
        if (string.IsNullOrWhiteSpace(runArgs))
            return runArgs;

        var args = SplitTopLevelArguments(runArgs);
        if (args.Count == 0)
            return runArgs;

        var parameters = GetTaskParameters(targetTask);
        if (parameters.Count == 0)
            return runArgs;

        for (var i = 0; i < args.Count && i < parameters.Count; i++)
        {
            var parameterType = NormalizeReturnTypeToken(parameters[i].ParameterType);
            var rewritten = CoerceCallArgumentForParameter(args[i].Trim(), parameterType, currentTask, !IsInputParameterDirection(parameters[i].ParameterDirection));

            if ((string.Equals(parameterType, "Number", StringComparison.Ordinal) ||
                 string.Equals(parameterType, "NumberParameter", StringComparison.Ordinal)) &&
                TryParseFunctionCall(StripRedundantOuterParentheses(rewritten.Trim()), out var functionName, out var functionArgs) &&
                IsTopLevelCall(functionName, "u.CastToNumber") &&
                functionArgs.Count == 1)
            {
                rewritten = $"u.CastToNumber({NormalizeCastToNumberInputUsingResolvedTypeCentral(functionArgs[0].Trim(), currentTask)})";
            }

            args[i] = rewritten;
        }

        return string.Join(", ", args);
    }

    private static int CountChangedRunArguments(string before, string after)
    {
        var beforeArgs = SplitTopLevelArguments(before ?? "");
        var afterArgs = SplitTopLevelArguments(after ?? "");
        var count = Math.Max(beforeArgs.Count, afterArgs.Count);
        var changed = 0;
        for (var i = 0; i < count; i++)
        {
            var beforeArg = i < beforeArgs.Count ? beforeArgs[i].Trim() : "";
            var afterArg = i < afterArgs.Count ? afterArgs[i].Trim() : "";
            if (!string.Equals(beforeArg, afterArg, StringComparison.Ordinal))
                changed++;
        }

        return changed;
    }

    private static bool ShouldPreserveParameterBinding(IReadOnlyList<string>? parameterDirections, int index)
    {
        if (parameterDirections is null)
            return true;
        if (index < 0 || index >= parameterDirections.Count)
            return false;

        return !IsInputParameterDirection(parameterDirections[index]);
    }

    private static bool IsInputParameterDirection(string? direction)
        => string.IsNullOrWhiteSpace(direction) ||
           string.Equals(direction, "Input", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(direction, "In", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldPreserveDirectParameterBinding(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = expression.Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed.StartsWith("@\"", StringComparison.Ordinal) ||
            trimmed.StartsWith("\"", StringComparison.Ordinal) ||
            trimmed.StartsWith("'", StringComparison.Ordinal))
            return true;

        if (bool.TryParse(trimmed, out _))
            return true;

        if (char.IsDigit(trimmed[0]) ||
            ((trimmed[0] == '-' || trimmed[0] == '+') && trimmed.Length > 1 && char.IsDigit(trimmed[1])))
            return true;

        if (trimmed.IndexOfAny(new[] { '(', ')', '+', '-', '*', '/', '?', ':', '=', '!', '<', '>', '&', '|', ',' }) >= 0)
            return false;

        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '.' || ch == '[' || ch == ']')
                continue;

            return false;
        }

        return true;
    }

    private static IReadOnlyList<string> AlignArgumentSequenceForParameters(
        IReadOnlyList<string> args,
        IReadOnlyList<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)> parameters,
        TaskSemantic currentTask)
    {
        var argCount = args.Count;
        var paramCount = parameters.Count;
        var costs = new int[argCount + 1, paramCount + 1];
        var actions = new byte[argCount + 1, paramCount + 1];
        const int inf = 1_000_000;

        for (var i = 0; i <= argCount; i++)
        for (var j = 0; j <= paramCount; j++)
            costs[i, j] = inf;

        costs[0, 0] = 0;

        for (var i = 0; i <= argCount; i++)
        {
            for (var j = 0; j <= paramCount; j++)
            {
                var current = costs[i, j];
                if (current >= inf)
                    continue;

                if (i < argCount && j < paramCount)
                {
                    var matchCost = current + GetArgumentMatchCost(args[i], parameters[j], currentTask);
                    if (matchCost < costs[i + 1, j + 1])
                    {
                        costs[i + 1, j + 1] = matchCost;
                        actions[i + 1, j + 1] = 1;
                    }
                }

                if (j < paramCount)
                {
                    var skipParamCost = current + GetSkippedParameterCost(parameters[j]);
                    if (skipParamCost < costs[i, j + 1])
                    {
                        costs[i, j + 1] = skipParamCost;
                        actions[i, j + 1] = 2;
                    }
                }

                if (i < argCount && ShouldDropOptionalArgument(args[i]))
                {
                    var skipArgCost = current + GetDroppedArgumentCost(args[i]);
                    if (skipArgCost < costs[i + 1, j])
                    {
                        costs[i + 1, j] = skipArgCost;
                        actions[i + 1, j] = 3;
                    }
                }
            }
        }

        var result = Enumerable.Repeat("null", paramCount).ToArray();
        var ai = argCount;
        var pj = paramCount;
        while (ai > 0 || pj > 0)
        {
            var action = actions[ai, pj];
            switch (action)
            {
                case 1:
                    result[pj - 1] = args[ai - 1];
                    ai--;
                    pj--;
                    break;
                case 2:
                    result[pj - 1] = "null";
                    pj--;
                    break;
                case 3:
                    ai--;
                    break;
                default:
                    if (pj > 0)
                    {
                        result[pj - 1] = ai > 0 ? args[ai - 1] : "null";
                        ai = Math.Max(0, ai - 1);
                        pj--;
                    }
                    else if (ai > 0)
                    {
                        ai--;
                    }
                    break;
            }
        }

        var lastNonNull = Array.FindLastIndex(result, value => !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase));
        return lastNonNull >= 0 ? result.Take(lastNonNull + 1).ToArray() : result;
    }

    private static bool HasPotentialOptionalTailAmbiguity(
        IReadOnlyList<string> args,
        IReadOnlyList<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)> parameters,
        int optionalStartIndex,
        TaskSemantic currentTask)
    {
        for (var i = optionalStartIndex; i < args.Count && i < parameters.Count; i++)
        {
            var argument = args[i].Trim();
            if (string.Equals(argument, "null", StringComparison.OrdinalIgnoreCase))
                continue;

            var expected = ExpectedTypeForParameterType(parameters[i].ParameterType);
            if (!expected.HasExpectation)
                continue;

            var actual = ResolveExpectedTypeFromExpressionEvidence(currentTask, argument);
            if (actual.HasExpectation && !ExpectedTypesMatch(actual, expected))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<string> AlignOptionalArgumentSuffix(
        IReadOnlyList<string> args,
        IReadOnlyList<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)> parameters,
        TaskSemantic currentTask)
    {
        var argCount = args.Count;
        var paramCount = parameters.Count;
        var costs = new int[argCount + 1, paramCount + 1];
        var actions = new byte[argCount + 1, paramCount + 1];
        const int inf = 1_000_000;

        for (var i = 0; i <= argCount; i++)
        for (var j = 0; j <= paramCount; j++)
            costs[i, j] = inf;

        costs[0, 0] = 0;

        for (var i = 0; i <= argCount; i++)
        {
            for (var j = 0; j <= paramCount; j++)
            {
                var current = costs[i, j];
                if (current >= inf)
                    continue;

                if (i < argCount && j < paramCount)
                {
                    var matchCost = current + GetArgumentMatchCost(args[i], parameters[j], currentTask);
                    if (matchCost < costs[i + 1, j + 1])
                    {
                        costs[i + 1, j + 1] = matchCost;
                        actions[i + 1, j + 1] = 1;
                    }
                }

                if (j < paramCount)
                {
                    var skipParamCost = current + GetSkippedParameterCost(parameters[j]);
                    if (skipParamCost < costs[i, j + 1])
                    {
                        costs[i, j + 1] = skipParamCost;
                        actions[i, j + 1] = 2;
                    }
                }

                if (i < argCount && ShouldDropOptionalArgument(args[i]))
                {
                    var skipArgCost = current + GetDroppedArgumentCost(args[i]);
                    if (skipArgCost < costs[i + 1, j])
                    {
                        costs[i + 1, j] = skipArgCost;
                        actions[i + 1, j] = 3;
                    }
                }
            }
        }

        var result = Enumerable.Repeat("null", paramCount).ToArray();
        var ai = argCount;
        var pj = paramCount;
        while (ai > 0 || pj > 0)
        {
            var action = actions[ai, pj];
            switch (action)
            {
                case 1:
                    result[pj - 1] = args[ai - 1];
                    ai--;
                    pj--;
                    break;
                case 2:
                    result[pj - 1] = "null";
                    pj--;
                    break;
                case 3:
                    ai--;
                    break;
                default:
                    if (pj > 0)
                    {
                        result[pj - 1] = ai > 0 ? args[ai - 1] : "null";
                        ai = Math.Max(0, ai - 1);
                        pj--;
                    }
                    else if (ai > 0)
                    {
                        ai--;
                    }
                    break;
            }
        }

        var lastNonNull = Array.FindLastIndex(result, value => !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase));
        return lastNonNull >= 0 ? result.Take(lastNonNull + 1).ToArray() : result;
    }

    private static int GetArgumentMatchCost(
        string argument,
        (string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection) parameter,
        TaskSemantic currentTask)
    {
        var trimmed = argument?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmed) || string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            return 1;

        var expected = ExpectedTypeForParameterType(parameter.ParameterType);
        if (!expected.HasExpectation)
            return 2;

        var actual = ResolveExpectedTypeFromExpressionEvidence(currentTask, trimmed);
        if (actual.HasExpectation && ExpectedTypesMatch(actual, expected))
            return 0;

        if (string.Equals(parameter.ColumnMember, "r_erro", StringComparison.OrdinalIgnoreCase))
            return 12;

        if (!IsInputParameterDirection(parameter.ParameterDirection))
            return 8;

        return 6;
    }

    private static int GetSkippedParameterCost((string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection) parameter)
    {
        if (string.Equals(parameter.ColumnMember, "r_erro", StringComparison.OrdinalIgnoreCase))
            return 0;

        if (!IsInputParameterDirection(parameter.ParameterDirection))
            return 1;

        if (string.Equals(parameter.ParameterType, "ByteArrayParameter", StringComparison.Ordinal))
            return 1;

        return 3;
    }

    private static bool ShouldDropOptionalArgument(string argument)
    {
        var trimmed = argument?.Trim() ?? "";
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            return true;

        if (trimmed.IndexOf(".r_erro", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (trimmed.StartsWith("u.CastToByteArray(", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static int GetDroppedArgumentCost(string argument)
    {
        var trimmed = argument?.Trim() ?? "";
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            return 0;

        if (trimmed.IndexOf(".r_erro", StringComparison.OrdinalIgnoreCase) >= 0)
            return 1;

        if (trimmed.StartsWith("u.CastToByteArray(", StringComparison.Ordinal))
            return 2;

        return 10;
    }

    private static string NormalizeObservedArgumentTypeToken(TaskSemantic callerTask, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return "";

        var trimmed = expression.Trim();
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            return "";

        var expected = ResolveExpectedTypeFromExpressionEvidence(callerTask, trimmed);
        var returnType = GetValueReturnType(expected.ReturnType);
        if (!string.IsNullOrWhiteSpace(returnType))
            return returnType switch
            {
                "Text" => "TextParameter",
                "Number" => "NumberParameter",
                "Date" => "DateParameter",
                "Time" => "TimeParameter",
                "Bool" => "BoolParameter",
                "byte[]" => "ByteArrayParameter",
                _ => returnType
            };

        return MapAttrObjToReturnType(expected.AttrObj) switch
        {
            "Text" => "TextParameter",
            "Number" => "NumberParameter",
            "Date" => "DateParameter",
            "Time" => "TimeParameter",
            "Bool" => "BoolParameter",
            "byte[]" => "ByteArrayParameter",
            _ => ""
        };
    }
}

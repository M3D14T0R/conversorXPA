using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static readonly ConditionalWeakTable<TaskSemantic, ConcurrentDictionary<string, ExpressionTypeInfo>> _expressionTypeEvidenceCacheByTask = new();
    private static readonly ConcurrentDictionary<string, ExpressionTypeInfo> _expressionTypeEvidenceCacheGlobal = new(StringComparer.Ordinal);

    private readonly record struct ExpressionTypeInfo(
        string ReturnType,
        XpaType XpaType,
        bool IsObjectLike,
        bool IsResolved)
    {
        internal static ExpressionTypeInfo Unknown => new("", XpaType.Unknown, false, false);
    }

    private static ExpressionTypeInfo ResolveExpressionTypeFromEvidence(TaskSemantic? task, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return ExpressionTypeInfo.Unknown;

        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (string.IsNullOrWhiteSpace(trimmed))
            return ExpressionTypeInfo.Unknown;

        if (task is null)
            return _expressionTypeEvidenceCacheGlobal.GetOrAdd(
                trimmed,
                key => ResolveExpressionTypeFromEvidenceCore(null, key));

        var taskCache = _expressionTypeEvidenceCacheByTask.GetValue(
            task,
            static _ => new ConcurrentDictionary<string, ExpressionTypeInfo>(StringComparer.Ordinal));
        return taskCache.GetOrAdd(
            trimmed,
            key => ResolveExpressionTypeFromEvidenceCore(task, key));
    }

    private static ExpressionTypeInfo ResolveExpressionTypeFromEvidenceCore(TaskSemantic? task, string expression)
    {
        var evidence = task is null
            ? BuildTasklessExpressionTypeEvidence(expression)
            : BuildStrictEmittedExpressionEvidence(expression, task, default);

        return StrictEmittedExpressionEngine.TrySelectReliableSourceType(evidence, out var selected) && selected.HasType
            ? CreateResolvedExpressionTypeInfo(selected.ReturnType)
            : ExpressionTypeInfo.Unknown;
    }

    private static IReadOnlyList<EmittedExpressionTypeEvidence> BuildTasklessExpressionTypeEvidence(string code)
    {
        var evidence = new List<EmittedExpressionTypeEvidence>(capacity: 2);
        AddLiteralEvidence(code, evidence);
        AddFunctionContractEvidence(code, null, evidence);
        return evidence;
    }

    private static bool TryResolveRegisteredTypedExpressionInfo(
        TaskSemantic task,
        string expression,
        out ExpressionTypeInfo typeInfo)
    {
        typeInfo = ExpressionTypeInfo.Unknown;
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var key = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{task.Ordinal}|{StripRedundantOuterParentheses(expression.Trim())}");
        if (!_typedExpressionReturnTypeByCodeCache.TryGetValue(key, out var returnType))
            return false;

        var normalizedReturnType = NormalizeReturnTypeToken(returnType);
        if (string.IsNullOrWhiteSpace(normalizedReturnType))
            return false;

        typeInfo = CreateResolvedExpressionTypeInfo(normalizedReturnType);
        if (typeInfo.IsResolved)
            System.Threading.Interlocked.Increment(ref _typedExpressionRegisteredLookupHitCount);
        return typeInfo.IsResolved;
    }

    private static bool TryResolveSafeLegacyFunctionReturnContract(
        string functionName,
        IReadOnlyList<string> args,
        out string returnType)
    {
        returnType = "";
        var normalizedFunction = NormalizeXpaFunctionContractName(functionName);
        if (string.IsNullOrWhiteSpace(normalizedFunction) ||
            IsUnsafeLegacyFunctionReturnContract(normalizedFunction))
            return false;

        if (!TryResolveKnownXpaFunctionReturnType(functionName, args, out var contractedReturnType))
            return false;

        contractedReturnType = NormalizeReturnTypeToken(contractedReturnType);
        if (!IsSafeLegacyContractReturnType(contractedReturnType))
            return false;

        returnType = contractedReturnType;
        return true;
    }

    private static bool IsUnsafeLegacyFunctionReturnContract(string normalizedFunction)
        => string.Equals(normalizedFunction, "DATAVIEWTODNDATATABLE", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "DATAVIEWTOTEXT", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "DATAVIEWTOHTML", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "DATAVIEWTOXML", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "FILEINFO", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CLIENTFILEINFO", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "HTTPGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "HTTPPOST", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "HTTPCALL", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CALLDLL", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CALLDLLF", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VARIANTGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VECGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "JGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "JCALL", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "JCALLSTATIC", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "JAVACOMPAT.JGETSTATIC", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "XMLGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "JSONGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "NULL", StringComparison.Ordinal);

    private static bool IsSafeLegacyContractReturnType(string returnType)
        => string.Equals(returnType, "Text", StringComparison.Ordinal) ||
           string.Equals(returnType, "Number", StringComparison.Ordinal) ||
           string.Equals(returnType, "Date", StringComparison.Ordinal) ||
           string.Equals(returnType, "Time", StringComparison.Ordinal) ||
           string.Equals(returnType, "Bool", StringComparison.Ordinal) ||
           string.Equals(returnType, "byte[]", StringComparison.Ordinal);

    private static ExpressionTypeInfo UnifyExpressionTypes(ExpressionTypeInfo left, ExpressionTypeInfo right)
    {
        if (!left.IsResolved)
            return right;
        if (!right.IsResolved)
            return left;

        if (string.Equals(left.ReturnType, right.ReturnType, StringComparison.Ordinal))
            return left;

        var unified = XpaTypeEngine.Unify(left.XpaType, right.XpaType);
        if (unified == XpaType.Unknown)
            return new("object", XpaType.Object, true, true);

        return CreateResolvedExpressionTypeInfo(MapXpaTypeToReturnTypeCentral(unified));
    }

    private static ExpressionTypeInfo CreateResolvedExpressionTypeInfo(string? returnType)
    {
        var normalizedReturnType = NormalizeReturnTypeToken(returnType);
        var xpaType = XpaTypeEngine.MapExpectedToXpaType(GetValueReturnType(normalizedReturnType));
        if (xpaType == XpaType.Unknown && string.Equals(normalizedReturnType, "byte[]", StringComparison.Ordinal))
            xpaType = XpaType.Blob;

        return new(
            normalizedReturnType,
            xpaType,
            xpaType == XpaType.Object,
            xpaType != XpaType.Unknown || !string.IsNullOrWhiteSpace(normalizedReturnType));
    }

    private static bool IsKnownExpressionReturnTypeCompatible(
        string expression,
        string expectedReturnType,
        TaskSemantic? task)
    {
        var normalizedExpected = GetValueReturnType(NormalizeReturnTypeToken(expectedReturnType));
        if (string.IsNullOrWhiteSpace(normalizedExpected))
            return false;

        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (string.Equals(normalizedExpected, "Number", StringComparison.Ordinal) &&
            (string.Equals(trimmed, "u.LoopCounter()", StringComparison.Ordinal) ||
             trimmed.StartsWith("u.StrTokenCnt(", StringComparison.Ordinal)))
            return true;
        if (string.Equals(normalizedExpected, "Text", StringComparison.Ordinal) &&
            (trimmed.StartsWith("u.CastToText(", StringComparison.Ordinal) ||
             trimmed.StartsWith("u.StrToken(", StringComparison.Ordinal)))
            return true;

        return TryResolveKnownExpressionReturnTypeWithoutLegacy(task, expression, out var returnType) &&
               string.Equals(GetValueReturnType(NormalizeReturnTypeToken(returnType)), normalizedExpected, StringComparison.Ordinal);
    }

    private static bool TryResolveKnownExpressionReturnTypeWithoutLegacy(
        TaskSemantic? task,
        string expression,
        out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        if (task is not null &&
            TryResolveRegisteredTypedExpressionInfo(task, trimmed, out var registeredTypeInfo) &&
            !string.IsNullOrWhiteSpace(registeredTypeInfo.ReturnType))
        {
            returnType = registeredTypeInfo.ReturnType;
            return true;
        }

        if (task is not null &&
            TryResolveSimpleSourceReturnTypeFromResourcePath(task, trimmed, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        if (task is null &&
            TryResolveSimpleSourceReturnTypeByUniqueMemberName(trimmed, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        if (task is null &&
            TryResolveSimpleSourceReturnTypeByDataObjectMemberPath(trimmed, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        if (task is null &&
            trimmed.Contains('.', StringComparison.Ordinal) &&
            TryResolveSimpleSourceReturnTypeByUniqueMemberName(trimmed[(trimmed.LastIndexOf('.') + 1)..], out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        if (IsWholeStringLiteralExpression(trimmed))
        {
            returnType = "Text";
            return true;
        }

        if (IsNumericLiteralExpressionCentral(trimmed))
        {
            returnType = "Number";
            return true;
        }

        if (bool.TryParse(trimmed, out _))
        {
            returnType = "Bool";
            return true;
        }

        if (string.Equals(trimmed, "XPARuntimeCore.Box.Date.Empty", StringComparison.Ordinal) ||
            string.Equals(trimmed, "Date.Empty", StringComparison.Ordinal))
        {
            returnType = "Date";
            return true;
        }

        if (string.Equals(trimmed, "XPARuntimeCore.Box.Time.Empty", StringComparison.Ordinal) ||
            string.Equals(trimmed, "Time.Empty", StringComparison.Ordinal))
        {
            returnType = "Time";
            return true;
        }

        if (TryResolveNewClrExpressionReturnType(trimmed, out returnType))
            return true;

        if (TryResolveKnownStatementExpressionReturnType(trimmed, out returnType))
            return true;

        var topLevelKnownFunction = TryGetTopLevelFunctionName(trimmed);
        if (IsTopLevelCall(topLevelKnownFunction, "u.LoopCounter") ||
            IsTopLevelCall(topLevelKnownFunction, "LoopCounter") ||
            IsTopLevelCall(topLevelKnownFunction, "u.StrTokenCnt") ||
            IsTopLevelCall(topLevelKnownFunction, "StrTokenCnt"))
        {
            returnType = "Number";
            return true;
        }

        if (SplitTopLevelComparisonExpression(trimmed) is not null ||
            SplitTopLevelBooleanBinaryExpression(trimmed) is not null)
        {
            returnType = "Bool";
            return true;
        }

        var arithmetic = SplitTopLevelArithmeticExpression(trimmed);
        if (arithmetic is not null)
        {
            if (!TryResolveKnownExpressionReturnTypeWithoutLegacy(task, arithmetic.Value.Left.Trim(), out var leftReturnType) ||
                !TryResolveKnownExpressionReturnTypeWithoutLegacy(task, arithmetic.Value.Right.Trim(), out var rightReturnType))
                return false;

            var leftValueReturnType = GetValueReturnType(NormalizeReturnTypeToken(leftReturnType));
            var rightValueReturnType = GetValueReturnType(NormalizeReturnTypeToken(rightReturnType));
            if (string.Equals(arithmetic.Value.Operator, "+", StringComparison.Ordinal) &&
                (string.Equals(leftValueReturnType, "Text", StringComparison.Ordinal) ||
                 string.Equals(rightValueReturnType, "Text", StringComparison.Ordinal) ||
                 string.Equals(leftValueReturnType, "byte[]", StringComparison.Ordinal) ||
                 string.Equals(rightValueReturnType, "byte[]", StringComparison.Ordinal)))
            {
                returnType = "Text";
                return true;
            }

            if (string.Equals(leftValueReturnType, "Number", StringComparison.Ordinal) &&
                string.Equals(rightValueReturnType, "Number", StringComparison.Ordinal))
            {
                returnType = "Number";
                return true;
            }

            if (string.Equals(leftValueReturnType, rightValueReturnType, StringComparison.Ordinal) &&
                (string.Equals(leftValueReturnType, "Date", StringComparison.Ordinal) ||
                 string.Equals(leftValueReturnType, "Time", StringComparison.Ordinal)))
            {
                returnType = leftValueReturnType;
                return true;
            }

            return false;
        }

        if (TryParseFunctionCall(trimmed, out var functionName, out var args))
        {
            if (task is not null &&
                TryResolveSourceDotNetMethodCallReturnType(
                    functionName,
                    args.Count,
                    task,
                    Array.Empty<DataObjectDef>(),
                    out returnType))
                return true;

            if (IsTopLevelCall(functionName, "u.If") && args.Count == 3)
            {
                if (!TryResolveKnownExpressionReturnTypeWithoutLegacy(task, args[1].Trim(), out var trueReturnType) ||
                    !TryResolveKnownExpressionReturnTypeWithoutLegacy(task, args[2].Trim(), out var falseReturnType))
                    return false;

                var unified = UnifyExpressionTypes(
                    CreateResolvedExpressionTypeInfo(trueReturnType),
                    CreateResolvedExpressionTypeInfo(falseReturnType));
                if (!unified.IsResolved)
                    return false;

                returnType = unified.ReturnType;
                return true;
            }

            if (IsScalarCastFunctionName(functionName, XpaType.Text) ||
                string.Equals(functionName, "CastToText", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(functionName, "u.CastToText", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(functionName, "StrToken", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(functionName, "u.StrToken", StringComparison.OrdinalIgnoreCase) ||
                IsTopLevelCall(functionName, "u.ByteArrayToText") ||
                IsTopLevelCall(functionName, "u.DStr") ||
                IsTopLevelCall(functionName, "u.TStr") ||
                IsTopLevelCall(functionName, "u.MTStr") ||
                IsTopLevelCall(functionName, "u.Str") ||
                IsTopLevelCall(functionName, "u.RepStr") ||
                IsKnownTextProducingFunction(functionName))
                returnType = "Text";
            else if (IsScalarCastFunctionName(functionName, XpaType.Number) ||
                     string.Equals(functionName, "LoopCounter", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(functionName, "u.LoopCounter", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(functionName, "StrTokenCnt", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(functionName, "u.StrTokenCnt", StringComparison.OrdinalIgnoreCase) ||
                     IsNumericProjectionFunctionName(functionName) ||
                     IsTopLevelCall(functionName, "u.TaskInstance") ||
                     IsTopLevelCall(functionName, "u.Val") ||
                     IsTopLevelCall(functionName, "u.StrNum") ||
                     IsTopLevelCall(functionName, "u.ASCIIVal") ||
                     IsTopLevelCall(functionName, "u.Len") ||
                     IsTopLevelCall(functionName, "u.InStr") ||
                     IsTopLevelCall(functionName, "u.IndexOf") ||
                     IsTopLevelCall(functionName, "u.StrTokenCnt") ||
                     IsTopLevelCall(functionName, "u.DOW") ||
                     IsTopLevelCall(functionName, "u.Year") ||
                     IsTopLevelCall(functionName, "u.Month") ||
                     IsTopLevelCall(functionName, "u.Day") ||
                     IsTopLevelCall(functionName, "u.DBName") ||
                     IsTopLevelCall(functionName, "u.LoopCounter") ||
                     IsTopLevelCall(functionName, "JSONInsert") ||
                     IsTopLevelCall(functionName, "JSONModify") ||
                     IsTopLevelCall(functionName, "JSONDelete") ||
                     IsTopLevelCall(functionName, "JSONFind") ||
                     IsTopLevelCall(functionName, "JSONCnt"))
                returnType = "Number";
            else if (IsScalarCastFunctionName(functionName, XpaType.Date) ||
                     IsTopLevelCall(functionName, "u.ToDate") ||
                     IsTopLevelCall(functionName, "u.DVal"))
                returnType = "Date";
            else if (IsScalarCastFunctionName(functionName, XpaType.Time) ||
                     IsTopLevelCall(functionName, "u.ToTime") ||
                     IsTopLevelCall(functionName, "UserMethods.ToTime") ||
                     IsTopLevelCall(functionName, "u.TVal"))
                returnType = "Time";
            else if (IsScalarCastFunctionName(functionName, XpaType.Bool) ||
                     IsTopLevelCall(functionName, "u.GetBoolParam") ||
                     IsTopLevelCall(functionName, "u.Not") ||
                     IsTopLevelCall(functionName, "JSONExist") ||
                     IsTopLevelCall(functionName, "u.DataViewToText") ||
                     IsTopLevelCall(functionName, "DataViewToText") ||
                     IsTopLevelCall(functionName, "u.DataViewToHTML") ||
                     IsTopLevelCall(functionName, "DataViewToHTML") ||
                     IsTopLevelCall(functionName, "u.DataViewToXML") ||
                     IsTopLevelCall(functionName, "DataViewToXML"))
                returnType = "Bool";
            else if (IsScalarCastFunctionName(functionName, XpaType.Blob) || IsTopLevelCall(functionName, "u.File2Blb"))
                returnType = "byte[]";
            else if (TryResolveSafeLegacyFunctionReturnContract(functionName, args, out var contractedReturnType))
                returnType = contractedReturnType;
            else if (IsTopLevelCall(functionName, "u.DataViewToDNDataTable") ||
                     IsTopLevelCall(functionName, "DataViewToDNDataTable"))
                returnType = "object";

            return !string.IsNullOrWhiteSpace(returnType);
        }

        return false;
    }

    private static bool TryResolveKnownStatementExpressionReturnType(string expression, out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = StripRedundantOuterParentheses(expression.Trim()).TrimEnd(';').Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        if (SplitTopLevelAssignmentExpression(trimmed) is not null)
        {
            returnType = "void";
            return true;
        }

        if (expression.TrimEnd().EndsWith(";", StringComparison.Ordinal) &&
            TryParseFunctionCall(trimmed, out var functionName, out _) &&
            IsKnownXpaStatementFunction(functionName))
        {
            returnType = "void";
            return true;
        }

        return false;
    }

    private static string MapXpaTypeToReturnTypeCentral(XpaType xpaType)
    {
        return xpaType switch
        {
            XpaType.Text => "Text",
            XpaType.Number => "Number",
            XpaType.Date => "Date",
            XpaType.Time => "Time",
            XpaType.Bool => "Bool",
            XpaType.Blob => "byte[]",
            XpaType.Object => "object",
            _ => ""
        };
    }

    private static string NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(string expression, string expectedReturnType)
        => NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(expression, expectedReturnType, null);

    private static string NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(
        string expression,
        string expectedReturnType,
        TaskSemantic? task)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var rawTrimmed = StripRedundantOuterParentheses(expression.Trim());
        if (IsKnownExpressionReturnTypeCompatible(rawTrimmed, expectedReturnType, task))
            return rawTrimmed;
        var actualReturnTypeKnownWithoutLegacy =
            TryResolveKnownExpressionReturnTypeWithoutLegacy(task, rawTrimmed, out _);

        if (TryNormalizeTemporalScalarExpectedExpressionWithoutLegacy(
                rawTrimmed,
                expectedReturnType,
                task,
                out var temporalScalar))
            return temporalScalar;

        if (!actualReturnTypeKnownWithoutLegacy)
        {
            TrackLegacyExpressionTreatment(
                "NormalizeType",
                nameof(NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral),
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"expected={expectedReturnType} expr={rawTrimmed} task={(task is null ? "<none>" : task.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture))}"));
        }
        if (TrySplitLeadingOpaqueInlineComment(rawTrimmed, out var leadingComment, out var uncommented))
        {
            var rewritten = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(
                uncommented,
                expectedReturnType,
                task);
            return string.IsNullOrWhiteSpace(rewritten) ? rawTrimmed : $"{leadingComment} {rewritten}";
        }

        // Preserve unresolved argument placeholders like `/*arg XX*/ null` so comment
        // delimiters never leak into arithmetic normalization as `/` and `*`.
        if (HasOpaqueInlineComment(rawTrimmed))
            return rawTrimmed;

        if (TryUnwrapNonBlobByteArrayToTextForExpectedScalar(rawTrimmed, expectedReturnType, task, out var unwrappedNonBlob))
            return NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(unwrappedNonBlob, expectedReturnType, task);

        if (string.Equals(expectedReturnType, "Text", StringComparison.Ordinal))
        {
            rawTrimmed = RewriteFunctionCalls(
                rawTrimmed,
                "u.GetNumberParam",
                args => $"u.GetTextParam({string.Join(", ", args)})");
        }
        if (TryParseFunctionCall(rawTrimmed, out var rawFunctionName, out var rawArgs) &&
            IsTopLevelCall(rawFunctionName, "u.CastToNumber") &&
            rawArgs.Count == 1 &&
            string.Equals(expectedReturnType, "Number", StringComparison.Ordinal))
        {
            var normalizedInner = NormalizeCastToNumberInputUsingResolvedTypeCentral(rawArgs[0].Trim(), task);
            if (IsTopLevelCall(TryGetTopLevelFunctionName(normalizedInner), "u.CastToNumber"))
                return normalizedInner;
            if (string.Equals(normalizedInner, rawArgs[0].Trim(), StringComparison.Ordinal))
                return rawTrimmed;
            return $"u.CastToNumber({normalizedInner})";
        }

        var trimmed = StripAccidentalScalarWrapperForExpectedTypeCentral(
            rawTrimmed,
            expectedReturnType);

        if (string.Equals(expectedReturnType, "Text", StringComparison.Ordinal) &&
            TryParseFunctionCall(trimmed, out var textFunctionName, out var textFunctionArgs) &&
            IsTopLevelCall(textFunctionName, "u.EditGet") &&
            textFunctionArgs.Count == 0)
        {
            return EmitScalarArgumentFromEvidence(trimmed, "Text");
        }

        if (TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            IsTopLevelCall(functionName, "u.If") &&
            args.Count == 3)
        {
            var condition = CollapseRedundantScalarCastWrappersDeep(args[0].Trim());
            var whenTrue = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(args[1].Trim(), expectedReturnType, task);
            var whenFalse = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(args[2].Trim(), expectedReturnType, task);
            return CollapseRedundantScalarCastWrappersDeep($"u.If({condition}, {whenTrue}, {whenFalse})");
        }

        if (TryParseFunctionCall(trimmed, out functionName, out args) &&
            IsTopLevelCall(functionName, "u.CastToNumber") &&
            args.Count == 1)
        {
            var normalizedInner = NormalizeCastToNumberInputUsingResolvedTypeCentral(args[0].Trim(), task);
            if (IsTopLevelCall(TryGetTopLevelFunctionName(normalizedInner), "u.CastToNumber"))
                return normalizedInner;
            if (string.Equals(normalizedInner, args[0].Trim(), StringComparison.Ordinal))
                return trimmed;
            return $"u.CastToNumber({normalizedInner})";
        }

        if (string.Equals(expectedReturnType, "Time", StringComparison.Ordinal) &&
            TryParseFunctionCall(trimmed, out functionName, out args) &&
            args.Count == 1 &&
            (IsTopLevelCall(functionName, "u.CastToTime") ||
             IsTopLevelCall(functionName, "u.ToTime") ||
             IsTopLevelCall(functionName, "UserMethods.ToTime")) &&
            SplitTopLevelArithmeticExpression(args[0].Trim()) is not null)
        {
            var normalizedInner = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(args[0].Trim(), "Number", task);
            return $"{functionName}({normalizedInner})";
        }

        if (string.Equals(expectedReturnType, "Number", StringComparison.Ordinal) &&
            SplitTopLevelArithmeticExpression(trimmed) is { } arithmetic)
        {
            var leftOriginal = arithmetic.Left.Trim();
            var rightOriginal = arithmetic.Right.Trim();
            var left = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(leftOriginal, "Number", task);
            var right = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(rightOriginal, "Number", task);
            left = NormalizeTemporalOperandForNumericArithmetic(left, leftOriginal, task);
            right = NormalizeTemporalOperandForNumericArithmetic(right, rightOriginal, task);
            return $"{left} {arithmetic.Operator} {right}";
        }

        if ((string.Equals(expectedReturnType, "Date", StringComparison.Ordinal) ||
             string.Equals(expectedReturnType, "Time", StringComparison.Ordinal)) &&
            SplitTopLevelArithmeticExpression(trimmed) is { } temporalArithmetic)
        {
            var leftOriginal = temporalArithmetic.Left.Trim();
            var rightOriginal = temporalArithmetic.Right.Trim();
            var left = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(leftOriginal, "Number", task);
            var right = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(rightOriginal, "Number", task);
            left = NormalizeTemporalOperandForNumericArithmetic(left, leftOriginal, task);
            right = NormalizeTemporalOperandForNumericArithmetic(right, rightOriginal, task);
            var numericExpression = $"{left} {temporalArithmetic.Operator} {right}";
            return string.Equals(expectedReturnType, "Date", StringComparison.Ordinal)
                ? $"u.CastToDate({numericExpression})"
                : $"u.CastToTime({numericExpression})";
        }

        if (task is not null && IsSimpleIdentifierPath(trimmed))
        {
            if (TryResolveSimpleSourceReturnTypeFromResourcePath(task, trimmed, out var pathReturnType))
            {
                var sourceXpaType = XpaTypeEngine.MapExpectedToXpaType(pathReturnType);
                var expectedXpaType = XpaTypeEngine.MapExpectedToXpaType(expectedReturnType);
                if (sourceXpaType != XpaType.Unknown && expectedXpaType != XpaType.Unknown)
                {
                    var materializedSource = MaterializeSimpleSourceExpressionForReturnType(trimmed, pathReturnType);
                    if (sourceXpaType == expectedXpaType)
                        return materializedSource;

                    if (XpaTypeEngine.CanCoerce(sourceXpaType, expectedXpaType))
                        return XpaTypeEngine.Coerce(materializedSource, sourceXpaType, expectedXpaType);
                }
            }

            if (TryResolveSimpleExpressionValueInfo(task, trimmed, out var sourceInfo))
            {
                var sourceAttrObj = ResolveEffectiveTargetAttrObj(
                    NormalizeAttrObjKind(sourceInfo.AttrObj),
                    NormalizeAttrObjKind(sourceInfo.ModelAttrObj));
                var sourceReturnType = NormalizeReturnTypeToken(MapAttrObjToReturnType(sourceAttrObj));
                if (string.IsNullOrWhiteSpace(sourceReturnType))
                {
                    if (sourceInfo.IsBoolean)
                        sourceReturnType = "Bool";
                    else if (sourceInfo.IsNumeric)
                        sourceReturnType = "Number";
                    else if (sourceInfo.IsBlob)
                        sourceReturnType = "byte[]";
                }

                var sourceXpaType = XpaTypeEngine.MapExpectedToXpaType(sourceReturnType);
                var expectedXpaType = XpaTypeEngine.MapExpectedToXpaType(expectedReturnType);
                if (sourceXpaType != XpaType.Unknown && expectedXpaType != XpaType.Unknown)
                {
                    var materializedSource = MaterializeSimpleSourceExpressionForReturnType(trimmed, sourceReturnType);
                    if (sourceXpaType == expectedXpaType)
                        return materializedSource;

                    if (XpaTypeEngine.CanCoerce(sourceXpaType, expectedXpaType))
                        return XpaTypeEngine.Coerce(materializedSource, sourceXpaType, expectedXpaType);
                }
            }

            var declaredReturnType = NormalizeReturnTypeToken(ResolveExpressionReturnType(null, trimmed, task));
            if (string.Equals(GetValueReturnType(declaredReturnType), expectedReturnType, StringComparison.Ordinal))
                return NormalizeExpressionForDeclaredReturnTypeCentral(trimmed, expectedReturnType, null);
        }

        if (task is not null &&
            string.Equals(expectedReturnType, "Number", StringComparison.Ordinal) &&
            TryParseFunctionCall(trimmed, out var localFunctionName, out var localFunctionArgs) &&
            localFunctionArgs.Count == 0 &&
            localFunctionName.StartsWith("Exp_", StringComparison.Ordinal) &&
            int.TryParse(localFunctionName["Exp_".Length..], out var localExpressionOrdinal) &&
            task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(localExpressionOrdinal, out var localExpression) &&
            localExpression is not null)
        {
            var localExpected = ExpectedTypeForExpressionAttribute(localExpression.Attribute);
            var localReturnType = GetValueReturnType(localExpected.ReturnType);
            if (string.Equals(localExpected.AttrObj, "FIELD_DATE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(localExpected.AttrObj, "FIELD_TIME", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(localReturnType, "Date", StringComparison.Ordinal) ||
                string.Equals(localReturnType, "Time", StringComparison.Ordinal))
                return $"u.ToNumber({trimmed})";
        }

        var resolved = ResolveExpressionTypeFromEvidence(task, trimmed);
        if (resolved.IsResolved &&
            string.Equals(expectedReturnType, "Number", StringComparison.Ordinal))
        {
            var resolvedReturnType = GetValueReturnType(resolved.ReturnType);
            if (string.Equals(resolvedReturnType, "Date", StringComparison.Ordinal) ||
                string.Equals(resolvedReturnType, "Time", StringComparison.Ordinal))
            {
                var normalizedTemporal = NormalizeExpressionForDeclaredReturnTypeCentral(trimmed, resolvedReturnType, null);
                return $"u.ToNumber({normalizedTemporal})";
            }
        }

        if (string.Equals(expectedReturnType, "Time", StringComparison.Ordinal) &&
            TryParseFunctionCall(trimmed, out var timeFunctionName, out var timeArgs) &&
            timeArgs.Count == 1 &&
            (IsTopLevelCall(timeFunctionName, "u.ToTime") ||
             IsTopLevelCall(timeFunctionName, "UserMethods.ToTime") ||
             IsTopLevelCall(timeFunctionName, "u.CastToTime")))
        {
            var inner = timeArgs[0].Trim();
            if (task is not null && IsTimeTypedExpression(inner, task))
                return inner;

            if (task is not null &&
                TryResolveSimpleSourceReturnTypeFromResourcePath(task, inner, out var innerSourceReturnType) &&
                string.Equals(GetValueReturnType(NormalizeReturnTypeToken(innerSourceReturnType)), "Time", StringComparison.Ordinal))
                return inner;

            var innerResolved = ResolveExpressionTypeFromEvidence(task, inner);
            if (string.Equals(GetValueReturnType(innerResolved.ReturnType), "Time", StringComparison.Ordinal))
                return NormalizeExpressionForDeclaredReturnTypeCentral(inner, "Time", null);
        }

        if (resolved.IsResolved &&
            string.Equals(expectedReturnType, "Time", StringComparison.Ordinal) &&
            resolved.XpaType == XpaType.Number)
        {
            var numericExpression = EmitScalarArgumentFromEvidence(
                StripExpectedAttributeCastWrappers(
                    NormalizeNumericConditionalBranchExpressionCentral(trimmed),
                    "FIELD_NUMERIC"),
                "Number",
                task);
            return $"u.ToTime({numericExpression})";
        }

        if (resolved.IsResolved &&
            string.Equals(GetValueReturnType(resolved.ReturnType), expectedReturnType, StringComparison.Ordinal))
            return NormalizeExpressionForDeclaredReturnTypeCentral(trimmed, expectedReturnType, null);

        if (task is not null)
        {
            var coerced = CoerceExpressionWithTypeEngine(
                trimmed,
                task,
                ExpectedTypeForReturnType(expectedReturnType),
                alwaysCoerceWholeExpression: true);
            if (!string.Equals(coerced, trimmed, StringComparison.Ordinal))
                return coerced;

            var attrObj = MapReturnTypeToAttrObj(expectedReturnType);
            if (!string.IsNullOrWhiteSpace(attrObj))
                return ApplyAttributeCastCentral(trimmed, attrObj);

            return coerced;
        }

        return EmitScalarArgumentFromEvidence(trimmed, expectedReturnType);
    }

    private static string MaterializeSimpleSourceExpressionForReturnType(string expression, string sourceReturnType)
    {
        var valueReturnType = GetValueReturnType(NormalizeReturnTypeToken(sourceReturnType));
        return valueReturnType switch
        {
            "Text" => $"u.CastToText({expression})",
            "Number" => $"u.CastToNumber({expression})",
            "Date" => $"u.CastToDate({expression})",
            "Time" => $"u.CastToTime({expression})",
            "Bool" => $"u.CastToBool({expression})",
            "byte[]" => $"u.CastToByteArray({expression})",
            _ => expression
        };
    }

    private static bool TryUnwrapNonBlobByteArrayToTextForExpectedScalar(
        string expression,
        string expectedReturnType,
        TaskSemantic? task,
        out string unwrapped)
    {
        unwrapped = "";
        if (task is null || string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            args.Count == 1)
        {
            if (IsTopLevelCall(functionName, "u.ByteArrayToText") &&
                TryResolveNonBlobSimpleSourceReturnType(task, args[0].Trim(), out _))
            {
                unwrapped = args[0].Trim();
                return true;
            }

            if (IsExpectedScalarCastFunction(functionName, expectedReturnType) &&
                TryParseFunctionCall(args[0].Trim(), out var innerFunctionName, out var innerArgs) &&
                IsTopLevelCall(innerFunctionName, "u.ByteArrayToText") &&
                innerArgs.Count == 1 &&
                TryResolveNonBlobSimpleSourceReturnType(task, innerArgs[0].Trim(), out _))
            {
                unwrapped = innerArgs[0].Trim();
                return true;
            }
        }

        return false;
    }

    private static bool IsExpectedScalarCastFunction(string functionName, string expectedReturnType)
        => (string.Equals(expectedReturnType, "Text", StringComparison.Ordinal) && IsTopLevelCall(functionName, "u.CastToText")) ||
           (string.Equals(expectedReturnType, "Number", StringComparison.Ordinal) && IsTopLevelCall(functionName, "u.CastToNumber")) ||
           (string.Equals(expectedReturnType, "Date", StringComparison.Ordinal) && IsTopLevelCall(functionName, "u.CastToDate")) ||
           (string.Equals(expectedReturnType, "Time", StringComparison.Ordinal) && IsTopLevelCall(functionName, "u.CastToTime")) ||
           (string.Equals(expectedReturnType, "Bool", StringComparison.Ordinal) && IsTopLevelCall(functionName, "u.CastToBool"));

    private static bool TryResolveNonBlobSimpleSourceReturnType(
        TaskSemantic task,
        string expression,
        out string returnType)
    {
        returnType = "";
        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (TryResolveSimpleSourceReturnTypeFromResourcePath(task, trimmed, out returnType))
            return !string.Equals(returnType, "byte[]", StringComparison.Ordinal);

        if (!TryResolveSimpleExpressionValueInfo(task, trimmed, out var sourceInfo))
            return false;

        var sourceAttrObj = ResolveEffectiveTargetAttrObj(
            NormalizeAttrObjKind(sourceInfo.AttrObj),
            NormalizeAttrObjKind(sourceInfo.ModelAttrObj));
        returnType = NormalizeReturnTypeToken(MapAttrObjToReturnType(sourceAttrObj));
        if (string.IsNullOrWhiteSpace(returnType))
        {
            if (sourceInfo.IsBoolean)
                returnType = "Bool";
            else if (sourceInfo.IsNumeric)
                returnType = "Number";
            else if (sourceInfo.IsBlob)
                returnType = "byte[]";
        }

        return !string.IsNullOrWhiteSpace(returnType) &&
               !string.Equals(returnType, "byte[]", StringComparison.Ordinal);
    }

    private static bool TryResolveSimpleSourceReturnTypeFromResourcePath(
        TaskSemantic task,
        string expression,
        out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(expression) || !IsSimpleIdentifierPath(expression))
            return false;

        var resource = ResolveResourceByTargetPath(task, expression.Trim(), _allTasks ?? Array.Empty<TaskSemantic>());
        if (resource is null && TryResolveDataViewMemberColumn(task, expression.Trim(), out _, out var dataColumn))
        {
            var dataColumnAttrObj = ResolveEffectiveDataColumnAttrObj(dataColumn);
            returnType = NormalizeReturnTypeToken(MapAttrObjToReturnType(dataColumnAttrObj));
            return !string.IsNullOrWhiteSpace(returnType);
        }

        if (resource is null)
            return TryResolveSimpleSourceReturnTypeByUniqueMemberName(expression, out returnType);

        var ownerTask = ResolveOwningTaskForResource(resource) ?? task;
        var resolvedType = ResolveTaskResourceColumnType(resource, _allFieldModels, ownerTask);
        var attrObj = ResolveAttrObjForColumnType(resolvedType, _allFieldModels, ownerTask);
        if (string.IsNullOrWhiteSpace(attrObj))
            attrObj = ResolveEffectiveTaskResourceAttrObj(resource, ownerTask);

        returnType = NormalizeReturnTypeToken(MapAttrObjToReturnType(attrObj));
        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static bool TryResolveSimpleSourceReturnTypeByDataObjectMemberPath(
        string expression,
        out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(expression) ||
            !expression.Contains('.', StringComparison.Ordinal) ||
            _allTasks is null)
            return false;

        var task = _allTasks.FirstOrDefault(t => t.MainProgram) ?? _allTasks.FirstOrDefault();
        if (task is null ||
            !TryResolveDataViewMemberColumn(task, expression.Trim(), out _, out var dataColumn))
            return false;

        var attrObj = ResolveEffectiveDataColumnAttrObj(dataColumn);
        returnType = NormalizeReturnTypeToken(MapAttrObjToReturnType(attrObj));
        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static bool TryResolveSimpleSourceReturnTypeByUniqueMemberName(
        string expression,
        out string returnType)
    {
        returnType = "";
        if (_allTasks is null)
            return false;

        var memberName = expression.Trim();
        while (memberName.StartsWith("_parent.", StringComparison.Ordinal))
            memberName = memberName["_parent.".Length..];

        if (memberName.EndsWith(".Value", StringComparison.Ordinal))
            memberName = memberName[..^".Value".Length];

        if (string.IsNullOrWhiteSpace(memberName) || memberName.Contains('.', StringComparison.Ordinal))
            return false;

        string? resolvedReturnType = null;
        foreach (var candidateTask in _allTasks)
        {
            foreach (var resource in candidateTask.ResourcesSemantic.Ordered)
            {
                var candidateMember = ResolveTaskResourceMemberName(candidateTask, resource);
                if (!string.Equals(candidateMember, memberName, StringComparison.Ordinal))
                    continue;

                var resolvedType = ResolveTaskResourceColumnType(resource, _allFieldModels, candidateTask);
                var attrObj = ResolveAttrObjForColumnType(resolvedType, _allFieldModels, candidateTask);
                if (string.IsNullOrWhiteSpace(attrObj))
                    attrObj = ResolveEffectiveTaskResourceAttrObj(resource, candidateTask);

                var candidateReturnType = NormalizeReturnTypeToken(MapAttrObjToReturnType(attrObj));
                if (string.IsNullOrWhiteSpace(candidateReturnType))
                    continue;

                if (resolvedReturnType is null)
                {
                    resolvedReturnType = candidateReturnType;
                    continue;
                }

                if (!string.Equals(resolvedReturnType, candidateReturnType, StringComparison.Ordinal))
                    return false;
            }
        }

        returnType = resolvedReturnType ?? "";
        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static bool TryNormalizeTemporalScalarExpectedExpressionWithoutLegacy(
        string expression,
        string expectedReturnType,
        TaskSemantic? task,
        out string normalized)
    {
        normalized = expression;
        var normalizedExpected = GetValueReturnType(NormalizeReturnTypeToken(expectedReturnType));

        if (string.Equals(normalizedExpected, "Time", StringComparison.Ordinal) &&
            TryParseFunctionCall(expression, out var timeFunctionName, out var timeArgs) &&
            timeArgs.Count == 1 &&
            (IsTopLevelCall(timeFunctionName, "u.ToTime") ||
             IsTopLevelCall(timeFunctionName, "u.CastToTime") ||
             IsTopLevelCall(timeFunctionName, "UserMethods.ToTime")) &&
            SplitTopLevelArithmeticExpression(timeArgs[0].Trim()) is not null &&
            TryNormalizeTemporalScalarExpectedExpressionWithoutLegacy(
                timeArgs[0].Trim(),
                "Number",
                task,
                out var numericInner))
        {
            normalized = $"{timeFunctionName}({numericInner})";
            return true;
        }

        if (!string.Equals(normalizedExpected, "Number", StringComparison.Ordinal))
            return false;

        if (SplitTopLevelArithmeticExpression(expression) is { } arithmetic)
        {
            var leftOriginal = arithmetic.Left.Trim();
            var rightOriginal = arithmetic.Right.Trim();
            var left = NormalizeTemporalNumericOperandWithoutLegacy(leftOriginal, task);
            var right = NormalizeTemporalNumericOperandWithoutLegacy(rightOriginal, task);
            if (!string.Equals(left, leftOriginal, StringComparison.Ordinal) ||
                !string.Equals(right, rightOriginal, StringComparison.Ordinal))
            {
                normalized = $"{left} {arithmetic.Operator} {right}";
                return true;
            }
        }

        var operand = NormalizeTemporalNumericOperandWithoutLegacy(expression, task);
        if (!string.Equals(operand, expression, StringComparison.Ordinal))
        {
            normalized = operand;
            return true;
        }

        return false;
    }

    private static string NormalizeTemporalNumericOperandWithoutLegacy(string expression, TaskSemantic? task)
    {
        if (!TryResolveKnownExpressionReturnTypeWithoutLegacy(task, expression, out var returnType))
            return expression;

        var valueReturnType = GetValueReturnType(NormalizeReturnTypeToken(returnType));
        if (!string.Equals(valueReturnType, "Date", StringComparison.Ordinal) &&
            !string.Equals(valueReturnType, "Time", StringComparison.Ordinal))
            return expression;

        return $"u.ToNumber({NormalizeExpressionForDeclaredReturnTypeCentral(expression, valueReturnType, null)})";
    }

    private static string NormalizeTemporalOperandForNumericArithmetic(string normalized, string original, TaskSemantic? task)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return normalized;

        var resolved = ResolveExpressionTypeFromEvidence(task, original.Trim());
        var returnType = GetValueReturnType(resolved.ReturnType);
        if ((string.Equals(returnType, "Date", StringComparison.Ordinal) ||
             string.Equals(returnType, "Time", StringComparison.Ordinal)) &&
            !normalized.StartsWith("u.ToNumber(", StringComparison.Ordinal))
        {
            return TrackLegacyExpressionTreatmentIfChanged(
                "NormalizeType",
                nameof(NormalizeTemporalOperandForNumericArithmetic),
                normalized,
                $"u.ToNumber({normalized})");
        }

        return normalized;
    }

    private static string NormalizeCastToNumberInputUsingResolvedTypeCentral(string expression, TaskSemantic? task)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (IsKnownExpressionReturnTypeCompatible(trimmed, "Number", task))
            return trimmed;

        TrackLegacyExpressionTreatment(
            "NormalizeType",
            nameof(NormalizeCastToNumberInputUsingResolvedTypeCentral),
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"expr={trimmed} task={(task is null ? "<none>" : task.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture))}"));
        if (TrySplitLeadingOpaqueInlineComment(trimmed, out var leadingComment, out var uncommented))
        {
            var rewritten = NormalizeCastToNumberInputUsingResolvedTypeCentral(uncommented, task);
            return string.IsNullOrWhiteSpace(rewritten) ? trimmed : $"{leadingComment} {rewritten}";
        }

        if (HasOpaqueInlineComment(trimmed))
            return trimmed;

        if (TryParseFunctionCall(trimmed, out var castFunctionName, out var castArgs) &&
            IsTopLevelCall(castFunctionName, "u.CastToNumber") &&
            castArgs.Count == 1)
            return trimmed;

        if (TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            IsTopLevelCall(functionName, "u.If") &&
            args.Count == 3)
        {
            var whenTrue = args[1].Trim();
            var whenFalse = args[2].Trim();
            var whenTrueSource = whenTrue;
            var whenFalseSource = whenFalse;

            var trueType = GetValueReturnType(ResolveExpressionTypeFromEvidence(task, whenTrue).ReturnType);
            var falseType = GetValueReturnType(ResolveExpressionTypeFromEvidence(task, whenFalse).ReturnType);
            var trueCanNormalizeAsNumber = CanNormalizeCastToNumberBranchAsNumber(whenTrue, trueType, task);
            var falseCanNormalizeAsNumber = CanNormalizeCastToNumberBranchAsNumber(whenFalse, falseType, task);

            if (trueCanNormalizeAsNumber)
                trueType = "Number";
            if (falseCanNormalizeAsNumber)
                falseType = "Number";

            if (trueCanNormalizeAsNumber && falseCanNormalizeAsNumber)
            {
                var normalizedTrueNumeric = EmitScalarArgumentFromEvidence(
                    NormalizeNumericConditionalBranchExpressionCentral(
                        UnwrapNumericTextBranchForCastToNumber(whenTrueSource, task)),
                    "Number");
                var normalizedFalseNumeric = EmitScalarArgumentFromEvidence(
                    NormalizeNumericConditionalBranchExpressionCentral(
                        UnwrapNumericTextBranchForCastToNumber(whenFalseSource, task)),
                    "Number");
                return $"u.If({args[0].Trim()}, {normalizedTrueNumeric}, {normalizedFalseNumeric})";
            }

            if (TryNormalizeCastToNumberConditionalBranchAsNumber(whenTrueSource, task, out var fallbackTrueNumeric) &&
                TryNormalizeCastToNumberConditionalBranchAsNumber(whenFalseSource, task, out var fallbackFalseNumeric))
            {
                return $"u.If({args[0].Trim()}, {fallbackTrueNumeric}, {fallbackFalseNumeric})";
            }

            var branchReturnType =
                string.Equals(trueType, falseType, StringComparison.Ordinal) &&
                trueType is "Number" or "Date" or "Time" or "Bool" or "Text" or "byte[]"
                    ? trueType
                    : "Text";

            if (string.Equals(branchReturnType, "Text", StringComparison.Ordinal))
            {
                var distributedTrue = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(whenTrueSource, "Number", task);
                var distributedFalse = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(whenFalseSource, "Number", task);
                return $"u.If({args[0].Trim()}, {distributedTrue}, {distributedFalse})";
            }

            if (string.Equals(branchReturnType, "Number", StringComparison.Ordinal))
            {
                whenTrueSource = UnwrapNumericTextBranchForCastToNumber(whenTrueSource, task);
                whenFalseSource = UnwrapNumericTextBranchForCastToNumber(whenFalseSource, task);
            }

            var normalizedTrue = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(whenTrueSource, branchReturnType, task);
            var normalizedFalse = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(whenFalseSource, branchReturnType, task);
            return $"u.If({args[0].Trim()}, {normalizedTrue}, {normalizedFalse})";
        }

        var resolved = ResolveExpressionTypeFromEvidence(task, trimmed);
        var resolvedReturnType = GetValueReturnType(resolved.ReturnType);
        if (resolved.IsResolved && resolvedReturnType is "Number" or "Date" or "Time" or "Bool" or "Text" or "byte[]")
            return NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(trimmed, resolvedReturnType, task);

        return trimmed;
    }

    private static bool TryNormalizeCastToNumberConditionalBranchAsNumber(string expression, TaskSemantic? task, out string normalized)
    {
        normalized = string.Empty;
        var source = UnwrapNumericTextBranchForCastToNumber(expression, task);
        if (!CanNormalizeCastToNumberBranchAsNumber(source, GetValueReturnType(ResolveExpressionTypeFromEvidence(task, source).ReturnType), task))
            return false;

        normalized = EmitScalarArgumentFromEvidence(
            NormalizeNumericConditionalBranchExpressionCentral(source),
            "Number");
        return true;
    }

    private static bool CanNormalizeCastToNumberBranchAsNumber(string expression, string? resolvedReturnType, TaskSemantic? task)
    {
        var normalizedReturnType = GetValueReturnType(resolvedReturnType);
        if (string.Equals(normalizedReturnType, "Number", StringComparison.Ordinal))
            return true;

        var trimmed = StripRedundantOuterParentheses(expression?.Trim() ?? "");
        if (trimmed.Length == 0)
            return false;

        while (TryUnwrapCastToTextExpressionCentral(trimmed, out var castTextInner))
            trimmed = StripRedundantOuterParentheses(castTextInner.Trim());

        if (IsNumericLiteralExpressionCentral(trimmed) ||
            double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            return true;

        var arithmetic = SplitTopLevelArithmeticExpression(trimmed);
        if (arithmetic is not null)
        {
            var leftIsNumeric = CanNormalizeCastToNumberBranchAsNumber(arithmetic.Value.Left.Trim(), null, task);
            var rightIsNumeric = CanNormalizeCastToNumberBranchAsNumber(arithmetic.Value.Right.Trim(), null, task);
            if (leftIsNumeric && rightIsNumeric)
                return true;
        }

        var inferred = ResolveExpressionTypeFromEvidence(task, trimmed);
        if (string.Equals(GetValueReturnType(inferred.ReturnType), "Number", StringComparison.Ordinal))
            return true;

        if (task is not null)
        {
            var expected = ResolveExpectedTypeFromExpressionEvidence(task, trimmed);
            if (string.Equals(expected.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetValueReturnType(expected.ReturnType), "Number", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string UnwrapNumericTextBranchForCastToNumber(string expression, TaskSemantic? task)
    {
        var trimmed = StripRedundantOuterParentheses(expression?.Trim() ?? "");
        if (trimmed.Length == 0)
            return trimmed;

        if (!TryUnwrapCastToTextExpressionCentral(trimmed, out var castTextInner))
            return trimmed;

        var inner = StripRedundantOuterParentheses(castTextInner.Trim());
        return CanNormalizeCastToNumberBranchAsNumber(inner, GetValueReturnType(ResolveExpressionTypeFromEvidence(task, inner).ReturnType), task)
            ? inner
            : trimmed;
    }
}

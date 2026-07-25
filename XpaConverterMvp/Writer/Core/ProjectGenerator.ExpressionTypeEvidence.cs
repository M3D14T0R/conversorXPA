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
        return TryResolveKnownExpressionReturnTypeWithoutLegacy(task, expression, out var returnType)
            ? CreateResolvedExpressionTypeInfo(returnType)
            : ExpressionTypeInfo.Unknown;
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
        {
            if (IsJavaGetStaticFunctionName(normalizedFunction) &&
                TryResolveKnownXpaFunctionReturnType(functionName, args, out var javaStaticReturnType))
            {
                javaStaticReturnType = NormalizeReturnTypeToken(javaStaticReturnType);
                if (IsSafeLegacyContractReturnType(javaStaticReturnType))
                {
                    returnType = javaStaticReturnType;
                    return true;
                }
            }

            return false;
        }

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
           IsJavaGetStaticFunctionName(normalizedFunction) ||
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

        var unified = XpaExpressionTypeMap.Unify(left.XpaType, right.XpaType);
        if (unified == XpaType.Unknown)
            return new("object", XpaType.Object, true, true);

        return CreateResolvedExpressionTypeInfo(MapXpaTypeToReturnTypeCentral(unified));
    }

    private static ExpressionTypeInfo CreateResolvedExpressionTypeInfo(string? returnType)
    {
        var normalizedReturnType = NormalizeReturnTypeToken(returnType);
        var xpaType = XpaExpressionTypeMap.FromReturnType(GetValueReturnType(normalizedReturnType));
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
        // VarPrev/VarCurr carry XPA type evidence through their indexed column,
        // but the runtime method itself returns object. Treating that evidence as
        // CLR assignment compatibility skips the scalar materialization required
        // by typed parameters and operators. Keep the evidence for choosing the
        // destination type, while forcing the central normalizer to emit the cast.
        if (TryParseFunctionCall(trimmed, out var materializedFunctionName, out _) &&
            IsVarCurrentLikeFunctionName(materializedFunctionName))
            return false;

        if (string.Equals(normalizedExpected, "Number", StringComparison.Ordinal) &&
            (string.Equals(trimmed, "Counter", StringComparison.Ordinal) ||
             string.Equals(trimmed, "u.LoopCounter()", StringComparison.Ordinal) ||
             trimmed.StartsWith("u.StrTokenCnt(", StringComparison.Ordinal)))
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
            !string.IsNullOrWhiteSpace(registeredTypeInfo.ReturnType) &&
            !string.Equals(
                NormalizeReturnTypeToken(registeredTypeInfo.ReturnType),
                "object",
                StringComparison.Ordinal))
        {
            returnType = registeredTypeInfo.ReturnType;
            return true;
        }

        if (task is not null &&
            TryResolveSimpleSourceReturnTypeFromResourcePath(task, trimmed, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        if (task is not null &&
            TryResolveDataViewMemberColumn(task, trimmed, out _, out var taskDataColumn))
        {
            var taskColumnAttrObj = ResolveEffectiveDataColumnAttrObj(taskDataColumn);
            returnType = NormalizeReturnTypeToken(MapAttrObjToReturnType(taskColumnAttrObj));
            if (!string.IsNullOrWhiteSpace(returnType))
                return true;
        }

        if (TryResolveSimpleSourceReturnTypeByDataObjectMemberPath(trimmed, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        if (TryResolveSimpleSourceReturnTypeByUniqueMemberName(trimmed, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        if (
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

        if (IsCounterExpression(trimmed))
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
                IsVarCurrentLikeFunctionName(functionName) &&
                args.Count == 1 &&
                TryParseFunctionCall(args[0].Trim(), out var indexFunction, out var indexArgs) &&
                IsTopLevelCall(indexFunction, "u.IndexOf") &&
                indexArgs.Count >= 1)
            {
                if (TryResolveKnownExpressionReturnTypeWithoutLegacy(task, indexArgs[0].Trim(), out var indexedReturnType) &&
                    !string.IsNullOrWhiteSpace(indexedReturnType))
                {
                    returnType = indexedReturnType;
                    return true;
                }

                var indexedAttrObj = ResolveModelColumnAttrObj(task, indexArgs[0].Trim());
                var indexedModelReturnType = NormalizeReturnTypeToken(MapAttrObjToReturnType(indexedAttrObj));
                if (!string.IsNullOrWhiteSpace(indexedModelReturnType))
                {
                    returnType = indexedModelReturnType;
                    return true;
                }
            }

            if (task is not null &&
                TryGetAccessibleFunctionContract(task, functionName, out var functionContract, out _) &&
                !string.IsNullOrWhiteSpace(functionContract.ReturnType))
            {
                returnType = functionContract.ReturnType;
                return true;
            }

            if (TryGetComponentFunctionCallContract(functionName, out var componentContract) &&
                !string.IsNullOrWhiteSpace(componentContract.ReturnType))
            {
                returnType = componentContract.ReturnType;
                return true;
            }

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

            return TryResolveKnownXpaFunctionReturnType(functionName, args, out returnType) &&
                   !string.IsNullOrWhiteSpace(returnType);
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
        if (IsDotNetTaskResource(resource))
        {
            returnType = "object";
            return true;
        }

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

        var task = _applicationTask;
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

        if (_uniqueResourceReturnTypeByMemberName.TryGetValue(memberName, out var indexedReturnType))
        {
            returnType = indexedReturnType ?? "";
            return indexedReturnType is not null;
        }

        return false;
    }

    private static Dictionary<string, string?> BuildUniqueResourceReturnTypeByMemberNameIndex()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var candidateTask in _allTasks)
        {
            foreach (var resource in candidateTask.ResourcesSemantic.Ordered)
            {
                var candidateMember = ResolveTaskResourceMemberName(candidateTask, resource);
                var resolvedType = ResolveTaskResourceColumnType(resource, _allFieldModels, candidateTask);
                var attrObj = ResolveAttrObjForColumnType(resolvedType, _allFieldModels, candidateTask);
                if (string.IsNullOrWhiteSpace(attrObj))
                    attrObj = ResolveEffectiveTaskResourceAttrObj(resource, candidateTask);

                var candidateReturnType = NormalizeReturnTypeToken(MapAttrObjToReturnType(attrObj));
                if (string.IsNullOrWhiteSpace(candidateReturnType))
                    continue;

                if (!result.TryGetValue(candidateMember, out var resolvedReturnType))
                {
                    result[candidateMember] = candidateReturnType;
                    continue;
                }

                if (resolvedReturnType is not null &&
                    !string.Equals(resolvedReturnType, candidateReturnType, StringComparison.Ordinal))
                    result[candidateMember] = null;
            }
        }

        return result;
    }


}

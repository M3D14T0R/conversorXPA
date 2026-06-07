using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private const int StrictFunctionArgumentBridgeMaxDepth = 128;
    private static readonly EmittedExpressionEngine StrictEmittedExpressionEngine = new();
    private static readonly ConcurrentDictionary<string, StrictEmissionCacheEntry> StrictEmissionCache = new(StringComparer.Ordinal);

    private readonly record struct StrictEmissionCacheEntry(
        bool Success,
        string Code,
        string ReturnType = "",
        XpaType XpaType = XpaType.Unknown,
        EmittedExpressionTypeEvidenceKind EvidenceKind = default,
        string EvidenceSourceKey = "");

    private static bool TryEmitThroughStrictEmittedExpression(
        string code,
        TaskSemantic task,
        ExpressionEmissionContext context,
        out string emittedCode)
    {
        emittedCode = "";
        if (!TryEmitThroughStrictEmittedExpressionTyped(code, task, context, out var emitted))
            return false;

        emittedCode = emitted.Code;
        return true;
    }

    private static bool TryEmitThroughStrictEmittedExpressionTyped(
        string code,
        TaskSemantic task,
        ExpressionEmissionContext context,
        out StrictEmittedExpression emittedExpression)
    {
        emittedExpression = default;
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var cleanCode = StripRedundantOuterParentheses(code.Trim());
        var expectedReturnType = ResolveReturnTypeForExpectedContext(context.Expected);

        var cacheKey = CreateStrictEmissionCacheKey(cleanCode, task, context, expectedReturnType);
        if (StrictEmissionCache.TryGetValue(cacheKey, out var cached))
        {
            TrackExpressionEmissionAuditCacheHit(context, expectedReturnType, cached.Success);
            if (cached.Success)
            {
                emittedExpression = new StrictEmittedExpression(
                    cached.Code,
                    cached.ReturnType,
                    cached.XpaType,
                    cached.EvidenceKind,
                    cached.EvidenceSourceKey);
                emittedExpression = ApplyStrictNestedTextArgumentBridges(
                    emittedExpression,
                    cleanCode,
                    task,
                    context,
                    expectedReturnType);
                TrackCriticalExternalCoercionIfBridgeChanged(
                    "EmittedExpression",
                    nameof(TryEmitThroughStrictEmittedExpression),
                    cleanCode,
                    emittedExpression.Code,
                    string.Create(CultureInfo.InvariantCulture, $"cacheHit=true sink={context.SinkKind} expected={expectedReturnType} expr={TruncateTelemetryValue(cleanCode)}"));
            }
            return cached.Success;
        }

        if (TryCreateNoOpStrictEmission(cleanCode, task, context, expectedReturnType, out emittedExpression))
        {
            emittedExpression = ApplyStrictNestedTextArgumentBridges(
                emittedExpression,
                cleanCode,
                task,
                context,
                expectedReturnType);
            StrictEmissionCache.TryAdd(
                cacheKey,
                new StrictEmissionCacheEntry(
                    true,
                    emittedExpression.Code,
                    emittedExpression.ReturnType,
                    emittedExpression.XpaType,
                    emittedExpression.EvidenceKind,
                    emittedExpression.EvidenceSourceKey));
            TrackExpressionEmissionAudit(
                task,
                context,
                cleanCode,
                expectedReturnType,
                success: true,
                emittedExpression.ReturnType,
                emittedExpression.EvidenceKind.ToString(),
                "");
            return true;
        }

        var evidence = BuildStrictEmittedExpressionEvidence(cleanCode, task, context);
        var request = new EmittedExpressionRequest(
            cleanCode,
            context.SinkKind.ToString(),
            expectedReturnType,
            context.TargetInfo?.TargetMember ?? "");

        if (!StrictEmittedExpressionEngine.TryEmitFromReliableEvidence(request, evidence, out var emitted))
        {
            if (TryEmitStrictNestedTextBridges(cleanCode, task, context, expectedReturnType, out emittedExpression))
            {
                StrictEmissionCache.TryAdd(
                    cacheKey,
                    new StrictEmissionCacheEntry(
                        true,
                        emittedExpression.Code,
                        emittedExpression.ReturnType,
                        emittedExpression.XpaType,
                        emittedExpression.EvidenceKind,
                        emittedExpression.EvidenceSourceKey));
                return true;
            }

            var hasReliableSource = StrictEmittedExpressionEngine.TrySelectReliableSourceType(evidence, out var selected) &&
                                    selected.HasType;
            var sourceReturnType = hasReliableSource ? selected.ReturnType : "";
            var sourceKind = hasReliableSource ? selected.EvidenceKind.ToString() : "";
            TrackExpressionEmissionAudit(
                task,
                context,
                cleanCode,
                expectedReturnType,
                success: false,
                sourceReturnType,
                sourceKind,
                ResolveStrictEmissionFailureReason(hasReliableSource, expectedReturnType, sourceReturnType));
            StrictEmissionCache.TryAdd(cacheKey, new StrictEmissionCacheEntry(false, ""));
            return false;
        }

        emittedExpression = ApplyStrictNestedTextArgumentBridges(
            emitted,
            cleanCode,
            task,
            context,
            expectedReturnType);
        TrackCriticalExternalCoercionIfBridgeChanged(
            "EmittedExpression",
            nameof(TryEmitThroughStrictEmittedExpression),
            cleanCode,
            emittedExpression.Code,
            string.Create(CultureInfo.InvariantCulture, $"sink={context.SinkKind} expected={expectedReturnType} source={emitted.ReturnType} sourceKind={emitted.EvidenceKind} expr={TruncateTelemetryValue(cleanCode)}"));
        TrackExpressionEmissionAudit(
            task,
            context,
            cleanCode,
            expectedReturnType,
            success: true,
            emittedExpression.ReturnType,
            emittedExpression.EvidenceKind.ToString(),
            "");
        StrictEmissionCache.TryAdd(
            cacheKey,
            new StrictEmissionCacheEntry(
                true,
                emittedExpression.Code,
                emittedExpression.ReturnType,
                emittedExpression.XpaType,
                emittedExpression.EvidenceKind,
                emittedExpression.EvidenceSourceKey));
        return true;
    }

    private static StrictEmittedExpression ApplyStrictNestedTextArgumentBridges(
        StrictEmittedExpression emitted,
        string originalCode,
        TaskSemantic task,
        ExpressionEmissionContext context,
        string expectedReturnType)
    {
        if (string.IsNullOrWhiteSpace(emitted.Code))
            return emitted;

        var bridgedCode = RenderStrictFunctionArgumentBridges(emitted.Code, task);
        if (string.Equals(bridgedCode, emitted.Code, StringComparison.Ordinal))
            return emitted;

        if (string.Equals(ScalarReturnType(CanonicalReturnType(expectedReturnType)), "Text", StringComparison.Ordinal))
        {
            TrackCriticalExternalCoercionIfBridgeChanged(
                "EmittedExpression",
                nameof(ApplyStrictNestedTextArgumentBridges),
                originalCode,
                bridgedCode,
                string.Create(CultureInfo.InvariantCulture, $"sink={context.SinkKind} expected={expectedReturnType} expr={TruncateTelemetryValue(originalCode)}"));
        }

        return new StrictEmittedExpression(
            bridgedCode,
            emitted.ReturnType,
            emitted.XpaType,
            emitted.EvidenceKind,
            emitted.EvidenceSourceKey);
    }

    private static bool TryEmitStrictNestedTextBridges(
        string cleanCode,
        TaskSemantic task,
        ExpressionEmissionContext context,
        string expectedReturnType,
        out StrictEmittedExpression emittedExpression)
    {
        emittedExpression = default;
        if (!string.Equals(ScalarReturnType(CanonicalReturnType(expectedReturnType)), "Text", StringComparison.Ordinal) ||
            SplitTopLevelArithmeticExpression(cleanCode) is null)
        {
            return false;
        }

        var bridgedCode = RenderStrictFunctionArgumentBridges(cleanCode, task);
        if (string.Equals(bridgedCode, cleanCode, StringComparison.Ordinal))
            return false;

        var normalizedReturnType = NormalizeReturnTypeToken(expectedReturnType);
        var xpaType = XpaTypeEngine.MapExpectedToXpaType(GetValueReturnType(normalizedReturnType));
        if (xpaType == XpaType.Unknown)
            return false;

        emittedExpression = new StrictEmittedExpression(
            bridgedCode,
            normalizedReturnType,
            xpaType,
            EmittedExpressionTypeEvidenceKind.FunctionContract,
            "nested-text-bridges");
        TrackCriticalExternalCoercionIfBridgeChanged(
            "EmittedExpression",
            nameof(TryEmitStrictNestedTextBridges),
            cleanCode,
            bridgedCode,
            string.Create(CultureInfo.InvariantCulture, $"sink={context.SinkKind} expected={expectedReturnType} expr={TruncateTelemetryValue(cleanCode)}"));
        TrackExpressionEmissionAudit(
            task,
            context,
            cleanCode,
            expectedReturnType,
            success: true,
            normalizedReturnType,
            EmittedExpressionTypeEvidenceKind.FunctionContract.ToString(),
            "");
        return true;
    }

    private static bool TryCreateNoOpStrictEmission(
        string cleanCode,
        TaskSemantic task,
        ExpressionEmissionContext context,
        string expectedReturnType,
        out StrictEmittedExpression emittedExpression)
    {
        emittedExpression = default;
        if (string.IsNullOrWhiteSpace(cleanCode))
            return false;

        string returnType;
        EmittedExpressionTypeEvidenceKind evidenceKind;
        string evidenceSourceKey;
        if (string.IsNullOrWhiteSpace(expectedReturnType))
        {
            if (TryParseFunctionCall(cleanCode, out var functionName, out var args) &&
                TryRenderStrictDbNameLiteralContract(functionName, args, out var renderedDbName))
            {
                emittedExpression = new StrictEmittedExpression(
                    renderedDbName,
                    "Text",
                    XpaType.Text,
                    EmittedExpressionTypeEvidenceKind.FunctionContract,
                    "function:DBName:literal");
                return true;
            }

            if (!TryResolveLiteralExpectedType(cleanCode, out var literalExpected))
                return false;

            returnType = ResolveReturnTypeForExpectedContext(literalExpected);
            evidenceKind = EmittedExpressionTypeEvidenceKind.Literal;
            evidenceSourceKey = "literal:no-expected";
        }
        else
        {
            if (TryGetCurrentTaskResourceReturnType(task, cleanCode, out var currentTaskReturnType) &&
                !SourceReturnTypeMatchesExpected(currentTaskReturnType, expectedReturnType))
            {
                return false;
            }

            if (TryResolveSimpleSourceReturnTypeFromResourcePath(task, cleanCode, out var resourceReturnType) &&
                !SourceReturnTypeMatchesExpected(resourceReturnType, expectedReturnType))
            {
                return false;
            }

            if (!IsKnownExpressionReturnTypeCompatible(cleanCode, expectedReturnType, task))
                return false;

            returnType = expectedReturnType;
            evidenceKind = EmittedExpressionTypeEvidenceKind.FunctionContract;
            evidenceSourceKey = "compatible:no-op";
        }

        var xpaType = XpaTypeEngine.MapExpectedToXpaType(GetValueReturnType(NormalizeReturnTypeToken(returnType)));
        if (xpaType == XpaType.Unknown)
            return false;

        emittedExpression = new StrictEmittedExpression(
            cleanCode,
            NormalizeReturnTypeToken(returnType),
            xpaType,
            evidenceKind,
            evidenceSourceKey);
        return true;
    }

    private static bool TryEmitStrictEngineOnly(
        string cleanCode,
        TaskSemantic task,
        ExpressionEmissionContext context,
        string expectedReturnType,
        out string emittedCode)
    {
        emittedCode = "";
        var cacheKey = CreateStrictEmissionCacheKey(cleanCode, task, context, expectedReturnType);
        if (StrictEmissionCache.TryGetValue(cacheKey, out var cached))
        {
            emittedCode = cached.Code;
            return cached.Success;
        }

        var evidence = BuildStrictEmittedExpressionEvidence(cleanCode, task, context);
        var request = new EmittedExpressionRequest(
            cleanCode,
            context.SinkKind.ToString(),
            expectedReturnType,
            context.TargetInfo?.TargetMember ?? "");

        if (!StrictEmittedExpressionEngine.TryEmitFromReliableEvidence(request, evidence, out var emitted))
        {
            StrictEmissionCache.TryAdd(cacheKey, new StrictEmissionCacheEntry(false, ""));
            return false;
        }

        emittedCode = emitted.Code;
        StrictEmissionCache.TryAdd(
            cacheKey,
            new StrictEmissionCacheEntry(
                true,
                emitted.Code,
                emitted.ReturnType,
                emitted.XpaType,
                emitted.EvidenceKind,
                emitted.EvidenceSourceKey));
        return true;
    }

    private static ExpectedTypeContext ResolveExpectedTypeFromExpressionEvidence(TaskSemantic task, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return default;

        var cleanCode = StripRedundantOuterParentheses(expression.Trim());
        if (string.IsNullOrWhiteSpace(cleanCode))
            return default;

        var cacheKey = string.Create(CultureInfo.InvariantCulture, $"{task.Ordinal}|{cleanCode}");
        if (_expectedTypeEvidenceCache.TryGetValue(cacheKey, out var cachedReturnType))
            return string.IsNullOrWhiteSpace(cachedReturnType)
                ? default
                : ExpectedTypeForReturnType(cachedReturnType);

        if (IsCounterExpression(cleanCode))
        {
            _expectedTypeEvidenceCache[cacheKey] = "Number";
            return ExpectedTypeForReturnType("Number");
        }

        var evidence = BuildStrictEmittedExpressionEvidence(cleanCode, task, default);
        if (!StrictEmittedExpressionEngine.TrySelectReliableSourceType(evidence, out var selected) || !selected.HasType)
        {
            _expectedTypeEvidenceCache[cacheKey] = "";
            return default;
        }

        var resolvedReturnType = CanonicalReturnType(selected.ReturnType);
        _expectedTypeEvidenceCache[cacheKey] = resolvedReturnType;
        return ExpectedTypeForReturnType(resolvedReturnType);
    }

    private static string CreateStrictEmissionCacheKey(
        string code,
        TaskSemantic task,
        ExpressionEmissionContext context,
        string expectedReturnType)
    {
        var target = context.TargetInfo;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{task.Ordinal}|{context.SinkKind}|{expectedReturnType}|{target?.TargetMember}|{target?.AttrObj}|{target?.ModelAttrObj}|{code}");
    }

    private static bool TryEmitExpectedArgumentFromReliableEvidence(
        string code,
        string expectedReturnType,
        TaskSemantic task,
        out string rendered)
    {
        rendered = code;
        if (string.IsNullOrWhiteSpace(code) ||
            string.IsNullOrWhiteSpace(expectedReturnType) ||
            !TryResolveStrictSourceReturnType(code, task, out var sourceReturnType))
            return false;

        var evidence = new[]
        {
            CreateEvidence(sourceReturnType, EmittedExpressionTypeEvidenceKind.FunctionContract, "argument"),
            CreateEvidence(expectedReturnType, EmittedExpressionTypeEvidenceKind.FunctionContract, "argument-contract", isExpectedType: true)
        };
        var request = new EmittedExpressionRequest(
            code.Trim(),
            ExpressionSinkKind.CallArgument.ToString(),
            expectedReturnType,
            "");

        if (!StrictEmittedExpressionEngine.TryEmitFromReliableEvidence(request, evidence, out var emitted))
            return false;

        rendered = emitted.Code;
        return true;
    }

    private static string EmitScalarArgumentFromEvidence(
        string expression,
        string? valueReturnType,
        TaskSemantic? task = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var cleanCode = StripRedundantOuterParentheses(expression.Trim());
        var expectedReturnType = ScalarReturnType(CanonicalReturnType(GetValueReturnType(valueReturnType)));
        if (string.IsNullOrWhiteSpace(expectedReturnType))
            return cleanCode;

        IReadOnlyList<EmittedExpressionTypeEvidence> evidence;
        if (task is null)
        {
            var tasklessEvidence = new List<EmittedExpressionTypeEvidence>(capacity: 3);
            AddLiteralEvidence(cleanCode, tasklessEvidence);
            AddFunctionContractEvidence(cleanCode, null, tasklessEvidence);
            tasklessEvidence.Add(CreateEvidence(
                expectedReturnType,
                EmittedExpressionTypeEvidenceKind.SinkExpectedType,
                ExpressionSinkKind.CallArgument.ToString(),
                isExpectedType: true));
            evidence = tasklessEvidence;
        }
        else
        {
            var context = new ExpressionEmissionContext(
                ExpressionSinkKind.CallArgument,
                ExpectedTypeForReturnType(expectedReturnType),
                expectedReturnType,
                false,
                null,
                "");
            evidence = BuildStrictEmittedExpressionEvidence(cleanCode, task, context);
        }

        var request = new EmittedExpressionRequest(
            cleanCode,
            ExpressionSinkKind.CallArgument.ToString(),
            expectedReturnType,
            "");

        return StrictEmittedExpressionEngine.TryEmitFromReliableEvidence(request, evidence, out var emitted)
            ? emitted.Code
            : cleanCode;
    }

    private static string EmitFromReliableTypeEvidence(
        string expression,
        string sourceReturnType,
        string expectedReturnType,
        string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var cleanCode = StripRedundantOuterParentheses(expression.Trim());
        var expected = ScalarReturnType(CanonicalReturnType(GetValueReturnType(expectedReturnType)));
        if (string.IsNullOrWhiteSpace(expected))
            return cleanCode;

        var evidence = new[]
        {
            CreateEvidence(sourceReturnType, EmittedExpressionTypeEvidenceKind.FunctionContract, sourceKey),
            CreateEvidence(expected, EmittedExpressionTypeEvidenceKind.FunctionContract, sourceKey + ":expected", isExpectedType: true)
        };
        var request = new EmittedExpressionRequest(
            cleanCode,
            ExpressionSinkKind.CallArgument.ToString(),
            expected,
            "");

        return StrictEmittedExpressionEngine.TryEmitFromReliableEvidence(request, evidence, out var emitted)
            ? emitted.Code
            : cleanCode;
    }

    private static IReadOnlyList<EmittedExpressionTypeEvidence> BuildStrictEmittedExpressionEvidence(
        string code,
        TaskSemantic task,
        ExpressionEmissionContext context)
    {
        var evidence = new List<EmittedExpressionTypeEvidence>(capacity: 6);

        AddLiteralEvidence(code, evidence);
        AddRegisteredExpressionEvidence(code, task, evidence);
        AddSourceReferenceEvidence(code, task, evidence);
        AddTargetEvidence(context, evidence);
        AddStructuralSourceEvidence(code, task, evidence);
        AddFunctionContractEvidence(code, task, evidence);
        AddSinkEvidence(context, evidence);

        return evidence;
    }

    private static void AddLiteralEvidence(string code, List<EmittedExpressionTypeEvidence> evidence)
    {
        if (IsWholeStringLiteralExpression(code))
            evidence.Add(CreateEvidence("Text", EmittedExpressionTypeEvidenceKind.Literal, "literal:string"));
        else if (IsNumericLiteralExpressionCentral(code))
            evidence.Add(CreateEvidence("Number", EmittedExpressionTypeEvidenceKind.Literal, "literal:number"));
        else if (bool.TryParse(code, out _))
            evidence.Add(CreateEvidence("Bool", EmittedExpressionTypeEvidenceKind.Literal, "literal:bool"));
        else if (IsNullCallExpression(code) || string.Equals(code, "null", StringComparison.OrdinalIgnoreCase))
            evidence.Add(CreateEvidence("object", EmittedExpressionTypeEvidenceKind.Literal, "literal:null"));
        else if (string.Equals(code, "XPARuntimeCore.Box.Date.Empty", StringComparison.Ordinal) ||
                 string.Equals(code, "Date.Empty", StringComparison.Ordinal))
            evidence.Add(CreateEvidence("Date", EmittedExpressionTypeEvidenceKind.Literal, "literal:date-empty"));
        else if (string.Equals(code, "XPARuntimeCore.Box.Time.Empty", StringComparison.Ordinal) ||
                 string.Equals(code, "Time.Empty", StringComparison.Ordinal))
            evidence.Add(CreateEvidence("Time", EmittedExpressionTypeEvidenceKind.Literal, "literal:time-empty"));
        else if (TryResolveNewClrExpressionReturnType(code, out var clrReturnType))
            evidence.Add(CreateEvidence(clrReturnType, EmittedExpressionTypeEvidenceKind.Literal, "literal:new-clr"));
    }

    private static void AddRegisteredExpressionEvidence(
        string code,
        TaskSemantic task,
        List<EmittedExpressionTypeEvidence> evidence)
    {
        if (!TryResolveRegisteredTypedExpressionInfo(task, code, out var registered) ||
            !registered.IsResolved)
            return;

        evidence.Add(new EmittedExpressionTypeEvidence(
            CanonicalReturnType(registered.ReturnType),
            registered.XpaType,
            EmittedExpressionTypeEvidenceKind.RegisteredExpression,
            string.Create(CultureInfo.InvariantCulture, $"task:{task.Ordinal}:registered")));
    }

    private static void AddSourceReferenceEvidence(
        string code,
        TaskSemantic task,
        List<EmittedExpressionTypeEvidence> evidence)
    {
        if (TryGetCurrentTaskResourceReturnType(task, code, out var currentTaskReturnType) &&
            !string.IsNullOrWhiteSpace(currentTaskReturnType))
        {
            evidence.Add(CreateEvidence(
                currentTaskReturnType,
                EmittedExpressionTypeEvidenceKind.TaskResourceColumn,
                code));
            return;
        }

        if (TryResolveSimpleSourceReturnTypeFromResourcePath(task, code, out var resourceReturnType) &&
            !string.IsNullOrWhiteSpace(resourceReturnType))
        {
            evidence.Add(CreateEvidence(
                resourceReturnType,
                EmittedExpressionTypeEvidenceKind.TaskResourceColumn,
                code));
            return;
        }

        if (TryResolveDataViewMemberColumn(task, code, out _, out var dataColumn))
        {
            var attrObj = ResolveEffectiveDataColumnAttrObj(dataColumn);
            var returnType = MapAttrObjToReturnType(attrObj);
            if (!string.IsNullOrWhiteSpace(returnType))
            {
                evidence.Add(CreateEvidence(
                    returnType,
                    EmittedExpressionTypeEvidenceKind.DataViewColumn,
                    code));
            }
            return;
        }

        if (TryResolveSimpleSourceReturnTypeByDataObjectMemberPath(code, out var dataObjectReturnType) &&
            !string.IsNullOrWhiteSpace(dataObjectReturnType))
        {
            evidence.Add(CreateEvidence(
                dataObjectReturnType,
                EmittedExpressionTypeEvidenceKind.DataViewColumn,
                code));
            return;
        }

        if (TryReadDotNetMethodCallEvidence(code, task, out var dotNetMethodReturnType) &&
            !string.IsNullOrWhiteSpace(dotNetMethodReturnType))
        {
            evidence.Add(CreateEvidence(
                dotNetMethodReturnType,
                EmittedExpressionTypeEvidenceKind.FunctionContract,
                code));
            return;
        }

        if (TryReadDotNetMemberEvidence(code, task, out var dotNetMemberReturnType) &&
            !string.IsNullOrWhiteSpace(dotNetMemberReturnType))
        {
            evidence.Add(CreateEvidence(
                dotNetMemberReturnType,
                EmittedExpressionTypeEvidenceKind.TaskResourceColumn,
                code));
        }
    }

    private static void AddTargetEvidence(
        ExpressionEmissionContext context,
        List<EmittedExpressionTypeEvidence> evidence)
    {
        if (context.TargetInfo is not TargetValueInfo targetInfo)
            return;

        var returnType = ResolveReturnTypeForExpectedContext(ExpectedTypeForTarget(targetInfo));
        if (string.IsNullOrWhiteSpace(returnType))
        {
            var attrObj = !string.IsNullOrWhiteSpace(targetInfo.AttrObj)
                ? targetInfo.AttrObj
                : targetInfo.ModelAttrObj ?? "";
            returnType = MapAttrObjToReturnType(attrObj);
        }
        if (string.IsNullOrWhiteSpace(returnType))
            return;

        var kind = targetInfo.Resource is not null
            ? EmittedExpressionTypeEvidenceKind.TaskResourceColumn
            : EmittedExpressionTypeEvidenceKind.SinkExpectedType;
        evidence.Add(CreateEvidence(
            returnType,
            kind,
            string.IsNullOrWhiteSpace(targetInfo.TargetMember) ? "target" : targetInfo.TargetMember,
            isExpectedType: true));
    }

    private static void AddTargetMemberEvidence(
        TaskSemantic task,
        ExpressionEmissionContext context,
        List<EmittedExpressionTypeEvidence> evidence)
    {
        if (context.TargetInfo is not TargetValueInfo targetInfo ||
            string.IsNullOrWhiteSpace(targetInfo.TargetMember) ||
            !TryGetCurrentTaskResourceReturnType(task, targetInfo.TargetMember, out var returnType) ||
            string.IsNullOrWhiteSpace(returnType))
        {
            return;
        }

        evidence.Add(CreateEvidence(
            returnType,
            EmittedExpressionTypeEvidenceKind.SinkExpectedType,
            targetInfo.TargetMember,
            isExpectedType: true));
    }

    private static void AddStructuralSourceEvidence(
        string code,
        TaskSemantic task,
        List<EmittedExpressionTypeEvidence> evidence)
    {
        if (!TryResolveStrictSourceReturnType(code, task, out var returnType) ||
            string.IsNullOrWhiteSpace(returnType))
            return;

        evidence.Add(CreateEvidence(
            returnType,
            EmittedExpressionTypeEvidenceKind.FunctionContract,
            "structural"));
    }

    private static void AddFunctionContractEvidence(string code, TaskSemantic? task, List<EmittedExpressionTypeEvidence> evidence)
    {
        if (!TryParseFunctionCall(code, out var functionName, out var args))
            return;

        if (task is not null &&
            IsTopLevelCall(functionName, "u.If") &&
            args.Count == 3 &&
            TryResolveStrictSourceReturnType(args[1].Trim(), task, out var trueReturnType) &&
            TryResolveStrictSourceReturnType(args[2].Trim(), task, out var falseReturnType) &&
            string.Equals(
                ScalarReturnType(CanonicalReturnType(trueReturnType)),
                ScalarReturnType(CanonicalReturnType(falseReturnType)),
                StringComparison.Ordinal))
        {
            evidence.Add(CreateEvidence(
                trueReturnType,
                EmittedExpressionTypeEvidenceKind.FunctionContract,
                functionName));
            return;
        }

        if (task is not null &&
            TryGetAccessibleFunctionContract(task, functionName, out var functionContract, out _))
        {
            evidence.Add(CreateEvidence(
                functionContract.ReturnType,
                EmittedExpressionTypeEvidenceKind.FunctionContract,
                functionName));
            return;
        }

        if (TryGetComponentFunctionCallContract(functionName, out var componentContract))
        {
            evidence.Add(CreateEvidence(
                componentContract.ReturnType,
                EmittedExpressionTypeEvidenceKind.FunctionContract,
                functionName));
            return;
        }

        if (!TryResolveKnownXpaFunctionReturnType(functionName, args, out var returnType))
            return;

        evidence.Add(CreateEvidence(
            returnType,
            EmittedExpressionTypeEvidenceKind.FunctionContract,
            functionName));
    }

    private static bool TryResolveStrictSourceReturnType(string code, TaskSemantic task, out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var cleanCode = StripRedundantOuterParentheses(code.Trim());
        if (TryGetCurrentTaskResourceReturnType(task, cleanCode, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        if (TryResolveSimpleSourceReturnTypeFromResourcePath(task, cleanCode, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        if (TryResolveDataViewMemberColumn(task, cleanCode, out _, out var dataColumn))
        {
            returnType = MapAttrObjToReturnType(ResolveEffectiveDataColumnAttrObj(dataColumn));
            if (!string.IsNullOrWhiteSpace(returnType))
                return true;
        }

        if (TryReadDotNetMethodCallEvidence(cleanCode, task, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        if (TryReadDotNetMemberEvidence(cleanCode, task, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        if (TryParseFunctionCall(cleanCode, out var componentFunctionName, out _) &&
            TryGetComponentFunctionCallContract(componentFunctionName, out var componentContract))
        {
            returnType = componentContract.ReturnType;
            return true;
        }

        if (SplitTopLevelComparisonExpression(cleanCode) is not null ||
            SplitTopLevelBooleanBinaryExpression(cleanCode) is not null)
            return TrySetStrictReturnType("Bool", out returnType);

        var arithmetic = SplitTopLevelArithmeticExpression(cleanCode);
        if (arithmetic is not null &&
            TryResolveStrictSourceReturnType(arithmetic.Value.Left.Trim(), task, out var leftReturnType) &&
            TryResolveStrictSourceReturnType(arithmetic.Value.Right.Trim(), task, out var rightReturnType))
        {
            var left = ScalarReturnType(CanonicalReturnType(leftReturnType));
            var right = ScalarReturnType(CanonicalReturnType(rightReturnType));
            if (string.Equals(arithmetic.Value.Operator, "+", StringComparison.Ordinal) &&
                (string.Equals(left, "Text", StringComparison.Ordinal) ||
                 string.Equals(right, "Text", StringComparison.Ordinal)))
                return TrySetStrictReturnType("Text", out returnType);

            if (string.Equals(arithmetic.Value.Operator, "-", StringComparison.Ordinal) &&
                string.Equals(left, "Time", StringComparison.Ordinal) &&
                string.Equals(right, "Time", StringComparison.Ordinal))
                return TrySetStrictReturnType("Time", out returnType);

            if (arithmetic.Value.Operator is "+" or "-" &&
                string.Equals(left, "Time", StringComparison.Ordinal) &&
                string.Equals(right, "Number", StringComparison.Ordinal))
                return TrySetStrictReturnType("Time", out returnType);

            if (string.Equals(arithmetic.Value.Operator, "+", StringComparison.Ordinal) &&
                string.Equals(left, "Number", StringComparison.Ordinal) &&
                string.Equals(right, "Time", StringComparison.Ordinal))
                return TrySetStrictReturnType("Time", out returnType);

            if (string.Equals(left, "Number", StringComparison.Ordinal) &&
                string.Equals(right, "Number", StringComparison.Ordinal))
                return TrySetStrictReturnType("Number", out returnType);
        }

        if (TryParseFunctionCall(cleanCode, out var functionName, out var args) &&
            IsTopLevelCall(functionName, "u.If") &&
            args.Count == 3 &&
            TryResolveStrictSourceReturnType(args[1].Trim(), task, out var trueReturnType) &&
            TryResolveStrictSourceReturnType(args[2].Trim(), task, out var falseReturnType) &&
            string.Equals(
                ScalarReturnType(CanonicalReturnType(trueReturnType)),
                ScalarReturnType(CanonicalReturnType(falseReturnType)),
                StringComparison.Ordinal))
        {
            returnType = trueReturnType;
            return true;
        }

        return TryResolveStrictSourceReturnType(cleanCode, out returnType);
    }

    private static string RenderStrictFunctionArgumentBridges(string code, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        return RenderStrictFunctionArgumentBridges(
            code,
            task,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            depth: 0);
    }

    private static string RenderStrictFunctionArgumentBridges(
        string code,
        TaskSemantic task,
        Dictionary<string, string> memo,
        HashSet<string> active,
        int depth)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        var originalCode = code.Trim();
        if (depth > StrictFunctionArgumentBridgeMaxDepth)
            return originalCode;

        if (memo.TryGetValue(originalCode, out var memoized))
            return memoized;

        if (!active.Add(originalCode))
            return originalCode;

        string Finish(string rendered)
        {
            memo[originalCode] = rendered;
            return rendered;
        }

        try
        {
        var strippedCode = StripRedundantOuterParentheses(originalCode);

        if (TryRenderStrictArrayNullPredicate(strippedCode, task, out var arrayNullPredicate))
            return Finish(arrayNullPredicate);

        if (TryRewriteStrictNestedNotComparisons(strippedCode, task, out var nestedNotComparisons))
            strippedCode = nestedNotComparisons;

        var embeddedBridge = RenderStrictEmbeddedFunctionArgumentBridges(strippedCode, task);
        if (!string.Equals(embeddedBridge, strippedCode, StringComparison.Ordinal))
            strippedCode = embeddedBridge;

        if (TryRewriteStrictNotComparison(strippedCode, task, out var rewrittenNotComparison))
            return Finish(RenderStrictFunctionArgumentBridges(rewrittenNotComparison, task, memo, active, depth + 1));

        var booleanBinary = SplitTopLevelBooleanBinaryExpression(strippedCode);
        if (booleanBinary is not null)
        {
            var left = RenderStrictFunctionArgumentBridges(booleanBinary.Value.Left, task, memo, active, depth + 1);
            var right = RenderStrictFunctionArgumentBridges(booleanBinary.Value.Right, task, memo, active, depth + 1);
            if (!string.Equals(left, booleanBinary.Value.Left, StringComparison.Ordinal) ||
                !string.Equals(right, booleanBinary.Value.Right, StringComparison.Ordinal))
                return Finish($"{left} {booleanBinary.Value.Operator} {right}");
        }

        if (TryRewriteStrictComparisonOperandBridges(strippedCode, task, out var rewrittenComparison))
            return Finish(rewrittenComparison);

        var arithmetic = SplitTopLevelArithmeticExpression(strippedCode);
        if (arithmetic is not null)
        {
            var left = RenderStrictFunctionArgumentBridges(arithmetic.Value.Left, task, memo, active, depth + 1);
            var right = RenderStrictFunctionArgumentBridges(arithmetic.Value.Right, task, memo, active, depth + 1);
            if (!string.Equals(left, arithmetic.Value.Left, StringComparison.Ordinal) ||
                !string.Equals(right, arithmetic.Value.Right, StringComparison.Ordinal))
                return Finish($"{left} {arithmetic.Value.Operator} {right}");
        }

        if (!TryParseFunctionCall(strippedCode, out var functionName, out var args) ||
            args.Count == 0)
        {
            return Finish(string.Equals(strippedCode, originalCode, StringComparison.Ordinal) ? code : strippedCode);
        }

        if (TryRenderStrictDbNameLiteralContract(functionName, args, out var dbNameRendered))
            return Finish(dbNameRendered);

        var changed = false;
        var renderedArgs = new string[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            var originalArg = args[i].Trim();
            var renderedArg = RenderStrictFunctionArgumentBridges(originalArg, task, memo, active, depth + 1);
            if ((IsTopLevelCall(functionName, "u.RangeAdd") || IsTopLevelCall(functionName, "u.LocateAdd")) &&
                (i == 1 || i == 2) &&
                TryRenderStrictObjectConditionalNullBridge(renderedArg, task, out var objectConditional))
            {
                renderedArg = objectConditional;
            }
            if ((TryGetAccessibleFunctionArgumentType(task, functionName, i, out var expectedReturnType) ||
                 TryGetComponentFunctionArgumentType(functionName, i, out expectedReturnType) ||
                 TryResolveXpaFunctionEmissionArgumentReturnTypeContract(
                     functionName,
                     i,
                     args.Count,
                     out expectedReturnType)) &&
                TryRenderStrictExpectedArgument(renderedArg, expectedReturnType, task, out var bridgedArg))
            {
                renderedArg = bridgedArg;
            }

            renderedArgs[i] = renderedArg;
            changed |= !string.Equals(renderedArg, originalArg, StringComparison.Ordinal);
        }

        return Finish(changed
            ? $"{functionName}({string.Join(", ", renderedArgs)})"
            : string.Equals(strippedCode, code.Trim(), StringComparison.Ordinal) ? code : strippedCode);
        }
        finally
        {
            active.Remove(originalCode);
        }
    }

    private static bool TryRenderStrictArrayNullPredicate(string code, TaskSemantic task, out string rendered)
    {
        rendered = "";
        if (!TryParseFunctionCall(code, out var functionName, out var args) ||
            args.Count != 1 ||
            (!IsTopLevelCall(functionName, "u.IsNull") &&
             !IsTopLevelCall(functionName, "IsNull")))
        {
            return false;
        }

        var argument = StripRedundantOuterParentheses(args[0].Trim());
        if (!TryGetCurrentTaskResourceReturnType(task, argument, out var returnType) ||
            !returnType.StartsWith("ArrayColumn<", StringComparison.Ordinal))
        {
            return false;
        }

        rendered = $"({argument}.Value == null)";
        return true;
    }

    private static bool TryGetAccessibleFunctionArgumentType(
        TaskSemantic task,
        string functionName,
        int argumentIndex,
        out string returnType)
    {
        returnType = "";
        if (!TryGetAccessibleFunctionContract(task, functionName, out var function, out _) ||
            argumentIndex < 0 ||
            argumentIndex >= function.Parameters.Count)
        {
            return false;
        }

        returnType = NormalizeReturnTypeToken(function.Parameters[argumentIndex].ParameterType);
        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static bool TryGetComponentFunctionArgumentType(
        string functionName,
        int argumentIndex,
        out string returnType)
    {
        returnType = "";
        if (!TryGetComponentFunctionCallContract(functionName, out var contract) ||
            argumentIndex < 0 ||
            argumentIndex >= contract.ParameterTypes.Count)
        {
            return false;
        }

        returnType = NormalizeReturnTypeToken(contract.ParameterTypes[argumentIndex]);
        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static bool TryRenderStrictDbNameLiteralContract(
        string functionName,
        List<string> args,
        out string rendered)
    {
        rendered = "";
        if (!IsTopLevelCall(functionName, "u.DBName") ||
            args.Count != 1 ||
            !TryReadStrictDbNameLiteralArgument(args[0].Trim(), out var dbNameLiteral) ||
            !TrySplitDbNameLiteral(dbNameLiteral, out var fileIndex, out var infoType))
        {
            return false;
        }

        rendered = $"u.DBName({fileIndex}, {infoType})";
        return true;
    }

    private static bool TryReadStrictDbNameLiteralArgument(string argument, out string literal)
    {
        literal = "";
        var trimmed = StripRedundantOuterParentheses((argument ?? "").Trim());
        if (TryGetWholeCSharpStringLiteral(trimmed, out literal))
            return true;

        if (!TryParseFunctionCall(trimmed, out var functionName, out var args) ||
            args.Count != 1 ||
            !IsTopLevelCall(functionName, "u.CastToNumber"))
        {
            return false;
        }

        return TryGetWholeCSharpStringLiteral(args[0].Trim(), out literal);
    }

    private static bool TryRenderStrictObjectConditionalNullBridge(
        string code,
        TaskSemantic task,
        out string rendered)
    {
        rendered = code;
        if (!TryParseFunctionCall(StripRedundantOuterParentheses(code.Trim()), out var functionName, out var args) ||
            !IsTopLevelCall(functionName, "u.If") ||
            args.Count != 3)
            return false;

        var trueArg = args[1].Trim();
        var falseArg = args[2].Trim();
        var trueIsNull = IsNullCallExpression(trueArg) || string.Equals(trueArg, "null", StringComparison.OrdinalIgnoreCase);
        var falseIsNull = IsNullCallExpression(falseArg) || string.Equals(falseArg, "null", StringComparison.OrdinalIgnoreCase);
        if (trueIsNull == falseIsNull)
            return false;

        var typedArg = trueIsNull ? falseArg : trueArg;
        if (!TryResolveStrictSourceReturnType(typedArg, task, out var typedReturnType) ||
            !string.Equals(CanonicalReturnType(typedReturnType), "Time", StringComparison.Ordinal))
            return false;

        var condition = args[0].Trim();
        rendered = trueIsNull
            ? $"(({condition}) ? (object){trueArg} : (object){typedArg})"
            : $"(({condition}) ? (object){typedArg} : (object){falseArg})";
        TrackCriticalExternalCoercionIfBridgeChanged(
            "EmittedExpression",
            nameof(TryRenderStrictObjectConditionalNullBridge),
            code,
            rendered,
            string.Create(CultureInfo.InvariantCulture, $"expected=object source=Time expr={TruncateTelemetryValue(code)}"));
        return true;
    }

    private static string RenderStrictEmbeddedFunctionArgumentBridges(string code, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        string RewriteFirstArgument(string functionName, List<string> args, string expectedReturnType)
        {
            if (args.Count == 0)
                return $"{functionName}()";

            var originalArg = args[0].Trim();
            var renderedArg = RenderStrictFunctionArgumentBridges(originalArg, task);
            if (TryRenderStrictExpectedArgument(renderedArg, expectedReturnType, task, out var bridgedArg) ||
                TryRenderStrictEvidenceBridge(renderedArg, "Number", expectedReturnType, out bridgedArg))
                renderedArg = bridgedArg;

            args[0] = renderedArg;
            return $"{functionName}({string.Join(", ", args)})";
        }

        var rewritten = RewriteFunctionCalls(
            code,
            "System.Windows.Forms.Form.FromHandle",
            args => RewriteFirstArgument("System.Windows.Forms.Form.FromHandle", args, "System.IntPtr"));

        rewritten = RewriteFunctionCalls(
            rewritten,
            "Form.FromHandle",
            args => RewriteFirstArgument("Form.FromHandle", args, "System.IntPtr"));

        rewritten = RewriteFunctionCalls(
            rewritten,
            "u.RangeAdd",
            args => RewriteStrictObjectRangeLocateCall("u.RangeAdd", args, task));

        rewritten = RewriteFunctionCalls(
            rewritten,
            "u.LocateAdd",
            args => RewriteStrictObjectRangeLocateCall("u.LocateAdd", args, task));

        rewritten = RewriteFunctionCalls(
            rewritten,
            "GetVarName",
            args => RewriteStrictContractedFunctionCall("GetVarName", args, task));

        rewritten = RewriteStrictAccessibleFunctionCalls(rewritten, task);
        rewritten = RewriteStrictComponentFunctionCalls(rewritten, task);

        rewritten = System.Text.RegularExpressions.Regex.Replace(
            rewritten,
            @"(?<prefix>GeneratedSnippets\.[A-Za-z0-9_\.]+\.func\s*\()(?<arg>u\.WinHWND\s*\([^()]*\))(?<suffix>\s*\))",
            m =>
            {
                var originalArg = m.Groups["arg"].Value.Trim();
                var renderedArg = RenderStrictFunctionArgumentBridges(originalArg, task);
                if (!TryRenderStrictExpectedArgument(renderedArg, "System.IntPtr", task, out var bridgedArg) &&
                    !TryRenderStrictEvidenceBridge(renderedArg, "Number", "System.IntPtr", out bridgedArg))
                    return m.Value;

                return $"{m.Groups["prefix"].Value}{bridgedArg}{m.Groups["suffix"].Value}";
            },
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        return rewritten;
    }

    private static string RewriteStrictComponentFunctionCalls(string code, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            code.IndexOf(".ComponentFunctions.", StringComparison.Ordinal) < 0 ||
            _componentFunctionSourceByName.Count == 0)
        {
            return code;
        }

        var rewritten = code;
        var visitedTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var functionName in _componentFunctionSourceByName.Keys)
        {
            if (!TryGetComponentFunctionCallContract(functionName, out var contract) ||
                contract.ParameterTypes.Count == 0 ||
                string.IsNullOrWhiteSpace(contract.TargetName) ||
                !visitedTargets.Add(contract.TargetName) ||
                rewritten.IndexOf(contract.TargetName, StringComparison.Ordinal) < 0)
            {
                continue;
            }

            rewritten = RewriteFunctionCalls(
                rewritten,
                contract.TargetName,
                args => RewriteStrictComponentFunctionCall(contract.TargetName, contract, args, task));
        }

        return rewritten;
    }

    private static string? RewriteStrictComponentFunctionCall(
        string targetName,
        ComponentFunctionCallContract contract,
        List<string> args,
        TaskSemantic task)
    {
        var changed = false;
        for (var i = 0; i < args.Count && i < contract.ParameterTypes.Count; i++)
        {
            var expectedReturnType = NormalizeReturnTypeToken(contract.ParameterTypes[i]);
            if (string.IsNullOrWhiteSpace(expectedReturnType))
                continue;

            var originalArg = args[i].Trim();
            var renderedArg = RenderStrictFunctionArgumentBridges(originalArg, task);
            if (TryRenderStrictExpectedArgument(renderedArg, expectedReturnType, task, out var bridgedArg))
                renderedArg = bridgedArg;

            args[i] = renderedArg;
            changed |= !string.Equals(renderedArg, originalArg, StringComparison.Ordinal);
        }

        return changed ? $"{targetName}({string.Join(", ", args)})" : null;
    }

    private static string RewriteStrictAccessibleFunctionCalls(string code, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        var rewritten = code;
        foreach (var functionName in ResolveStrictAccessibleFunctionCallNames(task))
        {
            if (rewritten.IndexOf(functionName, StringComparison.Ordinal) < 0)
                continue;

            rewritten = RewriteFunctionCalls(
                rewritten,
                functionName,
                args => RewriteStrictAccessibleFunctionCall(functionName, args, task));
        }

        return rewritten;
    }

    private static IReadOnlyList<string> ResolveStrictAccessibleFunctionCallNames(TaskSemantic task)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
            var trimmed = name.Trim();
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }

        foreach (var function in task.FunctionOverridesSemantic)
        {
            Add(function.MethodName);
            Add(function.Name);
        }

        foreach (var target in ResolveAccessibleApplicationFunctionTargets(task).Values)
            Add(target);

        foreach (var target in ResolveAccessibleParentFunctionTargets(task).Values)
            Add(target);

        return result;
    }

    private static string? RewriteStrictAccessibleFunctionCall(string functionName, List<string> args, TaskSemantic task)
    {
        if (!TryGetAccessibleFunctionContract(task, functionName, out var function, out _) ||
            function.Parameters.Count == 0 ||
            args.Count == 0)
        {
            return null;
        }

        var changed = false;
        for (var i = 0; i < args.Count && i < function.Parameters.Count; i++)
        {
            var expectedReturnType = NormalizeReturnTypeToken(function.Parameters[i].ParameterType);
            if (string.IsNullOrWhiteSpace(expectedReturnType))
                continue;

            var originalArg = args[i].Trim();
            var renderedArg = RenderStrictFunctionArgumentBridges(originalArg, task);
            if (TryRenderStrictExpectedArgument(renderedArg, expectedReturnType, task, out var bridgedArg))
                renderedArg = bridgedArg;

            args[i] = renderedArg;
            changed |= !string.Equals(renderedArg, originalArg, StringComparison.Ordinal);
        }

        return changed ? $"{functionName}({string.Join(", ", args)})" : null;
    }

    private static string RewriteStrictContractedFunctionCall(string functionName, List<string> args, TaskSemantic task)
    {
        var changed = false;
        for (var i = 0; i < args.Count; i++)
        {
            if (!TryResolveXpaFunctionEmissionArgumentReturnTypeContract(functionName, i, args.Count, out var expectedReturnType))
                continue;

            var originalArg = args[i].Trim();
            var renderedArg = RenderStrictFunctionArgumentBridges(originalArg, task);
            if (TryRenderStrictExpectedArgument(renderedArg, expectedReturnType, task, out var bridgedArg))
                renderedArg = bridgedArg;

            args[i] = renderedArg;
            changed |= !string.Equals(renderedArg, originalArg, StringComparison.Ordinal);
        }

        return changed ? $"{functionName}({string.Join(", ", args)})" : null!;
    }

    private static string RewriteStrictObjectRangeLocateCall(string functionName, List<string> args, TaskSemantic task)
    {
        for (var i = 1; i <= 2 && i < args.Count; i++)
        {
            var originalArg = args[i].Trim();
            var renderedArg = RenderStrictFunctionArgumentBridges(originalArg, task);
            if (TryRenderStrictObjectConditionalNullBridge(renderedArg, task, out var objectConditional))
                renderedArg = objectConditional;
            args[i] = renderedArg;
        }

        return $"{functionName}({string.Join(", ", args)})";
    }

    private static bool TryRenderStrictEvidenceBridge(
        string code,
        string sourceReturnType,
        string expectedReturnType,
        out string rendered)
    {
        rendered = code;
        if (string.IsNullOrWhiteSpace(code) ||
            string.IsNullOrWhiteSpace(sourceReturnType) ||
            string.IsNullOrWhiteSpace(expectedReturnType))
            return false;

        var evidence = new[]
        {
            CreateEvidence(sourceReturnType, EmittedExpressionTypeEvidenceKind.FunctionContract, "contract"),
            CreateEvidence(expectedReturnType, EmittedExpressionTypeEvidenceKind.FunctionContract, "contract-expected", isExpectedType: true)
        };
        var request = new EmittedExpressionRequest(
            code.Trim(),
            ExpressionSinkKind.CallArgument.ToString(),
            expectedReturnType,
            "");

        if (!StrictEmittedExpressionEngine.TryEmitFromReliableEvidence(request, evidence, out var emitted))
            return false;

        rendered = emitted.Code;
        TrackCriticalExternalCoercionIfBridgeChanged(
            "EmittedExpression",
            nameof(TryRenderStrictEvidenceBridge),
            code,
            rendered,
            string.Create(CultureInfo.InvariantCulture, $"source={sourceReturnType} expected={expectedReturnType} expr={TruncateTelemetryValue(code)}"));
        return true;
    }

    private static bool TryRewriteStrictNestedNotComparisons(string code, TaskSemantic task, out string rewritten)
    {
        rewritten = code;
        var changed = false;
        var next = System.Text.RegularExpressions.Regex.Replace(
            code,
            @"u\.CastToText\s*\(\s*u\.Not\s*\(\s*(?<operand>[A-Za-z_][A-Za-z0-9_\.]*)\s*\)\s*\)\s*(?<op>==|!=)\s*(?<literal>""(?:\\""|[^""])*"")",
            m =>
            {
                var operand = m.Groups["operand"].Value;
                if (!TryResolveStrictSourceReturnType(operand, task, out var operandReturnType) ||
                    string.Equals(CanonicalReturnType(operandReturnType), "Bool", StringComparison.Ordinal))
                    return m.Value;

                changed = true;
                return $"u.Not({operand} {m.Groups["op"].Value} {m.Groups["literal"].Value})";
            },
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        next = System.Text.RegularExpressions.Regex.Replace(
            next,
            @"u\.Not\s*\(\s*(?<operand>[A-Za-z_][A-Za-z0-9_\.]*)\s*\)\s*(?<op>==|!=)\s*(?<literal>""(?:\\""|[^""])*"")",
            m =>
            {
                var operand = m.Groups["operand"].Value;
                if (!TryResolveStrictSourceReturnType(operand, task, out var operandReturnType) ||
                    string.Equals(CanonicalReturnType(operandReturnType), "Bool", StringComparison.Ordinal))
                    return m.Value;

                changed = true;
                return $"u.Not({operand} {m.Groups["op"].Value} {m.Groups["literal"].Value})";
            },
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        rewritten = next;
        return changed;
    }

    private static bool TryRewriteStrictNotComparison(string code, TaskSemantic task, out string rewritten)
    {
        rewritten = code;
        var comparison = SplitTopLevelComparisonExpression(StripRedundantOuterParentheses(code.Trim()));
        if (comparison is null)
            return false;

        if (TryRewriteStrictNotComparisonSide(
                comparison.Value.Left,
                comparison.Value.Operator,
                comparison.Value.Right,
                task,
                out rewritten))
            return true;

        return TryRewriteStrictNotComparisonSide(
            comparison.Value.Right,
            comparison.Value.Operator,
            comparison.Value.Left,
            task,
            out rewritten);
    }

    private static bool TryRewriteStrictNotComparisonSide(
        string maybeNot,
        string comparisonOperator,
        string other,
        TaskSemantic task,
        out string rewritten)
    {
        rewritten = "";
        if (!TryParseFunctionCall(maybeNot.Trim(), out var functionName, out var args) ||
            args.Count != 1 ||
            !IsTopLevelCall(functionName, "u.Not"))
            return false;

        var operand = args[0].Trim();
        if (!TryResolveStrictSourceReturnType(operand, task, out var operandReturnType) ||
            string.Equals(CanonicalReturnType(operandReturnType), "Bool", StringComparison.Ordinal))
            return false;

        rewritten = $"u.Not({operand} {comparisonOperator} {other.Trim()})";
        return true;
    }

    private static bool TryRenderStrictExpectedArgument(
        string code,
        string expectedReturnType,
        TaskSemantic task,
        out string rendered)
    {
        rendered = code;
        if (string.IsNullOrWhiteSpace(expectedReturnType) ||
            !TryResolveStrictSourceReturnType(code, task, out var sourceReturnType))
        {
            if (TryRenderStrictByteArrayTextArgument(code, expectedReturnType, task, null, out rendered))
                return true;

            if (string.Equals(ScalarReturnType(CanonicalReturnType(expectedReturnType)), "Number", StringComparison.Ordinal) &&
                SplitTopLevelArithmeticExpression(code) is { } arithmetic &&
                TryRenderStrictExpectedArgument(arithmetic.Left.Trim(), "Number", task, out var left) &&
                TryRenderStrictExpectedArgument(arithmetic.Right.Trim(), "Number", task, out var right))
            {
                rendered = $"{left} {arithmetic.Operator} {right}";
                return true;
            }
            return false;
        }

        if (TryRenderStrictExpectedFromInnerScalarCast(code, expectedReturnType, task, out rendered))
            return true;

        if (TryRenderStrictByteArrayTextArgument(code, expectedReturnType, task, sourceReturnType, out rendered))
            return true;

        var evidence = new[]
        {
            CreateEvidence(sourceReturnType, EmittedExpressionTypeEvidenceKind.FunctionContract, "argument"),
            CreateEvidence(expectedReturnType, EmittedExpressionTypeEvidenceKind.FunctionContract, "argument-contract", isExpectedType: true)
        };
        var request = new EmittedExpressionRequest(
            code,
            ExpressionSinkKind.CallArgument.ToString(),
            expectedReturnType,
            "");

        if (!StrictEmittedExpressionEngine.TryEmitFromReliableEvidence(request, evidence, out var emitted))
            return false;

        rendered = emitted.Code;
        TrackCriticalExternalCoercionIfBridgeChanged(
            "EmittedExpression",
            nameof(TryRenderStrictExpectedArgument),
            code,
            rendered,
            string.Create(CultureInfo.InvariantCulture, $"expected={expectedReturnType} source={sourceReturnType} expr={TruncateTelemetryValue(code)}"));
        return true;
    }

    private static bool TryRenderStrictByteArrayTextArgument(
        string code,
        string expectedReturnType,
        TaskSemantic task,
        string? sourceReturnType,
        out string rendered)
    {
        rendered = code;
        if (!string.Equals(ScalarReturnType(CanonicalReturnType(expectedReturnType)), "byte[]", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var trimmed = StripRedundantOuterParentheses(code.Trim());
        var hasTextSource =
            !string.IsNullOrWhiteSpace(sourceReturnType) &&
            string.Equals(ScalarReturnType(CanonicalReturnType(sourceReturnType)), "Text", StringComparison.Ordinal);

        if (!hasTextSource &&
            TryResolveStrictSourceReturnType(trimmed, task, out var resolvedReturnType) &&
            string.Equals(ScalarReturnType(CanonicalReturnType(resolvedReturnType)), "Text", StringComparison.Ordinal))
        {
            hasTextSource = true;
        }

        if (!hasTextSource && !IsTextualBlobAssignmentExpression(trimmed))
            return false;

        var normalized = NormalizeByteArrayExpectedExpression(trimmed);
        if (string.Equals(normalized, trimmed, StringComparison.Ordinal))
            return false;

        rendered = normalized;
        TrackCriticalExternalCoercionIfBridgeChanged(
            "EmittedExpression",
            nameof(TryRenderStrictByteArrayTextArgument),
            code,
            rendered,
            string.Create(CultureInfo.InvariantCulture, $"expected={expectedReturnType} source=Text expr={TruncateTelemetryValue(code)}"));
        return true;
    }

    private static bool TryRenderStrictExpectedFromInnerScalarCast(
        string code,
        string expectedReturnType,
        TaskSemantic task,
        out string rendered)
    {
        rendered = code;
        if (!TryParseFunctionCall(StripRedundantOuterParentheses(code.Trim()), out var functionName, out var args) ||
            args.Count != 1 ||
            !IsStrictScalarCastFunction(functionName) ||
            IsExpectedStrictScalarCast(functionName, expectedReturnType))
            return false;

        var inner = args[0].Trim();
        if (!TryResolveStrictSourceReturnType(inner, task, out var innerSourceReturnType))
            return false;

        var evidence = new[]
        {
            CreateEvidence(innerSourceReturnType, EmittedExpressionTypeEvidenceKind.FunctionContract, "argument-inner"),
            CreateEvidence(expectedReturnType, EmittedExpressionTypeEvidenceKind.FunctionContract, "argument-contract", isExpectedType: true)
        };
        var request = new EmittedExpressionRequest(
            inner,
            ExpressionSinkKind.CallArgument.ToString(),
            expectedReturnType,
            "");

        if (!StrictEmittedExpressionEngine.TryEmitFromReliableEvidence(request, evidence, out var emitted))
            return false;

        rendered = emitted.Code;
        return true;
    }

    private static string RenderStrictConditionalInsideExpectedCast(string code, string expectedReturnType, TaskSemantic task)
    {
        var expected = ScalarReturnType(CanonicalReturnType(expectedReturnType));
        var castFunction = expected switch
        {
            "Text" => "u.CastToText",
            "Number" => "u.CastToNumber",
            "Date" => "u.CastToDate",
            "Time" => "u.CastToTime",
            "Bool" => "u.CastToBool",
            "byte[]" => "u.CastToByteArray",
            _ => ""
        };
        if (string.IsNullOrWhiteSpace(castFunction) ||
            code.IndexOf(castFunction + "(", StringComparison.Ordinal) < 0)
            return code;

        var rewritten = RewriteFunctionCalls(code, castFunction, args =>
            args.Count == 1 &&
            TryRenderStrictConditionalForExpected(args[0].Trim(), expectedReturnType, task, out var renderedConditional)
                ? renderedConditional
                : null);
        return TrackCriticalExternalCoercionIfBridgeChanged(
            "EmittedExpression",
            nameof(RenderStrictConditionalInsideExpectedCast),
            code,
            rewritten,
            string.Create(CultureInfo.InvariantCulture, $"expected={expectedReturnType} expr={TruncateTelemetryValue(code)}"));
    }

    private static bool IsExpectedStrictScalarCast(string functionName, string expectedReturnType)
    {
        var expected = ScalarReturnType(CanonicalReturnType(expectedReturnType));
        return expected switch
        {
            "Text" => IsTopLevelCall(functionName, "u.CastToText"),
            "Number" => IsTopLevelCall(functionName, "u.CastToNumber"),
            "Date" => IsTopLevelCall(functionName, "u.CastToDate"),
            "Time" => IsTopLevelCall(functionName, "u.CastToTime"),
            "Bool" => IsTopLevelCall(functionName, "u.CastToBool"),
            "byte[]" => IsTopLevelCall(functionName, "u.CastToByteArray"),
            _ => false
        };
    }

    private static bool IsStrictScalarCastFunction(string functionName)
        => IsTopLevelCall(functionName, "u.CastToText") ||
           IsTopLevelCall(functionName, "u.CastToNumber") ||
           IsTopLevelCall(functionName, "u.CastToDate") ||
           IsTopLevelCall(functionName, "u.CastToTime") ||
           IsTopLevelCall(functionName, "u.CastToBool") ||
           IsTopLevelCall(functionName, "u.CastToByteArray");

    private static bool TryRenderStrictConditionalForExpected(
        string code,
        string expectedReturnType,
        TaskSemantic task,
        out string rendered)
    {
        rendered = code;
        if (string.IsNullOrWhiteSpace(expectedReturnType) ||
            !TryParseFunctionCall(StripRedundantOuterParentheses(code.Trim()), out var functionName, out var args) ||
            !IsTopLevelCall(functionName, "u.If") ||
            args.Count != 3)
            return false;

        var trueArg = RenderStrictFunctionArgumentBridges(args[1].Trim(), task);
        var falseArg = RenderStrictFunctionArgumentBridges(args[2].Trim(), task);
        var changed = false;
        var expectedScalar = ScalarReturnType(CanonicalReturnType(expectedReturnType));

        if (string.Equals(expectedScalar, "Number", StringComparison.Ordinal) &&
            TryResolveStrictSourceReturnType(trueArg, task, out var trueSourceReturnType) &&
            TryResolveStrictSourceReturnType(falseArg, task, out var falseSourceReturnType))
        {
            var trueScalar = ScalarReturnType(CanonicalReturnType(trueSourceReturnType));
            var falseScalar = ScalarReturnType(CanonicalReturnType(falseSourceReturnType));
            if ((string.Equals(trueScalar, "Text", StringComparison.Ordinal) && string.Equals(falseScalar, "Number", StringComparison.Ordinal)) ||
                (string.Equals(trueScalar, "Number", StringComparison.Ordinal) && string.Equals(falseScalar, "Text", StringComparison.Ordinal)))
            {
                if (TryRenderStrictTextConditionalBranch(trueArg, task, out var textTrue) &&
                    TryRenderStrictTextConditionalBranch(falseArg, task, out var textFalse))
                {
                    rendered = $"u.CastToNumber({functionName}({args[0].Trim()}, {textTrue}, {textFalse}))";
                    TrackCriticalExternalCoercionIfBridgeChanged(
                        "EmittedExpression",
                        nameof(TryRenderStrictConditionalForExpected),
                        code,
                        rendered,
                        string.Create(CultureInfo.InvariantCulture, $"expected={expectedReturnType} expr={TruncateTelemetryValue(code)}"));
                    return true;
                }
            }
        }

        if (string.Equals(expectedScalar, "Number", StringComparison.Ordinal) &&
            (IsStrictTextEvidence(trueArg, task) || IsStrictTextEvidence(falseArg, task)) &&
            TryRenderStrictTextConditionalBranch(trueArg, task, out var fallbackTextTrue) &&
            TryRenderStrictTextConditionalBranch(falseArg, task, out var fallbackTextFalse))
        {
            rendered = $"u.CastToNumber({functionName}({args[0].Trim()}, {fallbackTextTrue}, {fallbackTextFalse}))";
            TrackCriticalExternalCoercionIfBridgeChanged(
                "EmittedExpression",
                nameof(TryRenderStrictConditionalForExpected),
                code,
                rendered,
                string.Create(CultureInfo.InvariantCulture, $"expected={expectedReturnType} expr={TruncateTelemetryValue(code)}"));
            return true;
        }

        if (TryRenderStrictExpectedArgument(trueArg, expectedReturnType, task, out var bridgedTrue))
        {
            changed |= !string.Equals(bridgedTrue, trueArg, StringComparison.Ordinal);
            trueArg = bridgedTrue;
        }

        if (TryRenderStrictExpectedArgument(falseArg, expectedReturnType, task, out var bridgedFalse))
        {
            changed |= !string.Equals(bridgedFalse, falseArg, StringComparison.Ordinal);
            falseArg = bridgedFalse;
        }

        if (!changed)
            return false;

        rendered = $"{functionName}({args[0].Trim()}, {trueArg}, {falseArg})";
        TrackCriticalExternalCoercionIfBridgeChanged(
            "EmittedExpression",
            nameof(TryRenderStrictConditionalForExpected),
            code,
            rendered,
            string.Create(CultureInfo.InvariantCulture, $"expected={expectedReturnType} expr={TruncateTelemetryValue(code)}"));
        return true;
    }

    private static bool TryRenderStrictTextConditionalBranch(string code, TaskSemantic task, out string rendered)
    {
        if (TryRenderStrictExpectedArgument(code, "Text", task, out rendered))
            return true;

        if (IsStrictTextEvidence(code, task))
        {
            rendered = code;
            return true;
        }

        rendered = $"u.CastToText({code})";
        TrackCriticalExternalCoercionIfBridgeChanged(
            "EmittedExpression",
            nameof(TryRenderStrictTextConditionalBranch),
            code,
            rendered,
            string.Create(CultureInfo.InvariantCulture, $"expected=Text expr={TruncateTelemetryValue(code)}"));
        return true;
    }

    private static bool IsStrictTextEvidence(string code, TaskSemantic task)
    {
        var trimmed = StripRedundantOuterParentheses(code.Trim());
        if (trimmed.StartsWith("u.CastToText(", StringComparison.Ordinal))
            return true;

        return TryResolveStrictSourceReturnType(trimmed, task, out var returnType) &&
               string.Equals(ScalarReturnType(CanonicalReturnType(returnType)), "Text", StringComparison.Ordinal);
    }

    private static bool TryRewriteStrictComparisonOperandBridges(
        string code,
        TaskSemantic task,
        out string rewritten)
    {
        rewritten = code;
        var comparison = SplitTopLevelComparisonExpression(StripRedundantOuterParentheses(code.Trim()));
        if (comparison is null)
            return false;

        var left = comparison.Value.Left.Trim();
        var right = comparison.Value.Right.Trim();
        if (!TryResolveStrictSourceReturnType(left, task, out var leftReturnType) ||
            !TryResolveStrictSourceReturnType(right, task, out var rightReturnType))
            return false;

        var leftScalar = ScalarReturnType(CanonicalReturnType(leftReturnType));
        var rightScalar = ScalarReturnType(CanonicalReturnType(rightReturnType));
        if (string.Equals(leftScalar, rightScalar, StringComparison.Ordinal))
            return false;

        if (TryChooseStrictComparisonExpectedType(leftScalar, rightScalar, out var expectedReturnType))
        {
            if (TryRenderStrictExpectedArgument(left, expectedReturnType, task, out var bridgedLeft))
                left = bridgedLeft;
            if (TryRenderStrictExpectedArgument(right, expectedReturnType, task, out var bridgedRight))
                right = bridgedRight;
        }

        var next = $"{left} {comparison.Value.Operator} {right}";
        if (string.Equals(next, code, StringComparison.Ordinal))
            return false;

        rewritten = next;
        return true;
    }

    private static bool TryChooseStrictComparisonExpectedType(string leftReturnType, string rightReturnType, out string expectedReturnType)
    {
        expectedReturnType = "";
        if ((string.Equals(leftReturnType, "Bool", StringComparison.Ordinal) && string.Equals(rightReturnType, "Text", StringComparison.Ordinal)) ||
            (string.Equals(leftReturnType, "Text", StringComparison.Ordinal) && string.Equals(rightReturnType, "Bool", StringComparison.Ordinal)))
        {
            expectedReturnType = "Text";
            return true;
        }

        if ((string.Equals(leftReturnType, "Bool", StringComparison.Ordinal) && string.Equals(rightReturnType, "Number", StringComparison.Ordinal)) ||
            (string.Equals(leftReturnType, "Number", StringComparison.Ordinal) && string.Equals(rightReturnType, "Bool", StringComparison.Ordinal)))
        {
            expectedReturnType = "Number";
            return true;
        }

        if ((string.Equals(leftReturnType, "Number", StringComparison.Ordinal) && string.Equals(rightReturnType, "Text", StringComparison.Ordinal)) ||
            (string.Equals(leftReturnType, "Text", StringComparison.Ordinal) && string.Equals(rightReturnType, "Number", StringComparison.Ordinal)))
        {
            expectedReturnType = "Number";
            return true;
        }

        if ((string.Equals(leftReturnType, "Date", StringComparison.Ordinal) && string.Equals(rightReturnType, "Time", StringComparison.Ordinal)) ||
            (string.Equals(leftReturnType, "Time", StringComparison.Ordinal) && string.Equals(rightReturnType, "Date", StringComparison.Ordinal)))
        {
            expectedReturnType = "Number";
            return true;
        }

        if ((string.Equals(leftReturnType, "Number", StringComparison.Ordinal) &&
             (string.Equals(rightReturnType, "Date", StringComparison.Ordinal) ||
              string.Equals(rightReturnType, "Time", StringComparison.Ordinal))) ||
            (string.Equals(rightReturnType, "Number", StringComparison.Ordinal) &&
             (string.Equals(leftReturnType, "Date", StringComparison.Ordinal) ||
              string.Equals(leftReturnType, "Time", StringComparison.Ordinal))))
        {
            expectedReturnType = "Number";
            return true;
        }

        if (string.Equals(leftReturnType, "object", StringComparison.Ordinal) &&
            IsStrictScalarComparisonType(rightReturnType))
        {
            expectedReturnType = rightReturnType;
            return true;
        }

        if (string.Equals(rightReturnType, "object", StringComparison.Ordinal) &&
            IsStrictScalarComparisonType(leftReturnType))
        {
            expectedReturnType = leftReturnType;
            return true;
        }

        return false;
    }

    private static bool IsStrictScalarComparisonType(string returnType)
        => string.Equals(returnType, "Text", StringComparison.Ordinal) ||
           string.Equals(returnType, "Number", StringComparison.Ordinal) ||
           string.Equals(returnType, "Date", StringComparison.Ordinal) ||
           string.Equals(returnType, "Time", StringComparison.Ordinal) ||
           string.Equals(returnType, "Bool", StringComparison.Ordinal);

    private static bool TryResolveStrictSourceReturnType(string code, out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var cleanCode = StripRedundantOuterParentheses(code.Trim());
        if (IsWholeStringLiteralExpression(cleanCode))
            return TrySetStrictReturnType("Text", out returnType);
        if (IsNumericLiteralExpressionCentral(cleanCode))
            return TrySetStrictReturnType("Number", out returnType);
        if (bool.TryParse(cleanCode, out _))
            return TrySetStrictReturnType("Bool", out returnType);
        if (IsNullCallExpression(cleanCode) || string.Equals(cleanCode, "null", StringComparison.OrdinalIgnoreCase))
            return TrySetStrictReturnType("object", out returnType);
        if (TryResolveNewClrExpressionReturnType(cleanCode, out returnType))
            return true;

        if (SplitTopLevelComparisonExpression(cleanCode) is not null ||
            SplitTopLevelBooleanBinaryExpression(cleanCode) is not null)
            return TrySetStrictReturnType("Bool", out returnType);

        var arithmetic = SplitTopLevelArithmeticExpression(cleanCode);
        if (arithmetic is not null &&
            TryResolveStrictSourceReturnType(arithmetic.Value.Left.Trim(), out var leftReturnType) &&
            TryResolveStrictSourceReturnType(arithmetic.Value.Right.Trim(), out var rightReturnType))
        {
            var left = ScalarReturnType(CanonicalReturnType(leftReturnType));
            var right = ScalarReturnType(CanonicalReturnType(rightReturnType));
            if (string.Equals(arithmetic.Value.Operator, "+", StringComparison.Ordinal) &&
                (string.Equals(left, "Text", StringComparison.Ordinal) ||
                 string.Equals(right, "Text", StringComparison.Ordinal)))
                return TrySetStrictReturnType("Text", out returnType);

            if (string.Equals(arithmetic.Value.Operator, "-", StringComparison.Ordinal) &&
                string.Equals(left, "Time", StringComparison.Ordinal) &&
                string.Equals(right, "Time", StringComparison.Ordinal))
                return TrySetStrictReturnType("Time", out returnType);

            if (arithmetic.Value.Operator is "+" or "-" &&
                string.Equals(left, "Time", StringComparison.Ordinal) &&
                string.Equals(right, "Number", StringComparison.Ordinal))
                return TrySetStrictReturnType("Time", out returnType);

            if (string.Equals(arithmetic.Value.Operator, "+", StringComparison.Ordinal) &&
                string.Equals(left, "Number", StringComparison.Ordinal) &&
                string.Equals(right, "Time", StringComparison.Ordinal))
                return TrySetStrictReturnType("Time", out returnType);

            if (string.Equals(left, "Number", StringComparison.Ordinal) &&
                string.Equals(right, "Number", StringComparison.Ordinal))
                return TrySetStrictReturnType("Number", out returnType);
        }

        if (!TryParseFunctionCall(cleanCode, out var functionName, out var args))
        {
            if (TryResolveSimpleSourceReturnTypeByDataObjectMemberPath(cleanCode, out returnType) &&
                !string.IsNullOrWhiteSpace(returnType))
                return true;
            return false;
        }

        if (IsTopLevelCall(functionName, "u.If") &&
            args.Count == 3 &&
            TryResolveStrictSourceReturnType(args[1].Trim(), out var trueReturnType) &&
            TryResolveStrictSourceReturnType(args[2].Trim(), out var falseReturnType) &&
            string.Equals(
                ScalarReturnType(CanonicalReturnType(trueReturnType)),
                ScalarReturnType(CanonicalReturnType(falseReturnType)),
                StringComparison.Ordinal))
        {
            returnType = trueReturnType;
            return true;
        }

        return TryResolveKnownXpaFunctionReturnType(functionName, args, out returnType);
    }

    private static bool TryGetCurrentTaskResourceReturnType(TaskSemantic task, string code, out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(code) || !IsSimpleIdentifierPath(code))
            return false;

        var targetPath = code.Trim();
        var resolvedResource = ResolveResourceByTargetPath(task, targetPath, _allTasks ?? Array.Empty<TaskSemantic>());
        if (resolvedResource is not null)
        {
            var ownerTask = ResolveOwningTaskForResource(resolvedResource) ?? task;
            return TryResolveTaskResourceStrictReturnType(resolvedResource, ownerTask, out returnType);
        }

        var memberName = targetPath;
        while (memberName.StartsWith("_parent.", StringComparison.Ordinal))
            memberName = memberName["_parent.".Length..];

        if (memberName.EndsWith(".Value", StringComparison.Ordinal))
            memberName = memberName[..^".Value".Length];

        if (memberName.Contains('.', StringComparison.Ordinal))
        {
            if (TryReadDotNetMemberEvidence(memberName, task, out returnType) &&
                !string.IsNullOrWhiteSpace(returnType))
                return true;

            return false;
        }

        foreach (var resource in task.ResourcesSemantic.Ordered)
        {
            var candidate = ResolveTaskResourceMemberName(task, resource);
            if (!string.Equals(candidate, memberName, StringComparison.Ordinal))
                continue;

            return TryResolveTaskResourceStrictReturnType(resource, task, out returnType);
        }

        return false;
    }

    private static bool TryResolveTaskResourceStrictReturnType(
        TaskResourceColumnDef resource,
        TaskSemantic ownerTask,
        out string returnType)
    {
        var columnType = ResolveTaskResourceColumnType(resource, _allFieldModels, ownerTask);
        if (IsDotNetTaskResource(resource))
        {
            returnType = "object";
            return true;
        }

        if (columnType.StartsWith("ArrayColumn<", StringComparison.Ordinal))
        {
            returnType = columnType;
            return true;
        }

        var attrObj = ResolveAttrObjForColumnType(columnType, _allFieldModels, ownerTask);
        if (string.IsNullOrWhiteSpace(attrObj))
            attrObj = ResolveEffectiveTaskResourceAttrObj(resource, ownerTask);

        returnType = MapAttrObjToReturnType(attrObj);
        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static bool TrySetStrictReturnType(string value, out string returnType)
    {
        returnType = value;
        return true;
    }

    private static void AddSinkEvidence(
        ExpressionEmissionContext context,
        List<EmittedExpressionTypeEvidence> evidence)
    {
        var returnType = ResolveReturnTypeForExpectedContext(context.Expected);
        if (string.IsNullOrWhiteSpace(returnType))
            return;

        evidence.Add(CreateEvidence(
            returnType,
            EmittedExpressionTypeEvidenceKind.SinkExpectedType,
            context.SinkKind.ToString(),
            isExpectedType: true));
    }

    private static EmittedExpressionTypeEvidence CreateEvidence(
        string returnType,
        EmittedExpressionTypeEvidenceKind kind,
        string sourceKey,
        bool isExpectedType = false)
    {
        var canonical = CanonicalReturnType(returnType);
        return new EmittedExpressionTypeEvidence(
            canonical,
            XpaTypeEngine.MapExpectedToXpaType(ScalarReturnType(canonical)),
            kind,
            sourceKey,
            isExpectedType);
    }

    private static string CanonicalReturnType(string? returnType)
    {
        if (string.IsNullOrWhiteSpace(returnType))
            return "";

        var trimmed = returnType.Trim();
        var lastDot = trimmed.LastIndexOf('.');
        var unqualified = lastDot >= 0 && lastDot + 1 < trimmed.Length
            ? trimmed[(lastDot + 1)..]
            : trimmed;

        return unqualified switch
        {
            "TextParameter" or "TextColumn" or "Text" => "Text",
            "NumberParameter" or "NumberColumn" or "Number" or "MVariableIndex" => "Number",
            "DateParameter" or "DateColumn" or "Date" => "Date",
            "TimeParameter" or "TimeColumn" or "Time" => "Time",
            "BoolParameter" or "BoolColumn" or "Bool" => "Bool",
            "ByteArrayParameter" or "ByteArrayColumn" or "byte[]" => "byte[]",
            "String[]" or "System.String[]" or "string[]" => "string[]",
            "IntPtr" or "System.IntPtr" => "System.IntPtr",
            _ => trimmed
        };
    }

    private static string ScalarReturnType(string? returnType)
    {
        var canonical = CanonicalReturnType(returnType);
        return canonical switch
        {
            "Text?" => "Text",
            "Number?" => "Number",
            "Date?" => "Date",
            "Time?" => "Time",
            "Bool?" => "Bool",
            _ => canonical
        };
    }
}

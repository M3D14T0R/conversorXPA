using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static long _typedExpressionResolveCount;
    private static long _typedExpressionCacheHitCount;
    private static long _typedExpressionIntrinsicKnownCount;
    private static long _typedExpressionEffectiveKnownCount;
    private static long _typedExpressionFallbackInferenceCount;
    private static long _typedExpressionExpectedTypeShortcutCount;
    private static long _typedExpressionRegisteredReturnTypeCount;
    private static long _typedExpressionRegisteredLookupHitCount;
    private static long _typedExpressionAmbiguousRegistrationSkipCount;
    private static long _typedExpressionSourceTypeCacheHitCount;
    private static long _typedExpressionSourceTypeCacheMissCount;
    private static long _sourceFunctionCallAnalyzeCount;
    private static long _sourceFunctionCallCacheHitCount;
    private static long _sourceFunctionCallReturnKnownCount;
    private static long _sourceFunctionCallExpectedArgumentKnownCount;
    private static long _sourceFunctionCallActualArgumentKnownCount;
    private static long _typedExpressionSourceDirectBindingHitCount;
    private static long _typedExpressionSourceArithmeticHitCount;
    private static readonly ConcurrentDictionary<string, long> _typedExpressionResolveCountBySink = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> _typedExpressionFallbackCountBySink = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> _typedExpressionExpectedShortcutCountBySink = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> _sourceFunctionCallAnalyzeCountByFunction = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> _sourceFunctionCallReturnKnownCountByFunction = new(StringComparer.Ordinal);

    internal readonly record struct EmittedExpression(
        string Code,
        string IntrinsicReturnType,
        XpaType IntrinsicXpaType,
        string EffectiveReturnType,
        XpaType EffectiveXpaType,
        bool HasIntrinsicType,
        bool HasEffectiveType,
        bool UsedFallbackInference,
        bool CanEmitAsStatement = false,
        string SourceSyntax = "",
        string SourceReturnType = "",
        int ExpressionOrdinal = 0,
        string SinkKind = "",
        string ExpectedReturnType = "",
        string EvidenceKind = "",
        string EvidenceSourceKey = "",
        string FailureReason = "")
    {
        internal string PreferredReturnType => HasEffectiveType ? EffectiveReturnType : IntrinsicReturnType;
        internal bool HasSourceReturnType => !string.IsNullOrWhiteSpace(SourceReturnType);
        internal bool HasExpressionOrdinal => ExpressionOrdinal > 0;
    }

    private readonly record struct SourceTranslatedExpression(
        string Code,
        string SourceReturnType,
        string SourceSyntax = "",
        int ExpressionOrdinal = 0,
        string EvidenceKind = "")
    {
        internal bool HasSourceReturnType => !string.IsNullOrWhiteSpace(SourceReturnType);
        internal bool HasSourceSyntax => !string.IsNullOrWhiteSpace(SourceSyntax);
    }

    internal readonly record struct SourceFunctionCallTypeInfo(
        string FunctionName,
        string ReturnType,
        string[] ExpectedArgumentReturnTypes,
        string[] ActualArgumentReturnTypes)
    {
        internal bool HasReturnType => !string.IsNullOrWhiteSpace(ReturnType);
    }

    private readonly record struct SourceFunctionArgumentExpression(
        string Code,
        string SourceReturnType,
        string EffectiveReturnType,
        string ExpectedReturnType,
        string EvidenceKind)
    {
        internal string PreferredReturnType => !string.IsNullOrWhiteSpace(EffectiveReturnType)
            ? EffectiveReturnType
            : SourceReturnType;
    }

    private static string ResolveExpressionCode(string? expressionId, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
        => ResolveExpression(expressionId, task, dataObjects).Code;

    private static EmittedExpression ResolveExpression(string? expressionId, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (!int.TryParse(expressionId, out var n))
            return default;
        var cacheKey = task.Ordinal.ToString(CultureInfo.InvariantCulture) + "|" + n.ToString(CultureInfo.InvariantCulture);
        if (!task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(n, out var expr))
        {
            _expressionCodeCache[cacheKey] = "";
            return default;
        }
        if (_expressionCodeCache.TryGetValue(cacheKey, out var cached))
            return CreateContextualEmittedExpression(cached, expr, task, dataObjects, default, evidenceKind: "expression-cache");

        var resolved = ResolveExpressionEntry(expr, task, dataObjects);
        _expressionCodeCache[cacheKey] = resolved.Code;
        return resolved;
    }

    private static string ResolveExpressionCode(
        string? expressionId,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpressionEmissionContext context)
        => ResolveExpressionCode(expressionId, task, dataObjects, context.Expected, context);

    private static string ResolveExpressionCode(
        string? expressionId,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpectedTypeContext expected)
        => ResolveExpressionCode(expressionId, task, dataObjects, expected, CreateExpectedEmissionContext(expected));

    private static string ResolveExpressionCode(
        string? expressionId,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpectedTypeContext expected,
        ExpressionEmissionContext context)
        => ResolveExpression(expressionId, task, dataObjects, expected, context).Code;

    private static EmittedExpression ResolveExpression(
        string? expressionId,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpectedTypeContext expected,
        ExpressionEmissionContext context)
    {
        if ((context.SinkKind == ExpressionSinkKind.Default || context.SinkKind == ExpressionSinkKind.ExpectedValue) &&
            !context.Expected.HasExpectation &&
            expected.HasExpectation)
            context = CreateExpectedEmissionContext(expected);

        if (int.TryParse(expressionId, out var expressionOrdinal) &&
            task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expressionOrdinal, out var contextExpr) &&
            contextExpr is not null)
        {
            if (ShouldUseTypedExpressionEntryResolution(context))
            {
                var typedResolved = ResolveTypedExpressionEntryCode(contextExpr, task, dataObjects, context);
                if (context.SinkKind == ExpressionSinkKind.BooleanCondition)
                    typedResolved = typedResolved with
                    {
                        Code = NormalizeStatementBooleanConditionSyntax(typedResolved.Code),
                        EffectiveReturnType = "Bool",
                        EffectiveXpaType = XpaType.Bool,
                        HasEffectiveType = true
                    };
                RegisterContextualExpressionReturnType(task, typedResolved.Code, context);
                return typedResolved;
            }

            if (ShouldPreferContextualRawResolution(contextExpr, context))
            {
                var rawResolved = ResolveRawExpressionEntryCode(contextExpr, task, dataObjects, expressionOrdinal);
                var contextualResolved = EmitExpressionForContext(rawResolved, task, context);
                if (context.SinkKind == ExpressionSinkKind.BooleanCondition)
                    contextualResolved = NormalizeStatementBooleanConditionSyntax(contextualResolved);
                RegisterContextualExpressionReturnType(task, contextualResolved, context);
                return CreateContextualEmittedExpression(contextualResolved, contextExpr, task, dataObjects, context);
            }
        }

        var fallbackExpr = int.TryParse(expressionId, out var fallbackOrdinal) &&
                           task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(fallbackOrdinal, out var fallbackEntry)
            ? fallbackEntry
            : null;
        var resolved = ResolveExpression(expressionId, task, dataObjects);
        if (string.IsNullOrWhiteSpace(resolved.Code))
            resolved = CreateContextualEmittedExpression("", fallbackExpr, task, dataObjects, context, evidenceKind: "empty");
        var emitted = EmitExpressionForContext(resolved, task, context);
        if (context.SinkKind == ExpressionSinkKind.BooleanCondition)
            emitted = emitted with
            {
                Code = NormalizeStatementBooleanConditionSyntax(emitted.Code),
                EffectiveReturnType = "Bool",
                EffectiveXpaType = XpaType.Bool,
                HasEffectiveType = true
            };
        RegisterContextualExpressionReturnType(task, emitted.Code, context);
        return emitted;
    }

    private static EmittedExpression CreateContextualEmittedExpression(
        string code,
        ExpressionEntrySemantic? expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpressionEmissionContext context,
        string? sourceReturnType = null,
        bool usedFallbackInference = false,
        bool canEmitAsStatement = false,
        string evidenceKind = "")
    {
        var intrinsicReturnType = NormalizeReturnTypeToken(sourceReturnType ?? "");
        if (string.IsNullOrWhiteSpace(intrinsicReturnType))
            intrinsicReturnType = NormalizeReturnTypeToken(ResolveSourceReturnTypeForExpressionEntry(expr, task, dataObjects));
        if (string.IsNullOrWhiteSpace(intrinsicReturnType))
            intrinsicReturnType = NormalizeReturnTypeToken(ResolveIntrinsicReturnTypeForExpressionEntry(expr));

        var expectedReturnType = NormalizeReturnTypeToken(ResolveReturnTypeForExpectedContext(context.Expected));
        var effectiveReturnType = intrinsicReturnType;

        return new EmittedExpression(
            code,
            intrinsicReturnType,
            XpaTypeEngine.MapExpectedToXpaType(intrinsicReturnType),
            effectiveReturnType,
            XpaTypeEngine.MapExpectedToXpaType(effectiveReturnType),
            !string.IsNullOrWhiteSpace(intrinsicReturnType),
            !string.IsNullOrWhiteSpace(effectiveReturnType),
            usedFallbackInference,
            canEmitAsStatement,
            expr is null ? "" : ResolveExpressionEntrySourceSyntax(expr),
            intrinsicReturnType,
            expr?.Ordinal ?? 0,
            context.SinkKind.ToString(),
            expectedReturnType,
            evidenceKind);
    }

    private static string ResolveInitialKeyExpressionCode(
        TaskSemantic task,
        DataObjectDef primaryEntity,
        string primaryMember,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (!task.InitialKeyExpressionId.HasValue)
            return "";
        if (!task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(task.InitialKeyExpressionId.Value, out var expr) || expr is null)
            return "";

        var translated = ResolveExpressionEntryCode(expr, task, dataObjects);
        return RewriteIndexLiteralsForOrderBy(expr.Syntax, translated, primaryEntity, primaryMember);
    }

    private static string ResolveExpressionEntryCode(ExpressionEntrySemantic? expr, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
        => ResolveExpressionEntry(expr, task, dataObjects).Code;

    private static EmittedExpression ResolveExpressionEntry(ExpressionEntrySemantic? expr, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (expr is null || string.IsNullOrWhiteSpace(expr.Syntax))
            return default;
        var sharedKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{task.Ordinal}|{expr.Attribute}|{expr.IsStringLiteral}|{expr.Syntax}|{expr.LiteralNormalizedSyntax}");
        EmittedExpression CacheAndRegister(SourceTranslatedExpression translated)
        {
            var value = translated.Code;
            _sharedExpressionEntryCodeCache[sharedKey] = value;
            RegisterTypedExpressionReturnType(
                task,
                value,
                translated.HasSourceReturnType
                    ? translated.SourceReturnType
                    : ResolveIntrinsicReturnTypeForExpressionEntry(expr));
            return CreateEmittedExpressionFromSourceTranslated(translated, expr, task, dataObjects, default);
        }

        if (_sharedExpressionEntryCodeCache.TryGetValue(sharedKey, out var sharedCached))
        {
            var sourceReturnType = ResolveSourceReturnTypeForExpressionEntry(expr, task, dataObjects);
            RegisterTypedExpressionReturnType(
                task,
                sharedCached,
                !string.IsNullOrWhiteSpace(sourceReturnType)
                    ? sourceReturnType
                    : ResolveIntrinsicReturnTypeForExpressionEntry(expr));
            return CreateContextualEmittedExpression(sharedCached, expr, task, dataObjects, default, sourceReturnType, evidenceKind: "shared-expression-cache");
        }

        return CacheAndRegister(TranslateExpressionEntryFromSource(expr, task, dataObjects, normalizeAttribute: true));
    }

    private static EmittedExpression CreateEmittedExpressionFromSourceTranslated(
        SourceTranslatedExpression translated,
        ExpressionEntrySemantic? expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpressionEmissionContext context)
        => CreateContextualEmittedExpression(
            translated.Code,
            expr,
            task,
            dataObjects,
            context,
            translated.SourceReturnType,
            evidenceKind: string.IsNullOrWhiteSpace(translated.EvidenceKind) ? "source-translated" : translated.EvidenceKind);

    private static SourceTranslatedExpression CreateSourceTranslatedExpression(
        ExpressionEntrySemantic expr,
        string code,
        string sourceReturnType,
        string evidenceKind)
        => new(
            code,
            NormalizeReturnTypeToken(sourceReturnType),
            ResolveExpressionEntrySourceSyntax(expr),
            expr.Ordinal,
            evidenceKind);

    private static string ResolveRawExpressionEntryCode(
        ExpressionEntrySemantic expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        int expressionOrdinal)
    {
        var cacheKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{task.Ordinal}|{expressionOrdinal}");
        if (_rawExpressionCodeCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var sharedKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{task.Ordinal}|raw|{expr.Attribute}|{expr.IsStringLiteral}|{expr.Syntax}|{expr.LiteralNormalizedSyntax}");
        if (_sharedRawExpressionEntryCodeCache.TryGetValue(sharedKey, out var sharedCached))
        {
            _rawExpressionCodeCache[cacheKey] = sharedCached;
            return sharedCached;
        }

        var translated = TranslateExpressionEntryFromSource(expr, task, dataObjects, normalizeAttribute: false);
        _sharedRawExpressionEntryCodeCache[sharedKey] = translated.Code;
        _rawExpressionCodeCache[cacheKey] = translated.Code;
        return translated.Code;
    }

    private static string ResolveExpressionEntryCodeWithoutAttributeNormalization(ExpressionEntrySemantic? expr, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (expr is null || string.IsNullOrWhiteSpace(expr.Syntax))
            return "";

        return TranslateExpressionEntryFromSource(expr, task, dataObjects, normalizeAttribute: false).Code;
    }

    private static ExpressionEntrySemantic CreateSyntheticExpressionEntry(
        string sourceSyntax,
        string? attribute,
        int ordinal = 0)
    {
        var syntax = WebUtility.HtmlDecode(sourceSyntax ?? "").Trim();
        var attr = attribute?.Trim() ?? "";
        return new ExpressionEntrySemantic(
            ordinal,
            syntax,
            syntax,
            attr,
            syntax,
            TryParseWholeXpaSingleQuotedLiteral(syntax, out _),
            ContainsSourceToken(syntax, "EOP"),
            ContainsSourceToken(syntax, "IOCurr"),
            ContainsSourceToken(syntax, "Page"),
            ContainsSourceToken(syntax, "Line"),
            ContainsSourceToken(syntax, "Str("),
            null);
    }

    private static string ResolveSourceFragmentCode(
        string sourceSyntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpressionEmissionContext context,
        string? attribute = null)
    {
        if (string.IsNullOrWhiteSpace(sourceSyntax))
            return "";

        var expectedReturnType = NormalizeReturnTypeToken(ResolveReturnTypeForExpectedContext(context.Expected));
        var effectiveAttribute = !string.IsNullOrWhiteSpace(attribute)
            ? attribute
            : MapReturnTypeToSourceExpressionAttribute(expectedReturnType);
        var synthetic = CreateSyntheticExpressionEntry(sourceSyntax, effectiveAttribute);
        return ResolveTypedExpressionEntryCode(synthetic, task, dataObjects, context).Code;
    }

    private static bool ContainsSourceToken(string syntax, string token)
        => !string.IsNullOrWhiteSpace(syntax) &&
           !string.IsNullOrWhiteSpace(token) &&
           syntax.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

    private static SourceTranslatedExpression TranslateExpressionEntryFromSource(
        ExpressionEntrySemantic expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        bool normalizeAttribute,
        bool inferSourceReturnType = true,
        bool translateKnownSourceFunctions = true,
        string? contextualExpectedReturnType = null,
        bool preferApplicationDatabaseBinding = false)
    {
        var sourceReturnType = inferSourceReturnType
            ? ResolveSourceReturnTypeForExpressionEntry(expr, task, dataObjects)
            : "";
        string translated;
        if (TryTranslateSourceVarIndexLiteral(
                ResolveExpressionEntrySourceSyntax(expr),
                task,
                dataObjects,
                out var varIndexCode))
        {
            return CreateSourceTranslatedExpression(expr, varIndexCode, "Number", "var-index-literal");
        }

        if (TryEmitRightLiteralNumberFromSourceEvidence(
                ResolveExpressionEntrySourceSyntax(expr),
                expr.Attribute,
                contextualExpectedReturnType,
                out var rightLiteralNumber))
        {
            return CreateSourceTranslatedExpression(expr, rightLiteralNumber, "Number", "right-literal-number");
        }

        if (expr.IsStringLiteral)
        {
            translated = TryResolveXpaLogicalLiteralCode(expr.Syntax, out var logicalLiteralCode)
                ? logicalLiteralCode
                : ResolveStringLiteralExpressionCode(expr);
            return CreateSourceTranslatedExpression(expr, translated, inferSourceReturnType && !string.IsNullOrWhiteSpace(sourceReturnType) ? sourceReturnType : "Text", "string-literal");
        }

        var unsupportedEnvironmentFallback = TryTranslateUnsupportedEnvironmentExpression(expr);
        if (!string.IsNullOrWhiteSpace(unsupportedEnvironmentFallback))
        {
            translated = NormalizeDateConstructorMappings(unsupportedEnvironmentFallback);
            translated = normalizeAttribute
                ? NormalizeExpressionByAttribute(task, expr.Attribute, translated)
                : translated;
            return CreateSourceTranslatedExpression(expr, translated, sourceReturnType, "unsupported-environment");
        }

        var varDbNameCode = TryResolveVarDbNameExpression(expr.Syntax, task, dataObjects);
        if (!string.IsNullOrWhiteSpace(varDbNameCode))
        {
            translated = NormalizeDateConstructorMappings(varDbNameCode);
            translated = normalizeAttribute
                ? NormalizeExpressionByAttribute(task, expr.Attribute, translated)
                : translated;
            return CreateSourceTranslatedExpression(expr, translated, sourceReturnType, "var-db-name");
        }

        var special = TryTranslateWholeExpressionSemantically(expr, task);
        if (!string.IsNullOrWhiteSpace(special))
        {
            translated = NormalizeDateConstructorMappings(special);
            translated = normalizeAttribute
                ? NormalizeExpressionByAttribute(task, expr.Attribute, translated)
                : translated;
            return CreateSourceTranslatedExpression(expr, translated, sourceReturnType, "semantic-whole-expression");
        }

        var expressionAttributeReturnType = ResolveSimpleReturnTypeForExpressionAttribute(expr.Attribute);
        var sourceFunctionExpectedReturnType = !string.IsNullOrWhiteSpace(expressionAttributeReturnType)
            ? expressionAttributeReturnType
            : contextualExpectedReturnType;
        if (translateKnownSourceFunctions &&
            TryTranslateWholeKnownSourceFunctionCall(
                ResolveExpressionEntrySourceSyntax(expr),
                task,
                dataObjects,
                out var sourceFunctionTranslated,
                sourceFunctionExpectedReturnType,
                preferApplicationDatabaseBinding: preferApplicationDatabaseBinding))
        {
            translated = sourceFunctionTranslated.Code;
            if (normalizeAttribute && !SourceReturnTypeMatchesExpressionAttribute(sourceFunctionTranslated.SourceReturnType, expr.Attribute))
                translated = NormalizeExpressionByAttribute(task, expr.Attribute, translated);
            return sourceFunctionTranslated with
            {
                Code = translated,
                SourceSyntax = ResolveExpressionEntrySourceSyntax(expr),
                ExpressionOrdinal = expr.Ordinal,
                EvidenceKind = string.IsNullOrWhiteSpace(sourceFunctionTranslated.EvidenceKind)
                    ? "source-function"
                    : sourceFunctionTranslated.EvidenceKind
            };
        }

        var sourceArithmeticExpectedReturnType = !string.IsNullOrWhiteSpace(expressionAttributeReturnType)
            ? expressionAttributeReturnType
            : sourceReturnType;
        if (translateKnownSourceFunctions &&
            IsSafeTypedSourceArithmeticExpressionEntry(expr, sourceArithmeticExpectedReturnType) &&
            TryTranslateSourceArithmeticExpression(
                ResolveExpressionEntrySourceSyntax(expr),
                task,
                dataObjects,
                0,
                out var sourceArithmeticTranslated,
                sourceArithmeticExpectedReturnType,
                preferApplicationDatabaseBinding))
        {
            translated = sourceArithmeticTranslated.Code;
            if (normalizeAttribute && !SourceReturnTypeMatchesExpressionAttribute(sourceArithmeticTranslated.SourceReturnType, expr.Attribute))
                translated = NormalizeExpressionByAttribute(task, expr.Attribute, translated);
            return sourceArithmeticTranslated with
            {
                Code = translated,
                SourceSyntax = ResolveExpressionEntrySourceSyntax(expr),
                ExpressionOrdinal = expr.Ordinal,
                EvidenceKind = string.IsNullOrWhiteSpace(sourceArithmeticTranslated.EvidenceKind)
                    ? "source-arithmetic"
                    : sourceArithmeticTranslated.EvidenceKind
            };
        }

        translated = NormalizeDateConstructorMappings(TranslateXpaExpressionToCSharp(ResolveExpressionEntrySourceSyntax(expr), task, dataObjects, expr.Attribute));
        translated = NormalizeVarSetValueExpressions(translated);
        if (string.Equals(expr.Attribute, "A", StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = translated.Trim();
            const string uriPrefix = "new System.Uri(";
            if (trimmed.StartsWith(uriPrefix, StringComparison.Ordinal) && trimmed.EndsWith(")", StringComparison.Ordinal))
                translated = trimmed.Substring(uriPrefix.Length, trimmed.Length - uriPrefix.Length - 1).Trim();
        }

        if (normalizeAttribute)
            translated = NormalizeExpressionByAttribute(task, expr.Attribute, translated);

        return CreateSourceTranslatedExpression(expr, translated, sourceReturnType, "translated-expression");
    }

    private static bool IsSafeTypedSourceArithmeticExpressionEntry(ExpressionEntrySemantic expr, string sourceReturnType)
    {
        var normalizedReturnType = GetValueReturnType(NormalizeReturnTypeToken(sourceReturnType));
        var sourceArithmetic = TrySplitTopLevelSourceArithmeticExpression(ResolveExpressionEntrySourceSyntax(expr));
        if (sourceArithmetic is not null &&
            string.Equals(sourceArithmetic.Value.Operator, "&", StringComparison.Ordinal))
        {
            var attributeReturnType = ResolveSimpleReturnTypeForExpressionAttribute(expr.Attribute);
            return string.Equals(NormalizeReturnTypeToken(attributeReturnType), "Text", StringComparison.Ordinal) ||
                   string.Equals(normalizedReturnType, "Text", StringComparison.Ordinal);
        }

        if (string.IsNullOrWhiteSpace(normalizedReturnType) ||
            string.Equals(normalizedReturnType, "Text", StringComparison.Ordinal) ||
            string.Equals(normalizedReturnType, "byte[]", StringComparison.Ordinal) ||
            string.Equals(normalizedReturnType, "object", StringComparison.Ordinal))
            return false;

        var normalizedAttribute = (expr.Attribute ?? "").Trim();
        return string.Equals(normalizedAttribute, "N", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedAttribute, "D", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedAttribute, "T", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedAttribute, "B", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedAttribute, "L", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveExpressionEntryCode(
        ExpressionEntrySemantic? expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpressionEmissionContext context)
        => ResolveExpressionEntryCode(expr, task, dataObjects, context.Expected, context);

    private static string ResolveExpressionEntryCode(
        ExpressionEntrySemantic? expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpectedTypeContext expected)
        => ResolveExpressionEntryCode(expr, task, dataObjects, expected, CreateExpectedEmissionContext(expected));

    private static string ResolveExpressionEntryCode(
        ExpressionEntrySemantic? expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpectedTypeContext expected,
        ExpressionEmissionContext context)
        => ResolveExpressionEntry(expr, task, dataObjects, expected, context).Code;

    private static EmittedExpression ResolveExpressionEntry(
        ExpressionEntrySemantic? expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpectedTypeContext expected,
        ExpressionEmissionContext context)
    {
        if ((context.SinkKind == ExpressionSinkKind.Default || context.SinkKind == ExpressionSinkKind.ExpectedValue) &&
            !context.Expected.HasExpectation &&
            expected.HasExpectation)
            context = CreateExpectedEmissionContext(expected);

        if (expr is not null && ShouldUseTypedExpressionEntryResolution(context))
        {
            var typed = ResolveTypedExpressionEntryCode(expr, task, dataObjects, expected, context);
            if (context.SinkKind == ExpressionSinkKind.BooleanCondition)
                typed = typed with
                {
                    Code = NormalizeStatementBooleanConditionSyntax(typed.Code),
                    EffectiveReturnType = "Bool",
                    EffectiveXpaType = XpaType.Bool,
                    HasEffectiveType = true
                };
            RegisterContextualExpressionReturnType(task, typed.Code, context);
            return typed;
        }

        if (expr is not null && ShouldPreferContextualRawResolution(expr, context))
        {
            var rawResolved = ResolveRawExpressionEntryCode(expr, task, dataObjects, expr.Ordinal);
            var emitted = EmitExpressionForContext(rawResolved, task, context);
            RegisterContextualExpressionReturnType(task, emitted, context);
            return CreateContextualEmittedExpression(emitted, expr, task, dataObjects, context);
        }

        var resolved = ResolveExpressionEntryCode(expr, task, dataObjects);
        var contextual = EmitExpressionForContext(resolved, task, context);
        RegisterContextualExpressionReturnType(task, contextual, context);
        return CreateContextualEmittedExpression(contextual, expr, task, dataObjects, context);
    }

    private static EmittedExpression ResolveTypedExpressionEntryCode(
        ExpressionEntrySemantic? expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpressionEmissionContext context)
        => ResolveTypedExpressionEntryCode(expr, task, dataObjects, context.Expected, context);

    private static EmittedExpression ResolveTypedExpressionEntryCode(
        ExpressionEntrySemantic? expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpectedTypeContext expected,
        ExpressionEmissionContext context)
    {
        if ((context.SinkKind == ExpressionSinkKind.Default || context.SinkKind == ExpressionSinkKind.ExpectedValue) &&
            !context.Expected.HasExpectation &&
            expected.HasExpectation)
            context = CreateExpectedEmissionContext(expected);

        var cacheKey = BuildTypedExpressionCacheKey(expr, task, context);
        if (_typedExpressionEntryCodeCache.TryGetValue(cacheKey, out var cached))
        {
            Interlocked.Increment(ref _typedExpressionCacheHitCount);
            return cached;
        }

        Interlocked.Increment(ref _typedExpressionResolveCount);
        _typedExpressionResolveCountBySink.AddOrUpdate(context.SinkKind.ToString(), 1, static (_, count) => count + 1);

        var expectedReturnTypeForBooleanSource = NormalizeReturnTypeToken(ResolveReturnTypeForExpectedContext(context.Expected));
        var booleanSourceSyntax = expr is not null
            ? ResolveExpressionEntrySourceSyntax(expr)
            : "";
        if (expr is not null &&
            (context.SinkKind == ExpressionSinkKind.BooleanCondition ||
             (context.Expected.HasExpectation &&
              string.Equals(expectedReturnTypeForBooleanSource, "Bool", StringComparison.Ordinal) &&
              string.Equals(NormalizeReturnTypeToken(ResolveSimpleReturnTypeForExpressionAttribute(expr.Attribute)), "Bool", StringComparison.Ordinal))) &&
            TryTranslateSourceBooleanExpression(booleanSourceSyntax, task, dataObjects, out var booleanCode, expr.Syntax))
        {
            var emittedBoolean = NormalizeStatementBooleanConditionSyntax(booleanCode);
            var typedBoolean = new EmittedExpression(
                emittedBoolean,
                "Bool",
                XpaType.Bool,
                "Bool",
                XpaType.Bool,
                HasIntrinsicType: true,
                HasEffectiveType: true,
                UsedFallbackInference: false,
                CanEmitAsStatement: false,
                SourceSyntax: booleanSourceSyntax,
                SourceReturnType: "Bool",
                ExpressionOrdinal: expr.Ordinal,
                SinkKind: context.SinkKind.ToString(),
                ExpectedReturnType: ResolveReturnTypeForExpectedContext(context.Expected),
                EvidenceKind: "source-boolean");
            _typedExpressionEntryCodeCache[cacheKey] = typedBoolean;
            Interlocked.Increment(ref _typedExpressionIntrinsicKnownCount);
            Interlocked.Increment(ref _typedExpressionEffectiveKnownCount);
            RegisterContextualExpressionReturnType(task, emittedBoolean, context);
            RegisterTypedExpressionReturnType(task, emittedBoolean, "Bool");
            return typedBoolean;
        }

        if (TryResolveExpectedDominantTypedExpression(expr, task, dataObjects, context, out var expectedDominant))
        {
            _typedExpressionEntryCodeCache[cacheKey] = expectedDominant;
            if (expectedDominant.HasIntrinsicType)
                Interlocked.Increment(ref _typedExpressionIntrinsicKnownCount);
            if (expectedDominant.HasEffectiveType)
                Interlocked.Increment(ref _typedExpressionEffectiveKnownCount);
            RegisterTypedExpressionReturnType(task, expectedDominant.Code, expectedDominant.PreferredReturnType);
            return expectedDominant;
        }

        var sourceReturnType = ResolveSourceReturnTypeForExpressionEntry(expr, task, dataObjects);
        string code;
        string blobObjectReturnType;
        if (TryResolveBlobObjectViewBindingExpression(expr, task, dataObjects, context, sourceReturnType, out var blobObjectCode, out blobObjectReturnType))
        {
            code = blobObjectCode;
        }
        else
        {
            var sourceTranslated = ResolveContextualSourceTranslatedExpressionEntryCode(expr, task, dataObjects, context);
            if (string.IsNullOrWhiteSpace(sourceReturnType))
                sourceReturnType = sourceTranslated.SourceReturnType;
            code = sourceTranslated.Code;
        }
        var intrinsicReturnType = !string.IsNullOrWhiteSpace(sourceReturnType)
            ? sourceReturnType
            : ResolveIntrinsicReturnTypeForExpressionEntry(expr);
        var usedFallbackInference = false;
        var effectiveReturnType = !string.IsNullOrWhiteSpace(blobObjectReturnType)
            ? blobObjectReturnType
            : ResolveEffectiveReturnTypeForTypedExpression(expr, code, task, context, intrinsicReturnType, sourceReturnType, out usedFallbackInference);
        var typed = new EmittedExpression(
            code,
            intrinsicReturnType,
            XpaTypeEngine.MapExpectedToXpaType(intrinsicReturnType),
            effectiveReturnType,
            XpaTypeEngine.MapExpectedToXpaType(effectiveReturnType),
            !string.IsNullOrWhiteSpace(intrinsicReturnType),
            !string.IsNullOrWhiteSpace(effectiveReturnType),
            usedFallbackInference,
            CanEmitExpressionEntryAsStatement(expr, task, dataObjects),
            expr is null ? "" : ResolveExpressionEntrySourceSyntax(expr),
            sourceReturnType,
            expr?.Ordinal ?? 0,
            context.SinkKind.ToString(),
            ResolveReturnTypeForExpectedContext(context.Expected),
            string.IsNullOrWhiteSpace(blobObjectReturnType) ? "source-expression" : "blob-object-view");

        _typedExpressionEntryCodeCache[cacheKey] = typed;
        if (typed.HasIntrinsicType)
            Interlocked.Increment(ref _typedExpressionIntrinsicKnownCount);
        if (typed.HasEffectiveType)
            Interlocked.Increment(ref _typedExpressionEffectiveKnownCount);
        if (typed.UsedFallbackInference)
        {
            Interlocked.Increment(ref _typedExpressionFallbackInferenceCount);
            _typedExpressionFallbackCountBySink.AddOrUpdate(context.SinkKind.ToString(), 1, static (_, count) => count + 1);
        }
        RegisterTypedExpressionReturnType(task, typed.Code, typed.PreferredReturnType);
        return typed;
    }

    private static bool TryResolveExpectedDominantTypedExpression(
        ExpressionEntrySemantic? expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpressionEmissionContext context,
        out EmittedExpression typed)
    {
        typed = default;
        if (expr is null ||
            string.IsNullOrWhiteSpace(expr.Syntax) ||
            !context.Expected.HasExpectation ||
            ShouldInferSourceTypeBeforeExpectedShortcut(context))
            return false;

        var expectedReturnType = NormalizeReturnTypeToken(ResolveReturnTypeForExpectedContext(context.Expected));
        if (string.IsNullOrWhiteSpace(expectedReturnType))
            return false;

        var translated = TranslateExpressionEntryFromSource(
            expr,
            task,
            dataObjects,
            normalizeAttribute: false,
            inferSourceReturnType: true,
            translateKnownSourceFunctions: true,
            contextualExpectedReturnType: expectedReturnType);
        var code = translated.Code;
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var intrinsicReturnType = !string.IsNullOrWhiteSpace(translated.SourceReturnType)
            ? translated.SourceReturnType
            : ResolveIntrinsicReturnTypeForExpressionEntry(expr);
        if (string.IsNullOrWhiteSpace(intrinsicReturnType))
            intrinsicReturnType = expectedReturnType;

        var emitted = CoerceTypedExpressionCodeForExpectedType(code, intrinsicReturnType, expectedReturnType);
        if (context.SinkKind == ExpressionSinkKind.BooleanCondition)
            emitted = NormalizeStatementBooleanConditionSyntax(emitted);

        Interlocked.Increment(ref _typedExpressionExpectedTypeShortcutCount);
        _typedExpressionExpectedShortcutCountBySink.AddOrUpdate(context.SinkKind.ToString(), 1, static (_, count) => count + 1);
        RegisterContextualExpressionReturnType(task, emitted, context);
        typed = new EmittedExpression(
            emitted,
            intrinsicReturnType,
            XpaTypeEngine.MapExpectedToXpaType(intrinsicReturnType),
            expectedReturnType,
            XpaTypeEngine.MapExpectedToXpaType(expectedReturnType),
            !string.IsNullOrWhiteSpace(intrinsicReturnType),
            true,
            UsedFallbackInference: false,
            CanEmitAsStatement: CanEmitExpressionEntryAsStatement(expr, task, dataObjects),
            SourceSyntax: ResolveExpressionEntrySourceSyntax(expr),
            SourceReturnType: intrinsicReturnType,
            ExpressionOrdinal: expr.Ordinal,
            SinkKind: context.SinkKind.ToString(),
            ExpectedReturnType: expectedReturnType,
            EvidenceKind: "expected-dominant");
        return true;
    }

    private static string CoerceTypedExpressionCodeForExpectedType(
        string code,
        string sourceReturnType,
        string expectedReturnType)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        var sourceXpaType = XpaTypeEngine.MapExpectedToXpaType(GetValueReturnType(NormalizeReturnTypeToken(sourceReturnType)));
        var expectedXpaType = XpaTypeEngine.MapExpectedToXpaType(GetValueReturnType(NormalizeReturnTypeToken(expectedReturnType)));
        if (sourceXpaType == XpaType.Unknown ||
            expectedXpaType == XpaType.Unknown ||
            sourceXpaType == expectedXpaType ||
            !XpaTypeEngine.CanCoerce(sourceXpaType, expectedXpaType))
            return code;

        return XpaTypeEngine.Coerce(code, sourceXpaType, expectedXpaType);
    }

    private static bool ShouldInferSourceTypeBeforeExpectedShortcut(ExpressionEmissionContext context)
    {
        if (context.SinkKind == ExpressionSinkKind.RunArgument && context.PreserveBinding)
            return true;

        var expectedReturnType = NormalizeReturnTypeToken(ResolveReturnTypeForExpectedContext(context.Expected));
        if (string.Equals(expectedReturnType, "byte[]", StringComparison.Ordinal))
            return true;

        return context.SinkKind is ExpressionSinkKind.ViewBinding or ExpressionSinkKind.BooleanCondition;
    }

    private static SourceTranslatedExpression ResolveContextualSourceTranslatedExpressionEntryCode(
        ExpressionEntrySemantic? expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpressionEmissionContext context)
    {
        if (expr is null || string.IsNullOrWhiteSpace(expr.Syntax))
            return new("", "");

        if (context.SinkKind == ExpressionSinkKind.BooleanCondition &&
            TryTranslateSourceBooleanExpression(ResolveExpressionEntrySourceSyntax(expr), task, dataObjects, out var booleanCode, expr.Syntax))
        {
            var emittedBoolean = NormalizeStatementBooleanConditionSyntax(booleanCode);
            RegisterContextualExpressionReturnType(task, emittedBoolean, context);
            return CreateSourceTranslatedExpression(expr, emittedBoolean, "Bool", "source-boolean");
        }

        var translated = TranslateExpressionEntryFromSource(
            expr,
            task,
            dataObjects,
            normalizeAttribute: !ShouldPreferContextualRawResolution(expr, context),
            preferApplicationDatabaseBinding: context.SinkKind == ExpressionSinkKind.SqlExpression);
        if (!translated.HasSourceReturnType &&
            TryResolveTranslatedSourceBindingReturnType(translated.Code, task, out var translatedSourceReturnType) &&
            !string.IsNullOrWhiteSpace(translatedSourceReturnType))
            translated = translated with { SourceReturnType = translatedSourceReturnType };
        var emitted = TryEmitSourceTranslatedExpressionForKnownContext(translated, expr, task, dataObjects, context, out var typedEmitted)
            ? typedEmitted
            : EmitExpressionForContext(
                CreateEmittedExpressionFromSourceTranslated(translated, expr, task, dataObjects, context),
                task,
                context);
        RegisterContextualExpressionReturnType(task, emitted.Code, context);
        return translated with
        {
            Code = emitted.Code,
            SourceReturnType = emitted.HasSourceReturnType ? emitted.SourceReturnType : translated.SourceReturnType,
            EvidenceKind = string.IsNullOrWhiteSpace(emitted.EvidenceKind) ? translated.EvidenceKind : emitted.EvidenceKind
        };
    }

    private static bool TryEmitSourceTranslatedExpressionForKnownContext(
        SourceTranslatedExpression translated,
        ExpressionEntrySemantic expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpressionEmissionContext context,
        out EmittedExpression emitted)
    {
        emitted = default;
        if (string.IsNullOrWhiteSpace(translated.Code) ||
            string.IsNullOrWhiteSpace(translated.SourceReturnType) ||
            !context.Expected.HasExpectation)
            return false;

        if (context.SinkKind == ExpressionSinkKind.RunArgument && context.PreserveBinding)
            return false;

        var expectedReturnType = NormalizeReturnTypeToken(ResolveReturnTypeForExpectedContext(context.Expected));
        if (string.IsNullOrWhiteSpace(expectedReturnType))
            return false;

        var sourceXpaType = XpaTypeEngine.MapExpectedToXpaType(GetValueReturnType(NormalizeReturnTypeToken(translated.SourceReturnType)));
        var expectedXpaType = XpaTypeEngine.MapExpectedToXpaType(GetValueReturnType(expectedReturnType));
        if (sourceXpaType == XpaType.Unknown || expectedXpaType == XpaType.Unknown)
            return false;

        var emittedCode = EmitFromReliableTypeEvidence(translated.Code.Trim(), translated.SourceReturnType, expectedReturnType, "source-translated");
        if (!SourceReturnTypeMatchesExpected(translated.SourceReturnType, expectedReturnType) &&
            string.Equals(emittedCode, translated.Code.Trim(), StringComparison.Ordinal))
            return false;
        if (context.SinkKind == ExpressionSinkKind.BooleanCondition)
            emittedCode = NormalizeStatementBooleanConditionSyntax(emittedCode);

        emitted = CreateContextualEmittedExpression(
            emittedCode,
            expr,
            task,
            dataObjects,
            context,
            translated.SourceReturnType,
            evidenceKind: string.IsNullOrWhiteSpace(translated.EvidenceKind) ? "source-translated-context" : translated.EvidenceKind) with
        {
            EffectiveReturnType = expectedReturnType,
            EffectiveXpaType = XpaTypeEngine.MapExpectedToXpaType(expectedReturnType),
            HasEffectiveType = true,
            ExpectedReturnType = expectedReturnType
        };
        RegisterTypedExpressionReturnType(task, emitted.Code, expectedReturnType);
        return true;
    }

    private static bool TryTranslateWholeKnownSourceFunctionCall(
        string? syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out SourceTranslatedExpression translated,
        string? contextualExpectedReturnType = null,
        int depth = 0,
        bool preferApplicationDatabaseBinding = false)
    {
        translated = new("", "");
        if (string.IsNullOrWhiteSpace(syntax) || depth > 16)
            return false;

        var decoded = CompleteSourceGrouping(WebUtility.HtmlDecode(syntax).Trim());
        if (!TryParseFunctionCall(decoded, out var functionName, out var args))
            return false;

        var normalizedFunction = NormalizeXpaFunctionContractName(functionName);
        var isTypedSourceFunction = IsTypedSourceFunctionCandidate(normalizedFunction);
        var isCaseFunction = string.Equals(normalizedFunction, "CASE", StringComparison.Ordinal) ||
                             string.Equals(normalizedFunction, "CASEUNTYPED", StringComparison.Ordinal);
        FunctionOverrideSemantic? taskFunction = null;
        var taskFunctionTargetName = "";
        var hasTaskFunctionContract = !isTypedSourceFunction &&
                                      TryGetAccessibleFunctionContract(task, functionName, out taskFunction, out taskFunctionTargetName);
        ComponentFunctionCallContract componentFunctionContract = default;
        var hasComponentFunctionContract = !isTypedSourceFunction &&
                                           TryGetComponentFunctionCallContract(functionName, out componentFunctionContract);
        if (!hasTaskFunctionContract && !hasComponentFunctionContract && !isTypedSourceFunction)
            return false;

        var info = hasTaskFunctionContract
            ? new SourceFunctionCallTypeInfo(
                functionName,
                NormalizeReturnTypeToken(taskFunction!.ReturnType),
                taskFunction.Parameters.Select(p => NormalizeReturnTypeToken(p.ParameterType)).ToArray(),
                Array.Empty<string>())
            : hasComponentFunctionContract
                ? new SourceFunctionCallTypeInfo(
                    functionName,
                    NormalizeReturnTypeToken(componentFunctionContract.ReturnType),
                    componentFunctionContract.ParameterTypes.Select(NormalizeReturnTypeToken).ToArray(),
                    Array.Empty<string>())
            : AnalyzeSourceFunctionCallTypeInfo(functionName, args, task, dataObjects, depth + 1);
        var returnType = NormalizeReturnTypeToken(info.ReturnType);
        if (!hasTaskFunctionContract && !hasComponentFunctionContract)
        {
            if (string.IsNullOrWhiteSpace(returnType) &&
                TryResolveKnownXpaFunctionReturnType(functionName, args, out var knownReturnType))
                returnType = NormalizeReturnTypeToken(knownReturnType);
            if (string.IsNullOrWhiteSpace(returnType) &&
                IsContextuallyTypedSourceFunction(normalizedFunction))
                returnType = NormalizeReturnTypeToken(contextualExpectedReturnType);

            var contextualReturnType = NormalizeReturnTypeToken(contextualExpectedReturnType);
            if (!string.IsNullOrWhiteSpace(contextualReturnType))
            {
                if (IsContextuallyTypedSourceFunction(normalizedFunction) &&
                    IsReliableSourceReturnType(contextualReturnType))
                    returnType = contextualReturnType;
                else if (string.Equals(normalizedFunction, "CNDRANGE", StringComparison.Ordinal) &&
                    args.Count >= 2 &&
                    IsSourceScalarReturnType(contextualReturnType))
                    returnType = contextualReturnType;
                else if (CanUseExpectedTypeForObjectSourceFunction(normalizedFunction, returnType) &&
                         IsReliableSourceReturnType(contextualReturnType))
                    returnType = contextualReturnType;
            }
        }

        var targetFunctionName = hasTaskFunctionContract
            ? taskFunctionTargetName
            : hasComponentFunctionContract
                ? componentFunctionContract.TargetName
            : ResolveTypedSourceFunctionTargetName(functionName, normalizedFunction, returnType);
        if (string.IsNullOrWhiteSpace(targetFunctionName))
            return false;

        var rewrittenArgs = new List<SourceFunctionArgumentExpression>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            var expectedArgType = i < info.ExpectedArgumentReturnTypes.Length
                ? NormalizeReturnTypeToken(info.ExpectedArgumentReturnTypes[i])
                : "";
            if (string.Equals(normalizedFunction, "CNDRANGE", StringComparison.Ordinal) &&
                i == 1 &&
                IsSourceScalarReturnType(returnType))
                expectedArgType = returnType;
            if (string.Equals(normalizedFunction, "IF", StringComparison.Ordinal) &&
                i is 1 or 2 &&
                !string.IsNullOrWhiteSpace(returnType))
                expectedArgType = returnType;
            if (string.IsNullOrWhiteSpace(expectedArgType))
            {
                expectedArgType = normalizedFunction switch
                {
                    "STR" when i == 0 => "Number",
                    "DSTR" when i == 0 => "Date",
                    "TSTR" when i == 0 => "Time",
                    _ => ""
                };
            }
            if (string.IsNullOrWhiteSpace(expectedArgType) &&
                TryResolveXpaFunctionSourceArgumentReturnTypeContract(functionName, i, args.Count, out var contractArgType))
                expectedArgType = NormalizeReturnTypeToken(contractArgType);

            SourceFunctionArgumentExpression rewrittenArg;
            if (string.Equals(normalizedFunction, "IF", StringComparison.Ordinal) &&
                i == 0 &&
                args.Count >= 3 &&
                !string.IsNullOrWhiteSpace(returnType) &&
                TryTranslateSourceIfBranchComparisonCondition(
                    args[0],
                    args[1],
                    args[2],
                    returnType,
                    task,
                    dataObjects,
                    out var ifConditionCode,
                    preferApplicationDatabaseBinding))
            {
                rewrittenArg = new SourceFunctionArgumentExpression(
                    ifConditionCode,
                    "Bool",
                    "Bool",
                    "Bool",
                    "source-if-branch-comparison-condition");
            }
            else if (string.Equals(normalizedFunction, "STR", StringComparison.Ordinal) &&
                i == 0 &&
                string.Equals(NormalizeReturnTypeToken(expectedArgType), "Number", StringComparison.Ordinal) &&
                TryTranslateWholeKnownSourceFunctionCall(
                    args[i],
                    task,
                    dataObjects,
                    out var numericSourceFunction,
                    expectedArgType,
                    depth + 1,
                    preferApplicationDatabaseBinding) &&
                SourceReturnTypeMatchesExpected(numericSourceFunction.SourceReturnType, expectedArgType))
            {
                rewrittenArg = new SourceFunctionArgumentExpression(
                    numericSourceFunction.Code,
                    numericSourceFunction.SourceReturnType,
                    numericSourceFunction.SourceReturnType,
                    expectedArgType,
                    string.IsNullOrWhiteSpace(numericSourceFunction.EvidenceKind)
                        ? "source-function-argument"
                        : numericSourceFunction.EvidenceKind);
            }
            else
            {
                rewrittenArg = TranslateSourceFunctionArgumentExpression(
                    args[i],
                    expectedArgType,
                    task,
                    dataObjects,
                    depth + 1,
                    preferApplicationDatabaseBinding);
            }

            if (string.Equals(normalizedFunction, "IF", StringComparison.Ordinal) &&
                i is 1 or 2 &&
                !string.IsNullOrWhiteSpace(returnType))
                rewrittenArg = CoerceSourceFunctionArgumentExpressionForExpected(rewrittenArg, returnType);

            rewrittenArgs.Add(rewrittenArg);
        }

        if (string.IsNullOrWhiteSpace(returnType))
            returnType = InferSourceFunctionReturnTypeFromTypedArguments(normalizedFunction, rewrittenArgs);

        var rewrittenArgCodes = rewrittenArgs.Select(static arg => arg.Code).ToArray();
        var code = isCaseFunction && BuildTypeValuedCaseExpression(rewrittenArgCodes, out var typeCaseCode)
            ? typeCaseCode
            : $"{targetFunctionName}({string.Join(", ", rewrittenArgCodes)})";
        if (!hasTaskFunctionContract && !hasComponentFunctionContract)
            code = MaterializeTypedSourceFunctionReturn(normalizedFunction, code, returnType);
        code = NormalizeDateConstructorMappings(NormalizeVarSetValueExpressions(code));
        code = CollapseRedundantScalarCastWrappersDeep(code);
        translated = new(code, returnType, decoded, 0, "source-function");
        return true;
    }

    private static SourceFunctionArgumentExpression TranslateSourceFunctionArgumentExpression(
        string argSyntax,
        string expectedReturnType,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        int depth,
        bool preferApplicationDatabaseBinding = false)
    {
        var normalizedExpected = NormalizeReturnTypeToken(expectedReturnType);
        var sourceReturnType = NormalizeReturnTypeToken(ResolveSourceExpressionReturnType(argSyntax, task, dataObjects, depth + 1));
        var code = TranslateSourceFunctionArgument(
            argSyntax,
            normalizedExpected,
            task,
            dataObjects,
            depth,
            preferApplicationDatabaseBinding);

        if (string.IsNullOrWhiteSpace(sourceReturnType) &&
            TryResolveTranslatedSourceBindingReturnType(code, task, out var translatedReturnType))
            sourceReturnType = NormalizeReturnTypeToken(translatedReturnType);

        var effectiveReturnType = sourceReturnType;
        if (!string.IsNullOrWhiteSpace(normalizedExpected) &&
            !string.IsNullOrWhiteSpace(sourceReturnType) &&
            SourceReturnTypeMatchesExpected(sourceReturnType, normalizedExpected))
            effectiveReturnType = normalizedExpected;
        else if (!string.IsNullOrWhiteSpace(normalizedExpected) &&
                 !string.IsNullOrWhiteSpace(sourceReturnType) &&
                 CanCoerceSourceFunctionArgument(sourceReturnType, normalizedExpected))
            effectiveReturnType = normalizedExpected;
        else if (string.IsNullOrWhiteSpace(effectiveReturnType) &&
                 !string.IsNullOrWhiteSpace(normalizedExpected))
            effectiveReturnType = normalizedExpected;

        return new SourceFunctionArgumentExpression(
            code.Trim(),
            sourceReturnType,
            NormalizeReturnTypeToken(effectiveReturnType),
            normalizedExpected,
            "source-function-argument");
    }

    private static SourceFunctionArgumentExpression CoerceSourceFunctionArgumentExpressionForExpected(
        SourceFunctionArgumentExpression argument,
        string expectedReturnType)
    {
        var normalizedExpected = NormalizeReturnTypeToken(expectedReturnType);
        if (string.IsNullOrWhiteSpace(argument.Code) ||
            string.IsNullOrWhiteSpace(normalizedExpected))
            return argument;

        var sourceReturnType = !string.IsNullOrWhiteSpace(argument.SourceReturnType)
            ? argument.SourceReturnType
            : argument.EffectiveReturnType;
        if (string.IsNullOrWhiteSpace(sourceReturnType) ||
            SourceReturnTypeMatchesExpected(sourceReturnType, normalizedExpected) ||
            !CanCoerceSourceFunctionArgument(sourceReturnType, normalizedExpected))
        {
            return argument with
            {
                ExpectedReturnType = normalizedExpected,
                EffectiveReturnType = SourceReturnTypeMatchesExpected(argument.EffectiveReturnType, normalizedExpected)
                    ? normalizedExpected
                    : argument.EffectiveReturnType
            };
        }

        var coerced = EmitFromReliableTypeEvidence(
            argument.Code,
            sourceReturnType,
            normalizedExpected,
            "source-function-argument");
        return argument with
        {
            Code = coerced.Trim(),
            ExpectedReturnType = normalizedExpected,
            EffectiveReturnType = normalizedExpected,
            EvidenceKind = "source-function-argument-coerced"
        };
    }

    private static bool CanCoerceSourceFunctionArgument(string sourceReturnType, string expectedReturnType)
    {
        var sourceXpaType = XpaTypeEngine.MapExpectedToXpaType(GetValueReturnType(NormalizeReturnTypeToken(sourceReturnType)));
        var expectedXpaType = XpaTypeEngine.MapExpectedToXpaType(GetValueReturnType(NormalizeReturnTypeToken(expectedReturnType)));
        return sourceXpaType != XpaType.Unknown &&
               expectedXpaType != XpaType.Unknown &&
               XpaTypeEngine.CanCoerce(sourceXpaType, expectedXpaType);
    }

    private static string InferSourceFunctionReturnTypeFromTypedArguments(
        string normalizedFunction,
        IReadOnlyList<SourceFunctionArgumentExpression> args)
    {
        if (string.Equals(normalizedFunction, "IF", StringComparison.Ordinal) &&
            args.Count >= 3)
            return UnifySourceReturnTypes(args[1].PreferredReturnType, args[2].PreferredReturnType);

        if ((string.Equals(normalizedFunction, "CASE", StringComparison.Ordinal) ||
             string.Equals(normalizedFunction, "CASEUNTYPED", StringComparison.Ordinal)) &&
            args.Count >= 3)
        {
            var returnType = "";
            for (var i = 2; i < args.Count; i += 2)
            {
                var candidate = args[i].PreferredReturnType;
                returnType = string.IsNullOrWhiteSpace(returnType)
                    ? candidate
                    : UnifySourceReturnTypes(returnType, candidate);
            }

            if (args.Count % 2 == 0)
            {
                var defaultCandidate = args[^1].PreferredReturnType;
                returnType = string.IsNullOrWhiteSpace(returnType)
                    ? defaultCandidate
                    : UnifySourceReturnTypes(returnType, defaultCandidate);
            }

            return NormalizeReturnTypeToken(returnType);
        }

        if (string.Equals(normalizedFunction, "CNDRANGE", StringComparison.Ordinal) &&
            args.Count >= 2)
            return NormalizeReturnTypeToken(args[1].PreferredReturnType);

        return "";
    }

    private static bool TryEmitRightLiteralNumberFromSourceEvidence(
        string? syntax,
        string? expressionAttribute,
        string? contextualExpectedReturnType,
        out string code)
    {
        code = "";
        var expectedReturnType = NormalizeReturnTypeToken(contextualExpectedReturnType);
        var attributeReturnType = NormalizeReturnTypeToken(ResolveSimpleReturnTypeForExpressionAttribute(expressionAttribute));
        if (!string.Equals(expectedReturnType, "Number", StringComparison.Ordinal) &&
            !string.Equals(attributeReturnType, "Number", StringComparison.Ordinal))
            return false;

        var source = WebUtility.HtmlDecode(syntax ?? "").Trim();
        if (string.IsNullOrWhiteSpace(source) ||
            !TryReadXpaLiteral(source, 0, out var literalEnd, out var literalValue, out _))
            return false;

        var cursor = literalEnd + 1;
        while (cursor < source.Length && char.IsWhiteSpace(source[cursor]))
            cursor++;

        if (!TryReadLiteralPostfixToken(source, cursor, out var token, out var tokenEnd) ||
            !string.Equals(token, "RIGHT", StringComparison.OrdinalIgnoreCase))
            return false;

        cursor = tokenEnd + 1;
        while (cursor < source.Length && char.IsWhiteSpace(source[cursor]))
            cursor++;
        if (cursor != source.Length)
            return false;

        var comma = literalValue.IndexOf(',');
        var numericPart = (comma >= 0 ? literalValue[..comma] : literalValue).Trim();
        if (!long.TryParse(numericPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rightNumber))
            return false;

        code = rightNumber.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryTranslateSourceVarIndexLiteral(
        string? syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string code)
    {
        code = "";
        var source = WebUtility.HtmlDecode(syntax ?? "").Trim();
        if (string.IsNullOrWhiteSpace(source) ||
            !TryReadXpaLiteral(source, 0, out var literalEnd, out var literalValue, out _) ||
            !IsBareIdentifier(literalValue))
            return false;

        var cursor = literalEnd + 1;
        while (cursor < source.Length && char.IsWhiteSpace(source[cursor]))
            cursor++;

        if (!TryReadLiteralPostfixToken(source, cursor, out var token, out var tokenEnd) ||
            !string.Equals(token, "VAR", StringComparison.OrdinalIgnoreCase))
            return false;

        cursor = tokenEnd + 1;
        while (cursor < source.Length && char.IsWhiteSpace(source[cursor]))
            cursor++;
        if (cursor != source.Length)
            return false;

        var allTasks = _allTasks ?? Array.Empty<TaskSemantic>();
        var binding = ResolveExpressionOrdinalBinding(literalValue, task, allTasks, dataObjects);
        if (string.IsNullOrWhiteSpace(binding))
            binding = ResolveDirectSourceBindingCode(literalValue, task, dataObjects, "");
        if (string.IsNullOrWhiteSpace(binding))
            binding = literalValue.Trim();

        code = $"u.IndexOf({binding})";
        return true;
    }

    private static bool IsContextuallyTypedSourceFunction(string normalizedFunction)
        => string.Equals(normalizedFunction, "IF", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CASE", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CASEUNTYPED", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "SHAREDVALGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "TREEVALUE", StringComparison.Ordinal);

    private static bool IsRuntimeValueAccessorFunction(string normalizedFunction)
        => string.Equals(normalizedFunction, "VARCURR", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VARCURRN", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VARPREV", StringComparison.Ordinal);

    private static bool IsSourceScalarReturnType(string returnType)
    {
        var valueReturnType = GetValueReturnType(NormalizeReturnTypeToken(returnType));
        return string.Equals(valueReturnType, "Text", StringComparison.Ordinal) ||
               string.Equals(valueReturnType, "Number", StringComparison.Ordinal) ||
               string.Equals(valueReturnType, "Date", StringComparison.Ordinal) ||
               string.Equals(valueReturnType, "Time", StringComparison.Ordinal) ||
               string.Equals(valueReturnType, "Bool", StringComparison.Ordinal) ||
               string.Equals(valueReturnType, "byte[]", StringComparison.Ordinal);
    }

    private static string MaterializeTypedSourceFunctionReturn(string normalizedFunction, string code, string returnType)
    {
        var normalizedReturnType = NormalizeReturnTypeToken(returnType);
        if (string.IsNullOrWhiteSpace(code) ||
            string.IsNullOrWhiteSpace(normalizedReturnType) ||
            string.Equals(normalizedReturnType, "object", StringComparison.Ordinal))
            return code;

        if (FunctionReturnsClrObjectBeforeXpaMaterialization(normalizedFunction))
            return EmitFromReliableTypeEvidence(code, "object", normalizedReturnType, normalizedFunction);

        return code;
    }

    private static bool CanUseExpectedTypeForObjectSourceFunction(string normalizedFunction, string returnType)
        => IsRuntimeValueAccessorFunction(normalizedFunction) ||
           string.Equals(normalizedFunction, "SHAREDVALGET", StringComparison.Ordinal) ||
           string.Equals(NormalizeReturnTypeToken(returnType), "object", StringComparison.Ordinal);

    private static bool IsReliableSourceReturnType(string returnType)
    {
        var valueReturnType = GetValueReturnType(NormalizeReturnTypeToken(returnType));
        return IsSourceScalarReturnType(valueReturnType) ||
               string.Equals(valueReturnType, "Text[]", StringComparison.Ordinal) ||
               string.Equals(valueReturnType, "Number[]", StringComparison.Ordinal) ||
               string.Equals(valueReturnType, "Date[]", StringComparison.Ordinal) ||
               string.Equals(valueReturnType, "Time[]", StringComparison.Ordinal) ||
               string.Equals(valueReturnType, "Bool[]", StringComparison.Ordinal) ||
               string.Equals(valueReturnType, "byte[][]", StringComparison.Ordinal);
    }

    private static bool FunctionReturnsClrObjectBeforeXpaMaterialization(string normalizedFunction)
        => string.Equals(normalizedFunction, "FILEINFO", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CLIENTFILEINFO", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "EDITGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "TREEVALUE", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VARCURR", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VARCURRN", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VARPREV", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CALLDLL", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CALLDLLF", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VARIANTGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VECGET", StringComparison.Ordinal) ||
           IsJavaGetStaticFunctionName(normalizedFunction) ||
           string.Equals(normalizedFunction, "NULL", StringComparison.Ordinal);

    private static bool IsTypedSourceFunctionCandidate(string normalizedFunction)
        => string.Equals(normalizedFunction, "LEFT", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "RIGHT", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "MID", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "TRIM", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "LTRIM", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "RTRIM", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "UPPER", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "LOWER", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "FLIP", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "DEL", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "TRANSLATE", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "ASCIICHR", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "REPSTR", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VAL", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "ASTR", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "STR", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "DSTR", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "TSTR", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "ABS", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "ACOS", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "ASIN", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "ATAN", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "COS", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "EXP", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "FIX", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "LOG", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "MOD", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "ROUND", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "SIN", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "TAN", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "LEN", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "INSTR", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "STRTOKEN", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "STRTOKENCNT", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "STRTOKENIDX", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "GETPARAM", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "GETTEXTPARAM", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "SETPARAM", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "STATUSBARSETTEXT", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "DATE", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "DVAL", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "ADDDATE", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "MDATE", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "UTCDATE", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "TIME", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "TVAL", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "ADDTIME", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "MTIME", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "UTCMTIME", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "UTCTIME", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "DOW", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "YEAR", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "MONTH", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "DAY", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "HOUR", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "MINUTE", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "SECOND", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "BOY", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "EOY", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CDOW", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CMONTH", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "NDOW", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "NMONTH", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "APPNAME", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "PROJECTDIR", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "PUBLICNAME", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "GETCOMPONENTNAME", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "GETGUID", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "EDITGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "HSTR", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "IF", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CASE", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CASEUNTYPED", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CNDRANGE", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "NOT", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "RIGHTS", StringComparison.Ordinal) ||
            string.Equals(normalizedFunction, "ISNULL", StringComparison.Ordinal) ||
            string.Equals(normalizedFunction, "RANGE", StringComparison.Ordinal) ||
            string.Equals(normalizedFunction, "RANGEADD", StringComparison.Ordinal) ||
            string.Equals(normalizedFunction, "LOCATEADD", StringComparison.Ordinal) ||
            string.Equals(normalizedFunction, "FILEEXIST", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CLIENTFILEEXIST", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "IMAGERELOAD", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "DNEXCEPTIONOCCURRED", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "IN", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "FILEINFO", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CLIENTFILEINFO", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "FILELISTGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CLIENTFILELISTGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CALLDLL", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "CALLDLLF", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "COMHANDLEGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VARCURR", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VARCURRN", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VARPREV", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "NULL", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VARSET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "SHAREDVALGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "TREELEVEL", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "TREEVALUE", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VARIANTGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "VECGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "DIFDATETIME", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "UTF8FROMANSI", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "UTF8TOANSI", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "UNICODEFROMANSI", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "UNICODETOANSI", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "XMLINSERT", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "JSONINSERT", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "JSONMODIFY", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "JSONDELETE", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "JSONEXIST", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "JSONFIND", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "JSONGET", StringComparison.Ordinal) ||
           string.Equals(normalizedFunction, "JSONCNT", StringComparison.Ordinal);

    private static string ResolveTypedSourceFunctionTargetName(string functionName, string normalizedFunction, string returnType = "")
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return "";

        var trimmed = functionName.Trim();
        if (trimmed.StartsWith("u.", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        if (string.Equals(normalizedFunction, "SHAREDVALGET", StringComparison.Ordinal) &&
            TryResolveTypedSharedValGetTargetName(returnType, out var sharedValTarget))
            return sharedValTarget;

        if (string.Equals(normalizedFunction, "TIME", StringComparison.Ordinal))
            return "u.Time";

        if (_xpaFunctionMap.TryGetValue(trimmed, out var mapped))
            return mapped;

        return normalizedFunction switch
        {
            "LEFT" => "u.Left",
            "RIGHT" => "u.Right",
            "MID" => "u.Mid",
            "VAL" => "u.Val",
            "ASTR" => "u.AStr",
            "STR" => "u.Str",
            "DSTR" => "u.DStr",
            "TSTR" => "u.TStr",
            "REPSTR" => "u.RepStr",
            "GETPARAM" => "u.GetTextParam",
            "GETTEXTPARAM" => "u.GetTextParam",
            "EDITGET" => "u.EditGet",
            "HSTR" => "u.HStr",
            "IF" => "u.If",
            "CASE" => "u.Case",
            "CASEUNTYPED" => "u.CaseUntyped",
            "CNDRANGE" => "u.CndRange",
            "NOT" => "u.Not",
            "RIGHTS" => "u.Rights",
            "ISNULL" => "u.IsNull",
            "RANGE" => "u.Range",
            "RANGEADD" => "u.RangeAdd",
            "LOCATEADD" => "u.LocateAdd",
            "FILEEXIST" => "u.FileExist",
            "CLIENTFILEEXIST" => "u.ClientFileExist",
            "DNEXCEPTIONOCCURRED" => "u.DNExceptionOccurred",
            "IN" => "u.In",
            "FILEINFO" => "u.FileInfo",
            "CLIENTFILEINFO" => "u.ClientFileInfo",
            "FILELISTGET" => "u.FileListGet",
            "CLIENTFILELISTGET" => "u.ClientFileListGet",
            "CALLDLL" => "u.CallDLL",
            "CALLDLLF" => "u.CallDLLF",
            "COMHANDLEGET" => "u.COMHandleGet",
            "VARCURR" => "u.VarCurr",
            "VARCURRN" => "u.VarCurrN",
            "VARPREV" => "u.VarPrev",
            "NULL" => "u.Null",
            "VARSET" => "u.VarSet",
            "TREELEVEL" => "u.TreeLevel",
            "TREEVALUE" => "u.TreeValue",
            "VARIANTGET" => "u.VariantGet",
            "VECGET" => "u.VecGet",
            "DIFDATETIME" => "u.DifDateTime",
            "UTF8FROMANSI" => "u.UTF8FromAnsi",
            "UTF8TOANSI" => "u.UTF8ToAnsi",
            "UNICODEFROMANSI" => "u.UnicodeFromAnsi",
            "UNICODETOANSI" => "u.UnicodeToAnsi",
            "JSONINSERT" => "JSONInsert",
            "JSONMODIFY" => "JSONModify",
            "JSONDELETE" => "JSONDelete",
            "JSONEXIST" => "JSONExist",
            "JSONFIND" => "JSONFind",
            "JSONGET" => "JSONGet",
            "JSONCNT" => "JSONCnt",
            "TIME" => "u.Time",
            _ => ""
        };
    }

    private static bool TryResolveTypedSharedValGetTargetName(string returnType, out string targetName)
    {
        targetName = NormalizeReturnTypeToken(returnType) switch
        {
            "Number" => "u.SharedValGetNumber",
            "Date" => "u.SharedValGetDate",
            "Time" => "u.SharedValGetTime",
            "Bool" => "u.SharedValGetBool",
            "Text" => "u.SharedValGetText",
            "Text[]" => "u.SharedValGetTextArray",
            "Number[]" => "u.SharedValGetNumberArray",
            "byte[]" => "u.SharedValGet",
            _ => ""
        };
        return !string.IsNullOrWhiteSpace(targetName);
    }

    private static string TranslateSourceFunctionArgument(
        string argSyntax,
        string expectedReturnType,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        int depth,
        bool preferApplicationDatabaseBinding = false)
    {
        if (string.IsNullOrWhiteSpace(argSyntax))
            return "";

        var trimmed = StripRedundantOuterParentheses(CompleteSourceGrouping(argSyntax.Trim()));
        var normalizedExpectedReturnType = NormalizeReturnTypeToken(expectedReturnType);
        if (TryTranslateSourceVarIndexLiteral(trimmed, task, dataObjects, out var varIndexCode))
        {
            if (string.IsNullOrWhiteSpace(normalizedExpectedReturnType) ||
                string.Equals(normalizedExpectedReturnType, "Number", StringComparison.Ordinal) ||
                IsClrObjectReturnTypeForTypedExpression(normalizedExpectedReturnType))
                return varIndexCode;

            return EmitFromReliableTypeEvidence(
                varIndexCode,
                "Number",
                normalizedExpectedReturnType,
                "source-var-index-argument").Trim();
        }

        if (IsColumnBaseReturnContract(normalizedExpectedReturnType) &&
            TryTranslateSourceColumnContractArgument(trimmed, task, dataObjects, out var columnArgument))
            return columnArgument;

        if (!string.IsNullOrWhiteSpace(normalizedExpectedReturnType) &&
            !string.Equals(normalizedExpectedReturnType, "Bool", StringComparison.Ordinal) &&
            !IsClrObjectReturnTypeForTypedExpression(normalizedExpectedReturnType) &&
            TryTranslateWholeKnownSourceFunctionCall(trimmed, task, dataObjects, out var directSourceFunction, normalizedExpectedReturnType, depth + 1, preferApplicationDatabaseBinding) &&
            SourceReturnTypeMatchesExpected(directSourceFunction.SourceReturnType, normalizedExpectedReturnType))
        {
            return directSourceFunction.Code.Trim();
        }

        if ((string.IsNullOrWhiteSpace(normalizedExpectedReturnType) ||
             IsClrObjectReturnTypeForTypedExpression(normalizedExpectedReturnType)) &&
            TryParseWholeXpaSingleQuotedLiteral(WebUtility.HtmlDecode(trimmed), out var sourceLiteralText))
            return ToCSharpLiteral(sourceLiteralText);

        if (string.Equals(normalizedExpectedReturnType, "Bool", StringComparison.Ordinal))
        {
            if (TryTranslateSourceBooleanExpression(trimmed, task, dataObjects, out var booleanCode, trimmed, preferApplicationDatabaseBinding))
                return NormalizeStatementBooleanConditionSyntax(booleanCode);

            if (TryTranslateWholeKnownSourceFunctionCall(trimmed, task, dataObjects, out var sourceFunctionTranslated, "Bool", depth + 1, preferApplicationDatabaseBinding))
            {
                var sourceFunctionCode = sourceFunctionTranslated.Code.Trim();
                if (!string.IsNullOrWhiteSpace(sourceFunctionTranslated.SourceReturnType) &&
                    !SourceReturnTypeMatchesExpected(sourceFunctionTranslated.SourceReturnType, "Bool"))
                {
                    sourceFunctionCode = EmitFromReliableTypeEvidence(
                        sourceFunctionCode,
                        sourceFunctionTranslated.SourceReturnType,
                        "Bool",
                        "source-function-argument");
                }

                return NormalizeStatementBooleanConditionSyntax(sourceFunctionCode);
            }

            var booleanTranslated = NormalizeDateConstructorMappings(TranslateXpaExpressionToCSharp(trimmed, task, dataObjects, "B"));
            booleanTranslated = NormalizeVarSetValueExpressions(booleanTranslated);
            booleanTranslated = StripRedundantOuterParentheses(booleanTranslated.Trim());
            return RewriteBooleanOperand(task, booleanTranslated);
        }

        if (IsClrObjectReturnTypeForTypedExpression(normalizedExpectedReturnType) &&
            TryTranslateSourceClrScalarOperand(trimmed, task, dataObjects, out var clrCode))
            return clrCode.Trim();

        if (TryTranslateSourceLiteralForExpectedReturnType(trimmed, expectedReturnType, out var literalCode))
            return literalCode;

        if (preferApplicationDatabaseBinding &&
            TryResolveApplicationDatabaseConfigSourceBinding(
                trimmed,
                task,
                normalizedExpectedReturnType,
                out var applicationDatabaseBinding,
                out _))
            return applicationDatabaseBinding;

        if (TryReadRegisteredExpressionCallOrdinal(trimmed, out var registeredExpressionOrdinal) &&
            task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(registeredExpressionOrdinal, out var registeredExpression) &&
            registeredExpression is not null)
        {
            var registeredCode = $"Exp_{registeredExpressionOrdinal}()";
            var registeredReturnType = ResolveIntrinsicReturnTypeForExpressionEntry(registeredExpression);
            if (string.IsNullOrWhiteSpace(registeredReturnType))
                registeredReturnType = ResolveSourceReturnTypeForExpressionEntry(registeredExpression, task, dataObjects);
            if (string.IsNullOrWhiteSpace(normalizedExpectedReturnType) ||
                SourceReturnTypeMatchesExpected(registeredReturnType, normalizedExpectedReturnType))
                return registeredCode;

            return EmitFromReliableTypeEvidence(
                registeredCode,
                registeredReturnType,
                normalizedExpectedReturnType,
                "registered-expression");
        }

        if (normalizedExpectedReturnType is "Number" or "Date" or "Time" &&
            TryTranslateSourceArithmeticExpression(trimmed, task, dataObjects, depth + 1, out var expectedArithmetic, normalizedExpectedReturnType, preferApplicationDatabaseBinding) &&
            SourceReturnTypeMatchesExpected(expectedArithmetic.SourceReturnType, normalizedExpectedReturnType))
            return expectedArithmetic.Code.Trim();

        if (TryTranslateDirectSourceBindingFunctionArgument(
                trimmed,
                expectedReturnType,
                task,
                dataObjects,
                depth + 1,
                out var directBindingArgument))
            return directBindingArgument;

        string code;
        string sourceReturnType;
        var usedTypedSourceFunction = false;
        if (TryTranslateWholeKnownSourceFunctionCall(trimmed, task, dataObjects, out var nested, expectedReturnType, depth + 1, preferApplicationDatabaseBinding))
        {
            code = nested.Code;
            sourceReturnType = nested.SourceReturnType;
            usedTypedSourceFunction = true;
        }
        else if (TryTranslateSourceArithmeticExpression(trimmed, task, dataObjects, depth + 1, out var arithmeticTranslated, expectedReturnType, preferApplicationDatabaseBinding))
        {
            code = arithmeticTranslated.Code;
            sourceReturnType = arithmeticTranslated.SourceReturnType;
            usedTypedSourceFunction = true;
        }
        else
        {
            var expressionAttr = MapReturnTypeToSourceExpressionAttribute(expectedReturnType);
            code = NormalizeDateConstructorMappings(TranslateXpaExpressionToCSharp(trimmed, task, dataObjects, expressionAttr));
            code = NormalizeVarSetValueExpressions(code);
            sourceReturnType = ResolveSourceExpressionReturnType(trimmed, task, dataObjects, depth + 1);
            if (string.IsNullOrWhiteSpace(sourceReturnType) &&
                TryResolveTranslatedSourceBindingReturnType(code, task, out var translatedBindingReturnType))
                sourceReturnType = translatedBindingReturnType;
            if (string.IsNullOrWhiteSpace(sourceReturnType) &&
                TryReadRegisteredExpressionCallOrdinal(code, out var translatedExpressionOrdinal) &&
                task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(translatedExpressionOrdinal, out var translatedExpressionEntry) &&
                translatedExpressionEntry is not null)
            {
                sourceReturnType = ResolveIntrinsicReturnTypeForExpressionEntry(translatedExpressionEntry);
                if (string.IsNullOrWhiteSpace(sourceReturnType))
                    sourceReturnType = ResolveSourceReturnTypeForExpressionEntry(translatedExpressionEntry, task, dataObjects);
            }
        }

        if (string.Equals(NormalizeReturnTypeToken(expectedReturnType), "Text", StringComparison.Ordinal) &&
            string.Equals(NormalizeReturnTypeToken(sourceReturnType), "Text", StringComparison.Ordinal))
            code = UnwrapSourceTypedTextByteArrayToText(code);

        if (!usedTypedSourceFunction)
            code = StripRedundantOuterParentheses(code.Trim());

        if (string.IsNullOrWhiteSpace(expectedReturnType))
            return code.Trim();

        var expected = ExpectedTypeForReturnType(expectedReturnType);
        if (!string.IsNullOrWhiteSpace(sourceReturnType))
        {
            var emittedFromEvidence = EmitFromReliableTypeEvidence(
                code,
                sourceReturnType,
                expectedReturnType,
                "source-function-argument");
            if (!string.Equals(emittedFromEvidence.Trim(), code.Trim(), StringComparison.Ordinal))
                return emittedFromEvidence.Trim();
        }

        if (!string.IsNullOrWhiteSpace(sourceReturnType) &&
            TryEmitThroughStrictEmittedExpression(code, task, CreateExpectedEmissionContext(expected), out var strictArgument))
            return strictArgument.Trim();

        code = CoerceSourceTranslatedExpressionForContext(code, sourceReturnType, CreateExpectedEmissionContext(expected));
        code = ApplyExpectedTypeContext(code, task, expected);
        code = EmitExpressionForAttrObj(code, task, MapReturnTypeToAttrObj(expectedReturnType));
        code = NormalizeExpressionForExpectedScalarReturnTypeUsingResolvedTypeCentral(code, expectedReturnType, task);
        code = EnsureKnownFunctionScalarArgumentForReturnTypeCentral(code, expectedReturnType, task);
        code = StripRedundantOuterParentheses(code.Trim());
        code = MaterializeKnownFunctionArgumentForReturnType(code, expectedReturnType, task);
        return code.Trim();
    }

    private static bool IsColumnBaseReturnContract(string returnType)
    {
        var normalized = NormalizeReturnTypeToken(returnType);
        return string.Equals(normalized, "ColumnBase", StringComparison.Ordinal) ||
               string.Equals(normalized, "XPARuntimeCore.Box.Data.Advanced.ColumnBase", StringComparison.Ordinal) ||
               normalized.EndsWith(".ColumnBase", StringComparison.Ordinal);
    }

    private static bool TryTranslateSourceColumnContractArgument(
        string syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string code)
    {
        code = "";
        var decoded = StripRedundantOuterParentheses(WebUtility.HtmlDecode(syntax ?? "").Trim());
        if (string.IsNullOrWhiteSpace(decoded))
            return false;

        if (TryParseFunctionCall(decoded, out var functionName, out var args) &&
            args.Count > 0 &&
            IsRuntimeValueAccessorFunction(NormalizeXpaFunctionContractName(functionName)))
            decoded = StripRedundantOuterParentheses(args[0].Trim());

        decoded = DecodeSourceColumnContractToken(decoded);
        if (!IsSimpleIdentifierPath(decoded))
            return false;

        var binding = ResolveDirectSourceBindingCode(decoded, task, dataObjects, "");
        if (string.IsNullOrWhiteSpace(binding))
            return false;

        code = binding.Trim();
        return true;
    }

    private static string DecodeSourceColumnContractToken(string token)
    {
        var decoded = StripRedundantOuterParentheses(WebUtility.HtmlDecode(token ?? "").Trim());
        if (string.IsNullOrWhiteSpace(decoded) ||
            !TryReadXpaLiteral(decoded, 0, out var literalEnd, out var literalValue, out _) ||
            literalEnd + 1 >= decoded.Length)
            return decoded;

        var cursor = literalEnd + 1;
        while (cursor < decoded.Length && char.IsWhiteSpace(decoded[cursor]))
            cursor++;

        if (!TryReadLiteralPostfixToken(decoded, cursor, out var postfixToken, out var postfixEnd) ||
            !string.Equals(postfixToken, "VAR", StringComparison.OrdinalIgnoreCase))
            return decoded;

        cursor = postfixEnd + 1;
        while (cursor < decoded.Length && char.IsWhiteSpace(decoded[cursor]))
            cursor++;

        return cursor == decoded.Length ? literalValue.Trim() : decoded;
    }

    private static bool TryTranslateDirectSourceBindingFunctionArgument(
        string trimmed,
        string expectedReturnType,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        int depth,
        out string code)
    {
        code = "";
        var normalizedExpectedReturnType = NormalizeReturnTypeToken(expectedReturnType);
        if (string.IsNullOrWhiteSpace(trimmed) ||
            string.IsNullOrWhiteSpace(normalizedExpectedReturnType) ||
            depth > 32 ||
            !IsSimpleIdentifierPath(trimmed))
            return false;

        string sourceReturnType;
        string binding;
        if (TryResolveLocalOrdinalDotNetStringBinding(trimmed, task, out var localDotNetStringBinding))
        {
            sourceReturnType = "Text";
            binding = localDotNetStringBinding;
        }
        else
        {
            binding = ResolveDirectSourceBindingCode(trimmed, task, dataObjects, normalizedExpectedReturnType);
            if (!string.IsNullOrWhiteSpace(binding) &&
                TryResolveTranslatedSourceBindingReturnType(binding, task, out sourceReturnType))
            {
                sourceReturnType = NormalizeReturnTypeToken(sourceReturnType);
            }
            else
            {
                var expectedAttr = MapReturnTypeToSourceExpressionAttribute(normalizedExpectedReturnType);
                var expectedTranslated = NormalizeDateConstructorMappings(TranslateXpaExpressionToCSharp(trimmed, task, dataObjects, expectedAttr));
                expectedTranslated = NormalizeVarSetValueExpressions(expectedTranslated).Trim();
                if (!string.IsNullOrWhiteSpace(expectedTranslated) &&
                    !string.Equals(expectedTranslated, trimmed, StringComparison.OrdinalIgnoreCase) &&
                    TryResolveTranslatedSourceBindingReturnType(expectedTranslated, task, out var expectedTranslatedReturnType) &&
                    string.Equals(
                        GetValueReturnType(NormalizeReturnTypeToken(expectedTranslatedReturnType)),
                        GetValueReturnType(normalizedExpectedReturnType),
                        StringComparison.Ordinal))
                {
                    binding = expectedTranslated;
                    sourceReturnType = NormalizeReturnTypeToken(expectedTranslatedReturnType);
                }
                else
                {
                    if (!TryResolveDirectSourceBindingReturnType(trimmed, task, dataObjects, depth, out sourceReturnType))
                        return false;

                    binding = ResolveDirectSourceBindingCode(trimmed, task, dataObjects, sourceReturnType);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(binding))
            return false;

        code = binding.Trim();
        if (TryResolveTranslatedSourceBindingReturnType(code, task, out var bindingReturnType))
            sourceReturnType = bindingReturnType;

        if (SourceReturnTypeMatchesExpected(sourceReturnType, normalizedExpectedReturnType))
        {
            sourceReturnType = NormalizeReturnTypeToken(sourceReturnType);
            RegisterTypedExpressionReturnType(task, code, sourceReturnType);
            return true;
        }

        RegisterTypedExpressionReturnType(task, code, sourceReturnType);
        code = CoerceTypedExpressionCodeForExpectedType(code, sourceReturnType, normalizedExpectedReturnType);
        return !string.IsNullOrWhiteSpace(code);
    }

    private static bool SourceReturnTypeMatchesExpected(string sourceReturnType, string expectedReturnType)
    {
        var sourceXpaType = XpaTypeEngine.MapExpectedToXpaType(NormalizeReturnTypeToken(sourceReturnType));
        var expectedXpaType = XpaTypeEngine.MapExpectedToXpaType(NormalizeReturnTypeToken(expectedReturnType));
        return sourceXpaType != XpaType.Unknown &&
               expectedXpaType != XpaType.Unknown &&
               sourceXpaType == expectedXpaType;
    }

    private static bool SourceReturnTypeMatchesExpressionAttribute(string sourceReturnType, string? expressionAttribute)
    {
        var attributeReturnType = NormalizeReturnTypeToken(ResolveSimpleReturnTypeForExpressionAttribute(expressionAttribute));
        return !string.IsNullOrWhiteSpace(attributeReturnType) &&
               SourceReturnTypeMatchesExpected(sourceReturnType, attributeReturnType);
    }

    private static bool TryTranslateSourceLiteralForExpectedReturnType(
        string syntax,
        string expectedReturnType,
        out string code)
    {
        code = "";
        var normalizedExpected = NormalizeReturnTypeToken(expectedReturnType);
        if (string.IsNullOrWhiteSpace(normalizedExpected))
            return false;

        var trimmed = StripRedundantOuterParentheses(WebUtility.HtmlDecode(syntax ?? "").Trim());
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        if (string.Equals(normalizedExpected, "Date", StringComparison.Ordinal))
        {
            if (string.Equals(trimmed, "0", StringComparison.Ordinal) ||
                string.Equals(trimmed, "''", StringComparison.Ordinal) ||
                string.Equals(trimmed, "\"\"", StringComparison.Ordinal) ||
                string.Equals(trimmed, "00/00/0000", StringComparison.Ordinal) ||
                string.Equals(trimmed, "'00/00/0000'DATE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "\"00/00/0000\"DATE", StringComparison.OrdinalIgnoreCase) ||
                IsSourceEmptyDateConstructorLiteral(trimmed))
            {
                code = "XPARuntimeCore.Box.Date.Empty";
                return true;
            }
        }

        if (string.Equals(normalizedExpected, "Time", StringComparison.Ordinal))
        {
            if (string.Equals(trimmed, "0", StringComparison.Ordinal) ||
                string.Equals(trimmed, "''", StringComparison.Ordinal) ||
                string.Equals(trimmed, "\"\"", StringComparison.Ordinal))
            {
                code = "u.CastToTime(u.Null())";
                return true;
            }
        }

        if (string.Equals(normalizedExpected, "Text", StringComparison.Ordinal) &&
            TryReadXpaLiteral(trimmed, 0, out var literalEnd, out var literalValue, out _) &&
            literalEnd + 1 < trimmed.Length)
        {
            var cursor = literalEnd + 1;
            while (cursor < trimmed.Length && char.IsWhiteSpace(trimmed[cursor]))
                cursor++;

            if (TryReadLiteralPostfixToken(trimmed, cursor, out var token, out var tokenEnd) &&
                string.Equals(token, "RIGHT", StringComparison.OrdinalIgnoreCase))
            {
                cursor = tokenEnd + 1;
                while (cursor < trimmed.Length && char.IsWhiteSpace(trimmed[cursor]))
                    cursor++;

                if (cursor == trimmed.Length)
                {
                    code = ToCSharpLiteral(literalValue);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryResolveLocalOrdinalDotNetStringBinding(
        string token,
        TaskSemantic task,
        out string binding)
    {
        binding = "";
        var normalizedToken = token.Trim().ToUpperInvariant();
        if (!IsAlphabeticBindingToken(normalizedToken))
            return false;

        var slot = ToAlphabeticSlot(normalizedToken);
        var columnIndex = slot - 2;
        if (columnIndex <= 0 || columnIndex > task.ResourcesSemantic.Ordered.Count)
            return false;

        var localResource = task.ResourcesSemantic.Ordered[columnIndex - 1];
        if (!IsDotNetTaskResource(localResource))
            return false;

        var objectType = NormalizeDotNetObjectType(localResource.ObjectType ?? "");
        if (!string.Equals(objectType, "System.String", StringComparison.Ordinal) &&
            !string.Equals(objectType, "string", StringComparison.OrdinalIgnoreCase))
            return false;

        binding = ResolveTaskResourceMemberName(task, localResource);
        return !string.IsNullOrWhiteSpace(binding);
    }

    private static string ResolveDirectSourceBindingCode(
        string token,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        string sourceReturnType)
    {
        var normalizedToken = token.Trim().ToUpperInvariant();
        if (task.SelectsSemantic.ItemsByName.TryGetValue(normalizedToken, out var directSelect) &&
            directSelect is not null)
        {
            if (TryResolveDirectSourceSelectResourceBinding(
                    directSelect,
                    task,
                    sourceReturnType,
                    out var directResourceBinding))
                return directResourceBinding;
        }

        if (directSelect is not null &&
            TryResolveSourceSelectReturnType(directSelect, task, dataObjects, out var directSelectReturnType) &&
            string.Equals(
                NormalizeReturnTypeToken(directSelectReturnType),
                NormalizeReturnTypeToken(sourceReturnType),
                StringComparison.Ordinal))
        {
            var directSelectBinding = ResolveSelectExpression(directSelect, task, dataObjects, "");
            if (!string.IsNullOrWhiteSpace(directSelectBinding))
                return directSelectBinding;
        }

        if (TryResolveParentSourceResourceBinding(normalizedToken, task, out var parentBinding, out var parentReturnType) &&
            (string.IsNullOrWhiteSpace(sourceReturnType) ||
             string.Equals(
                 NormalizeReturnTypeToken(parentReturnType),
                 NormalizeReturnTypeToken(sourceReturnType),
                 StringComparison.Ordinal)))
            return parentBinding;

        if (TryResolveSingleLetterApplicationBindingForExpectedSourceType(
                normalizedToken,
                task,
                dataObjects,
                sourceReturnType,
                out var applicationBinding,
                out _))
            return applicationBinding;

        if (TryResolveTranslatedSourceBindingForToken(normalizedToken, task, dataObjects, out var translatedBinding, out var translatedReturnType) &&
            string.Equals(
                NormalizeReturnTypeToken(translatedReturnType),
                NormalizeReturnTypeToken(sourceReturnType),
                StringComparison.Ordinal))
            return translatedBinding;

        if (IsAlphabeticBindingToken(normalizedToken))
        {
            var slot = ToAlphabeticSlot(normalizedToken);
            var columnIndex = slot - 2;
            if (columnIndex > 0 && columnIndex <= task.ResourcesSemantic.Ordered.Count)
            {
                var localResource = task.ResourcesSemantic.Ordered[columnIndex - 1];
                if (TryMapSourceResourceReturnType(localResource, task, out var localReturnType) &&
                    string.Equals(
                        NormalizeReturnTypeToken(localReturnType),
                        NormalizeReturnTypeToken(sourceReturnType),
                        StringComparison.Ordinal))
                    return ResolveTaskResourceMemberName(task, localResource);
            }
        }

        var allTasks = _allTasks ?? Array.Empty<TaskSemantic>();
        var ordinalBinding = ResolveExpressionOrdinalBinding(token, task, allTasks, dataObjects);
        if (!string.IsNullOrWhiteSpace(ordinalBinding) &&
            (string.IsNullOrWhiteSpace(sourceReturnType) ||
             (TryResolveTranslatedSourceBindingReturnType(ordinalBinding, task, out var ordinalReturnType) &&
              string.Equals(
                  NormalizeReturnTypeToken(ordinalReturnType),
                  NormalizeReturnTypeToken(sourceReturnType),
                  StringComparison.Ordinal))))
            return ordinalBinding;

        if (task.ResourcesSemantic.ByName.TryGetValue(token, out var namedResource) &&
            namedResource is not null &&
            (string.IsNullOrWhiteSpace(sourceReturnType) ||
             (TryMapSourceResourceReturnType(namedResource, task, out var namedReturnType) &&
              string.Equals(
                  NormalizeReturnTypeToken(namedReturnType),
                  NormalizeReturnTypeToken(sourceReturnType),
                  StringComparison.Ordinal))))
            return ResolveTaskResourceMemberName(task, namedResource);

        if (task.ResourcesSemantic.ByLegacyName.TryGetValue(token, out var legacyResource) &&
            legacyResource is not null &&
            (string.IsNullOrWhiteSpace(sourceReturnType) ||
             (TryMapSourceResourceReturnType(legacyResource, task, out var legacyReturnType) &&
              string.Equals(
                  NormalizeReturnTypeToken(legacyReturnType),
                  NormalizeReturnTypeToken(sourceReturnType),
                  StringComparison.Ordinal))))
            return ResolveTaskResourceMemberName(task, legacyResource);

        return "";
    }

    private static bool TryResolveSingleLetterApplicationBindingForExpectedSourceType(
        string token,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        string expectedReturnType,
        out string binding,
        out string returnType)
    {
        binding = "";
        returnType = "";

        var normalizedToken = (token ?? "").Trim().ToUpperInvariant();
        var normalizedExpectedReturnType = NormalizeReturnTypeToken(expectedReturnType);
        if (normalizedToken.Length != 1 ||
            string.IsNullOrWhiteSpace(normalizedExpectedReturnType) ||
            !IsAlphabeticBindingToken(normalizedToken))
            return false;

        var allTasks = _allTasks ?? Array.Empty<TaskSemantic>();
        var slot = ToAlphabeticSlot(normalizedToken);
        var applicationBinding = ResolveApplicationOrdinalBinding(slot, allTasks);
        if (string.IsNullOrWhiteSpace(applicationBinding) ||
            !TryResolveBindingReturnTypeForExpectedSourceBinding(applicationBinding, task, out var applicationReturnType) ||
            !SourceReturnTypeMatchesExpected(applicationReturnType, normalizedExpectedReturnType))
            return false;

        if (TryResolveLocalOrParentBindingReturnTypeForSingleLetterToken(
                normalizedToken,
                task,
                dataObjects,
                out var localReturnType) &&
            SourceReturnTypeMatchesExpected(localReturnType, normalizedExpectedReturnType))
            return false;

        binding = applicationBinding.Trim();
        returnType = NormalizeReturnTypeToken(applicationReturnType);
        return true;
    }

    private static string ResolveApplicationDatabaseConfigComparisonExpectedReturnType(
        string ownSyntax,
        TaskSemantic task,
        string counterpartReturnType)
    {
        if (!SourceReturnTypeMatchesExpected(counterpartReturnType, "Text"))
            return "";

        return TryResolveApplicationDatabaseConfigSourceBinding(
            ownSyntax,
            task,
            "Text",
            out _,
            out _)
            ? "Text"
            : "";
    }

    private static bool TryResolveApplicationDatabaseConfigSourceBinding(
        string syntax,
        TaskSemantic task,
        string expectedReturnType,
        out string binding,
        out string returnType)
    {
        binding = "";
        returnType = "";

        var normalizedExpectedReturnType = NormalizeReturnTypeToken(expectedReturnType);
        if (!string.IsNullOrWhiteSpace(normalizedExpectedReturnType) &&
            !SourceReturnTypeMatchesExpected(normalizedExpectedReturnType, "Text"))
            return false;

        var normalizedToken = StripRedundantOuterParentheses(WebUtility.HtmlDecode(syntax ?? "").Trim()).ToUpperInvariant();
        if (normalizedToken.Length != 1 ||
            !IsAlphabeticBindingToken(normalizedToken))
            return false;

        var allTasks = _allTasks ?? Array.Empty<TaskSemantic>();
        var appTask = allTasks.FirstOrDefault(t => t.MainProgram) ?? allTasks.FirstOrDefault(t => t.ParentOrdinal is null);
        if (appTask is null)
            return false;

        var slot = ToAlphabeticSlot(normalizedToken);
        if (slot <= 0 || slot > appTask.ResourcesSemantic.Ordered.Count)
            return false;

        var resource = appTask.ResourcesSemantic.Ordered[slot - 1];
        var memberName = ResolveTaskResourceMemberName(appTask, resource);
        if (!IsApplicationDatabaseConfigResource(resource, memberName))
            return false;

        var applicationBinding = $"Application.Instance.{memberName}";
        if (!TryResolveBindingReturnTypeForExpectedSourceBinding(applicationBinding, task, out var applicationReturnType) ||
            !SourceReturnTypeMatchesExpected(applicationReturnType, "Text"))
            return false;

        binding = applicationBinding;
        returnType = NormalizeReturnTypeToken(applicationReturnType);
        return true;
    }

    private static bool IsApplicationDatabaseConfigResource(TaskResourceColumnDef resource, string memberName)
    {
        var name = (resource.Name ?? "").Trim();
        var member = (memberName ?? "").Trim();
        return LooksLikeDatabaseConfig549Name(name) || LooksLikeDatabaseConfig549Name(member);
    }

    private static bool LooksLikeDatabaseConfig549Name(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var normalized = name.Replace("_", "", StringComparison.Ordinal).ToUpperInvariant();
        return normalized.Contains("549", StringComparison.Ordinal) &&
               (normalized.Contains("BANCO", StringComparison.Ordinal) ||
                normalized.Contains("DATABASE", StringComparison.Ordinal) ||
                normalized.Contains("DB", StringComparison.Ordinal));
    }

    private static bool TryResolveBindingReturnTypeForExpectedSourceBinding(
        string binding,
        TaskSemantic task,
        out string returnType)
    {
        returnType = "";
        var normalizedBinding = (binding ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalizedBinding))
            return false;

        if (TryResolveTranslatedSourceBindingReturnType(normalizedBinding, task, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        return TryResolveSimpleSourceReturnTypeFromResourcePath(task, normalizedBinding, out returnType) &&
               !string.IsNullOrWhiteSpace(returnType);
    }

    private static bool TryResolveLocalOrParentBindingReturnTypeForSingleLetterToken(
        string normalizedToken,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(normalizedToken) ||
            normalizedToken.Length != 1 ||
            !IsAlphabeticBindingToken(normalizedToken))
            return false;

        if (task.SelectsSemantic.ItemsByName.TryGetValue(normalizedToken, out var directSelect) &&
            directSelect is not null &&
            TryResolveSourceSelectReturnType(directSelect, task, dataObjects, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        var slot = ToAlphabeticSlot(normalizedToken);
        var columnIndex = slot - 2;
        if (columnIndex > 0 &&
            columnIndex <= task.ResourcesSemantic.Ordered.Count &&
            TryMapSourceResourceReturnType(task.ResourcesSemantic.Ordered[columnIndex - 1], task, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        if (TryResolveParentSourceResourceBinding(normalizedToken, task, out _, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        if (task.ResourcesSemantic.ByName.TryGetValue(normalizedToken, out var namedResource) &&
            namedResource is not null &&
            TryMapSourceResourceReturnType(namedResource, task, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        if (task.ResourcesSemantic.ByLegacyName.TryGetValue(normalizedToken, out var legacyResource) &&
            legacyResource is not null &&
            TryMapSourceResourceReturnType(legacyResource, task, out returnType) &&
            !string.IsNullOrWhiteSpace(returnType))
            return true;

        var ordinalBinding = ResolveExpressionOrdinalBinding(normalizedToken, task, _allTasks ?? Array.Empty<TaskSemantic>(), dataObjects);
        return !string.IsNullOrWhiteSpace(ordinalBinding) &&
               !ordinalBinding.StartsWith("Application.Instance.", StringComparison.Ordinal) &&
               TryResolveBindingReturnTypeForExpectedSourceBinding(ordinalBinding, task, out returnType) &&
               !string.IsNullOrWhiteSpace(returnType);
    }

    private static bool TryResolveDirectSourceSelectResourceBinding(
        TaskLogicSelectDef select,
        TaskSemantic task,
        string sourceReturnType,
        out string binding)
    {
        binding = "";
        if (string.Equals(select.Type, "R", StringComparison.OrdinalIgnoreCase))
            return false;

        var resource = ResolveTaskResourceColumn(task, select.ColumnId);
        if (resource is null ||
            !TryMapSourceResourceReturnType(resource, task, out var resourceReturnType) ||
            !string.Equals(
                NormalizeReturnTypeToken(resourceReturnType),
                NormalizeReturnTypeToken(sourceReturnType),
                StringComparison.Ordinal))
            return false;

        binding = ResolveTaskResourceMemberName(task, resource);
        return !string.IsNullOrWhiteSpace(binding);
    }

    private static bool TryTranslateSourceArithmeticExpression(
        string syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        int depth,
        out SourceTranslatedExpression translated,
        string expectedReturnType = "",
        bool preferApplicationDatabaseBinding = false)
    {
        translated = new("", "");
        if (string.IsNullOrWhiteSpace(syntax) || depth > 24)
            return false;

        syntax = StripRedundantOuterParentheses(CompleteSourceGrouping(syntax.Trim()));
        var arithmetic = TrySplitTopLevelSourceArithmeticExpression(syntax);
        if (arithmetic is null)
            return false;

        var leftSourceType = NormalizeReturnTypeToken(ResolveSourceExpressionReturnType(arithmetic.Value.Left, task, dataObjects, depth + 1));
        var rightSourceType = NormalizeReturnTypeToken(ResolveSourceExpressionReturnType(arithmetic.Value.Right, task, dataObjects, depth + 1));
        var originalLeftSourceType = leftSourceType;
        var originalRightSourceType = rightSourceType;
        var normalizedExpectedReturnType = NormalizeReturnTypeToken(expectedReturnType);
        var materializeTimeDifferenceAsNumber =
            string.Equals(arithmetic.Value.Operator, "-", StringComparison.Ordinal) &&
            string.Equals(normalizedExpectedReturnType, "Number", StringComparison.Ordinal) &&
            string.Equals(leftSourceType, "Time", StringComparison.Ordinal) &&
            string.Equals(rightSourceType, "Time", StringComparison.Ordinal);
        if (string.Equals(arithmetic.Value.Operator, "&", StringComparison.Ordinal))
        {
            leftSourceType = "Text";
            rightSourceType = "Text";
        }
        else if (string.Equals(normalizedExpectedReturnType, "Date", StringComparison.Ordinal) &&
                 arithmetic.Value.Operator is "+" or "-")
        {
            var leftHasNumberEvidence = string.Equals(leftSourceType, "Number", StringComparison.Ordinal) ||
                                        IsNumericLiteralExpressionCentral(arithmetic.Value.Left);
            var rightHasNumberEvidence = string.Equals(rightSourceType, "Number", StringComparison.Ordinal) ||
                                         IsNumericLiteralExpressionCentral(arithmetic.Value.Right);
            if (rightHasNumberEvidence)
            {
                leftSourceType = "Date";
                rightSourceType = "Number";
            }
            else if (string.Equals(arithmetic.Value.Operator, "+", StringComparison.Ordinal) &&
                     leftHasNumberEvidence)
            {
                leftSourceType = "Number";
                rightSourceType = "Date";
            }
        }
        else if (string.Equals(normalizedExpectedReturnType, "Time", StringComparison.Ordinal) &&
                 arithmetic.Value.Operator is "+" or "-")
        {
            var leftHasNumberEvidence = string.Equals(leftSourceType, "Number", StringComparison.Ordinal) ||
                                        IsNumericLiteralExpressionCentral(arithmetic.Value.Left);
            var rightHasNumberEvidence = string.Equals(rightSourceType, "Number", StringComparison.Ordinal) ||
                                         IsNumericLiteralExpressionCentral(arithmetic.Value.Right);
            var leftHasTimeEvidence = string.Equals(leftSourceType, "Time", StringComparison.Ordinal);
            var rightHasTimeEvidence = string.Equals(rightSourceType, "Time", StringComparison.Ordinal);
            if (leftHasTimeEvidence && rightHasNumberEvidence)
            {
                leftSourceType = "Time";
                rightSourceType = "Number";
            }
            else if (string.Equals(arithmetic.Value.Operator, "+", StringComparison.Ordinal) &&
                     rightHasTimeEvidence &&
                     leftHasNumberEvidence)
            {
                leftSourceType = "Number";
                rightSourceType = "Time";
            }
            else if (rightHasNumberEvidence && string.IsNullOrWhiteSpace(leftSourceType))
            {
                leftSourceType = "Time";
                rightSourceType = "Number";
            }
            else if (string.Equals(arithmetic.Value.Operator, "+", StringComparison.Ordinal) &&
                     leftHasNumberEvidence &&
                     string.IsNullOrWhiteSpace(rightSourceType))
            {
                leftSourceType = "Number";
                rightSourceType = "Time";
            }
            else if (leftHasNumberEvidence && !rightHasTimeEvidence)
            {
                leftSourceType = "Number";
                rightSourceType = "Number";
            }
            else if (leftHasNumberEvidence && string.IsNullOrWhiteSpace(rightSourceType))
            {
                leftSourceType = "Number";
                rightSourceType = "Number";
            }
            else if (leftHasNumberEvidence && rightHasNumberEvidence)
            {
                leftSourceType = "Number";
                rightSourceType = "Number";
            }
        }
        else if (materializeTimeDifferenceAsNumber)
        {
            leftSourceType = "Time";
            rightSourceType = "Time";
        }
        else if (string.Equals(normalizedExpectedReturnType, "Number", StringComparison.Ordinal) ||
            arithmetic.Value.Operator is "*" or "/" or "%" or "^")
        {
            leftSourceType = "Number";
            rightSourceType = "Number";
        }
        else if (IsReliableNumericSourceArithmetic(arithmetic.Value.Operator, leftSourceType, rightSourceType))
        {
            leftSourceType = "Number";
            rightSourceType = "Number";
        }
        if (ShouldPreferDateDifferenceRightOperand(arithmetic.Value.Operator, arithmetic.Value.Right, leftSourceType, rightSourceType, normalizedExpectedReturnType))
            rightSourceType = "Date";
        var leftOperand = TranslateSourceFunctionArgumentExpression(arithmetic.Value.Left, leftSourceType, task, dataObjects, depth + 1, preferApplicationDatabaseBinding);
        var rightOperand = TranslateSourceFunctionArgumentExpression(arithmetic.Value.Right, rightSourceType, task, dataObjects, depth + 1, preferApplicationDatabaseBinding);
        var leftCode = leftOperand.Code;
        var rightCode = rightOperand.Code;
        if (string.IsNullOrWhiteSpace(leftCode) || string.IsNullOrWhiteSpace(rightCode))
            return false;

        if (string.IsNullOrWhiteSpace(originalLeftSourceType))
            originalLeftSourceType = NormalizeReturnTypeToken(leftOperand.SourceReturnType);
        if (string.IsNullOrWhiteSpace(originalRightSourceType))
            originalRightSourceType = NormalizeReturnTypeToken(rightOperand.SourceReturnType);
        if (string.IsNullOrWhiteSpace(leftSourceType))
            leftSourceType = NormalizeReturnTypeToken(leftOperand.PreferredReturnType);
        if (string.IsNullOrWhiteSpace(rightSourceType))
            rightSourceType = NormalizeReturnTypeToken(rightOperand.PreferredReturnType);

        if (ShouldMaterializeSourceArithmeticOperandsAsNumber(arithmetic.Value.Operator, normalizedExpectedReturnType))
        {
            if (SourceReturnTypeMatchesExpected(leftSourceType, "Number") &&
                (TryGetRegisteredExpressionSignatureReturnType(arithmetic.Value.Left, task, out var leftRegisteredReturnType) ||
                 TryGetRegisteredExpressionSignatureReturnType(leftCode, task, out leftRegisteredReturnType)) &&
                !SourceReturnTypeMatchesExpected(leftRegisteredReturnType, "Number"))
                leftCode = EmitFromReliableTypeEvidence(leftCode, leftRegisteredReturnType, "Number", "registered-expression-operand");
            if (SourceReturnTypeMatchesExpected(rightSourceType, "Number") &&
                (TryGetRegisteredExpressionSignatureReturnType(arithmetic.Value.Right, task, out var rightRegisteredReturnType) ||
                 TryGetRegisteredExpressionSignatureReturnType(rightCode, task, out rightRegisteredReturnType)) &&
                !SourceReturnTypeMatchesExpected(rightRegisteredReturnType, "Number"))
                rightCode = EmitFromReliableTypeEvidence(rightCode, rightRegisteredReturnType, "Number", "registered-expression-operand");
            if (SourceReturnTypeMatchesExpected(leftSourceType, "Number") &&
                !SourceReturnTypeMatchesExpected(originalLeftSourceType, "Number"))
                leftCode = EmitFromReliableTypeEvidence(leftCode, originalLeftSourceType, "Number", "source-arithmetic-operand");
            if (SourceReturnTypeMatchesExpected(rightSourceType, "Number") &&
                !SourceReturnTypeMatchesExpected(originalRightSourceType, "Number"))
                rightCode = EmitFromReliableTypeEvidence(rightCode, originalRightSourceType, "Number", "source-arithmetic-operand");
        }

        if (materializeTimeDifferenceAsNumber)
        {
            leftCode = EmitFromReliableTypeEvidence(leftCode, "Time", "Number", "source-arithmetic");
            rightCode = EmitFromReliableTypeEvidence(rightCode, "Time", "Number", "source-arithmetic");
        }

        var sourceType = materializeTimeDifferenceAsNumber
            ? "Number"
            : ResolveSourceExpressionReturnType(syntax, task, dataObjects, depth + 1);
        if (ShouldMaterializeSourceArithmeticOperandsAsNumber(arithmetic.Value.Operator, normalizedExpectedReturnType))
            sourceType = "Number";
        if (string.IsNullOrWhiteSpace(sourceType) &&
            (string.Equals(normalizedExpectedReturnType, "Number", StringComparison.Ordinal) ||
             arithmetic.Value.Operator is "*" or "/" or "%" or "^"))
            sourceType = "Number";
        if (string.IsNullOrWhiteSpace(sourceType) &&
            string.Equals(normalizedExpectedReturnType, "Time", StringComparison.Ordinal) &&
            arithmetic.Value.Operator is "+" or "-" &&
            IsNumericLikeSourceType(leftSourceType) &&
            IsNumericLikeSourceType(rightSourceType) &&
            !string.Equals(originalLeftSourceType, "Time", StringComparison.Ordinal) &&
            !string.Equals(originalRightSourceType, "Time", StringComparison.Ordinal))
            sourceType = "Number";
        if (ShouldResolveArithmeticAsDateDifference(arithmetic.Value.Operator, leftSourceType, rightSourceType, normalizedExpectedReturnType))
            sourceType = "Number";
        if (string.Equals(arithmetic.Value.Operator, "&", StringComparison.Ordinal))
            sourceType = "Text";
        string code;
        if (string.Equals(arithmetic.Value.Operator, "^", StringComparison.Ordinal))
        {
            code = $"u.Pow({leftCode}, {rightCode})";
            sourceType = "Number";
        }
        else
        {
            var codeOperator = string.Equals(arithmetic.Value.Operator, "&", StringComparison.Ordinal) ? "+" : arithmetic.Value.Operator;
            leftCode = PreserveSourceArithmeticOperandGrouping(leftCode, arithmetic.Value.Left, arithmetic.Value.Operator, isRightOperand: false);
            rightCode = PreserveSourceArithmeticOperandGrouping(rightCode, arithmetic.Value.Right, arithmetic.Value.Operator, isRightOperand: true);
            code = $"{leftCode} {codeOperator} {rightCode}";
        }
        if (ShouldMaterializeDateArithmeticResult(arithmetic.Value.Operator, leftSourceType, rightSourceType, normalizedExpectedReturnType))
        {
            code = EmitFromReliableTypeEvidence(code, "Number", "Date", "source-arithmetic");
            sourceType = "Date";
        }
        else if (ShouldMaterializeTimeArithmeticResult(arithmetic.Value.Operator, leftSourceType, rightSourceType, normalizedExpectedReturnType))
        {
            if (!SourceReturnTypeMatchesExpected(sourceType, "Time"))
                code = EmitFromReliableTypeEvidence(code, "Number", "Time", "source-arithmetic");
            sourceType = "Time";
        }
        else if (string.Equals(normalizedExpectedReturnType, "Time", StringComparison.Ordinal) &&
                 SourceReturnTypeMatchesExpected(sourceType, "Number"))
        {
            code = EmitFromReliableTypeEvidence(code, "Number", "Time", "source-arithmetic");
            sourceType = "Time";
        }

        translated = new(code, sourceType, syntax, 0, "source-arithmetic");
        return true;
    }

    private static string PreserveSourceArithmeticOperandGrouping(
        string code,
        string sourceOperand,
        string parentOperator,
        bool isRightOperand)
    {
        var trimmedCode = code.Trim();
        if (string.IsNullOrWhiteSpace(trimmedCode))
            return trimmedCode;

        var child = TrySplitTopLevelSourceArithmeticExpression(StripRedundantOuterParentheses(sourceOperand.Trim()));
        if (child is null)
            return trimmedCode;

        var parentPrecedence = SourceArithmeticOperatorPrecedence(parentOperator);
        var childPrecedence = SourceArithmeticOperatorPrecedence(child.Value.Operator);
        if (childPrecedence < parentPrecedence ||
            (isRightOperand &&
             childPrecedence == parentPrecedence &&
             parentOperator is "-" or "/" or "%"))
            return $"({trimmedCode})";

        return trimmedCode;
    }

    private static int SourceArithmeticOperatorPrecedence(string op)
        => op switch
        {
            "^" => 3,
            "*" or "/" or "%" => 2,
            "+" or "-" or "&" => 1,
            _ => 0
        };

    private static bool ShouldMaterializeSourceArithmeticOperandsAsNumber(string op, string expectedReturnType)
        => op is "*" or "/" or "%" or "^" ||
           string.Equals(NormalizeReturnTypeToken(expectedReturnType), "Number", StringComparison.Ordinal);

    private static bool ShouldMaterializeDateArithmeticResult(
        string op,
        string leftSourceType,
        string rightSourceType,
        string expectedReturnType)
    {
        if (op is not "+" and not "-" ||
            !string.Equals(NormalizeReturnTypeToken(expectedReturnType), "Date", StringComparison.Ordinal))
            return false;

        var left = NormalizeReturnTypeToken(leftSourceType);
        var right = NormalizeReturnTypeToken(rightSourceType);
        return (string.Equals(left, "Date", StringComparison.Ordinal) && IsNumericLikeSourceType(right)) ||
               (string.Equals(right, "Date", StringComparison.Ordinal) && IsNumericLikeSourceType(left));
    }

    private static bool ShouldMaterializeTimeArithmeticResult(
        string op,
        string leftSourceType,
        string rightSourceType,
        string expectedReturnType)
    {
        if (op is not "+" and not "-" ||
            !string.Equals(NormalizeReturnTypeToken(expectedReturnType), "Time", StringComparison.Ordinal))
            return false;

        var left = NormalizeReturnTypeToken(leftSourceType);
        var right = NormalizeReturnTypeToken(rightSourceType);
        return (string.Equals(left, "Time", StringComparison.Ordinal) && IsNumericLikeSourceType(right)) ||
               (string.Equals(right, "Time", StringComparison.Ordinal) && IsNumericLikeSourceType(left));
    }

    private static bool IsNumericLikeSourceType(string returnType)
    {
        var normalized = NormalizeReturnTypeToken(returnType);
        return string.IsNullOrWhiteSpace(normalized) ||
               string.Equals(normalized, "Number", StringComparison.Ordinal) ||
               string.Equals(normalized, "object", StringComparison.Ordinal);
    }

    private static bool ShouldPreferDateDifferenceRightOperand(
        string op,
        string rightSyntax,
        string leftSourceType,
        string rightSourceType,
        string expectedReturnType)
    {
        if (!string.Equals(op, "-", StringComparison.Ordinal) ||
            !string.Equals(NormalizeReturnTypeToken(leftSourceType), "Date", StringComparison.Ordinal) ||
            !string.Equals(NormalizeReturnTypeToken(expectedReturnType), "Number", StringComparison.Ordinal))
            return false;

        if (IsNumericLiteralExpressionCentral(rightSyntax))
            return false;

        var normalizedRightType = NormalizeReturnTypeToken(rightSourceType);
        return string.IsNullOrWhiteSpace(normalizedRightType) ||
               string.Equals(normalizedRightType, "object", StringComparison.Ordinal) ||
               string.Equals(normalizedRightType, "Date", StringComparison.Ordinal);
    }

    private static bool ShouldResolveArithmeticAsDateDifference(
        string op,
        string leftSourceType,
        string rightSourceType,
        string expectedReturnType)
        => string.Equals(op, "-", StringComparison.Ordinal) &&
           string.Equals(NormalizeReturnTypeToken(leftSourceType), "Date", StringComparison.Ordinal) &&
           string.Equals(NormalizeReturnTypeToken(rightSourceType), "Date", StringComparison.Ordinal) &&
           string.Equals(NormalizeReturnTypeToken(expectedReturnType), "Number", StringComparison.Ordinal);

    private static string UnwrapSourceTypedTextByteArrayToText(string code)
    {
        var trimmed = code.Trim();
        if (!TryParseFunctionCall(trimmed, out var functionName, out var args) ||
            args.Count != 1 ||
            !IsTopLevelCall(functionName, "u.ByteArrayToText"))
            return code;

        return args[0].Trim();
    }

    private static string? MapReturnTypeToSourceExpressionAttribute(string returnType)
    {
        var normalized = GetValueReturnType(NormalizeReturnTypeToken(returnType));
        return normalized switch
        {
            "Text" => "A",
            "Number" => "N",
            "Date" => "D",
            "Time" => "T",
            "Bool" => "B",
            "byte[]" => "O",
            "byte[][]" => "O",
            _ => null
        };
    }

    private static bool TryTranslateSourceClrScalarOperand(
        string syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string code)
    {
        code = "";
        var trimmed = StripRedundantOuterParentheses(WebUtility.HtmlDecode(syntax ?? "").Trim());
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        if (TryResolveSourceDotNetEnumMemberReturnType(trimmed, out _))
        {
            code = StripDotNetQualifierOutsideQuotes(trimmed);
            return true;
        }

        if (TryTranslateExternalStaticDotNetMethodCall(trimmed, task, dataObjects, out code))
            return true;

        if (TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            TryResolveSourceDotNetMethodCallReturnType(functionName, args.Count, task, dataObjects, out _) &&
            TryResolveSourceDotNetMemberAccess(functionName, task, dataObjects, out var translatedMemberAccess, out _, out _))
        {
            var translatedArgs = args
                .Select(arg => StripDotNetQualifierOutsideQuotes(NormalizeDateConstructorMappings(TranslateXpaExpressionToCSharp(arg.Trim(), task, dataObjects))))
                .Select(arg => NormalizeVarSetValueExpressions(arg).Trim());
            code = $"{translatedMemberAccess}({string.Join(", ", translatedArgs)})";
            return true;
        }

        return false;
    }

    private static bool TryTranslateExternalStaticDotNetMethodCall(
        string syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string code)
    {
        code = "";
        if (!TryParseFunctionCall(syntax, out var functionName, out var args))
            return false;

        var normalizedFunctionName = StripDotNetQualifierOutsideQuotes(functionName).Trim();
        const string qrCodeType = "Cigam.Utils.BarCode.QRCode";
        const string qrCodePrefix = qrCodeType + ".";
        if (!normalizedFunctionName.StartsWith(qrCodePrefix, StringComparison.Ordinal))
            return false;

        var methodName = normalizedFunctionName[qrCodePrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(methodName) || !IsSimpleIdentifierPath(methodName))
            return false;

        var translatedArgs = args
            .Select(arg => StripDotNetQualifierOutsideQuotes(NormalizeDateConstructorMappings(TranslateXpaExpressionToCSharp(arg.Trim(), task, dataObjects))))
            .Select(arg => NormalizeVarSetValueExpressions(arg).Trim())
            .ToArray();
        var argumentList = translatedArgs.Length == 0
            ? ""
            : ", " + string.Join(", ", translatedArgs);
        code = $"ExternalTypeCompat.InvokeStatic(\"{Escape(qrCodeType)}\", \"{Escape(methodName)}\"{argumentList})";
        return true;
    }

    private static bool TryTranslateSourceBooleanExpression(
        string? syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string code,
        string? groupingSyntax = null,
        bool preferApplicationDatabaseBinding = false)
    {
        code = "";
        if (string.IsNullOrWhiteSpace(syntax))
            return false;

        var decoded = StripRedundantOuterParentheses(WebUtility.HtmlDecode(syntax).Trim());
        decoded = RepairMalformedBooleanFunctionOperatorGlueOutsideQuotes(decoded);
        decoded = RepairGluedSourceBooleanOperatorIdentifierBindings(decoded, task, dataObjects);
        var preferAndSplit = HasParenthesizedOrBeforeTopLevelAnd(WebUtility.HtmlDecode(groupingSyntax ?? syntax));
        if (!TrySplitTopLevelXpaBooleanBinaryExpression(decoded, out var left, out var op, out var right, preferAndSplit))
        {
            if (TryTranslateSourceLeadingNotExpression(decoded, task, dataObjects, out code, preferApplicationDatabaseBinding))
                return true;

            if (TryTranslateSourceComparisonExpression(decoded, task, dataObjects, out code, preferApplicationDatabaseBinding))
                return true;

            if (TryTranslateWholeKnownSourceFunctionCall(decoded, task, dataObjects, out var sourceFunctionTranslated, "Bool", preferApplicationDatabaseBinding: preferApplicationDatabaseBinding) &&
                string.Equals(NormalizeReturnTypeToken(sourceFunctionTranslated.SourceReturnType), "Bool", StringComparison.Ordinal))
            {
                code = NormalizeStatementBooleanConditionSyntax(sourceFunctionTranslated.Code);
                return true;
            }

            return false;
        }

        var leftCode = TranslateSourceBooleanOperand(left, task, dataObjects, preferApplicationDatabaseBinding);
        var rightCode = TranslateSourceBooleanOperand(right, task, dataObjects, preferApplicationDatabaseBinding);
        if (string.IsNullOrWhiteSpace(leftCode) || string.IsNullOrWhiteSpace(rightCode))
            return false;

        code = FormatBooleanConditionSyntax($"{leftCode} {op} {rightCode}");
        return true;
    }

    private static string RepairGluedSourceBooleanOperatorIdentifierBindings(
        string expression,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(expression) ||
            (expression.IndexOf("AND", StringComparison.OrdinalIgnoreCase) < 0 &&
             expression.IndexOf("OR", StringComparison.OrdinalIgnoreCase) < 0))
            return expression;

        StringBuilder? sb = null;
        var lastAppend = 0;
        for (var i = 0; i < expression.Length;)
        {
            if (IsQuotedSegmentStart(expression, i))
            {
                if (!TryReadQuotedSegmentEnd(expression, i, out var quoteEnd))
                    break;
                i = quoteEnd + 1;
                continue;
            }

            if (!TryReadIdentifierToken(expression, i, out var tokenEnd))
            {
                i++;
                continue;
            }

            var token = expression[i..tokenEnd];
            if (TrySplitGluedSourceBooleanOperatorIdentifierToken(expression, i, token, task, dataObjects, out var logicalOperator, out var suffix))
            {
                sb ??= new StringBuilder(expression.Length + 8);
                sb.Append(expression, lastAppend, i - lastAppend);
                sb.Append(logicalOperator);
                sb.Append(' ');
                sb.Append(suffix);
                lastAppend = tokenEnd;
            }

            i = tokenEnd;
        }

        if (sb is null)
            return expression;

        sb.Append(expression, lastAppend, expression.Length - lastAppend);
        return sb.ToString();
    }

    private static bool TrySplitGluedSourceBooleanOperatorIdentifierToken(
        string expression,
        int tokenStart,
        string token,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string logicalOperator,
        out string suffix)
    {
        logicalOperator = "";
        suffix = "";
        if (!HasLikelyOperandBefore(expression, tokenStart))
            return false;

        if (TryResolveTranslatedSourceBindingForToken(token, task, dataObjects, out _, out _))
            return false;

        if (TrySplitGluedSourceBooleanOperatorIdentifierToken(token, "AND", task, dataObjects, out suffix))
        {
            logicalOperator = "AND";
            return true;
        }

        if (TrySplitGluedSourceBooleanOperatorIdentifierToken(token, "OR", task, dataObjects, out suffix))
        {
            logicalOperator = "OR";
            return true;
        }

        return false;
    }

    private static bool TrySplitGluedSourceBooleanOperatorIdentifierToken(
        string token,
        string logicalOperator,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string suffix)
    {
        suffix = "";
        if (token.Length <= logicalOperator.Length ||
            !token.StartsWith(logicalOperator, StringComparison.OrdinalIgnoreCase))
            return false;

        suffix = token[logicalOperator.Length..];
        return IsAlphabeticBindingToken(suffix) &&
               TryResolveTranslatedSourceBindingForToken(suffix, task, dataObjects, out _, out _);
    }

    private static bool TryTranslateSourceLeadingNotExpression(
        string? syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string code,
        bool preferApplicationDatabaseBinding = false)
    {
        code = "";
        if (!TryTakeSourceLeadingNotOperand(syntax, out var operand))
            return false;

        if (TryTranslateSourceBooleanExpression(operand, task, dataObjects, out var operandCode, operand, preferApplicationDatabaseBinding))
        {
            code = $"u.Not({operandCode.Trim()})";
            return true;
        }

        if (TryTranslateWholeKnownSourceFunctionCall(operand, task, dataObjects, out var sourceFunctionTranslated, "Bool", preferApplicationDatabaseBinding: preferApplicationDatabaseBinding))
        {
            var sourceFunctionCode = sourceFunctionTranslated.Code.Trim();
            if (!string.IsNullOrWhiteSpace(sourceFunctionTranslated.SourceReturnType) &&
                !SourceReturnTypeMatchesExpected(sourceFunctionTranslated.SourceReturnType, "Bool"))
            {
                sourceFunctionCode = EmitFromReliableTypeEvidence(
                    sourceFunctionCode,
                    sourceFunctionTranslated.SourceReturnType,
                    "Bool",
                    "source-not-operand");
            }

            code = $"u.Not({sourceFunctionCode})";
            return true;
        }

        if (TryResolveTranslatedSourceBindingForToken(operand, task, dataObjects, out var binding, out var bindingReturnType))
        {
            var bindingCode = binding.Trim();
            if (!string.IsNullOrWhiteSpace(bindingReturnType) &&
                !SourceReturnTypeMatchesExpected(bindingReturnType, "Bool"))
            {
                bindingCode = EmitFromReliableTypeEvidence(
                    bindingCode,
                    bindingReturnType,
                    "Bool",
                    "source-not-operand");
            }

            code = $"u.Not({bindingCode})";
            return true;
        }

        return false;
    }

    private static bool TryTakeSourceLeadingNotOperand(string? syntax, out string operand)
    {
        operand = "";
        var trimmed = StripRedundantOuterParentheses(CompleteSourceGrouping(WebUtility.HtmlDecode(syntax ?? "").Trim()));
        if (string.IsNullOrWhiteSpace(trimmed) ||
            !IsTopLevelXpaBooleanWordAt(trimmed, 0, "NOT"))
            return false;

        operand = StripRedundantOuterParentheses(trimmed[3..].Trim());
        return !string.IsNullOrWhiteSpace(operand);
    }

    private static string TranslateSourceBooleanOperand(
        string syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        bool preferApplicationDatabaseBinding = false)
    {
        var trimmed = StripRedundantOuterParentheses(syntax.Trim());
        if (TryTranslateSourceBooleanExpression(trimmed, task, dataObjects, out var nested, preferApplicationDatabaseBinding: preferApplicationDatabaseBinding))
            return nested;

        if (TryTranslateSourceComparisonExpression(trimmed, task, dataObjects, out var comparisonCode, preferApplicationDatabaseBinding))
            return comparisonCode;

        if (TryTranslateWholeKnownSourceFunctionCall(trimmed, task, dataObjects, out var sourceFunctionTranslated, "Bool", preferApplicationDatabaseBinding: preferApplicationDatabaseBinding))
            return string.Equals(NormalizeReturnTypeToken(sourceFunctionTranslated.SourceReturnType), "Bool", StringComparison.Ordinal)
                ? NormalizeStatementBooleanConditionSyntax(sourceFunctionTranslated.Code)
                : RewriteBooleanOperand(task, sourceFunctionTranslated.Code);

        var translated = NormalizeDateConstructorMappings(TranslateXpaExpressionToCSharp(trimmed, task, dataObjects, "B"));
        translated = NormalizeVarSetValueExpressions(translated);
        translated = StripRedundantOuterParentheses(translated.Trim());
        return RewriteBooleanOperand(task, translated);
    }

    private static bool TryTranslateSourceComparisonExpression(
        string syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string code,
        bool preferApplicationDatabaseBinding = false,
        string? operandExpectedReturnTypeHint = null)
    {
        code = "";
        if (!TrySplitTopLevelXpaComparisonExpression(syntax, out var left, out var comparisonOperator, out var right))
            return false;

        var leftType = NormalizeReturnTypeToken(ResolveSourceExpressionReturnType(left, task, dataObjects, 0));
        var rightType = NormalizeReturnTypeToken(ResolveSourceExpressionReturnType(right, task, dataObjects, 0));
        if (TryResolveTranslatedSourceBindingForToken(left.Trim(), task, dataObjects, out _, out var translatedLeftType))
        {
            var normalizedTranslatedLeftType = NormalizeReturnTypeToken(translatedLeftType);
            if (!string.IsNullOrWhiteSpace(normalizedTranslatedLeftType))
                leftType = normalizedTranslatedLeftType;
        }
        if (TryResolveTranslatedSourceBindingForToken(right.Trim(), task, dataObjects, out _, out var translatedRightType))
        {
            var normalizedTranslatedRightType = NormalizeReturnTypeToken(translatedRightType);
            if (!string.IsNullOrWhiteSpace(normalizedTranslatedRightType))
                rightType = normalizedTranslatedRightType;
        }
        var matchedExpectedType = ResolveMatchedComparisonExpectedReturnType(leftType, rightType);
        var leftApplicationExpectedType = ResolveAmbiguousSingleLetterApplicationComparisonExpectedReturnType(
            left,
            leftType,
            rightType,
            task,
            dataObjects);
        var rightApplicationExpectedType = ResolveAmbiguousSingleLetterApplicationComparisonExpectedReturnType(
            right,
            rightType,
            leftType,
            task,
            dataObjects);
        if (preferApplicationDatabaseBinding)
        {
            var leftDatabaseExpectedType = ResolveApplicationDatabaseConfigComparisonExpectedReturnType(left, task, rightType);
            if (!string.IsNullOrWhiteSpace(leftDatabaseExpectedType))
                leftApplicationExpectedType = leftDatabaseExpectedType;

            var rightDatabaseExpectedType = ResolveApplicationDatabaseConfigComparisonExpectedReturnType(right, task, leftType);
            if (!string.IsNullOrWhiteSpace(rightDatabaseExpectedType))
                rightApplicationExpectedType = rightDatabaseExpectedType;
        }
        var hintedExpectedType = NormalizeReturnTypeToken(operandExpectedReturnTypeHint);
        if (!IsSourceScalarReturnType(hintedExpectedType))
            hintedExpectedType = "";
        var dominantExpectedType = !string.IsNullOrWhiteSpace(hintedExpectedType)
            ? hintedExpectedType
            : string.IsNullOrWhiteSpace(matchedExpectedType)
            ? ResolveDominantComparisonExpectedReturnType(left, leftType, right, rightType)
            : "";
        var leftExpectedType = !string.IsNullOrWhiteSpace(leftApplicationExpectedType)
            ? leftApplicationExpectedType
            : !string.IsNullOrWhiteSpace(dominantExpectedType)
            ? dominantExpectedType
            : !string.IsNullOrWhiteSpace(matchedExpectedType)
                ? matchedExpectedType
                : ResolveCounterpartComparisonExpectedReturnType(left, leftType, rightType, task, dataObjects);
        var rightExpectedType = !string.IsNullOrWhiteSpace(rightApplicationExpectedType)
            ? rightApplicationExpectedType
            : !string.IsNullOrWhiteSpace(dominantExpectedType)
            ? dominantExpectedType
            : !string.IsNullOrWhiteSpace(matchedExpectedType)
                ? matchedExpectedType
                : ResolveCounterpartComparisonExpectedReturnType(right, rightType, leftType, task, dataObjects);
        var leftOperand = TranslateSourceBooleanScalarOperandExpression(left, task, dataObjects, leftExpectedType, preferApplicationDatabaseBinding);
        var rightOperand = TranslateSourceBooleanScalarOperandExpression(right, task, dataObjects, rightExpectedType, preferApplicationDatabaseBinding);
        var leftCode = leftOperand.Code;
        var rightCode = rightOperand.Code;
        code = $"{leftCode} {comparisonOperator} {rightCode}";
        code = CollapseRedundantScalarCastWrappersDeep(code);
        return true;
    }

    private static bool TryTranslateSourceIfBranchComparisonCondition(
        string conditionSyntax,
        string trueBranchSyntax,
        string falseBranchSyntax,
        string branchReturnType,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string code,
        bool preferApplicationDatabaseBinding = false)
    {
        code = "";
        var expected = NormalizeReturnTypeToken(branchReturnType);
        if (!IsSourceScalarReturnType(expected) ||
            !TrySplitTopLevelXpaComparisonExpression(conditionSyntax, out var left, out _, out var right))
        {
            return false;
        }

        var leftMatchesTrue = SourceSyntaxEquivalent(left, trueBranchSyntax);
        var rightMatchesFalse = SourceSyntaxEquivalent(right, falseBranchSyntax);
        var leftMatchesFalse = SourceSyntaxEquivalent(left, falseBranchSyntax);
        var rightMatchesTrue = SourceSyntaxEquivalent(right, trueBranchSyntax);
        if ((!leftMatchesTrue || !rightMatchesFalse) &&
            (!leftMatchesFalse || !rightMatchesTrue))
        {
            return false;
        }

        return TryTranslateSourceComparisonExpression(
            conditionSyntax,
            task,
            dataObjects,
            out code,
            preferApplicationDatabaseBinding,
            expected);
    }

    private static bool SourceSyntaxEquivalent(string left, string right)
        => string.Equals(
            NormalizeSourceSyntaxForEquivalence(left),
            NormalizeSourceSyntaxForEquivalence(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSourceSyntaxForEquivalence(string syntax)
    {
        var value = StripRedundantOuterParentheses(CompleteSourceGrouping(WebUtility.HtmlDecode(syntax ?? "").Trim()));
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (IsQuotedSegmentStart(value, i))
            {
                if (!TryReadQuotedSegmentEnd(value, i, out var quoteEnd))
                    return value;
                sb.Append(value, i, quoteEnd - i + 1);
                i = quoteEnd;
                continue;
            }

            if (!char.IsWhiteSpace(value[i]))
                sb.Append(value[i]);
        }

        return sb.ToString();
    }

    private static string TranslateSourceBooleanScalarOperand(
        string syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        string expectedReturnType = "",
        bool preferApplicationDatabaseBinding = false)
        => TranslateSourceBooleanScalarOperandExpression(syntax, task, dataObjects, expectedReturnType, preferApplicationDatabaseBinding).Code;

    private static SourceFunctionArgumentExpression TranslateSourceBooleanScalarOperandExpression(
        string syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        string expectedReturnType = "",
        bool preferApplicationDatabaseBinding = false)
    {
        syntax = StripRedundantOuterParentheses(syntax.Trim());
        var normalizedExpectedReturnType = NormalizeReturnTypeToken(expectedReturnType);
        if (!string.Equals(normalizedExpectedReturnType, "Number", StringComparison.Ordinal))
        {
            if (IsSourceDateLiteral(syntax))
                normalizedExpectedReturnType = "Date";
            else if (IsSourceTimeLiteral(syntax))
                normalizedExpectedReturnType = "Time";
        }
        if (string.Equals(normalizedExpectedReturnType, "Bool", StringComparison.Ordinal) &&
            TryResolveDeclaredSourceBindingReturnTypeForToken(syntax, task, dataObjects, out var declaredBindingReturnType))
        {
            var normalizedDeclaredReturnType = GetValueReturnType(NormalizeReturnTypeToken(declaredBindingReturnType));
            if (!string.IsNullOrWhiteSpace(normalizedDeclaredReturnType) &&
                !string.Equals(normalizedDeclaredReturnType, "Bool", StringComparison.Ordinal))
                normalizedExpectedReturnType = normalizedDeclaredReturnType;
        }

        if (string.Equals(normalizedExpectedReturnType, "Bool", StringComparison.Ordinal) &&
            TryResolveTranslatedSourceBindingForToken(syntax, task, dataObjects, out _, out var directBindingReturnType))
        {
            var normalizedDirectReturnType = GetValueReturnType(NormalizeReturnTypeToken(directBindingReturnType));
            if (!string.IsNullOrWhiteSpace(normalizedDirectReturnType) &&
                !string.Equals(normalizedDirectReturnType, "Bool", StringComparison.Ordinal))
                normalizedExpectedReturnType = normalizedDirectReturnType;
        }

        if (!string.IsNullOrWhiteSpace(normalizedExpectedReturnType))
            return TranslateSourceFunctionArgumentExpression(syntax, normalizedExpectedReturnType, task, dataObjects, 0, preferApplicationDatabaseBinding);

        if (TryTranslateWholeKnownSourceFunctionCall(syntax, task, dataObjects, out var sourceFunctionTranslated, preferApplicationDatabaseBinding: preferApplicationDatabaseBinding))
        {
            var sourceReturnType = NormalizeReturnTypeToken(sourceFunctionTranslated.SourceReturnType);
            return new SourceFunctionArgumentExpression(
                sourceFunctionTranslated.Code.Trim(),
                sourceReturnType,
                sourceReturnType,
                "",
                string.IsNullOrWhiteSpace(sourceFunctionTranslated.EvidenceKind)
                    ? "source-comparison-function-operand"
                    : sourceFunctionTranslated.EvidenceKind);
        }

        var translated = NormalizeDateConstructorMappings(TranslateXpaExpressionToCSharp(syntax.Trim(), task, dataObjects));
        translated = NormalizeVarSetValueExpressions(translated);
        translated = StripRedundantOuterParentheses(translated.Trim());
        var sourceType = NormalizeReturnTypeToken(ResolveSourceExpressionReturnType(syntax, task, dataObjects, 0));
        if (string.IsNullOrWhiteSpace(sourceType) &&
            TryResolveTranslatedSourceBindingReturnType(translated, task, out var translatedReturnType))
            sourceType = NormalizeReturnTypeToken(translatedReturnType);

        return new SourceFunctionArgumentExpression(
            translated.Trim(),
            sourceType,
            sourceType,
            "",
            "source-comparison-operand");
    }

    private static bool TryResolveDeclaredSourceBindingReturnTypeForToken(
        string token,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string returnType)
    {
        returnType = "";
        var normalizedToken = token.Trim().ToUpperInvariant();
        if (!IsAlphabeticBindingToken(normalizedToken))
            return false;

        var binding = ResolveExpressionOrdinalBinding(normalizedToken, task, _allTasks ?? Array.Empty<TaskSemantic>(), dataObjects);
        if (string.IsNullOrWhiteSpace(binding) ||
            string.Equals(binding.Trim(), normalizedToken, StringComparison.OrdinalIgnoreCase))
            return false;

        if (TryResolveTranslatedSourceBindingReturnType(binding, task, out returnType))
            return true;

        return TryResolveSimpleSourceReturnTypeFromResourcePath(task, binding, out returnType);
    }

    private static string ResolveCounterpartComparisonExpectedReturnType(
        string ownSyntax,
        string ownReturnType,
        string counterpartReturnType,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        ownReturnType = NormalizeReturnTypeToken(ownReturnType);
        counterpartReturnType = NormalizeReturnTypeToken(counterpartReturnType);
        if (string.Equals(counterpartReturnType, "Number", StringComparison.Ordinal) &&
            IsSourceDateDifferenceCandidate(ownSyntax, task, dataObjects))
            return "Number";

        if (IsSourceUntypedParameterGetter(ownSyntax))
        {
            var counterpartValueType = GetValueReturnType(counterpartReturnType);
            if (counterpartValueType is "Number" or "Date" or "Time" or "Bool")
                return counterpartValueType;
        }

        if (!string.IsNullOrWhiteSpace(ownReturnType) &&
            !string.Equals(ownReturnType, "object", StringComparison.Ordinal))
            return "";

        if (string.IsNullOrWhiteSpace(counterpartReturnType) ||
            string.Equals(counterpartReturnType, "object", StringComparison.Ordinal))
            return "";

        return counterpartReturnType;
    }

    private static string ResolveAmbiguousSingleLetterApplicationComparisonExpectedReturnType(
        string ownSyntax,
        string ownReturnType,
        string counterpartReturnType,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        var expected = GetValueReturnType(NormalizeReturnTypeToken(counterpartReturnType));
        if (string.IsNullOrWhiteSpace(expected) ||
            string.Equals(expected, "object", StringComparison.Ordinal) ||
            SourceReturnTypeMatchesExpected(ownReturnType, expected))
            return "";

        return TryResolveSingleLetterApplicationBindingForExpectedSourceType(
            ownSyntax,
            task,
            dataObjects,
            expected,
            out _,
            out _)
            ? expected
            : "";
    }

    private static bool IsSourceUntypedParameterGetter(string syntax)
    {
        var trimmed = StripRedundantOuterParentheses(WebUtility.HtmlDecode(syntax ?? "").Trim());
        return TryParseFunctionCall(trimmed, out var functionName, out _) &&
               string.Equals(NormalizeXpaFunctionContractName(functionName), "GETPARAM", StringComparison.Ordinal);
    }

    private static string ResolveMatchedComparisonExpectedReturnType(string leftReturnType, string rightReturnType)
    {
        var left = GetValueReturnType(NormalizeReturnTypeToken(leftReturnType));
        var right = GetValueReturnType(NormalizeReturnTypeToken(rightReturnType));
        if (string.IsNullOrWhiteSpace(left) ||
            string.IsNullOrWhiteSpace(right) ||
            string.Equals(left, "object", StringComparison.Ordinal) ||
            string.Equals(right, "object", StringComparison.Ordinal) ||
            !string.Equals(left, right, StringComparison.Ordinal))
            return "";

        return left;
    }

    private static string ResolveDominantComparisonExpectedReturnType(
        string leftSyntax,
        string leftReturnType,
        string rightSyntax,
        string rightReturnType)
    {
        var left = GetValueReturnType(NormalizeReturnTypeToken(leftReturnType));
        var right = GetValueReturnType(NormalizeReturnTypeToken(rightReturnType));
        var leftIsDate = string.Equals(left, "Date", StringComparison.Ordinal) || IsSourceDateLiteral(leftSyntax);
        var leftIsTime = string.Equals(left, "Time", StringComparison.Ordinal) || IsSourceTimeLiteral(leftSyntax);
        var rightIsDate = string.Equals(right, "Date", StringComparison.Ordinal) || IsSourceDateLiteral(rightSyntax);
        var rightIsTime = string.Equals(right, "Time", StringComparison.Ordinal) || IsSourceTimeLiteral(rightSyntax);
        if ((leftIsDate && rightIsTime) || (leftIsTime && rightIsDate))
            return "Number";

        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) ||
            string.Equals(left, "object", StringComparison.Ordinal) ||
            string.Equals(right, "object", StringComparison.Ordinal) ||
            string.Equals(left, right, StringComparison.Ordinal))
            return "";

        if ((left is "Date" && IsSourceEmptyLiteralLike(rightSyntax)) ||
            (right is "Date" && IsSourceEmptyLiteralLike(leftSyntax)))
            return "Date";

        if ((left is "Time" && IsSourceEmptyLiteralLike(rightSyntax)) ||
            (right is "Time" && IsSourceEmptyLiteralLike(leftSyntax)))
            return "Time";

        if (IsSourceDateLiteral(leftSyntax) || IsSourceDateLiteral(rightSyntax))
            return "Date";

        if (IsSourceTimeLiteral(leftSyntax) || IsSourceTimeLiteral(rightSyntax))
            return "Time";

        if (((left is "Number" && (rightIsDate || rightIsTime) && !IsSourceEmptyLiteralLike(leftSyntax)) ||
             (right is "Number" && (leftIsDate || leftIsTime) && !IsSourceEmptyLiteralLike(rightSyntax))))
            return "Number";

        return "";
    }

    private static bool IsSourceEmptyLiteralLike(string syntax)
    {
        var trimmed = StripRedundantOuterParentheses(WebUtility.HtmlDecode(syntax ?? "").Trim());
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        if (string.Equals(trimmed, "0", StringComparison.Ordinal) ||
            string.Equals(trimmed, "''", StringComparison.Ordinal) ||
            string.Equals(trimmed, "\"\"", StringComparison.Ordinal) ||
            string.Equals(trimmed, "NULL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "u.Null()", StringComparison.Ordinal))
            return true;

        return string.Equals(trimmed, "'00/00/0000'DATE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "\"00/00/0000\"DATE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "00/00/0000", StringComparison.Ordinal) ||
               string.Equals(trimmed, "XPARuntimeCore.Box.Date.Empty", StringComparison.Ordinal) ||
               IsSourceEmptyDateConstructorLiteral(trimmed) ||
               string.Equals(trimmed, "Time.Empty", StringComparison.Ordinal) ||
               string.Equals(trimmed, "XPARuntimeCore.Box.Time.Empty", StringComparison.Ordinal);
    }

    private static bool IsSourceDateLiteral(string syntax)
    {
        var trimmed = StripRedundantOuterParentheses(WebUtility.HtmlDecode(syntax ?? "").Trim());
        return trimmed.EndsWith("'DATE", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith("\"DATE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "00/00/0000", StringComparison.Ordinal) ||
               string.Equals(trimmed, "XPARuntimeCore.Box.Date.Empty", StringComparison.Ordinal) ||
               IsSourceEmptyDateConstructorLiteral(trimmed);
    }

    private static bool IsSourceEmptyDateConstructorLiteral(string syntax)
    {
        var trimmed = StripRedundantOuterParentheses(WebUtility.HtmlDecode(syntax ?? "").Trim());
        if (!TryParseFunctionCall(trimmed, out var functionName, out var args) || args.Count != 3)
            return false;

        var normalizedFunction = functionName.Trim();
        if (!string.Equals(normalizedFunction, "new Date", StringComparison.Ordinal) &&
            !string.Equals(normalizedFunction, "new u.Date", StringComparison.Ordinal) &&
            !string.Equals(normalizedFunction, "new XPARuntimeCore.Box.Date", StringComparison.Ordinal))
            return false;

        return args.All(arg => string.Equals(arg.Trim(), "0", StringComparison.Ordinal));
    }

    private static bool IsSourceTimeLiteral(string syntax)
    {
        var trimmed = StripRedundantOuterParentheses(WebUtility.HtmlDecode(syntax ?? "").Trim());
        return trimmed.EndsWith("'TIME", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith("\"TIME", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "Time.Empty", StringComparison.Ordinal) ||
               string.Equals(trimmed, "XPARuntimeCore.Box.Time.Empty", StringComparison.Ordinal);
    }

    private static bool IsSourceDateDifferenceCandidate(
        string syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        var arithmetic = TrySplitTopLevelSourceArithmeticExpression(StripRedundantOuterParentheses(syntax.Trim()));
        if (arithmetic is null ||
            !string.Equals(arithmetic.Value.Operator, "-", StringComparison.Ordinal) ||
            IsNumericLiteralExpressionCentral(arithmetic.Value.Right))
            return false;

        var leftType = NormalizeReturnTypeToken(ResolveSourceExpressionReturnType(arithmetic.Value.Left, task, dataObjects, 0));
        return string.Equals(leftType, "Date", StringComparison.Ordinal);
    }

    private static bool TryResolveXpaLogicalLiteralCode(string? syntax, out string code)
    {
        code = "";
        if (string.IsNullOrWhiteSpace(syntax))
            return false;

        var normalized = WebUtility.HtmlDecode(syntax).Trim();
        if (string.Equals(normalized, "'TRUE'LOG", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "TRUE'LOG", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "TRUELOG", StringComparison.OrdinalIgnoreCase))
        {
            code = "true";
            return true;
        }

        if (string.Equals(normalized, "'FALSE'LOG", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "FALSE'LOG", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "FALSELOG", StringComparison.OrdinalIgnoreCase))
        {
            code = "false";
            return true;
        }

        return false;
    }

    private static bool TrySplitTopLevelXpaComparisonExpression(
        string syntax,
        out string left,
        out string comparisonOperator,
        out string right)
    {
        left = "";
        comparisonOperator = "";
        right = "";
        if (string.IsNullOrWhiteSpace(syntax))
            return false;

        var depth = 0;
        for (var i = 0; i < syntax.Length; i++)
        {
            if (IsQuotedSegmentStart(syntax, i))
            {
                if (!TryReadQuotedSegmentEnd(syntax, i, out var quoteEnd))
                    return false;

                i = quoteEnd;
                continue;
            }

            var ch = syntax[i];
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

            if (depth != 0)
                continue;

            var opLength = 0;
            var op = "";
            if (i + 1 < syntax.Length)
            {
                var pair = syntax.Substring(i, 2);
                if (pair == "<>" || pair == "!=")
                {
                    op = "!=";
                    opLength = 2;
                }
                else if (pair == "==")
                {
                    op = "==";
                    opLength = 2;
                }
                else if (pair is "<=" or ">=")
                {
                    op = pair;
                    opLength = 2;
                }
            }

            if (opLength == 0 && ch == '=')
            {
                op = "==";
                opLength = 1;
            }
            else if (opLength == 0 && ch is '<' or '>')
            {
                op = ch.ToString();
                opLength = 1;
            }

            if (opLength == 0)
                continue;

            left = syntax[..i].Trim();
            right = syntax[(i + opLength)..].Trim();
            comparisonOperator = op;
            return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right);
        }

        return false;
    }

    private static bool TrySplitTopLevelXpaBooleanBinaryExpression(
        string syntax,
        out string left,
        out string op,
        out string right,
        bool preferAndSplit = false)
    {
        if (preferAndSplit &&
            (TrySplitTopLevelXpaBooleanBinaryExpression(syntax, "AND", out left, out right) ||
             TrySplitTopLevelSymbolicBooleanBinaryExpression(syntax, "&&", out left, out right)))
        {
            op = "&&";
            return true;
        }

        if (TrySplitTopLevelXpaBooleanBinaryExpression(syntax, "OR", out left, out right) ||
            TrySplitTopLevelSymbolicBooleanBinaryExpression(syntax, "||", out left, out right))
        {
            op = "||";
            return true;
        }

        if (TrySplitTopLevelXpaBooleanBinaryExpression(syntax, "AND", out left, out right) ||
            TrySplitTopLevelSymbolicBooleanBinaryExpression(syntax, "&&", out left, out right))
        {
            op = "&&";
            return true;
        }

        left = "";
        op = "";
        right = "";
        return false;
    }

    private static bool TrySplitTopLevelXpaBooleanBinaryExpression(
        string syntax,
        string word,
        out string left,
        out string right)
    {
        left = "";
        right = "";
        if (string.IsNullOrWhiteSpace(syntax))
            return false;

        var depth = 0;
        for (var i = 0; i < syntax.Length; i++)
        {
            if (IsQuotedSegmentStart(syntax, i))
            {
                if (!TryReadQuotedSegmentEnd(syntax, i, out var quoteEnd))
                    return false;

                i = quoteEnd;
                continue;
            }

            var ch = syntax[i];
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

            if (depth != 0 ||
                !IsTopLevelXpaBooleanWordAt(syntax, i, word) ||
                !LooksLikeSourceBooleanOperatorAt(syntax, i, word.Length))
                continue;

            left = syntax[..i].Trim();
            right = syntax[(i + word.Length)..].Trim();
            return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right);
        }

        return false;
    }

    private static bool LooksLikeSourceBooleanOperatorAt(string syntax, int index, int wordLength)
        => HasCompleteSourceBooleanLeftOperand(syntax, index) &&
           HasCompleteSourceBooleanRightOperand(syntax, index + wordLength);

    private static bool HasCompleteSourceBooleanLeftOperand(string syntax, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var ch = syntax[i];
            if (char.IsWhiteSpace(ch))
                continue;

            if (ch is '<' or '>' or '=' or '+' or '-' or '*' or '/' or '&' or ',' or '(')
                return false;

            if (char.IsLetter(ch) && TryReadPreviousIdentifierToken(syntax, i, out var tokenStart))
            {
                var token = syntax[tokenStart..(i + 1)];
                if (IsSourceBooleanConnectorWord(token))
                    return false;
            }

            return true;
        }

        return false;
    }

    private static bool HasCompleteSourceBooleanRightOperand(string syntax, int index)
    {
        for (var i = index; i < syntax.Length; i++)
        {
            var ch = syntax[i];
            if (char.IsWhiteSpace(ch))
                continue;

            if (ch is '<' or '>' or '=' or '+' or '*' or '/' or '&' or ',' or ')')
                return false;

            if (char.IsLetter(ch) && TryReadIdentifierToken(syntax, i, out var tokenEnd))
            {
                var token = syntax[i..tokenEnd];
                return !token.Equals("AND", StringComparison.OrdinalIgnoreCase) &&
                       !token.Equals("OR", StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        return false;
    }

    private static bool TryReadPreviousIdentifierToken(string syntax, int tokenEndInclusive, out int tokenStart)
    {
        tokenStart = tokenEndInclusive;
        if (tokenEndInclusive < 0 ||
            tokenEndInclusive >= syntax.Length ||
            !IsIdentifierChar(syntax[tokenEndInclusive]))
            return false;

        while (tokenStart > 0 && IsIdentifierChar(syntax[tokenStart - 1]))
            tokenStart--;

        return true;
    }

    private static bool IsSourceBooleanConnectorWord(string token)
        => token.Equals("AND", StringComparison.OrdinalIgnoreCase) ||
           token.Equals("OR", StringComparison.OrdinalIgnoreCase) ||
           token.Equals("NOT", StringComparison.OrdinalIgnoreCase);

    private static bool TrySplitTopLevelSymbolicBooleanBinaryExpression(
        string syntax,
        string symbol,
        out string left,
        out string right)
    {
        left = "";
        right = "";
        if (string.IsNullOrWhiteSpace(syntax) || string.IsNullOrWhiteSpace(symbol))
            return false;

        var depth = 0;
        for (var i = 0; i < syntax.Length; i++)
        {
            if (IsQuotedSegmentStart(syntax, i))
            {
                if (!TryReadQuotedSegmentEnd(syntax, i, out var quoteEnd))
                    return false;

                i = quoteEnd;
                continue;
            }

            var ch = syntax[i];
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

            if (depth != 0 ||
                i + symbol.Length > syntax.Length ||
                !string.Equals(syntax.Substring(i, symbol.Length), symbol, StringComparison.Ordinal))
                continue;

            left = syntax[..i].Trim();
            right = syntax[(i + symbol.Length)..].Trim();
            return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right);
        }

        return false;
    }

    private static bool HasParenthesizedOrBeforeTopLevelAnd(string? syntax)
    {
        if (string.IsNullOrWhiteSpace(syntax))
            return false;

        var trimmed = syntax.Trim();
        if (trimmed.Length < 6 || trimmed[0] != '(')
            return false;

        var depth = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (IsQuotedSegmentStart(trimmed, i))
            {
                if (!TryReadQuotedSegmentEnd(trimmed, i, out var quoteEnd))
                    return false;

                i = quoteEnd;
                continue;
            }

            var ch = trimmed[i];
            if (ch == '(')
            {
                depth++;
                continue;
            }

            if (ch != ')')
                continue;

            depth--;
            if (depth != 0)
                continue;

            var inside = trimmed[1..i];
            var cursor = i + 1;
            while (cursor < trimmed.Length && char.IsWhiteSpace(trimmed[cursor]))
                cursor++;

            return IsTopLevelXpaBooleanWordAt(trimmed, cursor, "AND") &&
                   TrySplitTopLevelXpaBooleanBinaryExpression(inside, "OR", out _, out _);
        }

        return false;
    }

    private static string CoerceSourceTranslatedExpressionForContext(
        string code,
        string sourceReturnType,
        ExpressionEmissionContext context)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            string.IsNullOrWhiteSpace(sourceReturnType) ||
            !context.Expected.HasExpectation)
            return code;

        if (context.SinkKind == ExpressionSinkKind.RunArgument && context.PreserveBinding)
            return code;

        var sourceXpaType = XpaTypeEngine.MapExpectedToXpaType(NormalizeReturnTypeToken(sourceReturnType));
        var expectedXpaType = ResolveExpectedXpaType(context.Expected);
        if (sourceXpaType == XpaType.Unknown ||
            expectedXpaType == XpaType.Unknown ||
            sourceXpaType == expectedXpaType ||
            !XpaTypeEngine.CanCoerce(sourceXpaType, expectedXpaType))
            return code;

        return XpaTypeEngine.Coerce(code.Trim(), sourceXpaType, expectedXpaType);
    }

    private static bool TryResolveBlobObjectViewBindingExpression(
        ExpressionEntrySemantic? expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpressionEmissionContext context,
        string sourceReturnType,
        out string code,
        out string returnType)
    {
        code = "";
        returnType = "";
        if (expr is null ||
            context.SinkKind != ExpressionSinkKind.ViewBinding ||
            !string.Equals(NormalizeAttrObjKind(expr.Attribute), "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return false;

        var normalizedSourceReturnType = NormalizeReturnTypeToken(sourceReturnType);
        if (IsClrObjectReturnTypeForTypedExpression(normalizedSourceReturnType))
        {
            code = ResolveExpressionEntryCodeWithoutAttributeNormalization(expr, task, dataObjects);
            returnType = normalizedSourceReturnType;
            return !string.IsNullOrWhiteSpace(code);
        }

        var rawCode = ResolveExpressionEntryCodeWithoutAttributeNormalization(expr, task, dataObjects);
        if (string.IsNullOrWhiteSpace(rawCode))
            return false;

        var rawReturnType = NormalizeReturnTypeToken(ResolveExpressionReturnType(null, rawCode, task));
        if (!IsClrObjectReturnTypeForTypedExpression(rawReturnType))
            return false;

        code = rawCode;
        returnType = rawReturnType;
        return true;
    }

    private static bool IsClrObjectReturnTypeForTypedExpression(string returnType)
    {
        if (string.IsNullOrWhiteSpace(returnType))
            return false;

        var normalizedValueReturnType = GetValueReturnType(returnType);
        return (returnType.Contains('.', StringComparison.Ordinal) &&
                !string.Equals(normalizedValueReturnType, "byte[]", StringComparison.Ordinal) &&
                !string.Equals(normalizedValueReturnType, "byte[][]", StringComparison.Ordinal)) ||
               string.Equals(returnType, "System.String[]", StringComparison.Ordinal);
    }

    private static void RegisterContextualExpressionReturnType(
        TaskSemantic task,
        string code,
        ExpressionEmissionContext context)
    {
        if (string.IsNullOrWhiteSpace(code))
            return;

        var returnType = ResolveReturnTypeForExpectedContext(context.Expected);
        if (string.IsNullOrWhiteSpace(returnType))
            returnType = ResolveScalarReturnTypeForContext(
                context,
                !string.IsNullOrWhiteSpace(context.Expected.AttrObj)
                    ? context.Expected.AttrObj
                    : MapReturnTypeToAttrObj(GetValueReturnType(context.Expected.ReturnType)));

        if (string.IsNullOrWhiteSpace(returnType))
            return;

        RegisterTypedExpressionReturnType(task, code, returnType);
    }

    private static void RegisterTypedExpressionReturnType(TaskSemantic task, string code, string returnType)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(returnType))
            return;

        var normalizedReturnType = NormalizeReturnTypeToken(returnType);
        if (string.IsNullOrWhiteSpace(normalizedReturnType))
            return;

        var normalizedCode = StripRedundantOuterParentheses(code.Trim());
        if (string.IsNullOrWhiteSpace(normalizedCode))
            return;

        if (IsAmbiguousTypedExpressionRegistrationCode(normalizedCode))
        {
            Interlocked.Increment(ref _typedExpressionAmbiguousRegistrationSkipCount);
            return;
        }

        var key = BuildTypedExpressionReturnTypeCacheKey(task, normalizedCode);
        if (_typedExpressionReturnTypeByCodeCache.TryGetValue(key, out var existingReturnType))
        {
            if (string.IsNullOrWhiteSpace(existingReturnType))
                return;

            var normalizedExistingReturnType = NormalizeReturnTypeToken(existingReturnType);
            if (string.Equals(normalizedExistingReturnType, normalizedReturnType, StringComparison.Ordinal))
                return;

            // The same generated expression can be reused in different contexts.
            // When the effective types disagree, keep the old resolver as the
            // source of truth instead of letting the first registration win.
            _typedExpressionReturnTypeByCodeCache[key] = "";
            Interlocked.Increment(ref _typedExpressionAmbiguousRegistrationSkipCount);
            return;
        }

        _typedExpressionReturnTypeByCodeCache[key] = normalizedReturnType;
        Interlocked.Increment(ref _typedExpressionRegisteredReturnTypeCount);
    }

    private static bool IsAmbiguousTypedExpressionRegistrationCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return true;

        var trimmed = StripRedundantOuterParentheses(code.Trim());
        if (string.IsNullOrWhiteSpace(trimmed))
            return true;

        if (IsNullCallExpression(trimmed))
            return true;

        if (IsNumericLiteralExpressionCentral(trimmed))
            return true;

        if (TryGetWholeCSharpStringLiteral(trimmed, out var literalValue) &&
            string.IsNullOrEmpty(literalValue))
            return true;

        return bool.TryParse(trimmed, out _);
    }

    private static string BuildTypedExpressionReturnTypeCacheKey(TaskSemantic task, string code)
        => string.Create(CultureInfo.InvariantCulture, $"{task.Ordinal}|{code}");

    private static bool CanEmitExpressionEntryAsStatement(
        ExpressionEntrySemantic? expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (expr is null)
            return false;

        var sourceSyntax = StripRedundantOuterParentheses(
            WebUtility.HtmlDecode(ResolveExpressionEntrySourceSyntax(expr)).Trim());
        if (!TryParseFunctionCall(sourceSyntax, out var functionName, out _))
            return false;

        if (IsKnownXpaStatementFunction(functionName))
            return true;

        if (TryResolveSourceClrReturnType(sourceSyntax, task, dataObjects, out var returnType) &&
            string.Equals(NormalizeReturnTypeToken(returnType), "void", StringComparison.Ordinal))
            return true;

        var translated = TranslateXpaExpressionToCSharp(sourceSyntax, task, dataObjects, expr.Attribute);
        return TryReadDotNetMethodCallEvidence(translated, task, out returnType) &&
               string.Equals(NormalizeReturnTypeToken(returnType), "void", StringComparison.Ordinal);
    }

    private static string BuildTypedExpressionCacheKey(
        ExpressionEntrySemantic? expr,
        TaskSemantic task,
        ExpressionEmissionContext context)
    {
        var expressionKey = expr is null
            ? "<null>"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{expr.Ordinal}|{expr.Attribute}|{expr.IsStringLiteral}|{expr.Syntax}|{expr.LiteralNormalizedSyntax}");
        var blobTargetCacheKey = GetExpressionContextBlobTargetCacheKey(context);
        var targetMemberCacheKey = GetExpressionContextTargetMemberCacheKey(context);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{task.Ordinal}|{expressionKey}|{context.SinkKind}|{context.Expected.ReturnType}|{context.Expected.AttrObj}|{context.ParameterType}|{context.PreserveBinding}|{blobTargetCacheKey}|{context.TargetInfo?.AttrObj}|{targetMemberCacheKey}|{context.TargetInfo?.IsBlob}|{context.TargetInfo?.IsArray}|{context.TargetInfo?.IsBoolean}|{context.TargetInfo?.IsNumeric}|{context.TargetInfo?.IsDotNet}");
    }

    private static string ResolveIntrinsicReturnTypeForExpressionEntry(ExpressionEntrySemantic? expr)
    {
        if (expr is null)
            return "";

        if (expr.IsStringLiteral)
            return "Text";

        return ResolveSimpleReturnTypeForExpressionAttribute(expr.Attribute);
    }

    private static string ResolveEffectiveReturnTypeForTypedExpression(
        ExpressionEntrySemantic? expr,
        string code,
        TaskSemantic task,
        ExpressionEmissionContext context,
        string intrinsicReturnType,
        string sourceReturnType,
        out bool usedFallbackInference)
    {
        usedFallbackInference = false;
        if (string.IsNullOrWhiteSpace(code))
            return intrinsicReturnType;

        var trimmed = code.Trim();
        if (TryResolveSpecialExpressionReturnType(trimmed, out var specialReturnType))
            return specialReturnType;

        var normalizedSourceReturnType = NormalizeReturnTypeToken(sourceReturnType);
        if (!string.IsNullOrWhiteSpace(normalizedSourceReturnType) &&
            IsClrObjectReturnTypeForTypedExpression(normalizedSourceReturnType))
            return normalizedSourceReturnType;

        if (context.Expected.HasExpectation &&
            !ShouldDeferExpectedTypeShortcutForTypedExpression(trimmed, context))
        {
            var expectedReturnType = ResolveReturnTypeForExpectedContext(context.Expected);
            if (!string.IsNullOrWhiteSpace(expectedReturnType))
            {
                Interlocked.Increment(ref _typedExpressionExpectedTypeShortcutCount);
                _typedExpressionExpectedShortcutCountBySink.AddOrUpdate(context.SinkKind.ToString(), 1, static (_, count) => count + 1);
                return expectedReturnType;
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedSourceReturnType))
            return normalizedSourceReturnType;

        if (!string.IsNullOrWhiteSpace(intrinsicReturnType))
            return intrinsicReturnType;

        return "";
    }

    private static string ResolveSourceReturnTypeForExpressionEntry(
        ExpressionEntrySemantic? expr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (expr is null || string.IsNullOrWhiteSpace(expr.Syntax))
            return "";

        var cacheKey = BuildSourceExpressionEntryReturnTypeCacheKey(task, expr);
        if (_sourceExpressionReturnTypeCache.TryGetValue(cacheKey, out var cached))
        {
            Interlocked.Increment(ref _typedExpressionSourceTypeCacheHitCount);
            return cached;
        }

        if (!_sourceExpressionReturnTypeResolutionInProgress.Add(cacheKey))
        {
            var attributeFallback = ResolveSimpleReturnTypeForExpressionAttribute(expr.Attribute);
            return string.IsNullOrWhiteSpace(attributeFallback) ? "" : attributeFallback;
        }

        Interlocked.Increment(ref _typedExpressionSourceTypeCacheMissCount);
        try
        {
            string resolved;
            if (expr.IsStringLiteral)
            {
                resolved = TryResolveXpaLogicalLiteralCode(expr.Syntax, out _)
                    ? "Bool"
                    : "Text";
            }
            else
            {
                var syntax = WebUtility.HtmlDecode(ResolveExpressionEntrySourceSyntax(expr));
                var sourceType = ResolveSourceExpressionReturnType(syntax, task, dataObjects, 0);
                resolved = !string.IsNullOrWhiteSpace(sourceType)
                    ? sourceType
                    : ResolveSimpleReturnTypeForExpressionAttribute(expr.Attribute);
            }

            _sourceExpressionReturnTypeCache[cacheKey] = resolved;
            return resolved;
        }
        finally
        {
            _sourceExpressionReturnTypeResolutionInProgress.Remove(cacheKey);
        }
    }

    private static string ResolveSourceExpressionReturnType(
        string? syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        int depth)
    {
        if (string.IsNullOrWhiteSpace(syntax) || depth > 32)
            return "";

        var trimmed = syntax.Trim();
        var cacheKey = BuildSourceExpressionReturnTypeCacheKey(task, trimmed);
        if (_sourceExpressionReturnTypeCache.TryGetValue(cacheKey, out var cached))
        {
            Interlocked.Increment(ref _typedExpressionSourceTypeCacheHitCount);
            return cached;
        }

        if (!_sourceExpressionReturnTypeResolutionInProgress.Add(cacheKey))
            return "";

        Interlocked.Increment(ref _typedExpressionSourceTypeCacheMissCount);
        try
        {
            var resolved = ResolveSourceExpressionReturnTypeUncached(trimmed, task, dataObjects, depth);
            _sourceExpressionReturnTypeCache[cacheKey] = resolved;
            return resolved;
        }
        finally
        {
            _sourceExpressionReturnTypeResolutionInProgress.Remove(cacheKey);
        }
    }

    private static string ResolveSourceExpressionReturnTypeUncached(
        string trimmed,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        int depth)
    {
        var stripped = StripRedundantOuterParentheses(trimmed);
        if (!string.Equals(stripped, trimmed, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(stripped))
            return ResolveSourceExpressionReturnType(stripped, task, dataObjects, depth + 1);

        if (TryTranslateSourceVarIndexLiteral(trimmed, task, dataObjects, out _))
            return "Number";

        if (TryResolveXpaLogicalLiteralCode(trimmed, out _))
            return "Bool";
        if (TryParseWholeXpaSingleQuotedLiteral(trimmed, out _))
            return "Text";
        if (TryGetWholeCSharpStringLiteral(trimmed, out _))
            return "Text";
        if (IsSourceDateLiteral(trimmed))
            return "Date";
        if (IsSourceTimeLiteral(trimmed))
            return "Time";
        if (IsNumericLiteralExpressionCentral(trimmed))
            return "Number";
        if (string.Equals(trimmed, "TRUE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "FALSE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "No", StringComparison.OrdinalIgnoreCase))
            return "Bool";

        if (TryResolveSourceClrReturnType(trimmed, task, dataObjects, out var clrReturnType))
            return clrReturnType;

        if (TryParseFunctionCall(trimmed, out var localExpressionFunctionName, out var localExpressionArgs) &&
            IsEmptyFunctionArgumentList(localExpressionArgs) &&
            localExpressionFunctionName.StartsWith("Exp_", StringComparison.Ordinal) &&
            int.TryParse(localExpressionFunctionName["Exp_".Length..], out var localExpressionOrdinal) &&
            task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(localExpressionOrdinal, out var localExpression) &&
            localExpression is not null)
        {
            var localExpressionReturnType = ResolveIntrinsicReturnTypeForExpressionEntry(localExpression);
            if (string.IsNullOrWhiteSpace(localExpressionReturnType))
                localExpressionReturnType = ResolveSourceReturnTypeForExpressionEntry(localExpression, task, dataObjects);
            if (!string.IsNullOrWhiteSpace(localExpressionReturnType))
                return localExpressionReturnType;
        }

        if (TryReadRegisteredExpressionCallOrdinal(trimmed, out var registeredExpressionOrdinal) &&
            task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(registeredExpressionOrdinal, out var registeredExpression) &&
            registeredExpression is not null)
        {
            var registeredExpressionReturnType = ResolveIntrinsicReturnTypeForExpressionEntry(registeredExpression);
            if (string.IsNullOrWhiteSpace(registeredExpressionReturnType))
                registeredExpressionReturnType = ResolveSourceReturnTypeForExpressionEntry(registeredExpression, task, dataObjects);
            if (!string.IsNullOrWhiteSpace(registeredExpressionReturnType))
                return registeredExpressionReturnType;
        }

        if (TryResolveDirectSourceBindingReturnType(trimmed, task, dataObjects, depth, out var directBindingReturnType))
            return directBindingReturnType;

        if (trimmed.EndsWith(".Message", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".ToString()", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".FullDbName", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".DbName", StringComparison.OrdinalIgnoreCase))
            return "Text";

        if (ContainsTopLevelXpaBooleanExpression(trimmed))
            return "Bool";

        var arithmetic = TrySplitTopLevelSourceArithmeticExpression(trimmed) ??
                         SplitTopLevelArithmeticExpression(trimmed);
        if (arithmetic is not null)
        {
            Interlocked.Increment(ref _typedExpressionSourceArithmeticHitCount);
            var leftType = NormalizeReturnTypeToken(ResolveSourceExpressionReturnType(arithmetic.Value.Left, task, dataObjects, depth + 1));
            var rightType = NormalizeReturnTypeToken(ResolveSourceExpressionReturnType(arithmetic.Value.Right, task, dataObjects, depth + 1));
            var leftXpaType = XpaTypeEngine.MapExpectedToXpaType(leftType);
            var rightXpaType = XpaTypeEngine.MapExpectedToXpaType(rightType);

            if (string.Equals(arithmetic.Value.Operator, "&", StringComparison.Ordinal))
                return "Text";

            if (string.Equals(arithmetic.Value.Operator, "+", StringComparison.Ordinal) &&
                (leftXpaType == XpaType.Text ||
                 rightXpaType == XpaType.Text ||
                 leftXpaType == XpaType.Blob ||
                 rightXpaType == XpaType.Blob))
                return "Text";

            if (leftXpaType == XpaType.Number && rightXpaType == XpaType.Number)
                return "Number";

            if (IsReliableNumericSourceArithmetic(
                    arithmetic.Value.Operator,
                    leftType,
                    rightType))
                return "Number";

            if ((arithmetic.Value.Operator is "+" or "-") &&
                leftXpaType == XpaType.Date &&
                rightXpaType == XpaType.Number)
                return "Date";

            if (string.Equals(arithmetic.Value.Operator, "+", StringComparison.Ordinal) &&
                leftXpaType == XpaType.Number &&
                rightXpaType == XpaType.Date)
                return "Date";

            if ((arithmetic.Value.Operator is "+" or "-") &&
                leftXpaType == XpaType.Time &&
                rightXpaType == XpaType.Number)
                return "Time";

            if (string.Equals(arithmetic.Value.Operator, "+", StringComparison.Ordinal) &&
                leftXpaType == XpaType.Number &&
                rightXpaType == XpaType.Time)
                return "Time";

            if (leftXpaType == XpaType.Date && rightXpaType == XpaType.Date)
                return string.Equals(arithmetic.Value.Operator, "-", StringComparison.Ordinal)
                    ? "Number"
                    : "Date";

            if (leftXpaType == XpaType.Time && rightXpaType == XpaType.Time)
                return "Time";

            var unified = XpaTypeEngine.Unify(leftXpaType, rightXpaType);
            if (unified != XpaType.Unknown)
                return MapXpaTypeToReturnTypeCentral(unified);
        }

        if (TryParseFunctionCall(trimmed, out var functionName, out var args))
            return ResolveSourceFunctionReturnType(functionName, args, task, dataObjects, depth + 1);

        if (TryResolveSourceExpressionReturnTypeFromTaskExpressionSyntax(trimmed, task, out var expressionSyntaxReturnType))
            return expressionSyntaxReturnType;

        var translatedBinding = ResolveExpressionOrdinalBinding(trimmed, task, _allTasks ?? Array.Empty<TaskSemantic>(), dataObjects);
        if (!string.IsNullOrWhiteSpace(translatedBinding))
        {
            if (TryResolveTranslatedSourceBindingReturnType(translatedBinding, task, out var translatedBindingReturnType))
                return translatedBindingReturnType;

            return NormalizeReturnTypeToken(ResolveExpressionReturnType(null, translatedBinding, task));
        }

        if (IsSimpleIdentifierPath(trimmed))
        {
            if (TryResolveTranslatedSourceBindingReturnType(trimmed, task, out var translatedPathReturnType))
                return translatedPathReturnType;

            return NormalizeReturnTypeToken(ResolveExpressionReturnType(null, trimmed, task));
        }

        return "";
    }

    private static bool TryReadRegisteredExpressionCallOrdinal(string syntax, out int ordinal)
    {
        ordinal = 0;
        var trimmed = StripRedundantOuterParentheses(WebUtility.HtmlDecode(syntax ?? "").Trim());
        if (!TryParseFunctionCall(trimmed, out var functionName, out var args))
            return false;

        if (IsEmptyFunctionArgumentList(args) &&
            functionName.StartsWith("Exp_", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(functionName["Exp_".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal))
            return true;

        if (args.Count != 1 ||
            !string.Equals(functionName, "ExpCalc", StringComparison.OrdinalIgnoreCase))
            return false;

        return TryReadRegisteredExpressionOrdinalArgument(args[0], out ordinal);
    }

    private static bool TryReadRegisteredExpressionOrdinalArgument(string argument, out int ordinal)
    {
        ordinal = 0;
        var arg = StripRedundantOuterParentheses(WebUtility.HtmlDecode(argument ?? "").Trim());
        if (string.IsNullOrWhiteSpace(arg))
            return false;

        if (Regex.IsMatch(arg, @"^Exp_\d+\(\)$", RegexOptions.IgnoreCase))
        {
            var numeric = arg["Exp_".Length..^2];
            return int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal) &&
                   ordinal > 0;
        }

        if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal))
            return ordinal > 0;

        if (!TryReadXpaLiteral(arg, 0, out var literalEnd, out var literalValue, out _) ||
            !int.TryParse(literalValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal))
            return false;

        var cursor = literalEnd + 1;
        while (cursor < arg.Length && char.IsWhiteSpace(arg[cursor]))
            cursor++;

        if (!TryReadLiteralPostfixToken(arg, cursor, out var postfixToken, out var postfixEnd) ||
            !string.Equals(postfixToken, "EXP", StringComparison.OrdinalIgnoreCase))
            return false;

        cursor = postfixEnd + 1;
        while (cursor < arg.Length && char.IsWhiteSpace(arg[cursor]))
            cursor++;

        return cursor == arg.Length;
    }

    private static bool TryGetRegisteredExpressionSignatureReturnType(
        string syntax,
        TaskSemantic task,
        out string returnType)
    {
        returnType = "";
        if (!TryReadRegisteredExpressionCallOrdinal(syntax, out var ordinal) ||
            !task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(ordinal, out var expression) ||
            expression is null)
            return false;

        returnType = ResolveIntrinsicReturnTypeForExpressionEntry(expression);
        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static bool IsReliableNumericSourceArithmetic(
        string arithmeticOperator,
        string leftReturnType,
        string rightReturnType)
    {
        var left = XpaTypeEngine.MapExpectedToXpaType(NormalizeReturnTypeToken(leftReturnType));
        var right = XpaTypeEngine.MapExpectedToXpaType(NormalizeReturnTypeToken(rightReturnType));

        if (left is XpaType.Text or XpaType.Blob ||
            right is XpaType.Text or XpaType.Blob)
            return false;

        if (arithmeticOperator is not ("+" or "-" or "*" or "/" or "%" or "^"))
            return false;

        if (arithmeticOperator is "*" or "/" or "%" or "^")
            return IsNumericOperatorSourceType(left) &&
                   IsNumericOperatorSourceType(right);

        return (left == XpaType.Object && right == XpaType.Number) ||
               (left == XpaType.Number && right == XpaType.Object);
    }

    private static bool IsNumericOperatorSourceType(XpaType xpaType)
        => xpaType is XpaType.Number or XpaType.Date or XpaType.Time or XpaType.Object;

    private static bool TryResolveSourceClrReturnType(
        string syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string returnType)
    {
        returnType = "";
        var trimmed = StripRedundantOuterParentheses(WebUtility.HtmlDecode(syntax ?? "").Trim());
        if (TryResolveSourceDotNetEnumMemberReturnType(trimmed, out returnType))
            return true;

        if (TryParseFunctionCall(trimmed, out var functionName, out var args) &&
            TryResolveSourceDotNetMethodCallReturnType(functionName, args.Count, task, dataObjects, out returnType))
            return true;

        if (TryResolveSourceDotNetMemberAccess(trimmed, task, dataObjects, out _, out var objectType, out var memberName) &&
            TryReadDotNetMemberEvidence(objectType, memberName, out returnType))
            return true;

        return false;
    }

    private static bool TryResolveSourceDotNetEnumMemberReturnType(string syntax, out string returnType)
    {
        returnType = "";
        var normalized = StripDotNetQualifierOutsideQuotes(syntax ?? "").Trim();
        if (!IsSimpleIdentifierPath(normalized))
            return false;

        var lastDot = normalized.LastIndexOf('.');
        if (lastDot <= 0 || lastDot + 1 >= normalized.Length)
            return false;

        var declaringType = normalized[..lastDot];
        if (!IsKnownSourceClrEnumType(declaringType))
            return false;

        returnType = declaringType;
        return true;
    }

    private static bool TryResolveSourceDotNetMethodCallReturnType(
        string functionName,
        int argumentCount,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string returnType)
    {
        returnType = "";
        if (!TryResolveSourceDotNetMemberAccess(functionName, task, dataObjects, out _, out var objectType, out var memberName))
            return false;

        if (argumentCount == 0 &&
            string.Equals(memberName, "ShowDialog", StringComparison.Ordinal) &&
            IsWindowsFormsDialogLikeType(objectType))
        {
            returnType = "System.Windows.Forms.DialogResult";
            return true;
        }

        if (TryReadDotNetMethodReturnType(objectType, memberName, argumentCount, out returnType))
            return true;

        return false;
    }

    private static bool TryResolveSourceDotNetMemberAccess(
        string sourcePath,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string translatedPath,
        out string objectType,
        out string memberName)
    {
        translatedPath = "";
        objectType = "";
        memberName = "";
        if (string.IsNullOrWhiteSpace(sourcePath))
            return false;

        var dot = sourcePath.IndexOf('.');
        if (dot <= 0 || dot + 1 >= sourcePath.Length)
            return false;

        var sourceToken = sourcePath[..dot].Trim();
        memberName = sourcePath[(dot + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(sourceToken) || string.IsNullOrWhiteSpace(memberName))
            return false;

        if (!TryResolveSourceDotNetResourceToken(sourceToken, task, dataObjects, out var resourceBinding, out objectType))
            return false;

        translatedPath = $"{resourceBinding}.{StripDotNetQualifierOutsideQuotes(memberName)}";
        return true;
    }

    private static bool TryResolveSourceDotNetResourceToken(
        string sourceToken,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string binding,
        out string objectType)
    {
        binding = "";
        objectType = "";
        var trimmedToken = sourceToken.Trim();
        if (string.IsNullOrWhiteSpace(trimmedToken))
            return false;

        var resourceReferenceMap = BuildAccessibleLegacyResourceReferenceMap(task, dataObjects);
        if (resourceReferenceMap.TryGetValue(trimmedToken, out var mappedReference) &&
            !string.IsNullOrWhiteSpace(mappedReference))
        {
            var resource = ResolveResourceByTargetPath(task, mappedReference.Trim(), _allTasks ?? Array.Empty<TaskSemantic>());
            if (resource is not null && IsDotNetTaskResource(resource))
            {
                binding = mappedReference.Trim();
                objectType = NormalizeDotNetObjectType(resource.ObjectType ?? "");
                return !string.IsNullOrWhiteSpace(objectType);
            }
        }

        if (task.ResourcesSemantic.ByName.TryGetValue(trimmedToken, out var namedResource) &&
            namedResource is not null &&
            IsDotNetTaskResource(namedResource))
        {
            binding = ResolveTaskResourceMemberName(task, namedResource);
            objectType = NormalizeDotNetObjectType(namedResource.ObjectType ?? "");
            return !string.IsNullOrWhiteSpace(binding) && !string.IsNullOrWhiteSpace(objectType);
        }

        if (task.ResourcesSemantic.ByLegacyName.TryGetValue(trimmedToken, out var legacyResource) &&
            legacyResource is not null &&
            IsDotNetTaskResource(legacyResource))
        {
            binding = ResolveTaskResourceMemberName(task, legacyResource);
            objectType = NormalizeDotNetObjectType(legacyResource.ObjectType ?? "");
            return !string.IsNullOrWhiteSpace(binding) && !string.IsNullOrWhiteSpace(objectType);
        }

        var normalizedToken = trimmedToken.ToUpperInvariant();
        if (!IsAlphabeticBindingToken(normalizedToken))
            return false;

        var translated = ResolveExpressionOrdinalBinding(normalizedToken, task, _allTasks ?? Array.Empty<TaskSemantic>(), dataObjects);
        if (!string.IsNullOrWhiteSpace(translated))
        {
            var resource = ResolveResourceByTargetPath(task, translated.Trim(), _allTasks ?? Array.Empty<TaskSemantic>());
            if (resource is not null && IsDotNetTaskResource(resource))
            {
                binding = translated.Trim();
                objectType = NormalizeDotNetObjectType(resource.ObjectType ?? "");
                return !string.IsNullOrWhiteSpace(objectType);
            }
        }

        return false;
    }

    private static bool IsKnownSourceClrEnumType(string typeName)
    {
        var normalized = StripDotNetQualifierOutsideQuotes(typeName ?? "").Trim();
        return string.Equals(normalized, "System.Windows.Forms.DialogResult", StringComparison.Ordinal);
    }

    private static bool IsWindowsFormsDialogLikeType(string objectType)
    {
        var normalized = NormalizeDotNetObjectType(objectType ?? "");
        return string.Equals(normalized, "System.Windows.Forms.ColorDialog", StringComparison.Ordinal) ||
               string.Equals(normalized, "System.Windows.Forms.FontDialog", StringComparison.Ordinal) ||
               string.Equals(normalized, "System.Windows.Forms.OpenFileDialog", StringComparison.Ordinal) ||
               string.Equals(normalized, "System.Windows.Forms.SaveFileDialog", StringComparison.Ordinal) ||
               string.Equals(normalized, "System.Windows.Forms.FolderBrowserDialog", StringComparison.Ordinal) ||
               string.Equals(normalized, "System.Windows.Forms.Form", StringComparison.Ordinal);
    }

    private static string CompleteSourceGrouping(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = expression.Trim();
        var depth = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (IsQuotedSegmentStart(trimmed, i))
            {
                if (!TryReadQuotedSegmentEnd(trimmed, i, out var quoteEnd))
                    return trimmed;

                i = quoteEnd;
                continue;
            }

            var ch = trimmed[i];
            if (ch == '(')
            {
                depth++;
                continue;
            }

            if (ch != ')')
                continue;

            if (depth == 0)
                return trimmed;

            depth--;
        }

        return depth == 0
            ? trimmed
            : trimmed + new string(')', depth);
    }

    private static (string Left, string Operator, string Right)? TrySplitTopLevelSourceArithmeticExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var depth = 0;
        var operatorIndex = -1;
        char operatorChar = '\0';
        var selectedPrecedence = int.MaxValue;
        for (var i = 0; i < expression.Length; i++)
        {
            var ch = expression[i];
            if (IsQuotedSegmentStart(expression, i))
            {
                if (!TryReadQuotedSegmentEnd(expression, i, out var quoteEnd))
                    return null;

                i = quoteEnd;
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

            if (depth != 0 || ch is not ('+' or '-' or '*' or '/' or '%' or '&' or '^'))
                continue;

            if (ch is '+' or '-')
            {
                var previous = i - 1;
                while (previous >= 0 && char.IsWhiteSpace(expression[previous]))
                    previous--;

                if (previous < 0 || "+-*/%^(<>=!&|,".Contains(expression[previous]))
                    continue;
            }

            var leftCandidate = expression[..i].Trim();
            var rightCandidate = expression[(i + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(leftCandidate) || string.IsNullOrWhiteSpace(rightCandidate))
                continue;

            var precedence = SourceArithmeticOperatorPrecedence(ch.ToString());
            if (precedence > selectedPrecedence)
                continue;

            selectedPrecedence = precedence;
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

    private static bool TryResolveDirectSourceBindingReturnType(
        string token,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        int depth,
        out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(token) ||
            !IsAlphabeticBindingToken(token))
            return false;

        bool Hit()
        {
            Interlocked.Increment(ref _typedExpressionSourceDirectBindingHitCount);
            return true;
        }

        if (task.SelectsSemantic.ItemsByName.TryGetValue(token, out var select) &&
            select is not null)
        {
            if (TryResolveSourceSelectReturnType(select, task, dataObjects, out returnType))
                return Hit();

            return false;
        }

        if (TryResolveTranslatedSourceBindingForToken(token, task, dataObjects, out _, out returnType))
            return Hit();

        var translatedBinding = ResolveExpressionOrdinalBinding(token, task, _allTasks ?? Array.Empty<TaskSemantic>(), dataObjects);
        if (!string.IsNullOrWhiteSpace(translatedBinding) &&
            !string.Equals(translatedBinding.Trim(), token.Trim(), StringComparison.OrdinalIgnoreCase) &&
            TryResolveTranslatedSourceBindingReturnType(translatedBinding, task, out returnType))
            return Hit();

        if (TryResolveOrdinalSourceBindingReturnType(token, task, dataObjects, depth, out returnType))
            return Hit();

        if (TryResolveSourceResourceReturnType(token, task, out returnType))
            return Hit();

        if (TryResolveParentSourceResourceBinding(token, task, out _, out returnType))
            return Hit();

        if (!string.IsNullOrWhiteSpace(translatedBinding) &&
            TryResolveTranslatedSourceBindingReturnType(translatedBinding, task, out returnType))
            return Hit();

        return false;
    }

    private static bool TryResolveSourceSelectReturnType(
        TaskLogicSelectDef select,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string returnType)
    {
        returnType = "";
        if (string.Equals(select.Type, "R", StringComparison.OrdinalIgnoreCase) &&
            select.SourceDbObj.HasValue)
        {
            var dataObject = dataObjects.FirstOrDefault(x => x.Ordinal == select.SourceDbObj.Value);
            var column = dataObject?.Columns.FirstOrDefault(c => c.Id == select.ColumnId);
            if (dataObject is not null &&
                column is null &&
                select.ColumnId > 0 &&
                select.ColumnId <= dataObject.Columns.Count)
                column = dataObject.Columns[select.ColumnId - 1];

            if (column is not null)
            {
                returnType = NormalizeReturnTypeToken(MapAttrObjToReturnType(ResolveEffectiveDataColumnAttrObj(column)));
                return !string.IsNullOrWhiteSpace(returnType);
            }
        }
        else
        {
            var selectResource = ResolveTaskResourceColumn(task, select.ColumnId);
            if (selectResource is not null &&
                TryMapSourceResourceReturnType(selectResource, task, out returnType))
                return true;
        }

        if (select.AssignmentExpressionId.HasValue &&
            task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(select.AssignmentExpressionId.Value, out var selectExpression) &&
            selectExpression is not null)
        {
            returnType = ResolveSourceReturnTypeForExpressionEntry(selectExpression, task, dataObjects);
            return !string.IsNullOrWhiteSpace(returnType);
        }

        return false;
    }

    private static bool TryResolveTranslatedSourceBindingForToken(
        string token,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string binding,
        out string returnType)
    {
        binding = "";
        returnType = "";
        if (string.IsNullOrWhiteSpace(token) || !IsAlphabeticBindingToken(token.Trim().ToUpperInvariant()))
            return false;

        var normalizedToken = token.Trim();
        if (task.SelectsSemantic.ItemsByName.TryGetValue(normalizedToken, out var localSelect) &&
            localSelect is not null &&
            TryResolveSourceSelectReturnType(localSelect, task, dataObjects, out returnType))
        {
            var selectMap = BuildSelectNameToExpressionMap(task, dataObjects);
            if (selectMap.TryGetValue(normalizedToken, out var selectBinding) &&
                !string.IsNullOrWhiteSpace(selectBinding))
            {
                binding = selectBinding.Trim();
                return true;
            }
        }

        var ordinalBinding = ResolveExpressionOrdinalBinding(normalizedToken, task, _allTasks ?? Array.Empty<TaskSemantic>(), dataObjects);
        if (!string.IsNullOrWhiteSpace(ordinalBinding) &&
            !string.Equals(ordinalBinding.Trim(), normalizedToken, StringComparison.OrdinalIgnoreCase) &&
            TryResolveTranslatedSourceBindingReturnType(ordinalBinding, task, out returnType))
        {
            binding = ordinalBinding.Trim();
            return true;
        }

        if (TryResolveParentSourceResourceBinding(normalizedToken, task, out binding, out returnType))
            return true;

        var resourceReferenceMap = BuildAccessibleLegacyResourceReferenceMap(task, dataObjects);
        if (resourceReferenceMap.TryGetValue(normalizedToken, out var mappedReference) &&
            !string.IsNullOrWhiteSpace(mappedReference) &&
            TryResolveTranslatedSourceBindingReturnType(mappedReference, task, out returnType))
        {
            binding = mappedReference.Trim();
            return true;
        }

        var translated = NormalizeDateConstructorMappings(TranslateXpaExpressionToCSharp(token, task, dataObjects));
        translated = NormalizeVarSetValueExpressions(translated).Trim();
        if (string.IsNullOrWhiteSpace(translated))
            return false;

        if (TryResolveTranslatedSourceBindingReturnType(translated, task, out returnType))
        {
            binding = translated;
            return true;
        }

        if (TryResolveSimpleSourceReturnTypeFromResourcePath(task, translated, out returnType))
        {
            binding = translated;
            return true;
        }

        return false;
    }

    private static bool TryResolveParentSourceResourceBinding(
        string token,
        TaskSemantic task,
        out string binding,
        out string returnType)
    {
        binding = "";
        returnType = "";
        if (string.IsNullOrWhiteSpace(token) || _allTasks is null)
            return false;

        var normalizedToken = token.Trim();
        var depth = 0;
        var parentOrdinal = task.ParentOrdinal;
        while (parentOrdinal.HasValue)
        {
            var parentTask = GetTaskByOrdinal(parentOrdinal, _allTasks);
            if (parentTask is null)
                break;

            depth++;
            if (TryResolveTaskResourceByNameOrLegacy(parentTask, normalizedToken, out var resource) &&
                TryMapSourceResourceReturnType(resource, parentTask, out returnType))
            {
                binding = string.Concat(Enumerable.Repeat("_parent.", depth)) +
                          ResolveTaskResourceMemberName(parentTask, resource);
                return true;
            }

            parentOrdinal = parentTask.ParentOrdinal;
        }

        return false;
    }

    private static bool TryResolveTaskResourceByNameOrLegacy(
        TaskSemantic task,
        string token,
        out TaskResourceColumnDef resource)
    {
        resource = default!;
        if (task.ResourcesSemantic.ByName.TryGetValue(token, out var byName) && byName is not null)
        {
            resource = byName;
            return true;
        }

        if (task.ResourcesSemantic.ByLegacyName.TryGetValue(token, out var byLegacy) && byLegacy is not null)
        {
            resource = byLegacy;
            return true;
        }

        return false;
    }

    private static bool TryResolveOrdinalSourceBindingReturnType(
        string normalizedToken,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        int depth,
        out string returnType)
    {
        returnType = "";
        var slot = ToAlphabeticSlot(normalizedToken);
        if (slot <= 0)
            return false;

        var allTasks = _allTasks ?? Array.Empty<TaskSemantic>();
        if (normalizedToken.Length == 1)
        {
            var appTask = allTasks.FirstOrDefault(t => t.MainProgram) ?? allTasks.FirstOrDefault(t => t.ParentOrdinal is null);
            if (appTask is not null)
            {
                if (appTask.SelectsSemantic.ItemsByName.TryGetValue(normalizedToken, out var appSelect) &&
                    appSelect is not null &&
                    TryResolveSourceSelectReturnType(appSelect, appTask, dataObjects, out returnType))
                    return true;

                if (slot <= appTask.ResourcesSemantic.Ordered.Count &&
                    TryMapSourceResourceReturnType(appTask.ResourcesSemantic.Ordered[slot - 1], appTask, out returnType))
                    return true;
            }
        }

        var columnIndex = slot - 2;
        if (columnIndex <= 0)
            return false;

        var minLocalSelectSlot = task.SelectsSemantic.Items
            .Select(s => ToAlphabeticSlot(s.Name))
            .Where(slotValue => slotValue > 0)
            .DefaultIfEmpty(int.MaxValue)
            .Min();

        if (task.ParentOrdinal.HasValue && slot < minLocalSelectSlot &&
            TryResolveOrdinalSourceBindingReturnTypeFromParentChain(columnIndex, task, allTasks, out returnType))
            return true;

        if (columnIndex <= task.ResourcesSemantic.Ordered.Count &&
            TryMapSourceResourceReturnType(task.ResourcesSemantic.Ordered[columnIndex - 1], task, out returnType))
            return true;

        if (TryResolveOrdinalSourceBindingReturnTypeFromParentChain(columnIndex, task, allTasks, out returnType))
            return true;

        if (depth == 0)
        {
            foreach (var resource in task.ResourcesSemantic.Ordered)
            {
                if (!string.Equals(ResolveTaskResourceMemberName(task, resource), normalizedToken, StringComparison.OrdinalIgnoreCase))
                    continue;

                return TryMapSourceResourceReturnType(resource, task, out returnType);
            }
        }

        return false;
    }

    private static bool TryResolveOrdinalSourceBindingReturnTypeFromParentChain(
        int columnIndex,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        out string returnType)
    {
        returnType = "";
        if (columnIndex <= 0)
            return false;

        var parentOrdinal = task.ParentOrdinal;
        while (parentOrdinal.HasValue)
        {
            var parentTask = GetTaskByOrdinal(parentOrdinal, allTasks);
            if (parentTask is null)
                break;

            if (columnIndex <= parentTask.ResourcesSemantic.Ordered.Count)
                return TryMapSourceResourceReturnType(parentTask.ResourcesSemantic.Ordered[columnIndex - 1], parentTask, out returnType);

            parentOrdinal = parentTask.ParentOrdinal;
        }

        return false;
    }

    private static bool TryResolveSourceResourceReturnType(
        string token,
        TaskSemantic task,
        out string returnType)
    {
        returnType = "";
        if (task.ResourcesSemantic.ByName.TryGetValue(token, out var namedResource) &&
            namedResource is not null &&
            TryMapSourceResourceReturnType(namedResource, task, out returnType))
            return true;

        if (task.ResourcesSemantic.ByLegacyName.TryGetValue(token, out var legacyResource) &&
            legacyResource is not null &&
            TryMapSourceResourceReturnType(legacyResource, task, out returnType))
            return true;

        return false;
    }

    private static bool TryResolveTranslatedSourceBindingReturnType(
        string translatedBinding,
        TaskSemantic task,
        out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(translatedBinding))
            return false;

        var binding = translatedBinding.Trim();
        var resource = ResolveResourceByTargetPath(task, binding, _allTasks ?? Array.Empty<TaskSemantic>());
        if (resource is null)
        {
            if (TryReadDotNetMemberEvidence(binding, task, out returnType))
            {
                Interlocked.Increment(ref _typedExpressionSourceDirectBindingHitCount);
                return true;
            }

            return false;
        }

        var ownerTask = task.ResourcesSemantic.Ordered.Any(r => ReferenceEquals(r, resource))
            ? task
            : ResolveOwningTaskForResource(resource) ?? task;
        if (!TryMapSourceResourceReturnType(resource, ownerTask, out returnType))
            return false;

        Interlocked.Increment(ref _typedExpressionSourceDirectBindingHitCount);
        return true;
    }

    private static bool TryMapSourceResourceReturnType(
        TaskResourceColumnDef resource,
        TaskSemantic task,
        out string returnType)
    {
        returnType = "";
        if (IsDotNetTaskResource(resource))
        {
            var objectType = NormalizeDotNetObjectType(resource.ObjectType ?? "");
            if (string.Equals(objectType, "System.String", StringComparison.Ordinal) ||
                string.Equals(objectType, "string", StringComparison.OrdinalIgnoreCase))
            {
                returnType = "Text";
                return true;
            }

            return false;
        }

        if (IsArrayTaskResource(resource))
            return false;

        var resolvedType = ResolveTaskResourceColumnType(resource, _allFieldModels, task);
        var resolvedAttrObj = ResolveAttrObjForColumnType(resolvedType, _allFieldModels, task);
        var mapped = NormalizeReturnTypeToken(MapAttrObjToReturnType(resolvedAttrObj));
        if (string.IsNullOrWhiteSpace(mapped) ||
            string.Equals(mapped, "object", StringComparison.Ordinal) ||
            string.Equals(mapped, "byte[]", StringComparison.Ordinal))
        {
            var attrObj = ResolveEffectiveTaskResourceAttrObj(resource, task);
            mapped = NormalizeReturnTypeToken(MapAttrObjToReturnType(attrObj));
        }

        if (string.IsNullOrWhiteSpace(mapped) ||
            string.Equals(mapped, "object", StringComparison.Ordinal) ||
            string.Equals(mapped, "byte[]", StringComparison.Ordinal))
            return false;

        returnType = mapped;
        return true;
    }

    private static string BuildSourceExpressionEntryReturnTypeCacheKey(TaskSemantic task, ExpressionEntrySemantic expr)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"entry|{task.Ordinal}|{expr.Ordinal}|{expr.Attribute}|{expr.IsStringLiteral}|{expr.LiteralNormalizedSyntax}");

    private static bool TryResolveSourceExpressionReturnTypeFromTaskExpressionSyntax(
        string syntax,
        TaskSemantic task,
        out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(syntax) ||
            task.ExpressionsSemantic.Entries.Count == 0 ||
            !IsSimpleIdentifierPath(syntax))
            return false;

        foreach (var entry in task.ExpressionsSemantic.Entries)
        {
            if (entry is null)
                continue;

            var entrySyntax = ResolveExpressionEntrySourceSyntax(entry);
            if (!string.Equals(entrySyntax.Trim(), syntax, StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = NormalizeReturnTypeToken(ResolveSimpleReturnTypeForExpressionAttribute(entry.Attribute));
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (string.IsNullOrWhiteSpace(returnType))
            {
                returnType = candidate;
                continue;
            }

            if (!string.Equals(returnType, candidate, StringComparison.Ordinal))
            {
                returnType = "";
                return false;
            }
        }

        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static string ResolveExpressionEntrySourceSyntax(ExpressionEntrySemantic expr)
        => !string.IsNullOrWhiteSpace(expr.LiteralNormalizedSyntax)
            ? expr.LiteralNormalizedSyntax
            : expr.Syntax ?? "";

    private static string BuildSourceExpressionReturnTypeCacheKey(TaskSemantic task, string syntax)
        => string.Create(CultureInfo.InvariantCulture, $"syntax|{task.Ordinal}|{syntax}");

    private static string ResolveSourceFunctionReturnType(
        string functionName,
        IReadOnlyList<string> args,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        int depth)
    {
        if (TryResolveKnownXpaFunctionReturnType(functionName, args, out var knownReturnType))
            return NormalizeReturnTypeToken(knownReturnType);

        var info = AnalyzeSourceFunctionCallTypeInfo(functionName, args, task, dataObjects, depth);
        return info.ReturnType;
    }

    private static SourceFunctionCallTypeInfo AnalyzeSourceFunctionCallTypeInfo(
        string functionName,
        IReadOnlyList<string> args,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        int depth)
    {
        var cacheKey = BuildSourceFunctionCallTypeInfoCacheKey(task, functionName, args);
        if (_sourceFunctionCallTypeInfoCache.TryGetValue(cacheKey, out var cached))
        {
            Interlocked.Increment(ref _sourceFunctionCallCacheHitCount);
            return cached;
        }

        Interlocked.Increment(ref _sourceFunctionCallAnalyzeCount);
        var normalizedFunction = NormalizeXpaFunctionContractName(functionName);
        if (!string.IsNullOrWhiteSpace(normalizedFunction))
            _sourceFunctionCallAnalyzeCountByFunction.AddOrUpdate(normalizedFunction, 1, static (_, count) => count + 1);

        var returnType = "";
        var expectedArgumentReturnTypes = new string[args.Count];
        var actualArgumentReturnTypes = new string[args.Count];

        var isIfFunction = string.Equals(normalizedFunction, "IF", StringComparison.OrdinalIgnoreCase);
        var isCndRangeFunction = string.Equals(normalizedFunction, "CNDRANGE", StringComparison.OrdinalIgnoreCase);
        var isRangeFunction = string.Equals(normalizedFunction, "RANGE", StringComparison.OrdinalIgnoreCase);
        var isRangeLocateAddFunction =
            string.Equals(normalizedFunction, "RANGEADD", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedFunction, "LOCATEADD", StringComparison.OrdinalIgnoreCase);
        var isCaseFunction = string.Equals(normalizedFunction, "CASE", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(normalizedFunction, "CASEUNTYPED", StringComparison.OrdinalIgnoreCase);
        if (isIfFunction && args.Count >= 3)
        {
            expectedArgumentReturnTypes[0] = "Bool";
            var trueType = ResolveSourceExpressionReturnType(args[1], task, dataObjects, depth + 1);
            var falseType = ResolveSourceExpressionReturnType(args[2], task, dataObjects, depth + 1);
            returnType = UnifySourceReturnTypes(trueType, falseType);
            if (!string.IsNullOrWhiteSpace(returnType))
            {
                expectedArgumentReturnTypes[1] = returnType;
                expectedArgumentReturnTypes[2] = returnType;
            }
        }
        else if (isCndRangeFunction && args.Count >= 2)
        {
            expectedArgumentReturnTypes[0] = "Bool";
            var valueType = ResolveSourceExpressionReturnType(args[1], task, dataObjects, depth + 1);
            if (!string.IsNullOrWhiteSpace(valueType))
            {
                returnType = valueType;
                expectedArgumentReturnTypes[1] = valueType;
                actualArgumentReturnTypes[1] = valueType;
            }
        }
        else if (isRangeFunction && args.Count >= 3)
        {
            returnType = "Bool";
            var rangeValueType = ResolveSourceExpressionReturnType(args[0], task, dataObjects, depth + 1);
            if (!string.IsNullOrWhiteSpace(rangeValueType))
            {
                expectedArgumentReturnTypes[0] = rangeValueType;
                expectedArgumentReturnTypes[1] = rangeValueType;
                expectedArgumentReturnTypes[2] = rangeValueType;
                actualArgumentReturnTypes[0] = rangeValueType;
            }
        }
        else if (isRangeLocateAddFunction && args.Count >= 3)
        {
            returnType = "Bool";
            expectedArgumentReturnTypes[0] = "Number";
            for (var i = 1; i <= 2 && i < args.Count; i++)
            {
                if (TryResolveNullableIfBranchSourceReturnType(args[i], task, dataObjects, depth + 1, out var branchType))
                    expectedArgumentReturnTypes[i] = branchType;
            }
        }
        else if (isCaseFunction && args.Count >= 3)
        {
            var selectorType = ResolveSourceExpressionReturnType(args[0], task, dataObjects, depth + 1);
            if (!string.IsNullOrWhiteSpace(selectorType))
            {
                expectedArgumentReturnTypes[0] = selectorType;
                actualArgumentReturnTypes[0] = selectorType;
                for (var i = 1; i + 1 < args.Count; i += 2)
                    expectedArgumentReturnTypes[i] = selectorType;
            }

            var branchReturnType = "";
            for (var i = 2; i < args.Count; i += 2)
            {
                var candidate = ResolveSourceExpressionReturnType(args[i], task, dataObjects, depth + 1);
                branchReturnType = string.IsNullOrWhiteSpace(branchReturnType)
                    ? candidate
                    : UnifySourceReturnTypes(branchReturnType, candidate);
            }

            if (args.Count % 2 == 0)
            {
                var defaultCandidate = ResolveSourceExpressionReturnType(args[^1], task, dataObjects, depth + 1);
                branchReturnType = string.IsNullOrWhiteSpace(branchReturnType)
                    ? defaultCandidate
                    : UnifySourceReturnTypes(branchReturnType, defaultCandidate);
            }

            returnType = branchReturnType;
            if (!string.IsNullOrWhiteSpace(returnType))
            {
                for (var i = 2; i < args.Count; i += 2)
                    expectedArgumentReturnTypes[i] = returnType;
                if (args.Count % 2 == 0)
                    expectedArgumentReturnTypes[^1] = returnType;
            }
        }
        else if (TryResolveKnownXpaFunctionReturnType(functionName, args, out var knownReturnType))
        {
            returnType = knownReturnType;
        }

        if (!string.IsNullOrWhiteSpace(returnType))
        {
            Interlocked.Increment(ref _sourceFunctionCallReturnKnownCount);
            if (!string.IsNullOrWhiteSpace(normalizedFunction))
                _sourceFunctionCallReturnKnownCountByFunction.AddOrUpdate(normalizedFunction, 1, static (_, count) => count + 1);
        }

        for (var i = 0; i < args.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(expectedArgumentReturnTypes[i]) &&
                TryResolveXpaFunctionSourceArgumentReturnTypeContract(functionName, i, args.Count, out var expectedArgType))
                expectedArgumentReturnTypes[i] = expectedArgType;

            if (!string.IsNullOrWhiteSpace(expectedArgumentReturnTypes[i]))
                Interlocked.Increment(ref _sourceFunctionCallExpectedArgumentKnownCount);

            if (string.IsNullOrWhiteSpace(actualArgumentReturnTypes[i]) &&
                (isIfFunction || isCndRangeFunction || isRangeFunction || isRangeLocateAddFunction || isCaseFunction) &&
                depth < 32)
                actualArgumentReturnTypes[i] = ResolveSourceExpressionReturnType(args[i], task, dataObjects, depth + 1);

            if (!string.IsNullOrWhiteSpace(actualArgumentReturnTypes[i]))
                Interlocked.Increment(ref _sourceFunctionCallActualArgumentKnownCount);
        }

        var info = new SourceFunctionCallTypeInfo(
            normalizedFunction,
            returnType,
            expectedArgumentReturnTypes,
            actualArgumentReturnTypes);
        _sourceFunctionCallTypeInfoCache[cacheKey] = info;
        return info;
    }

    private static bool TryResolveNullableIfBranchSourceReturnType(
        string syntax,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        int depth,
        out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(syntax) || depth > 32)
            return false;

        var decoded = WebUtility.HtmlDecode(syntax).Trim();
        if (!TryParseFunctionCall(decoded, out var functionName, out var args) ||
            args.Count < 3 ||
            !string.Equals(NormalizeXpaFunctionContractName(functionName), "IF", StringComparison.Ordinal))
            return false;

        var trueIsNull = IsSourceNullExpression(args[1]);
        var falseIsNull = IsSourceNullExpression(args[2]);
        if (trueIsNull == falseIsNull)
            return false;

        var typedBranch = trueIsNull ? args[2] : args[1];
        returnType = NormalizeReturnTypeToken(ResolveSourceExpressionReturnType(typedBranch, task, dataObjects, depth + 1));
        return !string.IsNullOrWhiteSpace(returnType) &&
               !string.Equals(returnType, "object", StringComparison.Ordinal);
    }

    private static bool IsSourceNullExpression(string syntax)
    {
        if (string.IsNullOrWhiteSpace(syntax))
            return false;

        var decoded = WebUtility.HtmlDecode(syntax).Trim();
        if (string.Equals(decoded, "NULL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(decoded, "NULL()", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(decoded, "u.Null()", StringComparison.Ordinal))
            return true;

        return TryParseFunctionCall(decoded, out var functionName, out _) &&
               string.Equals(NormalizeXpaFunctionContractName(functionName), "NULL", StringComparison.Ordinal);
    }

    private static string BuildSourceFunctionCallTypeInfoCacheKey(TaskSemantic task, string functionName, IReadOnlyList<string> args)
    {
        var normalizedFunction = NormalizeXpaFunctionContractName(functionName);
        var sb = new StringBuilder(normalizedFunction.Length + 24 + args.Count * 16);
        sb.Append("fn|");
        sb.Append(task.Ordinal.ToString(CultureInfo.InvariantCulture));
        sb.Append('|');
        sb.Append(normalizedFunction);
        sb.Append('|');
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0)
                sb.Append('\u001f');
            sb.Append(args[i].Trim());
        }

        return sb.ToString();
    }

    private static string UnifySourceReturnTypes(string leftReturnType, string rightReturnType)
    {
        var left = CreateResolvedExpressionTypeInfo(leftReturnType);
        var right = CreateResolvedExpressionTypeInfo(rightReturnType);
        var unified = UnifyExpressionTypes(left, right);
        return unified.IsResolved ? unified.ReturnType : "";
    }

    private static bool ContainsTopLevelXpaBooleanExpression(string syntax)
    {
        if (string.IsNullOrWhiteSpace(syntax))
            return false;

        var depth = 0;
        for (var i = 0; i < syntax.Length; i++)
        {
            if (IsQuotedSegmentStart(syntax, i))
            {
                if (!TryReadQuotedSegmentEnd(syntax, i, out var quoteEnd))
                    return false;

                i = quoteEnd;
                continue;
            }

            var ch = syntax[i];
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

            if (depth != 0)
                continue;

            if (ch is '=' or '<' or '>')
                return true;

            if (IsTopLevelXpaBooleanWordAt(syntax, i, "AND") ||
                IsTopLevelXpaBooleanWordAt(syntax, i, "OR"))
                return true;
        }

        return false;
    }

    private static bool IsTopLevelXpaBooleanWordAt(string syntax, int index, string word)
    {
        if (index < 0 || index + word.Length > syntax.Length)
            return false;

        if (!string.Equals(syntax.Substring(index, word.Length), word, StringComparison.OrdinalIgnoreCase))
            return false;

        var before = index > 0 ? syntax[index - 1] : '\0';
        var afterIndex = index + word.Length;
        var after = afterIndex < syntax.Length ? syntax[afterIndex] : '\0';
        if (!IsIdentifierChar(before) && !IsIdentifierChar(after))
            return true;

        var attachedToLeft = index > 0 && !char.IsWhiteSpace(before);
        var attachedToRight = afterIndex < syntax.Length && !char.IsWhiteSpace(after);
        if (!attachedToLeft && !attachedToRight)
            return false;

        if (attachedToLeft && !CanSplitBooleanOperatorAfterLeftOperand(syntax, index))
            return false;

        return HasCompleteSourceBooleanLeftOperand(syntax, index) &&
               HasCompleteSourceBooleanRightOperand(syntax, afterIndex);
    }

    private static bool IsIdentifierChar(char ch)
        => char.IsLetterOrDigit(ch) || ch == '_';

    private static bool TryResolveSpecialExpressionReturnType(string code, out string returnType)
    {
        returnType = "";
        if (TryParseFunctionCall(code, out var wrappedFunctionName, out var wrappedArgs) &&
            wrappedArgs.Count == 1 &&
            IsTopLevelCall(wrappedFunctionName, "u.CastToByteArray"))
        {
            var inner = wrappedArgs[0].Trim();
            if (inner.StartsWith("new System.Uri(", StringComparison.Ordinal) &&
                inner.EndsWith(")", StringComparison.Ordinal))
            {
                returnType = "System.Uri";
                return true;
            }
        }

        if (code.EndsWith(".FullDbName", StringComparison.Ordinal) ||
            code.EndsWith(".DbName", StringComparison.Ordinal))
        {
            returnType = "Text";
            return true;
        }

        if (IsTopLevelCall(TryGetTopLevelFunctionName(code), "u.DataViewToDNDataTable"))
        {
            returnType = "System.Data.DataTable";
            return true;
        }

        return false;
    }

    private static bool ShouldDeferExpectedTypeShortcutForTypedExpression(string code, ExpressionEmissionContext context)
    {
        var expectedReturnType = ResolveReturnTypeForExpectedContext(context.Expected);
        if (!string.Equals(expectedReturnType, "byte[]", StringComparison.Ordinal))
            return false;

        var trimmed = StripRedundantOuterParentheses(code.Trim());
        if (IsSimpleIdentifierPath(trimmed))
            return true;

        var topLevelCall = TryGetTopLevelFunctionName(trimmed);
        if (IsTopLevelCall(topLevelCall, "u.DataViewToDNDataTable") ||
            IsObjectProducingExpression(topLevelCall))
            return true;

        return false;
    }

    private static string ResolveReturnTypeForExpectedContext(ExpectedTypeContext expected)
    {
        if (expected.IsBooleanCondition)
            return "Bool";

        var returnType = !string.IsNullOrWhiteSpace(expected.ReturnType)
            ? GetValueReturnType(expected.ReturnType)
            : "";
        if (!string.IsNullOrWhiteSpace(returnType))
            return returnType;

        return MapAttrObjToReturnType(expected.AttrObj);
    }

    private static string ResolveSimpleReturnTypeForExpressionAttribute(string? attr)
    {
        return (attr ?? "").ToUpperInvariant() switch
        {
            "N" => "Number",
            "D" => "Date",
            "T" => "Time",
            "B" => "Bool",
            // XPA attribute O is overloaded: it can be a binary/blob value, but it
            // also appears for CLR objects such as DataTable. Keep it unknown here
            // and let the typed sink or the existing resolver decide by context/code.
            "O" => "",
            "A" or "U" => "Text",
            _ => ""
        };
    }

    private static bool ShouldUseTypedExpressionEntryResolution(ExpressionEmissionContext context)
    {
        if (context.SinkKind == ExpressionSinkKind.BindValue)
            return context.TargetInfo is TargetValueInfo bindTarget &&
                   !bindTarget.IsDotNet &&
                   !string.IsNullOrWhiteSpace(bindTarget.AttrObj);

        if (context.SinkKind == ExpressionSinkKind.FilterComparison)
            return context.TargetInfo is TargetValueInfo filterTarget &&
                   !filterTarget.IsDotNet &&
                   !string.IsNullOrWhiteSpace(filterTarget.AttrObj);

        if (context.SinkKind == ExpressionSinkKind.RunArgument)
            return !context.PreserveBinding &&
                   !string.IsNullOrWhiteSpace(NormalizeReturnTypeToken(context.ParameterType));

        return context.SinkKind is
            ExpressionSinkKind.Default or
            ExpressionSinkKind.BooleanCondition or
            ExpressionSinkKind.ExpectedValue or
            ExpressionSinkKind.CallArgument or
            ExpressionSinkKind.ReturnValue or
            ExpressionSinkKind.Assignment or
            ExpressionSinkKind.ViewBinding or
            ExpressionSinkKind.DisplayExpression or
            ExpressionSinkKind.ProgramReference or
            ExpressionSinkKind.ProgramIndex or
            ExpressionSinkKind.IoArgument or
            ExpressionSinkKind.MessageText or
            ExpressionSinkKind.EvaluateStatement or
            ExpressionSinkKind.SqlExpression;
    }

    private static void ResetTypedExpressionTelemetryCounters()
    {
        Interlocked.Exchange(ref _typedExpressionResolveCount, 0);
        Interlocked.Exchange(ref _typedExpressionCacheHitCount, 0);
        Interlocked.Exchange(ref _typedExpressionIntrinsicKnownCount, 0);
        Interlocked.Exchange(ref _typedExpressionEffectiveKnownCount, 0);
        Interlocked.Exchange(ref _typedExpressionFallbackInferenceCount, 0);
        Interlocked.Exchange(ref _typedExpressionExpectedTypeShortcutCount, 0);
        Interlocked.Exchange(ref _typedExpressionRegisteredReturnTypeCount, 0);
        Interlocked.Exchange(ref _typedExpressionRegisteredLookupHitCount, 0);
        Interlocked.Exchange(ref _typedExpressionAmbiguousRegistrationSkipCount, 0);
        Interlocked.Exchange(ref _typedExpressionSourceTypeCacheHitCount, 0);
        Interlocked.Exchange(ref _typedExpressionSourceTypeCacheMissCount, 0);
        Interlocked.Exchange(ref _sourceFunctionCallAnalyzeCount, 0);
        Interlocked.Exchange(ref _sourceFunctionCallCacheHitCount, 0);
        Interlocked.Exchange(ref _sourceFunctionCallReturnKnownCount, 0);
        Interlocked.Exchange(ref _sourceFunctionCallExpectedArgumentKnownCount, 0);
        Interlocked.Exchange(ref _sourceFunctionCallActualArgumentKnownCount, 0);
        Interlocked.Exchange(ref _typedExpressionSourceDirectBindingHitCount, 0);
        Interlocked.Exchange(ref _typedExpressionSourceArithmeticHitCount, 0);
        _typedExpressionResolveCountBySink.Clear();
        _typedExpressionFallbackCountBySink.Clear();
        _typedExpressionExpectedShortcutCountBySink.Clear();
        _sourceFunctionCallAnalyzeCountByFunction.Clear();
        _sourceFunctionCallReturnKnownCountByFunction.Clear();
    }

    private static void LogTypedExpressionTelemetrySummary()
    {
        var resolved = Interlocked.Read(ref _typedExpressionResolveCount);
        var cacheHits = Interlocked.Read(ref _typedExpressionCacheHitCount);
        var intrinsicKnown = Interlocked.Read(ref _typedExpressionIntrinsicKnownCount);
        var effectiveKnown = Interlocked.Read(ref _typedExpressionEffectiveKnownCount);
        var fallbackInference = Interlocked.Read(ref _typedExpressionFallbackInferenceCount);
        var expectedTypeShortcuts = Interlocked.Read(ref _typedExpressionExpectedTypeShortcutCount);
        var registeredReturnTypes = Interlocked.Read(ref _typedExpressionRegisteredReturnTypeCount);
        var registeredLookupHits = Interlocked.Read(ref _typedExpressionRegisteredLookupHitCount);
        var ambiguousSkips = Interlocked.Read(ref _typedExpressionAmbiguousRegistrationSkipCount);
        var sourceTypeCacheHits = Interlocked.Read(ref _typedExpressionSourceTypeCacheHitCount);
        var sourceTypeCacheMisses = Interlocked.Read(ref _typedExpressionSourceTypeCacheMissCount);
        var sourceDirectBindingHits = Interlocked.Read(ref _typedExpressionSourceDirectBindingHitCount);
        var sourceArithmeticHits = Interlocked.Read(ref _typedExpressionSourceArithmeticHitCount);

        if (resolved == 0 &&
            cacheHits == 0 &&
            intrinsicKnown == 0 &&
            effectiveKnown == 0 &&
            fallbackInference == 0 &&
            expectedTypeShortcuts == 0 &&
            registeredReturnTypes == 0 &&
            registeredLookupHits == 0 &&
            ambiguousSkips == 0 &&
            sourceTypeCacheHits == 0 &&
            sourceTypeCacheMisses == 0 &&
            sourceDirectBindingHits == 0 &&
            sourceArithmeticHits == 0)
            return;

        ConversionTelemetry.Log(
            "TYPED_EXPR",
            string.Create(
                CultureInfo.InvariantCulture,
                $"summary resolved={resolved} cacheHits={cacheHits} intrinsicKnown={intrinsicKnown} effectiveKnown={effectiveKnown} fallbackInference={fallbackInference} expectedTypeShortcuts={expectedTypeShortcuts} registeredReturnTypes={registeredReturnTypes} registeredLookupHits={registeredLookupHits} ambiguousRegistrationSkips={ambiguousSkips} sourceTypeCacheHits={sourceTypeCacheHits} sourceTypeCacheMisses={sourceTypeCacheMisses} sourceDirectBindingHits={sourceDirectBindingHits} sourceArithmeticHits={sourceArithmeticHits}"));

        if (!_typedExpressionResolveCountBySink.IsEmpty)
            ConversionTelemetry.Log("TYPED_EXPR", $"sinks {FormatTypedExpressionSinkCounts(_typedExpressionResolveCountBySink)}");

        if (!_typedExpressionExpectedShortcutCountBySink.IsEmpty)
            ConversionTelemetry.Log("TYPED_EXPR", $"expected-shortcuts {FormatTypedExpressionSinkCounts(_typedExpressionExpectedShortcutCountBySink)}");

        if (!_typedExpressionFallbackCountBySink.IsEmpty)
            ConversionTelemetry.Log("TYPED_EXPR", $"fallbacks {FormatTypedExpressionSinkCounts(_typedExpressionFallbackCountBySink)}");

        LogSourceFunctionCallTypeInfoTelemetrySummary();
    }

    private static void LogSourceFunctionCallTypeInfoTelemetrySummary()
    {
        var analyzed = Interlocked.Read(ref _sourceFunctionCallAnalyzeCount);
        var cacheHits = Interlocked.Read(ref _sourceFunctionCallCacheHitCount);
        var returnKnown = Interlocked.Read(ref _sourceFunctionCallReturnKnownCount);
        var expectedArgumentKnown = Interlocked.Read(ref _sourceFunctionCallExpectedArgumentKnownCount);
        var actualArgumentKnown = Interlocked.Read(ref _sourceFunctionCallActualArgumentKnownCount);
        if (analyzed == 0 &&
            cacheHits == 0 &&
            returnKnown == 0 &&
            expectedArgumentKnown == 0 &&
            actualArgumentKnown == 0)
            return;

        ConversionTelemetry.Log(
            "SOURCE_FUNCTION_CALL_TYPE",
            string.Create(
                CultureInfo.InvariantCulture,
                $"analyzed={analyzed} cacheHits={cacheHits} returnKnown={returnKnown} expectedArgumentKnown={expectedArgumentKnown} actualArgumentKnown={actualArgumentKnown}"));

        if (!_sourceFunctionCallAnalyzeCountByFunction.IsEmpty)
            ConversionTelemetry.Log("SOURCE_FUNCTION_CALL_TYPE", $"functions {FormatTypedExpressionSinkCounts(_sourceFunctionCallAnalyzeCountByFunction)}");

        if (!_sourceFunctionCallReturnKnownCountByFunction.IsEmpty)
            ConversionTelemetry.Log("SOURCE_FUNCTION_CALL_TYPE", $"return-known {FormatTypedExpressionSinkCounts(_sourceFunctionCallReturnKnownCountByFunction)}");
    }

    private static string FormatTypedExpressionSinkCounts(ConcurrentDictionary<string, long> counts)
    {
        return string.Join(
            ",",
            counts
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key}:{kv.Value}")));
    }

    private static bool ShouldPreferContextualRawResolution(ExpressionEntrySemantic expr, ExpressionEmissionContext context)
    {
        if (!context.Expected.HasExpectation)
            return false;

        var originalAttr = NormalizeAttrObjKind(expr.Attribute);
        var expectedAttr = NormalizeAttrObjKind(context.Expected.AttrObj);
        if (string.IsNullOrWhiteSpace(expectedAttr))
        {
            var expectedReturnType = NormalizeReturnTypeToken(ResolveReturnTypeForExpectedContext(context.Expected));
            if (!string.IsNullOrWhiteSpace(expectedReturnType))
                expectedAttr = NormalizeAttrObjKind(MapReturnTypeToAttrObj(expectedReturnType));
        }
        if (string.IsNullOrWhiteSpace(originalAttr) || string.IsNullOrWhiteSpace(expectedAttr))
            return false;

        if (string.Equals(originalAttr, expectedAttr, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(originalAttr, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(expectedAttr, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(expectedAttr, "FIELD_DATE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(expectedAttr, "FIELD_TIME", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(expectedAttr, "FIELD_BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(expectedAttr, "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase);
        }

        // XPA attribute O/FIELD_BLOB is overloaded. It can represent real binary
        // values, but also CLR objects and textual environment values. In textual
        // contexts, avoid pre-normalizing the expression as Blob before the sink
        // has a chance to apply the expected type with richer context.
        if (!string.Equals(originalAttr, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(expectedAttr, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(expectedAttr, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(expectedAttr, "FIELD_DATE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(expectedAttr, "FIELD_TIME", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(expectedAttr, "FIELD_BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(expectedAttr, "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase);
    }
}

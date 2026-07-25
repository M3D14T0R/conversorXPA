using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveUpdateValueExpression(string rawWithValue, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects)
        => ResolveUpdateValueExpression(rawWithValue, t, dataObjects, default);

    private static string ResolveUpdateValueExpression(
        string rawWithValue,
        TaskSemantic t,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpressionEmissionContext context)
    {
        var contextToken = string.Create(
            CultureInfo.InvariantCulture,
            $"{context.SinkKind}|{context.Expected.ReturnType}|{context.Expected.AttrObj}|{context.ParameterType}|{context.PreserveBinding}|{GetExpressionContextBlobTargetCacheKey(context)}|{context.TargetInfo?.AttrObj}|{GetExpressionContextTargetMemberCacheKey(context)}|{context.TargetInfo?.IsBlob}|{context.TargetInfo?.IsArray}|{context.TargetInfo?.IsBoolean}|{context.TargetInfo?.IsNumeric}|{context.TargetInfo?.IsDotNet}");
        var cacheKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{t.Ordinal}|{rawWithValue}|{contextToken}");
        if (_updateValueExpressionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var value = rawWithValue;
        if (int.TryParse(rawWithValue, out var expId))
        {
            t.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expId, out var exp);
            if (exp is not null)
            {
                var translated = ResolveExpressionCode(rawWithValue, t, dataObjects, context);
                if (string.IsNullOrWhiteSpace(translated))
                {
                    var special = TryTranslateWholeExpressionSemantically(exp, t);
                    if (!string.IsNullOrWhiteSpace(special))
                    {
                        translated = EmitExpressionForContext(special, t, context);
                    }
                    else if (exp.IsStringLiteral)
                    {
                        translated = ResolveStringLiteralExpressionCode(exp);
                        if (context.SinkKind != default)
                            translated = EmitExpressionForContext(translated, t, context);
                    }
                    else
                    {
                        translated = ResolveTypedExpressionEntryCode(exp, t, dataObjects, context).Code;
                        if (string.Equals(exp.Attribute, "A", StringComparison.OrdinalIgnoreCase))
                        {
                            var trimmed = translated.Trim();
                            const string uriPrefix = "new System.Uri(";
                            if (trimmed.StartsWith(uriPrefix, StringComparison.Ordinal) && trimmed.EndsWith(")", StringComparison.Ordinal))
                                translated = trimmed.Substring(uriPrefix.Length, trimmed.Length - uriPrefix.Length - 1).Trim();
                        }
                        translated = EmitExpressionForContext(translated, t, context);
                    }
                }

                if (!string.IsNullOrWhiteSpace(translated) &&
                    context.SinkKind == ExpressionSinkKind.Assignment &&
                    context.TargetInfo is TargetValueInfo targetInfo &&
                    targetInfo.IsBlob)
                {
                    translated = EmitExpressionForContext(translated, t, context);
                }

                value = string.IsNullOrWhiteSpace(translated) ? exp.Syntax : translated;
            }
        }
        else if (context.Expected.HasExpectation)
        {
            var expectedReturnType = ResolveReturnTypeForExpectedContext(context.Expected);
            if (TryResolveDirectResourceReference(rawWithValue, t, out var directResourceReference))
            {
                value = directResourceReference;
            }
            else if (!string.IsNullOrWhiteSpace(expectedReturnType))
            {
                var translated = TranslateSourceFunctionArgument(rawWithValue, expectedReturnType, t, dataObjects, 0);
                if (!string.IsNullOrWhiteSpace(translated) &&
                    !string.Equals(translated.Trim(), rawWithValue.Trim(), StringComparison.Ordinal))
                    value = translated;
            }
        }
        _updateValueExpressionCache[cacheKey] = value;
        return value;
    }

    private static bool TryResolveDirectResourceReference(string rawValue, TaskSemantic task, out string reference)
    {
        reference = "";
        var trimmed = (rawValue ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Contains('(') ||
            trimmed.Contains(' ') ||
            trimmed.Contains('+') ||
            trimmed.Contains('-') ||
            trimmed.Contains('*') ||
            trimmed.Contains('/'))
            return false;

        var depth = 0;
        while (trimmed.StartsWith("_parent.", StringComparison.Ordinal))
        {
            depth++;
            trimmed = trimmed["_parent.".Length..];
        }

        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Contains('.', StringComparison.Ordinal))
            return false;

        var owner = task;
        if (depth > 0)
        {
            if (_allTasks is null)
                return false;

            var parentOrdinal = task.ParentOrdinal;
            for (var i = 0; i < depth; i++)
            {
                if (!parentOrdinal.HasValue)
                    return false;

                owner = GetTaskByOrdinal(parentOrdinal, _allTasks) ?? task;
                if (ReferenceEquals(owner, task))
                    return false;

                parentOrdinal = owner.ParentOrdinal;
            }
        }

        if (!TryResolveTaskResourceByNameOrLegacy(owner, trimmed, out var resource))
        {
            if (depth == 0 && TryResolveParentSourceResourceBinding(trimmed, task, out reference, out _))
                return true;

            return false;
        }

        reference = string.Concat(Enumerable.Repeat("_parent.", depth)) + ResolveTaskResourceMemberName(owner, resource);
        return true;
    }

    private static bool TryBuildUnsupportedUpdateExpressionComment(string rawWithValue, TaskSemantic t, out string comment)
    {
        comment = "";
        string? originalSyntax = null;
        if (int.TryParse(rawWithValue, out var expId))
        {
            if (t.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expId, out var exp) && exp is not null)
                originalSyntax = exp.Syntax;
        }
        else
        {
            originalSyntax = rawWithValue;
        }

        if (string.IsNullOrWhiteSpace(originalSyntax))
            return false;

        var unsupportedFunction = GetUnsupportedCompoundStorageFunctionName(originalSyntax);
        if (string.IsNullOrWhiteSpace(unsupportedFunction))
            return false;

        comment = $"/*Update //Couldn't write expression '{originalSyntax}' - Unknown function {unsupportedFunction}{Environment.NewLine}\t\t*/";
        return true;
    }

    private static string? GetUnsupportedCompoundStorageFunctionName(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var match = Regex.Match(expression, @"\b(BufSetUnicode|BufGetUnicode|BufSetBit|BufGetVector|BufSetVector|VariantGetVector|SharedValPack|SharedValUnPack)\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;
        return match.Groups[1].Value;
    }

    private static void EmitUpdateAssignmentStatements(StringBuilder sb, string pad, TaskUpdateDef update, string target, string value, TaskSemantic task, bool preferValueForResourceAssignments = false, bool suppressForcedUndo = false)
    {
        if (TryBuildUnsupportedUpdateExpressionComment(update.WithValue, task, out var comment))
        {
            sb.AppendLine($"{pad}{comment}");
            return;
        }
        sb.AppendLine($"{pad}{BuildUpdateAssignment(update, target, value, task, preferValueForResourceAssignments)}");
        var resource = ResolveTaskResourceForAssignment(task, update.Variable, target);
        if (update.ForcedUpdate && !suppressForcedUndo && !(resource is not null && IsDotNetTaskResource(resource)))
            sb.AppendLine($"{pad}u.DenyUndoFor({target});");
    }

    private static string BuildUpdateAssignment(TaskUpdateDef update, string target, string value, TaskSemantic task, bool preferValueForResourceAssignments = false)
    {
        var resource = ResolveTaskResourceForAssignment(task, update.Variable, target);
        var targetInfo = ResolveTargetValueInfo(task, update.Variable, target, resource);
        if (resource is null &&
            TryResolveDataViewMemberTargetValueInfo(task, target, out var declaredTargetInfo))
        {
            targetInfo = declaredTargetInfo;
        }

        string? resolvedColumnType = null;
        if (resource is not null)
        {
            var resourceOwnerTask = ResolveTaskOwnerFromTargetPath(task, target) ?? task;
            resolvedColumnType = ResolveTaskResourceColumnType(resource, _allFieldModels, resourceOwnerTask);
            if (!targetInfo.IsBlob)
                targetInfo = RecalibrateTargetInfoFromResolvedColumnType(targetInfo, resolvedColumnType);
        }
        if (TryResolveDirectResourceReference(value, task, out var directReference))
            value = directReference;
        if (TryEmitAssignmentValueFromKnownTypes(value, update, task, targetInfo, out var knownTypedValue))
            value = knownTypedValue;
        var topLevelCall = TryGetTopLevelFunctionName(value);

        if (IsArrayAssignmentTarget(task, target, resource, resolvedColumnType, targetInfo) &&
            IsSourceNullExpression(value))
            return $"{target}.Value = null;";

        if (update.Incremental)
        {
            var isBooleanTarget = targetInfo.IsBoolean;
            if (((resource is not null &&
                string.Equals(resource.AttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase) &&
                  target.IndexOf("ForceRefresh", StringComparison.OrdinalIgnoreCase) >= 0 &&
                  IsFormattingFunctionExpression(topLevelCall))) ||
                IsBooleanLiteralExpression(value) ||
                isBooleanTarget)
                return $"{target}.Value = {value};";
            return BuildIncrementalAssignment(target, value);
        }
        if (IsTopLevelCall(topLevelCall, "u.Blb2File"))
            return $"{target}.Value = {value};";
        if (IsTopLevelCall(topLevelCall, "u.IOCurr"))
            return $"{target}.Value = {value};";
        if (target.StartsWith("_parent.", StringComparison.Ordinal) &&
            targetInfo.IsDotNet &&
            resource is not null)
            return $"{target} = {EmitDotNetAssignmentExpression(value, resource.ObjectType)};";
        if (resource is not null && targetInfo.IsDotNet)
            return $"{target} = {EmitDotNetAssignmentExpression(value, resource.ObjectType)};";
        if (resource is not null &&
            !string.IsNullOrWhiteSpace(resolvedColumnType) &&
            resolvedColumnType.StartsWith("Types.", StringComparison.Ordinal))
            return $"{target}.Value = {value};";
        var isModelColumnTarget = IsSimpleMemberPath(target) &&
                                  !target.StartsWith("Application.", StringComparison.Ordinal);
        var isResourceVariable = resource is not null || target.StartsWith("_parent.", StringComparison.Ordinal);
        if (isModelColumnTarget)
        {
            if (TryEmitBlobWrappedNewClrExpression(value, out var modelClrAssignmentValue))
                value = modelClrAssignmentValue;
            return $"{target}.Value = {value};";
        }
        if (target.StartsWith("_parent.", StringComparison.Ordinal))
        {
            if (TryEmitBlobWrappedNewClrExpression(value, out var parentClrAssignmentValue))
                value = parentClrAssignmentValue;
            return $"{target}.Value = {value};";
        }
        if (isResourceVariable)
        {
            if (resource is not null && targetInfo.IsDotNet)
                return $"{target} = {EmitDotNetAssignmentExpression(value, resource.ObjectType)};";
            if (TryEmitBlobWrappedNewClrExpression(value, out var resourceClrAssignmentValue))
                value = resourceClrAssignmentValue;
            if (TryBuildBlobVariantAssignment(target, value, resource?.AttrObj, preferValueForResourceAssignments || update.ForcedUpdate, out var blobVariantAssignment))
                return blobVariantAssignment;
            if (IsTopLevelCall(topLevelCall, "u.Blb2File"))
                return $"{target}.Value = {value};";
            if (targetInfo.IsBlob &&
                !targetInfo.IsArray &&
                resource is not null)
            {
                if (string.Equals(value.Trim(), "null", StringComparison.OrdinalIgnoreCase))
                    return $"{target}.Value = null;";
                return $"{target}.Value = {value};";
            }
            if (resource is not null && targetInfo.IsBoolean)
                return $"{target}.Value = {value};";
            if (resource is not null)
            {
                if (targetInfo.IsArray)
                    return $"{target}.Value = {value};";
                if (preferValueForResourceAssignments || update.ForcedUpdate)
                    return $"{target}.Value = {value};";
                return $"{target}.SilentSet({value});";
            }
            return $"{target}.Value = {value};";
        }
        return $"{target}.Value = {value};";
    }

    private static bool IsArrayAssignmentTarget(
        TaskSemantic task,
        string target,
        TaskResourceColumnDef? resolvedResource,
        string? resolvedColumnType,
        TargetValueInfo targetInfo)
    {
        if (targetInfo.IsArray ||
            (!string.IsNullOrWhiteSpace(resolvedColumnType) &&
             resolvedColumnType.StartsWith("ArrayColumn<", StringComparison.Ordinal)))
        {
            return true;
        }

        var targetOwner = ResolveTaskOwnerFromTargetPath(task, target) ?? task;
        if (resolvedResource is not null &&
            IsTaskResourceArrayLike(resolvedResource, targetOwner))
            return true;

        if (TryResolveDataViewMemberTargetValueInfo(task, target, out var declaredTargetInfo) &&
            declaredTargetInfo.IsArray)
            return true;

        var declaredTargetResource = ResolveResourceByTargetPath(
            task,
            target,
            _allTasks ?? Array.Empty<TaskSemantic>());
        if (declaredTargetResource is not null &&
            IsTaskResourceArrayLike(declaredTargetResource, targetOwner))
            return true;

        return task.ResourcesSemantic.Ordered.Any(candidate =>
            string.Equals(
                ResolveTaskResourceMemberName(task, candidate),
                target,
                StringComparison.Ordinal) &&
            IsTaskResourceArrayLike(candidate, task));
    }

    private static bool TryEmitAssignmentValueFromKnownTypes(
        string value,
        TaskUpdateDef update,
        TaskSemantic task,
        TargetValueInfo targetInfo,
        out string emitted)
    {
        emitted = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var expectedReturnType = ResolveReturnTypeForExpectedContext(ExpectedTypeForTarget(targetInfo));
        if (string.IsNullOrWhiteSpace(expectedReturnType))
        {
            var attrObj = !string.IsNullOrWhiteSpace(targetInfo.AttrObj)
                ? targetInfo.AttrObj
                : targetInfo.ModelAttrObj ?? "";
            expectedReturnType = MapAttrObjToReturnType(attrObj);
        }

        var hasSourceReturnType =
            TryResolveKnownExpressionReturnTypeWithoutLegacy(task, value, out var sourceReturnType) &&
            !string.IsNullOrWhiteSpace(sourceReturnType);
        if (!hasSourceReturnType &&
            int.TryParse(update.WithValue, out var expressionOrdinal) &&
            task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expressionOrdinal, out var expression) &&
            expression is not null)
        {
            sourceReturnType = ResolveSourceReturnTypeForExpressionEntry(expression, task, _dataObjectsByOrdinal.Values.ToArray());
            if (string.IsNullOrWhiteSpace(sourceReturnType))
                sourceReturnType = ResolveSimpleReturnTypeForExpressionAttribute(expression.Attribute);
            hasSourceReturnType = !string.IsNullOrWhiteSpace(sourceReturnType);
        }

        if (string.IsNullOrWhiteSpace(expectedReturnType) || !hasSourceReturnType)
        {
            return false;
        }

        if (SourceReturnTypeMatchesExpected(sourceReturnType, expectedReturnType))
            return TryEmitClrObjectSourceForExpectedValue(value, expectedReturnType, out emitted);

        emitted = EmitFromReliableTypeEvidence(value, sourceReturnType, expectedReturnType, "assignment-target");
        return !string.IsNullOrWhiteSpace(emitted) &&
               !string.Equals(emitted.Trim(), value.Trim(), StringComparison.Ordinal);
    }

    private static bool TryEmitClrObjectSourceForExpectedValue(
        string value,
        string expectedReturnType,
        out string emitted)
    {
        emitted = "";
        var functionName = TryGetTopLevelFunctionName(value);
        if (string.IsNullOrWhiteSpace(functionName))
            return false;

        var normalizedFunction = NormalizeXpaFunctionContractName(functionName);
        if (!FunctionReturnsClrObjectBeforeXpaMaterialization(normalizedFunction))
            return false;

        emitted = EmitFromReliableTypeEvidence(value, "object", expectedReturnType, normalizedFunction);
        return !string.IsNullOrWhiteSpace(emitted) &&
               !string.Equals(emitted.Trim(), value.Trim(), StringComparison.Ordinal);
    }

    private static bool IsFormattingFunctionExpression(string? topLevelCall)
        => IsTopLevelCall(topLevelCall, "u.TStr") || IsTopLevelCall(topLevelCall, "u.MTStr");

}


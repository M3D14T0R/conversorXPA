using System;
using System.Collections.Generic;
using System.Linq;
using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveCallArgumentExpression(
        string argToken,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyDictionary<string, string> selectMap,
        IReadOnlyList<TaskSemantic>? allTasks = null,
        ExpressionEmissionContext context = default)
    {
        string ApplyArgumentContext(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            var effectiveContext = context.SinkKind == ExpressionSinkKind.Default && !context.Expected.HasExpectation
                ? CreateCallArgumentEmissionContext()
                : context;
            return EmitExpressionForContext(value, task, effectiveContext);
        }

        string ApplyKnownResourceArgumentContext(
            string value,
            TaskResourceColumnDef resource,
            TaskSemantic ownerTask)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                context.PreserveBinding ||
                !context.Expected.HasExpectation ||
                !TryResolveTaskResourceStrictReturnType(resource, ownerTask, out var sourceReturnType))
            {
                return value;
            }

            var sourceType = XpaExpressionTypeMap.FromReturnType(sourceReturnType);
            var destinationReturnType = ResolveReturnTypeForExpectedContext(context.Expected);
            var destinationType = XpaExpressionTypeMap.FromReturnType(destinationReturnType);
            if (sourceType == XpaType.Unknown ||
                destinationType == XpaType.Unknown ||
                !XpaExpressionTypeMap.TryApply(
                    new XpaTypedExpression(value, sourceReturnType, sourceType),
                    new XpaExpressionDestination(destinationReturnType, destinationType),
                    out var emitted))
            {
                return value;
            }

            return emitted.Code;
        }

        if (argToken.StartsWith("EXP:", StringComparison.OrdinalIgnoreCase))
        {
            var expId = argToken.Substring(4);
            var effectiveContext = context.SinkKind == ExpressionSinkKind.Default && !context.Expected.HasExpectation
                ? CreateCallArgumentEmissionContext()
                : context;
            var code = ResolveExpressionCode(expId, task, dataObjects, effectiveContext);
            return string.IsNullOrWhiteSpace(code) ? "null" : code;
        }
        if (selectMap.TryGetValue(argToken, out var expr))
        {
            var selectedResource = ResolveResourceByTargetPath(
                task,
                expr,
                allTasks ?? _allTasks ?? Array.Empty<TaskSemantic>());
            if (selectedResource is not null)
            {
                var owner = ResolveOwningTaskForResource(selectedResource) ?? task;
                return ApplyKnownResourceArgumentContext(expr, selectedResource, owner);
            }

            return ApplyArgumentContext(expr);
        }

        task.ResourcesSemantic.ByName.TryGetValue(argToken, out var taskResource);
        if (taskResource is not null)
        {
            var member = ResolveTaskResourceMemberName(task, taskResource);
            return ApplyKnownResourceArgumentContext(member, taskResource, task);
        }

        if (allTasks is not null)
        {
            var binding = ResolveExpressionOrdinalBinding(argToken, task, allTasks, dataObjects);
            if (!string.IsNullOrWhiteSpace(binding))
            {
                var boundResource = ResolveResourceByTargetPath(task, binding, allTasks);
                if (boundResource is not null)
                {
                    var owner = ResolveOwningTaskForResource(boundResource) ?? task;
                    return ApplyKnownResourceArgumentContext(binding, boundResource, owner);
                }

                return ApplyArgumentContext(binding);
            }
        }

        return "null";
    }

    private static ExpressionEmissionContext ResolveCallArgumentEmissionContext(
        IReadOnlyList<string>? expectedParameterTypes,
        IReadOnlyList<string>? expectedParameterDirections,
        int argIndex)
    {
        if (expectedParameterTypes is not null && argIndex < expectedParameterTypes.Count)
        {
            return CreateRunArgumentEmissionContext(
                expectedParameterTypes[argIndex],
                ShouldPreserveParameterBinding(expectedParameterDirections, argIndex));
        }

        return CreateCallArgumentEmissionContext();
    }

    private static IReadOnlyList<string> ResolveArgumentExpressions(
        IReadOnlyList<TaskArgumentDef>? argumentDefs,
        IReadOnlyList<string> fallbackArgumentTokens,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyDictionary<string, string> selectMap,
        IReadOnlyList<TaskSemantic>? allTasks = null,
        IReadOnlyList<string>? expectedParameterTypes = null,
        IReadOnlyList<string>? expectedParameterDirections = null)
    {
        if (argumentDefs is not null && argumentDefs.Count > 0)
        {
            var result = new List<string>();
            var argIndex = 0;
            foreach (var arg in argumentDefs)
            {
                if (arg.Skip == true)
                    continue;

                string? resolved = null;
                var argumentContext = ResolveCallArgumentEmissionContext(expectedParameterTypes, expectedParameterDirections, argIndex);
                if (arg.ExpressionId.HasValue)
                    resolved = ResolveExpressionCode(arg.ExpressionId.Value.ToString(), task, dataObjects, argumentContext);
                else if (arg.Exp.HasValue)
                    resolved = ResolveExpressionCode(arg.Exp.Value.ToString(), task, dataObjects, argumentContext);
                else if (!string.IsNullOrWhiteSpace(arg.Variable))
                    resolved = ResolveCallArgumentExpression(arg.Variable, task, dataObjects, selectMap, allTasks, argumentContext);

                if (!string.IsNullOrWhiteSpace(resolved))
                    result.Add(resolved);
                argIndex++;
            }

            if (result.Count > 0 || argumentDefs.Any(a => a.Skip == true))
                return result;
        }

        return fallbackArgumentTokens
            .Select((v, i) =>
            {
                var argumentContext = ResolveCallArgumentEmissionContext(expectedParameterTypes, expectedParameterDirections, i);
                return ResolveCallArgumentExpression(v, task, dataObjects, selectMap, allTasks, argumentContext);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static IReadOnlyList<string> ResolveCallArgumentExpressionsPreservingPositions(
        IReadOnlyList<TaskArgumentDef>? argumentDefs,
        IReadOnlyList<string> fallbackArgumentTokens,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyDictionary<string, string> selectMap,
        IReadOnlyList<TaskSemantic>? allTasks = null,
        IReadOnlyList<string>? expectedParameterTypes = null,
        IReadOnlyList<string>? expectedParameterDirections = null)
    {
        var cacheKey = BuildResolvedCallArgumentsCacheKey(
            task,
            argumentDefs,
            fallbackArgumentTokens,
            expectedParameterTypes,
            expectedParameterDirections);
        if (_resolvedCallArgumentsCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (argumentDefs is not null && argumentDefs.Count > 0)
        {
            var result = new List<string>();
            var lastNonNullIndex = -1;
            var argIndex = 0;
            foreach (var arg in argumentDefs)
            {
                if (arg.Skip == true)
                {
                    result.Add("null");
                    argIndex++;
                    continue;
                }

                string? resolved = null;
                var argumentContext = ResolveCallArgumentEmissionContext(expectedParameterTypes, expectedParameterDirections, argIndex);
                if (arg.ExpressionId.HasValue)
                    resolved = ResolveExpressionCode(arg.ExpressionId.Value.ToString(), task, dataObjects, argumentContext);
                else if (arg.Exp.HasValue)
                    resolved = ResolveExpressionCode(arg.Exp.Value.ToString(), task, dataObjects, argumentContext);
                else if (!string.IsNullOrWhiteSpace(arg.Variable))
                    resolved = ResolveCallArgumentExpression(arg.Variable, task, dataObjects, selectMap, allTasks, argumentContext);

                var value = string.IsNullOrWhiteSpace(resolved) ? "null" : resolved;
                result.Add(value);
                if (!string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
                    lastNonNullIndex = result.Count - 1;
                argIndex++;
            }

            if (lastNonNullIndex >= 0)
            {
                var trimmed = result.Take(lastNonNullIndex + 1).ToList();
                _resolvedCallArgumentsCache[cacheKey] = trimmed;
                return trimmed;
            }

            if (argumentDefs.Any(a => a.Skip == true))
            {
                _resolvedCallArgumentsCache[cacheKey] = result;
                return result;
            }
        }

        var resolvedFallback = ResolveArgumentExpressions(argumentDefs, fallbackArgumentTokens, task, dataObjects, selectMap, allTasks, expectedParameterTypes, expectedParameterDirections);
        _resolvedCallArgumentsCache[cacheKey] = resolvedFallback;
        return resolvedFallback;
    }

    private static string BuildResolvedCallArgumentsCacheKey(
        TaskSemantic task,
        IReadOnlyList<TaskArgumentDef>? argumentDefs,
        IReadOnlyList<string> fallbackArgumentTokens,
        IReadOnlyList<string>? expectedParameterTypes,
        IReadOnlyList<string>? expectedParameterDirections)
    {
        var argumentDefFingerprint = argumentDefs is null || argumentDefs.Count == 0
            ? "-"
            : string.Join(";", argumentDefs.Select(a => string.Join(",",
                a.Skip == true ? "skip" : "",
                a.ExpressionId?.ToString() ?? "",
                a.Exp?.ToString() ?? "",
                a.Variable ?? "")));

        var fallbackFingerprint = fallbackArgumentTokens.Count == 0
            ? "-"
            : string.Join(",", fallbackArgumentTokens);

        var expectedTypesFingerprint = expectedParameterTypes is null || expectedParameterTypes.Count == 0
            ? "-"
            : string.Join(",", expectedParameterTypes);

        var expectedDirectionsFingerprint = expectedParameterDirections is null || expectedParameterDirections.Count == 0
            ? "-"
            : string.Join(",", expectedParameterDirections);

        return string.Join("|",
            task.Ordinal.ToString(),
            argumentDefFingerprint,
            fallbackFingerprint,
            expectedTypesFingerprint,
            expectedDirectionsFingerprint);
    }

    private static IReadOnlyList<TaskResourceColumnDef> SelectTaskParameterResources(
        TaskSemantic targetTask,
        int desiredCount,
        ISet<int> declaredParameterIds,
        IReadOnlyList<Dictionary<string, int>>? observedEvidence = null)
    {
        if (desiredCount <= 0)
            return Array.Empty<TaskResourceColumnDef>();

        var resources = targetTask.ResourcesSemantic.Ordered.ToList();
        if (resources.Count <= desiredCount)
            return resources;

        var evidence = observedEvidence ?? BuildObservedTaskParameterEvidence(targetTask);
        var selectMappedParameters = GetDeclaredParameterResourcesFromVirtualSelects(targetTask, desiredCount);
        if (selectMappedParameters.Count >= desiredCount &&
            selectMappedParameters
                .Take(desiredCount)
                .Select((resource, index) => ParameterResourceMatchesObservedEvidence(
                    targetTask,
                    resource,
                    evidence.ElementAtOrDefault(index)))
                .All(matches => matches))
        {
            return selectMappedParameters.Take(desiredCount).ToList();
        }

        var selected = new List<TaskResourceColumnDef>(desiredCount);
        for (var i = 0; i < selectMappedParameters.Count && selected.Count < desiredCount; i++)
        {
            var resource = selectMappedParameters[i];
            if (ParameterResourceMatchesObservedEvidence(
                    targetTask,
                    resource,
                    evidence.ElementAtOrDefault(i)))
                selected.Add(resource);
        }
        var remainingResources = resources
            .Where(r => selected.All(s => s.Id != r.Id))
            .ToList();

        for (var position = selected.Count; position < desiredCount && remainingResources.Count > 0; position++)
        {
            TaskResourceColumnDef? best = null;
            var bestIndex = 0;
            var bestScore = int.MinValue;
            for (var i = 0; i < remainingResources.Count; i++)
            {
                var candidate = remainingResources[i];
                var score = ScoreParameterCandidate(targetTask, candidate, evidence.ElementAtOrDefault(position), declaredParameterIds);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                    bestIndex = i;
                }
            }

            best ??= remainingResources[0];
            selected.Add(best);
            remainingResources.RemoveAt(bestIndex);
        }

        return selected;
    }

    private static bool ParameterResourceMatchesObservedEvidence(
        TaskSemantic task,
        TaskResourceColumnDef resource,
        IReadOnlyDictionary<string, int>? evidence)
    {
        if (evidence is null || evidence.Count == 0)
            return true;

        var parameterType = ResolveParameterType(resource, task);
        return evidence.TryGetValue(parameterType, out var matches) && matches > 0;
    }

    private static IReadOnlyList<TaskResourceColumnDef> GetDeclaredParameterResourcesFromVirtualSelects(TaskSemantic targetTask, int desiredCount)
    {
        if (desiredCount <= 0)
            return Array.Empty<TaskResourceColumnDef>();

        var allowedNames = GetAllowedParameterSelectNames(targetTask);
        var orderedParameterSelects = targetTask.SelectsSemantic.Items
            .Where(s =>
                string.Equals(s.Type, "V", StringComparison.OrdinalIgnoreCase) &&
                s.IsParameter &&
                IsTaskRunParameterSelectOrigin(s))
            .Where(s => allowedNames.Count == 0 || allowedNames.Contains(s.Name))
            .ToList();

        if (orderedParameterSelects.Count == 0)
            return Array.Empty<TaskResourceColumnDef>();

        var result = new List<TaskResourceColumnDef>(desiredCount);
        var seenIds = new HashSet<int>();
        foreach (var select in orderedParameterSelects)
        {
            var resource = ResolveTaskResourceColumn(targetTask, select.ColumnId);
            if (resource is null || !seenIds.Add(resource.Id))
                continue;

            result.Add(resource);
            if (result.Count >= desiredCount)
                break;
        }

        return result;
    }

    private static IReadOnlyList<Dictionary<string, int>> BuildObservedTaskParameterEvidence(TaskSemantic targetTask)
    {
        if (_observedTaskParameterEvidenceCache.TryGetValue(targetTask.Ordinal, out var cached))
            return cached;

        if (!_observedTaskParameterEvidenceInProgress.Add(targetTask.Ordinal))
            return Array.Empty<Dictionary<string, int>>();

        if (_allTasks is null || _allTasks.Count == 0 || _dataObjectsByOrdinal.Count == 0)
        {
            _observedTaskParameterEvidenceInProgress.Remove(targetTask.Ordinal);
            return _observedTaskParameterEvidenceCache[targetTask.Ordinal] = Array.Empty<Dictionary<string, int>>();
        }

        try
        {
            var evidence = new List<Dictionary<string, int>>();
            if (!_incomingTaskCallsByTargetOrdinal.TryGetValue(targetTask.Ordinal, out var incomingCalls))
            {
                _observedTaskParameterEvidenceCache[targetTask.Ordinal] = evidence;
                return evidence;
            }

            var dataObjects = _dataObjectsByOrdinal.Values.ToList();
            foreach (var incoming in incomingCalls)
            {
                var caller = incoming.Caller;
                var call = incoming.Call;
                var selectMap = BuildSelectNameToExpressionMap(caller, dataObjects);
                // Keep evidence collection independent from target parameter inference.
                // Otherwise we recurse through GetTaskParameters(targetTask) while trying
                // to infer that same target's parameters, which is both expensive and noisy.
                var args = ResolveCallArgumentExpressionsPreservingPositions(
                    call.ArgumentDefs,
                    call.ArgumentVariables,
                    caller,
                    dataObjects,
                    selectMap,
                    _allTasks);
                for (var i = 0; i < args.Count; i++)
                {
                    var token = ResolveObservedArgumentTypeToken(caller, args[i]);
                    if (string.IsNullOrWhiteSpace(token))
                        continue;

                    while (evidence.Count <= i)
                        evidence.Add(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

                    evidence[i].TryGetValue(token, out var count);
                    evidence[i][token] = count + 1;
                }
            }

            _observedTaskParameterEvidenceCache[targetTask.Ordinal] = evidence;
            return evidence;
        }
        finally
        {
            _observedTaskParameterEvidenceInProgress.Remove(targetTask.Ordinal);
        }
    }

    private static int ScoreParameterCandidate(
        TaskSemantic task,
        TaskResourceColumnDef resource,
        Dictionary<string, int>? evidence,
        ISet<int> declaredParameterIds)
    {
        var score = 0;
        if (declaredParameterIds.Contains(resource.Id))
            score += 2;

        var member = ResolveTaskResourceMemberName(task, resource);
        if (member.StartsWith("p_", StringComparison.OrdinalIgnoreCase) ||
            member.StartsWith("pr_", StringComparison.OrdinalIgnoreCase) ||
            member.StartsWith("r_", StringComparison.OrdinalIgnoreCase) ||
            member.StartsWith("io_", StringComparison.OrdinalIgnoreCase))
            score += 1;
        else if (member.StartsWith("v_", StringComparison.OrdinalIgnoreCase))
            score -= 4;

        if (evidence is not null && evidence.Count > 0)
        {
            var parameterType = ResolveParameterType(resource, task);
            evidence.TryGetValue(parameterType, out var exactMatches);
            score += exactMatches * 6;

            if (parameterType == "BoolParameter")
            {
                evidence.TryGetValue("BoolParameter", out var boolMatches);
                score += boolMatches * 3;
            }

            if (parameterType == "ByteArrayParameter")
            {
                var nonBlobEvidence = evidence.Where(kvp => !string.Equals(kvp.Key, "ByteArrayParameter", StringComparison.OrdinalIgnoreCase)).Sum(kvp => kvp.Value);
                score -= nonBlobEvidence * (string.Equals(member, "r_erro", StringComparison.OrdinalIgnoreCase) ? 8 : 4);
            }
        }

        if (string.Equals(member, "r_erro", StringComparison.OrdinalIgnoreCase))
            score -= 3;
        if (string.Equals(member, "r_post", StringComparison.OrdinalIgnoreCase))
            score += 2;
        if (string.Equals(member, "p_metodo", StringComparison.OrdinalIgnoreCase))
            score += 2;

        return score;
    }

    private static string ResolveParameterDirection(string member)
    {
        if (member.StartsWith("r_", StringComparison.OrdinalIgnoreCase))
            return "Output";
        if (member.StartsWith("pr_", StringComparison.OrdinalIgnoreCase))
            return "InOut";
        if (member.StartsWith("io_", StringComparison.OrdinalIgnoreCase))
            return "InOut";
        return "Input";
    }

    private static string ResolveParameterType(TaskResourceColumnDef rc, TaskSemantic task)
    {
        var resolvedColumnType = ResolveTaskResourceColumnType(rc, _allFieldModels, task);
        if (IsDotNetTaskResource(rc))
            return resolvedColumnType;
        if (resolvedColumnType.StartsWith("ArrayColumn<", StringComparison.Ordinal))
            return $"ArrayParameter<{ResolveArrayColumnItemType(rc, _allFieldModels, task)}>";
        return rc.AttrObj switch
        {
            "FIELD_NUMERIC" => "NumberParameter",
            "FIELD_DATE" => "DateParameter",
            "FIELD_TIME" => "TimeParameter",
            "FIELD_BOOLEAN" => "BoolParameter",
            "FIELD_LOGICAL" => "BoolParameter",
            "FIELD_BLOB" => "ByteArrayParameter",
            _ => "TextParameter"
        };
    }

    private static ExpectedTypeContext ResolveRunArgumentExpectedType(TaskSemantic task, string argument)
    {
        var trimmed = StripRedundantOuterParentheses(argument?.Trim() ?? "");
        if (IsSimpleIdentifierPath(trimmed))
        {
            var resource = ResolveResourceByTargetPath(task, trimmed, _allTasks ?? Array.Empty<TaskSemantic>());
            if (resource is not null)
            {
                var ownerTask = ResolveOwningTaskForResource(resource) ?? task;
                var columnType = ResolveTaskResourceColumnType(resource, _allFieldModels, ownerTask);
                if (columnType.StartsWith("ArrayColumn<", StringComparison.Ordinal))
                {
                    var itemType = ResolveArrayColumnItemType(resource, _allFieldModels, ownerTask);
                    var arrayType = itemType switch
                    {
                        "Text" => "Text[]",
                        "Number" => "Number[]",
                        "Date" => "Date[]",
                        "Time" => "Time[]",
                        "Bool" => "Bool[]",
                        "byte[]" => "byte[][]",
                        _ => ""
                    };
                    if (!string.IsNullOrWhiteSpace(arrayType))
                        return ExpectedTypeForReturnType(arrayType);
                }

                var scalarReturnType = IsDotNetTaskResource(resource)
                    ? columnType
                    : MapAttrObjToReturnType(resource.AttrObj);
                if (!string.IsNullOrWhiteSpace(scalarReturnType))
                    return ExpectedTypeForReturnType(scalarReturnType);
            }
        }

        return ResolveExpectedTypeFromExpressionEvidence(task, trimmed);
    }

    private static bool HasPotentialRunArgumentAmbiguity(
        IReadOnlyList<string> args,
        IReadOnlyList<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)> parameters,
        TaskSemantic currentTask)
    {
        if (args.Count != parameters.Count)
            return true;

        for (var i = 0; i < args.Count && i < parameters.Count; i++)
        {
            var argument = args[i].Trim();
            if (string.Equals(argument, "null", StringComparison.OrdinalIgnoreCase))
                continue;

            var expected = ExpectedTypeForParameterType(parameters[i].ParameterType);
            if (!expected.HasExpectation)
                continue;

            var actual = ResolveRunArgumentExpectedType(currentTask, argument);
            if (actual.HasExpectation && !ExpectedTypesMatch(actual, expected))
            {
                if (!IsInputParameterDirection(parameters[i].ParameterDirection))
                    return true;

                if (i + 1 < parameters.Count)
                {
                    var nextExpected = ExpectedTypeForParameterType(parameters[i + 1].ParameterType);
                    if (nextExpected.HasExpectation && ExpectedTypesMatch(actual, nextExpected))
                        return true;
                }

                // A mismatch is itself sufficient evidence that an old XPA call
                // contract may contain skipped parameter slots.  Restricting the
                // alignment to values that matched only the immediately following
                // parameter left trailing and multi-slot shifts untouched.
                return true;
            }
        }

        return false;
    }

    private static bool ShouldRepairNonInputArgument(
        string argument,
        (string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection) parameter,
        TaskSemantic currentTask)
    {
        var trimmed = argument?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmed) || string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            return true;

        var expected = ExpectedTypeForParameterType(parameter.ParameterType);
        if (expected.HasExpectation)
        {
            var actual = ResolveRunArgumentExpectedType(currentTask, trimmed);
            if (actual.HasExpectation && !ExpectedTypesMatch(actual, expected))
                return true;
        }

        var terminalMember = ExtractExpressionTerminalMember(trimmed);
        if (string.IsNullOrWhiteSpace(terminalMember))
            return false;

        var direction = ResolveParameterDirection(terminalMember);
        if (!IsInputParameterDirection(direction))
            return false;

        return TokenOverlapScore(parameter.ColumnMember, terminalMember) <= 0;
    }

    private static string ResolveBestNonInputArgumentBinding(
        (string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection) parameter,
        TaskSemantic currentTask,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        var cacheKey = string.Join("|",
            currentTask.Ordinal.ToString(),
            parameter.ColumnMember,
            parameter.ParameterType,
            parameter.ParameterDirection);
        if (_nonInputArgumentBindingCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var candidates = GetNonInputArgumentCandidates(currentTask, allTasks);

        string bestExpression = "";
        var bestScore = int.MinValue;
        foreach (var candidate in candidates)
        {
            if (IsInputParameterDirection(candidate.Direction))
                continue;
            if (!string.Equals(candidate.ParameterType, parameter.ParameterType, StringComparison.OrdinalIgnoreCase))
                continue;

            var overlap = TokenOverlapScore(parameter.ColumnMember, candidate.Member);
            var exact = string.Equals(parameter.ColumnMember, candidate.Member, StringComparison.OrdinalIgnoreCase);
            if (!exact && overlap <= 0)
                continue;

            var score = exact ? 200 : overlap * 20;
            if (string.Equals(candidate.Direction, parameter.ParameterDirection, StringComparison.OrdinalIgnoreCase))
                score += 30;
            else if (!IsInputParameterDirection(candidate.Direction))
                score += 15;

            score -= candidate.Depth * 3;

            if (score > bestScore)
            {
                bestScore = score;
                bestExpression = candidate.Expression;
            }
        }

        _nonInputArgumentBindingCache[cacheKey] = bestExpression;
        return bestExpression;
    }

    private static IReadOnlyList<(string Expression, string Member, string Direction, string ParameterType, int Depth)> GetNonInputArgumentCandidates(
        TaskSemantic currentTask,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        if (_nonInputArgumentCandidatesByTaskOrdinal.TryGetValue(currentTask.Ordinal, out var cached))
            return cached;

        var candidates = new List<(string Expression, string Member, string Direction, string ParameterType, int Depth)>();
        foreach (var resource in currentTask.ResourcesSemantic.Ordered)
        {
            var member = ResolveTaskResourceMemberName(currentTask, resource);
            candidates.Add((member, member, ResolveParameterDirection(member), ResolveParameterType(resource, currentTask), 0));
        }

        var parentOrdinal = currentTask.ParentOrdinal;
        var parentPrefix = "_parent";
        var depth = 1;
        while (parentOrdinal.HasValue)
        {
            var parent = GetTaskByOrdinal(parentOrdinal, allTasks);
            if (parent is null)
                break;

            foreach (var resource in parent.ResourcesSemantic.Ordered)
            {
                var member = ResolveTaskResourceMemberName(parent, resource);
                candidates.Add(($"{parentPrefix}.{member}", member, ResolveParameterDirection(member), ResolveParameterType(resource, parent), depth));
            }

            parentOrdinal = parent.ParentOrdinal;
            parentPrefix += "._parent";
            depth++;
        }

        _nonInputArgumentCandidatesByTaskOrdinal[currentTask.Ordinal] = candidates;
        return candidates;
    }

    private static string ExtractExpressionTerminalMember(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return "";

        var trimmed = expression.Trim();
        var lastDot = trimmed.LastIndexOf('.');
        var terminal = lastDot >= 0 ? trimmed[(lastDot + 1)..] : trimmed;
        terminal = terminal.Trim();
        if (terminal.EndsWith("]", StringComparison.Ordinal))
        {
            var bracket = terminal.LastIndexOf('[');
            if (bracket >= 0)
                terminal = terminal[..bracket];
        }

        while (terminal.EndsWith(")", StringComparison.Ordinal))
        {
            var paren = terminal.LastIndexOf('(');
            if (paren < 0)
                break;
            terminal = terminal[..paren].Trim();
        }

        return terminal;
    }

    private static IReadOnlyList<string>? ResolveSnippetExpectedParameterTypes(IReadOnlyList<string>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return null;

        return parameters
            .Select(p => ResolveSnippetParameterClrType(p, "") switch
            {
                "System.IntPtr" or "IntPtr" => "System.IntPtr",
                var clrType => MapSnippetClrTypeToExpressionReturnType(clrType)
            })
            .ToList();
    }

    private static IReadOnlyList<string>? ResolveSnippetExpectedParameterDirections(IReadOnlyList<string>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return null;

        return parameters
            .Select(p => SnippetParameterRequiresRef(p) ? "Ref" : "Input")
            .ToList();
    }

    private static List<string> EmitSnippetArgumentValues(
        IReadOnlyList<string> argValues,
        IReadOnlyList<string>? snippetParameterTypes,
        TaskSemantic task)
    {
        var normalized = argValues.ToList();
        if (snippetParameterTypes is null || snippetParameterTypes.Count == 0)
            return normalized;

        for (var i = 0; i < normalized.Count && i < snippetParameterTypes.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(snippetParameterTypes[i]))
                continue;

            if (TryEmitExpectedArgumentFromReliableEvidence(
                    normalized[i],
                    snippetParameterTypes[i],
                    task,
                    out var emitted))
            {
                normalized[i] = emitted;
            }
        }

        return normalized;
    }

    private static bool IsSnippetTextParameter(string parameterType)
        => string.Equals(parameterType, "Text", StringComparison.Ordinal) ||
           string.Equals(parameterType, "string", StringComparison.Ordinal) ||
           string.Equals(parameterType, "String", StringComparison.Ordinal) ||
           string.Equals(parameterType, "System.String", StringComparison.Ordinal);
}

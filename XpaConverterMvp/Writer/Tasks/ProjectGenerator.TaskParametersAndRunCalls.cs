using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitRunMethod(StringBuilder sb, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks)
    {
        var parameters = GetTaskParameters(t);
        var returnType = ResolveTaskReturnType(t);
        var hasReturn = !string.IsNullOrWhiteSpace(returnType);
        if (TryEmitDirectInsertIfNotFoundRunMethod(sb, t, dataObjects, allTasks, parameters, hasReturn))
            return;
        if (parameters.Count == 0)
        {
            sb.AppendLine(hasReturn ? $"    public {returnType} Run()" : "    public void Run()");
            sb.AppendLine("    {");
            sb.AppendLine("        Execute();");
            if (hasReturn)
                sb.AppendLine("        return _taskResult;");
            sb.AppendLine("    }");
            return;
        }

        var signatureParts = BuildRunParameterSignatureParts(parameters);
        sb.AppendLine(hasReturn ? $"    public {returnType} Run({string.Join(", ", signatureParts)})" : $"    public void Run({string.Join(", ", signatureParts)})");
        sb.AppendLine("    {");
        var resourceByMember = t.ResourcesSemantic.Ordered
            .Select(rc => (Member: ResolveTaskResourceMemberName(t, rc), Resource: rc))
            .GroupBy(x => x.Member, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Resource, StringComparer.Ordinal);
        foreach (var p in parameters)
            foreach (var line in BuildRunParameterBindingStatements(t, p, resourceByMember))
                sb.AppendLine(line);
        sb.AppendLine("        Execute();");
        if (hasReturn)
        sb.AppendLine("        return _taskResult;");
        sb.AppendLine("    }");
    }

    private static bool TryEmitDirectInsertIfNotFoundRunMethod(
        StringBuilder sb,
        TaskSemantic t,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)> parameters,
        bool hasReturn)
    {
        if (hasReturn ||
            !string.Equals(ResolveBaseClass(t), "BusinessProcessBase", StringComparison.Ordinal) ||
            t.View.ShouldGenerate ||
            t.DataView.HasFrom ||
            t.DataView.TabCalls.Count > 0 ||
            GetPrintRowIos(t).Count > 0 ||
            GetTextIoReadRowIos(t).Count > 0)
            return false;

        var writeLinks = t.DataView.Links
            .Where(l => string.Equals(l.Mode, "W", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (writeLinks.Count != 1 || t.DataView.Links.Count != 1)
            return false;

        if (t.ResourceDbs.Count == 0 || !HasBusinessProcessLeaveRowWork(t))
            return false;

        var modelMembers = BuildModelMembers(t, dataObjects);
        var primaryObj = t.PrimaryDbObj ?? t.InformationDbObj;
        var linkMembers = BuildLinkMembers(t, dataObjects, modelMembers, primaryObj);
        var link = writeLinks[0];
        var linkMember = linkMembers.FirstOrDefault(m => ReferenceEquals(m.Link, link));
        if (string.IsNullOrWhiteSpace(linkMember.MemberName))
            return false;
        var linkSequence = linkMembers.IndexOf(linkMember) + 1;

        if (!_dataObjectsByOrdinal.TryGetValue(link.DbObj, out var target))
            return false;

        var relationAssignmentSelectByDbObjAndColumn = t.SelectsSemantic.Items
            .Where(s =>
                string.Equals(s.Type, "R", StringComparison.OrdinalIgnoreCase) &&
                s.SourceDbObj.HasValue &&
                s.AssignmentExpressionId.HasValue)
            .GroupBy(s => s.SourceDbObj!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(s => s.ColumnId)
                    .ToDictionary(gg => gg.Key, gg => gg.ToList()));
        var relationFilterSelectsByDbObj = t.SelectsSemantic.Items
            .Where(s =>
                string.Equals(s.Type, "R", StringComparison.OrdinalIgnoreCase) &&
                s.SourceDbObj.HasValue &&
                !s.AssignmentExpressionId.HasValue &&
                (s.HasLocate || s.HasRange))
            .GroupBy(s => s.SourceDbObj!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var condExpr = ResolveLinkConditionFromSelectAssignments(t, dataObjects, linkMember, target, linkSequence, relationAssignmentSelectByDbObjAndColumn);
        if (string.IsNullOrWhiteSpace(condExpr))
            condExpr = ResolveLinkConditionFromSelectFilters(t, dataObjects, linkMember, target, linkSequence, relationFilterSelectsByDbObj);
        condExpr = OverrideLinkConditionByReturnHint(t, dataObjects, linkMembers, linkSequence - 1, linkMember, target, condExpr);
        condExpr = NormalizeLinkConditionForWriteMode(link, condExpr);
        if (string.IsNullOrWhiteSpace(condExpr))
            return false;

        var resourceByMember = t.ResourcesSemantic.Ordered
            .Select(rc => (Member: ResolveTaskResourceMemberName(t, rc), Resource: rc))
            .GroupBy(x => x.Member, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Resource, StringComparer.Ordinal);

        if (parameters.Count == 0)
            sb.AppendLine("    public void Run()");
        else
        {
            var signatureParts = BuildRunParameterSignatureParts(parameters);
            sb.AppendLine($"    public void Run({string.Join(", ", signatureParts)})");
        }
        sb.AppendLine("    {");
        foreach (var p in parameters)
            foreach (var line in BuildRunParameterBindingStatements(t, p, resourceByMember))
                sb.AppendLine(line);
        sb.AppendLine($"        {linkMember.MemberName}.InsertIfNotFound(");
        sb.AppendLine($"            {condExpr},");
        sb.AppendLine("            __rowFound =>");
        sb.AppendLine("            {");
        EmitBusinessProcessLeaveRowBody(sb, t, dataObjects, allTasks, "                ", ResolveTaskClassName(t, allTasks), Stopwatch.StartNew());
        sb.AppendLine("            });");
        sb.AppendLine("    }");
        return true;
    }

    private static IEnumerable<string> BuildRunParameterBindingStatements(
        TaskSemantic task,
        (string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection) parameter,
        IReadOnlyDictionary<string, TaskResourceColumnDef> resourceByMember)
    {
        resourceByMember.TryGetValue(parameter.ColumnMember, out var resource);

        if (resource is not null &&
            parameter.ParameterType.StartsWith("ArrayParameter<", StringComparison.Ordinal))
        {
            yield return $"        BindParameter({parameter.ColumnMember}, {parameter.ParameterName});";
            yield break;
        }

        if (resource is not null &&
            string.Equals(parameter.ParameterType, "ByteArrayParameter", StringComparison.Ordinal))
        {
            var resolvedColumnType = ResolveTaskResourceColumnType(resource, _allFieldModels, task);
            if (!resolvedColumnType.StartsWith("ArrayColumn<", StringComparison.Ordinal))
                goto DefaultBind;

            var bridgeName = "__" + parameter.ParameterName + "Bridge";
            var itemType = ResolveArrayColumnItemType(resource, _allFieldModels, task);
            yield return $"        var {bridgeName} = new ByteArrayColumn();";
            yield return $"        BindParameter({bridgeName}, {parameter.ParameterName});";
            yield return $"        BindParameter({parameter.ColumnMember}, (ArrayParameter<{itemType}>){bridgeName});";
            yield break;
        }

    DefaultBind:
        yield return $"        BindParameter({parameter.ColumnMember}, {parameter.ParameterName});";
    }

    private static string[] BuildRunParameterSignatureParts(
        IReadOnlyList<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)> parameters)
    {
        return parameters
            .Select(p => $"{p.ParameterType} {p.ParameterName} = null")
            .ToArray();
    }

    private static string? ResolveTaskReturnType(TaskSemantic t)
    {
        var declared = t.ReturnValue?.MgAttr switch
        {
            "N" => "Number",
            "D" => "Date",
            "T" => "Time",
            "L" or "B" => "Bool",
            "O" => "byte[]",
            null or "" => null,
            _ => "Text"
        };

        if (!t.ReturnValueExpressionId.HasValue)
            return declared;

        if (!t.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(t.ReturnValueExpressionId.Value, out var entry) ||
            entry is null)
            return declared;

        var inferred = entry.Attribute switch
        {
            "N" => "Number",
            "D" => "Date",
            "T" => "Time",
            "L" or "B" => "Bool",
            "O" => "byte[]",
            "A" or "U" => "Text",
            _ => null
        };

        if (string.IsNullOrWhiteSpace(inferred))
            return declared;

        if (string.IsNullOrWhiteSpace(declared) ||
            string.Equals(declared, "byte[]", StringComparison.Ordinal))
            return inferred;

        return declared;
    }

    private static bool IsUsedAsSubform(TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks)
    {
        return allTasks.Any(parent => parent.View.SubformBindings.Any(b => b.TargetTaskOrdinal == task.Ordinal));
    }

    private static string BuildTaskRunStatement(
        TaskSemantic currentTask,
        TaskSemantic targetTask,
        string targetClass,
        string? operationType,
        string runArgs,
        TaskCallDef? call,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        var telemetryOwner = ResolveTaskClassName(currentTask, allTasks);
        var trimmedRunArgs = PrepareRunArgumentsForTarget(runArgs, currentTask, targetTask, allTasks, telemetryOwner);

        string BuildRunCallCore(string targetExpression)
        {
            var runCall = string.IsNullOrWhiteSpace(trimmedRunArgs)
                ? $"{targetExpression}.Run()"
                : $"{targetExpression}.Run({trimmedRunArgs})";
            if (!string.IsNullOrWhiteSpace(call?.ReturnVariable))
            {
                var target = ResolveUpdateTargetExpression(call.ReturnVariable!, currentTask, dataObjects, allTasks);
                if (!string.IsNullOrWhiteSpace(target))
                    return $"{target}.Value = {runCall};";
            }
            return runCall + ";";
        }

        var needsParentCtor =
            targetTask.ParentOrdinal == currentTask.Ordinal &&
            TaskNeedsParentReference(targetTask, allTasks, dataObjects);

        if (string.Equals(operationType, "P", StringComparison.OrdinalIgnoreCase) && targetTask.ParallelExecution)
            return BuildRunCallCore($"new {ToAsyncTypeReference(targetClass)}()");

        var targetBaseClass = ResolveBaseClass(targetTask);

        if (targetBaseClass == "BusinessProcessBase" &&
            string.IsNullOrWhiteSpace(call?.ReturnVariable))
        {
            var hasOwnView = targetTask.View.ShouldGenerate &&
                             !ShouldSuppressViewForBusinessProcessTextIo(targetTask);
            var shouldUseCachedInstance =
                !hasOwnView &&
                !needsParentCtor &&
                (targetTask.ResourceDbs.Any(db => db.Cache == true) ||
                 !string.IsNullOrWhiteSpace(targetTask.SourceComponent));
            if (shouldUseCachedInstance)
                return BuildRunCallCore($"Cached<{targetClass}>()");
            if (!hasOwnView && string.IsNullOrWhiteSpace(runArgs))
                return BuildRunCallCore($"Cached<{targetClass}>()");
            if (needsParentCtor)
                return BuildRunCallCore($"new {targetClass}(this)");
        }

        if (needsParentCtor)
            return BuildRunCallCore($"new {targetClass}(this)");

        if (string.Equals(operationType, "T", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(operationType, "P", StringComparison.OrdinalIgnoreCase))
            return BuildRunCallCore($"new {targetClass}()");

        return BuildRunCallCore($"new {targetClass}()");
    }

    private static string PrepareRunArgumentsForTarget(
        string runArgs,
        TaskSemantic currentTask,
        TaskSemantic targetTask,
        IReadOnlyList<TaskSemantic> allTasks,
        string telemetryOwner)
    {
        var targetName = ResolveTaskClassName(targetTask, allTasks);
        var cacheKey = string.Join("|",
            currentTask.Ordinal.ToString(),
            targetTask.Ordinal.ToString(),
            runArgs ?? "");
        if (_preparedRunArgumentsCache.TryGetValue(cacheKey, out var cached))
        {
            ConversionTelemetry.LogDuration("CALLPIPE", telemetryOwner, TimeSpan.Zero, $"section=\"align-run-args\" target={QuoteTelemetry(targetName)}");
            ConversionTelemetry.LogDuration("CALLPIPE", telemetryOwner, TimeSpan.Zero, $"section=\"repair-run-args\" target={QuoteTelemetry(targetName)}");
            ConversionTelemetry.LogDuration("CALLPIPE", telemetryOwner, TimeSpan.Zero, $"section=\"trim-run-args\" target={QuoteTelemetry(targetName)}");
            ConversionTelemetry.LogDuration("CALLPIPE", telemetryOwner, TimeSpan.Zero, $"section=\"coerce-run-args\" target={QuoteTelemetry(targetName)}");
            return cached;
        }

        var alignStopwatch = Stopwatch.StartNew();
        var trimmedRunArgs = AlignRunArgumentsForTarget(runArgs, currentTask, targetTask, allTasks);
        ConversionTelemetry.LogDuration("CALLPIPE", telemetryOwner, alignStopwatch.Elapsed, $"section=\"align-run-args\" target={QuoteTelemetry(targetName)}");

        var repairStopwatch = Stopwatch.StartNew();
        trimmedRunArgs = RepairRunArgumentsForTarget(trimmedRunArgs, currentTask, targetTask, allTasks);
        ConversionTelemetry.LogDuration("CALLPIPE", telemetryOwner, repairStopwatch.Elapsed, $"section=\"repair-run-args\" target={QuoteTelemetry(targetName)}");

        var trimStopwatch = Stopwatch.StartNew();
        trimmedRunArgs = TrimRunArgumentsForTarget(trimmedRunArgs, targetTask);
        ConversionTelemetry.LogDuration("CALLPIPE", telemetryOwner, trimStopwatch.Elapsed, $"section=\"trim-run-args\" target={QuoteTelemetry(targetName)}");

        var coerceStopwatch = Stopwatch.StartNew();
        var preCoerceRunArgs = trimmedRunArgs;
        trimmedRunArgs = CoerceRunArgumentsForTarget(trimmedRunArgs, currentTask, targetTask);
        var coercedArgumentCount = CountChangedRunArguments(preCoerceRunArgs, trimmedRunArgs);
        ConversionTelemetry.LogDuration(
            "CALLPIPE",
            telemetryOwner,
            coerceStopwatch.Elapsed,
            $"section=\"coerce-run-args\" target={QuoteTelemetry(targetName)} changed={coercedArgumentCount}");

        _preparedRunArgumentsCache[cacheKey] = trimmedRunArgs;
        return trimmedRunArgs;
    }

    private static string AlignRunArgumentsForTarget(string runArgs, TaskSemantic currentTask, TaskSemantic targetTask, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (string.IsNullOrWhiteSpace(runArgs))
            return runArgs;

        var args = SplitTopLevelArguments(runArgs);
        if (args.Count == 0)
            return runArgs;

        var parameters = GetTaskParameters(targetTask);
        if (parameters.Count == 0)
            return runArgs;

        var optionalStartIndex = ResolveOptionalRunParameterStartIndex(targetTask, allTasks, parameters.Count);
        if (optionalStartIndex > 0 &&
            optionalStartIndex < parameters.Count &&
            args.Count >= optionalStartIndex &&
            LeadingRunArgumentsMatchParameters(args, parameters, optionalStartIndex, currentTask))
        {
            var alignedPrefix = new List<string>(args.Take(optionalStartIndex).Select(a => a.Trim()));
            var suffixArgs = args.Skip(optionalStartIndex).Select(a => a.Trim()).ToList();
            var suffixParameters = parameters.Skip(optionalStartIndex).ToList();
            if (suffixArgs.Count > 0 && suffixParameters.Count > 0)
                alignedPrefix.AddRange(AlignOptionalArgumentSuffix(suffixArgs, suffixParameters, currentTask));
            return string.Join(", ", alignedPrefix);
        }

        if (args.Count == parameters.Count &&
            !HasPotentialRunArgumentAmbiguity(args, parameters, currentTask))
            return string.Join(", ", args);

        var hasAmbiguity = HasPotentialRunArgumentAmbiguity(args, parameters, currentTask);
        if (ShouldUseCheapRunAlignment(args.Count, parameters.Count) && !hasAmbiguity)
            return AlignRunArgumentsWithCheapFallback(args, parameters, currentTask, targetTask, allTasks);

        var aligned = AlignArgumentSequenceForParameters(args.Select(a => a.Trim()).ToList(), parameters, currentTask);
        return string.Join(", ", aligned);
    }

    private static bool LeadingRunArgumentsMatchParameters(
        IReadOnlyList<string> args,
        IReadOnlyList<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)> parameters,
        int count,
        TaskSemantic currentTask)
    {
        var limit = Math.Min(count, Math.Min(args.Count, parameters.Count));
        for (var i = 0; i < limit; i++)
        {
            var argument = args[i].Trim();
            if (string.Equals(argument, "null", StringComparison.OrdinalIgnoreCase))
                continue;

            var expected = ExpectedTypeForParameterType(parameters[i].ParameterType);
            if (!expected.HasExpectation)
                continue;

            var actual = ResolveExpectedTypeFromExpressionEvidence(currentTask, argument);
            if (actual.HasExpectation && !ExpectedTypesMatch(actual, expected))
                return false;
        }

        return true;
    }

    private static bool ShouldUseCheapRunAlignment(int argCount, int paramCount)
    {
        var larger = Math.Max(argCount, paramCount);
        return larger >= 18 || (argCount * paramCount) >= 220;
    }

    private static string AlignRunArgumentsWithCheapFallback(
        IReadOnlyList<string> args,
        IReadOnlyList<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)> parameters,
        TaskSemantic currentTask,
        TaskSemantic targetTask,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        var optionalStartIndex = ResolveOptionalRunParameterStartIndex(targetTask, allTasks, parameters.Count);
        if (optionalStartIndex > 0 &&
            optionalStartIndex < parameters.Count)
        {
            var prefixCount = Math.Min(optionalStartIndex, Math.Min(args.Count, parameters.Count));
            var aligned = new List<string>(args.Take(prefixCount).Select(a => a.Trim()));
            var suffixArgs = args.Skip(prefixCount).Select(a => a.Trim()).ToList();
            var suffixParameters = parameters.Skip(prefixCount).ToList();
            if (suffixArgs.Count > 0 && suffixParameters.Count > 0)
            {
                aligned.AddRange(AlignOptionalArgumentSuffix(suffixArgs, suffixParameters, currentTask));
                return string.Join(", ", aligned);
            }
        }

        if (args.Count <= parameters.Count)
        {
            var padded = args.Select(a => a.Trim()).ToList();
            while (padded.Count < parameters.Count)
                padded.Add("null");
            return string.Join(", ", padded);
        }

        return string.Join(", ", args.Take(parameters.Count).Select(a => a.Trim()));
    }

    private static IReadOnlyList<string> InsertMissingNullRunPlaceholders(
        IReadOnlyList<string> args,
        IReadOnlyList<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)> parameters,
        TaskSemantic currentTask)
    {
        if (args.Count >= parameters.Count)
            return args;

        var working = args.ToList();
        while (working.Count < parameters.Count)
        {
            var bestIndex = working.Count;
            var bestScore = int.MaxValue;

            for (var insertAt = 0; insertAt <= working.Count; insertAt++)
            {
                var candidate = working.ToList();
                candidate.Insert(insertAt, "null");
                var score = ScoreAlignedRunArguments(candidate, parameters, currentTask);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = insertAt;
                }
            }

            working.Insert(bestIndex, "null");
        }

        return working;
    }

    private static int ScoreAlignedRunArguments(
        IReadOnlyList<string> args,
        IReadOnlyList<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)> parameters,
        TaskSemantic currentTask)
    {
        var score = 0;
        var count = Math.Min(args.Count, parameters.Count);
        for (var i = 0; i < count; i++)
            score += GetArgumentMatchCost(args[i], parameters[i], currentTask);

        if (args.Count > parameters.Count)
            score += (args.Count - parameters.Count) * 12;
        else if (parameters.Count > args.Count)
            score += (parameters.Count - args.Count) * 6;

        return score;
    }

    private static string TrimRunArgumentsForTarget(string runArgs, TaskSemantic targetTask)
    {
        if (string.IsNullOrWhiteSpace(runArgs))
            return "";

        var targetParameterCount = GetTaskParameters(targetTask).Count;
        if (targetParameterCount <= 0)
            return "";

        var args = SplitTopLevelArguments(runArgs);
        if (args.Count <= targetParameterCount)
            return runArgs;

        return string.Join(", ", args.Take(targetParameterCount));
    }

    private static string RepairRunArgumentsForTarget(string runArgs, TaskSemantic currentTask, TaskSemantic targetTask, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (string.IsNullOrWhiteSpace(runArgs))
            return runArgs;

        var args = SplitTopLevelArguments(runArgs).Select(a => a.Trim()).ToList();
        if (args.Count == 0)
            return runArgs;

        var parameters = GetTaskParameters(targetTask);
        if (parameters.Count == 0)
            return runArgs;

        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            if (IsInputParameterDirection(parameter.ParameterDirection))
                continue;

            while (args.Count <= i)
                args.Add("null");

            var currentArg = args[i];
            if (!ShouldRepairNonInputArgument(currentArg, parameter, currentTask))
                continue;

            var replacement = ResolveBestNonInputArgumentBinding(parameter, currentTask, allTasks);
            if (!string.IsNullOrWhiteSpace(replacement))
                args[i] = replacement;
        }

        var lastNonNull = args.FindLastIndex(a => !string.Equals(a, "null", StringComparison.OrdinalIgnoreCase));
        if (lastNonNull >= 0 && lastNonNull + 1 < args.Count)
            args = args.Take(lastNonNull + 1).ToList();

        return string.Join(", ", args);
    }

    private static string BuildRunCallForExternalTarget(string targetClass, string runArgs)
    {
        string BuildRunCallCore(string ctor)
        {
            var runCall = string.IsNullOrWhiteSpace(runArgs) ? $"{ctor}.Run()" : $"{ctor}.Run({runArgs})";
            return runCall + ";";
        }

        return BuildRunCallCore($"Cached<{targetClass}>()");
    }

    private static List<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)> GetTaskParameters(TaskSemantic t)
    {
        if (_taskParametersCache.TryGetValue(t.Ordinal, out var cached))
            return cached;

        var handlerParameterIds = t.HandlersSemantic.Items
            .SelectMany(h => h.ParameterColumnIds)
            .Distinct()
            .ToHashSet();
        var paramIds = t.SelectsSemantic.Items
            .Where(s =>
                s.IsParameter &&
                !s.IsFunctionSelect &&
                !string.Equals(s.OriginLevel, "H", StringComparison.OrdinalIgnoreCase) &&
                !handlerParameterIds.Contains(s.ColumnId))
            .Select(s => s.ColumnId)
            .Distinct()
            .ToHashSet();
        var inferredParameterCount = 0;
        if (paramIds.Count == 0)
            inferredParameterCount = InferIncomingParameterCount(t);

        var maxParams = t.DeclaredParameterCount.GetValueOrDefault(0);
        if (maxParams < 0)
            maxParams = 0;

        var desiredParameterCount = Math.Max(paramIds.Count, inferredParameterCount);

        if (maxParams > 0)
            desiredParameterCount = desiredParameterCount > 0
                ? Math.Min(desiredParameterCount, maxParams)
                : maxParams;

        IReadOnlyList<Dictionary<string, int>> observedEvidence = Array.Empty<Dictionary<string, int>>();
        var needObservedEvidence =
            desiredParameterCount == 0 ||
            paramIds.Count == 0 ||
            (maxParams > 0 && desiredParameterCount < maxParams);
        if (needObservedEvidence)
        {
            observedEvidence = BuildObservedTaskParameterEvidence(t);
            var observedParameterCount = observedEvidence.Count;
            desiredParameterCount = Math.Max(desiredParameterCount, observedParameterCount);
            if (maxParams > 0)
                desiredParameterCount = Math.Min(desiredParameterCount, maxParams);
        }

        var selectedResources = desiredParameterCount > 0
            ? SelectTaskParameterResources(t, desiredParameterCount, paramIds, observedEvidence)
            : Array.Empty<TaskResourceColumnDef>();

        var result = new List<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)>();
        foreach (var rc in selectedResources)
        {
            var member = ResolveTaskResourceMemberName(t, rc);
            var pType = ResolveParameterType(rc, t);
            var pName = "p" + member;
            var pDirection = ResolveParameterDirection(member);
            result.Add((member, pType, pName, pDirection));
        }
        _taskParametersCache[t.Ordinal] = result;
        return result;
    }

}


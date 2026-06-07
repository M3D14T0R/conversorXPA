using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private readonly record struct StructuredRowItem(TaskRowActionDef? Action, TaskFormIoDef? Io);

    private static bool CanEmitStructuredActionBody(
        IReadOnlyList<TaskRowActionDef> actions,
        IReadOnlyList<TaskBlockDef> blocks,
        IReadOnlyList<TaskEndBlockDef> endBlocks)
    {
        return actions.Count > 0 &&
               blocks.Count > 0 &&
               endBlocks.Count > 0 &&
               blocks.All(b => b.LogicLineIndex.HasValue) &&
               endBlocks.All(b => b.LogicLineIndex.HasValue) &&
               blocks.All(b => string.Equals(b.Type, "I", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(b.Type, "E", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(b.Type, "L", StringComparison.OrdinalIgnoreCase));
    }

    private static bool CanEmitStructuredRowLogic(TaskRowLogicDef row)
    {
        return CanEmitStructuredActionBody(row.Actions, row.Blocks, row.EndBlocks);
    }

    private static void EmitStructuredRowLogic(
        StringBuilder sb,
        TaskRowLogicDef row,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        string pad)
    {
        EmitStructuredActionBody(sb, row.Actions, row.Blocks, row.EndBlocks, task, dataObjects, allTasks, pad);
    }

    private static void EmitStructuredActionBody(
        StringBuilder sb,
        IReadOnlyList<TaskRowActionDef> actions,
        IReadOnlyList<TaskBlockDef> blocks,
        IReadOnlyList<TaskEndBlockDef> endBlocks,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        string pad,
        IReadOnlyList<TaskFormIoDef>? formIos = null,
        IReadOnlyDictionary<int, string>? writeCallMap = null,
        IReadOnlyDictionary<int, string>? readCallMap = null)
    {
        var itemsByLine = new Dictionary<int, List<StructuredRowItem>>();
        foreach (var action in actions)
            AddStructuredRowItem(itemsByLine, ExtractLogicUnitLineIndex(action.XmlTrace) ?? int.MaxValue, new StructuredRowItem(action, null));
        if (formIos is not null)
        {
            foreach (var io in formIos)
                AddStructuredRowItem(itemsByLine, ExtractLogicUnitLineIndex(io.XmlTrace) ?? int.MaxValue, new StructuredRowItem(null, io));
        }
        var blocksByLine = blocks
            .Where(b => b.LogicLineIndex.HasValue)
            .ToDictionary(b => b.LogicLineIndex!.Value);
        var endBlockLines = endBlocks
            .Where(b => b.LogicLineIndex.HasValue)
            .Select(b => b.LogicLineIndex!.Value)
            .ToHashSet();
        var orderedLines = itemsByLine.Keys
            .Concat(blocksByLine.Keys)
            .Concat(endBlockLines)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        var index = 0;
        EmitStructuredRowLogicRange(sb, orderedLines, ref index, itemsByLine, blocksByLine, endBlockLines, task, dataObjects, allTasks, pad, null, null, null, false, writeCallMap, readCallMap);
    }

    private static void AddStructuredRowItem(
        IDictionary<int, List<StructuredRowItem>> itemsByLine,
        int line,
        StructuredRowItem item)
    {
        if (!itemsByLine.TryGetValue(line, out var items))
        {
            items = new List<StructuredRowItem>();
            itemsByLine[line] = items;
        }
        items.Add(item);
    }

    private static bool EmitStructuredRowLogicRange(
        StringBuilder sb,
        IReadOnlyList<int> orderedLines,
        ref int index,
        IReadOnlyDictionary<int, List<StructuredRowItem>> itemsByLine,
        IReadOnlyDictionary<int, TaskBlockDef> blocksByLine,
        ISet<int> endBlockLines,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        string pad,
        string? currentBlockCondition,
        string? currentImmediateCondition,
        int? currentLoopConditionId,
        bool stopAtElse,
        IReadOnlyDictionary<int, string>? writeCallMap,
        IReadOnlyDictionary<int, string>? readCallMap)
    {
        while (index < orderedLines.Count)
        {
            var line = orderedLines[index];
            if (endBlockLines.Contains(line))
            {
                index++;
                return false;
            }

            if (blocksByLine.TryGetValue(line, out var block))
            {
                if (string.Equals(block.Type, "E", StringComparison.OrdinalIgnoreCase))
                {
                    if (stopAtElse)
                        return true;
                    if (block.ConditionExpressionId.HasValue)
                    {
                        index++;
                        if (!HasExecutableStructuredContent(orderedLines, index, itemsByLine, blocksByLine, endBlockLines, stopAtElse: false))
                            continue;
                        var cond = ResolveConditionExpressionCode(block.ConditionExpressionId.Value.ToString(), task, dataObjects);
                        if (string.IsNullOrWhiteSpace(cond))
                            cond = "true";
                        var effectiveCond = CombineStructuredBlockCondition(currentBlockCondition, cond);

                        sb.AppendLine($"{pad}if ({cond})");
                        sb.AppendLine($"{pad}{{");
                        EmitStructuredRowLogicRange(sb, orderedLines, ref index, itemsByLine, blocksByLine, endBlockLines, task, dataObjects, allTasks, pad + "    ", effectiveCond, cond, currentLoopConditionId, false, writeCallMap, readCallMap);
                        sb.AppendLine($"{pad}}}");
                        continue;
                    }
                    index++;
                    continue;
                }

                if (string.Equals(block.Type, "I", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    if (!HasExecutableStructuredContent(orderedLines, index, itemsByLine, blocksByLine, endBlockLines, stopAtElse: true))
                    {
                        SkipStructuredRowLogicRange(orderedLines, ref index, blocksByLine, endBlockLines, stopAtElse: true);
                        continue;
                    }
                    var cond = block.ConditionExpressionId.HasValue
                        ? ResolveConditionExpressionCode(block.ConditionExpressionId.Value.ToString(), task, dataObjects)
                        : "";
                    if (string.IsNullOrWhiteSpace(cond))
                        cond = "true";
                    var effectiveCond = CombineStructuredBlockCondition(currentBlockCondition, cond);

                    if (!string.IsNullOrWhiteSpace(currentImmediateCondition) &&
                        AreEquivalentStructuredConditions(cond, currentImmediateCondition) &&
                        !HasTopLevelElseBeforeStructuredEnd(orderedLines, index, blocksByLine, endBlockLines))
                    {
                        EmitStructuredRowLogicRange(sb, orderedLines, ref index, itemsByLine, blocksByLine, endBlockLines, task, dataObjects, allTasks, pad, currentBlockCondition, currentImmediateCondition, currentLoopConditionId, true, writeCallMap, readCallMap);
                        continue;
                    }

                    sb.AppendLine($"{pad}if ({cond})");
                    sb.AppendLine($"{pad}{{");
                    var foundElse = EmitStructuredRowLogicRange(sb, orderedLines, ref index, itemsByLine, blocksByLine, endBlockLines, task, dataObjects, allTasks, pad + "    ", effectiveCond, cond, currentLoopConditionId, true, writeCallMap, readCallMap);
                    sb.AppendLine($"{pad}}}");

                    if (foundElse && index < orderedLines.Count && blocksByLine.TryGetValue(orderedLines[index], out var elseBlock) && string.Equals(elseBlock.Type, "E", StringComparison.OrdinalIgnoreCase))
                    {
                        index++;
                        var elseCond = elseBlock.ConditionExpressionId.HasValue
                            ? ResolveConditionExpressionCode(elseBlock.ConditionExpressionId.Value.ToString(), task, dataObjects)
                            : "";
                        var effectiveElseCond = !string.IsNullOrWhiteSpace(elseCond)
                            ? CombineStructuredBlockCondition(currentBlockCondition, elseCond)
                            : CombineStructuredBlockCondition(currentBlockCondition, $"u.Not({cond})");
                        if (!string.IsNullOrWhiteSpace(elseCond))
                            sb.AppendLine($"{pad}else if ({elseCond})");
                        else
                            sb.AppendLine($"{pad}else");
                        sb.AppendLine($"{pad}{{");
                        EmitStructuredRowLogicRange(sb, orderedLines, ref index, itemsByLine, blocksByLine, endBlockLines, task, dataObjects, allTasks, pad + "    ", effectiveElseCond, elseCond, currentLoopConditionId, false, writeCallMap, readCallMap);
                        sb.AppendLine($"{pad}}}");
                    }

                    continue;
                }

                if (string.Equals(block.Type, "L", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    if (!HasExecutableStructuredContent(orderedLines, index, itemsByLine, blocksByLine, endBlockLines, stopAtElse: false))
                    {
                        SkipStructuredRowLogicRange(orderedLines, ref index, blocksByLine, endBlockLines, stopAtElse: false);
                        continue;
                    }
                    var cond = block.ConditionExpressionId.HasValue
                        ? ResolveConditionExpressionCode(block.ConditionExpressionId.Value.ToString(), task, dataObjects)
                        : "";
                    if (string.IsNullOrWhiteSpace(cond))
                        cond = "true";

                    sb.AppendLine($"{pad}u.StartBlockLoop();");
                    sb.AppendLine($"{pad}while(u.AdvanceBlockLoop() &&({cond}))");
                    sb.AppendLine($"{pad}{{");
                    EmitStructuredRowLogicRange(sb, orderedLines, ref index, itemsByLine, blocksByLine, endBlockLines, task, dataObjects, allTasks, pad + "    ", currentBlockCondition, currentImmediateCondition, block.ConditionExpressionId, false, writeCallMap, readCallMap);
                    sb.AppendLine($"{pad}}}");
                    sb.AppendLine($"{pad}u.EndBlockLoop();");
                    continue;
                }

                if (!string.Equals(block.Type, "E", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    continue;
                }
            }

            if (itemsByLine.TryGetValue(line, out var items))
            {
                foreach (var item in items)
                    EmitStructuredRowItem(sb, item, currentBlockCondition, currentImmediateCondition, currentLoopConditionId, task, dataObjects, allTasks, pad, writeCallMap, readCallMap);
            }

            index++;
        }

        return false;
    }

    private static bool HasExecutableStructuredContent(
        IReadOnlyList<int> orderedLines,
        int startIndex,
        IReadOnlyDictionary<int, List<StructuredRowItem>> itemsByLine,
        IReadOnlyDictionary<int, TaskBlockDef> blocksByLine,
        ISet<int> endBlockLines,
        bool stopAtElse)
    {
        var depth = 0;
        for (var i = startIndex; i < orderedLines.Count; i++)
        {
            var line = orderedLines[i];
            if (endBlockLines.Contains(line))
            {
                if (depth == 0)
                    return false;
                depth--;
                continue;
            }

            if (blocksByLine.TryGetValue(line, out var block))
            {
                if (depth == 0 &&
                    stopAtElse &&
                    string.Equals(block.Type, "E", StringComparison.OrdinalIgnoreCase))
                    return false;

                if (!string.Equals(block.Type, "E", StringComparison.OrdinalIgnoreCase))
                    depth++;
                continue;
            }

            if (itemsByLine.TryGetValue(line, out var items) &&
                items.Any(IsExecutableStructuredItem))
                return true;
        }

        return false;
    }

    private static bool HasTopLevelElseBeforeStructuredEnd(
        IReadOnlyList<int> orderedLines,
        int startIndex,
        IReadOnlyDictionary<int, TaskBlockDef> blocksByLine,
        ISet<int> endBlockLines)
    {
        var depth = 0;
        for (var i = startIndex; i < orderedLines.Count; i++)
        {
            var line = orderedLines[i];
            if (endBlockLines.Contains(line))
            {
                if (depth == 0)
                    return false;
                depth--;
                continue;
            }

            if (!blocksByLine.TryGetValue(line, out var block))
                continue;

            if (depth == 0 && string.Equals(block.Type, "E", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.Equals(block.Type, "E", StringComparison.OrdinalIgnoreCase))
                depth++;
        }

        return false;
    }

    private static void SkipStructuredRowLogicRange(
        IReadOnlyList<int> orderedLines,
        ref int index,
        IReadOnlyDictionary<int, TaskBlockDef> blocksByLine,
        ISet<int> endBlockLines,
        bool stopAtElse)
    {
        var depth = 0;
        while (index < orderedLines.Count)
        {
            var line = orderedLines[index];
            if (endBlockLines.Contains(line))
            {
                index++;
                if (depth == 0)
                    return;
                depth--;
                continue;
            }

            if (blocksByLine.TryGetValue(line, out var block))
            {
                if (depth == 0 &&
                    stopAtElse &&
                    string.Equals(block.Type, "E", StringComparison.OrdinalIgnoreCase))
                    return;

                if (!string.Equals(block.Type, "E", StringComparison.OrdinalIgnoreCase))
                    depth++;
            }

            index++;
        }
    }

    private static bool IsExecutableStructuredAction(TaskRowActionDef action)
        => !string.Equals(action.Kind, "Remark", StringComparison.OrdinalIgnoreCase);

    private static bool IsExecutableStructuredItem(StructuredRowItem item)
    {
        if (item.Action is not null)
            return IsExecutableStructuredAction(item.Action);
        return item.Io?.FormEntryIndex.HasValue == true;
    }

    private static void EmitStructuredRowItem(
        StringBuilder sb,
        StructuredRowItem item,
        string? currentBlockCondition,
        string? currentImmediateCondition,
        int? currentLoopConditionId,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        string pad,
        IReadOnlyDictionary<int, string>? writeCallMap,
        IReadOnlyDictionary<int, string>? readCallMap)
    {
        if (item.Action is not null)
        {
            EmitStructuredRowAction(sb, item.Action, currentBlockCondition, currentImmediateCondition, currentLoopConditionId, task, dataObjects, allTasks, pad);
            return;
        }

        if (item.Io is not null)
            EmitStructuredFormIo(sb, item.Io, currentBlockCondition, currentImmediateCondition, currentLoopConditionId, task, dataObjects, pad, writeCallMap, readCallMap);
    }

    private static void EmitStructuredRowAction(
        StringBuilder sb,
        TaskRowActionDef action,
        string? currentBlockCondition,
        string? currentImmediateCondition,
        int? currentLoopConditionId,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        string pad)
    {
        if (!IsExecutableStructuredAction(action))
        {
            EmitRowActionCore(sb, action, task, dataObjects, allTasks, pad, preferValueForResourceAssignments: true, suppressForcedUndo: true);
            return;
        }

        if (action.ConditionLiteral == false)
        {
            var strippedDisabledAction = StripActionCondition(action);
            sb.AppendLine($"{pad}if (false)");
            sb.AppendLine($"{pad}{{");
            if (!EmitDirectResourceAssignment(sb, strippedDisabledAction, task, dataObjects, allTasks, pad + "    ", suppressForcedUndo: true))
                EmitRowActionCore(sb, strippedDisabledAction, task, dataObjects, allTasks, pad + "    ", preferValueForResourceAssignments: true, suppressForcedUndo: true);
            sb.AppendLine($"{pad}}}");
            return;
        }

        var actionCond = ResolveActionConditionCode(action, task, dataObjects);
        var residualActionCond = StripCoveredStructuredCondition(actionCond, currentBlockCondition);
        residualActionCond = StripCoveredStructuredCondition(residualActionCond, currentImmediateCondition);
        var loopMatchesBlock = currentLoopConditionId.HasValue && action.LoopConditionExpressionId == currentLoopConditionId;
        var actionMatchesBlock = (!string.IsNullOrWhiteSpace(currentBlockCondition) ||
                                  !string.IsNullOrWhiteSpace(currentImmediateCondition)) &&
                                 string.IsNullOrWhiteSpace(residualActionCond);
        var normalizedAction = loopMatchesBlock ? action with { LoopConditionExpressionId = null } : action;

        if (actionMatchesBlock || loopMatchesBlock)
        {
            var strippedAction = StripActionCondition(normalizedAction);
            if (!EmitDirectResourceAssignment(sb, strippedAction, task, dataObjects, allTasks, pad, suppressForcedUndo: true))
                EmitRowActionCore(sb, strippedAction, task, dataObjects, allTasks, pad, preferValueForResourceAssignments: true, suppressForcedUndo: true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(residualActionCond) &&
            !AreEquivalentStructuredConditions(residualActionCond, actionCond))
        {
            var strippedAction = StripActionCondition(normalizedAction);
            sb.AppendLine($"{pad}if ({residualActionCond})");
            sb.AppendLine($"{pad}{{");
            if (!EmitDirectResourceAssignment(sb, strippedAction, task, dataObjects, allTasks, pad + "    ", suppressForcedUndo: true))
                EmitRowActionCore(sb, strippedAction, task, dataObjects, allTasks, pad + "    ", preferValueForResourceAssignments: true, suppressForcedUndo: true);
            sb.AppendLine($"{pad}}}");
            return;
        }

        if (!EmitDirectResourceAssignment(sb, normalizedAction, task, dataObjects, allTasks, pad, suppressForcedUndo: true))
            EmitRowAction(sb, normalizedAction, task, dataObjects, allTasks, pad);
    }

    private static void EmitStructuredFormIo(
        StringBuilder sb,
        TaskFormIoDef io,
        string? currentBlockCondition,
        string? currentImmediateCondition,
        int? currentLoopConditionId,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        string pad,
        IReadOnlyDictionary<int, string>? writeCallMap,
        IReadOnlyDictionary<int, string>? readCallMap)
    {
        if (io.LoopConditionExpressionId.HasValue &&
            (!currentLoopConditionId.HasValue || io.LoopConditionExpressionId != currentLoopConditionId))
        {
            var loopCond = ResolveExpressionCode(io.LoopConditionExpressionId.Value.ToString(), task, dataObjects, CreateBooleanConditionEmissionContext());
            loopCond = NormalizeStatementBooleanConditionSyntax(loopCond);
            if (!string.IsNullOrWhiteSpace(loopCond))
            {
                sb.AppendLine($"{pad}u.StartBlockLoop();");
                sb.AppendLine($"{pad}while(u.AdvanceBlockLoop() &&({loopCond}))");
                sb.AppendLine($"{pad}{{");
                EmitStructuredFormIo(sb, io with { LoopConditionExpressionId = null }, currentBlockCondition, currentImmediateCondition, currentLoopConditionId, task, dataObjects, pad + "    ", writeCallMap, readCallMap);
                sb.AppendLine($"{pad}}}");
                sb.AppendLine($"{pad}u.EndBlockLoop();");
                return;
            }
        }

        var ioCond = ResolveFormIoConditionCode(io, task, dataObjects);
        var residualIoCond = StripCoveredStructuredCondition(ioCond, currentBlockCondition);
        residualIoCond = StripCoveredStructuredCondition(residualIoCond, currentImmediateCondition);
        var loopMatchesBlock = currentLoopConditionId.HasValue && io.LoopConditionExpressionId == currentLoopConditionId;
        var normalizedIo = loopMatchesBlock ? io with { LoopConditionExpressionId = null } : io;
        var ioMatchesBlock = (!string.IsNullOrWhiteSpace(currentBlockCondition) ||
                              !string.IsNullOrWhiteSpace(currentImmediateCondition)) &&
                             string.IsNullOrWhiteSpace(residualIoCond);

        if (ioMatchesBlock || loopMatchesBlock)
        {
            EmitStructuredFormIoCore(sb, StripFormIoCondition(normalizedIo), task, dataObjects, pad, writeCallMap, readCallMap);
            return;
        }

        if (!string.IsNullOrWhiteSpace(residualIoCond) &&
            !AreEquivalentStructuredConditions(residualIoCond, ioCond))
        {
            sb.AppendLine($"{pad}if ({residualIoCond})");
            sb.AppendLine($"{pad}{{");
            EmitStructuredFormIoCore(sb, StripFormIoCondition(normalizedIo), task, dataObjects, pad + "    ", writeCallMap, readCallMap);
            sb.AppendLine($"{pad}}}");
            return;
        }

        EmitStructuredFormIoCore(sb, normalizedIo, task, dataObjects, pad, writeCallMap, readCallMap);
    }

    private static string ResolveFormIoConditionCode(TaskFormIoDef io, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (!io.ConditionExpressionId.HasValue)
            return "";
        var cond = ResolveExpressionCode(io.ConditionExpressionId.Value.ToString(), task, dataObjects, CreateBooleanConditionEmissionContext());
        return NormalizeStatementBooleanConditionSyntax(cond);
    }

    private static TaskFormIoDef StripFormIoCondition(TaskFormIoDef io)
        => io with { LoopConditionExpressionId = null, ConditionExpressionId = null };

    private static void EmitStructuredFormIoCore(
        StringBuilder sb,
        TaskFormIoDef io,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        string pad,
        IReadOnlyDictionary<int, string>? writeCallMap,
        IReadOnlyDictionary<int, string>? readCallMap)
    {
        if (!io.FormEntryIndex.HasValue)
            return;
        if (io.OperationType == "O" &&
            writeCallMap is not null &&
            writeCallMap.TryGetValue(io.FormEntryIndex.Value, out var writeCall))
        {
            EmitFormIoWrite(sb, io, writeCall, task, dataObjects, pad);
            return;
        }
        if (io.OperationType == "I" &&
            readCallMap is not null &&
            readCallMap.TryGetValue(io.FormEntryIndex.Value, out var readCall))
        {
            EmitFormIoRead(sb, io, readCall, task, dataObjects, pad);
        }
    }

    private static string CombineStructuredBlockCondition(string? parentCondition, string? currentCondition)
    {
        if (string.IsNullOrWhiteSpace(parentCondition))
            return currentCondition ?? "";
        if (string.IsNullOrWhiteSpace(currentCondition))
            return parentCondition;
        return $"({parentCondition}) && ({currentCondition})";
    }

    private static bool AreEquivalentStructuredConditions(string first, string second)
    {
        return NormalizeStructuredCondition(first) == NormalizeStructuredCondition(second);
    }

    private static string NormalizeStructuredCondition(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return "";

        if (_normalizedStructuredConditionCache.TryGetValue(condition, out var cached))
            return cached;

        var text = NormalizeStructuredConditionCore(CanonicalizeStructuredConditionForComparison(condition.Trim()));
        _normalizedStructuredConditionCache[condition] = text;
        return text;
    }

    private static string NormalizeStructuredConditionCore(string condition)
    {
        var text = UnwrapStructuredConditionPart(condition);
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var booleanSplit =
            SplitTopLevelBooleanBinaryExpression(text) ??
            SplitMalformedTopLevelBooleanBinaryExpression(text);
        if (booleanSplit is not null)
        {
            var left = NormalizeStructuredConditionCore(booleanSplit.Value.Left);
            var right = NormalizeStructuredConditionCore(booleanSplit.Value.Right);
            return $"{left}{booleanSplit.Value.Operator}{right}";
        }

        var comparison = SplitTopLevelComparisonExpression(text);
        if (comparison is not null)
        {
            var left = NormalizeStructuredConditionCore(comparison.Value.Left);
            var right = NormalizeStructuredConditionCore(comparison.Value.Right);
            return $"{left}{comparison.Value.Operator}{right}";
        }

        if (TryParseFunctionCall(text, out var functionName, out var args))
        {
            var normalizedArgs = args.Select(NormalizeStructuredConditionCore);
            return $"{functionName}({string.Join(",", normalizedArgs)})";
        }

        return RemoveWhitespace(text);
    }

    private static string CanonicalizeStructuredConditionForComparison(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return condition;

        if (!condition.Contains("Rights", StringComparison.OrdinalIgnoreCase))
            return condition;

        // Some row/block conditions still arrive in a malformed transitional shape like:
        // u.Not(u.Not(Rights)(u.Rights("879,1")))
        // For structured-block matching we only need a stable canonical form so the action
        // condition can be recognized as already covered by the enclosing block.
        condition = Regex.Replace(
            condition,
            @"u\.Not\(\s*u\.Not\(Rights\)\s*\(\s*u\.Rights\((?<arg>.*?)\)\s*\)\s*\)",
            m => $"u.Not(u.Rights({m.Groups["arg"].Value}))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        condition = Regex.Replace(
            condition,
            @"u\.Not\(\s*Rights\s*\)\s*\(\s*u\.Rights\((?<arg>.*?)\)\s*\)",
            m => $"u.Not(u.Rights({m.Groups["arg"].Value}))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return condition;
    }

    private static string RemoveWhitespace(string text)
    {
        var firstWhitespace = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                continue;

            firstWhitespace = i;
            break;
        }

        if (firstWhitespace < 0)
            return text;

        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, firstWhitespace);
        for (var i = firstWhitespace + 1; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                builder.Append(text[i]);
        }

        return builder.ToString();
    }

    private static bool HasBalancedOuterParentheses(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '(') depth++;
            else if (ch == ')')
            {
                depth--;
                if (depth == 0 && i < text.Length - 1)
                    return false;
            }
        }

        return depth == 0;
    }

    private static string StripCoveredStructuredCondition(string actionCondition, string? coveredCondition)
    {
        if (string.IsNullOrWhiteSpace(actionCondition) || string.IsNullOrWhiteSpace(coveredCondition))
            return actionCondition;

        var cacheKey = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{actionCondition}|{coveredCondition}");
        if (_coveredStructuredConditionStripCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (AreEquivalentStructuredConditions(actionCondition, coveredCondition))
        {
            _coveredStructuredConditionStripCache[cacheKey] = "";
            return "";
        }

        var parts = SplitStructuredConditionConjunction(actionCondition);
        if (parts.Count <= 1)
        {
            _coveredStructuredConditionStripCache[cacheKey] = actionCondition;
            return actionCondition;
        }
        var coveredParts = SplitStructuredConditionConjunction(coveredCondition);
        if (coveredParts.Count == 0)
            coveredParts.Add(UnwrapStructuredConditionPart(coveredCondition));

        var remaining = parts
            .Where(part => !coveredParts.Any(coveredPart => AreEquivalentStructuredConditions(part, coveredPart)))
            .ToList();

        if (remaining.Count == parts.Count)
        {
            _coveredStructuredConditionStripCache[cacheKey] = actionCondition;
            return actionCondition;
        }
        if (remaining.Count == 0)
        {
            _coveredStructuredConditionStripCache[cacheKey] = "";
            return "";
        }
        if (remaining.Count == 1)
        {
            _coveredStructuredConditionStripCache[cacheKey] = remaining[0];
            return remaining[0];
        }
        var result = string.Join(" && ", remaining.Select(WrapStructuredConditionPart));
        _coveredStructuredConditionStripCache[cacheKey] = result;
        return result;
    }

    private static List<string> SplitStructuredConditionConjunction(string condition)
    {
        if (_structuredConditionConjunctionCache.TryGetValue(condition, out var cached))
            return new List<string>(cached);

        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(condition))
            return result;

        var text = UnwrapStructuredConditionPart(condition);
        var depth = 0;
        var start = 0;
        var split = false;
        for (var i = 0; i < text.Length - 1; i++)
        {
            var ch = text[i];
            if (ch == '(')
                depth++;
            else if (ch == ')')
                depth--;
            else if (ch == '&' && text[i + 1] == '&' && depth == 0)
            {
                var part = text[start..i].Trim();
                if (!string.IsNullOrWhiteSpace(part))
                    AddStructuredConditionPart(result, part, 0);
                start = i + 2;
                i++;
                split = true;
            }
        }

        var tail = text[start..].Trim();
        if (!string.IsNullOrWhiteSpace(tail))
            AddStructuredConditionPart(result, tail, split ? 0 : 1);
        _structuredConditionConjunctionCache[condition] = result.ToArray();
        return result;
    }

    private static void AddStructuredConditionPart(List<string> result, string part, int depth)
    {
        var unwrapped = UnwrapStructuredConditionPart(part);
        if (unwrapped.Length == 0)
            return;

        if (depth < 8)
        {
            var nested = SplitStructuredConditionConjunctionCore(unwrapped, depth + 1);
            if (nested.Count > 1)
            {
                result.AddRange(nested);
                return;
            }
        }

        result.Add(unwrapped);
    }

    private static List<string> SplitStructuredConditionConjunctionCore(string condition, int depth)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(condition) || depth > 8)
            return result;

        var text = UnwrapStructuredConditionPart(condition);
        var parenDepth = 0;
        var start = 0;
        var split = false;
        for (var i = 0; i < text.Length - 1; i++)
        {
            var ch = text[i];
            if (ch == '(')
                parenDepth++;
            else if (ch == ')')
                parenDepth--;
            else if (ch == '&' && text[i + 1] == '&' && parenDepth == 0)
            {
                var part = text[start..i].Trim();
                if (!string.IsNullOrWhiteSpace(part))
                    AddStructuredConditionPart(result, part, depth + 1);
                start = i + 2;
                i++;
                split = true;
            }
        }

        var tail = text[start..].Trim();
        if (!string.IsNullOrWhiteSpace(tail))
            AddStructuredConditionPart(result, tail, split ? depth + 1 : 9);
        return result;
    }

    private static string UnwrapStructuredConditionPart(string text)
    {
        var value = text.Trim();
        while (value.Length >= 2 && value[0] == '(' && value[^1] == ')' && HasBalancedOuterParentheses(value))
            value = value[1..^1].Trim();
        return value;
    }

    private static string WrapStructuredConditionPart(string text)
    {
        var value = text.Trim();
        return value.StartsWith("(", StringComparison.Ordinal) && value.EndsWith(")", StringComparison.Ordinal)
            ? value
            : $"({value})";
    }

    private static int? ExtractLogicLineOrder(string? xmlTrace)
    {
        if (string.IsNullOrWhiteSpace(xmlTrace))
            return null;
        var m = Regex.Match(xmlTrace, @"llLine=(\d+)");
        if (!m.Success)
            return null;
        return int.TryParse(m.Groups[1].Value, out var line) ? line : null;
    }

    private static int? ExtractLogicUnitLineIndex(string? xmlTrace)
    {
        if (string.IsNullOrWhiteSpace(xmlTrace))
            return null;
        var m = Regex.Match(xmlTrace, @"LogicLine\[(\d+)\]");
        if (!m.Success)
            return null;
        return int.TryParse(m.Groups[1].Value, out var line) ? line : null;
    }

    private static int? ExtractLogicUnitIndex(string? xmlTrace)
    {
        if (string.IsNullOrWhiteSpace(xmlTrace))
            return null;
        var m = Regex.Match(xmlTrace, @"LogicUnit\[(\d+)\]");
        if (!m.Success)
            return null;
        return int.TryParse(m.Groups[1].Value, out var unit) ? unit : null;
    }
}


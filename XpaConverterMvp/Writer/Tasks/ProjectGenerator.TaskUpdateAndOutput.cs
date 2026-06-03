using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveUpdateTargetExpression(string variable, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks)
    {
        var cacheKey = task.Ordinal.ToString(CultureInfo.InvariantCulture) + "|" + variable;
        if (_updateTargetExpressionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var selectMap = BuildSelectNameToExpressionMap(task, dataObjects);
        if (selectMap.TryGetValue(variable, out var target))
        {
            _updateTargetExpressionCache[cacheKey] = target;
            return target;
        }

        if (task.ParentOrdinal.HasValue)
        {
            var parentChainPrefix = "_parent";
            var parentOrdinal = task.ParentOrdinal;
            while (parentOrdinal.HasValue)
            {
                if (!_tasksByOrdinal.TryGetValue(parentOrdinal.Value, out var parent) || parent is null)
                    break;
                var parentSelectMap = BuildSelectNameToExpressionMap(parent, dataObjects);
                if (parentSelectMap.TryGetValue(variable, out var parentTarget))
                {
                    var resolvedParentTarget = parentChainPrefix + "." + parentTarget;
                    _updateTargetExpressionCache[cacheKey] = resolvedParentTarget;
                    return resolvedParentTarget;
                }
                parent.ResourcesSemantic.ByName.TryGetValue(variable, out var parentResource);
                if (parentResource is not null)
                {
                    var resolvedParentResource = parentChainPrefix + "." + ResolveTaskResourceMemberName(parent, parentResource);
                    _updateTargetExpressionCache[cacheKey] = resolvedParentResource;
                    return resolvedParentResource;
                }
                parentOrdinal = parent.ParentOrdinal;
                parentChainPrefix += "._parent";
            }
        }

        task.ResourcesSemantic.ByName.TryGetValue(variable, out var resource);
        if (resource is not null)
        {
            var resolvedResource = ResolveTaskResourceMemberName(task, resource);
            _updateTargetExpressionCache[cacheKey] = resolvedResource;
            return resolvedResource;
        }

        var applicationSelectMap = BuildApplicationSelectMap(allTasks, dataObjects);
        if (applicationSelectMap.TryGetValue(variable, out var applicationTarget))
        {
            _updateTargetExpressionCache[cacheKey] = applicationTarget;
            return applicationTarget;
        }

        _updateTargetExpressionCache[cacheKey] = "";
        return "";
    }

    private static void EmitOperationOStatement(StringBuilder sb, TaskCallDef call, TaskSemantic task, string pad)
    {
        EmitXmlTraceComment(sb, call.XmlTrace, pad);
        if (call.FormEntryIndex.HasValue)
        {
            var writeCallMap = BuildFormIoWriteCallMap(task);
            if (writeCallMap.TryGetValue(call.FormEntryIndex.Value, out var writeCall))
            {
                sb.AppendLine($"{pad}{writeCall};");
                return;
            }
        }

        sb.AppendLine($"{pad}// Output operation skipped: page={call.Page ?? "?"}, ioDevice={call.IoDeviceIndex?.ToString() ?? "?"}, formEntry={call.FormEntryIndex?.ToString() ?? "?"}. XML={call.XmlTrace ?? "?"}");
    }

    private static void EmitXmlTraceComment(StringBuilder sb, string? xmlTrace, string pad)
    {
        // XML trace comments are intentionally suppressed in generated code.
        // We keep only GAP comments (not mapped/skipped/fallback) with XML info.
        _ = sb;
        _ = xmlTrace;
        _ = pad;
    }

    private static void EmitRemarkComment(StringBuilder sb, string? remarkText, string pad)
    {
        if (string.IsNullOrWhiteSpace(remarkText))
            return;

        if (!_remarkLinesCache.TryGetValue(remarkText, out var lines))
        {
            var normalized = remarkText.Replace("\r\n", "\n").Replace('\r', '\n');
            lines = normalized.Split('\n');
            _remarkLinesCache[remarkText] = lines;
        }

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
                sb.AppendLine($"{pad}//");
            else
                sb.AppendLine($"{pad}// {trimmed}");
        }
    }
}


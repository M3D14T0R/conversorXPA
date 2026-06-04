using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool HasPrintLayout(TaskSemantic t)
    {
        return t.Layout.PrintForms.Count > 0;
    }

    private static bool DeclaresPrintStream(TaskSemantic task)
    {
        if (HasPrintLayout(task))
            return true;

        if (!HasWritablePrintIoDefinition(task))
            return false;

        return HasDescendantPrintFormIoTargetingTaskIo(task, 1);
    }

    private static bool HasWritablePrintIoDefinition(TaskSemantic task)
    {
        var ios = task.Ios.Count > 0
            ? task.Ios
            : (task.Io is null ? Array.Empty<TaskIoDef>() : new[] { task.Io });
        if (ios.Count == 0)
            return false;

        foreach (var io in ios)
        {
            if (io is null)
                continue;
            if (string.Equals(io.Access, "R", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(io.Media, "V", StringComparison.OrdinalIgnoreCase))
                continue;
            return true;
        }

        return false;
    }

    private static bool HasDescendantPrintFormIoTargetingTaskIo(TaskSemantic task, int depth)
    {
        var allTasks = _allTasks ?? Array.Empty<TaskSemantic>();
        var children = GetChildTasks(task.Ordinal, allTasks);
        foreach (var child in children)
        {
            if (TaskHasPrintFormIoTargetingAncestorIo(child, depth))
                return true;

            if (HasDescendantPrintFormIoTargetingTaskIo(child, depth + 1))
                return true;
        }

        return false;
    }

    private static bool TaskHasPrintFormIoTargetingAncestorIo(TaskSemantic task, int depth)
    {
        if (!HasPrintLayout(task))
            return false;

        foreach (var io in task.Layout.PrintOutputIos)
        {
            if (IsPrintFormOutputIoTargetingAncestorIo(io, depth))
                return true;
        }

        foreach (var io in task.Layout.PrintGroupIos)
        {
            if (IsPrintFormOutputIoTargetingAncestorIo(io, depth))
                return true;
        }

        foreach (var io in task.Layout.StartTaskOutputIos)
        {
            if (IsPrintFormOutputIoTargetingAncestorIo(io, depth))
                return true;
        }

        foreach (var io in task.Layout.EndTaskOutputIos)
        {
            if (IsPrintFormOutputIoTargetingAncestorIo(io, depth))
                return true;
        }

        foreach (var io in task.Layout.PrintRowIos)
        {
            if (IsPrintFormOutputIoTargetingAncestorIo(io, depth))
                return true;
        }

        return false;
    }

    private static bool IsPrintFormOutputIoTargetingAncestorIo(TaskFormIoDef io, int depth)
    {
        return string.Equals(io.OperationType, "O", StringComparison.OrdinalIgnoreCase) &&
               io.FormEntryIndex.HasValue &&
               io.IoDeviceParent.GetValueOrDefault() == depth;
    }

    private static bool HasTextIoLayout(TaskSemantic t)
    {
        return t.Layout.TextForms.Count > 0;
    }

    private static bool ShouldSuppressViewForBusinessProcessTextIo(TaskSemantic t)
    {
        return ResolveBaseClass(t) == "BusinessProcessBase" &&
               HasTextIoLayout(t) &&
               !ResolveTextIoStreams(t).Any(s => string.Equals(s.StreamType, "TextPrinterWriter", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsByteArrayTextIo(TaskSemantic t, TaskIoDef? io = null)
    {
        io ??= t.Io;
        return string.Equals(io?.Media, "V", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextIoReaderStream(TaskSemantic t, TaskIoDef? io = null)
    {
        return string.Equals(ResolveTextIoStreamType(t, io), "FileReader", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ResolveTextIoStreamType(t, io), "ByteArrayReader", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveTextIoStreamType(TaskSemantic t, TaskIoDef? io = null)
    {
        io ??= t.Io;
        if (string.Equals(io?.Media, "P", StringComparison.OrdinalIgnoreCase))
            return "TextPrinterWriter";
        if (!IsByteArrayTextIo(t, io))
            return string.Equals(io?.Access, "R", StringComparison.OrdinalIgnoreCase)
                ? "FileReader"
                : "FileWriter";
        return string.Equals(io?.Access, "R", StringComparison.OrdinalIgnoreCase)
            ? "ByteArrayReader"
            : "ByteArrayWriter";
    }

    private static void EmitTextIoReaderDataViewBindings(
        StringBuilder sb,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        var primaryObj = task.PrimaryDbObj ?? task.InformationDbObj;
        if (primaryObj <= 0)
            return;

        var bindings = task.DataView.Columns
            .Where(sel => !sel.IsParameter && sel.SourceDbObj == primaryObj)
            .Select(sel => ResolveSelectExpression(sel, task, dataObjects, ""))
            .Where(IsSimpleMemberAccess)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (bindings.Count == 0)
            return;

        sb.AppendLine($"        SetDataViewVarsColumns({string.Join(", ", bindings)});");
    }

    private static void EmitHiddenDataViewBindings(
        StringBuilder sb,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (task.View.ShouldGenerate || !string.Equals(ResolveBaseClass(task), "BusinessProcessBase", StringComparison.Ordinal))
            return;

        var primaryObj = task.PrimaryDbObj ?? task.InformationDbObj;
        if (primaryObj <= 0)
            return;

        var bindings = task.DataView.Columns
            .Where(sel => !sel.IsParameter && sel.SourceDbObj == primaryObj)
            .Select(sel => ResolveSelectExpression(sel, task, dataObjects, ""))
            .Where(IsSimpleMemberAccess)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (bindings.Count == 0)
            return;

        sb.AppendLine($"        SetDataViewVarsColumns({string.Join(", ", bindings)});");
    }

    private static string ResolveTextIoColumnExpression(TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks, TaskIoDef? io = null)
    {
        io ??= task.Io;
        if (string.IsNullOrWhiteSpace(io?.ColumnRef))
            return "";
        var expr = ResolveSelectExpressionByName(io.ColumnRef!, task, dataObjects);
        if (!string.IsNullOrWhiteSpace(expr))
            return expr;
        expr = ResolveExpressionOrdinalBinding(io.ColumnRef!, task, allTasks, dataObjects);
        if (!string.IsNullOrWhiteSpace(expr))
            return expr;
        task.ResourcesSemantic.ByName.TryGetValue(io.ColumnRef!, out var columnMember);
        if (columnMember is not null)
            return ToLegacyVariableName(columnMember.Name);
        return "";
    }

    private static bool IsByteArrayCompatibleTextIoSource(TaskSemantic task, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = expression.Trim();
        if (trimmed.StartsWith("u.CastToByteArray(", StringComparison.Ordinal) ||
            trimmed.StartsWith("u.File2Blb(", StringComparison.Ordinal) ||
            trimmed.StartsWith("u.BlobFromBase64(", StringComparison.Ordinal))
            return true;

        var targetInfo = ResolveTargetValueInfo(task, null, trimmed);
        return targetInfo.IsBlob && !targetInfo.IsArray;
    }

    private sealed record TextIoStreamInfo(string VariableName, string StreamType, TaskIoDef? Definition);

    private static string ResolveTextIoStreamFieldType(string streamType)
    {
        return string.Equals(streamType, "TextPrinterWriter", StringComparison.OrdinalIgnoreCase)
            ? "ENV.Printing.TextPrinterWriter"
            : $"ENV.IO.{streamType}";
    }

    private static IReadOnlyList<TextIoStreamInfo> ResolveTextIoStreams(TaskSemantic t)
    {
        var ios = t.Ios.Count > 0 ? t.Ios : (t.Io is null ? Array.Empty<TaskIoDef>() : new[] { t.Io });
        if (ios.Count == 0)
            return new[] { new TextIoStreamInfo("_ioReport", ResolveTextIoStreamType(t), t.Io) };

        if (ios.Count == 1)
            return new[] { new TextIoStreamInfo("_ioReport", ResolveTextIoStreamType(t, ios[0]), ios[0]) };

        return ios.Select((io, index) => new TextIoStreamInfo(ResolveTextIoStreamVariableName(io, index), ResolveTextIoStreamType(t, io), io)).ToList();
    }

    private static string ResolveTextIoStreamVariableName(TaskIoDef? io, int ioIndex)
    {
        var desc = io?.Description;
        if (!string.IsNullOrWhiteSpace(desc))
        {
            var suffix = ToPascalIdentifier(desc);
            if (!string.IsNullOrWhiteSpace(suffix))
                return "_io" + suffix;
        }
        return ioIndex == 0 ? "_ioReport" : $"_ioReport{ioIndex + 1}";
    }

    private static bool TryResolveIoDeviceAncestor(TaskSemantic task, int parentDepth, out TaskSemantic? ancestor, out string parentPrefix)
    {
        var current = task;
        parentPrefix = "";
        var allTasks = _allTasks ?? Array.Empty<TaskSemantic>();
        for (var i = 0; i < parentDepth; i++)
        {
            if (!current.ParentOrdinal.HasValue)
            {
                ancestor = null;
                parentPrefix = "";
                return false;
            }

            var parentOrdinal = current.ParentOrdinal.Value;
            var parent = allTasks.FirstOrDefault(x => x.Ordinal == parentOrdinal);
            if (parent is null)
            {
                ancestor = null;
                parentPrefix = "";
                return false;
            }

            current = parent;
            parentPrefix += "_parent.";
        }

        ancestor = current;
        return ancestor is not null;
    }

    private static string ResolveTextIoStreamVariableForIo(TaskSemantic t, TaskFormIoDef io)
    {
        if (io.IoDeviceParent.GetValueOrDefault() > 0 &&
            TryResolveIoDeviceAncestor(t, io.IoDeviceParent!.Value, out var ancestor, out var parentPrefix) &&
            ancestor is not null &&
            DeclaresTextIoStream(ancestor))
        {
            return parentPrefix + ResolveTextIoStreamVariableForIo(ancestor, io with { IoDeviceParent = null });
        }

        var streams = ResolveTextIoStreams(t);
        var idx = (io.IoDeviceIndex ?? 1) - 1;
        if (idx < 0)
            return streams[0].VariableName;
        if (idx < streams.Count)
            return streams[idx].VariableName;
        if (idx == streams.Count && HasMergeLayout(t))
            return ResolveMergeStreamExpression(t, _allTasks ?? Array.Empty<TaskSemantic>());
        return streams[0].VariableName;
    }

    private static bool DeclaresTextIoStream(TaskSemantic task)
        => HasTextIoLayout(task) ||
           (HasMergeLayout(task) && task.Io is not null);

    private static string ResolveParentBlobExpression(TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks)
    {
        var ancestor = task;
        var depth = 0;
        while (ancestor.ParentOrdinal.HasValue && depth < 6)
        {
            depth++;
            var parent = allTasks.FirstOrDefault(x => x.Ordinal == ancestor.ParentOrdinal.Value);
            if (parent is null)
                break;
            var blob = parent.ResourcesSemantic.FirstBlob;
            if (blob is not null)
            {
                var prefix = string.Concat(Enumerable.Repeat("_parent.", depth));
                return prefix + ToLegacyVariableName(blob.Name);
            }
            ancestor = parent;
        }
        return "";
    }

    private static bool HasMergeLayout(TaskSemantic t)
    {
        return t.Layout.MergeForms.Count > 0 || t.Merge.HasTemplate;
    }

    private static string ResolveMergeStreamVariableName(TaskSemantic t)
    {
        var suffix = ToPascalIdentifier(string.IsNullOrWhiteSpace(t.Io?.Description) ? "Merge" : t.Io.Description!);
        if (string.IsNullOrWhiteSpace(suffix))
            suffix = "Merge";
        return "_io" + suffix;
    }

    private static string ResolveMergeStreamExpression(TaskSemantic t, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (HasTextIoLayout(t) && TryResolveTextIoWriteStreamVariableName(t, out var textIoWriter))
            return textIoWriter;

        if (t.Io is not null)
            return ResolveMergeStreamVariableName(t);

        if (t.ParentOrdinal.HasValue)
        {
            var parent = allTasks.FirstOrDefault(x => x.Ordinal == t.ParentOrdinal.Value);
            if (parent is not null && (HasMergeLayout(parent) || parent.Io is not null))
                return $"_parent.{ResolveMergeStreamExpression(parent, allTasks)}";
        }

        return ResolveMergeStreamVariableName(t);
    }

    private static bool TryResolveTextIoWriteStreamVariableName(TaskSemantic t, out string variableName)
    {
        foreach (var stream in ResolveTextIoStreams(t))
        {
            if (string.Equals(stream.StreamType, "FileReader", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stream.StreamType, "ByteArrayReader", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stream.StreamType, "TextPrinterWriter", StringComparison.OrdinalIgnoreCase))
                continue;

            variableName = stream.VariableName;
            return true;
        }

        variableName = "";
        return false;
    }

    private static string ResolveMergeTemplateVariableName(TaskFormEntryDef formEntry)
    {
        var suffix = ToPascalIdentifier(string.IsNullOrWhiteSpace(formEntry.Form.FormName) ? $"Merge{formEntry.Index}" : formEntry.Form.FormName!);
        if (string.IsNullOrWhiteSpace(suffix))
            suffix = $"Merge{formEntry.Index}";
        return "_view" + suffix;
    }

    private static string ReplaceBareIoReportPlaceholder(string expression, string replacement)
    {
        const string placeholder = "_ioReport";
        if (string.IsNullOrWhiteSpace(expression) ||
            string.IsNullOrWhiteSpace(replacement) ||
            expression.IndexOf(placeholder, StringComparison.Ordinal) < 0)
            return expression;

        var sb = new StringBuilder(expression.Length + Math.Max(0, replacement.Length - placeholder.Length));
        var index = 0;
        while (index < expression.Length)
        {
            var found = expression.IndexOf(placeholder, index, StringComparison.Ordinal);
            if (found < 0)
            {
                sb.Append(expression, index, expression.Length - index);
                break;
            }

            var before = found > 0 ? expression[found - 1] : '\0';
            var afterIndex = found + placeholder.Length;
            var after = afterIndex < expression.Length ? expression[afterIndex] : '\0';
            var alreadyQualified = before == '.' || char.IsLetterOrDigit(before) || before == '_';
            var hasIdentifierSuffix = char.IsLetterOrDigit(after) || after == '_';
            sb.Append(expression, index, found - index);
            sb.Append(alreadyQualified || hasIdentifierSuffix ? placeholder : replacement);
            index = afterIndex;
        }

        return sb.ToString();
    }

    private static bool HasPrintGroupIo(TaskSemantic t)
    {
        return t.Layout.PrintGroupIos.Count > 0;
    }

    private static string? ResolvePageHeaderSectionName(TaskSemantic t)
    {
        return t.Layout.PageHeaderSectionName;
    }

    private static string? ResolvePageFooterSectionName(TaskSemantic t)
    {
        return t.Layout.PageFooterSectionName;
    }

    private static string? ResolvePageHeaderSectionName(TaskSemantic t, int ioIndex)
    {
        if (ioIndex <= 0)
            return t.Layout.PageHeaderSectionName;
        var taskIo = ioIndex < t.Ios.Count ? t.Ios[ioIndex] : null;
        if (taskIo?.PageHeaderFormEntryIndex is int explicitHeaderIndex &&
            t.Layout.PrintSectionNamesByFormEntryIndex.TryGetValue(explicitHeaderIndex, out var explicitHeaderSection))
            return explicitHeaderSection;
        return t.Layout.PageHeaderSectionName;
    }

    private static string? ResolvePageFooterSectionName(TaskSemantic t, int ioIndex)
    {
        if (ioIndex <= 0)
            return t.Layout.PageFooterSectionName;
        var taskIo = ioIndex < t.Ios.Count ? t.Ios[ioIndex] : null;
        if (taskIo?.PageFooterFormEntryIndex is int explicitFooterIndex &&
            t.Layout.PrintSectionNamesByFormEntryIndex.TryGetValue(explicitFooterIndex, out var explicitFooterSection))
            return explicitFooterSection;
        return t.Layout.PageFooterSectionName;
    }

    private static IReadOnlyList<string> ResolvePrintStreamVariableNames(TaskSemantic t)
    {
        var taskIos = t.Ios.Count > 0 ? t.Ios : (t.Io is null ? Array.Empty<TaskIoDef>() : new[] { t.Io });
        if (taskIos.Count == 0)
            return new[] { "_ioPrint" };
        return Enumerable.Range(0, taskIos.Count)
            .Select(i => ResolvePrintStreamVariableName(t, i))
            .ToList();
    }

    private static string ResolvePrintStreamVariableName(TaskSemantic t, int ioIndex)
    {
        if (ioIndex <= 0)
            return "_ioPrint";
        var taskIo = ioIndex < t.Ios.Count ? t.Ios[ioIndex] : null;
        var desc = taskIo?.Description;
        if (string.IsNullOrWhiteSpace(desc))
            return $"_ioPrint{ioIndex + 1}";
        var suffix = ToPascalIdentifier(desc);
        if (string.IsNullOrWhiteSpace(suffix) || string.Equals(suffix, "Print", StringComparison.OrdinalIgnoreCase))
            return $"_ioPrint{ioIndex + 1}";
        return "_io" + suffix;
    }

    private static string ResolvePrintStreamVariableForIo(TaskSemantic t, TaskFormIoDef io)
    {
        if (io.IoDeviceParent.GetValueOrDefault() > 0 &&
            TryResolveIoDeviceAncestor(t, io.IoDeviceParent!.Value, out var ancestor, out var parentPrefix) &&
            ancestor is not null &&
            DeclaresPrintStream(ancestor))
        {
            return parentPrefix + ResolvePrintStreamVariableForIo(ancestor, io with { IoDeviceParent = null });
        }

        var idx = (io.IoDeviceIndex ?? 1) - 1;
        return ResolvePrintStreamVariableName(t, idx < 0 ? 0 : idx);
    }

    private static void EmitPrintSectionWrites(StringBuilder sb, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (!HasPrintLayout(t))
            return;

        var printForms = t.Layout.PrintForms.OrderBy(x => x.Index).ToList();
        if (printForms.Count == 0)
            return;
        var sectionMap = t.Layout.PrintSectionNamesByFormEntryIndex;
        var writeCallMap = t.Layout.FormIoWriteCallsByFormEntryIndex;
        var ios = t.Layout.PrintOutputIos.ToList();
        if (ios.Count == 0)
            return;

        var groupIos = t.Layout.PrintGroupIos.ToList();
        if (groupIos.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("    void InitializeGroups()");
            sb.AppendLine("    {");
        var groupedByRef = groupIos.GroupBy(x => x.Reference ?? "").ToList();
        foreach (var g in groupedByRef)
        {
                var keyExpr = ResolveGroupReferenceExpression(g.Key, t, dataObjects);
                if (string.IsNullOrWhiteSpace(keyExpr))
                {
                    sb.AppendLine($"        // Print group skipped: unresolved reference {g.Key}.");
                    continue;
                }
                var enterActions = t.Logic.GroupLogics
                    .Where(x => string.Equals(x.Reference ?? "", g.Key, StringComparison.OrdinalIgnoreCase) && x.Type == "P")
                    .SelectMany(x => x.Actions)
                    .ToList();
                var leaveActions = t.Logic.GroupLogics
                    .Where(x => string.Equals(x.Reference ?? "", g.Key, StringComparison.OrdinalIgnoreCase) && x.Type == "S")
                    .SelectMany(x => x.Actions)
                    .ToList();
                sb.AppendLine($"        Groups.Add({keyExpr}).Enter += () =>");
                sb.AppendLine("        {");
                EmitOrderedGroupActions(sb, enterActions, g.Where(x => x.Type == "P").ToList(), writeCallMap, t, dataObjects, allTasks, "            ");
                sb.AppendLine("        };");
                if (g.Any(x => x.Type == "S") || leaveActions.Count > 0)
                {
                    sb.AppendLine($"        Groups[{keyExpr}].Leave += () =>");
                    sb.AppendLine("        {");
                    EmitOrderedGroupActions(sb, leaveActions, g.Where(x => x.Type == "S").ToList(), writeCallMap, t, dataObjects, allTasks, "            ");
                    sb.AppendLine("        };");
                }
            }
            sb.AppendLine("    }");
        }
    }

    private static List<TaskFormIoDef> GetPrintRowIos(TaskSemantic t)
    {
        return t.Layout.PrintRowIos.ToList();
    }

    private static List<TaskFormIoDef> GetTextIoReadRowIos(TaskSemantic t)
    {
        return t.Layout.TextIoReadRowIos.ToList();
    }

    private static Dictionary<int, string> BuildFormIoWriteCallMap(TaskSemantic t)
    {
        var map = t.Layout.FormIoWriteCallsByFormEntryIndex.ToDictionary(x => x.Key, x => x.Value);
        if (HasMergeLayout(t))
        {
            var effectiveStreamExpr = ResolveMergeStreamExpression(t, _allTasks ?? Array.Empty<TaskSemantic>());
            foreach (var fe in t.Layout.MergeForms)
            {
                if (t.Layout.MergeTemplateVariableNamesByFormEntryIndex.TryGetValue(fe.Index, out var variableName))
                    map[fe.Index] = $"{variableName}.WriteTo({effectiveStreamExpr})";
            }
        }
        return map;
    }

    private static Dictionary<int, string> BuildFormIoReadCallMap(TaskSemantic t)
    {
        return t.Layout.FormIoReadCallsByFormEntryIndex.ToDictionary(x => x.Key, x => x.Value);
    }

    private static string ResolveDelimiterCharLiteral(int? delimiterChar)
    {
        if (!delimiterChar.HasValue)
            return "' '";
        var c = (char)delimiterChar.Value;
        return c switch
        {
            '\'' => "'\\''",
            '\\' => "'\\\\'",
            _ => $"'{c}'"
        };
    }

    private static void EmitFormIoWrite(StringBuilder sb, TaskFormIoDef io, string writeCall, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, string pad)
    {
        var resolvedWriteCall = ReplaceBareIoReportPlaceholder(writeCall, ResolveTextIoStreamVariableForIo(t, io));
        if (io.FormEntryIndex.HasValue && resolvedWriteCall.Contains("_layout.", StringComparison.Ordinal))
            resolvedWriteCall = resolvedWriteCall.Replace("_layout.", ResolveTextIoLayoutVariableName(t, io.FormEntryIndex.Value) + ".", StringComparison.Ordinal);
        if (resolvedWriteCall.Contains("_ioPrint", StringComparison.Ordinal))
            resolvedWriteCall = resolvedWriteCall.Replace("_ioPrint", ResolvePrintStreamVariableForIo(t, io), StringComparison.Ordinal);
        if (HasMergeLayout(t) &&
            resolvedWriteCall.Contains(".WriteTo(", StringComparison.Ordinal) &&
            !resolvedWriteCall.Contains("_ioPrint", StringComparison.Ordinal) &&
            !resolvedWriteCall.Contains("_ioReport", StringComparison.Ordinal))
        {
            var idx = resolvedWriteCall.IndexOf(".WriteTo(", StringComparison.Ordinal);
            if (idx > 0)
            {
                var variableName = resolvedWriteCall[..idx];
                if (IsMergeTemplateVariable(variableName, t))
                {
                    var effectiveStreamExpr = ResolveMergeStreamExpression(t, _allTasks ?? Array.Empty<TaskSemantic>());
                    resolvedWriteCall = $"{variableName}.WriteTo({effectiveStreamExpr})";
                }
            }
        }
        var cond = io.ConditionExpressionId.HasValue
            ? ResolveExpressionCode(io.ConditionExpressionId.Value.ToString(), t, dataObjects, CreateBooleanConditionEmissionContext())
            : "";
        var hasCond = !string.IsNullOrWhiteSpace(cond);
        if (hasCond)
        {
            sb.AppendLine($"{pad}if ({cond})");
            sb.AppendLine($"{pad}{{");
            pad += "    ";
        }
        if (io.Page == "T" && resolvedWriteCall.Contains("_ioPrint", StringComparison.Ordinal))
            sb.AppendLine($"{pad}{ResolvePrintStreamVariableForIo(t, io)}.NewPage();");
        else if (io.Page == "S" &&
                 TryParsePrintWriteCall(resolvedWriteCall, out var sectionExpr, out var streamExpr))
        {
            sb.AppendLine($"{pad}if ({sectionExpr}.Height < {streamExpr}.HeightUntilEndOfPage && !{streamExpr}.NewPageOnNextWrite)");
            sb.AppendLine($"{pad}    {resolvedWriteCall};");
            sb.AppendLine($"{pad}else {streamExpr}.EndCurrentPage();");
            if (hasCond)
            {
                pad = pad[..^4];
                sb.AppendLine($"{pad}}}");
            }
            return;
        }
        sb.AppendLine($"{pad}{resolvedWriteCall};");
        if (hasCond)
        {
            pad = pad[..^4];
            sb.AppendLine($"{pad}}}");
        }
    }

    private static void EmitOrderedGroupActions(
        StringBuilder sb,
        IReadOnlyList<TaskRowActionDef> actions,
        IReadOnlyList<TaskFormIoDef> ios,
        IReadOnlyDictionary<int, string> writeCallMap,
        TaskSemantic t,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        string pad)
    {
        var ordered = new List<(int Order, TaskRowActionDef? Action, TaskFormIoDef? Io)>();
        var nextSyntheticOrder = 1_000_000;
        foreach (var action in actions)
        {
            var order = ExtractLogicLineOrder(action.XmlTrace) ?? nextSyntheticOrder++;
            ordered.Add((order, action, null));
        }
        foreach (var io in ios)
        {
            var order = ExtractLogicLineOrder(io.XmlTrace) ?? nextSyntheticOrder++;
            ordered.Add((order, null, io));
        }

        foreach (var item in ordered.OrderBy(x => x.Order))
        {
            if (item.Action is not null)
            {
                if (!EmitDirectResourceAssignment(sb, item.Action, t, dataObjects, allTasks, pad))
                    EmitRowActionCore(sb, item.Action, t, dataObjects, allTasks, pad, preferValueForResourceAssignments: true);
                continue;
            }
            if (item.Io is null || !item.Io.FormEntryIndex.HasValue || !writeCallMap.TryGetValue(item.Io.FormEntryIndex.Value, out var writeCall))
                continue;
            EmitFormIoWrite(sb, item.Io, writeCall, t, dataObjects, pad);
        }
    }

    private static bool IsMergeTemplateVariable(string variableName, TaskSemantic t)
    {
        if (string.IsNullOrWhiteSpace(variableName))
            return false;

        var trimmed = variableName.Trim();
        if (trimmed.StartsWith("_parent.", StringComparison.Ordinal))
            trimmed = trimmed[(trimmed.LastIndexOf("_parent.", StringComparison.Ordinal) + "_parent.".Length)..];

        return t.Layout.MergeTemplateVariableNamesByFormEntryIndex.Values
            .Any(v => string.Equals(v, trimmed, StringComparison.Ordinal));
    }

    private static bool TryParsePrintWriteCall(string writeCall, out string sectionExpr, out string streamExpr)
    {
        sectionExpr = "";
        streamExpr = "";
        var m = Regex.Match(writeCall, @"^(?<section>.+?)\.WriteTo\((?<stream>_[A-Za-z0-9_]+)\)$");
        if (!m.Success)
            return false;
        sectionExpr = m.Groups["section"].Value;
        streamExpr = m.Groups["stream"].Value;
        return true;
    }

    private static void EmitFormIoRead(StringBuilder sb, TaskFormIoDef io, string readCall, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, string pad)
    {
        readCall = ReplaceBareIoReportPlaceholder(readCall, ResolveTextIoStreamVariableForIo(t, io));
        if (io.FormEntryIndex.HasValue && readCall.Contains("_layout.", StringComparison.Ordinal))
            readCall = readCall.Replace("_layout.", ResolveTextIoLayoutVariableName(t, io.FormEntryIndex.Value) + ".", StringComparison.Ordinal);
        var cond = io.ConditionExpressionId.HasValue
            ? ResolveExpressionCode(io.ConditionExpressionId.Value.ToString(), t, dataObjects, CreateBooleanConditionEmissionContext())
            : "";
        var hasCond = !string.IsNullOrWhiteSpace(cond);
        if (hasCond)
        {
            sb.AppendLine($"{pad}if ({cond})");
            sb.AppendLine($"{pad}{{");
            pad += "    ";
        }
        sb.AppendLine($"{pad}{readCall};");
        if (hasCond)
        {
            pad = pad[..^4];
            sb.AppendLine($"{pad}}}");
        }
    }

    private static string ResolveGroupReferenceExpression(string reference, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "";
        if (!task.SelectsSemantic.ItemsByName.TryGetValue(reference, out var sel) || sel is null)
            return "";
        var primaryObj = task.PrimaryDbObj ?? task.InformationDbObj;
        var primaryMember = ResolvePrimaryMember(task, primaryObj ?? 0, dataObjects);
        var expr = ResolveSelectExpression(sel, task, dataObjects, primaryMember);
        if (string.IsNullOrWhiteSpace(expr))
            return "";
        return expr;
    }

    private static string BuildPrintLayoutClassName(TaskSemantic t)
    {
        return t.Layout.PrintLayoutClassName;
    }

    private static string BuildTextIoLayoutClassName(TaskSemantic t, IReadOnlyList<TaskSemantic> allTasks, TaskFormEntryDef? preferredTextForm = null)
    {
        if (!string.IsNullOrWhiteSpace(t.Layout.TextIoLayoutClassName) && preferredTextForm is null)
            return t.Layout.TextIoLayoutClassName!;
        var textForms = t.Layout.TextForms;
        var referencedFormIndexes = t.Layout.ReferencedFormIndexes;
        var textForm = preferredTextForm
            ?? textForms.FirstOrDefault(fe => referencedFormIndexes.Contains(fe.Index))
            ?? textForms.FirstOrDefault();
        var componentIndex = textForm?.ClassIndex ?? textForm?.Index ?? t.SubtaskIndex.GetValueOrDefault(1);
        if (componentIndex < 1)
            componentIndex = 1;

        if (!t.ParentOrdinal.HasValue)
            return ToTextIoTaskNameToken(t) + $"C{componentIndex}";
        var root = t;
        while (root.ParentOrdinal.HasValue)
        {
            var parent = allTasks.FirstOrDefault(x => x.Ordinal == root.ParentOrdinal.Value);
            if (parent is null)
                break;
            root = parent;
        }
        var suffix = ToTextIoTaskNameToken(t).TrimStart('_');
        if (string.IsNullOrWhiteSpace(suffix))
            suffix = "Task";
        return $"{ToTextIoTaskNameToken(root)}C{componentIndex}_{suffix}";
    }

    private static string ToTextIoTaskNameToken(TaskSemantic task)
    {
        var descToken = ToTaskClassName(task.Description);
        var usePublicName = !string.IsNullOrWhiteSpace(task.PublicName);
        if (usePublicName && Regex.IsMatch(task.PublicName!.Trim(), @"^[A-Za-z]+\d+$"))
            usePublicName = false;
        if (usePublicName)
        {
            var publicTrimmed = task.PublicName!.Trim();
            var publicLooksLikeShortCode = Regex.IsMatch(publicTrimmed, @"^[A-Za-z]{1,6}\d+[A-Za-z]?$", RegexOptions.IgnoreCase);
            var descHasMoreMeaning = descToken.StartsWith(publicTrimmed, StringComparison.OrdinalIgnoreCase)
                                     && descToken.Length > publicTrimmed.Length;
            if (publicLooksLikeShortCode && descHasMoreMeaning)
                usePublicName = false;
        }
        var raw = usePublicName
            ? task.PublicName!.Trim()
            : descToken;
        var token = Regex.Replace(raw, @"[^A-Za-z0-9_]+", "_");
        token = Regex.Replace(token, "_{2,}", "_");
        token = token.Trim('_');
        if (string.IsNullOrWhiteSpace(token))
            token = "UnnamedTask";
        if (char.IsDigit(token[0]))
            token = "_" + token;
        return token;
    }

    private static string ResolveTextIoNamespaceSegment(TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (!string.IsNullOrWhiteSpace(task.Layout.TextIoNamespaceSegment))
            return task.Layout.TextIoNamespaceSegment!;
        var folder = ResolveEffectiveTaskOutputFolder(task, allTasks);
        return string.IsNullOrWhiteSpace(folder) ? "TextIO" : $"{folder}.TextIO";
    }

    private static IReadOnlyList<TaskFormEntryDef> GetEffectiveTextIoForms(TaskSemantic task)
    {
        var textForms = task.Layout.TextForms.OrderBy(x => x.Index).ToList();
        var referencedFormIndexes = task.Layout.ReferencedFormIndexes;
        if (referencedFormIndexes.Count > 0)
        {
            var filtered = textForms.Where(x => referencedFormIndexes.Contains(x.Index)).ToList();
            if (filtered.Count > 0)
                textForms = filtered;
        }
        return textForms;
    }

    private static string ResolveTextIoLayoutVariableName(TaskSemantic task, int formEntryIndex)
    {
        var textForms = GetEffectiveTextIoForms(task);
        if (textForms.Count == 0)
            return "_layout";
        var first = textForms[0];
        if (first.Index == formEntryIndex)
            return "_layout";
        var formEntry = textForms.FirstOrDefault(x => x.Index == formEntryIndex);
        var suffix = formEntry?.ClassIndex ?? formEntry?.Index ?? formEntryIndex;
        return $"_layoutC{suffix}";
    }

    private static string ResolvePrintingNamespaceSegment(TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks)
    {
        var folder = ResolveEffectiveTaskOutputFolder(task, allTasks);
        return string.IsNullOrWhiteSpace(folder) ? "Printing" : $"{folder}.Printing";
    }

    private static void WritePrintingLayouts(IReadOnlyList<TaskSemantic> tasks, IReadOnlyList<DataObjectDef> dataObjects, string outputRoot, string appNamespace)
    {
        var allTasks = _allTasks ?? tasks;

        foreach (var task in tasks.Where(HasPrintLayout))
        {
            var taskFolder = ResolveEffectiveTaskOutputFolder(task, allTasks);
            var printingDir = string.IsNullOrWhiteSpace(taskFolder)
                ? Path.Combine(outputRoot, "Printing")
                : Path.Combine(outputRoot, taskFolder, "Printing");
            Directory.CreateDirectory(printingDir);
            var controllerClass = ResolveTaskTypeReference(task, allTasks);
            var layoutClass = BuildPrintLayoutClassName(task);
            var printingNamespace = $"{appNamespace}.{ResolvePrintingNamespaceSegment(task, allTasks)}";
            var printForms = task.Layout.PrintForms.OrderBy(x => x.Index).ToList();
            if (printForms.Count == 0)
                continue;

            var code = new StringBuilder();
            code.AppendLine($"namespace {printingNamespace};");
            code.AppendLine();
            code.AppendLine($"partial class {layoutClass} : Shared.Theme.Printing.ReportLayout");
            code.AppendLine("{");
            code.AppendLine($"    readonly {controllerClass} _controller;");
            code.AppendLine($"    internal {layoutClass}({controllerClass} controller) : base(controller)");
            code.AppendLine("    {");
            code.AppendLine("        _controller = controller;");
            code.AppendLine("        InitializeComponent();");
            code.AppendLine("    }");
            code.AppendLine("}");
            File.WriteAllText(Path.Combine(printingDir, $"{layoutClass}.cs"), code.ToString());

            var designer = new StringBuilder();
            designer.AppendLine("using System.Drawing;");
            designer.AppendLine($"using {appNamespace}.Shared.Theme;");
            designer.AppendLine();
            designer.AppendLine($"namespace {printingNamespace};");
            designer.AppendLine();
            designer.AppendLine($"partial class {layoutClass}");
            designer.AppendLine("{");
            var sectionInfos = new List<(TaskFormEntryDef FormEntry, string SectionName)>();
            for (var i = 0; i < printForms.Count; i++)
            {
                var sectionName = task.Layout.PrintSectionNamesByFormEntryIndex.TryGetValue(printForms[i].Index, out var mapped)
                    ? mapped
                    : ResolvePrintSectionName(printForms[i], i + 1);
                sectionInfos.Add((printForms[i], sectionName));
                designer.AppendLine($"    internal Shared.Theme.Printing.ReportSection {sectionName};");
            }
            var varByControlKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sectionInfos)
            {
                foreach (var c in GetSupportedPrintControls(task, s.FormEntry.Index))
                {
                    var type = ResolvePrintControlTypeName(task, s.FormEntry.Index, c);
                    var varName = ResolvePrintControlVariableName(task, s.FormEntry.Index, c);
                    varByControlKey[BuildPrintControlKey(s.FormEntry.Index, c.Id)] = varName;
                    designer.AppendLine($"    {type} {varName};");
                }
            }
            designer.AppendLine();
            designer.AppendLine("    void InitializeComponent()");
            designer.AppendLine("    {");
            var y = 0;
            foreach (var s in sectionInfos)
            {
                var headerSectionName = task.Layout.PageHeaderSectionName;
                designer.AppendLine($"        {s.SectionName} = new Shared.Theme.Printing.ReportSection();");
                designer.AppendLine($"        {s.SectionName}.Name = \"{s.SectionName}\";");
                designer.AppendLine($"        {s.SectionName}.Height = {Math.Max(12, s.FormEntry.Form.Height)};");
                designer.AppendLine($"        {s.SectionName}.Location = new Point(0, {y});");
                foreach (var uc in GetUnsupportedPrintControls(task, s.FormEntry.Index))
                    designer.AppendLine($"        // GAP: Print control model '{uc.Model}' not mapped (FormEntry={s.FormEntry.Index}, ControlId={uc.Id}).");
                if (string.Equals(s.SectionName, headerSectionName, StringComparison.Ordinal))
                    designer.AppendLine($"        {s.SectionName}.PageHeader = true;");
                designer.AppendLine($"        Controls.Add({s.SectionName});");
                y += Math.Max(12, s.FormEntry.Form.Height);
            }
            foreach (var s in sectionInfos)
            {
                foreach (var c in GetSupportedPrintControls(task, s.FormEntry.Index))
                {
                    var varName = varByControlKey[BuildPrintControlKey(s.FormEntry.Index, c.Id)];
                    var locationX = c.X;
                    var locationY = c.Y;
                    designer.AppendLine($"        {varName} = new {ResolvePrintControlTypeName(task, s.FormEntry.Index, c)}();");
                    designer.AppendLine($"        {varName}.Location = new Point({locationX}, {locationY});");
                    designer.AppendLine($"        {varName}.Size = new Size({Math.Max(1, c.Width)}, {Math.Max(1, c.Height)});");
                    if (c.ColorSchemeId.HasValue)
                        designer.AppendLine($"        {varName}.ColorScheme = ColorSchemes.Find({c.ColorSchemeId.Value});");
                    if (c.FontSchemeId.HasValue)
                        designer.AppendLine($"        {varName}.FontScheme = FontSchemes.Find({c.FontSchemeId.Value});");
                    if (IsPrintTableControl(c))
                    {
                        if (c.TitleHeight.HasValue)
                            designer.AppendLine($"        {varName}.HeaderHeight = {c.TitleHeight.Value};");
                        if (c.RowHeight.HasValue)
                            designer.AppendLine($"        {varName}.RowHeight = {c.RowHeight.Value};");
                    }
                    else if (IsPrintTableColumnControl(c))
                    {
                        if (!string.IsNullOrWhiteSpace(c.ColumnTitle))
                            designer.AppendLine($"        {varName}.Text = {ToCSharpLiteral(c.ColumnTitle)};");
                        designer.AppendLine($"        {varName}.Width = {Math.Max(1, c.Width)};");
                    }
                    else if (c.Model == "CTRL_GUI1_LINE")
                    {
                        designer.AppendLine($"        {varName}.Start = new Point({c.X}, {c.Y});");
                        designer.AppendLine($"        {varName}.End = new Point({c.X + Math.Max(1, c.Width)}, {c.Y + Math.Max(0, c.Height)});");
                    }
                    else if (!string.IsNullOrWhiteSpace(c.Text))
                    {
                        designer.AppendLine($"        {varName}.Text = {ToCSharpLiteral(c.Text)};");
                    }

                    var dataExpr = ResolveControlDataExpression(c, task, tasks, dataObjects);
                    if (!string.IsNullOrWhiteSpace(dataExpr))
                    {
                        designer.AppendLine($"        {varName}.Data = _controller.{dataExpr};");
                    }
                    else if (c.DataExpressionId.HasValue)
                    {
                        if (task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(c.DataExpressionId.Value, out var exp) && exp is not null)
                        {
                            var fromMethod = exp.Attribute switch
                            {
                                "N" => "FromNumber",
                                "D" => "FromDate",
                                "T" => "FromTime",
                                "B" => "FromBool",
                                _ => "FromText"
                            };
                            designer.AppendLine($"        {varName}.Data = XPARuntimeCore.Box.UI.Advanced.ControlData.{fromMethod}(_controller.Exp_{c.DataExpressionId.Value});");
                        }
                        else
                            designer.AppendLine($"        // GAP: Print data binding not resolved for {varName} (FormEntry={s.FormEntry.Index}, ControlId={c.Id}, DataExpressionId={c.DataExpressionId.Value}).");
                    }
                }
            }

            foreach (var s in sectionInfos)
            {
                var controls = GetSupportedPrintControls(task, s.FormEntry.Index).ToList();
                var controlById = controls.ToDictionary(c => c.Id);
                var tables = controls.Where(IsPrintTableControl).ToList();
                var columns = controls.Where(IsPrintTableColumnControl).ToList();
                var sectionRootControlIds = GetPrintSectionRootControlIds(task, s.FormEntry.Index);
                var columnChildIdsByColumn = GetPrintColumnChildIdsByColumn(task, s.FormEntry.Index);
                var tableChildIdsByTable = GetPrintTableChildIdsByTable(task, s.FormEntry.Index);

                foreach (var col in columns)
                {
                    var colVar = varByControlKey[BuildPrintControlKey(s.FormEntry.Index, col.Id)];
                    if (!columnChildIdsByColumn.TryGetValue(col.Id, out var childIds))
                        continue;
                    foreach (var childId in childIds)
                    {
                        if (!controlById.TryGetValue(childId, out var child))
                            continue;
                        var childVar = varByControlKey[BuildPrintControlKey(s.FormEntry.Index, child.Id)];
                        designer.AppendLine($"        {colVar}.Controls.Add({childVar});");
                    }
                }
                foreach (var table in tables)
                {
                    var tableVar = varByControlKey[BuildPrintControlKey(s.FormEntry.Index, table.Id)];
                    if (!tableChildIdsByTable.TryGetValue(table.Id, out var childIds))
                        continue;
                    foreach (var childId in childIds)
                    {
                        if (!controlById.TryGetValue(childId, out var child))
                            continue;
                        var childVar = varByControlKey[BuildPrintControlKey(s.FormEntry.Index, child.Id)];
                        designer.AppendLine($"        {tableVar}.Controls.Add({childVar});");
                    }
                }

                foreach (var controlId in sectionRootControlIds)
                {
                    if (!controlById.TryGetValue(controlId, out var c))
                        continue;
                    var varName = varByControlKey[BuildPrintControlKey(s.FormEntry.Index, c.Id)];
                    designer.AppendLine($"        {s.SectionName}.Controls.Add({varName});");
                }
            }
            designer.AppendLine($"        Name = \"{layoutClass}\";");
            designer.AppendLine($"        Width = {Math.Max(100, sectionInfos.Max(x => x.FormEntry.Form.Width))};");
            designer.AppendLine("    }");
            designer.AppendLine("}");
            File.WriteAllText(Path.Combine(printingDir, $"{layoutClass}.Designer.cs"), designer.ToString());
        }
    }

    private static void WriteTextIoLayouts(IReadOnlyList<TaskSemantic> tasks, IReadOnlyList<DataObjectDef> dataObjects, string outputRoot, string appNamespace)
    {
        var allTasks = _allTasks ?? tasks;
        foreach (var task in tasks.Where(HasTextIoLayout))
        {
            var taskFolder = ResolveEffectiveTaskOutputFolder(task, allTasks);
            var textIoDir = string.IsNullOrWhiteSpace(taskFolder)
                ? Path.Combine(outputRoot, "TextIO")
                : Path.Combine(outputRoot, taskFolder, "TextIO");
            Directory.CreateDirectory(textIoDir);
            var controllerType = ResolveTaskTypeReference(task, allTasks);
            var textIoNamespace = $"{appNamespace}.{ResolveTextIoNamespaceSegment(task, allTasks)}";
            var textForms = GetEffectiveTextIoForms(task).ToList();
            if (textForms.Count == 0)
                continue;
            foreach (var layoutGroup in textForms.GroupBy(tf => BuildTextIoLayoutClassName(task, allTasks, tf), StringComparer.Ordinal))
            {
                var layoutClass = layoutGroup.Key;
                var groupedForms = layoutGroup.ToList();

                var code = new StringBuilder();
                code.AppendLine($"namespace {textIoNamespace};");
                code.AppendLine();
                code.AppendLine($"partial class {layoutClass} : Shared.Theme.TextIO.TextLayout");
                code.AppendLine("{");
                code.AppendLine($"    readonly {controllerType} _controller;");
                code.AppendLine($"    internal {layoutClass}({controllerType} controller) : base(controller)");
                code.AppendLine("    {");
                code.AppendLine("        _controller = controller;");
                code.AppendLine("        InitializeComponent();");
                code.AppendLine("    }");
                code.AppendLine("}");
                File.WriteAllText(Path.Combine(textIoDir, $"{layoutClass}.cs"), code.ToString());

                var designer = new StringBuilder();
                designer.AppendLine("using System.Drawing;");
                designer.AppendLine();
                designer.AppendLine($"namespace {textIoNamespace};");
                designer.AppendLine();
                designer.AppendLine($"partial class {layoutClass}");
                designer.AppendLine("{");
                var sectionInfos = groupedForms
                    .Select((form, index) =>
                    {
                        var sectionName = task.Layout.TextSectionNamesByFormEntryIndex.TryGetValue(form.Index, out var mapped)
                            ? mapped
                            : ResolveTextIoSectionName(form, index + 1);
                        return (FormEntry: form, SectionName: sectionName);
                    })
                    .ToList();
                foreach (var (_, sectionName) in sectionInfos)
                    designer.AppendLine($"    internal Shared.Theme.TextIO.TextSection {sectionName};");
                var varByControlKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (textForm, _) in sectionInfos)
                {
                    foreach (var c in GetOrderedTextIoControls(task, textForm.Index))
                    {
                        var type = ResolveTextIoControlTypeName(task, textForm.Index, c);
                        var varName = ResolveTextIoControlVariableName(task, textForm.Index, c);
                        varByControlKey[BuildPrintControlKey(textForm.Index, c.Id)] = varName;
                        designer.AppendLine($"    {type} {varName};");
                    }
                }
                designer.AppendLine();
                designer.AppendLine("    void InitializeComponent()");
                designer.AppendLine("    {");
                designer.AppendLine("        SuspendLayout();");
                foreach (var (textForm, sectionName) in sectionInfos)
                {
                    designer.AppendLine($"        {sectionName} = new Shared.Theme.TextIO.TextSection();");
                    designer.AppendLine($"        {sectionName}.SuspendLayout();");
                    designer.AppendLine($"        {sectionName}.Name = \"{sectionName}\";");
                    designer.AppendLine($"        {sectionName}.HeightInChars = {Math.Max(1, textForm.Form.Height)};");
                    if (task.Layout.TextIoPageHeaderSectionNames.Contains(sectionName))
                        designer.AppendLine($"        {sectionName}.PageHeader = true;");
                    foreach (var uc in GetUnsupportedTextIoControls(task, textForm.Index))
                        designer.AppendLine($"        // GAP: TextIO control model '{uc.Model}' not mapped (FormEntry={textForm.Index}, ControlId={uc.Id}).");

                    foreach (var c in GetOrderedTextIoControls(task, textForm.Index))
                    {
                        var varName = varByControlKey[BuildPrintControlKey(textForm.Index, c.Id)];
                        designer.AppendLine($"        {varName} = new {ResolveTextIoControlTypeName(task, textForm.Index, c)}();");
                        if (c.Model == "CTRL_TEXT_LINE")
                        {
                            designer.AppendLine($"        {varName}.Start = new Point({c.X}, {c.Y});");
                            designer.AppendLine($"        {varName}.End = new Point({c.Width}, {c.Height});");
                        }
                        else
                        {
                            designer.AppendLine($"        {varName}.LocationInChars = new Point({c.X}, {c.Y});");
                            designer.AppendLine($"        {varName}.SizeInChars = new Size({Math.Max(1, c.Width)}, {Math.Max(1, c.Height)});");
                        }
                        designer.AppendLine($"        {varName}.Name = \"{varName}\";");
                        if (c.Model == "CTRL_TEXT_STATIC")
                        {
                            if (c.HorizontalAlignment == 3)
                                designer.AppendLine($"        {varName}.Alignment = System.Drawing.ContentAlignment.MiddleRight;");
                            else if (c.HorizontalAlignment == 2)
                                designer.AppendLine($"        {varName}.Alignment = System.Drawing.ContentAlignment.MiddleCenter;");
                        }
                        if (c.Model == "CTRL_TEXT_EDIT")
                        {
                            if (c.HorizontalAlignment == 3 || ShouldRightAlignTextIoEdit(c, task, dataObjects, allTasks))
                                designer.AppendLine($"        {varName}.Alignment = System.Drawing.ContentAlignment.TopRight;");
                        }
                        if (!string.IsNullOrWhiteSpace(c.Text))
                        {
                            if (c.Model == "CTRL_TEXT_EDIT")
                                designer.AppendLine($"        {varName}.Format = {ToCSharpLiteral(c.Text)};");
                            else if (c.Model == "CTRL_TEXT_STATIC")
                                designer.AppendLine($"        {varName}.Text = {ToCSharpLiteral(c.Text)};");
                        }
                        if (c.DataExpressionId.HasValue || !string.IsNullOrWhiteSpace(c.DataColumn))
                        {
                            var bindExpr = EnsureTextIoControllerBinding(ResolveControlDataExpressionBindingForView(c, task, dataObjects, allTasks));
                            if (!string.IsNullOrWhiteSpace(bindExpr))
                                designer.AppendLine($"        {varName}.Data = {bindExpr};");
                            else
                                designer.AppendLine($"        // GAP: TextIO data binding not resolved for {varName} (FormEntry={textForm.Index}, ControlId={c.Id}, DataExpressionId={c.DataExpressionId?.ToString() ?? "?"}, DataColumn={c.DataColumn ?? "?"}).");
                        }
                        designer.AppendLine($"        {sectionName}.Controls.Add({varName});");
                    }
                    designer.AppendLine($"        Controls.Add({sectionName});");
                    designer.AppendLine($"        {sectionName}.ResumeLayout(false);");
                }
                designer.AppendLine($"        Name = \"{layoutClass}\";");
                designer.AppendLine("        UseScaleConversion = false;");
                designer.AppendLine($"        WidthInChars = {Math.Max(1, groupedForms.Max(x => x.Form.Width))};");
                designer.AppendLine("        ResumeLayout(false);");
                designer.AppendLine("    }");
                designer.AppendLine("}");
                File.WriteAllText(Path.Combine(textIoDir, $"{layoutClass}.Designer.cs"), designer.ToString());
            }
        }
    }

    private static string BuildPrintControlKey(int formEntryIndex, int controlId) => $"{formEntryIndex}:{controlId}";

    private static IReadOnlyList<TaskFormControlDef> GetSupportedPrintControls(TaskSemantic task, int formEntryIndex)
    {
        return task.Layout.SupportedPrintControlsByFormEntryIndex.TryGetValue(formEntryIndex, out var controls)
            ? controls
            : Array.Empty<TaskFormControlDef>();
    }

    private static IReadOnlyList<TaskFormControlDef> GetUnsupportedPrintControls(TaskSemantic task, int formEntryIndex)
    {
        return task.Layout.UnsupportedPrintControlsByFormEntryIndex.TryGetValue(formEntryIndex, out var controls)
            ? controls
            : Array.Empty<TaskFormControlDef>();
    }

    private static IReadOnlyList<TaskFormControlDef> GetSupportedTextIoControls(TaskSemantic task, int formEntryIndex)
    {
        return task.Layout.SupportedTextIoControlsByFormEntryIndex.TryGetValue(formEntryIndex, out var controls)
            ? controls
            : Array.Empty<TaskFormControlDef>();
    }

    private static IReadOnlyList<TaskFormControlDef> GetUnsupportedTextIoControls(TaskSemantic task, int formEntryIndex)
    {
        return task.Layout.UnsupportedTextIoControlsByFormEntryIndex.TryGetValue(formEntryIndex, out var controls)
            ? controls
            : Array.Empty<TaskFormControlDef>();
    }

    private static IReadOnlyList<TaskFormControlDef> GetOrderedTextIoControls(TaskSemantic task, int formEntryIndex)
    {
        if (!task.Layout.TextIoSectionControlIdsByFormEntryIndex.TryGetValue(formEntryIndex, out var ids))
            return GetSupportedTextIoControls(task, formEntryIndex);
        var controls = GetSupportedTextIoControls(task, formEntryIndex).ToDictionary(c => c.Id);
        return ids.Where(controls.ContainsKey).Select(id => controls[id]).ToList();
    }

    private static IReadOnlyDictionary<int, int> GetPrintTableAttachmentByLeaf(TaskSemantic task, int formEntryIndex)
    {
        return task.Layout.PrintTableAttachmentByLeafByFormEntryIndex.TryGetValue(formEntryIndex, out var map)
            ? map
            : EmptyIntMap;
    }

    private static IReadOnlyDictionary<int, int> GetPrintColumnAttachmentByLeaf(TaskSemantic task, int formEntryIndex)
    {
        return task.Layout.PrintColumnAttachmentByLeafByFormEntryIndex.TryGetValue(formEntryIndex, out var map)
            ? map
            : EmptyIntMap;
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> GetPrintColumnChildIdsByColumn(TaskSemantic task, int formEntryIndex)
    {
        return task.Layout.PrintColumnChildIdsByColumnByFormEntryIndex.TryGetValue(formEntryIndex, out var map)
            ? map
            : EmptyIntListMap;
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> GetPrintTableChildIdsByTable(TaskSemantic task, int formEntryIndex)
    {
        return task.Layout.PrintTableChildIdsByTableByFormEntryIndex.TryGetValue(formEntryIndex, out var map)
            ? map
            : EmptyIntListMap;
    }

    private static IReadOnlyList<int> GetPrintSectionRootControlIds(TaskSemantic task, int formEntryIndex)
    {
        return task.Layout.PrintSectionRootControlIdsByFormEntryIndex.TryGetValue(formEntryIndex, out var ids)
            ? ids
            : Array.Empty<int>();
    }

    private static string ResolvePrintControlVariableName(TaskSemantic task, int formEntryIndex, TaskFormControlDef c)
    {
        return task.Layout.PrintControlVariableNameByFormEntryIndex.TryGetValue(formEntryIndex, out var map) &&
               map.TryGetValue(c.Id, out var name)
            ? name
            : ResolvePrintControlVariableName(c, formEntryIndex);
    }

    private static string ResolveTextIoControlVariableName(TaskSemantic task, int formEntryIndex, TaskFormControlDef c)
    {
        return task.Layout.TextIoControlVariableNameByFormEntryIndex.TryGetValue(formEntryIndex, out var map) &&
               map.TryGetValue(c.Id, out var name)
            ? name
            : ResolveTextIoControlVariableNameFallback(c, formEntryIndex);
    }

    private static string ResolveTextIoControlVariableNameFallback(TaskFormControlDef c, int formEntryIndex)
    {
        if (c.DataExpressionId.HasValue)
            return $"txtExp_{c.DataExpressionId.Value}";
        if (c.Model == "CTRL_TEXT_STATIC" && string.IsNullOrWhiteSpace(c.ControlName) && !string.IsNullOrWhiteSpace(c.Text))
        {
            var normalizedText = NormalizeIdentifierTokensWithUnderscore(c.Text);
            if (!string.IsNullOrWhiteSpace(normalizedText))
                return "lbl" + normalizedText;
        }
        var suffix = !string.IsNullOrWhiteSpace(c.ControlName)
            ? ToPascalIdentifier(c.ControlName)
            : c.Id.ToString();
        var prefix = c.Model switch
        {
            "CTRL_TEXT_STATIC" => "lbl",
            "CTRL_TEXT_EDIT" => "txt",
            "CTRL_TEXT_LINE" => "lin",
            "CTRL_TEXT_SHAPE" => "shp",
            _ => "ctl"
        };
        return $"{prefix}S{formEntryIndex}_{suffix}";
    }

    private static readonly IReadOnlyDictionary<int, int> EmptyIntMap = new Dictionary<int, int>();
    private static readonly IReadOnlyDictionary<int, IReadOnlyList<int>> EmptyIntListMap = new Dictionary<int, IReadOnlyList<int>>();

    private static bool IsSupportedPrintControl(TaskFormControlDef c)
    {
        return c.Model is "CTRL_GUI1_STATIC" or "CTRL_GUI1_EDIT" or "CTRL_GUI1_LINE" or "CTRL_GUI1_SHAPE" or "CTRL_GUI1_TABLE" or "CTRL_GUI1_COLUMN";
    }

    private static bool IsSupportedTextIoControl(TaskFormControlDef c)
    {
        return c.Model is "CTRL_TEXT_STATIC" or "CTRL_TEXT_EDIT" or "CTRL_TEXT_LINE" or "CTRL_TEXT_SHAPE";
    }

    private static string ResolveTextIoControlTypeName(TaskSemantic task, int formEntryIndex, TaskFormControlDef c)
    {
        return task.Layout.TextIoControlTypeNameByFormEntryIndex.TryGetValue(formEntryIndex, out var map) &&
               map.TryGetValue(c.Id, out var type)
            ? type
            : ResolveTextIoControlTypeName(c);
    }

    private static string ResolveTextIoControlTypeName(TaskFormControlDef c)
    {
        return c.Model switch
        {
            "CTRL_TEXT_STATIC" => "Shared.Theme.TextIO.TextLabel",
            "CTRL_TEXT_EDIT" => "Shared.Theme.TextIO.TextBox",
            "CTRL_TEXT_LINE" => "Shared.Theme.TextIO.Line",
            "CTRL_TEXT_SHAPE" => "Shared.Theme.TextIO.Shape",
            _ => "Shared.Theme.TextIO.TextBox"
        };
    }

    private static string ResolveTextIoControlVariableName(
        TaskFormControlDef c,
        int formEntryIndex,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (c.DataExpressionId.HasValue)
            return $"txtExp_{c.DataExpressionId.Value}";
        if (c.Model == "CTRL_TEXT_EDIT" && (string.IsNullOrWhiteSpace(c.ControlName) || IsLikelyGeneratedControlName(c.ControlName)))
        {
            var dataExpr = ResolveControlDataExpression(c, task, allTasks, dataObjects);
            if (string.IsNullOrWhiteSpace(dataExpr))
                dataExpr = ResolveDataColumnOrdinalBinding(c.DataColumn, task, allTasks);
            if (IsSimpleMemberAccess(dataExpr))
            {
                var member = dataExpr.Split('.').LastOrDefault();
                if (!string.IsNullOrWhiteSpace(member))
                    return "txt" + ToPascalIdentifier(member);
            }
        }
        if (c.Model == "CTRL_TEXT_STATIC" && string.IsNullOrWhiteSpace(c.ControlName) && !string.IsNullOrWhiteSpace(c.Text))
        {
            var normalizedText = NormalizeIdentifierTokensWithUnderscore(c.Text);
            if (!string.IsNullOrWhiteSpace(normalizedText))
                return "lbl" + normalizedText;
        }
        var suffix = !string.IsNullOrWhiteSpace(c.ControlName)
            ? ToPascalIdentifier(c.ControlName)
            : c.Id.ToString();
        var prefix = c.Model switch
        {
            "CTRL_TEXT_STATIC" => "lbl",
            "CTRL_TEXT_EDIT" => "txt",
            "CTRL_TEXT_LINE" => "lin",
            "CTRL_TEXT_SHAPE" => "shp",
            _ => "ctl"
        };
        return $"{prefix}S{formEntryIndex}_{suffix}";
    }

    private static string NormalizeIdentifierTokensWithUnderscore(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var raw = Regex.Replace(value, @"[^A-Za-z0-9]+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var tokens = raw
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => Regex.IsMatch(t, @"^\d+$") ? t : ToPascalIdentifier(t))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        if (tokens.Count == 0)
            return "";
        var joined = string.Join("_", tokens);
        if (!Regex.IsMatch(joined, @"^[A-Za-z_]"))
            joined = "_" + joined;
        return joined;
    }

    private static string ResolveTextIoSectionName(TaskFormEntryDef formEntry, int sequence)
    {
        var baseName = ToPascalIdentifier(formEntry.Form.FormName ?? "");
        if (string.IsNullOrWhiteSpace(baseName) || baseName == "Unnamed")
            baseName = "Section" + sequence;
        return baseName;
    }

    private static bool ShouldRightAlignTextIoEdit(
        TaskFormControlDef c,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        if (c.DataExpressionId.HasValue)
        {
            if (task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(c.DataExpressionId.Value, out var exp) &&
                exp is not null &&
                string.Equals(exp.Attribute, "N", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(c.DataColumn))
        {
            var selectName = c.DataColumn!.Trim();
            if (task.SelectsSemantic.ItemsByName.TryGetValue(selectName, out var select) && select is not null)
            {
                task.ResourcesSemantic.ById.TryGetValue(select.ColumnId, out var resourceColumn);
                if (resourceColumn is not null && IsNumericAttrObj(resourceColumn.AttrObj))
                    return true;
            }

            var expr = ResolveSelectExpressionByName(selectName, task, dataObjects);
            if (string.IsNullOrWhiteSpace(expr))
                expr = ResolveDataColumnOrdinalBinding(selectName, task, allTasks);
            if (!string.IsNullOrWhiteSpace(expr))
            {
                var memberMatch = Regex.Match(expr, @"\.([A-Za-z_][A-Za-z0-9_]*)\s*$");
                if (memberMatch.Success)
                {
                    var memberName = memberMatch.Groups[1].Value;
                    foreach (var d in dataObjects)
                    {
                        var col = d.Columns.FirstOrDefault(dc =>
                            string.Equals(ToPascalIdentifier(dc.Name), memberName, StringComparison.Ordinal) ||
                            string.Equals(ToPascalIdentifier(dc.DbColumnName ?? ""), memberName, StringComparison.Ordinal));
                        if (col is not null && IsNumericAttrObj(col.AttrObj))
                            return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsNumericAttrObj(string? attrObj)
    {
        if (string.IsNullOrWhiteSpace(attrObj))
            return false;
        var a = attrObj.Trim().ToUpperInvariant();
        return a.Contains("NUMERIC") || a.Contains("MONEY");
    }

    private static bool IsPrintTableControl(TaskFormControlDef c) => c.Model == "CTRL_GUI1_TABLE";
    private static bool IsPrintTableColumnControl(TaskFormControlDef c) => c.Model == "CTRL_GUI1_COLUMN";
    private static bool IsPrintLeafControl(TaskFormControlDef c) => c.Model is "CTRL_GUI1_STATIC" or "CTRL_GUI1_EDIT" or "CTRL_GUI1_LINE" or "CTRL_GUI1_SHAPE";

    private static string ResolvePrintControlTypeName(TaskSemantic task, int formEntryIndex, TaskFormControlDef c)
    {
        return task.Layout.PrintControlTypeNameByFormEntryIndex.TryGetValue(formEntryIndex, out var map) &&
               map.TryGetValue(c.Id, out var type)
            ? type
            : ResolvePrintControlTypeName(c);
    }

    private static string ResolvePrintControlTypeName(TaskFormControlDef c)
    {
        return c.Model switch
        {
            "CTRL_GUI1_STATIC" => "Shared.Theme.Printing.Label",
            "CTRL_GUI1_EDIT" => "Shared.Theme.Printing.TextBox",
            "CTRL_GUI1_LINE" => "Shared.Theme.Printing.Line",
            "CTRL_GUI1_SHAPE" => "Shared.Theme.Printing.Shape",
            "CTRL_GUI1_TABLE" => "Shared.Theme.Printing.Grid",
            "CTRL_GUI1_COLUMN" => "Shared.Theme.Printing.GridColumn",
            _ => "Shared.Theme.Printing.TextBox"
        };
    }

    private static string ResolvePrintControlVariableName(TaskFormControlDef c, int formEntryIndex)
    {
        var suffix = !string.IsNullOrWhiteSpace(c.ControlName)
            ? ToPascalIdentifier(c.ControlName)
            : !string.IsNullOrWhiteSpace(c.ColumnTitle)
                ? ToPascalIdentifier(c.ColumnTitle)
                : c.Id.ToString();
        var prefix = c.Model switch
        {
            "CTRL_GUI1_STATIC" => "lbl",
            "CTRL_GUI1_EDIT" => "txt",
            "CTRL_GUI1_LINE" => "lin",
            "CTRL_GUI1_SHAPE" => "shp",
            "CTRL_GUI1_TABLE" => "grd",
            "CTRL_GUI1_COLUMN" => "gcl",
            _ => "ctl"
        };
        return $"{prefix}S{formEntryIndex}_{suffix}";
    }

    private static string ResolvePrintSectionName(TaskFormEntryDef formEntry, int sequence)
    {
        var baseName = ToPascalIdentifier(formEntry.Form.FormName ?? "");
        if (string.IsNullOrWhiteSpace(baseName) || baseName == "Unnamed")
            baseName = "Section" + sequence;
        return baseName;
    }
}


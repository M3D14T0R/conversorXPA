using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveSelectExpression(TaskLogicSelectDef sel, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects, string primaryMember)
    {
        var cacheKey = string.Join("|",
            task.Ordinal.ToString(CultureInfo.InvariantCulture),
            sel.Name ?? "",
            sel.Type ?? "",
            sel.SourceDbObj?.ToString(CultureInfo.InvariantCulture) ?? "",
            sel.SourceLinkSequence?.ToString(CultureInfo.InvariantCulture) ?? "",
            sel.ColumnId.ToString(CultureInfo.InvariantCulture),
            primaryMember ?? "");
        if (_selectExpressionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (string.Equals(sel.Type, "R", StringComparison.OrdinalIgnoreCase) &&
            TryResolveRelationalSelectColumn(sel, task, dataObjects, out var d, out var col))
        {
            var sourceMember = ResolveSelectSourceMember(sel, task, dataObjects);
            if (string.IsNullOrWhiteSpace(sourceMember))
                sourceMember = primaryMember;
            var resolved = $"{sourceMember}.{ResolveDataObjectColumnMemberName(d, col)}";
            _selectExpressionCache[cacheKey] = resolved;
            return resolved;
        }

        if (string.Equals(sel.Type, "R", StringComparison.OrdinalIgnoreCase))
        {
            // The task XML can be newer than the mapped component manifest/DLL.
            // Keep the XPA select as an explicitly generated, typed compatibility
            // column instead of leaking its alphabetic alias into C# or confusing
            // its model ColumnId with an unrelated local resource id.
            var fallback = ResolveUnmappedRelationalSelectMemberName(sel);
            _selectExpressionCache[cacheKey] = fallback;
            return fallback;
        }

        var rc = ResolveTaskResourceColumn(task, sel.ColumnId);
        if (rc is not null)
        {
            var resolved = ResolveTaskResourceMemberName(task, rc);
            _selectExpressionCache[cacheKey] = resolved;
            return resolved;
        }

        if (!string.IsNullOrWhiteSpace(sel.Name) &&
            !task.MainProgram &&
            task.ParentOrdinal.HasValue)
        {
            var applicationSelectMap = BuildApplicationSelectMap(_allTasks ?? Array.Empty<TaskSemantic>(), dataObjects);
            if (applicationSelectMap.TryGetValue(sel.Name, out var applicationExpr) &&
                !string.IsNullOrWhiteSpace(applicationExpr))
            {
                _selectExpressionCache[cacheKey] = applicationExpr;
                return applicationExpr;
            }
        }

        if (sel.Type == "V")
        {
            var virtuals = task.SelectsSemantic.Items.Where(s => s.Type == "V").ToList();
            var pos = virtuals.FindIndex(s => string.Equals(s.Name, sel.Name, StringComparison.OrdinalIgnoreCase));
            if (pos >= 0)
            {
                var byOrder = task.ResourcesSemantic.Ordered.ToList();
                if (pos < byOrder.Count)
                {
                    var resolved = ResolveTaskResourceMemberName(task, byOrder[pos]);
                    _selectExpressionCache[cacheKey] = resolved;
                    return resolved;
                }
            }
        }

        _selectExpressionCache[cacheKey] = "";
        return "";
    }

    private static bool TryResolveRelationalSelectColumn(
        TaskLogicSelectDef select,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out DataObjectDef dataObject,
        out DataColumnDef column)
    {
        dataObject = default!;
        column = default!;
        if (!string.Equals(select.Type, "R", StringComparison.OrdinalIgnoreCase))
            return false;

        var sourceDbObj = select.SourceDbObj;
        if (!sourceDbObj.HasValue && select.SourceLinkSequence.HasValue)
        {
            var linkIndex = select.SourceLinkSequence.Value - 1;
            if (linkIndex >= 0 && linkIndex < task.Links.Count)
                sourceDbObj = task.Links[linkIndex].DbObj;
        }
        sourceDbObj ??= task.PrimaryDbObj ?? task.InformationDbObj;
        if (!sourceDbObj.HasValue)
            return false;

        var resolvedObject = ResolveDataObjectByOrdinal(dataObjects, sourceDbObj.Value);
        if (resolvedObject is null)
            return false;

        var resolvedColumn = resolvedObject.Columns.FirstOrDefault(c => c.Id == select.ColumnId);
        if (resolvedColumn is null &&
            select.ColumnId > 0 &&
            select.ColumnId <= resolvedObject.Columns.Count)
            resolvedColumn = resolvedObject.Columns[select.ColumnId - 1];
        if (resolvedColumn is null)
            return false;

        dataObject = resolvedObject;
        column = resolvedColumn;
        return true;
    }

    private static bool IsUnmappedRelationalSelect(
        TaskLogicSelectDef select,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
        => string.Equals(select.Type, "R", StringComparison.OrdinalIgnoreCase) &&
           !TryResolveRelationalSelectColumn(select, task, dataObjects, out _, out _);

    private static string ResolveUnmappedRelationalSelectMemberName(TaskLogicSelectDef select)
        => "Select_" + ToPascalIdentifier(
            string.IsNullOrWhiteSpace(select.Name)
                ? "Column" + select.ColumnId.ToString(CultureInfo.InvariantCulture)
                : select.Name);

    private static string ResolveUnmappedRelationalSelectReturnType(
        TaskLogicSelectDef select,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        var selectName = (select.Name ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(selectName))
        {
            if (select.AssignmentExpressionId.HasValue &&
                task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(
                    select.AssignmentExpressionId.Value,
                    out var assignmentExpression) &&
                assignmentExpression is not null)
            {
                var assignmentReturnType = ResolveSimpleReturnTypeForExpressionAttribute(
                    assignmentExpression.Attribute);
                if (!string.IsNullOrWhiteSpace(assignmentReturnType))
                    return NormalizeReturnTypeToken(assignmentReturnType);
            }

            var updateReturnTypes = EnumerateTaskUpdatesForArrayItemInference(task)
                .Where(update =>
                    string.Equals(
                        StripRedundantOuterParentheses(update.Variable ?? "").Trim(),
                        selectName,
                        StringComparison.OrdinalIgnoreCase))
                .Select(update => ResolveUpdateValueReturnType(update, task, dataObjects))
                .Where(returnType => !string.IsNullOrWhiteSpace(returnType))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (updateReturnTypes.Length == 1)
                return updateReturnTypes[0];

            var exactExpression = task.ExpressionsSemantic.Entries.FirstOrDefault(entry =>
                string.Equals(
                    StripRedundantOuterParentheses(entry.SourceSyntax ?? "").Trim(),
                    selectName,
                    StringComparison.OrdinalIgnoreCase));
            var exactReturnType = ResolveSimpleReturnTypeForExpressionAttribute(exactExpression?.Attribute);
            if (!string.IsNullOrWhiteSpace(exactReturnType))
                return NormalizeReturnTypeToken(exactReturnType);

            var callContractReturnType = ResolveSelectReturnTypeFromCallContracts(
                selectName,
                task);
            if (!string.IsNullOrWhiteSpace(callContractReturnType))
                return callContractReturnType;

            if (task.View.SelectedFormControls.Any(control =>
                    string.Equals(control.DataColumn, selectName, StringComparison.OrdinalIgnoreCase) &&
                    IsViewCheckBoxControl(control)))
                return "Bool";

            var escaped = Regex.Escape(selectName);
            var numericEvidence = task.ExpressionsSemantic.Entries.Any(entry =>
                Regex.IsMatch(
                    entry.SourceSyntax ?? "",
                    $@"\b(?:CastToNumber|Val|Str)\s*\(\s*{escaped}\b|\b{escaped}\b\s*(?:=|<>|<=|>=|<|>)\s*-?\d|\b{escaped}\b\s*[\+\-*/]|[\+\-*/]\s*\b{escaped}\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            if (numericEvidence)
                return "Number";

            foreach (var entry in task.ExpressionsSemantic.Entries)
            {
                foreach (Match comparison in Regex.Matches(
                             entry.SourceSyntax ?? "",
                             @"(?<![A-Za-z0-9_])(?<left>[A-Za-z]+)\s*(?:=|<>|<=|>=|<|>)\s*(?<right>[A-Za-z]+)(?![A-Za-z0-9_])",
                             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    var left = comparison.Groups["left"].Value;
                    var right = comparison.Groups["right"].Value;
                    var counterpart = string.Equals(left, selectName, StringComparison.OrdinalIgnoreCase)
                        ? right
                        : string.Equals(right, selectName, StringComparison.OrdinalIgnoreCase)
                            ? left
                            : "";
                    if (string.IsNullOrWhiteSpace(counterpart))
                        continue;

                    var counterpartType = ResolveSelectAliasReturnTypeFromTaskChain(
                        counterpart,
                        select,
                        task,
                        dataObjects);
                    if (!string.IsNullOrWhiteSpace(counterpartType))
                        return counterpartType;
                }
            }
        }

        return "Text";
    }

    private static string ResolveUpdateValueReturnType(
        TaskUpdateDef update,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (int.TryParse(update.WithValue, out var expressionOrdinal) &&
            task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expressionOrdinal, out var expression) &&
            expression is not null)
        {
            var attributeReturnType = ResolveSimpleReturnTypeForExpressionAttribute(expression.Attribute);
            if (!string.IsNullOrWhiteSpace(attributeReturnType))
                return NormalizeReturnTypeToken(attributeReturnType);
        }

        var source = ResolveUpdateValueSourceSyntax(update, task);
        return string.IsNullOrWhiteSpace(source)
            ? ""
            : NormalizeReturnTypeToken(ResolveSourceExpressionReturnType(source, task, dataObjects, 0));
    }

    private static string ResolveSelectReturnTypeFromCallContracts(
        string selectName,
        TaskSemantic task)
    {
        if (_allTasks is null || _allTasks.Count == 0)
            return "";

        var inferredTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var call in EnumerateTaskCalls(task))
        {
            if (call.ArgumentDefs.Count == 0)
                continue;

            var targetTask = ResolveTaskByCall(task, call, _allTasks);
            if (targetTask is null)
                continue;

            var parameters = GetTaskParameters(targetTask);
            if (parameters.Count == 0 ||
                call.ArgumentDefs.Count != parameters.Count)
                continue;

            for (var i = 0; i < call.ArgumentDefs.Count; i++)
            {
                var argument = call.ArgumentDefs[i];
                if (argument.Skip == true ||
                    !CallArgumentReferencesSelect(argument, selectName, task))
                    continue;

                var expected = ExpectedTypeForParameterType(parameters[i].ParameterType);
                var returnType = NormalizeReturnTypeToken(
                    GetValueReturnType(expected.ReturnType));
                if (!string.IsNullOrWhiteSpace(returnType))
                    inferredTypes.Add(returnType);
            }
        }

        return inferredTypes.Count == 1
            ? inferredTypes.First()
            : "";
    }

    private static bool CallArgumentReferencesSelect(
        TaskArgumentDef argument,
        string selectName,
        TaskSemantic task)
    {
        if (!string.IsNullOrWhiteSpace(argument.Variable) &&
            string.Equals(
                StripRedundantOuterParentheses(argument.Variable.Trim()),
                selectName,
                StringComparison.OrdinalIgnoreCase))
            return true;

        var expressionOrdinal = argument.ExpressionId ?? argument.Exp;
        if (!expressionOrdinal.HasValue ||
            !task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(
                expressionOrdinal.Value,
                out var expression) ||
            expression is null)
            return false;

        var sourceSyntax = StripRedundantOuterParentheses(
            ResolveExpressionEntrySourceSyntax(expression).Trim());
        return string.Equals(
            sourceSyntax,
            selectName,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSelectAliasReturnTypeFromTaskChain(
        string alias,
        TaskLogicSelectDef excludedSelect,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        var currentTask = task;
        while (currentTask is not null)
        {
            if (currentTask.SelectsSemantic.ItemsByName.TryGetValue(alias, out var candidate) &&
                candidate is not null &&
                !ReferenceEquals(candidate, excludedSelect) &&
                TryResolveSourceSelectReturnType(candidate, currentTask, dataObjects, out var returnType) &&
                !string.IsNullOrWhiteSpace(returnType))
                return NormalizeReturnTypeToken(returnType);

            if (!currentTask.ParentOrdinal.HasValue ||
                !_tasksByOrdinal.TryGetValue(currentTask.ParentOrdinal.Value, out var parentTask))
                break;
            currentTask = parentTask;
        }

        return "";
    }

    private static string ResolveUnmappedRelationalSelectColumnType(
        TaskLogicSelectDef select,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
        => ResolveUnmappedRelationalSelectReturnType(select, task, dataObjects) switch
        {
            "Number" => "NumberColumn",
            "Date" => "DateColumn",
            "Time" => "TimeColumn",
            "Bool" => "BoolColumn",
            _ => "TextColumn"
        };

    private static string ResolveSelectSourceMember(TaskLogicSelectDef sel, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var cacheKey = string.Join("|",
            task.Ordinal.ToString(CultureInfo.InvariantCulture),
            sel.SourceDbObj?.ToString(CultureInfo.InvariantCulture) ?? "",
            sel.SourceLinkSequence?.ToString(CultureInfo.InvariantCulture) ?? "",
            sel.ColumnId.ToString(CultureInfo.InvariantCulture));
        if (_selectSourceMemberCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var modelMembers = BuildModelMembers(task, dataObjects);
        if (sel.SourceLinkSequence.HasValue)
        {
            var primaryObj = task.PrimaryDbObj ?? task.InformationDbObj;
            var linkMembers = BuildLinkMembers(task, dataObjects, modelMembers, primaryObj);
            var idx = sel.SourceLinkSequence.Value - 1;
            if (idx >= 0 && idx < linkMembers.Count)
            {
                _selectSourceMemberCache[cacheKey] = linkMembers[idx].MemberName;
                return linkMembers[idx].MemberName;
            }
        }
        var resolved = modelMembers.FirstOrDefault(m => m.DbObj == sel.SourceDbObj).MemberName;
        _selectSourceMemberCache[cacheKey] = resolved;
        return resolved;
    }

    private static string ResolveSelectBindValueExpression(TaskLogicSelectDef sel, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
        => ResolveSelectBindValueExpression(sel, task, dataObjects, null);

    private static string ResolveSelectBindValueExpression(
        TaskLogicSelectDef sel,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        string? targetExpr)
    {
        if (!sel.AssignmentExpressionId.HasValue)
            return "";
        var cacheKey = string.Join("|",
            task.Ordinal.ToString(CultureInfo.InvariantCulture),
            sel.Name ?? "",
            sel.AssignmentExpressionId.Value.ToString(CultureInfo.InvariantCulture),
            targetExpr ?? "");
        if (_selectBindValueExpressionCache.TryGetValue(cacheKey, out var cached))
            return cached;
        string resolved;
        if (!string.IsNullOrWhiteSpace(targetExpr) &&
            task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(sel.AssignmentExpressionId.Value, out var bindExpression) &&
            bindExpression is not null)
        {
            var targetInfo = ResolveBindValueTargetInfoFromEvidence(task, sel, targetExpr);

            resolved = targetInfo.IsDotNet || string.IsNullOrWhiteSpace(targetInfo.AttrObj)
                ? ResolveFilterOperandExpression(task, targetExpr, sel.AssignmentExpressionId.Value, dataObjects)
                : ResolveTypedExpressionEntryCode(
                    bindExpression,
                    task,
                    dataObjects,
                    CreateBindValueEmissionContext(targetInfo, targetInfo.TargetMember)).Code;
        }
        else
        {
            resolved = string.IsNullOrWhiteSpace(targetExpr)
                ? ResolveExpressionCode(sel.AssignmentExpressionId.Value.ToString(), task, dataObjects)
                : ResolveFilterOperandExpression(task, targetExpr, sel.AssignmentExpressionId.Value, dataObjects);
        }

        _selectBindValueExpressionCache[cacheKey] = resolved;
        return resolved;
    }

    private static string ResolveSortSegmentExpression(
        TaskSemantic task,
        TaskSortSegmentDef seg,
        IReadOnlyList<DataObjectDef> dataObjects,
        string? primaryMember)
    {
        var selects = task.SelectsSemantic.Items.Where(s => !s.IsFunctionSelect).ToList();
        if (seg.FieldId > 0 && seg.FieldId <= selects.Count)
        {
            var sel = selects[seg.FieldId - 1];
            var expr = ResolveSelectExpression(sel, task, dataObjects, primaryMember ?? "");
            if (IsSimpleMemberAccess(expr))
                return expr;
        }
        return "";
    }
}


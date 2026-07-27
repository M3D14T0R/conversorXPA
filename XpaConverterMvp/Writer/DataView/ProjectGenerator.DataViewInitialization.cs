using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitInitializeDataView(
        StringBuilder sb,
        TaskSemantic t,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        string methodName)
    {
        var className = ResolveTaskClassName(t, allTasks);
        sb.AppendLine($"    void {methodName}()");
        sb.AppendLine("    {");
        var baseClass = ResolveBaseClass(t);
        var suppressImplicitDataView = string.Equals(baseClass, "BusinessProcessBase", StringComparison.Ordinal) &&
                                       t.ResourceDbs.Count == 0;
        var primaryObj = t.PrimaryDbObj ?? t.InformationDbObj;
        List<(int DbObj, string ModelType, string MemberName)> modelMembers = new();
        TimeSection(() => modelMembers = BuildModelMembers(t, dataObjects), "DATAVIEW", className, "build-model-members");
        if (!suppressImplicitDataView &&
            !t.DataView.HasFrom &&
            string.Equals(baseClass, "BusinessProcessBase", StringComparison.Ordinal) &&
            t.ResourceDbs.Any(db => db.Cache == true))
        {
            var relationEntityIds = t.Links
                .Select(link => link.DbObj)
                .ToHashSet();
            foreach (var db in t.ResourceDbs.Where(db => db.Cache == true && !relationEntityIds.Contains(db.DataObject)))
            {
                var mm = modelMembers.FirstOrDefault(m => m.DbObj == db.DataObject);
                if (!string.IsNullOrWhiteSpace(mm.MemberName))
                    sb.AppendLine($"        Entities.Add({mm.MemberName});");
            }
        }
        string? primaryMember = null;
        if (!suppressImplicitDataView && t.DataView.HasFrom && primaryObj > 0)
        {
            var d = ResolveDataObjectByOrdinal(dataObjects, primaryObj!.Value);
            if (d is not null)
            {
                var mm = modelMembers.FirstOrDefault(m => m.DbObj == primaryObj);
                primaryMember = string.IsNullOrWhiteSpace(mm.MemberName) ? ToEntityTypeName(d.Name) : mm.MemberName;
                sb.AppendLine($"        From = {primaryMember};");
            }
        }

        TimeSection(() => EmitTaskSqlForm(sb, t, dataObjects), "DATAVIEW", className, "emit-sql-form");
        if (!suppressImplicitDataView)
            TimeSection(() => EmitTaskSqlWhere(sb, t, dataObjects, allTasks), "DATAVIEW", className, "emit-sql-where");

        List<(TaskLogicLinkDef Link, string MemberName)> linkMembers = new();
        if (suppressImplicitDataView)
            linkMembers = new List<(TaskLogicLinkDef Link, string MemberName)>();
        else
            TimeSection(() => linkMembers = BuildLinkMembers(t, dataObjects, modelMembers, primaryObj), "DATAVIEW", className, "build-link-members");
        var linkParamCursorByDbObj = new Dictionary<int, int>();
        var writeModeBoundColumns = new HashSet<string>(StringComparer.Ordinal);
        var mutableFunctionAssignmentTargets =
            ResolveMutableFunctionAssignmentTargets(t, dataObjects, allTasks);
        var dataObjectByOrdinal = _dataObjectsByOrdinal;
        var resourceDbByObject = t.ResourceDbs
            .GroupBy(r => r.DataObject)
            .ToDictionary(g => g.Key, g => g.First());
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
        var sourceEntity = !string.IsNullOrWhiteSpace(primaryMember) && primaryObj > 0 && dataObjectByOrdinal.TryGetValue(primaryObj.Value, out var sourceEntityResolved)
            ? sourceEntityResolved
            : null;
        var relationConditionSourceColumns = new HashSet<string>(StringComparer.Ordinal);
        var relationDependencyEntityMembers = modelMembers
            .Select(member => member.MemberName)
            .Where(member => !string.IsNullOrWhiteSpace(member))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        TimeSection(() =>
        {
            var relationsTotalSw = Stopwatch.StartNew();
            long bindEnabledMs = 0;
            long orderByMs = 0;
            long directConditionMs = 0;
            long assignmentConditionMs = 0;
            long filterConditionMs = 0;
            long overrideConditionMs = 0;
            long emitRelationMs = 0;
            long writeModeBoundMs = 0;
            long notifyMs = 0;
            long bindRelationEnabledMs = 0;
            var relationCount = 0;
            for (var i = 0; i < linkMembers.Count; i++)
            {
                var lb = linkMembers[i];
                if (!dataObjectByOrdinal.TryGetValue(lb.Link.DbObj, out var target))
                    continue;
                relationCount++;

                string? bindEnabledExpr = null;
                var sw = Stopwatch.StartNew();
                if (lb.Link.ConditionExpressionId.HasValue)
                {
                    bindEnabledExpr = ResolveExpressionCode(lb.Link.ConditionExpressionId.Value.ToString(), t, dataObjects, CreateBooleanConditionEmissionContext());
                }
                sw.Stop();
                bindEnabledMs += sw.ElapsedMilliseconds;

                sw.Restart();
                var orderByExpr = ResolveOrderByExpression(target, lb.Link);
                sw.Stop();
                orderByMs += sw.ElapsedMilliseconds;
                var condExpr = "";
                sw.Restart();
                if (!string.IsNullOrWhiteSpace(primaryMember) && sourceEntity is not null)
                    condExpr = ResolveLinkConditionExpression(t, sourceEntity, primaryMember!, target, lb.MemberName, lb.Link);
                sw.Stop();
                directConditionMs += sw.ElapsedMilliseconds;
                resourceDbByObject.TryGetValue(lb.Link.DbObj, out var resourceDb);
                var writeModeNoPrimary = string.IsNullOrWhiteSpace(primaryMember)
                                         && string.Equals(lb.Link.Mode, "A", StringComparison.OrdinalIgnoreCase)
                                         && string.Equals(resourceDb?.Access, "W", StringComparison.OrdinalIgnoreCase);
                if (!writeModeNoPrimary && string.IsNullOrWhiteSpace(condExpr))
                {
                    sw.Restart();
                    condExpr = ResolveLinkConditionFromSelectAssignments(t, dataObjects, lb, target, i + 1, relationAssignmentSelectByDbObjAndColumn);
                    sw.Stop();
                    assignmentConditionMs += sw.ElapsedMilliseconds;
                }
                if (!writeModeNoPrimary && string.IsNullOrWhiteSpace(condExpr))
                {
                    sw.Restart();
                    condExpr = ResolveLinkConditionFromSelectFilters(t, dataObjects, lb, target, i + 1, relationFilterSelectsByDbObj);
                    sw.Stop();
                    filterConditionMs += sw.ElapsedMilliseconds;
                }
                sw.Restart();
                condExpr = OverrideLinkConditionByReturnHint(t, dataObjects, linkMembers, i, lb, target, condExpr);
                sw.Stop();
                overrideConditionMs += sw.ElapsedMilliseconds;
                if (!string.IsNullOrWhiteSpace(condExpr))
                {
                    foreach (var sourceMember in relationDependencyEntityMembers)
                    {
                        if (string.Equals(sourceMember, lb.MemberName, StringComparison.Ordinal))
                            continue;

                        foreach (Match match in Regex.Matches(
                                     condExpr,
                                     $@"\b{Regex.Escape(sourceMember)}\.(\w+)",
                                     RegexOptions.CultureInvariant))
                        {
                            if (match.Groups.Count > 1)
                                relationConditionSourceColumns.Add($"{sourceMember}.{match.Groups[1].Value}");
                        }
                    }
                }
                var relationTypePrefix = "";
                if (string.Equals(lb.Link.Mode, "W", StringComparison.OrdinalIgnoreCase))
                    relationTypePrefix = "RelationType.InsertIfNotFound, ";
                else if (writeModeNoPrimary)
                    relationTypePrefix = "RelationType.Insert, ";
                else if (string.Equals(lb.Link.Mode, "J", StringComparison.OrdinalIgnoreCase))
                    relationTypePrefix = "RelationType.Join, ";
                else if (string.Equals(lb.Link.Mode, "O", StringComparison.OrdinalIgnoreCase))
                    relationTypePrefix = "RelationType.OuterJoin, ";
                sw.Restart();
                if (!string.IsNullOrWhiteSpace(orderByExpr))
                {
                    if (!string.IsNullOrWhiteSpace(condExpr))
                        sb.AppendLine($"        Relations.Add({lb.MemberName}, {relationTypePrefix}{condExpr}, {lb.MemberName}.{orderByExpr});");
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(relationTypePrefix))
                            sb.AppendLine($"        Relations.Add({lb.MemberName}, {relationTypePrefix}{lb.MemberName}.{orderByExpr});");
                        else
                            sb.AppendLine($"        Relations.Add({lb.MemberName}, {lb.MemberName}.{orderByExpr});");
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(condExpr))
                        sb.AppendLine($"        Relations.Add({lb.MemberName}, {relationTypePrefix}{condExpr});");
                    else if (!string.IsNullOrWhiteSpace(relationTypePrefix))
                        sb.AppendLine($"        Relations.Add({lb.MemberName}, {relationTypePrefix.TrimEnd(' ', ',')});");
                }
                sw.Stop();
                emitRelationMs += sw.ElapsedMilliseconds;
                if (string.Equals(lb.Link.Mode, "W", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(condExpr))
                {
                    sw.Restart();
                    foreach (Match m in Regex.Matches(condExpr, $@"\b{Regex.Escape(lb.MemberName)}\.(\w+)\.(?:BindEqualTo|IsEqualTo)\s*\(", RegexOptions.IgnoreCase))
                    {
                        if (m.Groups.Count > 1)
                            writeModeBoundColumns.Add($"{lb.MemberName}.{m.Groups[1].Value}");
                    }
                    sw.Stop();
                    writeModeBoundMs += sw.ElapsedMilliseconds;
                }

                sw.Restart();
                if (lb.Link.Direction == "D")
                    sb.AppendLine($"        Relations[{lb.MemberName}].OrderBy.Reversed = true;");
                if (!string.IsNullOrWhiteSpace(lb.Link.ReturnValueName))
                {
                    var notifyExpr = ResolveSelectExpressionByName(lb.Link.ReturnValueName!, t, dataObjects);
                    if (!string.IsNullOrWhiteSpace(notifyExpr))
                        sb.AppendLine($"        Relations[{lb.MemberName}].NotifyRowWasFoundTo({notifyExpr});");
                }
                sw.Stop();
                notifyMs += sw.ElapsedMilliseconds;
                sw.Restart();
                if (!string.IsNullOrWhiteSpace(bindEnabledExpr) && !string.Equals(lb.Link.EvaluateConditionMode, "T", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine($"        Relations[{lb.MemberName}].BindEnabled(() => {bindEnabledExpr});");
                sw.Stop();
                bindRelationEnabledMs += sw.ElapsedMilliseconds;
            }
            relationsTotalSw.Stop();
            ConversionTelemetry.Log("RELATIONS",
                $"{className} totalMs={relationsTotalSw.ElapsedMilliseconds} count={relationCount} " +
                $"bind-enabled-ms={bindEnabledMs} orderby-ms={orderByMs} direct-ms={directConditionMs} " +
                $"assign-ms={assignmentConditionMs} filters-ms={filterConditionMs} override-ms={overrideConditionMs} " +
                $"emit-ms={emitRelationMs} writebound-ms={writeModeBoundMs} " +
                $"notify-ms={notifyMs} bindrelation-ms={bindRelationEnabledMs}");
        }, "DATAVIEW", className, "emit-relations");

        if (!suppressImplicitDataView && !string.IsNullOrWhiteSpace(primaryMember))
        {
            var primaryEntity = ResolveDataObjectByOrdinal(dataObjects, primaryObj!.Value);
            if (primaryEntity is not null)
            {
                TimeSection(() =>
                {
                    if (t.InitialKeyExpressionId.HasValue)
                    {
                        var keyExpr = ResolveInitialKeyExpressionCode(t, primaryEntity, primaryMember, dataObjects);
                        if (!string.IsNullOrWhiteSpace(keyExpr))
                        {
                            if (baseClass == "BusinessProcessBase")
                                sb.AppendLine($"        OrderBy = {primaryMember}.Indexes[{keyExpr}];");
                            else
                                sb.AppendLine($"        BindOrderBy(() => {primaryMember}.Indexes[{keyExpr}]);");
                        }
                        else
                        {
                            var orderByExpr = ResolveOrderByExpression(primaryEntity, new TaskLogicLinkDef(primaryObj ?? 0, "A", t.InitialKeyIndexId, null, null, null, null, null, null, null, null, false, null));
                            if (!string.IsNullOrWhiteSpace(orderByExpr))
                            {
                                sb.AppendLine($"        OrderBy = {primaryMember}.{orderByExpr};");
                                if (ShouldReversePrimaryOrderBy(t))
                                    sb.AppendLine("        OrderBy.Reversed = true;");
                            }
                        }
                    }
                    else if (t.SortSegments.Count > 0)
                    {
                        var emittedAny = false;
                        var emittedSortExprs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var seg in t.SortSegments)
                        {
                            var sortExpr = ResolveSortSegmentExpression(t, seg, dataObjects, primaryMember);
                            if (string.IsNullOrWhiteSpace(sortExpr))
                            {
                                var col = primaryEntity.Columns.FirstOrDefault(c => c.Id == seg.FieldId);
                                if (col is null)
                                    continue;
                                sortExpr = $"{primaryMember}.{ResolveDataObjectColumnMemberName(primaryEntity, col)}";
                            }
                            if (emittedSortExprs.Contains(sortExpr))
                                continue;
                            if (string.Equals(seg.Direction, "D", StringComparison.OrdinalIgnoreCase))
                                sb.AppendLine($"        OrderBy.Add({sortExpr}, SortDirection.Descending);");
                            else
                                sb.AppendLine($"        OrderBy.Add({sortExpr});");
                            emittedSortExprs.Add(sortExpr);
                            emittedAny = true;
                        }
                        if (t.InitialKeyIndexId.HasValue)
                        {
                            var keyIdx = primaryEntity.Indexes.FirstOrDefault(i => i.Id == t.InitialKeyIndexId.Value);
                            if (keyIdx is not null)
                            {
                                foreach (var seg in keyIdx.Segments)
                                {
                                    var col = primaryEntity.Columns.FirstOrDefault(c => c.Id == seg.ColumnId);
                                    if (col is null)
                                        continue;
                                    var tieExpr = $"{primaryMember}.{ResolveDataObjectColumnMemberName(primaryEntity, col)}";
                                    if (emittedSortExprs.Contains(tieExpr))
                                        continue;
                                    if (string.Equals(seg.Order, "D", StringComparison.OrdinalIgnoreCase))
                                        sb.AppendLine($"        OrderBy.Add({tieExpr}, SortDirection.Descending);");
                                    else
                                        sb.AppendLine($"        OrderBy.Add({tieExpr});");
                                    emittedSortExprs.Add(tieExpr);
                                    emittedAny = true;
                                }
                            }
                        }
                        if (!emittedAny)
                        {
                            var orderByExpr = ResolveOrderByExpression(primaryEntity, new TaskLogicLinkDef(primaryObj ?? 0, "A", t.InitialKeyIndexId, null, null, null, null, null, null, null, null, false, null));
                            if (!string.IsNullOrWhiteSpace(orderByExpr))
                            {
                                sb.AppendLine($"        OrderBy = {primaryMember}.{orderByExpr};");
                                if (ShouldReversePrimaryOrderBy(t))
                                    sb.AppendLine("        OrderBy.Reversed = true;");
                            }
                        }
                    }
                    else
                    {
                        var orderByExpr = ResolveOrderByExpression(primaryEntity, new TaskLogicLinkDef(primaryObj ?? 0, "A", t.InitialKeyIndexId, null, null, null, null, null, null, null, null, false, null));
                        if (!string.IsNullOrWhiteSpace(orderByExpr))
                        {
                            sb.AppendLine($"        OrderBy = {primaryMember}.{orderByExpr};");
                            if (ShouldReversePrimaryOrderBy(t))
                                sb.AppendLine("        OrderBy.Reversed = true;");
                        }
                    }
                }, "DATAVIEW", className, "emit-primary-orderby");
            }
        }

        if (!suppressImplicitDataView)
        {
            TimeSection(() => EmitSubtaskWhereByRangeAssignments(sb, t, dataObjects, allTasks), "DATAVIEW", className, "emit-subtask-range-assignments");
            TimeSection(() => EmitTaskWhereByRanges(sb, t, dataObjects, allTasks, primaryObj, primaryMember), "DATAVIEW", className, "emit-task-where-ranges");
            TimeSection(() => EmitTaskRangeExpressions(sb, t, dataObjects), "DATAVIEW", className, "emit-task-range-expressions");
            TimeSection(() => EmitExpandBeforeFlowCalls(sb, t, dataObjects, allTasks), "DATAVIEW", className, "emit-expand-before-flow");
        }

        var allowedParameterSelectNames = GetAllowedParameterSelectNames(t);
        var emittedDataViewColumns = new HashSet<string>(StringComparer.Ordinal);
        TimeSection(() =>
        {
            var totalSw = Stopwatch.StartNew();
            long resolveResourceMs = 0;
            long resolveExprMs = 0;
            long bindExprMs = 0;
            long adjustBindMs = 0;
            long emitMs = 0;
            var selectCount = 0;
            foreach (var sel in t.SelectsSemantic.Items)
            {
                if (string.Equals(sel.OriginLevel, "H", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(sel.OriginType, "U", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (sel.IsParameter && sel.Type == "V" && allowedParameterSelectNames.Count > 0 && !allowedParameterSelectNames.Contains(sel.Name))
                    continue;

                selectCount++;
                var sw = Stopwatch.StartNew();
                var rcForSelect = ResolveTaskResourceColumn(t, sel.ColumnId);
                sw.Stop();
                resolveResourceMs += sw.ElapsedMilliseconds;
                if (suppressImplicitDataView && rcForSelect is null)
                    continue;
                sw.Restart();
                var refExpr = ResolveSelectExpression(sel, t, dataObjects, primaryMember ?? "");
                sw.Stop();
                resolveExprMs += sw.ElapsedMilliseconds;
                if (!string.IsNullOrWhiteSpace(refExpr))
                {
                    var dotNetResourceMember = rcForSelect is not null && IsDotNetTaskResource(rcForSelect)
                        ? ResolveTaskResourceMemberName(t, rcForSelect)
                        : "";
                    var isDotNetResourceSelect =
                        string.Equals(sel.Type, "V", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(dotNetResourceMember) &&
                        string.Equals(refExpr, dotNetResourceMember, StringComparison.Ordinal);
                    if (rcForSelect?.ClearsInheritedExpandEvent == true)
                    {
                        var resourceMemberName = ResolveTaskResourceMemberName(t, rcForSelect);
                        if (!string.IsNullOrWhiteSpace(resourceMemberName))
                        {
                            sb.AppendLine($"        {resourceMemberName}.ClearExpandEvent();");
                            sb.AppendLine($"        {resourceMemberName}.AfterExpandGoToNextControl = false;");
                        }
                    }
                    sw.Restart();
                    var bindExpr = ResolveSelectBindValueExpression(sel, t, dataObjects, refExpr);
                    sw.Stop();
                    bindExprMs += sw.ElapsedMilliseconds;
                    sw.Restart();
                    bindExpr = AdjustBindValueExpressionForTarget(sel, refExpr, bindExpr, t, dataObjects);
                    sw.Stop();
                    adjustBindMs += sw.ElapsedMilliseconds;
                    var suppressBind =
                        writeModeBoundColumns.Contains(refExpr) ||
                        (sel.IsFunctionSelect &&
                         sel.AssignmentExpressionId.HasValue &&
                         mutableFunctionAssignmentTargets.Contains(refExpr));
                    var addCollection = sel.IsFunctionSelect ? "AdditionalColumns" : "Columns";
                    var allowBindForRangedVirtual = sel.HasRange &&
                                                    string.Equals(sel.Type, "V", StringComparison.OrdinalIgnoreCase) &&
                                                    rcForSelect is not null;
                    sw.Restart();
                    var isAdditionalColumn = sel.IsFunctionSelect;
                    if (isDotNetResourceSelect)
                    {
                        if (!string.IsNullOrWhiteSpace(bindExpr))
                            sb.AppendLine($"        {refExpr} = {bindExpr};");
                    }
                    else if (!string.IsNullOrWhiteSpace(sel.RealVarName))
                    {
                        emittedDataViewColumns.Add(refExpr);
                        if (isAdditionalColumn)
                        {
                            sb.AppendLine($"        {addCollection}.Add({refExpr});");
                            if (!suppressBind && !string.IsNullOrWhiteSpace(bindExpr) && (!sel.HasRange || allowBindForRangedVirtual))
                                sb.AppendLine($"        {refExpr}{BuildBindValueSuffix(bindExpr, t)};");
                            sb.AppendLine($"        {refExpr}.Caption = \"{Escape(sel.RealVarName)}\";");
                        }
                        else
                        {
                            var local = $"col{ToPascalIdentifier(sel.Name)}";
                            if (!suppressBind && !string.IsNullOrWhiteSpace(bindExpr) && (!sel.HasRange || allowBindForRangedVirtual))
                                sb.AppendLine($"        var {local} = {addCollection}.Add({refExpr}){BuildBindValueSuffix(bindExpr, t)};");
                            else
                                sb.AppendLine($"        var {local} = {addCollection}.Add({refExpr});");
                            sb.AppendLine($"        {local}.Caption = \"{Escape(sel.RealVarName)}\";");
                        }
                    }
                    else if (!suppressBind && !string.IsNullOrWhiteSpace(bindExpr) && (!sel.HasRange || allowBindForRangedVirtual))
                    {
                        emittedDataViewColumns.Add(refExpr);
                        if (isAdditionalColumn)
                        {
                            sb.AppendLine($"        {addCollection}.Add({refExpr});");
                            sb.AppendLine($"        {refExpr}{BuildBindValueSuffix(bindExpr, t)};");
                        }
                        else
                        {
                            sb.AppendLine($"        {addCollection}.Add({refExpr}){BuildBindValueSuffix(bindExpr, t)};");
                        }
                    }
                    else
                    {
                        emittedDataViewColumns.Add(refExpr);
                        sb.AppendLine($"        {addCollection}.Add({refExpr});");
                    }
                    sw.Stop();
                    emitMs += sw.ElapsedMilliseconds;
                }
            }
            totalSw.Stop();
            ConversionTelemetry.Log("SELECTS",
                $"{className} totalMs={totalSw.ElapsedMilliseconds} count={selectCount} " +
                $"resolve-resource-ms={resolveResourceMs} resolve-expr-ms={resolveExprMs} " +
                $"bind-expr-ms={bindExprMs} adjust-bind-ms={adjustBindMs} emit-ms={emitMs}");
        }, "DATAVIEW", className, "emit-selects");
        foreach (var relationSourceColumn in relationConditionSourceColumns
                     .Where(column => !emittedDataViewColumns.Contains(column))
                     .OrderBy(column => column, StringComparer.Ordinal))
        {
            sb.AppendLine($"        Columns.Add({relationSourceColumn});");
        }

        if (ResolveBaseClass(t) != "BusinessProcessBase")
        {
            TimeSection(() =>
            {
                foreach (var v in t.FlowValidations)
                {
                    var cond = ResolveExpressionCode(v.ConditionExpressionId?.ToString(), t, dataObjects, CreateBooleanConditionEmissionContext());
                    var msg = ResolveExpressionCode(v.MessageExpressionId?.ToString(), t, dataObjects, CreateMessageTextEmissionContext());
                    if (string.IsNullOrWhiteSpace(cond) || string.IsNullOrWhiteSpace(msg))
                        continue;
                    sb.AppendLine($"        Flow.Add(() => ENV.Message.ShowError({msg}), () => {cond});");
                }
            }, "DATAVIEW", className, "emit-flow-validations");
        }

        if (ResolveBaseClass(t) != "BusinessProcessBase")
            TimeSection(() => EmitTabFlowCalls(sb, t, dataObjects, allTasks), "DATAVIEW", className, "emit-tabflow-calls");
        var dotNetResourceMembers = t.ResourcesSemantic.Ordered
            .Where(IsDotNetTaskResource)
            .Select(resource => ResolveTaskResourceMemberName(t, resource))
            .ToHashSet(StringComparer.Ordinal);
        var paramMembers = GetTaskParameters(t)
            .Select(p => p.ColumnMember)
            .Where(member => !dotNetResourceMembers.Contains(member))
            .Distinct()
            .ToArray();
        if (paramMembers.Length > 0)
            TimeSection(() => sb.AppendLine($"        MarkParameterColumns({string.Join(", ", paramMembers)});"), "DATAVIEW", className, "emit-mark-parameter-columns");
        sb.AppendLine("    }");
    }
}


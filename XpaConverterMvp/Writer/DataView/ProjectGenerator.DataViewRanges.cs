using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static DataObjectDef? ResolveDataObjectByOrdinal(IReadOnlyList<DataObjectDef> dataObjects, int ordinal)
    {
        if (_dataObjectsByOrdinal.TryGetValue(ordinal, out var dataObject))
            return dataObject;
        return dataObjects.FirstOrDefault(x => x.Ordinal == ordinal);
    }

    private static Dictionary<int, DataColumnDef> BuildColumnsByIdLookup(DataObjectDef dataObject)
    {
        var result = new Dictionary<int, DataColumnDef>();
        foreach (var column in dataObject.Columns)
        {
            if (!result.ContainsKey(column.Id))
                result[column.Id] = column;
        }
        return result;
    }

    private static void EmitSubtaskWhereByRangeAssignments(
        StringBuilder sb,
        TaskSemantic t,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        if (!t.ParentOrdinal.HasValue)
            return;
        var primaryObj = t.PrimaryDbObj ?? t.InformationDbObj;
        if (!primaryObj.HasValue || primaryObj.Value <= 0)
            return;
        var fromEntity = ResolveDataObjectByOrdinal(dataObjects, primaryObj.Value);
        if (fromEntity is null)
            return;
        var fromMember = ToEntityTypeName(fromEntity.Name);
        var fromColumnsById = BuildColumnsByIdLookup(fromEntity);

        foreach (var s in t.SelectsSemantic.Items.Where(x =>
                     x.HasRange &&
                     x.SourceLinkSequence.HasValue &&
                     (x.SourceDbObj == primaryObj || !x.SourceDbObj.HasValue)))
        {
            fromColumnsById.TryGetValue(s.ColumnId, out var col);
            if (col is null)
                continue;
            var fromCol = $"{fromMember}.{ResolveDataObjectColumnMemberName(fromEntity, col)}";
            if (TryBuildRequiredEmbeddedSubformKeyRange(
                    s,
                    fromCol,
                    t,
                    dataObjects,
                    allTasks,
                    out var requiredSubformRange))
            {
                sb.AppendLine($"        Where.Add({requiredSubformRange});");
                continue;
            }
            var rangeWhereExpr = ResolveSelectRangeWhereExpression(s, fromCol, t, dataObjects);
            if (!string.IsNullOrWhiteSpace(rangeWhereExpr))
            {
                sb.AppendLine($"        Where.Add({rangeWhereExpr});");
                continue;
            }
            if (s.RangeMin.HasValue &&
                s.RangeMax.HasValue &&
                s.RangeMin.Value == s.RangeMax.Value)
            {
                var directRangeExpr = ResolveFilterOperandExpression(t, fromCol, s.RangeMin.Value, dataObjects);
                if (IsBooleanLiteralExpression(directRangeExpr))
                {
                    sb.AppendLine($"        Where.Add({BuildFilterBindEqualTo(t, fromCol, directRangeExpr)});");
                    continue;
                }
            }
            var parentExpr = ResolveParentBindingExpression(col, t, dataObjects, allTasks);
            if (!string.IsNullOrWhiteSpace(parentExpr))
                sb.AppendLine($"        Where.Add({BuildFilterBindEqualTo(t, fromCol, parentExpr)});");
        }
    }

    private static void EmitTaskWhereByRanges(
        StringBuilder sb,
        TaskSemantic t,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        int? primaryObj,
        string? primaryMember)
    {
        if (!primaryObj.HasValue || primaryObj.Value <= 0 || string.IsNullOrWhiteSpace(primaryMember))
            return;
        var fromEntity = ResolveDataObjectByOrdinal(dataObjects, primaryObj.Value);
        if (fromEntity is null)
            return;
        var fromColumnsById = BuildColumnsByIdLookup(fromEntity);

        var emittedRangeWhere = false;
        foreach (var s in t.SelectsSemantic.Items.Where(x =>
                     x.HasRange &&
                     !x.AssignmentExpressionId.HasValue &&
                     (x.SourceDbObj == primaryObj || !x.SourceDbObj.HasValue)))
        {
            var usedParentFallback = false;
            fromColumnsById.TryGetValue(s.ColumnId, out var col);
            if (col is null)
                continue;
            var fromCol = $"{primaryMember}.{ResolveDataObjectColumnMemberName(fromEntity, col)}";
            var isDirectLiteralRange =
                s.RangeMin.HasValue &&
                s.RangeMax.HasValue &&
                s.RangeMin.Value == s.RangeMax.Value &&
                TryResolveExpressionAsStringLiteralCode(t, s.RangeMin.Value, dataObjects, out _);
            var cndRangeExpr = isDirectLiteralRange ? "" : ResolveCndRangeExpressionForSelect(s, fromCol, t, dataObjects);
            if (!string.IsNullOrWhiteSpace(cndRangeExpr))
            {
                if (TryBuildRequiredEmbeddedSubformKeyRange(
                        s,
                        fromCol,
                        t,
                        dataObjects,
                        allTasks,
                        out var sourceCndRequiredSubformRange))
                    sb.AppendLine($"        Where.Add({sourceCndRequiredSubformRange});");
                else
                    sb.AppendLine($"        Where.Add({cndRangeExpr});");
                emittedRangeWhere = true;
                continue;
            }
            var fromExpr = "";
            var usesLiteralEquality = false;
            TaskLogicSelectDef? rangeParam = null;
            if (s.RangeMin.HasValue && !s.RangeMax.HasValue)
            {
                var minExpr = ResolveFilterOperandExpression(t, fromCol, s.RangeMin.Value, dataObjects);
                if (!string.IsNullOrWhiteSpace(minExpr))
                {
                    sb.AppendLine($"        Where.Add({BuildFilterIsGreaterOrEqualTo(t, fromCol, minExpr)});");
                    emittedRangeWhere = true;
                    continue;
                }
            }
            if (!s.RangeMin.HasValue && s.RangeMax.HasValue)
            {
                var maxExpr = ResolveFilterOperandExpression(t, fromCol, s.RangeMax.Value, dataObjects);
                if (!string.IsNullOrWhiteSpace(maxExpr))
                {
                    if (TrySplitCndRangeExpression(maxExpr, out var condExpr, out var valueExpr))
                        sb.AppendLine($"        Where.Add(CndRange(() => {condExpr}, {BuildFilterIsLessOrEqualTo(t, fromCol, valueExpr)}));");
                    else
                        sb.AppendLine($"        Where.Add({BuildFilterIsLessOrEqualTo(t, fromCol, maxExpr)});");
                    emittedRangeWhere = true;
                    continue;
                }
            }
            if (s.RangeMin.HasValue && s.RangeMax.HasValue && s.RangeMin.Value != s.RangeMax.Value)
            {
                var minExpr = ResolveFilterOperandExpression(t, fromCol, s.RangeMin.Value, dataObjects);
                var maxExpr = ResolveFilterOperandExpression(t, fromCol, s.RangeMax.Value, dataObjects);
                if (!string.IsNullOrWhiteSpace(minExpr) && !string.IsNullOrWhiteSpace(maxExpr))
                {
                    if (TrySplitCndRangeExpression(maxExpr, out var condExpr, out var valueExpr))
                        sb.AppendLine($"        Where.Add(CndRangeBetween({fromCol}, () => true, {EmitComparisonRightExpression(t, fromCol, minExpr)}, () => {condExpr}, {EmitComparisonRightExpression(t, fromCol, valueExpr)}));");
                    else
                        sb.AppendLine($"        Where.Add({BuildFilterIsGreaterOrEqualTo(t, fromCol, minExpr)}.And({BuildFilterIsLessOrEqualTo(t, fromCol, maxExpr)}));");
                    emittedRangeWhere = true;
                    continue;
                }
            }
            if (s.RangeMin.HasValue && s.RangeMax.HasValue && s.RangeMin.Value == s.RangeMax.Value)
            {
                var directRangeExpr = TryResolveExpressionAsStringLiteralCode(t, s.RangeMin.Value, dataObjects, out var literalRangeExpr)
                    ? literalRangeExpr
                    : ResolveFilterOperandExpression(t, fromCol, s.RangeMin.Value, dataObjects);
                if (IsBooleanLiteralExpression(directRangeExpr))
                {
                    sb.AppendLine($"        Where.Add({BuildFilterBindEqualTo(t, fromCol, directRangeExpr)});");
                    emittedRangeWhere = true;
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(directRangeExpr) &&
                    !TrySplitCndRangeExpression(directRangeExpr, out _, out _) &&
                    !IsBooleanLiteralExpression(directRangeExpr))
                {
                    fromExpr = directRangeExpr;
                    usesLiteralEquality = true;
                }
            }
            if (string.IsNullOrWhiteSpace(fromExpr) && s.RangeMin.HasValue)
            {
                var parameterByRange = GetEffectiveRangeParameterSelects(t)
                    .ElementAtOrDefault(Math.Max(0, s.RangeMin.Value - 1));
                if (parameterByRange is not null)
                    rangeParam = parameterByRange;
            }
            if (rangeParam is not null)
                fromExpr = ResolveSelectExpression(rangeParam, t, dataObjects, primaryMember);
            if (string.IsNullOrWhiteSpace(fromExpr))
                fromExpr = ResolveSelectBindValueExpression(s, t, dataObjects, fromCol);
            if (string.IsNullOrWhiteSpace(fromExpr))
                fromExpr = ResolveSelectExpression(s, t, dataObjects, primaryMember);
            if (string.IsNullOrWhiteSpace(fromExpr) && t.ParentOrdinal.HasValue)
            {
                var parentExpr = ResolveParentBindingExpression(col, t, dataObjects, _allTasks ?? Array.Empty<TaskSemantic>());
                if (!string.IsNullOrWhiteSpace(parentExpr))
                {
                    sb.AppendLine($"        Where.Add({BuildFilterIsEqualTo(t, fromCol, parentExpr)});");
                    emittedRangeWhere = true;
                    continue;
                }
            }
            if (!string.IsNullOrWhiteSpace(fromExpr) &&
                string.Equals(NormalizeKey(fromExpr), NormalizeKey(fromCol), StringComparison.Ordinal) &&
                t.ParentOrdinal.HasValue)
            {
                var parentExpr = ResolveParentBindingExpression(col, t, dataObjects, _allTasks ?? Array.Empty<TaskSemantic>());
                if (!string.IsNullOrWhiteSpace(parentExpr) &&
                    !string.Equals(NormalizeKey(parentExpr), NormalizeKey(fromCol), StringComparison.Ordinal))
                {
                    fromExpr = parentExpr;
                    usedParentFallback = true;
                }
            }
            if (string.IsNullOrWhiteSpace(fromExpr))
                continue;
            if (string.Equals(NormalizeKey(fromExpr), NormalizeKey(fromCol), StringComparison.Ordinal))
                continue;
            var usesParentBinding = usedParentFallback || fromExpr.Contains("_parent.", StringComparison.Ordinal);
            if (usesParentBinding)
            {
                var useDirectEqualityForFetch =
                    (ResolveBaseClass(t) == "BusinessProcessBase" &&
                     !t.View.ShouldGenerate &&
                     string.Equals(t.Execution.Activity, "Activities.Browse", StringComparison.Ordinal)) ||
                    (ResolveBaseClass(t) == "UIControllerBase" &&
                     string.Equals(t.Execution.RowLocking, "LockingStrategy.OnUserEdit", StringComparison.Ordinal) &&
                     string.Equals(t.Execution.TransactionScope, "TransactionScopes.RowLocking", StringComparison.Ordinal));
                if (useDirectEqualityForFetch)
                    sb.AppendLine($"        Where.Add({BuildFilterIsEqualTo(t, fromCol, fromExpr)});");
                else
                    sb.AppendLine($"        Where.Add({BuildFilterBindEqualTo(t, fromCol, fromExpr)});");
                emittedRangeWhere = true;
                continue;
            }
            // A literal equality is a field range, not a parameter range.  Besides
            // filtering existing rows, XPA uses that value to initialize a new row.
            // BindEqualTo preserves both behaviours; treating the literal as a
            // parameter emits IsEqualTo only and can immediately reject an insert
            // row whose field has not been initialized yet.
            var isParameterRange =
                !usesLiteralEquality &&
                (s.IsParameter || rangeParam is not null);
            var whereExpr = isParameterRange
                ? BuildFilterIsEqualTo(t, fromCol, fromExpr)
                : BuildFilterBindEqualTo(t, fromCol, fromExpr);
            if (isParameterRange)
            {
                var rangeCond = usesLiteralEquality
                    ? ""
                    : ResolveParameterRangeConditionExpression(rangeParam ?? s, fromExpr, t);
                if (!string.IsNullOrWhiteSpace(rangeCond))
                    rangeCond = EmitExpressionForContext(rangeCond, t, CreateBooleanConditionEmissionContext());
                if (ShouldSuppressRangeCondition(rangeCond, fromExpr))
                    rangeCond = "";
                if (string.IsNullOrWhiteSpace(rangeCond))
                    sb.AppendLine($"        Where.Add({whereExpr});");
                else
                    sb.AppendLine($"        Where.Add(CndRange(() => {rangeCond}, {whereExpr}));");
            }
            else
                sb.AppendLine($"        Where.Add({whereExpr});");
            emittedRangeWhere = true;
        }

        foreach (var s in t.SelectsSemantic.Items.Where(x =>
                     x.HasRange &&
                     x.AssignmentExpressionId.HasValue &&
                     (x.SourceDbObj == primaryObj || !x.SourceDbObj.HasValue)))
        {
            var virtualResource = string.Equals(s.Type, "V", StringComparison.OrdinalIgnoreCase)
                                  ? ResolveTaskResourceColumn(t, s.ColumnId)
                                  : null;
            if (virtualResource is not null)
            {
                var targetExpr = ResolveTaskResourceMemberName(t, virtualResource);
                var virtualRangeWhereExpr = ResolveSelectRangeWhereExpression(
                    s,
                    targetExpr,
                    t,
                    dataObjects);
                if (!string.IsNullOrWhiteSpace(virtualRangeWhereExpr))
                    sb.AppendLine($"        NonDbWhere.Add({virtualRangeWhereExpr});");
                continue;
            }

            fromColumnsById.TryGetValue(s.ColumnId, out var col);
            if (col is null)
            {
                sb.AppendLine($"        // GAP: Range with assignment not mapped to Where (Select={s.Name}, ColumnId={s.ColumnId}, ASS={s.AssignmentExpressionId?.ToString() ?? "?"}). XML={s.XmlTrace ?? "?"}");
                continue;
            }

            var fromCol = $"{primaryMember}.{ResolveDataObjectColumnMemberName(fromEntity, col)}";
            var cndRangeExpr = ResolveCndRangeExpressionForSelect(
                s,
                fromCol,
                t,
                dataObjects);
            if (!string.IsNullOrWhiteSpace(cndRangeExpr))
            {
                if (TryBuildRequiredEmbeddedSubformKeyRange(
                        s,
                        fromCol,
                        t,
                        dataObjects,
                        allTasks,
                        out var sourceCndRequiredSubformRange))
                    sb.AppendLine($"        Where.Add({sourceCndRequiredSubformRange});");
                else
                    sb.AppendLine($"        Where.Add({cndRangeExpr});");
                continue;
            }

            if (!s.RangeMin.HasValue ||
                !s.RangeMax.HasValue ||
                s.RangeMin.Value != s.RangeMax.Value)
            {
                var rangeWhereExpr = ResolveSelectRangeWhereExpression(
                    s,
                    fromCol,
                    t,
                    dataObjects);
                if (!string.IsNullOrWhiteSpace(rangeWhereExpr))
                    sb.AppendLine($"        Where.Add({rangeWhereExpr});");
                continue;
            }

            var valueExpr = ResolveFilterOperandExpression(
                t,
                fromCol,
                s.RangeMin.Value,
                dataObjects);
            if (string.IsNullOrWhiteSpace(valueExpr))
            {
                sb.AppendLine($"        // GAP: Range with assignment not mapped to Where (Select={s.Name}, ColumnId={s.ColumnId}, ASS={s.AssignmentExpressionId?.ToString() ?? "?"}). XML={s.XmlTrace ?? "?"}");
                continue;
            }

            if (valueExpr.Contains("_parent.", StringComparison.Ordinal))
            {
                var useDirectEqualityForParentBrowseHelper =
                    ResolveBaseClass(t) == "BusinessProcessBase" &&
                    !t.View.ShouldGenerate &&
                    string.Equals(t.Execution.Activity, "Activities.Browse", StringComparison.Ordinal);
                if (useDirectEqualityForParentBrowseHelper)
                    sb.AppendLine($"        Where.Add({BuildFilterIsEqualTo(t, fromCol, valueExpr)});");
                else
                    sb.AppendLine($"        Where.Add({BuildFilterBindEqualTo(t, fromCol, valueExpr)});");
                continue;
            }

            if (TryBuildRequiredEmbeddedSubformKeyRangeFromOperands(
                    fromCol,
                    t,
                    allTasks,
                    valueExpr,
                    valueExpr,
                    out var requiredSubformRange))
            {
                sb.AppendLine($"        Where.Add({requiredSubformRange});");
                continue;
            }

            t.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(
                s.RangeMin.Value,
                out var rangeSource);
            var rangeFilter = IsLiteralRangeAssignment(rangeSource)
                ? BuildFilterBindEqualTo(t, fromCol, valueExpr)
                : BuildFilterIsEqualTo(t, fromCol, valueExpr);
            sb.AppendLine($"        Where.Add({rangeFilter});");
        }

        _ = emittedRangeWhere;

        foreach (var s in t.SelectsSemantic.Items.Where(x =>
                     x.HasLocate &&
                     (x.SourceDbObj == primaryObj || !x.SourceDbObj.HasValue)))
        {
            if (t.LocateExpressionId.HasValue)
                continue;
            fromColumnsById.TryGetValue(s.ColumnId, out var col);
            if (col is null || !s.LocateMin.HasValue)
                continue;
            var fromCol = $"{primaryMember}.{ResolveDataObjectColumnMemberName(fromEntity, col)}";
            var locateExpr = ResolveFilterOperandExpression(t, fromCol, s.LocateMin.Value, dataObjects);
            if (string.IsNullOrWhiteSpace(locateExpr))
                continue;
            var locateTarget = string.Equals(ResolveBaseClass(t), "BusinessProcessBase", StringComparison.Ordinal)
                ? "Where"
                : "StartOnRowWhere";
            if (TrySplitCndRangeExpression(locateExpr, out var condExpr, out var valueExpr))
            {
                condExpr = EmitExpressionForContext(condExpr, t, CreateBooleanConditionEmissionContext());
                sb.AppendLine($"        {locateTarget}.Add(CndRange(() => {condExpr}, {BuildFilterIsGreaterOrEqualTo(t, fromCol, valueExpr)}));");
            }
            else if (!string.Equals(fromCol, locateExpr, StringComparison.Ordinal))
                sb.AppendLine($"        {locateTarget}.Add({BuildFilterIsGreaterOrEqualTo(t, fromCol, locateExpr)});");
        }
    }

    private static string ResolveSelectRangeWhereExpression(
        TaskLogicSelectDef select,
        string fromCol,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (!select.HasRange)
            return "";

        var isDirectLiteralRange =
            select.RangeMin.HasValue &&
            select.RangeMax.HasValue &&
            select.RangeMin.Value == select.RangeMax.Value &&
            TryResolveExpressionAsStringLiteralCode(task, select.RangeMin.Value, dataObjects, out _);
        var cndRangeExpr = isDirectLiteralRange ? "" : ResolveCndRangeExpressionForSelect(select, fromCol, task, dataObjects);
        if (!string.IsNullOrWhiteSpace(cndRangeExpr))
            return cndRangeExpr;

        if (select.RangeMin.HasValue && select.RangeMax.HasValue)
        {
            var minExpr = ResolveFilterOperandExpression(task, fromCol, select.RangeMin.Value, dataObjects);
            var maxExpr = ResolveFilterOperandExpression(task, fromCol, select.RangeMax.Value, dataObjects);
            if (!string.IsNullOrWhiteSpace(minExpr) && !string.IsNullOrWhiteSpace(maxExpr))
            {
                if (select.RangeMin.Value == select.RangeMax.Value || string.Equals(minExpr, maxExpr, StringComparison.Ordinal))
                {
                    var initializeInsertedRow =
                        isDirectLiteralRange ||
                        IsBooleanLiteralExpression(minExpr);
                    return initializeInsertedRow
                        ? BuildFilterBindEqualTo(task, fromCol, minExpr)
                        : BuildFilterIsEqualTo(task, fromCol, minExpr);
                }
                return $"{BuildFilterIsGreaterOrEqualTo(task, fromCol, minExpr)}.And({BuildFilterIsLessOrEqualTo(task, fromCol, maxExpr)})";
            }
        }

        if (select.RangeMin.HasValue)
        {
            var minExpr = ResolveFilterOperandExpression(task, fromCol, select.RangeMin.Value, dataObjects);
            if (!string.IsNullOrWhiteSpace(minExpr))
                return BuildFilterIsGreaterOrEqualTo(task, fromCol, minExpr);
        }

        if (select.RangeMax.HasValue)
        {
            var maxExpr = ResolveFilterOperandExpression(task, fromCol, select.RangeMax.Value, dataObjects);
            if (!string.IsNullOrWhiteSpace(maxExpr))
                return BuildFilterIsLessOrEqualTo(task, fromCol, maxExpr);
        }

        return "";
    }

    private static void EmitTaskRangeExpressions(
        StringBuilder sb,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (task.RangeExpressionId.HasValue)
        {
            var rangeExpr = ResolveExpressionCode(task.RangeExpressionId.Value.ToString(), task, dataObjects, CreateBooleanConditionEmissionContext());
            if (!string.IsNullOrWhiteSpace(rangeExpr))
                sb.AppendLine($"        NonDbWhere.Add(() => {rangeExpr});");
        }

        foreach (var info in task.VarRangeInfos.Where(x => string.Equals(x.Mode, "F", StringComparison.OrdinalIgnoreCase)))
        {
            if (!task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(info.VarRangeVeeIsn, out var expr) ||
                expr is null ||
                string.IsNullOrWhiteSpace(expr.Syntax))
                continue;

            var sourceSyntax = expr.Syntax.Trim();
            if (TrySplitTopLevelXpaBooleanBinaryExpression(sourceSyntax, out var booleanLeft, out var booleanOperator, out var booleanRight))
            {
                if (string.Equals(booleanOperator, "&&", StringComparison.Ordinal) &&
                    TrySplitSimpleVarRangeEquality(booleanLeft, out var rangeToken, out var rangeSource))
                {
                    var rangeLeftExpr = ResolveExpressionOrdinalBinding(rangeToken, task, _allTasks ?? Array.Empty<TaskSemantic>(), dataObjects);
                    if (string.IsNullOrWhiteSpace(rangeLeftExpr))
                        continue;

                    var rangeValueExpr = TryCreateFilterComparisonContext(task, rangeLeftExpr, out var rangeComparisonContext)
                        ? ResolveSourceFragmentCode(rangeSource, task, dataObjects, rangeComparisonContext)
                        : ResolveSourceFragmentCode(rangeSource, task, dataObjects, CreateExpectedEmissionContext(default));
                    var conditionExpr = ResolveSourceFragmentCode(booleanRight, task, dataObjects, CreateBooleanConditionEmissionContext());
                    if (!string.IsNullOrWhiteSpace(rangeValueExpr) && !string.IsNullOrWhiteSpace(conditionExpr))
                    {
                        if (string.Equals(rangeSource, "Date()", StringComparison.OrdinalIgnoreCase))
                            rangeValueExpr = "db.Date()";
                        sb.AppendLine($"        Where.Add(CndRange(() => {conditionExpr}, {BuildFilterIsEqualTo(task, rangeLeftExpr, rangeValueExpr)}));");
                    }
                    continue;
                }

                // VarRangeInfo is a range declaration, not an arbitrary row predicate.
                // If the expression cannot be decomposed into an equality plus its
                // optional condition, emitting the whole boolean expression as Where
                // changes XPA semantics and can reject a freshly inserted row before
                // the user has a chance to fill its fields.
                continue;
            }

            if (!TrySplitSimpleVarRangeEquality(sourceSyntax, out var token, out var rhsSource))
                continue;

            var leftExpr = ResolveExpressionOrdinalBinding(token, task, _allTasks ?? Array.Empty<TaskSemantic>(), dataObjects);
            if (string.IsNullOrWhiteSpace(leftExpr))
                continue;

            if (TrySplitTopLevelXpaBooleanBinaryExpression(rhsSource, out var rhsRangeSource, out var rhsRangeOperator, out var rhsConditionSource) &&
                string.Equals(rhsRangeOperator, "&&", StringComparison.Ordinal))
            {
                var rhsValueExpr = TryCreateFilterComparisonContext(task, leftExpr, out var splitComparisonContext)
                    ? ResolveSourceFragmentCode(rhsRangeSource, task, dataObjects, splitComparisonContext)
                    : TranslateXpaExpressionToCSharp(rhsRangeSource, task, dataObjects);
                var rhsConditionExpr = ResolveSourceFragmentCode(rhsConditionSource, task, dataObjects, CreateBooleanConditionEmissionContext());
                if (!string.IsNullOrWhiteSpace(rhsValueExpr) && !string.IsNullOrWhiteSpace(rhsConditionExpr))
                {
                    if (string.Equals(rhsRangeSource, "Date()", StringComparison.OrdinalIgnoreCase))
                        rhsValueExpr = "db.Date()";
                    sb.AppendLine($"        Where.Add(CndRange(() => {rhsConditionExpr}, {BuildFilterIsEqualTo(task, leftExpr, rhsValueExpr)}));");
                }
                continue;
            }

            var rhsExpr = TryCreateFilterComparisonContext(task, leftExpr, out var comparisonContext)
                ? ResolveSourceFragmentCode(rhsSource, task, dataObjects, comparisonContext)
                : TranslateXpaExpressionToCSharp(rhsSource, task, dataObjects);
            if (string.IsNullOrWhiteSpace(rhsExpr))
                continue;
            if (string.Equals(rhsSource, "Date()", StringComparison.OrdinalIgnoreCase))
                rhsExpr = "db.Date()";

            var normalizedRhsExpr = StripRedundantOuterParentheses(rhsExpr);
            var rhsBoolean = SplitTopLevelBooleanBinaryExpression(normalizedRhsExpr);
            if (rhsBoolean is not null)
            {
                sb.AppendLine($"        Where.Add(\"(({{0}} = {{1}}) {rhsBoolean.Value.Operator} ({{2}}))\", {leftExpr}, {rhsBoolean.Value.Left}, {rhsBoolean.Value.Right});");
            }
            else
            {
                sb.AppendLine($"        Where.Add(\"{{0}} = {{1}}\", {leftExpr}, {rhsExpr});");
            }
        }
    }

    private static bool TrySplitSimpleVarRangeEquality(string syntax, out string token, out string valueSource)
    {
        token = "";
        valueSource = "";
        if (string.IsNullOrWhiteSpace(syntax))
            return false;

        var trimmed = syntax.Trim();
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

            if (ch == ')')
            {
                if (depth > 0)
                    depth--;
                continue;
            }

            if (depth != 0 || ch != '=')
                continue;

            var before = i > 0 ? trimmed[i - 1] : '\0';
            var after = i + 1 < trimmed.Length ? trimmed[i + 1] : '\0';
            if (before is '<' or '>' or '=' || after == '=')
                continue;

            token = trimmed[..i].Trim();
            valueSource = trimmed[(i + 1)..].Trim();
            return Regex.IsMatch(token, @"^[A-Za-z]+$") && !string.IsNullOrWhiteSpace(valueSource);
        }

        return false;
    }


    private static IReadOnlyList<TaskLogicSelectDef> GetEffectiveRangeParameterSelects(TaskSemantic task)
    {
        if (_effectiveRangeParameterSelectsCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var parameters = task.SelectsSemantic.Items
            .Where(x => x.Type == "V" && x.IsParameter)
            .ToList();
        if (parameters.Count <= 1)
        {
            _effectiveRangeParameterSelectsCache[task.Ordinal] = parameters;
            return parameters;
        }
        var filtered = parameters
            .Where(x =>
            {
                var resource = ResolveTaskResourceColumn(task, x.ColumnId);
                return resource is null || !LooksLikeIoNameResource(resource.Name);
            })
            .ToList();
        if (filtered.Count > 0)
        {
            _effectiveRangeParameterSelectsCache[task.Ordinal] = filtered;
            return filtered;
        }
        var ioToUseParam = ResolveIoToUseParameterSelect(task);
        if (ioToUseParam is null)
        {
            _effectiveRangeParameterSelectsCache[task.Ordinal] = parameters;
            return parameters;
        }
        var result = parameters.Where(x => !ReferenceEquals(x, ioToUseParam)).ToList();
        _effectiveRangeParameterSelectsCache[task.Ordinal] = result;
        return result;
    }

    private static TaskLogicSelectDef? ResolveIoToUseParameterSelect(TaskSemantic task)
    {
        var parameters = task.SelectsSemantic.Items
            .Where(x => x.Type == "V" && x.IsParameter)
            .ToList();
        if (parameters.Count == 0)
            return null;

        foreach (var parameter in parameters)
        {
            var resource = ResolveTaskResourceColumn(task, parameter.ColumnId);
            if (resource is not null && LooksLikeIoNameResource(resource.Name))
                return parameter;
        }

        if (task.Io?.IoToUseColumnId is int ioToUseColumnId)
        {
            var direct = parameters.FirstOrDefault(x => x.ColumnId == ioToUseColumnId);
            if (direct is not null)
                return direct;

            var byIndex = ioToUseColumnId - 1;
            if (byIndex >= 0 && byIndex < parameters.Count)
                return parameters[byIndex];

            var byShiftedIndex = ioToUseColumnId - 2;
            if (byShiftedIndex >= 0 && byShiftedIndex < parameters.Count)
                return parameters[byShiftedIndex];
        }

        return parameters.FirstOrDefault();
    }

    private static bool LooksLikeIoNameResource(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var normalized = Regex.Replace(name, "[^A-Za-z0-9]+", "").ToUpperInvariant();
        return normalized.Contains("IONAME", StringComparison.Ordinal)
               || normalized.Contains("IODEVICE", StringComparison.Ordinal)
               || normalized.StartsWith("PIIO", StringComparison.Ordinal);
    }

    private static bool NeedsUnicodeFileWriterEncoding(TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (task.ResourcesSemantic.Ordered.Any(r => string.Equals(r.AttrObj, "FIELD_UNICODE", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (task.ParentOrdinal.HasValue)
        {
            _tasksByOrdinal.TryGetValue(task.ParentOrdinal.Value, out var parent);
            if (parent is not null &&
                parent.ResourcesSemantic.Ordered.Any(r => string.Equals(r.AttrObj, "FIELD_UNICODE", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private static bool TaskHasConcreteDataSource(TaskSemantic task)
    {
        return task.PrimaryDbObj.HasValue ||
               task.InformationDbObj.HasValue ||
               task.ResourceDataObjects.Count > 0 ||
               task.ResourceDbs.Count > 0;
    }

    private static string ResolveCndRangeExpressionForSelect(TaskLogicSelectDef select, string fromCol, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (!select.HasRange || !select.RangeMin.HasValue || string.IsNullOrWhiteSpace(fromCol))
            return "";
        if (!TryResolveSourceCndRangeOperands(
                select.RangeMin.Value,
                fromCol,
                task,
                dataObjects,
                out var cond,
                out var val))
        {
            var expr = ResolveExpressionCode(select.RangeMin.Value.ToString(), task, dataObjects);
            if (string.IsNullOrWhiteSpace(expr) ||
                !TrySplitCndRangeExpression(expr, out cond, out val))
                return "";

            cond = EmitExpressionForContext(cond, task, CreateBooleanConditionEmissionContext());
            val = EmitComparisonRightExpression(task, fromCol, val);
        }

        // A parent-bound value still belongs to the conditional range.  Dropping
        // CndRange here turns an optional XPA range into an unconditional filter
        // (for example, filtering TipoPCP by an empty parameter during OP
        // explosion). BindEqualTo keeps the parent binding semantics, while the
        // outer CndRange preserves whether that filter is active.
        var filter = val.Contains("_parent.", StringComparison.Ordinal)
            ? BuildFilterBindEqualTo(task, fromCol, val)
            : BuildFilterIsEqualTo(task, fromCol, val);
        return $"CndRange(() => {cond}, {filter})";
    }

    private static bool TryResolveSourceCndRangeOperands(
        int expressionOrdinal,
        string fromCol,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string condition,
        out string value)
    {
        condition = "";
        value = "";
        if (!task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expressionOrdinal, out var expression) ||
            expression is null)
            return false;

        var source = ResolveExpressionEntrySourceSyntax(expression);
        if (!TryParseFunctionCall(source, out var functionName, out var arguments) ||
            !string.Equals(
                NormalizeXpaFunctionContractName(functionName),
                "CNDRANGE",
                StringComparison.Ordinal) ||
            arguments.Count < 2)
            return false;

        condition = ResolveSourceFragmentCode(
            arguments[0],
            task,
            dataObjects,
            CreateBooleanConditionEmissionContext());

        if (TryCreateFilterComparisonContext(task, fromCol, out var comparisonContext))
        {
            value = ResolveSourceFragmentCode(
                arguments[1],
                task,
                dataObjects,
                comparisonContext);
        }
        else
        {
            value = TranslateXpaExpressionToCSharp(arguments[1], task, dataObjects);
            value = EmitComparisonRightExpression(task, fromCol, value);
        }

        condition = condition.Trim();
        value = value.Trim();
        return !string.IsNullOrWhiteSpace(condition) &&
               !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryBuildRequiredEmbeddedSubformKeyRange(
        TaskLogicSelectDef select,
        string fromCol,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic>? allTasks,
        out string filter)
    {
        filter = "";
        var embeddedSubform = IsEmbeddedSubformTask(task, allTasks);
        if (!embeddedSubform ||
            !select.HasRange ||
            !select.RangeMin.HasValue ||
            (select.RangeMax.HasValue && select.RangeMax.Value != select.RangeMin.Value))
            return false;

        if (!TryResolveSourceCndRangeOperands(
                select.RangeMin.Value,
                fromCol,
                task,
                dataObjects,
                out var condition,
                out var value))
        {
            var expression = ResolveExpressionCode(select.RangeMin.Value.ToString(), task, dataObjects);
            if (!TrySplitCndRangeExpression(expression, out condition, out value))
                return false;
        }

        return TryBuildRequiredEmbeddedSubformKeyRangeFromOperands(
            fromCol,
            task,
            allTasks,
            condition,
            value,
            out filter);
    }

    private static bool TryBuildRequiredEmbeddedSubformKeyRangeFromOperands(
        string fromCol,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic>? allTasks,
        string condition,
        string value,
        out string filter)
    {
        filter = "";
        if (!IsEmbeddedSubformTask(task, allTasks) ||
            string.IsNullOrWhiteSpace(condition) ||
            string.IsNullOrWhiteSpace(value))
            return false;

        // CndRange(P_KEY, P_KEY) is commonly used by callable searches to make
        // a parameter optional. In an embedded subform, however, disabling the
        // range for an empty numeric master key exposes rows from every master.
        // Keep the range mandatory only for the exact key-parameter idiom.
        if (!string.Equals(
                NormalizeRequiredSubformRangeOperand(condition),
                NormalizeRequiredSubformRangeOperand(value),
                StringComparison.Ordinal))
            return false;

        var isNumericKeyParameter = GetTaskParameters(task)
            .Any(parameter =>
                string.Equals(parameter.ParameterType, "NumberParameter", StringComparison.Ordinal) &&
                string.Equals(
                NormalizeRequiredSubformRangeOperand(parameter.ColumnMember),
                NormalizeRequiredSubformRangeOperand(value),
                StringComparison.Ordinal));
        if (!isNumericKeyParameter)
            return false;

        var comparisonValue = EmitComparisonRightExpression(task, fromCol, value);
        filter = BuildFilterIsEqualTo(task, fromCol, comparisonValue);
        return true;
    }

    private static string NormalizeRequiredSubformRangeOperand(string expression)
    {
        var current = StripRedundantOuterParentheses(expression.Trim());
        while (TryParseFunctionCall(current, out var functionName, out var arguments) &&
               arguments.Count == 1 &&
               (string.Equals(functionName, "u.CastToBool", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(functionName, "u.CastToNumber", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(functionName, "u.CastToText", StringComparison.OrdinalIgnoreCase)))
        {
            current = StripRedundantOuterParentheses(arguments[0].Trim());
        }

        return NormalizeKey(current);
    }

    private static bool IsEmbeddedSubformTask(
        TaskSemantic task,
        IReadOnlyList<TaskSemantic>? taskScope)
    {
        var allTasks = taskScope ?? _allTasks ?? Array.Empty<TaskSemantic>();
        if (allTasks.Any(owner =>
                owner.View.SubformBindings.Any(binding =>
                    binding.Kind == ViewSubformBindingKind.Task &&
                    binding.TargetTaskOrdinal == task.Ordinal)))
            return true;

        if (!task.ParentOrdinal.HasValue)
            return false;

        var parent = GetTaskByOrdinal(task.ParentOrdinal, allTasks);
        if (parent is null)
            return false;

        if (parent.View.SubformBindings.Any(binding =>
                binding.Kind == ViewSubformBindingKind.Task &&
                binding.TargetTaskOrdinal == task.Ordinal))
            return true;

        return task.SubtaskIndex.HasValue &&
               parent.Form?.Controls.Any(control =>
                   string.Equals(control.Model, "CTRL_GUI0_SUBFORM", StringComparison.OrdinalIgnoreCase) &&
                   !control.SubformComponentId.HasValue &&
                   control.SubformTaskNumber == task.SubtaskIndex) == true;
    }

    private static bool TrySplitCndRangeExpression(string expr, out string cond, out string val)
    {
        cond = "";
        val = "";
        if (string.IsNullOrWhiteSpace(expr))
            return false;
        if (!TryParseFunctionCall(expr.Trim(), out var functionName, out var args))
            return false;
        if (!string.Equals(functionName, "u.CndRange", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(functionName, "CndRange", StringComparison.OrdinalIgnoreCase))
            return false;
        if (args.Count < 2)
            return false;
        cond = args[0].Trim();
        val = args[1].Trim();
        return !string.IsNullOrWhiteSpace(cond) && !string.IsNullOrWhiteSpace(val);
    }

    private static bool ShouldSuppressRangeCondition(string? conditionExpression, string comparedValue)
    {
        if (string.IsNullOrWhiteSpace(conditionExpression))
            return false;

        var trimmedCond = conditionExpression.Trim();
        var trimmedValue = comparedValue.Trim();
        if (string.Equals(trimmedCond, trimmedValue, StringComparison.Ordinal))
            return true;
        if (IsWholeStringLiteralExpression(trimmedCond))
            return true;
        if (IsSimpleIdentifierPath(trimmedCond))
            return true;
        return false;
    }

    private static bool IsBooleanLiteralExpression(string value)
    {
        var trimmed = value.Trim();
        return string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLiteralRangeAssignment(ExpressionEntrySemantic? expression)
    {
        if (expression is null)
            return false;
        if (expression.IsStringLiteral)
            return true;

        var source = expression.SourceSyntax;
        return TryResolveXpaLogicalLiteralCode(source, out _) ||
               IsNumericLiteralExpressionCentral(source) ||
               IsSourceDateLiteral(source) ||
               IsSourceTimeLiteral(source);
    }

    private static bool IsSimpleIdentifierPath(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;
        var parts = trimmed.Split('.');
        return parts.All(IsSimpleIdentifier);
    }

    private static List<string> SplitTopLevelArgs(string input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
            return result;
        var depth = 0;
        var inString = false;
        var quoteChar = '\0';
        var sb = new StringBuilder();
        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (inString)
            {
                sb.Append(ch);
                if (ch == quoteChar)
                    inString = false;
                continue;
            }
            if (ch == '"' || ch == '\'')
            {
                inString = true;
                quoteChar = ch;
                sb.Append(ch);
                continue;
            }
            if (ch == '(')
            {
                depth++;
                sb.Append(ch);
                continue;
            }
            if (ch == ')')
            {
                depth = Math.Max(0, depth - 1);
                sb.Append(ch);
                continue;
            }
            if (ch == ',' && depth == 0)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0)
            result.Add(sb.ToString());
        return result;
    }

    private static string ResolveParameterRangeConditionExpression(TaskLogicSelectDef select, string parameterExpr, TaskSemantic task)
    {
        var rc = ResolveTaskResourceColumn(task, select.ColumnId);
        if (rc is null)
            return "";

        var conditionSource = parameterExpr?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(conditionSource))
        {
            while (SplitTopLevelArithmeticExpression(conditionSource) is { } concat &&
                   string.Equals(concat.Operator, "+", StringComparison.Ordinal))
            {
                var left = concat.Left.Trim();
                var right = concat.Right.Trim();
                if (TryGetWholeCSharpStringLiteral(right, out _))
                {
                    conditionSource = left;
                    continue;
                }

                if (TryGetWholeCSharpStringLiteral(left, out _))
                {
                    conditionSource = right;
                    continue;
                }

                break;
            }
        }

        var sourceReturnType = TryResolveTaskResourceStrictReturnType(rc, task, out var resolvedReturnType)
            ? resolvedReturnType
            : MapAttrObjToReturnType(rc.AttrObj);
        return EmitFromReliableTypeEvidence(
            conditionSource,
            sourceReturnType,
            "Bool",
            "data-view-range-condition");
    }
}


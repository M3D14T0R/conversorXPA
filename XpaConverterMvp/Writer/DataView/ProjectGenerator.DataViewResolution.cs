using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static Dictionary<string, int> BuildParameterExpressionOrderMap(TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (_parameterExpressionOrderCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var selectMap = BuildSelectNameToExpressionMap(task, dataObjects);
        var result = task.SelectsSemantic.Items
            .Where(s => s.IsParameter && s.Type == "V")
            .Select((s, i) => (Expr: selectMap.TryGetValue(s.Name, out var e) ? e : "", Order: i))
            .Where(x => !string.IsNullOrWhiteSpace(x.Expr))
            .GroupBy(x => x.Expr, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(x => x.Order), StringComparer.OrdinalIgnoreCase);
        _parameterExpressionOrderCache[task.Ordinal] = result;
        return result;
    }

    private static Dictionary<string, string> BuildSelectNameToExpressionMap(TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (_selectNameToExpressionMapCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sel in task.SelectsSemantic.Items)
        {
            if (string.IsNullOrWhiteSpace(sel.Name))
                continue;

            var expr = ResolveSelectExpressionByName(sel.Name!, task, dataObjects);
            if (!string.IsNullOrWhiteSpace(expr))
                result[sel.Name!] = expr;
        }

        foreach (var kv in task.SelectsSemantic.NameToExpression)
        {
            if (!result.ContainsKey(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                result[kv.Key] = kv.Value;
        }

        _selectNameToExpressionMapCache[task.Ordinal] = result;
        return result;
    }

    private static Dictionary<string, string> BuildApplicationSelectMap(IReadOnlyList<TaskSemantic> tasks, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (_applicationSelectMap.Count > 0)
            return _applicationSelectMap;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var appTask = tasks.FirstOrDefault(t => t.MainProgram) ?? tasks.FirstOrDefault(t => t.ParentOrdinal is null);
        if (appTask is null)
            return result;
        var appMap = BuildSelectNameToExpressionMap(appTask, dataObjects);
        foreach (var kv in appMap)
            result[kv.Key] = $"Application.Instance.{kv.Value}";
        _applicationSelectMap = result;
        return result;
    }

    private static bool TryParseWholeXpaSingleQuotedLiteral(string expr, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(expr))
            return false;
        var s = expr.Trim();
        if (s.Length < 2 || s[0] != '\'' || s[^1] != '\'')
            return false;
        var sb = new StringBuilder();
        for (var i = 1; i < s.Length - 1; i++)
        {
            var ch = s[i];
            if (ch == '\'')
            {
                if (i + 1 < s.Length - 1 && s[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i++;
                    continue;
                }
                // Unescaped quote before the end means this is not a single whole literal.
                return false;
            }
            sb.Append(ch);
        }
        value = sb.ToString();
        return true;
    }

    private static List<(TaskLogicLinkDef Link, string MemberName)> BuildLinkMembers(
        TaskSemantic t,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<(int DbObj, string ModelType, string MemberName)> modelMembers,
        int? primaryObj)
    {
        var cacheKey = string.Join("|",
            t.Ordinal.ToString(CultureInfo.InvariantCulture),
            primaryObj?.ToString(CultureInfo.InvariantCulture) ?? "");
        if (_linkMembersCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var result = new List<(TaskLogicLinkDef Link, string MemberName)>();
        var seqByObj = new Dictionary<int, int>();
        foreach (var link in t.DataView.Links)
        {
            if (!seqByObj.ContainsKey(link.DbObj))
                seqByObj[link.DbObj] = 0;
            seqByObj[link.DbObj]++;
            var n = seqByObj[link.DbObj];

            var existing = modelMembers.FirstOrDefault(m => m.DbObj == link.DbObj).MemberName;
            var member = existing;
            if (link.DbObj == primaryObj && n == 1 && !string.IsNullOrWhiteSpace(existing))
            {
                member = existing + "_";
            }
            else if (link.DbObj == primaryObj || n > 1)
            {
                var d = dataObjects.FirstOrDefault(x => x.Ordinal == link.DbObj);
                var baseName = d is null ? "Link" + link.DbObj : ToEntityTypeName(d.Name);
                member = n == 1 ? baseName : baseName + n;
            }
            if (string.IsNullOrWhiteSpace(member))
                member = "Link" + link.DbObj;
            result.Add((link, member));
        }
        _linkMembersCache[cacheKey] = result;
        return result;
    }

    private static string ResolveOrderByExpression(DataObjectDef target, TaskLogicLinkDef link)
    {
        DataIndexDef? idx = null;
        if (link.KeyIndexId.HasValue)
            idx = target.Indexes.FirstOrDefault(i => i.Id == link.KeyIndexId.Value);
        idx ??= target.Indexes.FirstOrDefault();
        if (idx is null)
            return "";
        return ResolveDataObjectIndexMemberNames(target, ResolveDataObjectTypeName(target)).TryGetValue(idx.Id, out var member)
            ? member
            : ToPascalIdentifier("SortBy" + idx.Name);
    }

    private static string ResolveLinkConditionFromSelectAssignments(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        (TaskLogicLinkDef Link, string MemberName) currentLink,
        DataObjectDef target)
        => ResolveLinkConditionFromSelectAssignments(task, dataObjects, currentLink, target, null, null);

    private static string ResolveLinkConditionFromSelectAssignments(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        (TaskLogicLinkDef Link, string MemberName) currentLink,
        DataObjectDef target,
        int? currentLinkSequence,
        IReadOnlyDictionary<int, Dictionary<int, List<TaskLogicSelectDef>>>? assignmentSelectByDbObjAndColumn)
    {
        if (!currentLink.Link.KeyIndexId.HasValue)
            return "";
        var keyCols = ResolveLinkKeyColumns(target, currentLink.Link);
        if (keyCols.Count == 0)
            return "";

        var parts = new List<string>();
        foreach (var keyCol in keyCols)
        {
            TaskLogicSelectDef? sel = null;
            if (assignmentSelectByDbObjAndColumn is not null &&
                assignmentSelectByDbObjAndColumn.TryGetValue(currentLink.Link.DbObj, out var byColumn) &&
                byColumn.TryGetValue(keyCol.Id, out var candidates))
                sel = candidates.FirstOrDefault(s => SelectBelongsToLinkSequence(s, currentLinkSequence));
            else
                sel = task.SelectsSemantic.ItemsByName.Values.FirstOrDefault(s =>
                    string.Equals(s.Type, "R", StringComparison.OrdinalIgnoreCase) &&
                    s.SourceDbObj == currentLink.Link.DbObj &&
                    s.ColumnId == keyCol.Id &&
                    s.AssignmentExpressionId.HasValue &&
                    SelectBelongsToLinkSequence(s, currentLinkSequence));
            if (sel is null)
                continue;

            var assignExpr = ResolveFilterOperandExpression(task, $"{currentLink.MemberName}.{ToPascalIdentifier(keyCol.Name)}", sel.AssignmentExpressionId!.Value, dataObjects);
            if (string.IsNullOrWhiteSpace(assignExpr))
                continue;
            parts.Add(BuildLinkComparison(task, currentLink.Link, $"{currentLink.MemberName}.{ToPascalIdentifier(keyCol.Name)}", assignExpr));
        }

        if (parts.Count == 0)
            return "";
        if (parts.Count == 1)
            return parts[0];
        var combined = parts[0];
        for (var i = 1; i < parts.Count; i++)
            combined += $".And({parts[i]})";
        return combined;
    }

    private static string ResolveLinkConditionFromSelectFilters(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        (TaskLogicLinkDef Link, string MemberName) currentLink,
        DataObjectDef target)
        => ResolveLinkConditionFromSelectFilters(task, dataObjects, currentLink, target, null, null);

    private static string ResolveLinkConditionFromSelectFilters(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        (TaskLogicLinkDef Link, string MemberName) currentLink,
        DataObjectDef target,
        int? currentLinkSequence,
        IReadOnlyDictionary<int, List<TaskLogicSelectDef>>? filterSelectsByDbObj)
    {
        var parts = new List<string>();
        var selects = filterSelectsByDbObj is not null &&
                      filterSelectsByDbObj.TryGetValue(currentLink.Link.DbObj, out var prefiltered)
            ? prefiltered
            : task.SelectsSemantic.Items.Where(s =>
                    string.Equals(s.Type, "R", StringComparison.OrdinalIgnoreCase) &&
                    s.SourceDbObj == currentLink.Link.DbObj &&
                    !s.AssignmentExpressionId.HasValue &&
                    (s.HasLocate || s.HasRange))
                .ToList();
        foreach (var sel in selects.Where(s => SelectBelongsToLinkSequence(s, currentLinkSequence)))
        {
            var col = target.Columns.FirstOrDefault(c => c.Id == sel.ColumnId);
            if (col is null)
                continue;

            var targetExpr = $"{currentLink.MemberName}.{ToPascalIdentifier(col.Name)}";
            var part = ResolveSelectFilterConditionForLink(task, dataObjects, currentLink.Link, sel, targetExpr);
            if (!string.IsNullOrWhiteSpace(part))
                parts.Add(part);
        }

        if (parts.Count == 0)
            return "";
        if (parts.Count == 1)
            return parts[0];
        var combined = parts[0];
        for (var i = 1; i < parts.Count; i++)
            combined += $".And({parts[i]})";
        return combined;
    }

    private static bool SelectBelongsToLinkSequence(TaskLogicSelectDef select, int? currentLinkSequence)
        => !currentLinkSequence.HasValue ||
           !select.SourceLinkSequence.HasValue ||
           select.SourceLinkSequence.Value == currentLinkSequence.Value;

    private static string ResolveSelectFilterConditionForLink(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        TaskLogicLinkDef link,
        TaskLogicSelectDef sel,
        string targetExpr)
    {
        var cacheKey = string.Join("|",
            task.Ordinal.ToString(CultureInfo.InvariantCulture),
            link.DbObj.ToString(CultureInfo.InvariantCulture),
            link.Mode ?? "",
            targetExpr,
            sel.ColumnId.ToString(CultureInfo.InvariantCulture),
            sel.HasLocate ? "L" : "",
            sel.LocateMin?.ToString(CultureInfo.InvariantCulture) ?? "",
            sel.LocateMax?.ToString(CultureInfo.InvariantCulture) ?? "",
            sel.HasRange ? "R" : "",
            sel.RangeMin?.ToString(CultureInfo.InvariantCulture) ?? "",
            sel.RangeMax?.ToString(CultureInfo.InvariantCulture) ?? "");
        if (_linkFilterConditionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var resolved = "";
        if (sel.HasLocate)
        {
            resolved = ResolveSelectFilterConditionCore(
                task,
                dataObjects,
                link,
                targetExpr,
                sel.LocateMin,
                sel.LocateMax);
        }

        if (string.IsNullOrWhiteSpace(resolved) && sel.HasRange)
        {
            resolved = ResolveSelectFilterConditionCore(
                task,
                dataObjects,
                link,
                targetExpr,
                sel.RangeMin,
                sel.RangeMax);
        }

        _linkFilterConditionCache[cacheKey] = resolved;
        return resolved;
    }

    private static string ResolveSelectFilterConditionCore(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        TaskLogicLinkDef link,
        string targetExpr,
        int? minId,
        int? maxId)
    {
        if (minId.HasValue && maxId.HasValue)
        {
            var minExpr = ResolveFilterOperandExpression(task, targetExpr, minId.Value, dataObjects);
            var maxExpr = ResolveFilterOperandExpression(task, targetExpr, maxId.Value, dataObjects);
            if (!string.IsNullOrWhiteSpace(minExpr) && !string.IsNullOrWhiteSpace(maxExpr))
            {
                if (minId.Value == maxId.Value || string.Equals(minExpr, maxExpr, StringComparison.Ordinal))
                    return BuildLinkComparison(task, link, targetExpr, minExpr);
                return BuildFilterIsBetween(task, targetExpr, minExpr, maxExpr);
            }
        }

        if (minId.HasValue)
        {
            var minExpr = ResolveFilterOperandExpression(task, targetExpr, minId.Value, dataObjects);
            if (!string.IsNullOrWhiteSpace(minExpr))
                return BuildFilterIsGreaterOrEqualTo(task, targetExpr, minExpr);
        }

        if (maxId.HasValue)
        {
            var maxExpr = ResolveFilterOperandExpression(task, targetExpr, maxId.Value, dataObjects);
            if (!string.IsNullOrWhiteSpace(maxExpr))
                return BuildFilterIsLessOrEqualTo(task, targetExpr, maxExpr);
        }

        return "";
    }

    private static string ResolveLinkConditionExpression(
        TaskSemantic task,
        DataObjectDef source,
        string sourceMember,
        DataObjectDef target,
        string targetMember,
        TaskLogicLinkDef link)
    {
        if (source.Ordinal == target.Ordinal)
            return "";
        var targetColumns = ResolveLinkKeyColumns(target, link);
        foreach (var tc in targetColumns)
        {
            var sc = source.Columns.FirstOrDefault(c => string.Equals(c.Name, tc.Name, StringComparison.OrdinalIgnoreCase)
                                                     || string.Equals(c.DbColumnName, tc.DbColumnName, StringComparison.OrdinalIgnoreCase));
            if (sc is not null)
                return BuildLinkComparison(task, link, $"{targetMember}.{ToPascalIdentifier(tc.Name)}", $"{sourceMember}.{ToPascalIdentifier(sc.Name)}");
        }
        return "";
    }

    private static string ResolveLinkConditionFromTaskContext(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<(int DbObj, string ModelType, string MemberName)> modelMembers,
        IReadOnlyList<(TaskLogicLinkDef Link, string MemberName)> linkMembers,
        int linkIndex,
        (TaskLogicLinkDef Link, string MemberName) currentLink,
        DataObjectDef target)
    {
        if (!currentLink.Link.KeyIndexId.HasValue)
            return "";
        var keyCols = ResolveLinkKeyColumns(target, currentLink.Link);
        if (keyCols.Count == 0)
            return "";
        var returnHintExpr = !string.IsNullOrWhiteSpace(currentLink.Link.ReturnValueName)
            ? ResolveSelectExpressionByName(currentLink.Link.ReturnValueName!, task, dataObjects)
            : "";
        var hintNorm = NormalizeKey(returnHintExpr);
        if (HintTargetsMissingKeyColumns(hintNorm, keyCols))
            return "";

        var parts = new List<string>();
        foreach (var keyCol in keyCols)
        {
            var sourceExpr = ResolveBestLinkSourceExpression(task, dataObjects, modelMembers, linkMembers, linkIndex, currentLink.MemberName, currentLink.Link, keyCol);
            if (string.IsNullOrWhiteSpace(sourceExpr))
                continue;
            parts.Add(BuildLinkComparison(task, currentLink.Link, $"{currentLink.MemberName}.{ToPascalIdentifier(keyCol.Name)}", sourceExpr));
        }
        if (parts.Count == 0)
            return "";
        if (parts.Count == 1)
            return parts[0];
        var combined = parts[0];
        for (var i = 1; i < parts.Count; i++)
            combined += $".And({parts[i]})";
        return combined;
    }

    private static string ResolveLinkConditionFromLocateSelects(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<(int DbObj, string ModelType, string MemberName)> modelMembers,
        IReadOnlyList<(TaskLogicLinkDef Link, string MemberName)> linkMembers,
        int linkIndex,
        (TaskLogicLinkDef Link, string MemberName) currentLink,
        DataObjectDef target)
    {
        if (!currentLink.Link.KeyIndexId.HasValue)
            return "";

        var keyCols = ResolveLinkKeyColumns(target, currentLink.Link);
        if (keyCols.Count == 0)
            return "";

        var targetSelects = task.SelectsSemantic.Items
            .Where(s =>
                string.Equals(s.Type, "R", StringComparison.OrdinalIgnoreCase) &&
                s.SourceDbObj == currentLink.Link.DbObj &&
                s.HasLocate)
            .ToList();

        if (targetSelects.Count == 0)
            return "";

        var parts = new List<string>();
        foreach (var keyCol in keyCols)
        {
            var sel = targetSelects.FirstOrDefault(s => s.ColumnId == keyCol.Id);
            if (sel is null)
                continue;

            var targetExpr = $"{currentLink.MemberName}.{ToPascalIdentifier(keyCol.Name)}";
            if (sel.LocateMin.HasValue && sel.LocateMax.HasValue && sel.LocateMin.Value == sel.LocateMax.Value)
            {
                var sourceExpr = ResolveFilterOperandExpression(task, targetExpr, sel.LocateMin.Value, dataObjects);
                if (string.IsNullOrWhiteSpace(sourceExpr))
                    sourceExpr = ResolveBestLinkSourceExpression(task, dataObjects, modelMembers, linkMembers, linkIndex, currentLink.MemberName, currentLink.Link, keyCol);
                if (!string.IsNullOrWhiteSpace(sourceExpr))
                    parts.Add(BuildLinkComparison(task, currentLink.Link, targetExpr, sourceExpr));
                continue;
            }

            if (sel.LocateMin.HasValue)
            {
                var minExpr = ResolveFilterOperandExpression(task, targetExpr, sel.LocateMin.Value, dataObjects);
                if (!string.IsNullOrWhiteSpace(minExpr))
                    parts.Add($"{targetExpr}.IsGreaterOrEqualTo({minExpr})");
            }
            if (sel.LocateMax.HasValue)
            {
                var maxExpr = ResolveFilterOperandExpression(task, targetExpr, sel.LocateMax.Value, dataObjects);
                if (!string.IsNullOrWhiteSpace(maxExpr))
                    parts.Add($"{targetExpr}.IsLessOrEqualTo({maxExpr})");
            }
        }

        if (parts.Count == 0)
            return "";
        if (parts.Count == keyCols.Count)
        {
            var combinedAll = parts[0];
            for (var i = 1; i < parts.Count; i++)
                combinedAll += $".And({parts[i]})";
            return combinedAll;
        }

        return "";
    }

    private static string AppendLocateConstraintToLinkCondition(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        (TaskLogicLinkDef Link, string MemberName) currentLink,
        DataObjectDef target,
        string condExpr)
    {
        var extraParts = new List<string>();
        foreach (var sel in task.SelectsSemantic.ItemsByName.Values.Where(s =>
                     string.Equals(s.Type, "R", StringComparison.OrdinalIgnoreCase) &&
                     s.SourceDbObj == currentLink.Link.DbObj &&
                     !s.AssignmentExpressionId.HasValue &&
                     s.HasLocate))
        {
            var col = target.Columns.FirstOrDefault(c => c.Id == sel.ColumnId);
            if (col is null)
                continue;

            var targetExpr = $"{currentLink.MemberName}.{ToPascalIdentifier(col.Name)}";
            if (!string.IsNullOrWhiteSpace(condExpr) && condExpr.Contains(targetExpr, StringComparison.Ordinal))
                continue;
            if (sel.LocateMin.HasValue)
            {
                var minExpr = ResolveFilterOperandExpression(task, targetExpr, sel.LocateMin.Value, dataObjects);
                if (!string.IsNullOrWhiteSpace(minExpr))
                    extraParts.Add(BuildFilterIsGreaterOrEqualTo(task, targetExpr, minExpr));
            }
            if (sel.LocateMax.HasValue)
            {
                var maxExpr = ResolveFilterOperandExpression(task, targetExpr, sel.LocateMax.Value, dataObjects);
                if (!string.IsNullOrWhiteSpace(maxExpr))
                    extraParts.Add(BuildFilterIsLessOrEqualTo(task, targetExpr, maxExpr));
            }
        }

        if (extraParts.Count == 0)
            return condExpr;

        var combinedExtra = extraParts[0];
        for (var i = 1; i < extraParts.Count; i++)
            combinedExtra += $".And({extraParts[i]})";

        if (string.IsNullOrWhiteSpace(condExpr))
            return combinedExtra;
        return $"{condExpr}.And({combinedExtra})";
    }

    private static string ResolveBestLinkSourceExpression(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<(int DbObj, string ModelType, string MemberName)> modelMembers,
        IReadOnlyList<(TaskLogicLinkDef Link, string MemberName)> linkMembers,
        int linkIndex,
        string targetMemberName,
        TaskLogicLinkDef currentLink,
        DataColumnDef keyCol)
    {
        var keyNorm = NormalizeKey(keyCol.Name);
        var returnHintExpr = !string.IsNullOrWhiteSpace(currentLink.ReturnValueName)
            ? ResolveSelectExpressionByName(currentLink.ReturnValueName!, task, dataObjects)
            : "";
        var hintNorm = NormalizeKey(returnHintExpr);
        var candidates = new List<(string Expr, int Score)>();

        foreach (var mm in modelMembers)
        {
            var d = dataObjects.FirstOrDefault(x => x.Ordinal == mm.DbObj);
            if (d is null)
                continue;
            var col = d.Columns.FirstOrDefault(c => NormalizeKey(c.Name) == keyNorm || NormalizeKey(c.DbColumnName ?? "") == keyNorm);
            if (col is null)
                continue;
            var expr = $"{mm.MemberName}.{ToPascalIdentifier(col.Name)}";
            if (expr.StartsWith(targetMemberName + ".", StringComparison.Ordinal))
                continue;
            var exprNorm = NormalizeKey(expr);
            var score = 70 + TokenOverlapScore(returnHintExpr, expr) +
                        ScoreLinkExpressionAgainstHint(hintNorm, exprNorm);
            candidates.Add((expr, score));
        }

        for (var i = 0; i < linkIndex; i++)
        {
            var lm = linkMembers[i];
            var d = dataObjects.FirstOrDefault(x => x.Ordinal == lm.Link.DbObj);
            if (d is null)
                continue;
            var col = d.Columns.FirstOrDefault(c => NormalizeKey(c.Name) == keyNorm || NormalizeKey(c.DbColumnName ?? "") == keyNorm);
            if (col is null)
                continue;
            var expr = $"{lm.MemberName}.{ToPascalIdentifier(col.Name)}";
            if (expr.StartsWith(targetMemberName + ".", StringComparison.Ordinal))
                continue;
            var exprNorm = NormalizeKey(expr);
            var score = 90 - (linkIndex - i) + TokenOverlapScore(returnHintExpr, expr) +
                        ScoreLinkExpressionAgainstHint(hintNorm, exprNorm);
            candidates.Add((expr, score));
        }

        var selectMap = BuildSelectNameToExpressionMap(task, dataObjects);
        foreach (var s in task.SelectsSemantic.Items.Where(x => x.IsParameter && x.Type == "V"))
        {
            if (!selectMap.TryGetValue(s.Name, out var expr))
                continue;
            var exprNorm = NormalizeKey(expr);
            var score = TokenOverlapScore(keyCol.Name, expr) + 20 + TokenOverlapScore(returnHintExpr, expr) +
                        ScoreLinkExpressionAgainstHint(hintNorm, exprNorm);
            candidates.Add((expr, score));
        }

        var best = candidates.OrderByDescending(c => c.Score).FirstOrDefault();
        return best.Expr ?? "";
    }

    private static string ResolveSelectExpressionByName(string selectName, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(selectName))
            return "";

        if (_selectNameToExpressionMapCache.TryGetValue(task.Ordinal, out var cachedMap) &&
            cachedMap.TryGetValue(selectName, out var cachedExpr) &&
            !string.IsNullOrWhiteSpace(cachedExpr))
            return cachedExpr;

        if (task.SelectsSemantic.ItemsByName.TryGetValue(selectName, out var sel) && sel is not null)
        {
            var primaryObj = task.PrimaryDbObj ?? task.InformationDbObj;
            var primaryMember = ResolvePrimaryMember(task, primaryObj ?? 0, dataObjects);
            var dynamicExpr = ResolveSelectExpression(sel, task, dataObjects, primaryMember ?? "");
            if (!string.IsNullOrWhiteSpace(dynamicExpr))
                return dynamicExpr;
        }

        if (task.SelectsSemantic.NameToExpression.TryGetValue(selectName, out var expr) &&
            !string.IsNullOrWhiteSpace(expr))
            return expr;

        if (_parentSelectMapByTaskOrdinal.TryGetValue(task.Ordinal, out var parentSelectMap) &&
            parentSelectMap.TryGetValue(selectName, out var parentExpr) &&
            !string.IsNullOrWhiteSpace(parentExpr))
            return parentExpr;

        return "";
    }

    private static string NormalizeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    private static int TokenOverlapScore(string left, string right)
    {
        var lt = SplitAlphaNumericTokens(left).ToHashSet(StringComparer.Ordinal);
        var rt = SplitAlphaNumericTokens(right).ToHashSet(StringComparer.Ordinal);
        if (lt.Count == 0 || rt.Count == 0)
            return 0;
        return lt.Count(t => rt.Contains(t)) * 10;
    }

    private static IEnumerable<string> SplitAlphaNumericTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        var sb = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }

        if (sb.Length > 0)
            yield return sb.ToString();
    }

    private static int ScoreLinkExpressionAgainstHint(string hintNorm, string exprNorm)
    {
        if (string.IsNullOrWhiteSpace(hintNorm) || string.IsNullOrWhiteSpace(exprNorm))
            return 0;

        var overlap = TokenOverlapScore(hintNorm, exprNorm);
        var score = overlap * 10;
        if (overlap == 0)
            return 0;

        if (IsParameterLikeNormalizedExpression(exprNorm))
            score += 10;

        return score;
    }

    private static bool ConditionAlreadyCoversHint(string conditionExpression, string hintNorm)
    {
        if (string.IsNullOrWhiteSpace(conditionExpression) || string.IsNullOrWhiteSpace(hintNorm))
            return false;

        return TokenOverlapScore(NormalizeKey(conditionExpression), hintNorm) > 0;
    }

    private static bool IsMemberAccessExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = expression.Trim();
        if (trimmed.StartsWith("_parent.", StringComparison.Ordinal))
            return true;

        return trimmed.Contains('.', StringComparison.Ordinal);
    }

    private static bool IsParameterLikeNormalizedExpression(string exprNorm)
    {
        if (string.IsNullOrWhiteSpace(exprNorm) || exprNorm[0] != 'p')
            return false;

        return exprNorm.All(char.IsLetterOrDigit);
    }

    private static IReadOnlyList<DataColumnDef> ResolveLinkKeyColumns(DataObjectDef target, TaskLogicLinkDef link)
    {
        var cacheKey = target.Ordinal.ToString(CultureInfo.InvariantCulture) + "|" + (link.KeyIndexId?.ToString(CultureInfo.InvariantCulture) ?? "");
        if (_linkKeyColumnsCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var idx = link.KeyIndexId.HasValue ? target.Indexes.FirstOrDefault(i => i.Id == link.KeyIndexId.Value) : target.Indexes.FirstOrDefault();
        if (idx is null)
        {
            _linkKeyColumnsCache[cacheKey] = Array.Empty<DataColumnDef>();
            return _linkKeyColumnsCache[cacheKey];
        }
        var cols = idx.Segments
            .Select(s => target.Columns.FirstOrDefault(c => c.Id == s.ColumnId))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();
        _linkKeyColumnsCache[cacheKey] = cols;
        return cols;
    }

}


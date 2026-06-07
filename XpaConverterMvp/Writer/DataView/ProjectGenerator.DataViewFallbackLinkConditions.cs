using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveFallbackLinkConditionWithoutKey(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<(TaskLogicLinkDef Link, string MemberName)> linkMembers,
        int linkIndex,
        (TaskLogicLinkDef Link, string MemberName) currentLink,
        DataObjectDef target,
        Dictionary<int, int> linkParamCursorByDbObj)
    {
        var targetCols = target.Columns.OrderBy(c => c.Id).ToList();
        if (targetCols.Count == 0)
            return "";
        var targetKeyCols = ResolveLinkKeyColumns(target, currentLink.Link);
        if (!linkParamCursorByDbObj.TryGetValue(currentLink.Link.DbObj, out var cursor))
            cursor = 0;

        var selectMap = BuildSelectNameToExpressionMap(task, dataObjects);
        var parameterExprOrder = BuildParameterExpressionOrderMap(task, dataObjects);
        var sourceExprs = task.SelectsSemantic.Items
            .Where(s => s.Type == "V")
            .Select(s => selectMap.TryGetValue(s.Name, out var e) ? e : "")
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .ToList();
        var ancestor = task;
        var depth = 0;
        while (ancestor.ParentOrdinal.HasValue)
        {
            depth++;
            var parent = _allTasks.FirstOrDefault(x => x.Ordinal == ancestor.ParentOrdinal.Value);
            if (parent is null)
                break;
            var prefix = string.Concat(Enumerable.Repeat("_parent.", depth));
            sourceExprs.AddRange(parent.ResourcesSemantic.Ordered.Select(rc => prefix + ResolveTaskResourceMemberName(parent, rc)));
            var parentPrimary = parent.PrimaryDbObj ?? parent.InformationDbObj;
            var parentMembers = BuildModelMembers(parent, dataObjects);
            var primaryMember = parentMembers.FirstOrDefault(m => m.DbObj == parentPrimary).MemberName;
            if (!string.IsNullOrWhiteSpace(primaryMember))
            {
                var pd = dataObjects.FirstOrDefault(x => x.Ordinal == parentPrimary);
                if (pd is not null)
                {
                    foreach (var c in pd.Columns)
                        sourceExprs.Add($"{prefix}{primaryMember}.{ResolveDataObjectColumnMemberName(pd, c)}");
                }
            }
            foreach (var pm in parentMembers)
            {
                var pd = dataObjects.FirstOrDefault(x => x.Ordinal == pm.DbObj);
                if (pd is null)
                    continue;
                foreach (var c in pd.Columns)
                    sourceExprs.Add($"{prefix}{pm.MemberName}.{ResolveDataObjectColumnMemberName(pd, c)}");
            }
            ancestor = parent;
        }

        var firstKeyCol = targetKeyCols.ElementAtOrDefault(0);
        var secondKeyCol = targetKeyCols.ElementAtOrDefault(1);
        var firstKeySource = FindBestSourceExpressionForTargetColumn(sourceExprs, currentLink.MemberName, firstKeyCol, targetKeyCols, parameterExprOrder, preferParameterLike: true);
        if (TryBuildSingleKeyParameterFallbackLinkCondition(task, currentLink, target, targetKeyCols, firstKeyCol, firstKeySource, out var singleKeyParameterCondition))
            return singleKeyParameterCondition;

        if (TryBuildTwoKeyFallbackLinkCondition(
                task,
                sourceExprs,
                currentLink,
                target,
                targetKeyCols,
                parameterExprOrder,
                firstKeyCol,
                secondKeyCol,
                out var twoKeyCondition))
        {
            linkParamCursorByDbObj[currentLink.Link.DbObj] = cursor + 1;
            return twoKeyCondition;
        }

        if (TryBuildWriteModeTwoKeyFallbackLinkCondition(
                task,
                sourceExprs,
                currentLink,
                target,
                targetKeyCols,
                parameterExprOrder,
                firstKeyCol,
                secondKeyCol,
                out var writeModeTwoKeyCondition))
        {
            linkParamCursorByDbObj[currentLink.Link.DbObj] = cursor + 1;
            return writeModeTwoKeyCondition;
        }

        if (TryBuildInitialSingleKeyFallbackLinkCondition(
                task,
                sourceExprs,
                linkMembers,
                linkIndex,
                currentLink,
                target,
                cursor,
                targetKeyCols,
                parameterExprOrder,
                firstKeyCol,
                out var initialSingleKeyCondition))
        {
            linkParamCursorByDbObj[currentLink.Link.DbObj] = cursor + 1;
            return initialSingleKeyCondition;
        }

        // For non-key links moving from one linked table to another, prefer chaining by a
        // stable shared key from the earliest prior link (matches legacy conversion behavior).
        if (string.Equals(currentLink.Link.Mode, "R", StringComparison.OrdinalIgnoreCase) &&
            linkIndex > 0 &&
            linkMembers[linkIndex - 1].Link.DbObj != currentLink.Link.DbObj)
        {
            var chainedKeyCol = targetKeyCols.FirstOrDefault();
            if (chainedKeyCol is not null)
            {
                for (var i = 0; i < linkIndex; i++)
                {
                    var prev = linkMembers[i];
                    var pd = dataObjects.FirstOrDefault(x => x.Ordinal == prev.Link.DbObj);
                    if (pd is null)
                        continue;
                    var pc = pd.Columns.FirstOrDefault(c => NormalizeKey(c.Name) == NormalizeKey(chainedKeyCol.Name));
                    if (pc is null)
                        continue;
                    var src = $"{prev.MemberName}.{ResolveDataObjectColumnMemberName(pd, pc)}";
                    return BuildLinkComparison(task, currentLink.Link, $"{currentLink.MemberName}.{ResolveDataObjectColumnMemberName(target, chainedKeyCol)}", src);
                }
            }
        }

        var ranked = new List<(string ColName, string Expr, int Score)>();
        for (var i = 0; i < linkIndex; i++)
        {
            var lm = linkMembers[i];
            var d = dataObjects.FirstOrDefault(x => x.Ordinal == lm.Link.DbObj);
            if (d is null)
                continue;
            foreach (var tc in targetCols)
            {
                var sc = d.Columns.FirstOrDefault(c => NormalizeKey(c.Name) == NormalizeKey(tc.Name));
                if (sc is null)
                    continue;
                var expr = $"{lm.MemberName}.{ResolveDataObjectColumnMemberName(d, sc)}";
                if (!expr.StartsWith(currentLink.MemberName + ".", StringComparison.Ordinal))
                    ranked.Add((tc.Name, expr, 100 - (linkIndex - i) + ScoreLinkSourceForColumn(tc, expr, targetKeyCols)));
            }
        }
        foreach (var tc in targetCols)
        {
            foreach (var expr in sourceExprs)
            {
                if (expr.StartsWith(currentLink.MemberName + ".", StringComparison.Ordinal))
                    continue;
                ranked.Add((tc.Name, expr, 60 + ScoreLinkSourceForColumn(tc, expr, targetKeyCols, parameterExprOrder)));
            }
        }
        if (ranked.Count == 0)
            return "";

        var returnHintExpr = !string.IsNullOrWhiteSpace(currentLink.Link.ReturnValueName)
            ? ResolveSelectExpressionByName(currentLink.Link.ReturnValueName!, task, dataObjects)
            : "";
        var hintNorm = NormalizeKey(returnHintExpr);
        ranked = ranked
            .Select(x =>
            {
                var colNorm = NormalizeKey(x.ColName);
                var exprNorm = NormalizeKey(x.Expr);
                var s = x.Score;
                s += ScoreLinkHintCompatibility(hintNorm, colNorm, exprNorm);
                return (x.ColName, x.Expr, Score: s);
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        var bestKeyMatches = targetKeyCols
            .Select(keyCol => ranked.FirstOrDefault(x => string.Equals(x.ColName, keyCol.Name, StringComparison.OrdinalIgnoreCase)))
            .Where(x => !string.IsNullOrWhiteSpace(x.ColName))
            .GroupBy(x => x.ColName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(2)
            .ToList();
        if (bestKeyMatches.Count >= 2 &&
            !string.Equals(bestKeyMatches[0].Expr, bestKeyMatches[1].Expr, StringComparison.OrdinalIgnoreCase))
        {
            var p1 = BuildLinkComparison(task, currentLink.Link, $"{currentLink.MemberName}.{ToPascalIdentifier(bestKeyMatches[0].ColName)}", bestKeyMatches[0].Expr);
            var p2 = BuildLinkComparison(task, currentLink.Link, $"{currentLink.MemberName}.{ToPascalIdentifier(bestKeyMatches[1].ColName)}", bestKeyMatches[1].Expr);
            linkParamCursorByDbObj[currentLink.Link.DbObj] = cursor + 1;
            return p1 + ".And(" + p2 + ")";
        }

        var grouped = ranked.GroupBy(x => x.ColName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (grouped.Count == 0)
            return "";

        var paramOnly = grouped
            .Where(g => parameterExprOrder.ContainsKey(g.Expr))
            .OrderBy(g => parameterExprOrder[g.Expr])
            .ThenByDescending(g => g.Score)
            .ToList();
        if (paramOnly.Count > 0)
        {
            if (cursor >= paramOnly.Count)
                cursor = paramOnly.Count - 1;
            var pChosen = paramOnly[cursor];
            linkParamCursorByDbObj[currentLink.Link.DbObj] = cursor + 1;
            return BuildLinkComparison(task, currentLink.Link, $"{currentLink.MemberName}.{ToPascalIdentifier(pChosen.ColName)}", pChosen.Expr);
        }

        if (cursor >= grouped.Count)
            cursor = grouped.Count - 1;
        var chosen = grouped[cursor];
        linkParamCursorByDbObj[currentLink.Link.DbObj] = cursor + 1;
        return BuildLinkComparison(task, currentLink.Link, $"{currentLink.MemberName}.{ToPascalIdentifier(chosen.ColName)}", chosen.Expr);
    }
}


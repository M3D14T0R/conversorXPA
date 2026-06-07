using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static HashSet<string> GetAllowedParameterSelectNames(TaskSemantic task)
    {
        if (_allowedParameterSelectNamesCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!task.DeclaredParameterCount.HasValue || task.DeclaredParameterCount.Value <= 0)
        {
            _allowedParameterSelectNamesCache[task.Ordinal] = result;
            return result;
        }

        var paramVirtuals = task.SelectsSemantic.Items
            .Where(s => s.Type == "V" && s.IsParameter)
            .Select(s => s.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var count = Math.Min(task.DeclaredParameterCount.Value, paramVirtuals.Count);
        for (var i = 0; i < count; i++)
            result.Add(paramVirtuals[i]);
        _allowedParameterSelectNamesCache[task.Ordinal] = result;
        return result;
    }

    private static bool ContainsVariantGetExpression(string expression)
    {
        if (IsTopLevelCall(TryGetTopLevelFunctionName(expression), "u.VariantGet"))
            return true;

        if (!TryParseFunctionCall(expression, out _, out var args))
            return false;

        return args.Any(ContainsVariantGetExpression);
    }

    private static string OverrideLinkConditionByReturnHint(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<(TaskLogicLinkDef Link, string MemberName)> linkMembers,
        int linkIndex,
        (TaskLogicLinkDef Link, string MemberName) currentLink,
        DataObjectDef target,
        string condExpr)
    {
        if (string.IsNullOrWhiteSpace(currentLink.Link.ReturnValueName))
            return condExpr;
        var hint = NormalizeKey(ResolveSelectExpressionByName(currentLink.Link.ReturnValueName!, task, dataObjects));
        var condNorm = NormalizeKey(condExpr);
        if (string.IsNullOrWhiteSpace(hint) || ConditionAlreadyCoversHint(condNorm, hint))
            return condExpr;

        var targetColumns = ResolveLinkKeyColumns(target, currentLink.Link);
        if (targetColumns.Count == 0)
            targetColumns = target.Columns;
        if (TryBuildLinkOverrideByHint(task, dataObjects, linkMembers, linkIndex, currentLink, target, targetColumns, hint, out var overrideCondition))
            return overrideCondition;

        return condExpr;
    }

    private static string ResolveBestSourceByHint(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<(TaskLogicLinkDef Link, string MemberName)> linkMembers,
        int linkIndex,
        string hintNorm,
        string currentMemberName,
        DataColumnDef targetColumn)
    {
        var selectMap = BuildSelectNameToExpressionMap(task, dataObjects);
        var parameterExprOrder = BuildParameterExpressionOrderMap(task, dataObjects);
        var candidates = task.SelectsSemantic.Items
            .Where(s => s.Type == "V")
            .Select(s => selectMap.TryGetValue(s.Name, out var ex) ? ex : "")
            .Where(ex => !string.IsNullOrWhiteSpace(ex))
            .ToList();

        for (var i = linkIndex - 1; i >= 0; i--)
        {
            var lm = linkMembers[i];
            var d = dataObjects.FirstOrDefault(x => x.Ordinal == lm.Link.DbObj);
            if (d is null)
                continue;
            foreach (var column in d.Columns)
                candidates.Add($"{lm.MemberName}.{ResolveDataObjectColumnMemberName(d, column)}");
        }

        return FindBestSourceExpressionForTargetColumn(candidates, currentMemberName, targetColumn, null, parameterExprOrder, preferParameterLike: true, hintNorm: hintNorm);
    }

    private static bool TryBuildLinkOverrideByHint(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<(TaskLogicLinkDef Link, string MemberName)> linkMembers,
        int linkIndex,
        (TaskLogicLinkDef Link, string MemberName) currentLink,
        DataObjectDef target,
        IReadOnlyList<DataColumnDef> targetColumns,
        string hintNorm,
        out string condition)
    {
        condition = "";
        if (!HintRequestsOverride(hintNorm) || targetColumns.Count == 0)
            return false;

        var targetColumn = targetColumns
            .Select(c => (Column: c, Score: ScoreHintColumnCompatibility(hintNorm, c)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => GetKeyColumnPosition(targetColumns, x.Column))
            .Select(x => x.Column)
            .FirstOrDefault();
        if (targetColumn is null)
            return false;

        var sourceExpr = ResolveBestSourceByHint(task, dataObjects, linkMembers, linkIndex, hintNorm, currentLink.MemberName, targetColumn);
        if (string.IsNullOrWhiteSpace(sourceExpr))
            return false;

        condition = BuildLinkComparison(task, currentLink.Link, $"{currentLink.MemberName}.{ResolveDataObjectColumnMemberName(target, targetColumn)}", sourceExpr);
        return true;
    }

    private static bool HintRequestsOverride(string hintNorm)
    {
        return !string.IsNullOrWhiteSpace(hintNorm);
    }

    private static bool CanInlineStringLiteralExpression(TaskSemantic task, int expressionId, IReadOnlyList<DataObjectDef> dataObjects)
        => TryResolveExpressionAsStringLiteralCode(task, expressionId, dataObjects, out _);
}


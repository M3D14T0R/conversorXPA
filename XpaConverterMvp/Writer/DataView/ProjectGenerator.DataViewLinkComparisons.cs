using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static int ScoreLinkSourceForColumn(
        DataColumnDef targetCol,
        string expr,
        IReadOnlyList<DataColumnDef>? targetKeyCols = null,
        IReadOnlyDictionary<string, int>? parameterExprOrder = null)
    {
        var exprNorm = NormalizeKey(expr);
        var colNorm = NormalizeKey(targetCol.Name);
        var score = TokenOverlapScore(targetCol.Name, expr);
        if (!string.IsNullOrWhiteSpace(targetCol.DbColumnName))
            score += TokenOverlapScore(targetCol.DbColumnName!, expr);

        if (TokenOverlapScore(colNorm, exprNorm) > 0)
            score += 50;
        if (HasStrongTerminalCompatibility(targetCol, expr))
            score += 60;
        if (IsMemberAccessExpression(expr))
            score += 10;
        if (IsParameterLikeNormalizedExpression(exprNorm))
            score += 15;
        if (targetKeyCols is not null)
        {
            var keyPosition = GetKeyColumnPosition(targetKeyCols, targetCol);
            if (keyPosition >= 0)
            {
                score += Math.Max(0, 20 - (keyPosition * 5));
                if (parameterExprOrder is not null &&
                    parameterExprOrder.TryGetValue(expr, out var parameterOrder) &&
                    IsParameterLikeNormalizedExpression(exprNorm))
                {
                    var distance = Math.Abs(parameterOrder - keyPosition);
                    score += Math.Max(0, 40 - (distance * 12));
                }
            }
        }
        return score;
    }

    private static string ResolveFilterOperandExpression(
        TaskSemantic task,
        string leftExpr,
        int expressionId,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        var cacheKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{task.Ordinal}|{leftExpr}|{expressionId}");
        if (_filterOperandExpressionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        string expr;
        if (task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expressionId, out var entry) &&
            entry is not null &&
            TryCreateFilterComparisonContext(task, leftExpr, out var comparisonContext))
        {
            expr = ResolveTypedExpressionEntryCode(entry, task, dataObjects, comparisonContext).Code;
        }
        else if (task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expressionId, out entry) &&
            entry is not null)
        {
            // For DLL-backed/external models the left side may not have XML metadata in this run.
            // In that case, preserving the source expression avoids forcing FIELD_ALPHA casts that
            // can break numeric/date relation overloads; the relation API still validates the type.
            expr = ResolveRawExpressionEntryCode(entry, task, dataObjects, expressionId);
        }
        else
        {
            expr = ResolveExpressionCode(expressionId.ToString(), task, dataObjects);
        }

        if (string.IsNullOrWhiteSpace(expr))
        {
            _filterOperandExpressionCache[cacheKey] = expr;
            return expr;
        }

        var resolved = TrySplitCndRangeExpression(expr, out _, out _)
            ? expr
            : EmitComparisonRightExpression(task, leftExpr, expr);
        _filterOperandExpressionCache[cacheKey] = resolved;
        return resolved;
    }

    private static bool TryCreateFilterComparisonContext(
        TaskSemantic task,
        string leftExpr,
        out ExpressionEmissionContext context)
    {
        context = default;
        if (string.IsNullOrWhiteSpace(leftExpr))
            return false;

        var targetInfo = ResolveTargetValueInfo(task, null, leftExpr);
        var hasTargetTypeEvidence = HasFilterComparisonTargetTypeEvidence(targetInfo);
        if (targetInfo.IsDotNet || string.IsNullOrWhiteSpace(targetInfo.AttrObj))
            targetInfo = RecalibrateTargetInfoFromDeclaredTargetType(task, targetInfo, leftExpr);

        if (targetInfo.IsDotNet || string.IsNullOrWhiteSpace(targetInfo.AttrObj))
            return false;

        if (!hasTargetTypeEvidence &&
            string.Equals(NormalizeAttrObjKind(targetInfo.AttrObj), "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase))
            return false;

        context = CreateFilterComparisonEmissionContext(targetInfo);
        return context.Expected.HasExpectation;
    }

    private static bool HasFilterComparisonTargetTypeEvidence(TargetValueInfo targetInfo)
        => targetInfo.Resource is not null ||
           !string.IsNullOrWhiteSpace(targetInfo.ModelAttrObj) ||
           targetInfo.IsBlob ||
           targetInfo.IsArray ||
           targetInfo.IsNumeric ||
           targetInfo.IsBoolean ||
           targetInfo.IsDotNet;

    private static string BuildLinkComparison(TaskSemantic task, TaskLogicLinkDef link, string leftExpr, string rightExpr)
    {
        if (TrySplitCndRangeExpression(rightExpr, out var condExpr, out var valueExpr))
        {
            valueExpr = EmitComparisonRightExpression(task, leftExpr, valueExpr);
            var comparison = ShouldUseBindEqualTo(link, valueExpr)
                ? BuildLinkBindEqualTo(leftExpr, valueExpr)
                : BuildLinkIsEqualTo(leftExpr, valueExpr);
            return $"CndRange(() => {condExpr}, {comparison})";
        }

        rightExpr = EmitComparisonRightExpression(task, leftExpr, rightExpr);
        return ShouldUseBindEqualTo(link, rightExpr)
            ? BuildLinkBindEqualTo(leftExpr, rightExpr)
            : BuildLinkIsEqualTo(leftExpr, rightExpr);
    }

    private static bool TryBuildSingleKeyParameterFallbackLinkCondition(
        TaskSemantic task,
        (TaskLogicLinkDef Link, string MemberName) currentLink,
        DataObjectDef target,
        IReadOnlyList<DataColumnDef> targetKeyCols,
        DataColumnDef? targetKeyCol,
        string? sourceExpression,
        out string condition)
    {
        condition = "";
        if (targetKeyCol is null ||
            !targetKeyCols.Any(c => c.Id == targetKeyCol.Id) ||
            targetKeyCols.Count != 1 ||
            string.IsNullOrWhiteSpace(sourceExpression))
            return false;

        condition = BuildLinkComparison(task, currentLink.Link, $"{currentLink.MemberName}.{ResolveDataObjectColumnMemberName(target, targetKeyCol)}", sourceExpression);
        return true;
    }

    private static bool TryBuildTwoKeyFallbackLinkCondition(
        TaskSemantic task,
        IReadOnlyList<string> sourceExprs,
        (TaskLogicLinkDef Link, string MemberName) currentLink,
        DataObjectDef target,
        IReadOnlyList<DataColumnDef> targetKeyCols,
        IReadOnlyDictionary<string, int> parameterExprOrder,
        DataColumnDef? firstKeyCol,
        DataColumnDef? secondKeyCol,
        out string condition)
    {
        condition = "";
        if (firstKeyCol is null ||
            secondKeyCol is null ||
            targetKeyCols.Count < 2 ||
            !targetKeyCols.Any(c => c.Id == firstKeyCol.Id) ||
            !targetKeyCols.Any(c => c.Id == secondKeyCol.Id))
            return false;

        var firstSource = FindBestSourceExpressionForTargetColumn(sourceExprs, currentLink.MemberName, firstKeyCol, targetKeyCols, parameterExprOrder, preferParameterLike: true);
        if (string.Equals(currentLink.Link.Mode, "W", StringComparison.OrdinalIgnoreCase))
        {
            var firstModelSource = FindBestSourceExpressionForTargetColumn(sourceExprs, currentLink.MemberName, firstKeyCol, targetKeyCols, parameterExprOrder, preferMemberAccess: true);
            if (!string.IsNullOrWhiteSpace(firstModelSource))
                firstSource = firstModelSource;
        }

        var secondSource = FindBestSourceExpressionForTargetColumn(sourceExprs, currentLink.MemberName, secondKeyCol, targetKeyCols, parameterExprOrder, preferMemberAccess: true);
        if (string.IsNullOrWhiteSpace(firstSource) || string.IsNullOrWhiteSpace(secondSource))
            return false;

        var p1 = BuildLinkComparison(task, currentLink.Link, $"{currentLink.MemberName}.{ResolveDataObjectColumnMemberName(target, firstKeyCol)}", firstSource);
        var p2 = BuildLinkComparison(task, currentLink.Link, $"{currentLink.MemberName}.{ResolveDataObjectColumnMemberName(target, secondKeyCol)}", secondSource);
        condition = p1 + ".And(" + p2 + ")";
        return true;
    }

    private static bool TryBuildWriteModeTwoKeyFallbackLinkCondition(
        TaskSemantic task,
        IReadOnlyList<string> sourceExprs,
        (TaskLogicLinkDef Link, string MemberName) currentLink,
        DataObjectDef target,
        IReadOnlyList<DataColumnDef> targetKeyCols,
        IReadOnlyDictionary<string, int> parameterExprOrder,
        DataColumnDef? firstKeyCol,
        DataColumnDef? secondKeyCol,
        out string condition)
    {
        condition = "";
        if (!string.Equals(currentLink.Link.Mode, "W", StringComparison.OrdinalIgnoreCase) ||
            firstKeyCol is null ||
            secondKeyCol is null)
            return false;

        var firstModelSource = FindBestSourceExpressionForTargetColumn(sourceExprs, currentLink.MemberName, firstKeyCol, targetKeyCols, parameterExprOrder, preferMemberAccess: true);
        var secondModelSource = FindBestSourceExpressionForTargetColumn(sourceExprs, currentLink.MemberName, secondKeyCol, targetKeyCols, parameterExprOrder, preferMemberAccess: true);
        if (string.IsNullOrWhiteSpace(firstModelSource) || string.IsNullOrWhiteSpace(secondModelSource))
            return false;

        var p1 = BuildLinkComparison(task, currentLink.Link, $"{currentLink.MemberName}.{ResolveDataObjectColumnMemberName(target, firstKeyCol)}", firstModelSource);
        var p2 = BuildLinkComparison(task, currentLink.Link, $"{currentLink.MemberName}.{ResolveDataObjectColumnMemberName(target, secondKeyCol)}", secondModelSource);
        condition = p1 + ".And(" + p2 + ")";
        return true;
    }

    private static bool TryBuildInitialSingleKeyFallbackLinkCondition(
        TaskSemantic task,
        IReadOnlyList<string> sourceExprs,
        IReadOnlyList<(TaskLogicLinkDef Link, string MemberName)> linkMembers,
        int linkIndex,
        (TaskLogicLinkDef Link, string MemberName) currentLink,
        DataObjectDef target,
        int cursor,
        IReadOnlyList<DataColumnDef> targetKeyCols,
        IReadOnlyDictionary<string, int> parameterExprOrder,
        DataColumnDef? targetKeyCol,
        out string condition)
    {
        condition = "";
        if (cursor != 0 ||
            targetKeyCol is null ||
            targetKeyCols.Count != 1 ||
            !targetKeyCols.Any(c => c.Id == targetKeyCol.Id) ||
            (linkIndex != 0 && linkMembers[linkIndex - 1].Link.DbObj != currentLink.Link.DbObj))
            return false;

        var sourceExpression = FindBestSourceExpressionForTargetColumn(sourceExprs, currentLink.MemberName, targetKeyCol, targetKeyCols, parameterExprOrder, preferParameterLike: true);
        if (string.IsNullOrWhiteSpace(sourceExpression))
            return false;

        condition = BuildLinkComparison(task, currentLink.Link, $"{currentLink.MemberName}.{ResolveDataObjectColumnMemberName(target, targetKeyCol)}", sourceExpression);
        return true;
    }

    private static bool ContainsPrimaryIdParameterExpression(string expression)
    {
        var normalized = NormalizeKey(expression);
        return IsParameterLikeNormalizedExpression(normalized) && normalized.EndsWith("id", StringComparison.Ordinal);
    }

    private static int GetKeyColumnPosition(IReadOnlyList<DataColumnDef>? targetKeyCols, DataColumnDef targetCol)
    {
        if (targetKeyCols is null || targetKeyCols.Count == 0)
            return -1;

        for (var i = 0; i < targetKeyCols.Count; i++)
        {
            if (targetKeyCols[i].Id == targetCol.Id)
                return i;
        }

        return -1;
    }

    private static bool HasStrongTerminalCompatibility(DataColumnDef targetCol, string expr)
    {
        var targetNames = new[]
        {
            targetCol.Name,
            targetCol.DbColumnName ?? ""
        }
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(NormalizeKey)
        .Distinct(StringComparer.Ordinal)
        .ToList();

        var exprTerminal = NormalizeKey(GetExpressionTerminalName(expr));
        if (!string.IsNullOrWhiteSpace(exprTerminal) && targetNames.Contains(exprTerminal, StringComparer.Ordinal))
            return true;

        return targetNames.Any(name => TokenOverlapScore(name, exprTerminal) > 0);
    }

    private static string GetExpressionTerminalName(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return "";

        var trimmed = expression.Trim();
        if (TryParseFunctionCall(trimmed, out var functionName, out _))
            return functionName.Split('.').LastOrDefault() ?? functionName;

        var lastDot = trimmed.LastIndexOf('.');
        if (lastDot >= 0 && lastDot + 1 < trimmed.Length)
            return trimmed[(lastDot + 1)..];

        return trimmed;
    }

    private static int ScoreLinkHintCompatibility(string hintNorm, string colNorm, string exprNorm)
    {
        if (string.IsNullOrWhiteSpace(hintNorm))
            return 0;

        var colOverlap = TokenOverlapScore(hintNorm, colNorm);
        var exprOverlap = TokenOverlapScore(hintNorm, exprNorm);
        var score = (colOverlap * 8) + (exprOverlap * 8);

        if (colOverlap > 0 && exprOverlap > 0)
            score += 20;
        else if (colOverlap > 0 && exprOverlap == 0)
            score -= 10;

        return score;
    }

    private static int ScoreHintColumnCompatibility(string hintNorm, DataColumnDef column)
    {
        if (string.IsNullOrWhiteSpace(hintNorm))
            return 0;

        var nameScore = TokenOverlapScore(hintNorm, NormalizeKey(column.Name));
        var dbNameScore = TokenOverlapScore(hintNorm, NormalizeKey(column.DbColumnName ?? ""));
        var score = Math.Max(nameScore, dbNameScore) * 10;

        if (nameScore > 0 && dbNameScore > 0)
            score += 10;

        return score;
    }

    private static bool HintTargetsMissingKeyColumns(string hintNorm, IReadOnlyList<DataColumnDef> keyCols)
    {
        if (string.IsNullOrWhiteSpace(hintNorm) || keyCols.Count == 0)
            return false;

        var bestOverlap = keyCols.Max(c =>
            Math.Max(
                TokenOverlapScore(hintNorm, NormalizeKey(c.Name)),
                TokenOverlapScore(hintNorm, NormalizeKey(c.DbColumnName ?? ""))));

        return bestOverlap == 0;
    }

    private static string BuildLinkIsEqualTo(string leftExpr, string rightExpr)
        => $"{leftExpr}.IsEqualTo({FormatComparisonArgument(rightExpr, deferDynamicExpressions: true, allowParentDirect: true)})";

    private static string BuildLinkBindEqualTo(string leftExpr, string rightExpr)
        => $"{leftExpr}.BindEqualTo({FormatComparisonArgument(rightExpr, deferDynamicExpressions: true, allowParentDirect: true)})";

    private static string BuildFilterIsEqualTo(TaskSemantic task, string leftExpr, string rightExpr)
    {
        if (TrySplitCndRangeExpression(rightExpr, out var condExpr, out var valueExpr))
            return $"CndRange(() => {condExpr}, {BuildFilterIsEqualTo(task, leftExpr, valueExpr)})";

        rightExpr = EmitComparisonRightExpression(task, leftExpr, rightExpr);
        return $"{leftExpr}.IsEqualTo({FormatComparisonArgument(rightExpr, deferDynamicExpressions: true, allowParentDirect: true)})";
    }

    private static string BuildFilterBindEqualTo(TaskSemantic task, string leftExpr, string rightExpr)
    {
        if (TrySplitCndRangeExpression(rightExpr, out var condExpr, out var valueExpr))
            return $"CndRange(() => {condExpr}, {BuildFilterBindEqualTo(task, leftExpr, valueExpr)})";

        rightExpr = EmitComparisonRightExpression(task, leftExpr, rightExpr);
        return $"{leftExpr}.BindEqualTo({FormatComparisonArgument(rightExpr, deferDynamicExpressions: false, allowParentDirect: true)})";
    }

    private static string BuildFilterIsGreaterOrEqualTo(TaskSemantic task, string leftExpr, string rightExpr)
    {
        if (TrySplitCndRangeExpression(rightExpr, out var condExpr, out var valueExpr))
            return $"CndRange(() => {condExpr}, {BuildFilterIsGreaterOrEqualTo(task, leftExpr, valueExpr)})";

        rightExpr = EmitComparisonRightExpression(task, leftExpr, rightExpr);
        return $"{leftExpr}.IsGreaterOrEqualTo({rightExpr})";
    }

    private static string BuildFilterIsLessOrEqualTo(TaskSemantic task, string leftExpr, string rightExpr)
    {
        if (TrySplitCndRangeExpression(rightExpr, out var condExpr, out var valueExpr))
            return $"CndRange(() => {condExpr}, {BuildFilterIsLessOrEqualTo(task, leftExpr, valueExpr)})";

        rightExpr = EmitComparisonRightExpression(task, leftExpr, rightExpr);
        return $"{leftExpr}.IsLessOrEqualTo({rightExpr})";
    }

    private static string BuildFilterIsBetween(TaskSemantic task, string leftExpr, string minExpr, string maxExpr)
    {
        minExpr = EmitComparisonRightExpression(task, leftExpr, minExpr);
        maxExpr = EmitComparisonRightExpression(task, leftExpr, maxExpr);
        return $"{leftExpr}.IsBetween({minExpr}, {maxExpr})";
    }

    private static string FindBestSourceExpressionForTargetColumn(
        IReadOnlyList<string> sourceExprs,
        string currentMemberName,
        DataColumnDef? targetColumn,
        IReadOnlyList<DataColumnDef>? targetKeyCols = null,
        IReadOnlyDictionary<string, int>? parameterExprOrder = null,
        bool preferParameterLike = false,
        bool preferMemberAccess = false,
        string? hintNorm = null)
    {
        if (targetColumn is null)
            return "";

        var ranked = sourceExprs
            .Where(expr => !string.IsNullOrWhiteSpace(expr) && !expr.StartsWith(currentMemberName + ".", StringComparison.Ordinal))
            .Select(expr =>
            {
                var exprNorm = NormalizeKey(expr);
                var score = ScoreLinkSourceForColumn(targetColumn, expr, targetKeyCols, parameterExprOrder);
                score += ScoreLinkExpressionAgainstHint(hintNorm ?? "", exprNorm);
                if (preferParameterLike)
                {
                    if (IsParameterLikeNormalizedExpression(exprNorm))
                    {
                        score += 40;
                        if (parameterExprOrder is not null && parameterExprOrder.TryGetValue(expr, out var parameterOrder))
                            score += Math.Max(0, 20 - (parameterOrder * 5));
                    }
                    else if (IsMemberAccessExpression(expr))
                        score -= 10;
                }

                if (preferMemberAccess)
                {
                    if (IsMemberAccessExpression(expr))
                        score += 40;
                    else if (IsParameterLikeNormalizedExpression(exprNorm))
                        score -= 10;
                }

                return (Expr: expr, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        return ranked.FirstOrDefault().Expr ?? "";
    }

    private static string FormatComparisonArgument(string expr, bool deferDynamicExpressions, bool allowParentDirect)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return expr;

        var trimmed = expr.Trim();
        if (trimmed.StartsWith("() =>", StringComparison.Ordinal))
            return trimmed;

        if (!deferDynamicExpressions || !RequiresDeferredEvaluation(trimmed, allowParentDirect))
            return trimmed;

        return $"() => {trimmed}";
    }

    private static bool RequiresDeferredEvaluation(string expr, bool allowParentDirect)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return false;

        var trimmed = expr.Trim();
        if (allowParentDirect && trimmed.StartsWith("_parent.", StringComparison.Ordinal))
            return false;
        if (IsWholeStringLiteralExpression(trimmed))
            return false;
        if (IsSimpleNumericLiteral(trimmed))
            return false;
        if (IsSimpleConstantLiteral(trimmed))
            return false;

        if (TryGetTopLevelFunctionName(trimmed) is not null ||
            ContainsDynamicOperators(trimmed) ||
            ContainsRuntimeHelperReference(trimmed) ||
            ContainsIdentifierToken(trimmed, "Counter") ||
            ContainsIdentifierToken(trimmed, "LoopCounter"))
            return true;

        return false;
    }

    private static bool IsSimpleNumericLiteral(string value)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _);

    private static bool IsSimpleConstantLiteral(string value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "null", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsDynamicOperators(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (TryReadQuotedSegmentEnd(value, i, out var quotedEnd))
            {
                i = quotedEnd;
                continue;
            }

            var ch = value[i];
            if (ch is '(' or ')' or '?' or ':' or '<' or '>' or '+' or '*' or '/' or '%')
                return true;
            if (ch == '-')
            {
                if (i > 0)
                    return true;
                continue;
            }
            if (ch == '&' && i + 1 < value.Length && value[i + 1] == '&')
                return true;
            if (ch == '|' && i + 1 < value.Length && value[i + 1] == '|')
                return true;
            if (ch == '=' && i + 1 < value.Length && value[i + 1] == '=')
                return true;
            if (ch == '!' && i + 1 < value.Length && value[i + 1] == '=')
                return true;
        }

        return false;
    }

    private static bool ShouldUseBindEqualTo(TaskLogicLinkDef link, string rightExpr)
    {
        if (!string.Equals(link.Mode, "W", StringComparison.OrdinalIgnoreCase))
            return false;
        var n = NormalizeKey(rightExpr);
        if (ContainsPrimaryIdParameterExpression(rightExpr) || IsParameterLikeNormalizedExpression(n))
            return false;
        return true;
    }
}


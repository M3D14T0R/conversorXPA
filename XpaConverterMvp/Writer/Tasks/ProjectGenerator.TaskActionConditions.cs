using System.Globalization;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveActionConditionCode(TaskRowActionDef action, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var key = BuildRowActionCacheKey(task.Ordinal, action, "cond");
        if (_rowActionConditionCodeCache.TryGetValue(key, out var cached))
            return cached;

        var condId = action.ConditionExpressionId
            ?? action.Call?.ConditionExpressionId
            ?? action.Update?.ConditionExpressionId
            ?? action.Stop?.ConditionExpressionId
            ?? action.Invoke?.ConditionExpressionId;

        string sharedConditionToken = "";
        if (condId.HasValue &&
            task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(condId.Value, out var conditionExpression) &&
            conditionExpression is not null)
        {
            sharedConditionToken = !string.IsNullOrWhiteSpace(conditionExpression.LiteralSourceSyntax)
                ? conditionExpression.LiteralSourceSyntax
                : conditionExpression.Syntax ?? "";
        }

        if (string.IsNullOrWhiteSpace(sharedConditionToken))
            sharedConditionToken = condId?.ToString(CultureInfo.InvariantCulture) ?? "";

        var sharedKey = string.Join("|",
            "shared-cond",
            task.Ordinal.ToString(CultureInfo.InvariantCulture),
            action.ConditionLiteral?.ToString() ?? "",
            sharedConditionToken);

        if (_sharedActionConditionCodeCache.TryGetValue(sharedKey, out var sharedCached))
        {
            _rowActionConditionCodeCache[key] = sharedCached;
            return sharedCached;
        }

        string result;
        if (action.ConditionLiteral == false)
            result = "false";
        else if (action.ConditionLiteral == true &&
            !action.ConditionExpressionId.HasValue &&
            action.Call?.ConditionExpressionId is null &&
            action.Update?.ConditionExpressionId is null &&
            action.Stop?.ConditionExpressionId is null &&
            action.Invoke?.ConditionExpressionId is null)
            result = "";
        else
        {
            result = condId.HasValue ? ResolveConditionExpressionCode(condId.Value.ToString(), task, dataObjects) : "";
        }

        _sharedActionConditionCodeCache[sharedKey] = result;
        _rowActionConditionCodeCache[key] = result;
        return result;
    }

    private static string BuildRowActionCacheKey(int taskOrdinal, TaskRowActionDef action, string suffix)
    {
        return string.Join("|",
            suffix,
            taskOrdinal.ToString(CultureInfo.InvariantCulture),
            action.Kind ?? "",
            action.XmlTrace ?? "",
            action.ConditionLiteral?.ToString() ?? "",
            action.ConditionExpressionId?.ToString(CultureInfo.InvariantCulture) ?? "",
            action.LoopConditionExpressionId?.ToString(CultureInfo.InvariantCulture) ?? "",
            action.Call?.ConditionExpressionId?.ToString(CultureInfo.InvariantCulture) ?? "",
            action.Update?.ConditionExpressionId?.ToString(CultureInfo.InvariantCulture) ?? "",
            action.Stop?.ConditionExpressionId?.ToString(CultureInfo.InvariantCulture) ?? "",
            action.Invoke?.ConditionExpressionId?.ToString(CultureInfo.InvariantCulture) ?? "",
            action.Update?.Variable ?? "",
            action.Update?.WithValue ?? "",
            action.EvaluateExpressionId?.ToString(CultureInfo.InvariantCulture) ?? "",
            action.EvaluateReturnVariable ?? "",
            action.RemarkText ?? "");
    }
}


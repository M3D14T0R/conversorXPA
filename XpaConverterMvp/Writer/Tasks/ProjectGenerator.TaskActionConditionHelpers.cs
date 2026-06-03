using System;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static TaskRowActionDef StripActionCondition(TaskRowActionDef action)
    {
        var key = BuildRowActionCacheKey(0, action, "strip");
        if (_strippedActionCache.TryGetValue(key, out var cached))
            return cached;

        var stripped = action with
        {
            LoopConditionExpressionId = null,
            ConditionExpressionId = null,
            ConditionLiteral = null,
            Call = action.Call is null ? null : action.Call with { ConditionExpressionId = null },
            Update = action.Update is null ? null : action.Update with { ConditionExpressionId = null },
            Stop = action.Stop is null ? null : action.Stop with { ConditionExpressionId = null },
            Invoke = action.Invoke is null ? null : action.Invoke with { ConditionExpressionId = null }
        };
        _strippedActionCache[key] = stripped;
        return stripped;
    }

    private static bool IsComplementaryCondition(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            return false;
        var f = NormalizeConditionText(first);
        var s = NormalizeConditionText(second);
        return s == $"u.Not({f})" || f == $"u.Not({s})";
    }

    private static string NormalizeConditionText(string cond)
    {
        return Regex.Replace(cond.Trim(), @"\s+", " ");
    }

    private static string ResolveRowActionTelemetryLabel(TaskRowActionDef action)
    {
        if (!string.IsNullOrWhiteSpace(action.Kind))
            return action.Kind!;
        if (action.Update is not null)
            return "Update";
        if (action.Call is not null)
            return "Call";
        if (action.Invoke is not null)
            return "Invoke";
        if (action.Stop is not null)
            return "Stop";
        if (!string.IsNullOrWhiteSpace(action.RemarkText))
            return "Remark";
        return "Unknown";
    }
}


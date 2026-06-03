using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool TryEmitMultiFormViewSwitch(StringBuilder sb, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks)
    {
        var guiForms = t.FormEntries
            .Where(fe => string.Equals(fe.Model, "FORM_GUI0", StringComparison.OrdinalIgnoreCase) && fe.Form is not null)
            .OrderBy(fe => fe.Index)
            .ToList();
        if (guiForms.Count <= 1 || !t.DisplayExpressionId.HasValue)
            return false;
        if (!t.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(t.DisplayExpressionId.Value, out var displayExpr) || displayExpr is null)
            return false;

        var variantClasses = guiForms
            .Select(fe => ResolveMultiFormViewClassName(t, fe, allTasks))
            .ToList();
        var displayIndices = Regex.Matches(displayExpr.Syntax ?? "", "['\"](?<formId>\\d+)['\"]FORM", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(m => int.Parse(m.Groups["formId"].Value))
            .Distinct()
            .ToList();
        if (displayIndices.Count != guiForms.Count)
            displayIndices = guiForms.Select(fe => fe.Index).ToList();
        var match = Regex.Match(displayExpr.Syntax ?? "", @"^'?(?<start>\d+)'?\s*FORM\s*-\s*1\s*\+\s*(?<selector>[A-Z]+)\s*$", RegexOptions.IgnoreCase);
        string? displaySwitchExpr = null;
        if (match.Success)
        {
            var startIndex = int.Parse(match.Groups["start"].Value);
            var selectorToken = match.Groups["selector"].Value;
            var selectorExpr = ResolveExpressionOrdinalBinding(selectorToken, t, allTasks, dataObjects);
            if (!string.IsNullOrWhiteSpace(selectorExpr))
                displaySwitchExpr = $"{startIndex} - 1 + {selectorExpr}";
        }
        displaySwitchExpr ??= ResolveExpressionCode(t.DisplayExpressionId.Value.ToString(), t, dataObjects, CreateDisplayExpressionEmissionContext());
        if (string.IsNullOrWhiteSpace(displaySwitchExpr))
            return false;

        sb.AppendLine($"        switch ((int)({displaySwitchExpr}))");
        sb.AppendLine("        {");
        sb.AppendLine("            default:");
        sb.AppendLine($"                View = () => new Views.{variantClasses[0]}(this);");
        sb.AppendLine($"                SetMainDisplayIndex({displayIndices[0]});");
        sb.AppendLine("                break;");
        for (var i = 1; i < guiForms.Count; i++)
        {
            var displayIndex = displayIndices[i];
            sb.AppendLine($"            case {displayIndex}:");
            sb.AppendLine($"                View = () => new Views.{variantClasses[i]}(this);");
            sb.AppendLine($"                SetMainDisplayIndex({displayIndex});");
            sb.AppendLine("                break;");
        }
        sb.AppendLine("        }");
        return true;
    }
}


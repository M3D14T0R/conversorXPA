using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool TryEmitMultiFormViewSwitch(
        StringBuilder sb,
        TaskSemantic t,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        string indentation = "        ")
    {
        if (!t.View.ShouldGenerate)
            return false;

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
        var expressionFormIndices = ResolveDisplayExpressionFormIndices(displayExpr.Syntax);
        var displayIndices = expressionFormIndices.Count == guiForms.Count
            ? expressionFormIndices
            : guiForms.Select(fe => fe.ReferenceIndex).ToList();
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

        sb.AppendLine($"{indentation}switch ((int)({displaySwitchExpr}))");
        sb.AppendLine($"{indentation}{{");
        sb.AppendLine($"{indentation}    default:");
        sb.AppendLine($"{indentation}        View = () => new Views.{variantClasses[0]}(this);");
        sb.AppendLine($"{indentation}        SetMainDisplayIndex({displayIndices[0]});");
        sb.AppendLine($"{indentation}        break;");
        for (var i = 1; i < guiForms.Count; i++)
        {
            var displayIndex = displayIndices[i];
            sb.AppendLine($"{indentation}    case {displayIndex}:");
            sb.AppendLine($"{indentation}        View = () => new Views.{variantClasses[i]}(this);");
            sb.AppendLine($"{indentation}        SetMainDisplayIndex({displayIndex});");
            sb.AppendLine($"{indentation}        break;");
        }
        sb.AppendLine($"{indentation}}}");
        return true;
    }

    private static List<int> ResolveDisplayExpressionFormIndices(string? syntax)
    {
        var result = new List<int>();

        void CollectBranch(string branch)
        {
            var value = StripRedundantOuterParentheses(branch?.Trim() ?? "");
            if (TryParseFunctionCall(value, out var functionName, out var arguments) &&
                IsTopLevelCall(functionName, "IF") &&
                arguments.Count == 3)
            {
                CollectBranch(arguments[1]);
                CollectBranch(arguments[2]);
                return;
            }

            var match = Regex.Match(
                value,
                @"^['""]?(?<formId>\d+)['""]?(?:\s*FORM)?$",
                RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups["formId"].Value, out var formId))
                result.Add(formId);
        }

        CollectBranch(syntax ?? "");
        return result
            .Distinct()
            .OrderBy(formId => formId)
            .ToList();
    }
}


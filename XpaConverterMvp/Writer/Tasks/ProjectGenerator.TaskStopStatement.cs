using System;
using System.Collections.Generic;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitStopStatement(
        StringBuilder sb,
        TaskStopDef stop,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        string pad)
    {
        var method = stop.Mode switch
        {
            "E" => "ShowError",
            "W" => "ShowWarning",
            _ => "ShowWarning"
        };

        var textExpr = ResolveStopTextExpression(stop, task, dataObjects);
        var titleExpr = string.IsNullOrWhiteSpace(stop.TitleText) ? "null" : $"\"{Escape(stop.TitleText)}\"";
        var buttonsExpr = MapMessageButtons(stop.Buttons);
        var iconExpr = MapMessageIcon(stop.Image, stop.Mode);
        var defaultButtonExpr = MapMessageDefaultButton(stop.DefaultButton);
        var appendExpr = stop.AppendToErrorLog is false ? "false" : "true";
        var conditionExpr = ResolveExpressionCode(stop.ConditionExpressionId?.ToString(), task, dataObjects, CreateBooleanConditionEmissionContext());
        var showInStatusBar = string.Equals(stop.VisualDisplay, "S", StringComparison.OrdinalIgnoreCase);
        var isEmptyErrorToStatusBar =
            method == "ShowError" &&
            ((stop.ExpressionId.HasValue && textExpr == "\"\"") ||
             (!stop.ExpressionId.HasValue && string.IsNullOrWhiteSpace(stop.Text)));
        var isDefaultWarningShape =
            method == "ShowWarning" &&
            (string.IsNullOrWhiteSpace(stop.TitleText) || string.Equals(stop.TitleText, "Warning", StringComparison.OrdinalIgnoreCase)) &&
            buttonsExpr == "MessageBoxButtons.OK" &&
            defaultButtonExpr == "MessageBoxDefaultButton.Button1";

        if (!string.IsNullOrWhiteSpace(conditionExpr))
            sb.AppendLine($"{pad}if ({conditionExpr})");
        if (!string.IsNullOrWhiteSpace(conditionExpr))
            sb.AppendLine($"{pad}{{");

        var innerPad = string.IsNullOrWhiteSpace(conditionExpr) ? "" : "    ";
        if (isEmptyErrorToStatusBar)
            sb.AppendLine($"{pad}{innerPad}ENV.Message.ShowErrorInStatusBar(\"\");");
        else if (showInStatusBar && method == "ShowWarning")
            sb.AppendLine($"{pad}{innerPad}ENV.Message.ShowWarningInStatusBar({textExpr});");
        else if (showInStatusBar && method == "ShowError")
            sb.AppendLine($"{pad}{innerPad}ENV.Message.ShowErrorInStatusBar({textExpr});");
        else if (method == "ShowWarning" &&
                 appendExpr == "true" &&
                 isDefaultWarningShape)
            sb.AppendLine($"{pad}{innerPad}ENV.Message.ShowWarning({textExpr}, {iconExpr}, true);");
        else if (titleExpr == "null" && buttonsExpr == "MessageBoxButtons.OK" && defaultButtonExpr == "MessageBoxDefaultButton.Button1")
            sb.AppendLine($"{pad}{innerPad}ENV.Message.{method}({textExpr});");
        else if (isDefaultWarningShape)
            sb.AppendLine($"{pad}{innerPad}ENV.Message.{method}({textExpr});");
        else if (method == "ShowError")
            sb.AppendLine($"{pad}{innerPad}ENV.Message.ShowError({textExpr}, {titleExpr}, {buttonsExpr}, {iconExpr}, {defaultButtonExpr}, {appendExpr});");
        else
            sb.AppendLine($"{pad}{innerPad}ENV.Message.{method}({textExpr}, {titleExpr}, {buttonsExpr}, {iconExpr}, {defaultButtonExpr});");

        if (!string.IsNullOrWhiteSpace(conditionExpr))
            sb.AppendLine($"{pad}}}");
    }
}


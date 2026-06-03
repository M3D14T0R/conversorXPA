using System;
using System.Collections.Generic;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string MapMessageButtons(string? buttons)
    {
        return buttons switch
        {
            "O" => "MessageBoxButtons.OK",
            "OC" => "MessageBoxButtons.OKCancel",
            "YNC" => "MessageBoxButtons.YesNoCancel",
            "YN" => "MessageBoxButtons.YesNo",
            _ => "MessageBoxButtons.OK"
        };
    }

    private static string MapMessageDefaultButton(int? defaultButton)
    {
        return defaultButton switch
        {
            2 => "MessageBoxDefaultButton.Button2",
            3 => "MessageBoxDefaultButton.Button3",
            _ => "MessageBoxDefaultButton.Button1"
        };
    }

    private static string MapMessageIcon(string? image, string mode)
    {
        if (image == "E")
            return "MessageBoxIcon.Exclamation";
        if (image == "I")
            return "MessageBoxIcon.Information";
        if (image == "Q")
            return "MessageBoxIcon.Question";
        if (image == "C")
            return "MessageBoxIcon.Error";
        return mode == "E" ? "MessageBoxIcon.Error" : "MessageBoxIcon.Warning";
    }

    private static string ResolveStopTextExpression(TaskStopDef stop, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (!string.IsNullOrWhiteSpace(stop.Text))
            return $"\"{Escape(stop.Text)}\"";

        if (stop.ExpressionId.HasValue)
        {
            var code = ResolveExpressionCode(stop.ExpressionId.Value.ToString(), task, dataObjects, CreateMessageTextEmissionContext());
            if (!string.IsNullOrWhiteSpace(code))
                return code;
            return $"\"Exp {stop.ExpressionId.Value}\"";
        }

        if (!string.IsNullOrWhiteSpace(stop.TitleText))
            return $"\"{Escape(stop.TitleText)}\"";

        return "\"STP\"";
    }
}


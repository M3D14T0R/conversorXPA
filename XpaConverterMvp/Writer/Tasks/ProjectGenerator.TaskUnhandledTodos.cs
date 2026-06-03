using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitUnhandledTodos(StringBuilder sb, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var items = task.Unhandled
            .Where(item => !IsRedundantUnhandled(item, task, dataObjects))
            .ToList();
        if (items.Count == 0)
            return;

        sb.AppendLine("    #region Semantic TODOs");
        foreach (var item in items)
        {
            sb.AppendLine($"    // TODO: Unhandled {item.Source}");
            sb.AppendLine($"    // Raw: {Escape(item.Raw)}");
        }
        sb.AppendLine("    #endregion");
        sb.AppendLine();
    }

    private static bool IsRedundantUnhandled(UnhandledSemantic item, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (!string.Equals(item.Source, "Expression", StringComparison.Ordinal))
            return false;
        var sep = item.Raw.IndexOf('|');
        if (sep <= 0)
            return false;
        var left = item.Raw[..sep].Trim();
        if (!int.TryParse(left, out var exprId))
            return false;
        var code = ResolveExpressionCode(exprId.ToString(), task, dataObjects, CreateCallArgumentEmissionContext());
        return !string.IsNullOrWhiteSpace(code) &&
               !code.Contains("TODO", StringComparison.OrdinalIgnoreCase);
    }
}


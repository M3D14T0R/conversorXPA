using System;
using System.Collections.Generic;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitOnSavingRow(StringBuilder sb, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (t.Logic.SavingRowLogics.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("    protected override void OnSavingRow()");
        sb.AppendLine("    {");
        foreach (var row in t.Logic.SavingRowLogics)
        {
            if (CanEmitStructuredRowLogic(row))
            {
                EmitStructuredRowLogic(sb, row, t, dataObjects, allTasks, "        ");
                continue;
            }
            foreach (var action in row.Actions)
            {
                if (action.Kind == "Update" && action.Update is not null)
                {
                    var up = action.Update;
                    var target = ResolveUpdateTargetExpression(up.Variable, t, dataObjects, allTasks);
                    if (target.StartsWith("_parent.", StringComparison.Ordinal))
                    {
                        var targetInfo = ResolveTargetValueInfo(t, up.Variable, target);
                        var value = ResolveUpdateValueExpression(up.WithValue, t, dataObjects, CreateAssignmentEmissionContext(targetInfo, target));
                        var cond = up.ConditionExpressionId.HasValue ? ResolveExpressionCode(up.ConditionExpressionId.Value.ToString(), t, dataObjects, CreateBooleanConditionEmissionContext()) : "";
                        var parentResource = ResolveResourceByTargetPath(t, target, allTasks)
                            ?? ResolveAncestorTaskResourceForAssignment(t, up.Variable, allTasks);
                        var assignment = up.Incremental
                            ? BuildUpdateAssignment(up, target, value, t)
                            : $"{target}.Value = {value};";
                        if (!string.IsNullOrWhiteSpace(cond))
                        {
                            sb.AppendLine($"        if ({cond})");
                            sb.AppendLine($"        {{");
                            sb.AppendLine($"            {assignment}");
                            sb.AppendLine($"        }}");
                        }
                        else
                        {
                            sb.AppendLine($"        {assignment}");
                        }
                        continue;
                    }
                }
                EmitRowAction(sb, action, t, dataObjects, allTasks, "        ");
            }
        }
        sb.AppendLine("    }");
    }
}


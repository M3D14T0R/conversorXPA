using System;
using System.Collections.Generic;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitOnEnd(StringBuilder sb, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks)
    {
        var writeCallMap = BuildFormIoWriteCallMap(t);
        var endIos = t.Layout.EndTaskOutputIos;
        if (t.Logic.EndLogics.Count == 0 && t.Logic.EndRaises.Count == 0 && endIos.Count == 0 && !t.HasEndLogicUnit)
            return;

        sb.AppendLine();
        sb.AppendLine("    protected override void OnEnd()");
        sb.AppendLine("    {");
        foreach (var row in t.Logic.EndLogics)
        {
            if (CanEmitStructuredRowLogic(row))
            {
                EmitStructuredRowLogic(sb, row, t, dataObjects, allTasks, "        ");
                continue;
            }

            foreach (var action in row.Actions)
            {
                if (!EmitDirectResourceAssignment(sb, action, t, dataObjects, allTasks, "        "))
                    EmitRowAction(sb, action, t, dataObjects, allTasks, "        ");
            }
            EmitRaiseStatements(sb, row.Raises, t, dataObjects, "        ");
        }
        foreach (var io in endIos)
        {
            if (!io.FormEntryIndex.HasValue || !writeCallMap.TryGetValue(io.FormEntryIndex.Value, out var writeCall))
                continue;
            EmitFormIoWrite(sb, io, writeCall, t, dataObjects, "        ");
        }
        sb.AppendLine("    }");
    }

    private static void EmitOnUnLoad(StringBuilder sb, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (!t.ReturnValueExpressionId.HasValue || string.IsNullOrWhiteSpace(ResolveTaskReturnType(t)))
            return;

        var returnType = ResolveTaskReturnType(t)!;
        var expr = ResolveExpressionCode(t.ReturnValueExpressionId.Value.ToString(), t, dataObjects, CreateReturnValueEmissionContext(returnType));
        if (string.IsNullOrWhiteSpace(expr))
            return;

        sb.AppendLine();
        sb.AppendLine("    protected override void OnUnLoad()");
        sb.AppendLine("    {");
        sb.AppendLine($"        _taskResult = {expr};");
        sb.AppendLine("    }");
    }
}


using System;
using System.Collections.Generic;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitTaskMembers(StringBuilder sb, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<FieldModelDef> fieldModels)
    {
        EmitUnhandledTodos(sb, t, dataObjects);

        var returnType = ResolveTaskReturnType(t);
        if (!string.IsNullOrWhiteSpace(returnType))
        {
            sb.AppendLine($"    {returnType} _taskResult;");
            sb.AppendLine();
        }

        EmitTaskModelMembers(sb, t, dataObjects);

        EmitTaskResourceMembers(sb, t, fieldModels);

        EmitTaskPrintAndMergeMembers(sb, t);

        var customCommandLines = BuildTaskCustomCommandDefinitions(t);
        if (customCommandLines.Count > 0)
        {
            sb.AppendLine("    #region Custom Commands");
            foreach (var line in customCommandLines)
                sb.AppendLine("    " + line);
            sb.AppendLine("    #endregion");
            sb.AppendLine();
        }
    }
}


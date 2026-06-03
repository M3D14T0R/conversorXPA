using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitSubformMethods(
        StringBuilder sb,
        TaskSemantic currentTask,
        IReadOnlyList<SubformBindingDef> subforms,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        if (subforms.Count == 0)
            return;

        var telemetryOwner = ResolveTaskClassName(currentTask, allTasks);
        sb.AppendLine("    #region Subforms");
        foreach (var s in subforms)
        {
            var rawRunArgs = s.RunArguments.Count == 0
                ? ""
                : string.Join(", ", s.RunArguments);
            var preparedRunArgs = PrepareRunArgumentsForTarget(
                rawRunArgs,
                currentTask,
                s.TargetTask,
                allTasks,
                telemetryOwner);

            if (string.IsNullOrWhiteSpace(preparedRunArgs))
                sb.AppendLine($"    internal void {s.MethodName}() => {s.FieldName}.Run();");
            else
                sb.AppendLine($"    internal void {s.MethodName}() => {s.FieldName}.Run({preparedRunArgs});");
            sb.AppendLine();
        }
        sb.AppendLine("    #endregion");
        sb.AppendLine();
    }
}


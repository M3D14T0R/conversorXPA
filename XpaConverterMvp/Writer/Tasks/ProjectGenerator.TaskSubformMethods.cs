using System;
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
            if (s.Kind == ViewSubformBindingKind.ExternalProgram)
            {
                var compatCall = BuildExternalSubformProgramCall(s);
                var compatMethod = GetExternalProgramCompatMethodName(compatCall);
                var externalArgs = s.RunArguments.Count == 0
                    ? ""
                    : string.Join(", ", s.RunArguments);
                if (string.IsNullOrWhiteSpace(externalArgs))
                    sb.AppendLine($"    internal void {s.MethodName}() => ExternalProgramCompat.{compatMethod}();");
                else
                    sb.AppendLine($"    internal void {s.MethodName}() => ExternalProgramCompat.{compatMethod}({externalArgs});");
                sb.AppendLine();
                continue;
            }

            if (s.TargetTask is null)
                continue;

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

    private static TaskCallDef BuildExternalSubformProgramCall(SubformBindingDef subform)
        => new(
            TaskId: null,
            TargetComponentId: subform.TargetComponentId,
            TargetObjectId: subform.TargetObjectId,
            TargetComponentName: subform.TargetComponentName,
            TargetPublicName: subform.TargetPublicName,
            OperationType: "P",
            ArgumentVariables: Array.Empty<string>(),
            ArgumentDefs: Array.Empty<TaskArgumentDef>(),
            ReturnVariable: null,
            ReturnValue: null,
            ConditionExpressionId: null,
            Direction: null,
            Modifier: null,
            Page: null,
            IoDeviceIndex: null,
            FormEntryIndex: null,
            WaitForCompletion: true,
            Lock: null,
            SyncData: null,
            RetainFocus: null,
            EventType: null,
            EventInternalEventId: null,
            DestSubformName: null,
            Disabled: false,
            FunctionName: null,
            SnippetCode: null,
            CompiledCode: null,
            IsRoute: null,
            RoutePath: null,
            XmlTrace: null);
}


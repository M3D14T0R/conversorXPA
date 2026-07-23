using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static List<SubformBindingDef> BuildSubformBindings(TaskSemantic currentTask, IReadOnlyList<TaskSemantic> allTasks, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var result = new List<SubformBindingDef>();
        var controls = currentTask.Form?.Controls
            .Where(c => c.Model == "CTRL_GUI0_SUBFORM" && c.SubformTaskNumber.HasValue)
            .OrderBy(c => c.TabOrder ?? int.MaxValue)
            .ThenBy(c => c.Id)
            .ToList() ?? new List<TaskFormControlDef>();
        if (controls.Count == 0)
            return result;

        var usedFieldNames = new HashSet<string>(StringComparer.Ordinal);
        var usedMethodNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var control in controls)
        {
            var taskNumber = control.SubformTaskNumber!.Value;
            var targetTask = GetChildTasks(currentTask.Ordinal, allTasks).FirstOrDefault(t => t.SubtaskIndex == taskNumber)
                             ?? ResolveTaskByXpaId(taskNumber, allTasks);
            if (targetTask is null)
                continue;

            var baseName = ResolveTaskClassName(targetTask, allTasks);
            var fieldName = baseName + "_";
            var i = 1;
            while (!usedFieldNames.Add(fieldName))
            {
                fieldName = $"{baseName}_{i}";
                i++;
            }

            var methodName = baseName + "_SubForm";
            var j = 1;
            while (!usedMethodNames.Add(methodName))
            {
                methodName = baseName + "_SubForm" + new string('_', j);
                j++;
            }

            var runArgs = new List<string>();
            foreach (var rawArg in control.SubformArguments)
            {
                string? converted = null;
                if (rawArg.StartsWith("EXP:", StringComparison.OrdinalIgnoreCase))
                {
                    converted = ResolveExpressionCode(rawArg["EXP:".Length..], currentTask, dataObjects, CreateCallArgumentEmissionContext());
                }
                else
                {
                    converted = ResolveSelectExpressionByName(rawArg, currentTask, dataObjects) ?? rawArg;
                }
                if (!string.IsNullOrWhiteSpace(converted))
                    runArgs.Add(converted);
            }

            result.Add(new SubformBindingDef(
                Control: control,
                TargetTask: targetTask,
                FieldName: fieldName,
                MethodName: methodName,
                TargetNeedsParentCtor: TaskNeedsParentReference(targetTask, allTasks, dataObjects),
                RunArguments: runArgs
            ));
        }

        return result;
    }

    private static List<SubformBindingDef> BuildSubformBindingsFromSemantic(TaskSemantic currentTask, IReadOnlyList<TaskSemantic> allTasks, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var result = new List<SubformBindingDef>();

        foreach (var binding in currentTask.View.SubformBindings)
        {
            var control = currentTask.View.SelectedFormControls.FirstOrDefault(c => c.Id == binding.ControlId);
            if (control is null)
                continue;

            var targetTask = GetTaskByOrdinal(binding.TargetTaskOrdinal, allTasks);
            if (targetTask is null)
                continue;

            var runArgs = new List<string>();
            foreach (var rawArg in binding.RawArguments)
            {
                string? converted;
                if (rawArg.StartsWith("EXP:", StringComparison.OrdinalIgnoreCase))
                    converted = ResolveExpressionCode(rawArg["EXP:".Length..], currentTask, dataObjects, CreateCallArgumentEmissionContext());
                else
                    converted = ResolveSelectExpressionByName(rawArg, currentTask, dataObjects) ?? rawArg;

                if (!string.IsNullOrWhiteSpace(converted))
                    runArgs.Add(converted);
            }

            result.Add(new SubformBindingDef(
                Control: control,
                TargetTask: targetTask,
                FieldName: binding.FieldName,
                MethodName: binding.MethodName,
                TargetNeedsParentCtor: TaskNeedsParentReference(targetTask, allTasks, dataObjects),
                RunArguments: runArgs));
        }

        if (result.Count > 0)
            return result;

        return BuildSubformBindings(currentTask, allTasks, dataObjects);
    }
}


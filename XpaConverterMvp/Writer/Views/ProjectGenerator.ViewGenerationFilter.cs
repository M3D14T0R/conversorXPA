using System.Collections.Generic;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool ShouldGenerateView(TaskSemantic t)
    {
        // A GUI display switch is an explicit runtime dependency: TaskOnLoad emits
        // references to every FORM_GUI0 variant.  TextIO suppression applies only
        // to the ordinary single-view fallback; suppressing a multi-form task here
        // leaves those emitted view type references without matching classes.
        return t.View.ShouldGenerate &&
               (HasMultiFormViewSwitchCandidate(t) ||
                !ShouldSuppressViewForBusinessProcessTextIo(t));
    }

    private static bool HasMultiFormViewSwitchCandidate(TaskSemantic t)
        => t.DisplayExpressionId.HasValue &&
           t.FormEntries.Count(fe =>
               string.Equals(fe.Model, "FORM_GUI0", System.StringComparison.OrdinalIgnoreCase) &&
               fe.Form is not null) > 1;

    private static bool HasGeneratedControllerForView(
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> tasks,
        IReadOnlySet<int> referencedStructuralTopLevelTasks)
    {
        var root = task;
        while (root.ParentOrdinal.HasValue)
        {
            var parent = GetTaskByOrdinal(root.ParentOrdinal, tasks);
            if (parent is null)
                break;
            root = parent;
        }

        if (root.MainProgram)
            return true;
        if (!ShouldGenerateForTarget(root))
            return false;

        return !IsStructuralTask(root) ||
               referencedStructuralTopLevelTasks.Contains(root.Ordinal);
    }
}


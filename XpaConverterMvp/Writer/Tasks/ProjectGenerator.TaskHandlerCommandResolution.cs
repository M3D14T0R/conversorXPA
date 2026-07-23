namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveHandlerCommandCaption(TaskHandlerDef h, TaskSemantic task)
    {
        if (TryResolveHandlerEvent(h, task, out var evt) &&
            !string.IsNullOrWhiteSpace(evt.Description))
            return evt.Description!;
        return ResolveHandlerCommandName(h, task);
    }

    private static TaskEventDef? ResolveMatchingTaskEvent(TaskHandlerDef h, TaskSemantic task, string caption, string commandName)
    {
        if (TryResolveHandlerEvent(h, task, out var directEvent))
            return directEvent;

        var normalizedCaption = NormalizeKey(caption);
        return task.EventsSemantic.Items.FirstOrDefault(ev =>
            string.Equals(ResolveTaskCommandIdentifier(task, ev.Description, preserveCase: true), commandName, StringComparison.Ordinal) ||
            string.Equals(ResolveTaskCommandIdentifier(task, ev.Description), commandName, StringComparison.Ordinal) ||
            string.Equals(NormalizeKey(ev.Description), normalizedCaption, StringComparison.Ordinal));
    }

    private static bool TryResolveHandlerEvent(TaskHandlerDef h, TaskSemantic task, out TaskEventDef evt)
    {
        if (h.EventParent.HasValue && task.ParentOrdinal.HasValue)
        {
            var parentTask = GetTaskByOrdinal(task.ParentOrdinal.Value, _allTasks);
            if (parentTask is not null &&
                int.TryParse(h.EventPublicObject, out var parentEventObj) &&
                parentTask.EventsSemantic.ItemsByOrdinal.TryGetValue(parentEventObj, out evt!))
                return true;
        }

        if (int.TryParse(h.EventPublicObject, out var eventObj) &&
            task.EventsSemantic.ItemsByOrdinal.TryGetValue(eventObj, out evt!))
            return true;

        if (IsApplicationEventReference(h.EventParent, h.EventPublicComponentId))
        {
            var appTask = _applicationTask;
            if (appTask is not null &&
                int.TryParse(h.EventPublicObject, out var appEventObj) &&
                appTask.EventsSemantic.ItemsByOrdinal.TryGetValue(appEventObj, out evt!))
                return true;
        }

        evt = null!;
        return false;
    }

    private static bool IsExpandEvent(TaskSemantic task, TaskHandlerDef h)
    {
        var caption = ResolveHandlerCommandCaption(h, task);
        return caption.IndexOf("expand", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ResolveRecordHandlerCommand(TaskHandlerDef h, TaskSemantic task)
    {
        return IsExpandEvent(task, h)
            ? "Application.Zoom_Post_Record_Update"
            : "Application.Select_Post_Record_Update";
    }

    private static string ResolveExpressionComment(string? expressionId, TaskSemantic task)
    {
        if (int.TryParse(expressionId, out var n))
        {
            task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(n, out var expr);
            if (expr is not null && !string.IsNullOrWhiteSpace(expr.Syntax))
                return $"Exp {n}: {expr.Syntax}";
            return $"Exp {n}";
        }
        return "Expression";
    }
}


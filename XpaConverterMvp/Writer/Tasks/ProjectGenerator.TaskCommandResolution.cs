using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool TryResolveEventAncestor(
        TaskSemantic task,
        int? eventParent,
        out TaskSemantic ancestor,
        out string relativePrefix)
    {
        ancestor = null!;
        relativePrefix = "";
        if (!eventParent.HasValue ||
            eventParent.Value <= 0 ||
            IsApplicationEventReference(eventParent, null))
            return false;

        var currentTask = task;
        for (var level = 0; level < eventParent.Value; level++)
        {
            if (!currentTask.ParentOrdinal.HasValue)
                return false;

            currentTask = GetTaskByOrdinal(
                currentTask.ParentOrdinal.Value,
                _allTasks ?? Array.Empty<TaskSemantic>());
            if (currentTask is null)
                return false;

            relativePrefix += "_parent.";
        }

        ancestor = currentTask;
        return true;
    }

    private static string ResolveHandlerCommandName(TaskHandlerDef h, TaskSemantic task)
    {
        if (IsApplicationEventReference(h.EventParent, h.EventPublicComponentId) &&
            int.TryParse(h.EventPublicObject, out var appEventObj))
        {
            var allTasks = _allTasks ?? Array.Empty<TaskSemantic>();
            var appTask = _applicationTask;
            if (appTask is not null &&
                appTask.EventsSemantic.ItemsByOrdinal.TryGetValue(appEventObj, out var appEvt) &&
                !string.IsNullOrWhiteSpace(appEvt.Description))
                return ResolveTaskCommandIdentifier(appTask, appEvt.Description, preserveCase: true);
        }

        if (TryResolveEventAncestor(task, h.EventParent, out var parentTask, out var relativePrefix))
        {
            if (int.TryParse(h.EventPublicObject, out var parentEventObj) &&
                parentTask.EventsSemantic.ItemsByOrdinal.TryGetValue(parentEventObj, out var parentEvt) &&
                !string.IsNullOrWhiteSpace(parentEvt.Description))
                return relativePrefix + ResolveTaskCommandIdentifier(parentTask, parentEvt.Description, preserveCase: true);
        }

        if (TryResolveHandlerEvent(h, task, out var evt) &&
            !string.IsNullOrWhiteSpace(evt.Description))
            return ResolveTaskCommandIdentifier(task, evt.Description, preserveCase: true);

        return "Event" + (h.EventPublicObject ?? "X");
    }

    private static bool IsApplicationEventReference(int? eventParent, int? eventPublicComponentId)
        => eventParent == 32768 || (eventPublicComponentId == -1 && eventParent == 32768);

    private static string ResolveCommandNameByEventObject(string? eventPublicObject, int? eventPublicComponentId, int? eventParent, TaskSemantic task)
    {
        if (!int.TryParse(eventPublicObject, out var eventObj))
            return "";
        if (IsApplicationEventReference(eventParent, eventPublicComponentId))
        {
            var appTask = _applicationTask;
            if (appTask is not null && appTask.EventsSemantic.DescriptionByOrdinal.TryGetValue(eventObj, out var appDescription))
                return "Application." + ResolveTaskCommandIdentifier(appTask, appDescription, preserveCase: true);
        }
        if (TryResolveEventAncestor(task, eventParent, out var parentTask, out var relativePrefix))
        {
            if (parentTask.EventsSemantic.DescriptionByOrdinal.TryGetValue(eventObj, out var parentDescription))
                return relativePrefix + ResolveTaskCommandIdentifier(parentTask, parentDescription, preserveCase: true);
            return "";
        }
        if (task.EventsSemantic.DescriptionByOrdinal.TryGetValue(eventObj, out var description))
            return ResolveTaskCommandIdentifier(task, description, preserveCase: true);
        return "";
    }

    private static int? ResolveRaiseTargetParameterCount(TaskRaiseEventDef raise, TaskSemantic task)
    {
        if (!int.TryParse(raise.EventPublicObject, out var eventObj))
            return null;

        if (IsApplicationEventReference(raise.EventParent, raise.EventPublicComponentId))
        {
            var appTask = _applicationTask;
            if (appTask is not null && appTask.EventsSemantic.ItemsByOrdinal.TryGetValue(eventObj, out var appEvent))
                return appEvent.Parameters.Count;
            return null;
        }

        if (TryResolveEventAncestor(task, raise.EventParent, out var parentTask, out _))
        {
            return parentTask.EventsSemantic.ItemsByOrdinal.TryGetValue(eventObj, out var parentEvent)
                ? parentEvent.Parameters.Count
                : null;
        }

        if (task.EventsSemantic.ItemsByOrdinal.TryGetValue(eventObj, out var taskEvent))
            return taskEvent.Parameters.Count;

        return null;
    }

    private static (IReadOnlyList<string> Types, IReadOnlyList<string> Directions) ResolveRaiseTargetParameterMetadata(TaskRaiseEventDef raise, TaskSemantic task)
    {
        if (!int.TryParse(raise.EventPublicObject, out var eventObj))
            return (Array.Empty<string>(), Array.Empty<string>());

        IReadOnlyList<TaskEventParameterDef>? parameters = null;
        if (IsApplicationEventReference(raise.EventParent, raise.EventPublicComponentId))
        {
            var appTask = _applicationTask;
            if (appTask is not null && appTask.EventsSemantic.ItemsByOrdinal.TryGetValue(eventObj, out var appEvent))
                parameters = appEvent.Parameters;
        }
        else if (TryResolveEventAncestor(task, raise.EventParent, out var parentTask, out _))
        {
            if (parentTask.EventsSemantic.ItemsByOrdinal.TryGetValue(eventObj, out var parentEvent))
                parameters = parentEvent.Parameters;
        }
        else if (task.EventsSemantic.ItemsByOrdinal.TryGetValue(eventObj, out var taskEvent))
        {
            parameters = taskEvent.Parameters;
        }

        if (parameters is null || parameters.Count == 0)
            return (Array.Empty<string>(), Array.Empty<string>());

        return (
            parameters.Select(ResolveEventParameterType).ToArray(),
            parameters.Select(parameter => ResolveParameterDirection(
                ToParameterIdentifier(parameter.Name))).ToArray());
    }

    private static string ResolveEventParameterType(TaskEventParameterDef parameter)
    {
        return parameter.Attr switch
        {
            "N" => "NumberParameter",
            "D" => "DateParameter",
            "T" => "TimeParameter",
            "L" => "BoolParameter",
            "B" => "BoolParameter",
            _ => "TextParameter"
        };
    }

    private static HashSet<string> CollectReservedCommandNames(TaskSemantic task)
        => new(BuildReservedCommandNames(task), StringComparer.Ordinal);

    private static HashSet<string> BuildReservedCommandNames(TaskSemantic task)
    {
        if (_reservedTaskCommandNameCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var reserved = new HashSet<string>(StringComparer.Ordinal);
        reserved.Add(ResolveTaskClassName(task, _allTasks ?? Array.Empty<TaskSemantic>()));
        foreach (var rc in task.ResourcesSemantic.Ordered)
            reserved.Add(ResolveTaskResourceMemberName(task, rc));
        foreach (var child in GetChildTasks(task.Ordinal, _allTasks))
            reserved.Add(ResolveTaskClassName(child, _allTasks ?? Array.Empty<TaskSemantic>()));
        foreach (var fn in task.FunctionOverridesSemantic)
        {
            if (!string.IsNullOrWhiteSpace(fn.MethodName))
                reserved.Add(fn.MethodName);
        }
        _reservedTaskCommandNameCache[task.Ordinal] = reserved;
        return reserved;
    }

    private static string ResolveTaskCommandIdentifier(TaskSemantic task, string raw, bool preserveCase = false)
    {
        var id = ToCustomCommandIdentifier(raw, preserveCase);
        var reserved = BuildReservedCommandNames(task);
        if (!reserved.Contains(id))
            return id;

        var candidate = id + "Command";
        var suffix = 2;
        while (reserved.Contains(candidate))
        {
            candidate = id + "Command" + suffix;
            suffix++;
        }
        return candidate;
    }

    private static string ToCustomCommandIdentifier(string raw, bool preserveCase = false)
    {
        var id = preserveCase ? ToCodeIdentifierPreservingCase(raw) : ToPascalIdentifier(raw);
        if (string.Equals(id, "Start", StringComparison.Ordinal))
            return "Start_";
        return id;
    }
}


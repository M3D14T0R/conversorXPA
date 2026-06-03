using System;
using System.Collections.Generic;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static Dictionary<string, string> BuildTaskCommandMemberMap(TaskSemantic task)
    {
        if (_taskCommandMemberMapCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
            map[name] = name;
        }

        foreach (var evt in task.EventsSemantic.Items)
        {
            if (!string.IsNullOrWhiteSpace(evt.Description))
                Add(ResolveTaskCommandIdentifier(task, evt.Description, preserveCase: true));
        }

        foreach (var handler in task.HandlersSemantic.Items)
        {
            var isApplicationCommand = IsApplicationEventReference(handler.EventParent, handler.EventPublicComponentId);
            var isParentCommand = handler.EventParent.HasValue && task.ParentOrdinal.HasValue && !isApplicationCommand;
            if (isApplicationCommand || isParentCommand)
                continue;

            Add(ResolveHandlerCommandName(handler, task));
        }

        _taskCommandMemberMapCache[task.Ordinal] = map;
        return map;
    }



    private static string ResolveInternalHandlerCommand(TaskHandlerDef h, TaskSemantic t)
    {
        if (h.EventInternalEventId.HasValue)
        {
            var mapped = ResolveCommandByInternalEventId(h.EventInternalEventId.Value);
            if (!string.IsNullOrWhiteSpace(mapped))
                return mapped!;
        }
        if (int.TryParse(h.EventPublicObject, out var eventObj))
        {
            if (TryResolveHandlerEvent(h, t, out var evt) && evt.InternalEventId is int internalId)
            {
                var mapped = ResolveCommandByInternalEventId(internalId);
                if (!string.IsNullOrWhiteSpace(mapped))
                    return mapped!;
            }
            if (TryResolveHandlerEvent(h, t, out evt) &&
                string.Equals(evt.Description, "Program Recall", StringComparison.OrdinalIgnoreCase))
                return "ENV.Commands.SingleInstanceAsyncTaskReactivated";
        }
        return "";
    }
}


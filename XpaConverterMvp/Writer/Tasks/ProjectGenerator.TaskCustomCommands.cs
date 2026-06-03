using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static List<string> BuildTaskCustomCommandDefinitions(TaskSemantic task)
    {
        var result = new List<string>();
        var emittedNames = CollectReservedCommandNames(task);

        foreach (var h in task.HandlersSemantic.Items.Where(h => h.EventType == "U"))
        {
            if (h.EventParent.HasValue && task.ParentOrdinal.HasValue)
                continue;
            if (h.EventPublicComponentId == -1)
                continue;

            var commandName = ResolveHandlerCommandName(h, task);
            if (!emittedNames.Add(commandName))
                continue;
            var caption = ResolveHandlerCommandCaption(h, task);
            var matchingEvent = ResolveMatchingTaskEvent(h, task, caption, commandName);
            var cmdExpr = matchingEvent?.InternalEventId.HasValue == true
                ? ResolveCommandByInternalEventId(matchingEvent.InternalEventId.Value)
                : null;
            var keyExpr = matchingEvent?.EventKeyCombinationId.HasValue == true
                ? ResolveKeyCombination(matchingEvent.EventKeyCombinationId.Value)
                : "";
            if (string.IsNullOrWhiteSpace(cmdExpr) && string.IsNullOrWhiteSpace(keyExpr) && caption.Equals("Print", StringComparison.OrdinalIgnoreCase))
                result.Add($"internal readonly CustomCommand {commandName} = new CustomCommand(\"{Escape(caption)}\") {{ Precondition = CustomCommandPrecondition.LeaveRowAndSaveToDatabaseAfterHandlerInvokation }};");
            else
                result.Add($"internal readonly CustomCommand {commandName} = {BuildCustomCommandExpression(caption, cmdExpr, keyExpr, matchingEvent?.ForceExit, matchingEvent?.EventType)};");
            if (matchingEvent?.Parameters.Count > 0)
            {
                var signature = string.Join(", ", matchingEvent.Parameters.Select(BuildEventParameterSignature));
                var args = string.Join(", ", matchingEvent.Parameters.Select(p => ToParameterIdentifier(p.Name)));
                result.Add($"public CommandWithArgs {commandName}WithArgs({signature}) => new CommandWithArgs({commandName}, {args});");
            }
        }

        foreach (var ev in task.EventsSemantic.Items)
        {
            var commandName = ResolveTaskCommandIdentifier(task, ev.Description, preserveCase: true);
            if (!emittedNames.Add(commandName))
                continue;

            var cmdExpr = ev.InternalEventId.HasValue ? ResolveCommandByInternalEventId(ev.InternalEventId.Value) : null;
            if (string.IsNullOrWhiteSpace(cmdExpr) && ev.Description.Equals("myZoom", StringComparison.OrdinalIgnoreCase))
                cmdExpr = "Command.Expand";
            var keyExpr = ev.EventKeyCombinationId.HasValue ? ResolveKeyCombination(ev.EventKeyCombinationId.Value) : "";
            result.Add($"internal readonly CustomCommand {commandName} = {BuildCustomCommandExpression(ev.Description, cmdExpr, keyExpr, ev.ForceExit, ev.EventType)};");
            if (ev.Parameters.Count > 0)
            {
                var signature = string.Join(", ", ev.Parameters.Select(BuildEventParameterSignature));
                var args = string.Join(", ", ev.Parameters.Select(p => ToParameterIdentifier(p.Name)));
                result.Add($"public CommandWithArgs {commandName}WithArgs({signature}) => new CommandWithArgs({commandName}, {args});");
            }
        }

        return result;
    }

    private static string BuildCustomCommandExpression(string caption, string? commandExpression, string? keyExpression, string? forceExit, string? eventType)
    {
        var ctorArgs = new List<string> { $"\"{Escape(caption)}\"" };
        if (!string.IsNullOrWhiteSpace(commandExpression))
            ctorArgs.Add(commandExpression!);
        else if (!string.IsNullOrWhiteSpace(keyExpression))
            ctorArgs.Add(keyExpression!);

        var pre = forceExit switch
        {
            "C" => "Precondition = CustomCommandPrecondition.LeaveControl, CancelTrigger = true",
            "P" => "Precondition = CustomCommandPrecondition.LeaveRow",
            "R" => "Precondition = CustomCommandPrecondition.LeaveRowAndSaveToDatabaseAfterHandlerInvokation",
            "E" when !string.IsNullOrWhiteSpace(keyExpression) || !string.IsNullOrWhiteSpace(commandExpression) => "Precondition = CustomCommandPrecondition.SaveControlDataToColumn, CancelTrigger = true",
            "E" when string.Equals(eventType, "S", StringComparison.OrdinalIgnoreCase) => "Precondition = CustomCommandPrecondition.SaveControlDataToColumn, CancelTrigger = true",
            "E" => "Precondition = CustomCommandPrecondition.SaveControlDataToColumn",
            _ => ""
        };

        return string.IsNullOrWhiteSpace(pre)
            ? $"new CustomCommand({string.Join(", ", ctorArgs)})"
            : $"new CustomCommand({string.Join(", ", ctorArgs)}) {{ {pre} }}";
    }
}


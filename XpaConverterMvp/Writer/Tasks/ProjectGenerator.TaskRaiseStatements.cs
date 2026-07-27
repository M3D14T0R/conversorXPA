using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitRaiseStatements(
        StringBuilder sb,
        IReadOnlyList<TaskRaiseEventDef> raises,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        string pad)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raise in raises)
        {
            if (raise.Disabled)
                continue;

            if (string.Equals(raise.EventType, "U", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(raise.EventPublicObject))
            {
                var commandName = ResolveCommandNameByEventObject(raise.EventPublicObject, raise.EventPublicComponentId, raise.EventParent, task);
                if (!string.IsNullOrWhiteSpace(commandName))
                {
                    var selectMap = BuildSelectNameToExpressionMap(task, dataObjects);
                    var parameterMetadata = ResolveRaiseTargetParameterMetadata(raise, task);
                    var args = ResolveCallArgumentExpressionsPreservingPositions(
                        raise.ArgumentDefs,
                        raise.ArgumentExpressionIds,
                        task,
                        dataObjects,
                        selectMap,
                        _allTasks,
                        parameterMetadata.Types,
                        parameterMetadata.Directions).ToList();
                    var targetParameterCount = ResolveRaiseTargetParameterCount(raise, task);
                    if (targetParameterCount == 0)
                        args.Clear();
                    string statement;
                    if (IsApplicationEventReference(raise.EventParent, raise.EventPublicComponentId) &&
                        commandName.StartsWith("Application.", StringComparison.Ordinal))
                    {
                        var appCommandName = commandName["Application.".Length..];
                        if (args.Count == 0 &&
                            int.TryParse(raise.EventPublicObject, out var appEventOrdinal))
                        {
                            var appTask = _applicationTask;
                            var appEvent = appTask?.EventsSemantic.ItemsByOrdinal.TryGetValue(appEventOrdinal, out var resolvedAppEvent) == true
                                ? resolvedAppEvent
                                : null;
                            if (appEvent is not null && appEvent.Parameters.Count == 1)
                            {
                                var parameterName = appEvent.Parameters[0].Name?.Trim();
                                var matchingResource = task.ResourcesSemantic.Ordered.FirstOrDefault(r =>
                                    string.Equals(r.Name?.Trim(), parameterName, StringComparison.OrdinalIgnoreCase));
                                if (matchingResource is not null)
                                    args.Add(ResolveTaskResourceMemberName(task, matchingResource));
                            }
                        }
                        if (args.Count == 0 &&
                            string.Equals(commandName, "Application.GetMessage", StringComparison.Ordinal))
                        {
                            var messageBinding = ResolveExpressionOrdinalBinding("D", task, _allTasks ?? Array.Empty<TaskSemantic>(), dataObjects);
                            if (!string.IsNullOrWhiteSpace(messageBinding))
                                args.Add(messageBinding);
                        }
                        if (!string.IsNullOrWhiteSpace(raise.DestinationContext))
                        {
                            var destinationContext = ResolveRaiseDestinationContextExpression(raise.DestinationContext!, task, dataObjects);
                            if (string.Equals(commandName, "Application.GetMessage", StringComparison.Ordinal))
                                statement = $"{pad}RaiseOnContext({destinationContext}, Application.GetMessageWithArgs(Message));";
                            else
                            {
                            statement = args.Count == 0
                                ? $"{pad}RaiseOnContext({destinationContext}, {commandName});"
                                : $"{pad}RaiseOnContext({destinationContext}, Application.{appCommandName}WithArgs({string.Join(", ", args)}));";
                            }
                        }
                        else if (args.Count == 0)
                            statement = $"{pad}Invoke({commandName});";
                        else
                            statement = $"{pad}Invoke(Application.{appCommandName}WithArgs({string.Join(", ", args)}));";
                    }
                    else
                    {
                        statement = args.Count == 0
                            ? $"{pad}Invoke({commandName});"
                            : $"{pad}Invoke({commandName}WithArgs({string.Join(", ", args)}));";
                    }
                    statement = ApplyRaiseCondition(statement, raise, task, dataObjects, pad);
                    if (!string.IsNullOrWhiteSpace(statement) && emitted.Add(statement))
                        sb.AppendLine(statement);
                }
                continue;
            }

            if (string.Equals(raise.EventType, "I", StringComparison.OrdinalIgnoreCase) && raise.EventInternalEventId.HasValue)
            {
                var raiseCommand = ResolveCommandByInternalEventId(raise.EventInternalEventId.Value);
                if (!string.IsNullOrWhiteSpace(raiseCommand))
                {
                    // A component application is loaded inside its host application.
                    // Its XPA "Exit Application" terminates component initialization;
                    // forwarding the command through the shared runtime queue would
                    // instead close the host MDI.
                    if (task.MainProgram &&
                        string.Equals(_outputType, "Library", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(raiseCommand, "Command.ExitApplication", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var selectMap = BuildSelectNameToExpressionMap(task, dataObjects);
                    var parameterMetadata = ResolveRaiseTargetParameterMetadata(raise, task);
                    var args = ResolveCallArgumentExpressionsPreservingPositions(
                        raise.ArgumentDefs,
                        raise.ArgumentExpressionIds,
                        task,
                        dataObjects,
                        selectMap,
                        _allTasks,
                        parameterMetadata.Types,
                        parameterMetadata.Directions).ToList();
                    var statement = args.Count == 0
                        ? $"{pad}Raise({raiseCommand});"
                        : $"{pad}Raise({raiseCommand}, {string.Join(", ", args)});";
                    statement = ApplyRaiseCondition(statement, raise, task, dataObjects, pad);
                    if (!string.IsNullOrWhiteSpace(statement) && emitted.Add(statement))
                        sb.AppendLine(statement);
                }
            }
        }
    }

    private static string ApplyRaiseCondition(
        string statement,
        TaskRaiseEventDef raise,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        string pad)
    {
        if (!raise.ConditionExpressionId.HasValue)
            return statement;

        var condition = ResolveExpressionCode(
            raise.ConditionExpressionId.Value.ToString(),
            task,
            dataObjects,
            CreateBooleanConditionEmissionContext());
        if (string.IsNullOrWhiteSpace(condition))
            return statement;

        var body = statement.StartsWith(pad, StringComparison.Ordinal)
            ? statement[pad.Length..]
            : statement.TrimStart();
        return string.Join(
            Environment.NewLine,
            $"{pad}if ({condition})",
            $"{pad}{{",
            $"{pad}    {body}",
            $"{pad}}}");
    }
}


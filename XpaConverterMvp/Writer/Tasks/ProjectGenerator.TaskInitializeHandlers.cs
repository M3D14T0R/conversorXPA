using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitInitializeHandlers(StringBuilder sb, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks)
    {
        sb.AppendLine("    void InitializeHandlers()");
        sb.AppendLine("    {");
        if (t.Unhandled.Count > 0)
        {
            foreach (var gap in t.Unhandled.Where(x => x.Source is "Gap" or "Handler" or "Event"))
            {
                sb.AppendLine($"        // TODO: Unhandled {gap.Source}");
                sb.AppendLine($"        // Raw: {Escape(gap.Raw)}");
            }
        }

        if (t.HandlersSemantic.Items.Count == 0)
        {
            sb.AppendLine("    }");
            return;
        }

        for (var i = 0; i < t.HandlersSemantic.Items.Count; i++)
        {
            var h = t.HandlersSemantic.Items[i];
            if (string.Equals(h.Level, "C", StringComparison.OrdinalIgnoreCase) && h.Type is "P" or "S" or "V")
                continue;
            EmitXmlTraceComment(sb, h.XmlTrace, "        ");
            _handlerKindByReference.TryGetValue(h, out var handlerKind);
            if (handlerKind == 1)
            {
                var monitorExpr = ResolveSelectExpressionByName(h.Reference!, t, dataObjects);
                if (string.IsNullOrWhiteSpace(monitorExpr))
                {
                    sb.AppendLine($"        // Handler #{i + 1}: Level=V Type=C sem referencia resolvida. XML={h.XmlTrace ?? "?"}");
                    continue;
                }
                sb.AppendLine($"        MonitorValueChanged({monitorExpr}).Invokes += e =>");
                sb.AppendLine("        {");
                EmitHandlerBody(sb, h, t, dataObjects, allTasks, 3);
                sb.AppendLine("        };");
                continue;
            }
            if (handlerKind == 2)
            {
                var commandName = ResolveHandlerCommandName(h, t);
                var ancestorCommandTarget = ResolveAncestorCommandTarget(commandName, t, allTasks);
                var commandTarget = IsApplicationEventReference(h.EventParent, h.EventPublicComponentId)
                    ? commandName.StartsWith("_parent.", StringComparison.Ordinal)
                        ? commandName
                        : commandName.StartsWith("Application.", StringComparison.Ordinal)
                            ? commandName
                            : $"Application.{commandName}"
                    : h.EventParent.HasValue && !string.IsNullOrWhiteSpace(ancestorCommandTarget)
                        ? ancestorCommandTarget
                        : commandName;
                var handlerVar = $"h{i + 1}";
                if (!string.IsNullOrWhiteSpace(h.Reference))
                    sb.AppendLine($"        var {handlerVar} = Handlers.Add({commandTarget}, \"{Escape(h.Reference!)}\", HandlerScope.CurrentTaskOnly);");
                else
                    sb.AppendLine($"        var {handlerVar} = Handlers.Add({commandTarget}, HandlerScope.CurrentTaskOnly);");
                if (h.ConditionExpressionId.HasValue)
                {
                    var condExpr = ResolveExpressionCode(h.ConditionExpressionId.Value.ToString(), t, dataObjects, CreateBooleanConditionEmissionContext());
                    if (!string.IsNullOrWhiteSpace(condExpr))
                        sb.AppendLine($"        {handlerVar}.BindEnabled(() => {condExpr});");
                }
                foreach (var colId in h.ParameterColumnIds)
                {
                    t.ResourcesSemantic.ById.TryGetValue(colId, out var rc);
                    if (rc is null)
                        continue;
                    sb.AppendLine($"        {handlerVar}.Parameters.Add({ResolveTaskResourceMemberName(t, rc)});");
                }
                EmitHandlerProgramRegistrations(sb, handlerVar, h, t, dataObjects);
                sb.AppendLine($"        {handlerVar}.Invokes += e =>");
                sb.AppendLine("        {");
                EmitHandlerBody(sb, h, t, dataObjects, allTasks, 3);
                sb.AppendLine($"            e.Handled = {ResolveHandlerHandledExpression(h, t, dataObjects)};");
                sb.AppendLine("        };");
            }
            else if (handlerKind == 3)
            {
                var cmd = ResolveInternalHandlerCommand(h, t);
                if (string.IsNullOrWhiteSpace(cmd))
                {
                    sb.AppendLine($"        // Handler #{i + 1}: EventType=I not mapped yet. XML={h.XmlTrace ?? "?"}");
                    continue;
                }
                var handlerVar = $"h{i + 1}";
                if (!string.IsNullOrWhiteSpace(h.Reference))
                    sb.AppendLine($"        var {handlerVar} = Handlers.Add({cmd}, \"{Escape(h.Reference!)}\", HandlerScope.CurrentTaskOnly);");
                else
                    sb.AppendLine($"        var {handlerVar} = Handlers.Add({cmd}, HandlerScope.CurrentTaskOnly);");
                if (h.ConditionExpressionId.HasValue)
                {
                    var condExpr = ResolveExpressionCode(h.ConditionExpressionId.Value.ToString(), t, dataObjects, CreateBooleanConditionEmissionContext());
                    if (!string.IsNullOrWhiteSpace(condExpr))
                        sb.AppendLine($"        {handlerVar}.BindEnabled(() => {condExpr});");
                }
                foreach (var colId in h.ParameterColumnIds)
                {
                    t.ResourcesSemantic.ById.TryGetValue(colId, out var rc);
                    if (rc is null)
                        continue;
                    sb.AppendLine($"        {handlerVar}.Parameters.Add({ResolveTaskResourceMemberName(t, rc)});");
                }
                EmitHandlerProgramRegistrations(sb, handlerVar, h, t, dataObjects);
                sb.AppendLine($"        {handlerVar}.Invokes += e =>");
                sb.AppendLine("        {");
                EmitHandlerBody(sb, h, t, dataObjects, allTasks, 3);
                sb.AppendLine($"            e.Handled = {ResolveHandlerHandledExpression(h, t, dataObjects)};");
                sb.AppendLine("        };");
            }
            else if (handlerKind == 4)
            {
                var exprCode = ResolveExpressionCode(h.EventExpression, t, dataObjects, CreateBooleanConditionEmissionContext());
                var exprComment = ResolveExpressionComment(h.EventExpression, t);
                if (string.IsNullOrWhiteSpace(exprCode))
                    exprCode = $"true /* {exprComment} */";
                sb.AppendLine($"        Handlers.Add(Command.Expression(() => {exprCode})).Invokes += e =>");
                sb.AppendLine("        {");
                EmitHandlerBody(sb, h, t, dataObjects, allTasks, 3);
                sb.AppendLine($"            e.Handled = {ResolveHandlerHandledExpression(h, t, dataObjects)};");
                sb.AppendLine("        };");
            }
            else if (handlerKind == 5)
            {
                var interval = h.EventTime.GetValueOrDefault(1);
                if (interval <= 0)
                    interval = 1;
                sb.AppendLine($"        Handlers.Add(Command.CreateTimer({interval}), HandlerScope.CurrentTaskOnly).Invokes += e =>");
                sb.AppendLine("        {");
                EmitHandlerBody(sb, h, t, dataObjects, allTasks, 3);
                sb.AppendLine($"            e.Handled = {ResolveHandlerHandledExpression(h, t, dataObjects)};");
                sb.AppendLine("        };");
            }
            else if (handlerKind == 6)
            {
                EmitSystemEventHandler(sb, h, t, dataObjects);
            }
            else if (handlerKind == 7)
            {
                var handlerVar = $"h{i + 1}";
                if (string.Equals(h.EventType, "R", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine($"        var {handlerVar} = Handlers.AddDatabaseErrorHandler(XPARuntimeCore.Box.Data.DataProvider.DatabaseErrorType.AllErrors, HandlerScope.CurrentTaskOnly);");
                else
                    sb.AppendLine($"        var {handlerVar} = Handlers.Add({ResolveRecordHandlerCommand(h, t)}, HandlerScope.CurrentTaskOnly);");
                if (h.ConditionExpressionId.HasValue)
                {
                    var condExpr = ResolveExpressionCode(h.ConditionExpressionId.Value.ToString(), t, dataObjects, CreateBooleanConditionEmissionContext());
                    if (!string.IsNullOrWhiteSpace(condExpr))
                        sb.AppendLine($"        {handlerVar}.BindEnabled(() => {condExpr});");
                }
                sb.AppendLine($"        {handlerVar}.Invokes += e =>");
                sb.AppendLine("        {");
                EmitHandlerBody(sb, h, t, dataObjects, allTasks, 3);
                sb.AppendLine($"            e.Handled = {ResolveHandlerHandledExpression(h, t, dataObjects)};");
                sb.AppendLine("        };");
            }
            else
            {
                sb.AppendLine($"        // TODO: Unhandled Handler");
                sb.AppendLine($"        // Raw: {Escape($"EventType={h.EventType}; Level={h.Level}; Type={h.Type}; XML={h.XmlTrace ?? "?"}")}");
            }
        }
        sb.AppendLine("    }");
    }

    private static string ResolveAncestorCommandTarget(string commandName, TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (string.IsNullOrWhiteSpace(commandName))
            return "";

        var prefix = "_parent";
        var parentOrdinal = task.ParentOrdinal;
        while (parentOrdinal.HasValue)
        {
            var parentTask = GetTaskByOrdinal(parentOrdinal, allTasks);
            if (parentTask is null)
                break;
            if (BuildTaskCommandMemberMap(parentTask).ContainsKey(commandName))
                return $"{prefix}.{commandName}";
            parentOrdinal = parentTask.ParentOrdinal;
            prefix += "._parent";
        }

        return "";
    }
}


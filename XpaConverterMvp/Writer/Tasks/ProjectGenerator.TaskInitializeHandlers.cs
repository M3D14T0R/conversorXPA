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

        var controlSelectPrograms = ResolveControlSelectPrograms(t, dataObjects, allTasks);
        if (t.HandlersSemantic.Items.Count == 0 && controlSelectPrograms.Count == 0)
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
                sb.AppendLine($"            e.Handled = {ResolveHandlerHandledExpression(h, t, dataObjects, commandTarget)};");
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
                sb.AppendLine($"            e.Handled = {ResolveHandlerHandledExpression(h, t, dataObjects, cmd)};");
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
        EmitControlSelectProgramHandlers(sb, t, dataObjects, allTasks, controlSelectPrograms);
        sb.AppendLine("    }");
    }

    private sealed record ControlSelectProgramBinding(
        TaskFormControlDef Control,
        TaskSemantic TargetTask,
        string Reference,
        string DataExpression);

    private static IReadOnlyList<ControlSelectProgramBinding> ResolveControlSelectPrograms(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        if (task.Form?.Controls is null)
            return Array.Empty<ControlSelectProgramBinding>();

        var result = new List<ControlSelectProgramBinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in task.Form.Controls)
        {
            if (!control.SelectProgramObj.HasValue)
                continue;

            // A SelectProgram sem componente referencia um programa do projeto
            // atual. Referencias de componentes externos exigem o tipo publico
            // resolvido pelo repositorio e nao podem ser confundidas com um
            // ordinal local com o mesmo numero.
            if (control.SelectProgramComponentId.GetValueOrDefault() > 0)
                continue;

            var targetTask = allTasks.FirstOrDefault(candidate =>
                                 !candidate.ParentOrdinal.HasValue &&
                                 candidate.TopLevelProgramIndex == control.SelectProgramObj.Value);
            if (targetTask is null)
                continue;

            var reference = !string.IsNullOrWhiteSpace(control.ControlName)
                ? control.ControlName!
                : !string.IsNullOrWhiteSpace(control.DataColumn)
                    ? control.DataColumn!
                    : "";
            var dataExpression = ResolveControlDataExpression(control, task, allTasks, dataObjects);
            if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(dataExpression))
                continue;

            var duplicateKey = $"{reference}|{control.SelectProgramObj.Value}|{dataExpression}";
            if (!seen.Add(duplicateKey) || HasExplicitExpandHandlerForControl(task, control, dataObjects, allTasks))
                continue;

            result.Add(new ControlSelectProgramBinding(control, targetTask, reference, dataExpression));
        }

        return result;
    }

    private static bool HasExplicitExpandHandlerForControl(
        TaskSemantic task,
        TaskFormControlDef control,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        foreach (var handler in task.HandlersSemantic.Items)
        {
            if (!string.Equals(handler.EventType, "I", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ResolveInternalHandlerCommand(handler, task), "Command.Expand", StringComparison.Ordinal))
                continue;

            if (DoesControlMatchHandlerReference(handler, control, task, allTasks, dataObjects))
                return true;
        }

        return false;
    }

    private static void EmitControlSelectProgramHandlers(
        StringBuilder sb,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<ControlSelectProgramBinding> bindings)
    {
        for (var i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            var handlerVar = $"hControlSelect{i + 1}";
            var targetClass = ResolveTaskTypeReference(binding.TargetTask, allTasks);
            // SelectProgram has a positional contract of its own: the value of
            // the control is the first parameter of the selected program.
            // Do not pass this through the heuristic call aligner, because a
            // lookup with many optional parameters of the same XPA type can
            // incorrectly move that value to a later compatible position.
            var parameters = GetTaskParameters(binding.TargetTask);

            sb.AppendLine($"        // SelectProgram do controle #{binding.Control.Id} (XML).");
            sb.AppendLine($"        var {handlerVar} = Handlers.Add(Command.Expand, \"{Escape(binding.Reference)}\", HandlerScope.CurrentTaskOnly);");
            sb.AppendLine($"        {handlerVar}.Invokes += e =>");
            sb.AppendLine("        {");
            if (parameters.Count == 0)
            {
                sb.AppendLine($"            new {targetClass}().Run();");
            }
            else
            {
                EmitSelectProgramRun(
                    sb,
                    task,
                    binding,
                    parameters[0],
                    targetClass,
                    i + 1);
            }
            sb.AppendLine("            e.Handled = true;");
            sb.AppendLine("        };");
        }
    }

    private static void EmitSelectProgramRun(
        StringBuilder sb,
        TaskSemantic currentTask,
        ControlSelectProgramBinding binding,
        (string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection) parameter,
        string targetClass,
        int bindingIndex)
    {
        var sourceExpression = binding.DataExpression.Trim();
        var actualType = ResolveRunArgumentExpectedType(currentTask, sourceExpression);
        var expectedType = ExpectedTypeForParameterType(parameter.ParameterType);
        var typesDiffer =
            actualType.HasExpectation &&
            expectedType.HasExpectation &&
            !ExpectedTypesMatch(actualType, expectedType);
        var preservesBinding = !IsInputParameterDirection(parameter.ParameterDirection);
        var bridgeColumnType = ResolveSelectProgramBridgeColumnType(parameter.ParameterType);
        var canWriteBack = IsSimpleIdentifierPath(sourceExpression);

        if (!typesDiffer || !preservesBinding || string.IsNullOrWhiteSpace(bridgeColumnType) || !canWriteBack)
        {
            var argument = EmitCallArgumentForParameter(
                sourceExpression,
                parameter.ParameterType,
                currentTask,
                preserveBinding: preservesBinding && !typesDiffer);
            sb.AppendLine($"            new {targetClass}().Run({argument});");
            return;
        }

        // XPA VAR parameters are input/output bindings. When the selected
        // program declares a different XPA scalar type, use a typed column as
        // the bridge so the value can travel in both directions.
        var bridgeName = $"__selectProgramValue{bindingIndex}";
        var expectedReturnType = ResolveReturnTypeForExpectedContext(expectedType);
        var actualReturnType = ResolveReturnTypeForExpectedContext(actualType);
        var inputValue = EmitFromReliableTypeEvidence(
            sourceExpression,
            actualReturnType,
            expectedReturnType,
            "select-program-input");
        var outputValue = EmitFromReliableTypeEvidence(
            bridgeName,
            expectedReturnType,
            actualReturnType,
            "select-program-output");
        var writeBack = BuildReturnAssignmentExpression(
            sourceExpression,
            sourceExpression,
            outputValue,
            currentTask,
            xmlTrace: null);

        sb.AppendLine($"            var {bridgeName} = new {bridgeColumnType}();");
        sb.AppendLine($"            {bridgeName}.Value = {inputValue};");
        sb.AppendLine($"            new {targetClass}().Run({bridgeName});");
        sb.AppendLine($"            {writeBack}");
    }

    private static string ResolveSelectProgramBridgeColumnType(string parameterType)
        => NormalizeReturnTypeToken(parameterType) switch
        {
            "Text" => "TextColumn",
            "Number" => "NumberColumn",
            "Date" => "DateColumn",
            "Time" => "TimeColumn",
            "Bool" => "BoolColumn",
            "byte[]" => "ByteArrayColumn",
            _ => ""
        };

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


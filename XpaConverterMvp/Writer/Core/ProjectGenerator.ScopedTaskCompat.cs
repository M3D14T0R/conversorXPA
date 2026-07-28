using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteScopedTaskCompatAsset(
        ProjectSemantic parsed,
        IReadOnlyList<TaskSemantic> generatedTasks,
        string outputRoot,
        string appNamespace)
    {
        var generatedOrdinals = generatedTasks.Select(t => t.Ordinal).ToHashSet();
        var tasksByOrdinal = parsed.Tasks.ToDictionary(t => t.Ordinal);
        var compatTargets = ResolveScopedTaskCompatTargets(generatedTasks, generatedOrdinals, parsed.Tasks);

        var requiredOrdinals = new HashSet<int>();
        foreach (var task in compatTargets)
        {
            var current = task;
            while (true)
            {
                requiredOrdinals.Add(current.Ordinal);
                if (!current.ParentOrdinal.HasValue || !tasksByOrdinal.TryGetValue(current.ParentOrdinal.Value, out var parent))
                    break;
                if (generatedOrdinals.Contains(parent.Ordinal))
                    break;
                current = parent;
            }
        }

        var rootOrdinals = requiredOrdinals
            .Select(o => tasksByOrdinal[o])
            .Where(t => !t.ParentOrdinal.HasValue
                        || !tasksByOrdinal.ContainsKey(t.ParentOrdinal.Value)
                        || generatedOrdinals.Contains(t.ParentOrdinal.Value) == false && !requiredOrdinals.Contains(t.ParentOrdinal.Value))
            .Select(t => t.Ordinal)
            .Distinct()
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("using ENV;");
        sb.AppendLine("using ENV.Data;");
        sb.AppendLine("using XPARuntimeCore.Box;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace};");
        sb.AppendLine();
        sb.AppendLine("internal static class ScopedTaskCompat");
        sb.AppendLine("{");
        sb.AppendLine("    internal static System.Exception CreateMissingTaskException(string taskName)");
        sb.AppendLine("        => new System.NotImplementedException($\"Scoped task probe placeholder: {taskName}\");");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("internal sealed class Application : ENV.ApplicationControllerBase");
        sb.AppendLine("{");
        sb.AppendLine("    public static Application Instance { get; } = new();");
        EmitScopedTaskApplicationRunner(sb, generatedTasks, parsed.Tasks);
        var appTask = parsed.Tasks.FirstOrDefault(t => t.MainProgram) ??
                      parsed.Tasks.FirstOrDefault(t => t.ParentOrdinal is null);
        var emittedCommands = new HashSet<string>(StringComparer.Ordinal);
        if (appTask is not null)
        {
            foreach (var ev in appTask.EventsSemantic.Items)
            {
                var name = ResolveTaskCommandIdentifier(appTask, ev.Description, preserveCase: true);
                if (string.IsNullOrWhiteSpace(name) || !emittedCommands.Add(name))
                    continue;

                var commandExpression = ev.InternalEventId.HasValue
                    ? ResolveCommandByInternalEventId(ev.InternalEventId.Value)
                    : null;
                var keyExpression = ev.EventKeyCombinationId.HasValue
                    ? ResolveKeyCombination(ev.EventKeyCombinationId.Value)
                    : "";
                if (!string.IsNullOrWhiteSpace(commandExpression) ||
                    !string.IsNullOrWhiteSpace(keyExpression))
                {
                    sb.AppendLine(
                        $"    internal static readonly CustomCommand {name} = {BuildCustomCommandExpression(ev.Description, commandExpression, keyExpression, ev.ForceExit, ev.EventType)};");
                }
                else if (!string.IsNullOrWhiteSpace(ev.PublicName))
                {
                    var precondition = ev.ForceExit switch
                    {
                        "C" => "Precondition = CustomCommandPrecondition.LeaveControl, CancelTrigger = true, ",
                        "P" => "Precondition = CustomCommandPrecondition.LeaveRow, CancelTrigger = true, ",
                        "E" => "Precondition = CustomCommandPrecondition.SaveControlDataToColumn, ",
                        _ => ""
                    };
                    sb.AppendLine(
                        $"    internal static readonly CustomCommand {name} = new CustomCommand(\"{Escape(ev.Description)}\") {{ {precondition}Key = \"{Escape(ev.PublicName)}\", AllowInvokeByKey = CustomCommandAllowInvokeByKey.FromSameModuleOnly }};");
                }
                else
                {
                    sb.AppendLine(
                        $"    internal static readonly CustomCommand {name} = {BuildCustomCommandExpression(ev.Description, null, null, ev.ForceExit, ev.EventType)};");
                }

                if (ev.Parameters.Count > 0)
                {
                    var signature = string.Join(", ", ev.Parameters.Select(BuildEventParameterSignature));
                    var args = string.Join(", ", ev.Parameters.Select(p => ToParameterIdentifier(p.Name)));
                    sb.AppendLine(
                        $"    public static CommandWithArgs {name}WithArgs({signature}) => new CommandWithArgs({name}, {args});");
                }
            }
        }

        foreach (var fallbackCommand in new[]
                 {
                     "Zoom_Post_Record_Update",
                     "Select_Post_Record_Update",
                     "Zoom_Editing"
                 })
        {
            if (emittedCommands.Add(fallbackCommand))
                sb.AppendLine(
                    $"    public static readonly CustomCommand {fallbackCommand} = new CustomCommand(\"{fallbackCommand}\");");
        }
        sb.AppendLine("    public ScopedTaskCompatPrograms AllPrograms { get; } = new();");
        sb.AppendLine("    public ScopedTaskCompatEntities AllEntities { get; } = new();");
        if (appTask is not null)
        {
            var emittedMembers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var resource in appTask.ResourcesSemantic.Ordered)
            {
                var memberName = ResolveTaskResourceMemberName(appTask, resource);
                if (string.IsNullOrWhiteSpace(memberName) || !emittedMembers.Add(memberName))
                    continue;

                var columnType = ResolveScopedCompatColumnType(resource.AttrObj);
                sb.AppendLine($"    public readonly {columnType} {memberName} = new {columnType}(\"{Escape(resource.Name)}\");");
            }
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("internal sealed class ScopedTaskCompatPrograms");
        sb.AppendLine("{");
        sb.AppendLine("    public void RunByIndex(object program, params object[] args) => throw ScopedTaskCompat.CreateMissingTaskException($\"Program index {program}\");");
        sb.AppendLine("    public void RunByPublicName(object program, params object[] args) => throw ScopedTaskCompat.CreateMissingTaskException($\"Program {program}\");");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("internal sealed class ScopedTaskCompatEntities");
        sb.AppendLine("{");
        sb.AppendLine("    public int IndexOf(object entity) => 0;");
        sb.AppendLine("}");
        sb.AppendLine();

        foreach (var rootOrdinal in rootOrdinals.OrderBy(x => x))
            EmitScopedTaskCompatBlock(sb, tasksByOrdinal[rootOrdinal], parsed.Tasks, tasksByOrdinal, requiredOrdinals, 0);

        WriteGeneratedSourceFile(outputRoot, "ScopedTaskCompat", sb.ToString(), Encoding.UTF8);
    }

    private static void EmitScopedTaskApplicationRunner(
        StringBuilder sb,
        IReadOnlyList<TaskSemantic> generatedTasks,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        var programs = generatedTasks
            .Where(task => task.ParentOrdinal is null && !task.MainProgram)
            .OrderBy(task => task.TopLevelProgramIndex ?? int.MaxValue)
            .ThenBy(task => task.Ordinal)
            .ToList();

        sb.AppendLine("    public static void Run(string startProgram)");
        sb.AppendLine("    {");
        if (programs.Count == 0)
        {
            sb.AppendLine("        throw new System.InvalidOperationException(\"O recorte nao contem um programa executavel.\");");
            sb.AppendLine("    }");
            return;
        }

        var defaultProgram = ResolveTaskClassName(programs[0], allTasks);
        sb.AppendLine($"        var requested = string.IsNullOrWhiteSpace(startProgram) ? \"{Escape(defaultProgram)}\" : startProgram.Trim();");
        sb.AppendLine("        string primaryArgument = null;");
        sb.AppendLine("        var argumentSeparator = requested.IndexOf('|');");
        sb.AppendLine("        if (argumentSeparator >= 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            primaryArgument = requested.Substring(argumentSeparator + 1);");
        sb.AppendLine("            requested = requested.Substring(0, argumentSeparator).Trim();");
        sb.AppendLine("        }");
        foreach (var program in programs)
        {
            var className = ResolveTaskClassName(program, allTasks);
            var parameters = GetTaskParameters(program);
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                className
            };
            if (program.TopLevelProgramIndex is > 0)
            {
                aliases.Add(program.TopLevelProgramIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                aliases.Add($"CG{program.TopLevelProgramIndex.Value:D5}");
            }
            if (!string.IsNullOrWhiteSpace(program.PublicName))
                aliases.Add(program.PublicName.Trim());

            var condition = string.Join(
                " || ",
                aliases.Select(alias =>
                    $"string.Equals(requested, \"{Escape(alias)}\", System.StringComparison.OrdinalIgnoreCase)"));
            sb.AppendLine($"        if ({condition})");
            sb.AppendLine("        {");
            if (parameters.Count == 0)
            {
                sb.AppendLine($"            new {className}().Run();");
            }
            else
            {
                var primaryArgumentExpression = BuildScopedPrimaryArgumentExpression(
                    parameters[0].ParameterType);
                sb.AppendLine("            if (primaryArgument == null)");
                sb.AppendLine($"                new {className}().Run();");
                sb.AppendLine("            else");
                sb.AppendLine($"                new {className}().Run({primaryArgumentExpression});");
            }
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
        }

        var available = string.Join(
            ", ",
            programs.Select(program =>
                program.TopLevelProgramIndex is > 0
                    ? $"CG{program.TopLevelProgramIndex.Value:D5}"
                    : ResolveTaskClassName(program, allTasks)));
        sb.AppendLine(
            $"        throw new System.ArgumentException(\"Programa '\" + requested + \"' nao existe neste recorte. Disponiveis: {Escape(available)}\");");
        sb.AppendLine("    }");
    }

    private static string BuildScopedPrimaryArgumentExpression(string parameterType)
        => parameterType switch
        {
            "NumberParameter" => "XPARuntimeCore.Box.Number.Parse(primaryArgument)",
            "BoolParameter" => "ENV.UserMethods.Instance.CastToBool(primaryArgument)",
            "DateParameter" => "ENV.UserMethods.Instance.CastToDate(primaryArgument)",
            "TimeParameter" => "ENV.UserMethods.Instance.CastToTime(primaryArgument)",
            _ => "ENV.UserMethods.Instance.CastToText(primaryArgument)"
        };

    private static string ResolveScopedCompatColumnType(string? attrObj)
        => NormalizeAttrObjKind(attrObj) switch
        {
            "FIELD_DATE" => "DateColumn",
            "FIELD_TIME" => "TimeColumn",
            "FIELD_BOOLEAN" or "FIELD_LOGICAL" => "BoolColumn",
            "FIELD_BLOB" => "ByteArrayColumn",
            "FIELD_NUMERIC" => "NumberColumn",
            _ => "TextColumn"
        };

    private static List<TaskSemantic> ResolveScopedTaskCompatTargets(
        IReadOnlyList<TaskSemantic> generatedTasks,
        IReadOnlySet<int> generatedOrdinals,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        var result = new Dictionary<int, TaskSemantic>();
        foreach (var task in generatedTasks)
        {
            if (task.Form?.Controls is not null)
            {
                foreach (var control in task.Form.Controls)
                {
                    if (!control.SelectProgramObj.HasValue ||
                        control.SelectProgramComponentId.GetValueOrDefault() > 0)
                        continue;

                    var selectProgramTask = allTasks.FirstOrDefault(candidate =>
                        !candidate.ParentOrdinal.HasValue &&
                        candidate.TopLevelProgramIndex == control.SelectProgramObj.Value);
                    if (selectProgramTask is null ||
                        generatedOrdinals.Contains(selectProgramTask.Ordinal) ||
                        !ShouldGenerateForTarget(selectProgramTask))
                        continue;

                    result[selectProgramTask.Ordinal] = selectProgramTask;
                }
            }

            foreach (var call in EnumerateTaskCalls(task))
            {
                var targetTask = ResolveTaskByCall(task, call, allTasks);
                if (targetTask is null)
                    continue;
                if (generatedOrdinals.Contains(targetTask.Ordinal))
                    continue;
                if (!ShouldGenerateForTarget(targetTask))
                    continue;
                result[targetTask.Ordinal] = targetTask;
            }
        }

        return result.Values.OrderBy(t => t.Ordinal).ToList();
    }

    private static void EmitScopedTaskCompatBlock(
        StringBuilder sb,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyDictionary<int, TaskSemantic> tasksByOrdinal,
        IReadOnlySet<int> requiredOrdinals,
        int indentLevel)
    {
        if (!requiredOrdinals.Contains(task.Ordinal))
            return;

        var pad = new string(' ', indentLevel * 4);
        var className = ResolveTaskClassName(task, allTasks);
        var parentTask = GetTaskByOrdinal(task.ParentOrdinal, allTasks);
        var parentType = parentTask is null ? "object" : ResolveTaskTypeReference(parentTask, allTasks);

        sb.AppendLine($"{pad}internal class {className}");
        sb.AppendLine($"{pad}{{");
        sb.AppendLine($"{pad}    public {className}() {{ }}");
        if (parentTask is not null)
            sb.AppendLine($"{pad}    public {className}({parentType} parent) {{ }}");
        EmitScopedTaskCompatRunMethod(sb, task, allTasks, indentLevel + 1);

        var childTasks = GetChildTasks(task.Ordinal, allTasks)
            .Where(child => requiredOrdinals.Contains(child.Ordinal))
            .ToList();
        foreach (var child in childTasks)
        {
            sb.AppendLine();
            EmitScopedTaskCompatBlock(sb, child, allTasks, tasksByOrdinal, requiredOrdinals, indentLevel + 1);
        }

        sb.AppendLine($"{pad}}}");
    }

    private static void EmitScopedTaskCompatRunMethod(
        StringBuilder sb,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        int indentLevel)
    {
        var pad = new string(' ', indentLevel * 4);
        var parameters = GetTaskParameters(task);
        var returnType = ResolveTaskReturnType(task);
        var hasReturn = !string.IsNullOrWhiteSpace(returnType);
        var signatureParts = BuildRunParameterSignatureParts(parameters);
        var signature = signatureParts.Length == 0 ? "" : string.Join(", ", signatureParts);
        sb.AppendLine(hasReturn
            ? $"{pad}public {returnType} Run({signature}) => throw ScopedTaskCompat.CreateMissingTaskException(\"{Escape(task.Description)}\");"
            : $"{pad}public void Run({signature}) => throw ScopedTaskCompat.CreateMissingTaskException(\"{Escape(task.Description)}\");");
    }
}

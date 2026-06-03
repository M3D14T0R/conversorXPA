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
        sb.AppendLine("internal sealed class Application");
        sb.AppendLine("{");
        sb.AppendLine("    public static Application Instance { get; } = new();");
        sb.AppendLine("    public ScopedTaskCompatPrograms AllPrograms { get; } = new();");
        sb.AppendLine("    public ScopedTaskCompatEntities AllEntities { get; } = new();");
        var appTask = parsed.Tasks.FirstOrDefault(t => t.MainProgram) ?? parsed.Tasks.FirstOrDefault(t => t.ParentOrdinal is null);
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

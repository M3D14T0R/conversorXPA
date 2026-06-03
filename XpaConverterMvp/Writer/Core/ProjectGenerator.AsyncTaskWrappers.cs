using System.IO;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteAsyncTaskWrappers(IReadOnlyList<TaskSemantic> tasks, string outputRoot, string appNamespace)
    {
        var targets = tasks
            .Where(t => t.ParentOrdinal is null && t.ParallelExecution)
            .OrderBy(t => t.Ordinal)
            .ToList();

        foreach (var task in targets)
        {
            var taskClass = ResolveTaskClassName(task, tasks);
            var asyncClass = taskClass + "Async";
            var parameters = GetTaskParameters(task);
            var signatureParts = BuildRunParameterSignatureParts(parameters);
            var runArgs = parameters.Count == 0 ? "" : string.Join(", ", parameters.Select(p => p.ParameterName));

            var sb = new StringBuilder();
            sb.AppendLine("using ENV;");
            sb.AppendLine();
            sb.AppendLine($"namespace {appNamespace};");
            sb.AppendLine();
            sb.AppendLine($"/// <summary>{task.Description}</summary>");
            sb.AppendLine($"public class {asyncClass} : AsyncHelperBase");
            sb.AppendLine("{");
            sb.AppendLine($"    public {asyncClass}()");
            sb.AppendLine("    {");
            if (task.SingleInstance)
                sb.AppendLine("        SingleInstance = true;");
            if (parameters.Count > 0 || task.CopyGlobalParameters)
                sb.AppendLine("        CopyParametersInMemory = true;");
            else
                sb.AppendLine("        DisableApplicationStart = true;");
            sb.AppendLine("    }");
            sb.AppendLine($"    public string Run({string.Join(", ", signatureParts)})");
            sb.AppendLine("    {");
            if (parameters.Count == 0)
                sb.AppendLine($"        return RunAsync<{taskClass}>(c => c.Run());");
            else
                sb.AppendLine($"        return RunAsync<{taskClass}>(c => c.Run({runArgs}));");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            var taskFolder = ResolveTaskOutputFolder(task.Folder);
            var targetDir = string.IsNullOrWhiteSpace(taskFolder) ? outputRoot : Path.Combine(outputRoot, taskFolder);
            Directory.CreateDirectory(targetDir);
            WriteGeneratedSourceFile(targetDir, asyncClass, sb.ToString());
        }
    }
}


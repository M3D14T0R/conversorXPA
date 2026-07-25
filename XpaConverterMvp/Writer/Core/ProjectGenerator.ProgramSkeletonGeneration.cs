using System.Diagnostics;
using System.IO;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteProgramSkeleton(
        TaskSemantic t,
        string programsDir,
        string appNamespace,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<FieldModelDef> fieldModels,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        var className = ResolveTaskClassName(t, allTasks);
        var taskStopwatch = Stopwatch.StartNew();
        LogProgress($"Task start: {className} [{t.Description}]");

        var sb = new StringBuilder();
        sb.AppendLine("using ENV;");
        sb.AppendLine("using ENV.Data;");
        if (HasMergeLayoutInTree(t, allTasks))
            sb.AppendLine("using ENV.IO;");
        sb.AppendLine("using XPARuntimeCore.Box;");
        sb.AppendLine("using XPARuntimeCore.Box.Advanced;");
        if (RequiresFlowUsing(t))
            sb.AppendLine("using XPARuntimeCore.Box.Flow;");
        sb.AppendLine("using System.Windows.Forms;");
        sb.AppendLine("using Message = ENV.Message;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace};");
        sb.AppendLine();
        sb.Append(BuildTaskClassBlock(t, dataObjects, fieldModels, allTasks));
        var generatedCode = sb.ToString();

        var taskFolder = ResolveTaskOutputFolder(t.Folder);
        var targetDir = string.IsNullOrWhiteSpace(taskFolder) ? programsDir : Path.Combine(programsDir, taskFolder);
        Directory.CreateDirectory(targetDir);
        WriteGeneratedSourceFile(targetDir, className, generatedCode);
        WriteTaskSnippetFiles(t, allTasks, targetDir);
        LogProgress($"Task done: {className}");
        ConversionTelemetry.LogDuration("TASK", className, taskStopwatch.Elapsed, $"description={QuoteTelemetry(t.Description)}");
    }
}


using System.Linq;
using System.IO;
using System.Threading.Tasks;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitGeneratedTaskOutputs(
        ProjectSemantic parsed,
        ProjectGenerationScope scope,
        IReadOnlyList<TaskSemantic> generatedTasks,
        ProjectOutputLayout outputLayout,
        string appNamespace,
        bool parallelTaskGeneration,
        bool incrementalOutput)
    {
        if (!string.IsNullOrWhiteSpace(scope.NormalizedFolderFilter) || scope.TaskScopedGeneration)
            WriteSupportingTypesAndModelsForScopedGeneration(parsed, generatedTasks, outputLayout.TypesDir, outputLayout.ModelsDir, appNamespace);

        if (!string.IsNullOrWhiteSpace(scope.NormalizedFolderFilter) || scope.TaskScopedGeneration)
        {
            var generatedTaskOrdinals = generatedTasks.Select(t => t.Ordinal).ToHashSet();
            LogProgress($"Stage: scoped task skeletons -> {appNamespace} ({generatedTasks.Count} tasks)");
            var skeletonTasks = generatedTasks.Where(t =>
                    !t.MainProgram &&
                    (!scope.TaskScopedGeneration || scope.ExplicitlyRequestedTaskOrdinals?.Contains(t.Ordinal) != true || !IsStructuralTask(t)) &&
                    ((scope.TaskScopedGeneration && scope.ExplicitlyRequestedTaskOrdinals?.Contains(t.Ordinal) == true)
                     || !t.ParentOrdinal.HasValue
                     || !generatedTaskOrdinals.Contains(t.ParentOrdinal.Value)))
                .ToList();

            if (parallelTaskGeneration && skeletonTasks.Count > 1)
            {
                var scheduledTasks = ScheduleParallelSkeletonTasks(skeletonTasks, generatedTasks);
                var effectiveDegree = ResolveParallelSkeletonTaskDegree(scheduledTasks, Environment.ProcessorCount);
                LogProgress($"Stage: scoped task skeletons parallel -> enabled degree={effectiveDegree} requested={Environment.ProcessorCount} scheduled=largest-first");
                LogProgress($"Stage: scoped task skeletons parallel schedule head -> {FormatParallelScheduleHead(scheduledTasks)}");
                RunParallelSkeletonTaskQueue(
                    scheduledTasks,
                    Environment.ProcessorCount,
                    task => WriteProgramSkeleton(task, outputLayout.ProgramsDir, appNamespace, parsed.DataObjects, parsed.FieldModels, parsed.Tasks));
            }
            else
            {
                foreach (var task in skeletonTasks)
                    WriteProgramSkeleton(task, outputLayout.ProgramsDir, appNamespace, parsed.DataObjects, parsed.FieldModels, parsed.Tasks);
            }

            if (scope.TaskScopedGeneration)
            {
                var existingApplicationPath = Path.Combine(outputLayout.OutputRoot, "Application.cs");
                if (incrementalOutput && File.Exists(existingApplicationPath))
                {
                    LogProgress($"Stage: scoped task compat skipped for incremental task scope with existing application -> {appNamespace}");
                }
                else
                {
                    LogProgress($"Stage: scoped task compat -> {appNamespace}");
                    WriteScopedTaskCompatAsset(parsed, generatedTasks, outputLayout.OutputRoot, appNamespace);
                }
            }
        }

        LogProgress($"Stage: async wrappers -> {appNamespace}");
        WriteAsyncTaskWrappers(generatedTasks, outputLayout.OutputRoot, appNamespace);
        var mainTaskForTarget = generatedTasks.FirstOrDefault(t => t.MainProgram) ?? generatedTasks.FirstOrDefault();
        LogProgress($"Stage: views -> {appNamespace}");
        WriteViews(parsed, generatedTasks, parsed.DataObjects, parsed.ControlButtonModels, outputLayout.ViewsDir, appNamespace, mainTaskForTarget, includeApplicationView: !scope.TaskScopedGeneration);
        if (scope.TaskScopedGeneration)
        {
            LogProgress($"Stage: mdi/menu skipped for task scope -> {appNamespace}");
        }
        else
        {
            LogProgress($"Stage: mdi/menu -> {appNamespace}");
            WriteMdiAndMenus(parsed, outputLayout.ViewsDir, appNamespace);
        }
        LogProgress($"Stage: printing layouts -> {appNamespace}");
        WritePrintingLayouts(generatedTasks, parsed.DataObjects, outputLayout.OutputRoot, appNamespace);
        LogProgress($"Stage: textio layouts -> {appNamespace}");
        WriteTextIoLayouts(generatedTasks, parsed.DataObjects, outputLayout.OutputRoot, appNamespace);
        if (string.IsNullOrWhiteSpace(scope.NormalizedFolderFilter) && !scope.TaskScopedGeneration)
        {
            LogProgress($"Stage: function mapping report -> {appNamespace}");
            WriteFunctionMappingReport(parsed, outputLayout.OutputRoot, appNamespace);
        }
    }
}

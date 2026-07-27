using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void PrepareProjectOutputs(
        ProjectSemantic parsed,
        ProjectSemantic sharedAssetsSource,
        ProjectGenerationScope scope,
        ProjectOutputLayout outputLayout,
        string sourceRoot,
        string appNamespace,
        bool parallelTaskGeneration,
        bool incrementalOutput)
    {
        if (!string.IsNullOrWhiteSpace(scope.NormalizedFolderFilter))
            return;

        Directory.CreateDirectory(outputLayout.TypesDir);
        Directory.CreateDirectory(outputLayout.ModelsDir);
        Directory.CreateDirectory(outputLayout.ViewsDir);
        Directory.CreateDirectory(outputLayout.SharedDir);
        Directory.CreateDirectory(outputLayout.PropertiesDir);
        Directory.CreateDirectory(outputLayout.ProgramsDir);

        // A task-scoped incremental conversion is built from a reduced XML. Its
        // semantic graph intentionally contains only the selected tasks, so it
        // must not replace project-wide assets (for example Printers.cs) that
        // were generated from the complete project.
        if (incrementalOutput && scope.TaskScopedGeneration)
        {
            LogProgress($"Stage: shared assets skipped for incremental task scope -> {appNamespace}");
            // Program.cs depends only on the application namespace and the
            // converter's runtime bootstrap contract. Refreshing it is safe
            // even when the reduced XML does not contain the full project.
            WriteProgramEntry(outputLayout.OutputRoot, appNamespace);
            LogProgress($"Stage: program entry refreshed for incremental task scope -> {appNamespace}");
            return;
        }

        LogProgress($"Stage: shared assets -> {appNamespace}");
        WriteSharedThemeAssets(sourceRoot, outputLayout.SharedDir, appNamespace, parsed.DataObjects, parsed.Tasks);
        if (UsesSharedPrintingAssets(parsed))
            WriteSharedPrintingAssets(outputLayout.SharedDir, appNamespace, parsed.Tasks);
        if (UsesJsonCompat(parsed))
            WriteJsonCompatAsset(outputLayout.OutputRoot, appNamespace);
        if (UsesEnterpriseServerCompat(parsed))
            WriteEnterpriseServerCompatAsset(outputLayout.OutputRoot, appNamespace);
        if (UsesJavaCompat(parsed))
            WriteJavaCompatAsset(outputLayout.OutputRoot, appNamespace);
        if (UsesXmlCompat(parsed))
            WriteXmlCompatAsset(outputLayout.OutputRoot, appNamespace);
        if (UsesHandlingGuiCompat(parsed))
            WriteHandlingGuiCompatAsset(outputLayout.OutputRoot, appNamespace);
        if (UsesViewCompat(parsed))
            WriteViewCompatAsset(outputLayout.OutputRoot, appNamespace);
        if (UsesExternalTypeCompat(parsed))
            WriteExternalTypeCompatAsset(outputLayout.OutputRoot, appNamespace);
        WriteDotNetByRefInteropAsset(outputLayout.OutputRoot, appNamespace, parsed);
        if (!scope.TaskScopedGeneration && UsesPublicComponentFunctions(parsed))
            WriteComponentFunctionsAsset(outputLayout.OutputRoot, appNamespace, parsed);
        if (UsesComponentFunctionCompat(sharedAssetsSource))
            WriteComponentFunctionCompatAsset(outputLayout.OutputRoot, appNamespace, sharedAssetsSource);
        if (UsesExternalProgramCompat(sharedAssetsSource))
            WriteExternalProgramCompatAsset(outputLayout.OutputRoot, appNamespace, sharedAssetsSource);
        WriteProjectPropertiesAssets(outputLayout.PropertiesDir, appNamespace);
        if (scope.TaskScopedGeneration)
            WriteRolesSkeleton(parsed, outputLayout.OutputRoot, appNamespace);

        if (!scope.TaskScopedGeneration)
        {
            LogProgress($"Stage: field models -> {appNamespace}");
            foreach (var model in parsed.FieldModels.Where(ShouldGenerateForTarget))
                WriteType(model, outputLayout.TypesDir, appNamespace, parsed.Tasks);

            LogProgress($"Stage: data models -> {appNamespace}");
            foreach (var dataObject in parsed.DataObjects.Where(ShouldGenerateForTarget))
                WriteModel(dataObject, outputLayout.ModelsDir, appNamespace, parsed.FieldModels);
        }

        if (!scope.TaskScopedGeneration)
        {
            LogProgress($"Stage: task skeletons -> {appNamespace}");
            var referencedStructuralTasks = ResolveReferencedStructuralTopLevelTasks(parsed.Tasks);
            var skeletonTasks = parsed.Tasks
                .Where(t =>
                    t.ParentOrdinal is null &&
                    !t.MainProgram &&
                    (!IsStructuralTask(t) || referencedStructuralTasks.Contains(t.Ordinal)) &&
                    ShouldGenerateForTarget(t))
                .ToList();
            if (parallelTaskGeneration && skeletonTasks.Count > 1)
            {
                var scheduledTasks = ScheduleParallelSkeletonTasks(skeletonTasks, parsed.Tasks);
                LogProgress($"Stage: task skeletons parallel -> enabled {FormatParallelDegreePlan(scheduledTasks, Environment.ProcessorCount)}");
                LogProgress($"Stage: task skeletons parallel schedule head -> {FormatParallelScheduleHead(scheduledTasks)}");
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

            LogProgress($"Stage: application skeleton -> {appNamespace}");
            WriteApplicationRegistries(parsed, outputLayout.OutputRoot, appNamespace);
            WriteApplicationSkeleton(parsed, outputLayout.OutputRoot, appNamespace);
            WriteAsyncHelperBaseSkeleton(outputLayout.OutputRoot, appNamespace);
            WriteBusinessProcessBaseSkeleton(outputLayout.OutputRoot, appNamespace);
            WriteRolesSkeleton(parsed, outputLayout.OutputRoot, appNamespace);
            WriteProgramEntry(outputLayout.OutputRoot, appNamespace);
        }
    }
}

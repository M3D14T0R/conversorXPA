using System.IO;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteProjectMetadataOutputs(
        ProjectSemantic parsed,
        ProjectGenerationScope scope,
        string outputRoot,
        string appNamespace,
        bool incrementalOutput)
    {
        if (string.IsNullOrWhiteSpace(scope.NormalizedFolderFilter))
        {
            var projectName = appNamespace.Split('.').FirstOrDefault() ?? "Generated";
            var projectPath = Path.Combine(outputRoot, projectName + ".csproj");
            if (incrementalOutput && scope.TaskScopedGeneration && File.Exists(projectPath))
            {
                LogProgress($"Stage: project file skipped for incremental task scope -> {appNamespace}");
            }
            else
            {
                LogProgress($"Stage: project file -> {appNamespace}");
                WriteProjectFile(parsed, outputRoot, appNamespace, includeFullManifest: !scope.TaskScopedGeneration);
            }
        }

        if (string.IsNullOrWhiteSpace(scope.NormalizedFolderFilter) && !scope.TaskScopedGeneration)
            WriteDotNetComponentReferenceReport(parsed, outputRoot);

        if (!scope.TaskScopedGeneration && string.IsNullOrWhiteSpace(_targetComponent))
            WriteExternalMagicComponentPlaceholders(parsed, outputRoot);
    }
}

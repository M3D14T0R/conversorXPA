using System;
using System.Collections.Generic;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    public static void Generate(
        ProjectSemantic parsed,
        string outputRoot,
        string appNamespace,
        string sourceRoot,
        ProjectSemantic? sharedAssetsSemantic = null,
        string? solutionRoot = null,
        string? targetComponent = null,
        Dictionary<string, string>? componentNamespaces = null,
        IReadOnlyDictionary<string, string>? projectReferenceMap = null,
        IReadOnlyDictionary<string, string>? dllReferenceMap = null,
        string outputType = "WinExe",
        string envReferenceMode = "Project",
        string? envDllPath = null,
        string? folderFilter = null,
        IReadOnlyList<string>? taskFilters = null,
        bool withTaskDependencies = false)
        => Generate(new ProjectGenerationRequest
        {
            Parsed = parsed,
            OutputRoot = outputRoot,
            AppNamespace = appNamespace,
            SourceRoot = sourceRoot,
            SharedAssetsSemantic = sharedAssetsSemantic,
            SolutionRoot = solutionRoot,
            TargetComponent = targetComponent,
            ComponentNamespaces = componentNamespaces,
            ProjectReferenceMap = projectReferenceMap,
            DllReferenceMap = dllReferenceMap,
            OutputType = outputType,
            EnvReferenceMode = envReferenceMode,
            EnvDllPath = envDllPath,
            FolderFilter = folderFilter,
            TaskFilters = taskFilters,
            ForceTaskScopedGeneration = false,
            WithTaskDependencies = withTaskDependencies,
            IncrementalOutput = false,
            ParallelTaskGeneration = false
        });

    public static void Generate(ProjectGenerationRequest request)
    {
        InitializeGenerationState(request);
        var sharedAssetsSource = request.SharedAssetsSemantic ?? request.Parsed;
        var scope = ResolveGenerationScope(request.Parsed, request.FolderFilter, request.TaskFilters, request.WithTaskDependencies, request.ForceTaskScopedGeneration);
        var outputLayout = new ProjectOutputLayout(request.OutputRoot);

        PrepareProjectOutputs(request.Parsed, sharedAssetsSource, scope, outputLayout, request.SourceRoot, request.AppNamespace, request.ParallelTaskGeneration);
        WriteProjectMetadataOutputs(request.Parsed, scope, request.OutputRoot, request.AppNamespace, request.IncrementalOutput);

        var generatedTasks = ResolveGeneratedTasks(request.Parsed, scope);
        if (generatedTasks.Count == 0)
            return;

        EmitGeneratedTaskOutputs(request.Parsed, scope, generatedTasks, outputLayout, request.AppNamespace, request.ParallelTaskGeneration, request.IncrementalOutput);

        LogTypedExpressionTelemetrySummary();
        LogExpressionEmissionAuditSummary();
        LogLegacyExpressionTelemetrySummary();
        LogXpaFunctionContractTelemetrySummary();
        LogProgress($"Stage: generate complete -> {request.AppNamespace}");
    }
}

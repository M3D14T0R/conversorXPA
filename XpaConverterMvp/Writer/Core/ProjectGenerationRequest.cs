using System.Collections.Generic;

namespace XpaConverterMvp;

internal sealed class ProjectGenerationRequest
{
    public required ProjectSemantic Parsed { get; init; }
    public required string OutputRoot { get; init; }
    public required string AppNamespace { get; init; }
    public required string SourceRoot { get; init; }
    public ProjectSemantic? SharedAssetsSemantic { get; init; }
    public string? SolutionRoot { get; init; }
    public string? TargetComponent { get; init; }
    public Dictionary<string, string>? ComponentNamespaces { get; init; }
    public IReadOnlyDictionary<string, string>? ProjectReferenceMap { get; init; }
    public IReadOnlyDictionary<string, string>? DllReferenceMap { get; init; }
    public string OutputType { get; init; } = "WinExe";
    public string EnvReferenceMode { get; init; } = "Project";
    public string? EnvDllPath { get; init; }
    public string? FolderFilter { get; init; }
    public IReadOnlyList<string>? TaskFilters { get; init; }
    public bool ForceTaskScopedGeneration { get; init; }
    public bool WithTaskDependencies { get; init; }
    public bool IncrementalOutput { get; init; }
    public bool IsolatedTaskProject { get; init; }
    public bool ParallelTaskGeneration { get; init; }
}

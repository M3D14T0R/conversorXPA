using System.Collections.Generic;

namespace XpaConverterMvp;

internal sealed class ProjectGenerationScope
{
    public string NormalizedFolderFilter { get; init; } = "";
    public IReadOnlyList<string> NormalizedTaskFilters { get; init; } = [];
    public bool TaskScopedGeneration { get; init; }
    public bool WithTaskDependencies { get; init; }
    public HashSet<int>? ExplicitlyRequestedTaskOrdinals { get; init; }
    public HashSet<int>? SelectedTaskOrdinals { get; init; }
}

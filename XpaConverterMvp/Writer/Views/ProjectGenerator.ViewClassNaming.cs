using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveViewClassName(TaskSemantic t, IReadOnlyList<TaskSemantic> allTasks)
    {
        var className = t.View.ClassName;
        if (string.IsNullOrWhiteSpace(className))
            return className;

        var collisionCount = _viewClassNameCounts.TryGetValue(className, out var count)
            ? count
            : 1;
        if (collisionCount <= 1)
            return className;

        return $"{className}_T{t.Ordinal}";
    }
}

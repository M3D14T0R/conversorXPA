using System.Collections.Generic;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveViewClassName(TaskSemantic t, IReadOnlyList<TaskSemantic> allTasks)
    {
        return t.View.ClassName;
    }
}

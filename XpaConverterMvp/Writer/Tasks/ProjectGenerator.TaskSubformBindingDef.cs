using System.Collections.Generic;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private sealed record SubformBindingDef(
        TaskFormControlDef Control,
        TaskSemantic TargetTask,
        string FieldName,
        string MethodName,
        bool TargetNeedsParentCtor,
        IReadOnlyList<string> RunArguments
    );
}

using System.Collections.Generic;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string AdjustBindValueExpressionForTarget(TaskLogicSelectDef sel, string targetExpr, string bindExpr, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
        => AdjustBindValueExpressionForTargetCentral(sel, targetExpr, bindExpr, task, dataObjects);
}

using System;
using System.Collections.Generic;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitOrderedActions(
        StringBuilder sb,
        IReadOnlyList<TaskRowActionDef> orderedActions,
        IReadOnlyList<HandlerActionBlockSemantic>? blocks,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        string pad)
    {
        if (orderedActions.Count == 0)
            return;

        if (blocks is not null && blocks.Count > 0)
        {
            foreach (var block in blocks)
            {
                if (block.Kind == "Loop" && block.Length > 0)
                {
                    var loopAction = orderedActions[block.StartIndex];
                    if (loopAction.LoopConditionExpressionId.HasValue)
                    {
                        var loopCond = ResolveExpressionCode(loopAction.LoopConditionExpressionId.Value.ToString(), task, dataObjects, CreateBooleanConditionEmissionContext());
                        if (!string.IsNullOrWhiteSpace(loopCond))
                        {
                            sb.AppendLine($"{pad}u.StartBlockLoop();");
                            sb.AppendLine($"{pad}while(u.AdvanceBlockLoop() &&({loopCond}))");
                            sb.AppendLine($"{pad}{{");
                            for (var i = 0; i < block.Length; i++)
                            {
                                var loopBodyAction = orderedActions[block.StartIndex + i] with
                                {
                                    LoopConditionExpressionId = null
                                };
                                var loopBodyCond = ResolveActionConditionCode(loopBodyAction, task, dataObjects);
                                if (!string.IsNullOrWhiteSpace(loopBodyCond) &&
                                    ContainsFunctionCallOutsideQuotes(loopBodyCond, "u.LoopCounter"))
                                {
                                    EmitRowActionCore(
                                        sb,
                                        StripActionCondition(loopBodyAction),
                                        task,
                                        dataObjects,
                                        allTasks,
                                        pad + "    ",
                                        preferValueForResourceAssignments: true);
                                }
                                else
                                {
                                    EmitRowAction(sb, loopBodyAction, task, dataObjects, allTasks, pad + "    ");
                                }
                            }
                            sb.AppendLine($"{pad}}}");
                            sb.AppendLine($"{pad}u.EndBlockLoop();");
                            continue;
                        }
                    }
                }
                if (block.Kind == "GroupedCondition" && block.Length > 1)
                {
                    var cond = ResolveActionConditionCode(orderedActions[block.StartIndex], task, dataObjects);
                    if (!string.IsNullOrWhiteSpace(cond))
                    {
                        sb.AppendLine($"{pad}if ({cond})");
                        sb.AppendLine($"{pad}{{");
                        for (var i = 0; i < block.Length; i++)
                            EmitRowActionCore(sb, StripActionCondition(orderedActions[block.StartIndex + i]), task, dataObjects, allTasks, pad + "    ", preferValueForResourceAssignments: true);
                        sb.AppendLine($"{pad}}}");
                        continue;
                    }
                }
                else if (block.Kind == "ComplementaryPair" && block.Length == 2)
                {
                    var firstAction = orderedActions[block.StartIndex];
                    var secondAction = orderedActions[block.StartIndex + 1];
                    var cond = ResolveActionConditionCode(firstAction, task, dataObjects);
                    if (!string.IsNullOrWhiteSpace(cond))
                    {
                        sb.AppendLine($"{pad}if ({cond})");
                        sb.AppendLine($"{pad}{{");
                        EmitRowActionCore(sb, StripActionCondition(firstAction), task, dataObjects, allTasks, pad + "    ", preferValueForResourceAssignments: true);
                        sb.AppendLine($"{pad}}}");
                        sb.AppendLine($"{pad}else");
                        sb.AppendLine($"{pad}{{");
                        EmitRowActionCore(sb, StripActionCondition(secondAction), task, dataObjects, allTasks, pad + "    ", preferValueForResourceAssignments: true);
                        sb.AppendLine($"{pad}}}");
                        continue;
                    }
                }

                EmitRowAction(sb, orderedActions[block.StartIndex], task, dataObjects, allTasks, pad);
            }
            return;
        }

        for (var i = 0; i < orderedActions.Count; i++)
        {
            var action = orderedActions[i];
            var cond = ResolveActionConditionCode(action, task, dataObjects);
            var isLoop = !string.IsNullOrWhiteSpace(cond) && cond.Contains("u.LoopCounter()", StringComparison.Ordinal);

            if (!string.IsNullOrWhiteSpace(cond) && !isLoop && i + 1 < orderedActions.Count)
            {
                var group = new List<TaskRowActionDef> { action };
                var j = i + 1;
                while (j < orderedActions.Count)
                {
                    var next = orderedActions[j];
                    var nextCond = ResolveActionConditionCode(next, task, dataObjects);
                    if (string.Equals(cond, nextCond, StringComparison.Ordinal))
                    {
                        group.Add(next);
                        j++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (group.Count > 1)
                {
                    sb.AppendLine($"{pad}if ({cond})");
                    sb.AppendLine($"{pad}{{");
                    foreach (var g in group)
                        EmitRowActionCore(sb, StripActionCondition(g), task, dataObjects, allTasks, pad + "    ", preferValueForResourceAssignments: true);
                    sb.AppendLine($"{pad}}}");
                    i += group.Count - 1;
                    continue;
                }
            }
            if (!string.IsNullOrWhiteSpace(cond) && !isLoop && i + 1 < orderedActions.Count)
            {
                var nextAction = orderedActions[i + 1];
                var nextCond = ResolveActionConditionCode(nextAction, task, dataObjects);
                if (IsComplementaryCondition(cond, nextCond))
                {
                    sb.AppendLine($"{pad}if ({cond})");
                    sb.AppendLine($"{pad}{{");
                    EmitRowActionCore(sb, StripActionCondition(action), task, dataObjects, allTasks, pad + "    ", preferValueForResourceAssignments: true);
                    sb.AppendLine($"{pad}}}");
                    sb.AppendLine($"{pad}else");
                    sb.AppendLine($"{pad}{{");
                    EmitRowActionCore(sb, StripActionCondition(nextAction), task, dataObjects, allTasks, pad + "    ", preferValueForResourceAssignments: true);
                    sb.AppendLine($"{pad}}}");
                    i++;
                    continue;
                }
            }

            EmitRowAction(sb, action, task, dataObjects, allTasks, pad);
        }
    }
}


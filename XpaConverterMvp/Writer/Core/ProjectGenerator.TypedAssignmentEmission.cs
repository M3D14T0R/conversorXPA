using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static TargetValueInfo ResolveBindValueTargetInfoFromEvidence(
        TaskSemantic task,
        TaskLogicSelectDef select,
        string targetExpression)
    {
        // A coluna real do DataView é a evidência autoritativa para BindValue.
        // ColumnId também é usado por recursos auxiliares/expressões no XML e
        // pode apontar para um tipo diferente do membro de destino.
        if (TryResolveDataViewMemberTargetValueInfo(task, targetExpression, out var dataViewTarget))
            return dataViewTarget;

        var resource = ResolveTaskResourceColumn(task, select.ColumnId);
        var info = ResolveTargetValueInfo(task, select.Name, targetExpression, resource);
        return string.IsNullOrWhiteSpace(info.TargetMember)
            ? info with { TargetMember = targetExpression.Trim() }
            : info;
    }

    private static string EmitBindValueForTarget(
        TaskLogicSelectDef select,
        string targetExpression,
        string valueExpression,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(valueExpression))
            return valueExpression;

        var target = ResolveBindValueTargetInfoFromEvidence(task, select, targetExpression);
        if (target.IsDotNet)
            return valueExpression;
        return EmitExpressionForContext(
            valueExpression,
            task,
            CreateBindValueEmissionContext(target, target.TargetMember));
    }

    private static string EmitComparisonRightExpression(
        TaskSemantic task,
        string leftExpression,
        string rightExpression)
    {
        if (string.IsNullOrWhiteSpace(rightExpression))
            return rightExpression;

        // The declared DataView column is authoritative here.  A model can
        // expose a custom column class whose CLR name does not describe its
        // XPA scalar type, while the column metadata still does.
        var target = TryResolveDataViewMemberTargetValueInfo(task, leftExpression, out var dataViewTarget)
            ? dataViewTarget
            : ResolveTargetValueInfo(task, null, leftExpression);
        target = RecalibrateTargetInfoFromDeclaredTargetType(task, target, leftExpression);

        var expectedReturnType = ResolveReturnTypeForExpectedContext(ExpectedTypeForTarget(target));
        if (!string.IsNullOrWhiteSpace(expectedReturnType) &&
            TryResolveKnownExpressionReturnTypeWithoutLegacy(task, rightExpression, out var sourceReturnType) &&
            !string.IsNullOrWhiteSpace(sourceReturnType))
        {
            return EmitFromReliableTypeEvidence(
                rightExpression,
                sourceReturnType,
                expectedReturnType,
                "filter-comparison");
        }

        return rightExpression.Trim();
    }

    private static TargetValueInfo RecalibrateTargetInfoFromDeclaredTargetType(
        TaskSemantic task,
        TargetValueInfo target,
        string targetExpression)
    {
        var expected = ResolveExpectedTypeFromExpressionEvidence(task, targetExpression);
        var attr = MapReturnTypeToAttrObj(expected.ReturnType);
        return string.IsNullOrWhiteSpace(attr)
            ? target
            : RecalibrateTargetInfoFromAttrObj(target, attr);
    }

    private static string EmitInvokeReturnValue(
        string valueExpression,
        TaskReturnValueDef? returnValue,
        string returnVariable,
        string target,
        TaskSemantic task)
    {
        var targetInfo = ResolveTargetValueInfo(task, returnVariable, target);
        return EmitExpressionForContext(
            valueExpression,
            task,
            CreateAssignmentEmissionContext(targetInfo, target));
    }

    private static string BuildReturnAssignmentExpression(
        string returnVariable,
        string target,
        string valueExpression,
        TaskSemantic task,
        string? xmlTrace)
        => BuildUpdateAssignment(
            new TaskUpdateDef(returnVariable, "", null, false, false, null, null, null, false, xmlTrace),
            target,
            valueExpression,
            task,
            preferValueForResourceAssignments: true);

    private static IReadOnlyList<string> AlignAndEmitExternalCallArguments(
        IReadOnlyList<string> arguments,
        IReadOnlyList<string>? parameterTypes,
        TaskSemantic task)
    {
        if (parameterTypes is null)
            return arguments;

        var result = arguments.ToArray();
        for (var i = 0; i < result.Length && i < parameterTypes.Count; i++)
            result[i] = EmitCallArgumentForParameter(result[i], parameterTypes[i], task);
        return result;
    }

    private static string EmitRunArgumentsForTarget(
        string runArguments,
        TaskSemantic currentTask,
        TaskSemantic targetTask)
    {
        if (string.IsNullOrWhiteSpace(runArguments))
            return runArguments;

        var arguments = SplitTopLevelArguments(runArguments);
        var parameters = GetTaskParameters(targetTask);
        for (var i = 0; i < arguments.Count && i < parameters.Count; i++)
        {
            arguments[i] = EmitCallArgumentForParameter(
                arguments[i].Trim(),
                parameters[i].ParameterType,
                currentTask,
                preserveBinding: !IsInputParameterDirection(parameters[i].ParameterDirection));
        }
        return string.Join(", ", arguments);
    }

    private static int CountChangedRunArguments(string before, string after)
    {
        var left = SplitTopLevelArguments(before ?? "");
        var right = SplitTopLevelArguments(after ?? "");
        var changed = 0;
        for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            var leftValue = i < left.Count ? left[i].Trim() : "";
            var rightValue = i < right.Count ? right[i].Trim() : "";
            if (!string.Equals(leftValue, rightValue, StringComparison.Ordinal))
                changed++;
        }
        return changed;
    }

    private static string EnsureTextIoControllerBinding(string expression)
        => expression;
}

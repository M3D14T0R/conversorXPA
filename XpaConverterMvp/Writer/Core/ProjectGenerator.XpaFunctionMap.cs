using System;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ApplyXpaFunctionMap(string expr, TaskSemantic task, string? expressionAttr = null)
    {
        var localFunctionNames = task.FunctionOverridesSemantic
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accessibleParentFunctions = ResolveAccessibleParentFunctionTargets(task);
        var accessibleApplicationFunctions = ResolveAccessibleApplicationFunctionTargets(task);
        var userMethodsMap = GetUserMethodsPublicNameMap();
        expr = RewriteBareFunctionInvocations(expr, functionName =>
        {
            if (localFunctionNames.Contains(functionName))
                return null;

            if (_xpaFunctionMap.TryGetValue(functionName, out var xpaTarget) &&
                !string.Equals(functionName, "Level", StringComparison.OrdinalIgnoreCase))
                return xpaTarget;

            if (string.Equals(functionName, "ImageReload", StringComparison.OrdinalIgnoreCase))
                return "ViewCompat.ImageReload";

            if (userMethodsMap.TryGetValue(functionName, out var canonicalUserMethod))
                return $"u.{canonicalUserMethod}";

            if (accessibleParentFunctions.TryGetValue(functionName, out var parentTarget))
                return parentTarget;

            if (accessibleApplicationFunctions.TryGetValue(functionName, out var applicationTarget))
                return applicationTarget;

            if (TryGetComponentFunctionCallContract(functionName, out var componentContract))
                return componentContract.TargetName;

            return null;
        });

        expr = RewriteBareFunctionInvocations(expr, functionName =>
        {
            if (localFunctionNames.Contains(functionName))
                return null;
            return userMethodsMap.TryGetValue(functionName, out var canonical) ? $"u.{canonical}" : null;
        });

        expr = RewriteBareFunctionInvocations(expr, functionName =>
        {
            if (localFunctionNames.Contains(functionName))
                return null;
            if (TryGetComponentFunctionCallContract(functionName, out var componentContract))
                return componentContract.TargetName;
            if (!_componentFunctionReturnTypeByName.ContainsKey(functionName))
                return null;
            var methodName = ToCodeIdentifierPreservingCase(functionName);
            return $"ComponentFunctionCompat.{methodName}";
        });

        return expr;
    }
}


using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveSnippetClassName(TaskSemantic task, TaskInvokeDef targetInvoke, IReadOnlyList<TaskSemantic> allTasks)
    {
        var classBase = ResolveTaskClassName(task, allTasks) + "Snippet";
        var targetIndex = EnumerateTaskInvokes(task)
            .Where(i => i.OperationType == "." && !string.IsNullOrWhiteSpace(i.SnippetCode) && !string.IsNullOrWhiteSpace(i.FunctionName))
            .ToList()
            .FindIndex(i => ReferenceEquals(i, targetInvoke) || SameSnippetInvokeIdentity(i, targetInvoke));
        var emitted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var suffix = 0;
        var currentIndex = 0;
        foreach (var invoke in EnumerateTaskInvokes(task)
                     .Where(i => i.OperationType == "." && !string.IsNullOrWhiteSpace(i.SnippetCode) && !string.IsNullOrWhiteSpace(i.FunctionName)))
        {
            var className = suffix == 0 ? classBase : classBase + (suffix + 1).ToString();
            var code = BuildSnippetSource(invoke.SnippetCode!, className, BuildSnippetNamespace(className));
            while (emitted.TryGetValue(className, out var existing) && !string.Equals(existing, code, StringComparison.Ordinal))
            {
                suffix++;
                className = classBase + (suffix + 1).ToString();
                code = BuildSnippetSource(invoke.SnippetCode!, className, BuildSnippetNamespace(className));
            }

            if (!emitted.TryGetValue(className, out var same) || !string.Equals(same, code, StringComparison.Ordinal))
                emitted[className] = code;

            if (currentIndex == targetIndex)
                return className;

            suffix++;
            currentIndex++;
        }

        return classBase;
    }

    private static bool SameSnippetInvokeIdentity(TaskInvokeDef left, TaskInvokeDef right)
    {
        return string.Equals(left.XmlTrace, right.XmlTrace, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.FunctionName, right.FunctionName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.SnippetCode, right.SnippetCode, StringComparison.Ordinal)
               && string.Equals(left.ReturnVariable, right.ReturnVariable, StringComparison.OrdinalIgnoreCase)
               && left.ArgumentVariables.SequenceEqual(right.ArgumentVariables, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetSnippetFunctionParameters(string? snippetCode, string functionName, out List<string> parameters)
    {
        parameters = new List<string>();
        if (string.IsNullOrWhiteSpace(snippetCode) || string.IsNullOrWhiteSpace(functionName))
            return false;
        var rx = new Regex(@"\b" + Regex.Escape(functionName) + @"\s*\(([^)]*)\)", RegexOptions.IgnoreCase);
        var m = rx.Match(snippetCode);
        if (!m.Success)
            return false;
        var args = m.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(args))
            return true;
        parameters = args.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        return true;
    }

    private static bool TryGetSnippetFunctionReturnContract(string? snippetCode, string functionName, out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(snippetCode) || string.IsNullOrWhiteSpace(functionName))
            return false;

        var rx = new Regex(
            @"\b(?:public|private|internal|protected|static|extern|unsafe|async|\s)+(?<type>[A-Za-z_][A-Za-z0-9_<>\.\[\]]*)\s+" +
            Regex.Escape(functionName) +
            @"\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var m = rx.Match(snippetCode);
        if (!m.Success)
            return false;

        returnType = MapSnippetClrTypeToExpressionReturnType(ResolveSnippetParameterClrType(m.Groups["type"].Value, ""));
        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static string MapSnippetClrTypeToExpressionReturnType(string clrType)
    {
        var normalized = (clrType ?? "").Trim();
        return normalized switch
        {
            "string" or "System.String" => "Text",
            "bool" or "Boolean" or "System.Boolean" => "Bool",
            "byte[]" or "System.Byte[]" => "byte[]",
            "int" or "long" or "short" or "float" or "double" or "decimal" or
            "Int32" or "Int64" or "Int16" or "Single" or "Double" or "Decimal" or
            "System.Int32" or "System.Int64" or "System.Int16" or "System.Single" or "System.Double" or "System.Decimal" => "Number",
            "void" or "System.Void" => "",
            _ => normalized
        };
    }

    private static bool SnippetParameterRequiresRef(string parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter))
            return false;
        parameter = parameter.TrimStart();
        return parameter.StartsWith("ref ", StringComparison.OrdinalIgnoreCase)
            || parameter.StartsWith("out ", StringComparison.OrdinalIgnoreCase)
            || parameter.StartsWith("in ", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSnippetParameterModifier(string parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter))
            return "ref";
        parameter = parameter.TrimStart();
        if (parameter.StartsWith("out ", StringComparison.OrdinalIgnoreCase))
            return "out";
        if (parameter.StartsWith("in ", StringComparison.OrdinalIgnoreCase))
            return "in";
        return "ref";
    }

    private static bool TryResolveSnippetDotNetResourceArgument(TaskSemantic task, string argExpression, string parameter, TaskArgumentDef? argumentDef, out string targetExpr)
    {
        targetExpr = "";
        var expectedClrType = ResolveSnippetParameterClrType(parameter, "");
        TaskResourceColumnDef? fallbackByType = null;
        foreach (var resource in task.ResourcesSemantic.Ordered)
        {
            if (!IsDotNetTaskResource(resource))
                continue;

            var memberName = ResolveTaskResourceMemberName(task, resource);
            var normalizedResourceObjectType = !string.IsNullOrWhiteSpace(resource.ObjectType)
                ? NormalizeDotNetObjectType(resource.ObjectType)
                : null;
            if (string.Equals(argExpression, memberName, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(expectedClrType) ||
                    string.Equals(normalizedResourceObjectType, expectedClrType, StringComparison.Ordinal) ||
                    string.Equals(resource.ObjectType, expectedClrType, StringComparison.Ordinal))
                {
                    targetExpr = memberName;
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedClrType) &&
                (string.Equals(normalizedResourceObjectType, expectedClrType, StringComparison.Ordinal) ||
                 string.Equals(resource.ObjectType, expectedClrType, StringComparison.Ordinal)))
            {
                if (argumentDef is not null &&
                    !string.IsNullOrWhiteSpace(argumentDef.Name) &&
                    memberName.IndexOf(ToPascalIdentifier(argumentDef.Name), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    targetExpr = memberName;
                    return true;
                }

                fallbackByType ??= resource;
            }
        }

        if (fallbackByType is not null)
        {
            targetExpr = ResolveTaskResourceMemberName(task, fallbackByType);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(expectedClrType) &&
            TryResolveSnippetParentDotNetResourceArgument(task, expectedClrType, argumentDef, out targetExpr))
            return true;

        return false;
    }

    private static bool TryResolveSnippetParentDotNetResourceArgument(TaskSemantic task, string expectedClrType, TaskArgumentDef? argumentDef, out string targetExpr)
    {
        targetExpr = "";
        if (_allTasks is null)
            return false;

        var depth = 0;
        var ancestorOrdinal = task.ParentOrdinal;
        while (ancestorOrdinal.HasValue && depth < 6)
        {
            depth++;
            var parent = _allTasks.FirstOrDefault(x => x.Ordinal == ancestorOrdinal.Value);
            if (parent is null)
                break;

            TaskResourceColumnDef? fallbackByType = null;
            foreach (var resource in parent.ResourcesSemantic.Ordered)
            {
                if (!IsDotNetTaskResource(resource))
                    continue;

                var normalizedResourceObjectType = !string.IsNullOrWhiteSpace(resource.ObjectType)
                    ? NormalizeDotNetObjectType(resource.ObjectType)
                    : null;
                if (!string.Equals(normalizedResourceObjectType, expectedClrType, StringComparison.Ordinal) &&
                    !string.Equals(resource.ObjectType, expectedClrType, StringComparison.Ordinal))
                    continue;

                var memberName = ResolveTaskResourceMemberName(parent, resource);
                if (argumentDef is not null &&
                    !string.IsNullOrWhiteSpace(argumentDef.Name) &&
                    memberName.IndexOf(ToPascalIdentifier(argumentDef.Name), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    targetExpr = string.Concat(Enumerable.Repeat("_parent.", depth)) + memberName;
                    return true;
                }

                fallbackByType ??= resource;
            }

            if (fallbackByType is not null)
            {
                targetExpr = string.Concat(Enumerable.Repeat("_parent.", depth)) + ResolveTaskResourceMemberName(parent, fallbackByType);
                return true;
            }

            ancestorOrdinal = parent.ParentOrdinal;
        }

        return false;
    }

    private static bool TryResolveSnippetColumnArgument(TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects, string argExpression, string parameter, TaskArgumentDef? argumentDef, out string targetExpr, out string valueType, out string readExpr, out string writeExpr)
    {
        targetExpr = "";
        valueType = "";
        readExpr = "";
        writeExpr = "";
        foreach (var resource in task.ResourcesSemantic.Ordered)
        {
            var memberName = ResolveTaskResourceMemberName(task, resource);
            if (IsDotNetTaskResource(resource))
                continue;
            if (!SnippetArgumentReferencesResource(task, dataObjects, argExpression, argumentDef, memberName, resource))
                continue;

            valueType = ResolveSnippetParameterClrType(parameter, resource.AttrObj switch
            {
                "FIELD_NUMERIC" => "Number",
                "FIELD_DATE" => "Date",
                "FIELD_TIME" => "Time",
                "FIELD_BOOLEAN" or "FIELD_LOGICAL" => "Bool",
                "FIELD_BLOB" => "byte[]",
                _ => "Text"
            });
            targetExpr = memberName;
            readExpr = string.Equals(valueType, "string", StringComparison.Ordinal)
                ? $"{memberName}.Value.ToString()"
                : $"{memberName}.Value";
            writeExpr = $"{memberName}.Value = __TEMP__";
            return true;
        }

        return false;
    }

    private static bool SnippetArgumentReferencesResource(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        string argExpression,
        TaskArgumentDef? argumentDef,
        string memberName,
        TaskResourceColumnDef resource)
    {
        if (string.Equals(argExpression, memberName, StringComparison.Ordinal))
            return true;

        var variable = argumentDef?.Variable?.Trim();
        if (string.IsNullOrWhiteSpace(variable))
            return false;

        var normalizedVariable = variable.ToUpperInvariant();
        if (IsAlphabeticBindingToken(normalizedVariable))
        {
            var sourceBinding = ResolveExpressionOrdinalBinding(normalizedVariable, task, _allTasks ?? Array.Empty<TaskSemantic>(), dataObjects);
            if (string.Equals(sourceBinding, memberName, StringComparison.Ordinal))
                return true;
        }

        return string.Equals(variable, resource.Name, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(variable, memberName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSnippetParameterClrType(string parameter, string fallbackType)
    {
        if (string.IsNullOrWhiteSpace(parameter))
            return fallbackType;

        var trimmed = parameter.Trim();
        trimmed = Regex.Replace(trimmed, @"^(ref|out|in)\s+", "", RegexOptions.IgnoreCase);
        var lastSpace = trimmed.LastIndexOf(' ');
        var typePart = lastSpace > 0 ? trimmed.Substring(0, lastSpace).Trim() : trimmed;
        if (string.Equals(typePart, "System.String", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(typePart, "string", StringComparison.OrdinalIgnoreCase))
            return "string";
        if (string.Equals(typePart, "System.Void", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(typePart, "void", StringComparison.OrdinalIgnoreCase))
            return "void";
        return string.IsNullOrWhiteSpace(typePart) ? fallbackType : NormalizeDotNetObjectType(typePart);
    }
}


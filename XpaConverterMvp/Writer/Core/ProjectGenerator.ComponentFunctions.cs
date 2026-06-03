using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static readonly HashSet<string> ReservedRuntimeFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Date",
        "Time",
        "User",
        "VarSet",
        "Stat",
        "IniPut",
        "Rights"
    };

    private readonly record struct ComponentFunctionExport(
        string FunctionName,
        string MethodName,
        string ReturnType,
        IReadOnlyList<(string Type, string Name)> Parameters);

    private readonly record struct ComponentFunctionCallContract(
        string ComponentName,
        string TargetName,
        string ReturnType,
        IReadOnlyList<string> ParameterTypes);

    private static IReadOnlyList<ComponentFunctionExport> CollectComponentFunctionExports(ProjectSemantic parsed)
    {
        var appTask = parsed.Tasks.FirstOrDefault(t => t.MainProgram) ??
                      parsed.Tasks.FirstOrDefault(t => t.ParentOrdinal is null);
        if (appTask is null || appTask.FunctionOverridesSemantic.Count == 0)
            return Array.Empty<ComponentFunctionExport>();

        var result = new List<ComponentFunctionExport>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fn in appTask.FunctionOverridesSemantic)
        {
            if (string.IsNullOrWhiteSpace(fn.Name) ||
                string.IsNullOrWhiteSpace(fn.MethodName) ||
                ReservedRuntimeFunctionNames.Contains(fn.Name) ||
                !seen.Add(fn.Name))
            {
                continue;
            }

            result.Add(new ComponentFunctionExport(
                fn.Name,
                fn.MethodName,
                DefaultComponentFunctionType(NormalizeReturnTypeToken(fn.ReturnType)),
                fn.Parameters
                    .Select(p => (DefaultComponentFunctionType(NormalizeReturnTypeToken(p.ParameterType)), p.ParameterName))
                    .ToArray()));
        }

        return result;
    }

    private static string DefaultComponentFunctionType(string type)
        => string.IsNullOrWhiteSpace(type) ? "Text" : type;

    private static bool UsesPublicComponentFunctions(ProjectSemantic parsed)
        => CollectComponentFunctionExports(parsed).Count > 0;

    private static void WriteComponentFunctionsAsset(string outputRoot, string appNamespace, ProjectSemantic parsed)
    {
        var functions = CollectComponentFunctionExports(parsed);
        if (functions.Count == 0)
            return;

        var componentName = string.IsNullOrWhiteSpace(_targetComponent)
            ? appNamespace.Split('.').FirstOrDefault() ?? appNamespace
            : _targetComponent!;

        var sb = new StringBuilder();
        sb.AppendLine("using XPARuntimeCore.Box;");
        sb.AppendLine("using System;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace};");
        sb.AppendLine();
        sb.AppendLine("[AttributeUsage(AttributeTargets.Method)]");
        sb.AppendLine("public sealed class ComponentFunctionAttribute : Attribute");
        sb.AppendLine("{");
        sb.AppendLine("    public string Component { get; }");
        sb.AppendLine("    public string Function { get; }");
        sb.AppendLine();
        sb.AppendLine("    public ComponentFunctionAttribute(string component, string function)");
        sb.AppendLine("    {");
        sb.AppendLine("        Component = component;");
        sb.AppendLine("        Function = function;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("public static class ComponentFunctions");
        sb.AppendLine("{");

        var first = true;
        foreach (var fn in functions)
        {
            if (!first)
                sb.AppendLine();
            first = false;

            var parameters = string.Join(", ", fn.Parameters.Select(p => $"{p.Type} {p.Name}"));
            var arguments = string.Join(", ", fn.Parameters.Select(p => p.Name));
            sb.AppendLine($"    [ComponentFunction(\"{Escape(componentName)}\", \"{Escape(fn.FunctionName)}\")]");
            sb.AppendLine($"    public static {fn.ReturnType} {fn.MethodName}({parameters})");
            sb.AppendLine($"        => Application.Instance.{fn.MethodName}({arguments});");
        }

        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(outputRoot, "ComponentFunctions.cs"), sb.ToString(), Encoding.UTF8);
    }

    private static bool TryGetComponentFunctionCallContract(
        string functionName,
        out ComponentFunctionCallContract contract)
    {
        contract = default;
        var lookupName = ExtractFunctionLookupName(functionName);
        if (string.IsNullOrWhiteSpace(lookupName) ||
            ReservedRuntimeFunctionNames.Contains(lookupName))
            return false;

        if (!_componentFunctionSourceByName.TryGetValue(lookupName, out var componentName) ||
            string.IsNullOrWhiteSpace(componentName))
        {
            return false;
        }

        if (!_projectReferenceManifests.TryGetValue(componentName, out var manifest) ||
            manifest.Functions.Count == 0 ||
            !manifest.Functions.TryGetValue(lookupName, out var function) ||
            string.IsNullOrWhiteSpace(function.ClassName) ||
            string.IsNullOrWhiteSpace(function.MethodName))
        {
            return false;
        }

        var ns = string.IsNullOrWhiteSpace(manifest.Namespace) ? componentName : manifest.Namespace;
        contract = new ComponentFunctionCallContract(
            componentName,
            $"global::{ns}.{function.ClassName}.{function.MethodName}",
            NormalizeReturnTypeToken(function.ReturnType),
            function.ParameterTypes.Select(NormalizeReturnTypeToken).ToArray());
        return !string.IsNullOrWhiteSpace(contract.ReturnType);
    }

    private static string ExtractFunctionLookupName(string functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return "";

        var trimmed = functionName.Trim();
        if (trimmed.StartsWith("global::", StringComparison.Ordinal))
            trimmed = trimmed["global::".Length..];

        var lastDot = trimmed.LastIndexOf('.');
        return lastDot >= 0 ? trimmed[(lastDot + 1)..] : trimmed;
    }
}

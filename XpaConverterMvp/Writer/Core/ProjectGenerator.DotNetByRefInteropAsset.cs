using System.Reflection;
using System.Text;
using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private sealed record DotNetByRefMethodContract(
        string DeclaringType,
        string MethodName,
        string ReturnType,
        IReadOnlyList<DotNetByRefParameterContract> Parameters);

    private sealed record DotNetByRefParameterContract(
        string Name,
        string ClrType,
        bool IsByRef,
        bool IsOut);

    private static bool UsesDotNetByRefInterop(ProjectSemantic parsed)
        => ProjectUsesDnRefSyntax(parsed) || CollectDotNetByRefMethodContracts(parsed).Count > 0;

    private static void WriteDotNetByRefInteropAsset(string outputRoot, string appNamespace, ProjectSemantic parsed)
    {
        var contracts = CollectDotNetByRefMethodContracts(parsed);
        if (contracts.Count == 0 && !ProjectUsesDnRefSyntax(parsed))
            return;

        var sb = new StringBuilder();
        sb.AppendLine("namespace " + appNamespace + ";");
        sb.AppendLine();
        sb.AppendLine("internal static class DotNetByRefInterop");
        sb.AppendLine("{");
        AppendCommonDotNetByRefHelpers(sb);
        foreach (var contract in contracts
                     .OrderBy(c => c.DeclaringType, StringComparer.Ordinal)
                     .ThenBy(c => c.MethodName, StringComparer.Ordinal)
                     .ThenBy(c => c.Parameters.Count))
        {
            AppendDotNetByRefExtension(sb, contract);
            sb.AppendLine();
        }
        sb.AppendLine("}");

        WriteGeneratedSourceFile(outputRoot, "DotNetByRefInterop", sb.ToString(), Encoding.UTF8);
    }

    private static bool ProjectUsesDnRefSyntax(ProjectSemantic parsed)
        => parsed.Tasks
            .Where(ShouldGenerateForTarget)
            .SelectMany(task => task.ExpressionsSemantic.Entries)
            .Any(expression => (expression.Syntax ?? "").IndexOf("DNRef(", StringComparison.OrdinalIgnoreCase) >= 0);

    private static void AppendCommonDotNetByRefHelpers(StringBuilder sb)
    {
        sb.AppendLine("    internal delegate void StringOutAction(out string value);");
        sb.AppendLine("    internal delegate void StringRefAction(ref string value);");
        sb.AppendLine();
        sb.AppendLine("    internal static void InvokeStringOut(global::ENV.Data.ByteArrayColumn target, StringOutAction action)");
        sb.AppendLine("    {");
        sb.AppendLine("        string value;");
        sb.AppendLine("        action(out value);");
        sb.AppendLine(
            "        target.Value = " +
            XpaExpressionTypeMap.Convert(
                "value ?? \"\"",
                XpaType.Object,
                XpaType.Blob,
                "global::ENV.UserMethods.Instance") +
            ";");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal static void InvokeStringOut(global::ENV.Data.TextColumn target, StringOutAction action)");
        sb.AppendLine("    {");
        sb.AppendLine("        string value;");
        sb.AppendLine("        action(out value);");
        sb.AppendLine("        target.Value = value ?? \"\";");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal static void InvokeStringRef(global::ENV.Data.ByteArrayColumn target, StringRefAction action)");
        sb.AppendLine("    {");
        sb.AppendLine("        var value = global::ENV.UserMethods.Instance.ByteArrayToText(target).ToString();");
        sb.AppendLine("        action(ref value);");
        sb.AppendLine(
            "        target.Value = " +
            XpaExpressionTypeMap.Convert(
                "value ?? \"\"",
                XpaType.Object,
                XpaType.Blob,
                "global::ENV.UserMethods.Instance") +
            ";");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal static void InvokeStringRef(global::ENV.Data.TextColumn target, StringRefAction action)");
        sb.AppendLine("    {");
        sb.AppendLine("        var value = ((string)target) ?? \"\";");
        sb.AppendLine("        action(ref value);");
        sb.AppendLine("        target.Value = value ?? \"\";");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static IReadOnlyList<DotNetByRefMethodContract> CollectDotNetByRefMethodContracts(ProjectSemantic parsed)
    {
        if (_dllReferenceMap.Count == 0)
            return Array.Empty<DotNetByRefMethodContract>();

        var contracts = new Dictionary<string, DotNetByRefMethodContract>(StringComparer.Ordinal);
        foreach (var task in parsed.Tasks.Where(ShouldGenerateForTarget))
        {
            var objectTypeByToken = BuildDotNetObjectTypeByLegacyToken(task, parsed.DataObjects);
            if (objectTypeByToken.Count == 0)
                continue;

            foreach (var expression in task.ExpressionsSemantic.Entries)
            {
                var syntax = expression.Syntax ?? "";
                if (syntax.IndexOf("DNRef(", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                foreach (var call in EnumerateDotNetByRefCalls(syntax))
                {
                    if (!objectTypeByToken.TryGetValue(call.TargetToken, out var objectType))
                        continue;

                    if (!TryReadDotNetByRefMethodContract(objectType, call.MethodName, call.ArgumentCount, out var contract))
                        continue;

                    var key = BuildDotNetByRefContractKey(contract);
                    contracts.TryAdd(key, contract);
                }
            }
        }

        return contracts.Values.ToList();
    }

    private static Dictionary<string, string> BuildDotNetObjectTypeByLegacyToken(TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void AddResource(TaskSemantic owner, TaskResourceColumnDef resource, int legacyOrdinal)
        {
            if (!IsDotNetTaskResource(resource))
                return;

            var objectType = NormalizeDotNetObjectType(resource.ObjectType ?? "");
            if (string.IsNullOrWhiteSpace(objectType) || string.Equals(objectType, "dynamic", StringComparison.Ordinal))
                return;

            var memberName = ResolveTaskResourceMemberName(owner, resource);
            if (!string.IsNullOrWhiteSpace(resource.Name))
                result.TryAdd(resource.Name, objectType);
            if (!string.IsNullOrWhiteSpace(memberName))
                result.TryAdd(memberName, objectType);

            var legacyAlias = ToLegacyExpressionAlias(legacyOrdinal);
            if (!string.IsNullOrWhiteSpace(legacyAlias))
                result.TryAdd(legacyAlias, objectType);
        }

        for (var i = 0; i < task.ResourcesSemantic.Ordered.Count; i++)
            AddResource(task, task.ResourcesSemantic.Ordered[i], i + 3);

        foreach (var pair in task.ResourcesSemantic.ByLegacyName)
        {
            if (IsDotNetTaskResource(pair.Value))
                result.TryAdd(pair.Key, NormalizeDotNetObjectType(pair.Value.ObjectType ?? ""));
        }

        foreach (var pair in BuildAccessibleLegacyResourceReferenceMap(task, dataObjects))
        {
            var resource = ResolveResourceByTargetPath(task, pair.Value, _allTasks ?? Array.Empty<TaskSemantic>());
            if (resource is null || !IsDotNetTaskResource(resource))
                continue;

            var objectType = NormalizeDotNetObjectType(resource.ObjectType ?? "");
            if (!string.IsNullOrWhiteSpace(objectType) && !string.Equals(objectType, "dynamic", StringComparison.Ordinal))
                result.TryAdd(pair.Key, objectType);
        }

        return result;
    }

    private static IEnumerable<(string TargetToken, string MethodName, int ArgumentCount)> EnumerateDotNetByRefCalls(string syntax)
    {
        var cursor = 0;
        while (cursor < syntax.Length)
        {
            if (!IsDotNetIdentifierStart(syntax[cursor]))
            {
                cursor++;
                continue;
            }

            var targetStart = cursor;
            cursor++;
            while (cursor < syntax.Length && IsDotNetIdentifierPart(syntax[cursor]))
                cursor++;
            var target = syntax[targetStart..cursor];

            var afterTarget = SkipDotNetByRefWhitespace(syntax, cursor);
            if (afterTarget >= syntax.Length || syntax[afterTarget] != '.')
                continue;

            cursor = SkipDotNetByRefWhitespace(syntax, afterTarget + 1);
            if (cursor >= syntax.Length || !IsDotNetIdentifierStart(syntax[cursor]))
                continue;

            var methodStart = cursor;
            cursor++;
            while (cursor < syntax.Length && IsDotNetIdentifierPart(syntax[cursor]))
                cursor++;
            var method = syntax[methodStart..cursor];

            var openParen = SkipDotNetByRefWhitespace(syntax, cursor);
            if (openParen >= syntax.Length || syntax[openParen] != '(')
                continue;

            var closeParen = FindMatchingParen(syntax, openParen);
            if (closeParen < 0)
            {
                cursor = openParen + 1;
                continue;
            }

            var inner = syntax[(openParen + 1)..closeParen];
            if (inner.IndexOf("DNRef(", StringComparison.OrdinalIgnoreCase) >= 0)
                yield return (target, method, SplitTopLevelArguments(inner).Count);

            cursor = closeParen + 1;
        }
    }

    private static int SkipDotNetByRefWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static bool IsDotNetIdentifierStart(char ch)
        => ch == '_' || char.IsLetter(ch);

    private static bool IsDotNetIdentifierPart(char ch)
        => ch == '_' || char.IsLetterOrDigit(ch);

    private static bool TryReadDotNetByRefMethodContract(
        string objectType,
        string methodName,
        int argumentCount,
        out DotNetByRefMethodContract contract)
    {
        contract = default!;
        if (!TryLoadClrTypeFromMappedReference(objectType, out var clrType) || clrType is null)
            return false;

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
        var method = clrType
            .GetMethods(flags)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
            .Where(m => m.GetParameters().Length == argumentCount)
            .Where(m => m.GetParameters().Any(p => p.ParameterType.IsByRef))
            .OrderByDescending(m => m.GetParameters().Count(p => p.ParameterType.IsByRef))
            .FirstOrDefault();
        if (method is null)
            return false;

        var parameters = new List<DotNetByRefParameterContract>();
        foreach (var parameter in method.GetParameters())
        {
            var parameterType = parameter.ParameterType;
            var effectiveType = parameterType.IsByRef
                ? parameterType.GetElementType()
                : parameterType;
            if (effectiveType is null)
                return false;

            if (parameterType.IsByRef && !CanBridgeDotNetByRefType(effectiveType))
                return false;

            parameters.Add(new DotNetByRefParameterContract(
                SafeParameterName(parameter.Name, parameters.Count),
                FormatClrTypeName(effectiveType),
                parameterType.IsByRef,
                parameter.IsOut));
        }

        contract = new DotNetByRefMethodContract(
            FormatClrTypeName(method.DeclaringType ?? clrType),
            method.Name,
            FormatClrTypeName(method.ReturnType),
            parameters);
        return true;
    }

    private static bool CanBridgeDotNetByRefType(Type type)
    {
        var effective = Nullable.GetUnderlyingType(type) ?? type;
        return effective == typeof(string) ||
               effective == typeof(bool) ||
               effective == typeof(byte[]) ||
               IsClrNumericType(effective);
    }

    private static string BuildDotNetByRefContractKey(DotNetByRefMethodContract contract)
        => contract.DeclaringType + "|" +
           contract.MethodName + "|" +
           string.Join("|", contract.Parameters.Select(p => (p.IsByRef ? "ref " : "") + p.ClrType));

    private static void AppendDotNetByRefExtension(StringBuilder sb, DotNetByRefMethodContract contract)
    {
        var parameters = new List<string> { $"this {contract.DeclaringType} instance" };
        for (var i = 0; i < contract.Parameters.Count; i++)
        {
            var parameter = contract.Parameters[i];
            var parameterType = parameter.IsByRef
                ? ColumnTypeForByRefClrType(parameter.ClrType)
                : parameter.ClrType;
            parameters.Add(parameterType + " " + parameter.Name);
        }

        sb.AppendLine($"    public static {contract.ReturnType} {contract.MethodName}({string.Join(", ", parameters)})");
        sb.AppendLine("    {");

        for (var i = 0; i < contract.Parameters.Count; i++)
        {
            var parameter = contract.Parameters[i];
            if (!parameter.IsByRef)
                continue;

            var localName = ByRefLocalName(i);
            var initialValue = parameter.IsOut
                ? $"default({parameter.ClrType})!"
                : ReadByRefColumnValue(parameter.Name, parameter.ClrType);
            sb.AppendLine($"        var {localName} = {initialValue};");
        }

        var callArguments = contract.Parameters
            .Select((parameter, index) => parameter.IsByRef ? "ref " + ByRefLocalName(index) : parameter.Name)
            .ToList();
        var call = $"instance.{contract.MethodName}({string.Join(", ", callArguments)})";
        var returnsVoid = string.Equals(contract.ReturnType, "void", StringComparison.Ordinal);
        if (returnsVoid)
            sb.AppendLine($"        {call};");
        else
            sb.AppendLine($"        var __result = {call};");

        for (var i = 0; i < contract.Parameters.Count; i++)
        {
            var parameter = contract.Parameters[i];
            if (!parameter.IsByRef)
                continue;

            sb.AppendLine($"        {parameter.Name}.Value = {ByRefLocalName(i)};");
        }

        if (!returnsVoid)
            sb.AppendLine("        return __result;");
        sb.AppendLine("    }");
    }

    private static string ColumnTypeForByRefClrType(string clrType)
        => clrType switch
        {
            "string" => "global::XPARuntimeCore.Box.Data.TextColumn",
            "bool" => "global::XPARuntimeCore.Box.Data.BoolColumn",
            "byte[]" => "global::XPARuntimeCore.Box.Data.ByteArrayColumn",
            _ => "global::XPARuntimeCore.Box.Data.NumberColumn"
        };

    private static string ReadByRefColumnValue(string parameterName, string clrType)
        => clrType switch
        {
            "string" => $"(string){parameterName}",
            "bool" => $"(bool){parameterName}",
            "byte[]" => $"{parameterName}.Value",
            _ => $"({clrType}){parameterName}"
        };

    private static string ByRefLocalName(int index)
        => "__byRef" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string SafeParameterName(string? name, int index)
    {
        var candidate = string.IsNullOrWhiteSpace(name)
            ? "arg" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : SanitizeIdentifier(name.Trim());
        if (candidate.Length == 0 || char.IsDigit(candidate[0]))
            candidate = "_" + candidate;
        return candidate switch
        {
            "instance" or "return" or "ref" or "out" or "in" or "string" or "bool" or "int" or "double" or "decimal" => candidate + "Arg",
            _ => candidate
        };
    }

    private static string SanitizeIdentifier(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(IsDotNetIdentifierPart(ch) ? ch : '_');
        return sb.ToString();
    }

    private static string FormatClrTypeName(Type type)
    {
        if (type == typeof(void))
            return "void";
        if (type == typeof(string))
            return "string";
        if (type == typeof(bool))
            return "bool";
        if (type == typeof(byte[]))
            return "byte[]";
        if (type == typeof(int))
            return "int";
        if (type == typeof(double))
            return "double";
        if (type == typeof(decimal))
            return "decimal";
        if (type == typeof(long))
            return "long";
        if (type == typeof(short))
            return "short";
        if (type == typeof(float))
            return "float";
        if (type == typeof(byte))
            return "byte";
        if (type == typeof(uint))
            return "uint";
        if (type == typeof(ulong))
            return "ulong";
        if (type == typeof(ushort))
            return "ushort";
        if (type == typeof(sbyte))
            return "sbyte";
        if (type.IsArray && type.GetElementType() is { } elementType)
            return FormatClrTypeName(elementType) + "[]";

        return "global::" + (type.FullName ?? type.Name).Replace("+", ".");
    }
}

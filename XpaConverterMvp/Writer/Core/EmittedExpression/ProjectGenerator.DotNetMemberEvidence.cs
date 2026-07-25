using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static readonly ConcurrentDictionary<string, string> _dotNetMemberTypeEvidenceCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> _dotNetMethodReturnTypeEvidenceCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> _dotNetMethodParameterTypeEvidenceCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> _dotNetMemberExistenceEvidenceCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> _dotNetTypeAssemblyPathEvidenceCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> _dotNetTypeScalarEvidenceCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> _dotNetEvidenceAssemblyDependencyLoadCache = new(StringComparer.OrdinalIgnoreCase);

    private static bool TryReadDotNetMemberEvidence(
        string translatedBinding,
        TaskSemantic task,
        out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(translatedBinding))
            return false;

        var binding = translatedBinding.Trim();
        for (var dot = binding.LastIndexOf('.'); dot > 0; dot = binding.LastIndexOf('.', dot - 1))
        {
            var ownerPath = binding[..dot].Trim();
            var memberPath = binding[(dot + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(ownerPath) || !IsDotNetMemberEvidencePath(memberPath))
                continue;

            var resource = ResolveResourceByTargetPath(task, ownerPath, _allTasks ?? Array.Empty<TaskSemantic>());
            if (resource is null || !IsDotNetTaskResource(resource))
                continue;

            var objectType = NormalizeDotNetObjectType(resource.ObjectType ?? "");
            if (TryReadDotNetMemberEvidence(objectType, memberPath, out returnType))
                return true;
        }

        return false;
    }

    private static bool TryReadDotNetMethodCallEvidence(
        string translatedCall,
        TaskSemantic task,
        out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(translatedCall) ||
            !TryParseFunctionCall(translatedCall.Trim(), out var functionName, out var args))
            return false;

        var dot = functionName.LastIndexOf('.');
        if (dot <= 0 || dot + 1 >= functionName.Length)
            return false;

        var ownerPath = functionName[..dot].Trim();
        var methodName = functionName[(dot + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(ownerPath) || string.IsNullOrWhiteSpace(methodName))
            return false;

        var resource = ResolveResourceByTargetPath(task, ownerPath, _allTasks ?? Array.Empty<TaskSemantic>());
        if (resource is null || !IsDotNetTaskResource(resource))
            return false;

        var objectType = NormalizeDotNetObjectType(resource.ObjectType ?? "");
        return TryReadDotNetMethodReturnType(objectType, methodName, args.Count, out returnType);
    }

    private static bool TryReadDotNetMethodArgumentTypeEvidence(
        string functionName,
        TaskSemantic task,
        int argumentIndex,
        int argumentCount,
        out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(functionName) ||
            argumentIndex < 0 ||
            argumentIndex >= argumentCount)
            return false;

        var dot = functionName.LastIndexOf('.');
        if (dot <= 0 || dot + 1 >= functionName.Length)
            return false;

        var ownerPath = functionName[..dot].Trim();
        var methodName = functionName[(dot + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(ownerPath) || string.IsNullOrWhiteSpace(methodName))
            return false;

        var resource = ResolveResourceByTargetPath(task, ownerPath, _allTasks ?? Array.Empty<TaskSemantic>());
        if (resource is null || !IsDotNetTaskResource(resource))
            return false;

        var objectType = NormalizeDotNetObjectType(resource.ObjectType ?? "");
        return TryReadDotNetMethodParameterType(
            objectType,
            methodName,
            argumentCount,
            argumentIndex,
            out returnType);
    }

    private static bool TryReadDotNetMemberEvidence(
        string objectType,
        string memberPath,
        out string returnType)
    {
        returnType = "";
        var normalizedObjectType = NormalizeDotNetObjectType(objectType ?? "");
        var normalizedMemberPath = StripDotNetQualifierOutsideQuotes(memberPath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalizedObjectType) ||
            string.Equals(normalizedObjectType, "dynamic", StringComparison.Ordinal) ||
            !IsDotNetMemberEvidencePath(normalizedMemberPath))
            return false;

        var assemblyPath = FindMappedAssemblyPathForDotNetType(normalizedObjectType);
        var cacheKey = BuildDotNetEvidenceCacheKey(normalizedObjectType, normalizedMemberPath, assemblyPath);
        if (_dotNetMemberTypeEvidenceCache.TryGetValue(cacheKey, out var cached))
        {
            returnType = cached;
            return !string.IsNullOrWhiteSpace(returnType);
        }

        var found =
            TryReadDotNetMemberEvidenceFromReflection(normalizedObjectType, normalizedMemberPath, out returnType) ||
            TryReadDotNetMemberEvidenceFromMetadata(normalizedObjectType, normalizedMemberPath, out returnType);

        _dotNetMemberTypeEvidenceCache[cacheKey] = found ? returnType : "";
        return found;
    }

    private static bool HasDotNetMemberEvidence(string objectType, string memberPath)
    {
        var normalizedObjectType = NormalizeDotNetObjectType(objectType ?? "");
        var normalizedMemberPath = StripDotNetQualifierOutsideQuotes(memberPath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalizedObjectType) ||
            string.Equals(normalizedObjectType, "dynamic", StringComparison.Ordinal) ||
            !IsDotNetMemberEvidencePath(normalizedMemberPath))
            return false;

        var assemblyPath = FindMappedAssemblyPathForDotNetType(normalizedObjectType);
        var cacheKey = BuildDotNetEvidenceCacheKey(normalizedObjectType, "exists|" + normalizedMemberPath, assemblyPath);
        return _dotNetMemberExistenceEvidenceCache.GetOrAdd(cacheKey, _ =>
            HasDotNetMemberEvidenceFromReflection(normalizedObjectType, normalizedMemberPath) ||
            HasDotNetMemberEvidenceFromMetadata(normalizedObjectType, normalizedMemberPath));
    }

    private static bool HasDotNetMemberEvidenceFromReflection(string objectType, string memberPath)
    {
        if (!TryLoadClrTypeFromMappedReference(objectType, out var clrType) || clrType is null)
            return false;

        var currentType = clrType;
        foreach (var segment in memberPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryReadClrMemberType(currentType, segment, out var memberType) || memberType is null)
                return false;

            currentType = memberType;
        }

        return true;
    }

    private static bool HasDotNetMemberEvidenceFromMetadata(string objectType, string memberPath)
    {
        var currentObjectType = objectType.Trim();
        foreach (var segment in memberPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryReadMetadataMemberShape(currentObjectType, segment, out var shape) ||
                string.IsNullOrWhiteSpace(shape.ClrTypeName))
                return false;

            currentObjectType = shape.ClrTypeName;
        }

        return true;
    }

    private static bool TryReadDotNetMethodReturnType(
        string objectType,
        string methodName,
        int argumentCount,
        out string returnType)
    {
        returnType = "";
        var normalizedObjectType = NormalizeDotNetObjectType(objectType ?? "");
        var normalizedMethodName = (methodName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalizedObjectType) ||
            string.IsNullOrWhiteSpace(normalizedMethodName) ||
            argumentCount < 0)
            return false;

        var assemblyPath = FindMappedAssemblyPathForDotNetType(normalizedObjectType);
        var cacheKey = BuildDotNetEvidenceCacheKey(
            normalizedObjectType,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{normalizedMethodName}|{argumentCount}"),
            assemblyPath);
        if (_dotNetMethodReturnTypeEvidenceCache.TryGetValue(cacheKey, out var cached))
        {
            returnType = cached;
            return !string.IsNullOrWhiteSpace(returnType);
        }

        if (TryReadDotNetMethodReturnTypeFromMetadata(
                normalizedObjectType,
                normalizedMethodName,
                argumentCount,
                out returnType))
        {
            _dotNetMethodReturnTypeEvidenceCache[cacheKey] = returnType;
            return true;
        }

        if (!TryLoadClrTypeFromMappedReference(normalizedObjectType, out var clrType) || clrType is null)
        {
            _dotNetMethodReturnTypeEvidenceCache[cacheKey] = "";
            return false;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.IgnoreCase;
        var returnTypes = clrType
            .GetMethods(flags)
            .Where(method =>
                string.Equals(method.Name, normalizedMethodName, StringComparison.OrdinalIgnoreCase) &&
                method.GetParameters().Length == argumentCount)
            .Select(ReadClrMethodReturnTypeName)
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (returnTypes.Length != 1)
        {
            _dotNetMethodReturnTypeEvidenceCache[cacheKey] = "";
            return false;
        }

        returnType = returnTypes[0];
        _dotNetMethodReturnTypeEvidenceCache[cacheKey] = returnType;
        return true;
    }

    private static string ReadClrMethodReturnTypeName(MethodInfo method)
    {
        var type = method.ReturnType;
        if (type == typeof(void))
            return "void";

        return TryMapClrEvidenceType(type, out var mapped)
            ? mapped
            : type.FullName ?? type.Name;
    }

    private static bool TryReadDotNetMethodParameterType(
        string objectType,
        string methodName,
        int argumentCount,
        int argumentIndex,
        out string returnType)
    {
        returnType = "";
        var normalizedObjectType = NormalizeDotNetObjectType(objectType ?? "");
        var normalizedMethodName = (methodName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalizedObjectType) ||
            string.IsNullOrWhiteSpace(normalizedMethodName) ||
            argumentCount < 0 ||
            argumentIndex < 0 ||
            argumentIndex >= argumentCount)
            return false;

        var assemblyPath = FindMappedAssemblyPathForDotNetType(normalizedObjectType);
        var cacheKey = BuildDotNetEvidenceCacheKey(
            normalizedObjectType,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{normalizedMethodName}|arg:{argumentIndex}|count:{argumentCount}"),
            assemblyPath);
        if (_dotNetMethodParameterTypeEvidenceCache.TryGetValue(cacheKey, out var cached))
        {
            returnType = cached;
            return !string.IsNullOrWhiteSpace(returnType);
        }

        if (TryReadDotNetMethodParameterTypeFromMetadata(
                normalizedObjectType,
                normalizedMethodName,
                argumentCount,
                argumentIndex,
                out returnType))
        {
            _dotNetMethodParameterTypeEvidenceCache[cacheKey] = returnType;
            return true;
        }

        if (!TryLoadClrTypeFromMappedReference(normalizedObjectType, out var clrType) || clrType is null)
        {
            _dotNetMethodParameterTypeEvidenceCache[cacheKey] = "";
            return false;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.IgnoreCase;
        var parameterTypes = clrType
            .GetMethods(flags)
            .Where(method =>
                string.Equals(method.Name, normalizedMethodName, StringComparison.OrdinalIgnoreCase) &&
                method.GetParameters().Length == argumentCount)
            .Select(method => ReadClrMethodParameterTypeName(method.GetParameters()[argumentIndex].ParameterType))
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (parameterTypes.Length != 1)
        {
            _dotNetMethodParameterTypeEvidenceCache[cacheKey] = "";
            return false;
        }

        returnType = parameterTypes[0];
        _dotNetMethodParameterTypeEvidenceCache[cacheKey] = returnType;
        return true;
    }

    private static string ReadClrMethodParameterTypeName(Type parameterType)
    {
        var type = parameterType.IsByRef && parameterType.GetElementType() is { } elementType
            ? elementType
            : parameterType;

        return TryMapClrEvidenceType(type, out var mapped)
            ? mapped
            : "object";
    }

    private static bool TryReadDotNetMethodReturnTypeFromMetadata(
        string objectType,
        string methodName,
        int argumentCount,
        out string returnType)
    {
        returnType = "";
        var visitedTypes = new HashSet<string>(StringComparer.Ordinal);
        return TryReadDotNetMethodReturnTypeFromMetadata(
            objectType,
            methodName,
            argumentCount,
            visitedTypes,
            out returnType);
    }

    private static bool TryReadDotNetMethodReturnTypeFromMetadata(
        string objectType,
        string methodName,
        int argumentCount,
        HashSet<string> visitedTypes,
        out string returnType)
    {
        returnType = "";
        if (!visitedTypes.Add(objectType))
            return false;

        var assemblyPath = FindMappedAssemblyPathForDotNetType(objectType);
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return false;

        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                return false;

            var reader = peReader.GetMetadataReader();
            var provider = new DotNetMetadataTypeNameProvider();
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                if (!MetadataTypeMatches(reader, typeHandle, type, objectType))
                    continue;

                var returnTypes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    if (!string.Equals(reader.GetString(method.Name), methodName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var signature = method.DecodeSignature(provider, null);
                    if (signature.ParameterTypes.Length != argumentCount)
                        continue;

                    var mapped = MapMetadataMethodReturnType(signature.ReturnType);
                    if (!string.IsNullOrWhiteSpace(mapped))
                        returnTypes.Add(mapped);
                }

                if (returnTypes.Count == 1)
                {
                    returnType = returnTypes.First();
                    return true;
                }

                if (returnTypes.Count > 1)
                    return false;

                var baseType = ReadMetadataBaseTypeName(reader, type.BaseType, provider);
                if (!string.IsNullOrWhiteSpace(baseType) &&
                    !string.Equals(baseType, "System.Object", StringComparison.Ordinal) &&
                    TryReadDotNetMethodReturnTypeFromMetadata(baseType, methodName, argumentCount, visitedTypes, out returnType))
                    return true;

                return false;
            }

            return false;
        }
        catch
        {
            returnType = "";
            return false;
        }
    }

    private static string MapMetadataMethodReturnType(string returnType)
    {
        if (string.Equals(returnType, "System.Void", StringComparison.Ordinal) ||
            string.Equals(returnType, "Void", StringComparison.Ordinal))
            return "void";

        return TryMapClrEvidenceTypeName(returnType, out var mapped)
            ? mapped
            : returnType;
    }

    private static bool TryReadDotNetMemberEvidenceFromReflection(
        string objectType,
        string memberPath,
        out string returnType)
    {
        returnType = "";
        if (!TryLoadClrTypeFromMappedReference(objectType, out var clrType) || clrType is null)
            return false;

        var currentType = clrType;
        foreach (var segment in memberPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryReadClrMemberType(currentType, segment, out var memberType) || memberType is null)
                return false;

            currentType = memberType;
        }

        return TryMapClrEvidenceType(currentType, out returnType);
    }

    private static bool TryReadDotNetMemberEvidenceFromMetadata(
        string objectType,
        string memberPath,
        out string returnType)
    {
        returnType = "";
        var currentObjectType = objectType.Trim();
        var segments = memberPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments[..^1])
        {
            if (!TryReadMetadataMemberShape(currentObjectType, segment, out var shape) ||
                string.IsNullOrWhiteSpace(shape.ClrTypeName))
                return false;

            currentObjectType = shape.ClrTypeName;
        }

        return segments.Length > 0 &&
               TryReadMetadataMemberShape(currentObjectType, segments[^1], out var lastShape) &&
               TryMapClrEvidenceTypeName(lastShape.ClrTypeName, out returnType);
    }

    private static bool TryReadMetadataMemberShape(
        string objectType,
        string memberName,
        out DotNetMemberEvidenceShape shape)
    {
        shape = default;
        var assemblyPath = FindMappedAssemblyPathForDotNetType(objectType);
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return false;

        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                return false;

            var reader = peReader.GetMetadataReader();
            var provider = new DotNetMetadataTypeNameProvider();
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                if (!MetadataTypeMatches(reader, typeHandle, type, objectType))
                    continue;

                foreach (var propertyHandle in type.GetProperties())
                {
                    var property = reader.GetPropertyDefinition(propertyHandle);
                    if (string.Equals(reader.GetString(property.Name), memberName, StringComparison.OrdinalIgnoreCase))
                        return BuildDotNetMemberEvidenceShape(property.DecodeSignature(provider, null).ReturnType, out shape);
                }

                foreach (var fieldHandle in type.GetFields())
                {
                    var field = reader.GetFieldDefinition(fieldHandle);
                    if (string.Equals(reader.GetString(field.Name), memberName, StringComparison.OrdinalIgnoreCase))
                        return BuildDotNetMemberEvidenceShape(field.DecodeSignature(provider, null), out shape);
                }

                return false;
            }
        }
        catch
        {
            shape = default;
            return false;
        }

        return false;
    }

    private static string ReadMetadataBaseTypeName(
        MetadataReader reader,
        EntityHandle handle,
        DotNetMetadataTypeNameProvider provider)
    {
        if (handle.IsNil)
            return "";

        return handle.Kind switch
        {
            HandleKind.TypeDefinition => ReadMetadataTypeFullName(reader, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => ReadMetadataTypeReferenceFullName(reader, (TypeReferenceHandle)handle),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(provider, null),
            _ => ""
        };
    }

    private static bool BuildDotNetMemberEvidenceShape(string clrTypeName, out DotNetMemberEvidenceShape shape)
    {
        shape = string.IsNullOrWhiteSpace(clrTypeName)
            ? default
            : new DotNetMemberEvidenceShape(clrTypeName.Trim());
        return !string.IsNullOrWhiteSpace(shape.ClrTypeName);
    }

    private static bool HasClrConstructorEvidenceFromMetadata(string objectType, int argumentCount)
    {
        var assemblyPath = FindMappedAssemblyPathForDotNetType(objectType);
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return false;

        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                return false;

            var reader = peReader.GetMetadataReader();
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                if (!MetadataTypeMatches(reader, typeHandle, type, objectType))
                    continue;

                foreach (var methodHandle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    if (!string.Equals(reader.GetString(method.Name), ".ctor", StringComparison.Ordinal))
                        continue;

                    if (method.GetParameters().Count == argumentCount)
                        return true;
                }

                return false;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsDotNetMemberEvidencePath(string memberPath)
        => !string.IsNullOrWhiteSpace(memberPath) &&
           memberPath.Split('.').All(segment =>
           {
               var trimmed = segment.Trim();
               return trimmed.Length > 0 &&
                      (trimmed[0] == '_' || char.IsLetter(trimmed[0])) &&
                      trimmed.Skip(1).All(ch => ch == '_' || char.IsLetterOrDigit(ch));
           });

    private static bool TryLoadClrTypeFromMappedReference(string objectType, out Type? clrType)
    {
        clrType = Type.GetType(objectType, throwOnError: false, ignoreCase: false);
        if (clrType is not null)
            return true;

        var assemblyPath = FindMappedAssemblyPathForDotNetType(objectType);
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return false;

        try
        {
            LoadDotNetEvidenceAssemblyDependencies(assemblyPath);
            var assembly = Assembly.LoadFrom(assemblyPath);
            clrType = assembly.GetType(objectType, throwOnError: false, ignoreCase: false);
            if (clrType is not null)
                return true;

            clrType = assembly
                .GetExportedTypes()
                .FirstOrDefault(t => string.Equals(t.FullName, objectType, StringComparison.Ordinal) ||
                                     string.Equals(t.Name, objectType, StringComparison.Ordinal));
            return clrType is not null;
        }
        catch
        {
            clrType = null;
            return false;
        }
    }

    private static void LoadDotNetEvidenceAssemblyDependencies(string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
            return;

        var normalizedPath = NormalizeDotNetEvidenceAssemblyPath(assemblyPath);
        if (!_dotNetEvidenceAssemblyDependencyLoadCache.TryAdd(normalizedPath, true))
            return;

        foreach (var referenceName in ReadDotNetEvidenceAssemblyReferenceNames(normalizedPath))
        {
            var dependencyPath = FindDotNetEvidenceDependencyAssemblyPath(normalizedPath, referenceName);
            if (string.IsNullOrWhiteSpace(dependencyPath) || !File.Exists(dependencyPath))
                continue;

            try
            {
                Assembly.LoadFrom(dependencyPath);
            }
            catch
            {
                continue;
            }
        }
    }

    private static IReadOnlyList<string> ReadDotNetEvidenceAssemblyReferenceNames(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                return Array.Empty<string>();

            var reader = peReader.GetMetadataReader();
            var result = new List<string>();
            foreach (var handle in reader.AssemblyReferences)
            {
                var reference = reader.GetAssemblyReference(handle);
                var name = reader.GetString(reference.Name);
                if (!string.IsNullOrWhiteSpace(name))
                    result.Add(name);
            }

            return result;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string FindDotNetEvidenceDependencyAssemblyPath(string assemblyPath, string referenceName)
    {
        foreach (var path in EnumerateDotNetAssemblyEvidencePaths())
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var fileName = Path.GetFileNameWithoutExtension(path.Trim());
            if (string.Equals(fileName, referenceName, StringComparison.OrdinalIgnoreCase))
                return path;
        }

        var directory = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrWhiteSpace(directory))
            return "";

        var siblingPath = Path.Combine(directory, referenceName + ".dll");
        return File.Exists(siblingPath) ? siblingPath : "";
    }

    private static string BuildDotNetEvidenceCacheKey(string objectType, string evidenceKey, string assemblyPath)
        => objectType.Trim() + "|" + evidenceKey.Trim() + "|" + NormalizeDotNetEvidenceAssemblyPath(assemblyPath);

    private static string NormalizeDotNetEvidenceAssemblyPath(string assemblyPath)
        => string.IsNullOrWhiteSpace(assemblyPath)
            ? "<unmapped>"
            : Path.GetFullPath(assemblyPath.Trim());

    private static string FindMappedAssemblyPathForDotNetType(string objectType)
    {
        var normalizedObjectType = NormalizeDotNetObjectType(objectType ?? "");
        if (string.IsNullOrWhiteSpace(normalizedObjectType) || !HasDotNetAssemblyEvidenceSources())
            return "";

        var cacheKey = BuildDotNetTypeAssemblyEvidenceCacheKey(normalizedObjectType);
        return _dotNetTypeAssemblyPathEvidenceCache.GetOrAdd(
            cacheKey,
            _ => FindMappedAssemblyPathForDotNetTypeUncached(normalizedObjectType));
    }

    private static bool TryReadDotNetMethodParameterTypeFromMetadata(
        string objectType,
        string methodName,
        int argumentCount,
        int argumentIndex,
        out string returnType)
    {
        returnType = "";
        var visitedTypes = new HashSet<string>(StringComparer.Ordinal);
        return TryReadDotNetMethodParameterTypeFromMetadata(
            objectType,
            methodName,
            argumentCount,
            argumentIndex,
            visitedTypes,
            out returnType);
    }

    private static bool TryReadDotNetMethodParameterTypeFromMetadata(
        string objectType,
        string methodName,
        int argumentCount,
        int argumentIndex,
        HashSet<string> visitedTypes,
        out string returnType)
    {
        returnType = "";
        if (!visitedTypes.Add(objectType))
            return false;

        var assemblyPath = FindMappedAssemblyPathForDotNetType(objectType);
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return false;

        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                return false;

            var reader = peReader.GetMetadataReader();
            var provider = new DotNetMetadataTypeNameProvider();
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                if (!MetadataTypeMatches(reader, typeHandle, type, objectType))
                    continue;

                var parameterTypes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    if (!string.Equals(reader.GetString(method.Name), methodName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var signature = method.DecodeSignature(provider, null);
                    if (signature.ParameterTypes.Length != argumentCount)
                        continue;

                    var mapped = MapMetadataMethodParameterType(signature.ParameterTypes[argumentIndex]);
                    if (!string.IsNullOrWhiteSpace(mapped))
                        parameterTypes.Add(mapped);
                }

                if (parameterTypes.Count == 1)
                {
                    returnType = parameterTypes.First();
                    return true;
                }

                if (parameterTypes.Count > 1)
                    return false;

                var baseType = ReadMetadataBaseTypeName(reader, type.BaseType, provider);
                if (!string.IsNullOrWhiteSpace(baseType) &&
                    !string.Equals(baseType, "System.Object", StringComparison.Ordinal) &&
                    TryReadDotNetMethodParameterTypeFromMetadata(baseType, methodName, argumentCount, argumentIndex, visitedTypes, out returnType))
                    return true;

                return false;
            }

            return false;
        }
        catch
        {
            returnType = "";
            return false;
        }
    }

    private static string MapMetadataMethodParameterType(string parameterType)
        => TryMapClrEvidenceTypeName(parameterType, out var mapped)
            ? mapped
            : "object";

    private static string FindMappedAssemblyPathForDotNetTypeUncached(string objectType)
    {
        string bestPath = "";
        var bestLength = -1;
        foreach (var (key, path) in EnumerateDotNetAssemblyEvidenceMappings())
        {
            var normalizedKey = NormalizeDotNetReferenceMapKey(key ?? "");
            if (string.IsNullOrWhiteSpace(normalizedKey) || string.IsNullOrWhiteSpace(path))
                continue;

            var matches = string.Equals(objectType, normalizedKey, StringComparison.OrdinalIgnoreCase) ||
                          objectType.StartsWith(normalizedKey + ".", StringComparison.OrdinalIgnoreCase);
            if (matches && normalizedKey.Length > bestLength)
            {
                bestLength = normalizedKey.Length;
                bestPath = path;
            }
        }

        if (!string.IsNullOrWhiteSpace(bestPath))
            return bestPath;

        return FindMappedAssemblyPathByMetadataType(objectType);
    }

    private static string BuildDotNetTypeAssemblyEvidenceCacheKey(string objectType)
    {
        var sb = new System.Text.StringBuilder(objectType.Length + ((_dllReferenceMap.Count + _dotNetReferenceAssemblyPathMap.Count) * 32));
        sb.Append(objectType.Trim());
        foreach (var pair in EnumerateDotNetAssemblyEvidenceMappings()
                     .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append('|');
            sb.Append(NormalizeDotNetReferenceMapKey(pair.Key));
            sb.Append('=');
            sb.Append(NormalizeDotNetEvidenceAssemblyPath(pair.Value));
        }

        return sb.ToString();
    }

    private static string FindMappedAssemblyPathByMetadataType(string objectType)
    {
        foreach (var path in EnumerateDotNetAssemblyEvidencePaths())
        {
            if (!File.Exists(path))
                continue;

            try
            {
                using var stream = File.OpenRead(path);
                using var peReader = new PEReader(stream);
                if (!peReader.HasMetadata)
                    continue;

                var reader = peReader.GetMetadataReader();
                foreach (var typeHandle in reader.TypeDefinitions)
                {
                    var type = reader.GetTypeDefinition(typeHandle);
                    if (MetadataTypeMatches(reader, typeHandle, type, objectType))
                        return path;
                }
            }
            catch
            {
                continue;
            }
        }

        return "";
    }

    private static bool HasDotNetAssemblyEvidenceSources()
        => _dllReferenceMap.Count > 0 || _dotNetReferenceAssemblyPathMap.Count > 0;

    private static IEnumerable<KeyValuePair<string, string>> EnumerateDotNetAssemblyEvidenceMappings()
    {
        foreach (var pair in _dllReferenceMap)
            yield return pair;

        foreach (var pair in _dotNetReferenceAssemblyPathMap)
            yield return pair;
    }

    private static IEnumerable<string> EnumerateDotNetAssemblyEvidencePaths()
        => EnumerateDotNetAssemblyEvidenceMappings()
            .Select(pair => pair.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeDotNetReferenceMapKey(string key)
    {
        var normalized = NormalizeDotNetObjectType(key ?? "");
        if (normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return normalized[..^".dll".Length];
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return normalized[..^".exe".Length];
        return normalized;
    }

    private static bool TryReadClrMemberType(Type ownerType, string memberName, out Type? memberType)
    {
        memberType = null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.IgnoreCase;
        var property = ownerType.GetProperty(memberName, flags);
        if (property is not null)
        {
            memberType = property.PropertyType;
            return true;
        }

        var field = ownerType.GetField(memberName, flags);
        if (field is not null)
        {
            memberType = field.FieldType;
            return true;
        }

        return false;
    }

    private static bool TryMapClrEvidenceType(Type clrType, out string returnType)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (type.IsArray && type.GetElementType() is { } element)
            return TryMapClrEvidenceTypeName((element.FullName ?? element.Name) + "[]", out returnType);

        return TryMapClrEvidenceTypeName(type.FullName ?? type.Name, out returnType) ||
               (type.IsEnum || IsClrNumericType(type)) && SetReturnType("Number", out returnType);
    }

    private static bool TryMapClrEvidenceTypeName(string clrTypeName, out string returnType)
        => TryMapClrEvidenceTypeName(clrTypeName, out returnType, new HashSet<string>(StringComparer.Ordinal));

    private static bool TryMapClrEvidenceTypeName(string clrTypeName, out string returnType, HashSet<string> visitedTypes)
    {
        returnType = "";
        var normalized = clrTypeName.Trim();
        if (normalized.EndsWith("[]", StringComparison.Ordinal) &&
            TryMapClrEvidenceTypeName(normalized[..^2], out var elementType, visitedTypes) &&
            !string.Equals(elementType, "Date", StringComparison.Ordinal) &&
            !string.Equals(elementType, "Time", StringComparison.Ordinal))
            return SetReturnType(elementType + "[]", out returnType);

        if (TryMapDirectClrEvidenceTypeName(normalized, out returnType))
            return true;

        return TryMapClrEvidenceTypeNameFromMetadataBase(normalized, out returnType, visitedTypes);
    }

    private static bool TryMapDirectClrEvidenceTypeName(string normalized, out string returnType)
    {
        returnType = "";
        return normalized switch
        {
            "System.String" or "String" or "XPARuntimeCore.Box.Text" or "ENV.Data.TextColumn" => SetReturnType("Text", out returnType),
            "System.Char" or "Char" => SetReturnType("System.Char", out returnType),
            "System.Boolean" or "Boolean" or "XPARuntimeCore.Box.Bool" or "ENV.Data.BoolColumn" => SetReturnType("Bool", out returnType),
            "System.Byte[]" or "Byte[]" => SetReturnType("byte[]", out returnType),
            "XPARuntimeCore.Box.Number" or "ENV.Data.NumberColumn" => SetReturnType("Number", out returnType),
            "XPARuntimeCore.Box.Date" or "ENV.Data.DateColumn" => SetReturnType("Date", out returnType),
            "XPARuntimeCore.Box.Time" or "ENV.Data.TimeColumn" => SetReturnType("Time", out returnType),
            _ when IsClrNumericTypeName(normalized) => SetReturnType("Number", out returnType),
            _ => false
        };
    }

    private static bool TryMapClrEvidenceTypeNameFromMetadataBase(
        string normalized,
        out string returnType,
        HashSet<string> visitedTypes)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(normalized) ||
            !visitedTypes.Add(normalized))
        {
            return false;
        }

        if (_dotNetTypeScalarEvidenceCache.TryGetValue(normalized, out var cached))
            return !string.IsNullOrWhiteSpace(cached) && SetReturnType(cached, out returnType);

        if (!TryReadMetadataBaseTypeNameForClrType(normalized, out var baseType) ||
            string.IsNullOrWhiteSpace(baseType) ||
            string.Equals(baseType, "System.Object", StringComparison.Ordinal))
        {
            _dotNetTypeScalarEvidenceCache.TryAdd(normalized, "");
            return false;
        }

        if (TryMapDirectClrEvidenceTypeName(baseType, out returnType) ||
            TryMapClrEvidenceTypeName(baseType, out returnType, visitedTypes))
        {
            _dotNetTypeScalarEvidenceCache.TryAdd(normalized, returnType);
            return true;
        }

        _dotNetTypeScalarEvidenceCache.TryAdd(normalized, "");
        return false;
    }

    private static bool TryReadMetadataBaseTypeNameForClrType(string objectType, out string baseType)
    {
        baseType = "";
        var assemblyPath = FindMappedAssemblyPathForDotNetType(objectType);
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return false;

        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                return false;

            var reader = peReader.GetMetadataReader();
            var provider = new DotNetMetadataTypeNameProvider();
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                if (!MetadataTypeMatches(reader, typeHandle, type, objectType))
                    continue;

                baseType = ReadMetadataBaseTypeName(reader, type.BaseType, provider);
                return !string.IsNullOrWhiteSpace(baseType);
            }

            return false;
        }
        catch
        {
            baseType = "";
            return false;
        }
    }

    private static bool SetReturnType(string value, out string returnType)
    {
        returnType = value;
        return true;
    }

    private static bool IsClrNumericType(Type type)
        => type == typeof(byte) || type == typeof(sbyte) ||
           type == typeof(short) || type == typeof(ushort) ||
           type == typeof(int) || type == typeof(uint) ||
           type == typeof(long) || type == typeof(ulong) ||
           type == typeof(float) || type == typeof(double) ||
           type == typeof(decimal);

    private static bool IsClrNumericTypeName(string name)
        => name is "System.Byte" or "Byte" or "System.SByte" or "SByte" or
                   "System.Int16" or "Int16" or "System.UInt16" or "UInt16" or
                   "System.Int32" or "Int32" or "System.UInt32" or "UInt32" or
                   "System.Int64" or "Int64" or "System.UInt64" or "UInt64" or
                   "System.Single" or "Single" or "System.Double" or "Double" or
                   "System.Decimal" or "Decimal";

    private static bool MetadataTypeMatches(
        MetadataReader reader,
        TypeDefinitionHandle handle,
        TypeDefinition type,
        string objectType)
    {
        var fullName = ReadMetadataTypeFullName(reader, handle);
        return string.Equals(fullName, objectType, StringComparison.Ordinal) ||
               string.Equals(reader.GetString(type.Name), objectType, StringComparison.Ordinal);
    }

    private static string ReadMetadataTypeFullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var declaringType = type.GetDeclaringType();
        var typeName = reader.GetString(type.Name);
        if (!declaringType.IsNil)
        {
            var parent = ReadMetadataTypeFullName(reader, declaringType);
            return string.IsNullOrWhiteSpace(parent) ? typeName : parent + "." + typeName;
        }

        return ReadMetadataTypeFullName(reader, type.Namespace, type.Name);
    }

    private static string ReadMetadataTypeFullName(
        MetadataReader reader,
        StringHandle namespaceHandle,
        StringHandle nameHandle)
    {
        var namespaceName = reader.GetString(namespaceHandle);
        var typeName = reader.GetString(nameHandle);
        return string.IsNullOrWhiteSpace(namespaceName) ? typeName : namespaceName + "." + typeName;
    }

    private static string ReadMetadataTypeReferenceFullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        return ReadMetadataTypeFullName(reader, reference.Namespace, reference.Name);
    }

    private readonly record struct DotNetMemberEvidenceShape(string ClrTypeName);

    private sealed class DotNetMetadataTypeNameProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode)
            => typeCode switch
            {
                PrimitiveTypeCode.Boolean => "System.Boolean",
                PrimitiveTypeCode.Byte => "System.Byte",
                PrimitiveTypeCode.SByte => "System.SByte",
                PrimitiveTypeCode.Int16 => "System.Int16",
                PrimitiveTypeCode.UInt16 => "System.UInt16",
                PrimitiveTypeCode.Int32 => "System.Int32",
                PrimitiveTypeCode.UInt32 => "System.UInt32",
                PrimitiveTypeCode.Int64 => "System.Int64",
                PrimitiveTypeCode.UInt64 => "System.UInt64",
                PrimitiveTypeCode.Single => "System.Single",
                PrimitiveTypeCode.Double => "System.Double",
                PrimitiveTypeCode.String => "System.String",
                PrimitiveTypeCode.Char => "System.Char",
                PrimitiveTypeCode.Object => "System.Object",
                PrimitiveTypeCode.IntPtr => "System.IntPtr",
                PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
                PrimitiveTypeCode.Void => "System.Void",
                _ => ""
            };

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => ReadMetadataTypeFullName(reader, handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => ReadMetadataTypeReferenceFullName(reader, handle);

        public string GetSZArrayType(string elementType) => AppendArray(elementType);
        public string GetArrayType(string elementType, ArrayShape shape) => AppendArray(elementType);
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;
        public string GetByReferenceType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "System.IntPtr";
        public string GetGenericMethodParameter(object? genericContext, int index) => "";
        public string GetGenericTypeParameter(object? genericContext, int index) => "";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind)
        {
            var specification = reader.GetTypeSpecification(handle);
            return specification.DecodeSignature(this, genericContext);
        }

        private static string AppendArray(string elementType)
            => string.IsNullOrWhiteSpace(elementType) ? "" : elementType + "[]";
    }
}

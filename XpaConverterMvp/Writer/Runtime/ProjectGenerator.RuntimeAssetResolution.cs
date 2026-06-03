using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string? ResolveRuntimeCoreDllPath(string outputRoot)
    {
        if (!string.Equals(_runtimeCoreReferenceMode, "Dll", StringComparison.OrdinalIgnoreCase))
            return null;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_solutionRoot))
        {
            candidates.Add(Path.Combine(_solutionRoot, ExternalRefsDirectoryName, RuntimeExternalRefsDirectoryName, RuntimeCoreProjectName + ".dll"));
            candidates.Add(Path.Combine(_solutionRoot, ExternalRefsDirectoryName, RuntimeExternalRefsDirectoryName, LegacyRuntimeProjectName + ".dll"));
            candidates.Add(Path.Combine(_solutionRoot, RuntimeCoreProjectName + ".dll"));
            candidates.Add(Path.Combine(_solutionRoot, RuntimeCoreProjectName, RuntimeCoreProjectName + ".dll"));
            candidates.Add(Path.Combine(_solutionRoot, RuntimeCoreProjectName, LegacyRuntimeProjectName, "bin", "Debug", LegacyRuntimeProjectName + ".dll"));
            candidates.Add(Path.Combine(_solutionRoot, RuntimeCoreProjectName, LegacyRuntimeProjectName, "bin", "Release", LegacyRuntimeProjectName + ".dll"));
            candidates.Add(Path.Combine(_solutionRoot, LegacyRuntimeProjectName + ".dll"));
            candidates.Add(Path.Combine(_solutionRoot, LegacyRuntimeProjectName, LegacyRuntimeProjectName + ".dll"));
        }
        if (!string.IsNullOrWhiteSpace(_runtimeCoreDllPath))
            candidates.Add(_runtimeCoreDllPath!);
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, "bin", "Debug", RuntimeCoreProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, "bin", "Release", RuntimeCoreProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, RuntimeCoreProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, LegacyRuntimeProjectName, "bin", "Debug", LegacyRuntimeProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, LegacyRuntimeProjectName, "bin", "Release", LegacyRuntimeProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, "bin", "Debug", RuntimeCoreProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, "bin", "Release", RuntimeCoreProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, RuntimeCoreProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, "bin", "Debug", LegacyRuntimeProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, "bin", "Release", LegacyRuntimeProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, LegacyRuntimeProjectName + ".dll"));
        foreach (var baseDir in EnumerateToolSearchRoots())
        {
            candidates.Add(Path.Combine(baseDir, "assets", "runtime", LegacyRuntimeProjectName + ".dll"));
            candidates.Add(Path.Combine(baseDir, "tools", "XpaConverterMvp", "assets", "runtime", LegacyRuntimeProjectName + ".dll"));
        }

        var resolved = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(resolved))
            return null;

        return NormalizeProjectPath(Path.GetRelativePath(outputRoot, resolved));
    }

    private static string? ResolveRuntimeCompatDllPath(string outputRoot, string? runtimeCoreDllReferencePath)
    {
        if (!string.Equals(_runtimeCoreReferenceMode, "Dll", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!RuntimeNeedsLegacyCompat(outputRoot, runtimeCoreDllReferencePath))
            return null;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(runtimeCoreDllReferencePath))
        {
            var runtimeCoreResolvedPath = Path.GetFullPath(Path.Combine(outputRoot, runtimeCoreDllReferencePath));
            var siblingLegacyPath = Path.Combine(Path.GetDirectoryName(runtimeCoreResolvedPath) ?? outputRoot, LegacyRuntimeProjectName + ".dll");
            candidates.Add(siblingLegacyPath);
        }

        if (!string.IsNullOrWhiteSpace(_solutionRoot))
        {
            candidates.Add(Path.Combine(_solutionRoot, ExternalRefsDirectoryName, RuntimeExternalRefsDirectoryName, LegacyRuntimeProjectName + ".dll"));
            candidates.Add(Path.Combine(_solutionRoot, LegacyRuntimeProjectName + ".dll"));
            candidates.Add(Path.Combine(_solutionRoot, LegacyRuntimeProjectName, LegacyRuntimeProjectName + ".dll"));
        }

        if (!string.IsNullOrWhiteSpace(_runtimeCoreDllPath))
        {
            var explicitDir = Path.GetDirectoryName(_runtimeCoreDllPath!);
            if (!string.IsNullOrWhiteSpace(explicitDir))
                candidates.Add(Path.Combine(explicitDir, LegacyRuntimeProjectName + ".dll"));
        }

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, "bin", "Debug", LegacyRuntimeProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, LegacyRuntimeProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, "bin", "Debug", LegacyRuntimeProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, LegacyRuntimeProjectName + ".dll"));

        var resolved = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(resolved))
            return null;

        return NormalizeProjectPath(Path.GetRelativePath(outputRoot, resolved));
    }

    private static bool RuntimeNeedsLegacyCompat(string outputRoot, string? runtimeCoreDllReferencePath)
    {
        if (!string.IsNullOrWhiteSpace(runtimeCoreDllReferencePath))
        {
            var runtimeCoreResolvedPath = Path.GetFullPath(Path.Combine(outputRoot, runtimeCoreDllReferencePath));
            if (File.Exists(runtimeCoreResolvedPath))
                return UsesLegacyRuntimeAssemblyIdentity(runtimeCoreResolvedPath);
        }

        if (!string.IsNullOrWhiteSpace(_runtimeCoreDllPath) && File.Exists(_runtimeCoreDllPath))
            return UsesLegacyRuntimeAssemblyIdentity(_runtimeCoreDllPath!);

        return false;
    }

    private static bool UsesLegacyRuntimeAssemblyIdentity(string assemblyPath)
    {
        try
        {
            return string.Equals(
                AssemblyName.GetAssemblyName(assemblyPath).Name,
                LegacyRuntimeProjectName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static (string ColorFilePath, string FontFilePath) ResolveRuntimeThemeFiles(string sourceRoot)
    {
        var iniPath = Path.Combine(sourceRoot, "Magic.ini");
        var colorRef = "clr_rnt.eng";
        var fontRef = "fnt_rnt.eng";

        if (File.Exists(iniPath))
        {
            var ini = ParseSimpleIni(iniPath);
            if (ini.TryGetValue("RuntimeApplicationColorDefinitionFile", out var c) && !string.IsNullOrWhiteSpace(c))
                colorRef = c;
            if (ini.TryGetValue("RuntimeApplicationFontDefinitionFile", out var f) && !string.IsNullOrWhiteSpace(f))
                fontRef = f;
        }

        var colorFilePath = ResolveRuntimeAssetPath(sourceRoot, colorRef, "clr_rnt.eng");
        var fontFilePath = ResolveRuntimeAssetPath(sourceRoot, fontRef, "fnt_rnt.eng");
        return (colorFilePath, fontFilePath);
    }

    private static Dictionary<string, string> ParseSimpleIni(string iniPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(iniPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("["))
                continue;
            var p = line.IndexOf('=');
            if (p <= 0)
                continue;
            var key = line[..p].Trim();
            var value = line[(p + 1)..].Trim();
            if (key.Length == 0)
                continue;
            result[key] = value;
        }
        return result;
    }

    private static string ResolveRuntimeAssetPath(string sourceRoot, string configuredRelativePath, string fallbackFileName)
    {
        if (!string.IsNullOrWhiteSpace(configuredRelativePath))
        {
            var sanitized = configuredRelativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var fileName = Path.GetFileName(sanitized);

            var direct = Path.Combine(sourceRoot, sanitized);
            if (File.Exists(direct))
                return direct;

            var inRootByName = Path.Combine(sourceRoot, fileName);
            if (File.Exists(inRootByName))
                return inRootByName;

            var supportDir = Path.Combine(sourceRoot, "SUPPORT");
            var inSupport = Path.Combine(supportDir, fileName);
            if (File.Exists(inSupport))
                return inSupport;
        }

        var fallbackInRoot = Path.Combine(sourceRoot, fallbackFileName);
        if (File.Exists(fallbackInRoot))
            return fallbackInRoot;
        return fallbackInRoot;
    }
}


using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteFunctionMappingReport(ProjectSemantic parsed, string outputRoot, string appNamespace)
    {
        var tasks = parsed.Tasks.Where(ShouldGenerateForTarget).ToList();
        var expressions = tasks
            .SelectMany(t => t.Expressions.Select(e => e.Syntax))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rx = new Regex(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);
        foreach (var expr in expressions)
        {
            foreach (Match m in rx.Matches(expr))
            {
                var fn = m.Groups[1].Value;
                if (string.Equals(fn, "u", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!counts.ContainsKey(fn))
                    counts[fn] = 0;
                counts[fn]++;
            }
        }

        var userMethods = GetUserMethodsPublicNames();
        var componentFunctions = parsed.ComponentFunctions
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => string.Join(", ",
                    g.Select(x => x.ComponentName)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine($"# Functions De/Para Report - {appNamespace}");
        sb.AppendLine();
        sb.AppendLine($"Expressions analisadas: {expressions.Count}");
        sb.AppendLine($"Funcoes detectadas: {counts.Count}");
        sb.AppendLine($"Entradas no catalogo de/para: {_xpaFunctionMap.Count}");
        sb.AppendLine();
        sb.AppendLine("| Function | Count | Status | De/Para |");
        sb.AppendLine("|---|---:|---|---|");

        foreach (var kv in counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var fn = kv.Key;
            var mapped = _xpaFunctionMap.TryGetValue(fn, out var targetValue);
            var target = targetValue ?? "";
            var targetMethod = mapped && target.StartsWith("u.", StringComparison.Ordinal) ? target.Substring(2) : "";
            var existsInRuntime = !string.IsNullOrWhiteSpace(targetMethod) && userMethods.Contains(targetMethod);
            var isComponentFunction = componentFunctions.TryGetValue(fn, out var componentName);
            var status = mapped
                ? (string.IsNullOrWhiteSpace(targetMethod) ? "Mapped (special)" : existsInRuntime ? "Mapped + Runtime" : "Mapped (runtime unknown)")
                : userMethods.Contains(fn) ? "Runtime only (pending map)"
                : isComponentFunction ? $"ComponentFunc ({componentName})"
                : "Unknown";
            var dePara = mapped ? target : userMethods.Contains(fn) ? $"u.{fn}" : isComponentFunction ? $"{componentName}.{fn}" : "-";
            sb.AppendLine($"| `{fn}` | {kv.Value} | {status} | `{dePara}` |");
        }

        var unknown = counts.Keys
            .Where(fn => !_xpaFunctionMap.ContainsKey(fn) && !userMethods.Contains(fn))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        sb.AppendLine();
        sb.AppendLine("## Pending Review");
        if (unknown.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var fn in unknown)
                sb.AppendLine($"- `{fn}`");

        var catalogWithoutRuntime = _xpaFunctionMap
            .Where(kv => kv.Value.StartsWith("u.", StringComparison.Ordinal))
            .Select(kv => kv.Value.Substring(2))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(m => !userMethods.Contains(m))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        sb.AppendLine();
        sb.AppendLine("## Catalogo sem metodo publico no UserMethods");
        if (catalogWithoutRuntime.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var m in catalogWithoutRuntime)
                sb.AppendLine($"- `{m}`");

        File.WriteAllText(Path.Combine(outputRoot, "FUNCTIONS_DE_PARA_REPORT.md"), sb.ToString());
    }

    private static void WriteDotNetComponentReferenceReport(ProjectSemantic parsed, string outputRoot)
    {
        if (parsed.DotNetComponentReferences.Count == 0)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("# .NET Component References");
        sb.AppendLine();
        sb.AppendLine("| Name | Folder | Assembly | Path | UseSpecificVersion |");
        sb.AppendLine("|---|---|---|---|---|");

        foreach (var reference in parsed.DotNetComponentReferences
                     .OrderBy(r => r.Folder ?? "", StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("| ")
              .Append(EscapeMarkdownCell(reference.Name))
              .Append(" | ")
              .Append(EscapeMarkdownCell(reference.Folder))
              .Append(" | ")
              .Append(EscapeMarkdownCell(reference.AssemblyName))
              .Append(" | ")
              .Append(EscapeMarkdownCell(reference.AssemblyPath))
              .Append(" | ")
              .Append(EscapeMarkdownCell(reference.UseSpecificVersion))
              .AppendLine(" |");
        }

        File.WriteAllText(Path.Combine(outputRoot, "DOTNET_COMPONENT_REFERENCES.md"), sb.ToString());
    }

    private static void WriteExternalMagicComponentPlaceholders(ProjectSemantic parsed, string outputRoot)
    {
        var componentsToPlaceholder = parsed.ExternalMagicComponents
            .Where(c => !_projectReferenceMap.ContainsKey(c.Name) && !_dllReferenceMap.ContainsKey(c.Name))
            .ToList();

        if (componentsToPlaceholder.Count == 0)
            return;

        var report = new StringBuilder();
        report.AppendLine("# External Magic Component Placeholders");
        report.AppendLine();

        foreach (var component in componentsToPlaceholder.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var componentDir = Path.Combine(outputRoot, "External", component.Name);
            Directory.CreateDirectory(componentDir);

            WriteExternalMagicRolesPlaceholder(component, componentDir);
            WriteExternalMagicProgramsPlaceholder(component, componentDir);
            WriteExternalMagicEntitiesPlaceholder(component, componentDir);

            report.AppendLine($"## {component.Name}");
            if (!string.IsNullOrWhiteSpace(component.Description))
                report.AppendLine(component.Description);
            report.AppendLine();
            report.AppendLine($"- Tabelas auxiliares: {(component.HasTablesXml ? "sim" : "nao")}");
            report.AppendLine($"- DataObjects: {component.DataObjects.Count}");
            report.AppendLine($"- Programs: {component.Programs.Count}");
            report.AppendLine($"- Rights: {component.Rights.Count}");
            report.AppendLine();
        }

        File.WriteAllText(Path.Combine(outputRoot, "EXTERNAL_MAGIC_COMPONENTS.md"), report.ToString(), Encoding.UTF8);
    }

    private static void WriteExternalMagicRolesPlaceholder(ExternalMagicComponentDef component, string componentDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using ENV.Security;");
        sb.AppendLine($"namespace {component.Name};");
        sb.AppendLine();
        sb.AppendLine("internal class Roles");
        sb.AppendLine("{");
        sb.AppendLine("    static Roles()");
        sb.AppendLine("    {");
        sb.AppendLine("        Administrator.ApplyAdminTo(typeof(Roles));");
        sb.AppendLine("    }");
        foreach (var right in component.Rights.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var member = ToRoleMemberIdentifier(right);
            sb.AppendLine($"    public static readonly Role {member} = new Role(\"{Escape(right)}\", \"{Escape(right)}\", false);");
        }
        sb.AppendLine("    internal static readonly Role.Administrator Administrator = new Role.Administrator(\"Administrator\", \"Administrator\");");
        sb.AppendLine("}");
        WriteGeneratedSourceFile(componentDir, "Roles", sb.ToString(), Encoding.UTF8);
    }

    private static void WriteExternalMagicProgramsPlaceholder(ExternalMagicComponentDef component, string componentDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"namespace {component.Name};");
        sb.AppendLine();
        sb.AppendLine("internal static class ExternalPrograms");
        sb.AppendLine("{");
        foreach (var program in component.Programs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var member = ToCodeIdentifierPreservingCase(program);
            sb.AppendLine($"    internal static object? {member}(params object?[] args) => throw new System.NotImplementedException(\"External component program placeholder: {Escape(component.Name)}.{Escape(program)}\");");
        }
        sb.AppendLine("}");
        WriteGeneratedSourceFile(componentDir, "ExternalPrograms", sb.ToString(), Encoding.UTF8);
    }

    private static void WriteExternalMagicEntitiesPlaceholder(ExternalMagicComponentDef component, string componentDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"namespace {component.Name};");
        sb.AppendLine();
        sb.AppendLine("internal static class ExternalEntities");
        sb.AppendLine("{");
        foreach (var entity in component.DataObjects.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var member = ToCodeIdentifierPreservingCase(entity);
            sb.AppendLine($"    internal const string {member} = \"{Escape(entity)}\";");
        }
        sb.AppendLine("}");
        WriteGeneratedSourceFile(componentDir, "ExternalEntities", sb.ToString(), Encoding.UTF8);
    }

    private static void WriteProjectFile(ProjectSemantic parsed, string outputRoot, string appNamespace, bool includeFullManifest = true)
    {
        var projectName = appNamespace.Split('.').FirstOrDefault() ?? "Generated";
        var runtimeBoxProjectPath = ResolveRuntimeBoxProjectPath(outputRoot);
        var envProjectPath = ResolveEnvProjectPath(outputRoot);
        var sb = new StringBuilder();

        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine($"    <OutputType>{_outputType}</OutputType>");
        sb.AppendLine("    <TargetFramework>net472</TargetFramework>");
        sb.AppendLine("    <UseWindowsForms>true</UseWindowsForms>");
        sb.AppendLine($"    <RootNamespace>{EscapeXml(projectName)}</RootNamespace>");
        sb.AppendLine($"    <AssemblyName>{EscapeXml(projectName)}</AssemblyName>");
        sb.AppendLine("    <LangVersion>latest</LangVersion>");
        // Large XPA applications can exceed the CLR user-string heap limit in
        // a single generated assembly. Roslyn's data-section string literal
        // mode keeps those projects compilable without changing source values.
        // The default threshold only moves literals with at least 100 characters.
        // Large XPA projects also contain enough short literals to overflow #US,
        // so make every non-empty literal eligible for the data section.
        sb.AppendLine("    <Features>experimental-data-section-string-literals=0</Features>");
        sb.AppendLine("    <Nullable>disable</Nullable>");
        sb.AppendLine("    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>");
        sb.AppendLine("    <NoWarn>1587;1570;1591;1573</NoWarn>");
        sb.AppendLine("    <PlatformTarget>AnyCPU</PlatformTarget>");
        sb.AppendLine("    <Prefer32Bit>false</Prefer32Bit>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();

        sb.AppendLine("  <ItemGroup>");
        if (string.Equals(_runtimeCoreReferenceMode, "Project", StringComparison.OrdinalIgnoreCase) && File.Exists(runtimeBoxProjectPath))
        {
            var relativeRuntimeBoxProject = NormalizeProjectPath(Path.GetRelativePath(outputRoot, runtimeBoxProjectPath));
            sb.AppendLine($"    <ProjectReference Include=\"{EscapeXml(relativeRuntimeBoxProject)}\" />");
        }

        if (string.Equals(_runtimeCoreReferenceMode, "Project", StringComparison.OrdinalIgnoreCase) && File.Exists(envProjectPath))
        {
            var relativeEnvProject = NormalizeProjectPath(Path.GetRelativePath(outputRoot, envProjectPath));
            if (!string.Equals(
                    Path.GetFullPath(runtimeBoxProjectPath),
                    Path.GetFullPath(envProjectPath),
                    StringComparison.OrdinalIgnoreCase))
                sb.AppendLine($"    <ProjectReference Include=\"{EscapeXml(relativeEnvProject)}\" />");
        }

        if (_isComponentized && string.IsNullOrWhiteSpace(_targetComponent))
        {
            var parentDir = ResolveProjectContainerRoot(outputRoot);
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                foreach (var component in parsed.Components.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var componentProjectPath = Path.Combine(parentDir, component, component + ".csproj");
                    var relativeProjectPath = NormalizeProjectPath(Path.GetRelativePath(outputRoot, componentProjectPath));
                    sb.AppendLine($"    <ProjectReference Include=\"{EscapeXml(relativeProjectPath)}\" Condition=\"Exists('{EscapeXml(relativeProjectPath)}')\" />");
                }
            }
        }

        foreach (var projectReference in _projectReferenceMap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!ShouldIncludeExternalProjectReference(projectReference.Key, parsed))
                continue;

            var relativeProjectPath = NormalizeProjectPath(Path.GetRelativePath(outputRoot, projectReference.Value));
            sb.AppendLine($"    <ProjectReference Include=\"{EscapeXml(relativeProjectPath)}\" Condition=\"Exists('{EscapeXml(relativeProjectPath)}')\" />");
        }
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine();

        // Preserve WinForms design-time behavior in SDK-style projects. The first
        // rule makes every generated view open with the Form Designer; the second
        // nests the generated Designer file below its code-behind in Solution Explorer.
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine("    <Compile Update=\"**\\Views\\*.cs\">");
        sb.AppendLine("      <SubType>Form</SubType>");
        sb.AppendLine("    </Compile>");
        sb.AppendLine("    <Compile Update=\"**\\Views\\*.Designer.cs\">");
        sb.AppendLine("      <DependentUpon>$([System.String]::Copy('%(Filename)').Replace('.Designer', '')).cs</DependentUpon>");
        sb.AppendLine("    </Compile>");
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine();

        var assemblyReferences = BuildDotNetAssemblyReferences(parsed, outputRoot);
        var seenAssemblyIncludes = new HashSet<string>(
            assemblyReferences.Select(r => r.Include),
            StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(_targetComponent))
        {
            foreach (var dllReference in _dllReferenceMap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                var hintPath = ResolveExternalComponentDllHintPath(outputRoot, dllReference.Key, dllReference.Value);
                var include = ResolveExternalComponentDllInclude(outputRoot, dllReference.Key, dllReference.Value, hintPath);
                if (!string.IsNullOrWhiteSpace(include) &&
                    !string.IsNullOrWhiteSpace(hintPath) &&
                    seenAssemblyIncludes.Add(include))
                    assemblyReferences.Add((include, hintPath, false));
            }
        }
        var runtimeCoreDllReferencePath = ResolveRuntimeCoreDllPath(outputRoot);
        if (!string.IsNullOrWhiteSpace(runtimeCoreDllReferencePath))
        {
            var runtimeCoreInclude = TryResolveAssemblyNameFromHintPath(outputRoot, runtimeCoreDllReferencePath) ?? RuntimeCoreProjectName;
            if (seenAssemblyIncludes.Add(runtimeCoreInclude))
                assemblyReferences.Insert(0, (runtimeCoreInclude, runtimeCoreDllReferencePath, false));
        }
        var runtimeCompatDllReferencePath = ResolveRuntimeCompatDllPath(outputRoot, runtimeCoreDllReferencePath);
        if (UsesJsonCompat(parsed) && !assemblyReferences.Any(r => string.Equals(r.Include, "Newtonsoft.Json", StringComparison.OrdinalIgnoreCase)))
        {
            var newtonsoftHintPath = ResolveNewtonsoftJsonHintPath(outputRoot);
            if (!string.IsNullOrWhiteSpace(newtonsoftHintPath))
                assemblyReferences.Add(("Newtonsoft.Json", newtonsoftHintPath, false));
        }
        if (UsesExternalTypeCompat(parsed) && !assemblyReferences.Any(r => string.Equals(r.Include, "Microsoft.CSharp", StringComparison.OrdinalIgnoreCase)))
            assemblyReferences.Add(("Microsoft.CSharp", null, false));
        if (assemblyReferences.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var reference in assemblyReferences)
            {
                sb.AppendLine($"    <Reference Include=\"{EscapeXml(reference.Include)}\">");
                if (!string.IsNullOrWhiteSpace(reference.HintPath))
                    sb.AppendLine($"      <HintPath>{EscapeXml(reference.HintPath)}</HintPath>");
                if (reference.SpecificVersion.HasValue)
                    sb.AppendLine($"      <SpecificVersion>{(reference.SpecificVersion.Value ? "True" : "False")}</SpecificVersion>");
                if (IsDesignerFrameworkReference(reference.Include))
                    sb.AppendLine("      <Private>True</Private>");
                sb.AppendLine("    </Reference>");
            }
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine();
        }

        if (UsesOfficeWordInterop(parsed))
        {
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <PackageReference Include=\"Microsoft.Office.Interop.Word\" Version=\"15.0.4797.1004\" />");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine();
        }

        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine("    <None Update=\"*.xpa-manifest.json\">");
        sb.AppendLine("      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
        sb.AppendLine("    </None>");
        sb.AppendLine("    <None Update=\"*.ini\">");
        sb.AppendLine("      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
        sb.AppendLine("    </None>");
        sb.AppendLine("    <None Update=\"security\">");
        sb.AppendLine("      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
        sb.AppendLine("    </None>");
        sb.AppendLine("    <None Include=\"*.sqlite\">");
        sb.AppendLine("      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
        sb.AppendLine("    </None>");
        sb.AppendLine("    <None Include=\"*.SQLite\">");
        sb.AppendLine("      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
        sb.AppendLine("    </None>");
        if (!string.IsNullOrWhiteSpace(runtimeCompatDllReferencePath))
        {
            sb.AppendLine($"    <None Include=\"{EscapeXml(runtimeCompatDllReferencePath)}\">");
            sb.AppendLine("      <Link>ENV.dll</Link>");
            sb.AppendLine("      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
            sb.AppendLine("    </None>");
        }
        if (!string.IsNullOrWhiteSpace(_solutionRoot))
        {
            var runtimeRefsRelative = NormalizeProjectPath(Path.GetRelativePath(
                outputRoot,
                Path.Combine(_solutionRoot, ExternalRefsDirectoryName, RuntimeExternalRefsDirectoryName)));
            var runtimeRefsPath = EscapeXml(runtimeRefsRelative);
            var runtimeSupportDllExcludes = string.Join(";", new[]
            {
                LegacyRuntimeProjectName + ".dll",
                RuntimeCoreProjectName + ".dll",
                RuntimeBoxAssemblyName + ".dll",
                "System.Data.SQLite.dll",
                "SQLite.Interop.dll",
                "sqlite3.dll"
            }.Select(fileName => runtimeRefsPath + "/" + fileName));
            sb.AppendLine($"    <None Include=\"{runtimeRefsPath}/*.dll\" Exclude=\"{runtimeSupportDllExcludes}\" Condition=\"Exists('{runtimeRefsPath}')\">");
            sb.AppendLine("      <Link>%(Filename)%(Extension)</Link>");
            sb.AppendLine("      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
            sb.AppendLine("    </None>");
            sb.AppendLine($"    <None Include=\"{runtimeRefsPath}/Gateways/**/*\" Condition=\"Exists('{runtimeRefsPath}/Gateways')\">");
            sb.AppendLine("      <Link>Gateways\\%(RecursiveDir)%(Filename)%(Extension)</Link>");
            sb.AppendLine("      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
            sb.AppendLine("    </None>");
            sb.AppendLine($"    <None Include=\"{runtimeRefsPath}/sqlite3.dll\" Condition=\"Exists('{runtimeRefsPath}/sqlite3.dll')\">");
            sb.AppendLine("      <Link>sqlite3.dll</Link>");
            sb.AppendLine("      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
            sb.AppendLine("    </None>");
            sb.AppendLine($"    <None Include=\"{runtimeRefsPath}/System.Data.SQLite.dll\" Condition=\"Exists('{runtimeRefsPath}/System.Data.SQLite.dll')\">");
            sb.AppendLine("      <Link>System.Data.SQLite.dll</Link>");
            sb.AppendLine("      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
            sb.AppendLine("    </None>");
            sb.AppendLine($"    <None Include=\"{runtimeRefsPath}/SQLite.Interop.dll\" Condition=\"Exists('{runtimeRefsPath}/SQLite.Interop.dll')\">");
            sb.AppendLine("      <Link>SQLite.Interop.dll</Link>");
            sb.AppendLine("      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
            sb.AppendLine("    </None>");
            sb.AppendLine("    <None Include=\"../*.sqlite\">");
            sb.AppendLine("      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
            sb.AppendLine("    </None>");
            sb.AppendLine("    <None Include=\"../*.SQLite\">");
            sb.AppendLine("      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
            sb.AppendLine("    </None>");
        }
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");

        File.WriteAllText(Path.Combine(outputRoot, projectName + ".csproj"), sb.ToString(), Encoding.UTF8);
        WriteAppConfigIfNeeded(outputRoot);
        if (includeFullManifest)
            WriteProjectManifest(parsed, outputRoot, appNamespace, projectName);
    }

    private static bool UsesOfficeWordInterop(ProjectSemantic parsed)
        => parsed.DotNetComponentReferences.Any(reference =>
            string.Equals(reference.Name, "Microsoft.Office.Interop.Word", StringComparison.OrdinalIgnoreCase));

    private static bool ShouldIncludeExternalProjectReference(string componentName, ProjectSemantic parsed)
    {
        if (string.IsNullOrWhiteSpace(componentName))
            return false;

        if (!string.IsNullOrWhiteSpace(_targetComponent))
            return !string.Equals(componentName, _targetComponent, StringComparison.OrdinalIgnoreCase);

        return true;
    }

    private static void WriteAppConfigIfNeeded(string outputRoot)
    {
        if (!string.Equals(_outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
            return;

        var bindingRedirects = ResolveRuntimeBindingRedirects(outputRoot);
        if (bindingRedirects.Count == 0)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<configuration>");
        sb.AppendLine("  <runtime>");
        sb.AppendLine("    <assemblyBinding xmlns=\"urn:schemas-microsoft-com:asm.v1\">");
        foreach (var bindingRedirect in bindingRedirects.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine("      <dependentAssembly>");
            sb.AppendLine($"        <assemblyIdentity name=\"{EscapeXml(bindingRedirect.Name)}\" publicKeyToken=\"{EscapeXml(bindingRedirect.PublicKeyToken)}\" culture=\"neutral\" />");
            sb.AppendLine($"        <bindingRedirect oldVersion=\"0.0.0.0-9.9.9.9\" newVersion=\"{EscapeXml(bindingRedirect.Version)}\" />");
            sb.AppendLine("      </dependentAssembly>");
        }
        sb.AppendLine("    </assemblyBinding>");
        sb.AppendLine("  </runtime>");
        sb.AppendLine("</configuration>");

        File.WriteAllText(Path.Combine(outputRoot, "App.config"), sb.ToString(), Encoding.UTF8);
    }

    private static List<(string Name, string PublicKeyToken, string Version)> ResolveRuntimeBindingRedirects(string outputRoot)
    {
        var redirects = new Dictionary<string, (string Name, string PublicKeyToken, string Version)>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in EnumerateRuntimeBindingRedirectCandidates(outputRoot))
        {
            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(candidate);
                if (string.IsNullOrWhiteSpace(assemblyName.Name) || assemblyName.Version is null)
                    continue;
                if (!ShouldEmitRuntimeBindingRedirect(assemblyName.Name))
                    continue;

                if (!redirects.ContainsKey(assemblyName.Name))
                {
                    redirects[assemblyName.Name] = (
                        assemblyName.Name,
                        ToPublicKeyTokenString(assemblyName),
                        assemblyName.Version.ToString());
                }
            }
            catch
            {
            }
        }

        return redirects.Values.ToList();
    }

    private static IEnumerable<string> EnumerateRuntimeBindingRedirectCandidates(string outputRoot)
    {
        var directories = new List<string>();
        if (!string.IsNullOrWhiteSpace(_solutionRoot))
            directories.Add(Path.Combine(_solutionRoot, ExternalRefsDirectoryName, RuntimeExternalRefsDirectoryName));

        var containerRoot = ResolveProjectContainerRoot(outputRoot);
        if (!string.IsNullOrWhiteSpace(containerRoot))
            directories.Add(Path.Combine(containerRoot, ExternalRefsDirectoryName, RuntimeExternalRefsDirectoryName));

        directories.Add(Path.Combine(Directory.GetCurrentDirectory(), "lib"));

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            foreach (var file in Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
                yield return file;
        }
    }

    private static bool ShouldEmitRuntimeBindingRedirect(string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return false;
        if (string.Equals(assemblyName, LegacyRuntimeProjectName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assemblyName, RuntimeCoreProjectName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assemblyName, RuntimeBoxAssemblyName, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static string ToPublicKeyTokenString(AssemblyName assemblyName)
    {
        var token = assemblyName.GetPublicKeyToken();
        if (token is null || token.Length == 0)
            return "null";

        var sb = new StringBuilder(token.Length * 2);
        foreach (var b in token)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string? ResolveSystemDataSqliteAssemblyVersion(string outputRoot)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_solutionRoot))
        {
            candidates.Add(Path.Combine(_solutionRoot, ExternalRefsDirectoryName, RuntimeExternalRefsDirectoryName, "System.Data.SQLite.dll"));
            candidates.Add(Path.Combine(_solutionRoot, ExternalRefsDirectoryName, RuntimeExternalRefsDirectoryName, "lib", "System.Data.SQLite.dll"));
            candidates.Add(Path.Combine(_solutionRoot, "System.Data.SQLite.dll"));
            candidates.Add(Path.Combine(_solutionRoot, "lib", "System.Data.SQLite.dll"));
        }

        var containerRoot = ResolveProjectContainerRoot(outputRoot);
        if (!string.IsNullOrWhiteSpace(containerRoot))
        {
            candidates.Add(Path.Combine(containerRoot, ExternalRefsDirectoryName, RuntimeExternalRefsDirectoryName, "System.Data.SQLite.dll"));
            candidates.Add(Path.Combine(containerRoot, ExternalRefsDirectoryName, RuntimeExternalRefsDirectoryName, "lib", "System.Data.SQLite.dll"));
            candidates.Add(Path.Combine(containerRoot, "System.Data.SQLite.dll"));
            candidates.Add(Path.Combine(containerRoot, "lib", "System.Data.SQLite.dll"));
        }

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "lib", "System.Data.SQLite.dll"));

        foreach (var candidate in candidates.Where(File.Exists))
        {
            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(candidate);
                if (assemblyName.Version is not null)
                    return assemblyName.Version.ToString();
            }
            catch
            {
            }
        }

        return null;
    }

    private static void WriteProjectManifest(ProjectSemantic parsed, string outputRoot, string appNamespace, string projectName)
    {
        var manifest = new ProjectManifest
        {
            ComponentName = string.IsNullOrWhiteSpace(_targetComponent) ? projectName : _targetComponent!,
            Namespace = appNamespace
        };

        foreach (var task in parsed.Tasks
                     .Where(ShouldGenerateForTarget)
                     .Where(t => t.ParentOrdinal is null && !t.MainProgram))
        {
            var publicName = task.PublicName ?? task.Description;
            if (string.IsNullOrWhiteSpace(publicName))
                continue;
            var className = ResolveTaskClassName(task, parsed.Tasks);
            manifest.Programs[publicName] = className;
            if (task.TopLevelProgramIndex.HasValue)
                manifest.ProgramsByIndex[task.TopLevelProgramIndex.Value] = className;
            manifest.ProgramDetails.Add(new ProjectManifestProgram
            {
                ObjectIndex = task.TopLevelProgramIndex,
                ClassName = className,
                PublicName = task.PublicName,
                IsPublic = IsPublicTopLevelTask(task),
                ParameterTypes = GetTaskParameters(task).Select(p => p.ParameterType).ToList()
            });
        }

        foreach (var dataObject in parsed.DataObjects.Where(ShouldGenerateForTarget))
        {
            var publicName = dataObject.PublicName ?? dataObject.Name;
            if (string.IsNullOrWhiteSpace(publicName))
                continue;
            manifest.DataObjects[publicName] = ResolveDataObjectTypeName(dataObject);
            manifest.DataObjectDetails.Add(new ProjectManifestDataObject
            {
                ObjectIndex = dataObject.Ordinal,
                Name = dataObject.Name,
                PhysicalName = dataObject.PhysicalName,
                Comment = dataObject.Comment,
                Resident = dataObject.Resident,
                PublicName = dataObject.PublicName,
                Owner = dataObject.Owner,
                DataSource = dataObject.DataSource,
                Columns = dataObject.Columns.Select(c => new ProjectManifestDataColumn
                {
                    Id = c.Id,
                    Name = c.Name,
                    AttrObj = c.AttrObj,
                    Picture = c.Picture,
                    FieldPhysicalName = c.FieldPhysicalName,
                    FieldPhysicalPicture = c.FieldPhysicalPicture,
                    FieldPhysicalSize = c.FieldPhysicalSize,
                    AllowedNull = c.AllowedNull,
                    Attribute = c.Attribute,
                    ContextCookies = c.ContextCookies,
                    DatabaseDefinition = c.DatabaseDefinition,
                    Storage = c.Storage,
                    Translate = c.Translate,
                    DbColumnName = c.DbColumnName,
                    DbType = c.DbType,
                    ModelRefObj = c.ModelRefObj
                }).ToList(),
                Indexes = dataObject.Indexes.Select(i => new ProjectManifestDataIndex
                {
                    Id = i.Id,
                    Name = i.Name,
                    Unique = i.Unique,
                    Primary = i.Primary,
                    Segments = i.Segments.Select(s => new ProjectManifestIndexSegment
                    {
                        ColumnId = s.ColumnId,
                        Order = s.Order
                    }).ToList()
                }).ToList()
            });
            manifest.DataObjectsByIndex[dataObject.Ordinal] = ResolveDataObjectTypeName(dataObject);
        }

        foreach (var right in parsed.Rights)
        {
            if (string.IsNullOrWhiteSpace(right.Name))
                continue;
            manifest.Rights[right.Name] = ToRoleMemberIdentifier(right.Name);
        }

        foreach (var fn in CollectComponentFunctionExports(parsed))
        {
            manifest.Functions[fn.FunctionName] = new ProjectManifestFunction
            {
                ClassName = "ComponentFunctions",
                MethodName = fn.MethodName,
                ReturnType = fn.ReturnType,
                ParameterTypes = fn.Parameters.Select(p => p.Type).ToList()
            };
        }

        var manifestPath = Path.Combine(outputRoot, projectName + ".xpa-manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static void WriteProjectPropertiesAssets(string propertiesDir, string appNamespace)
    {
        var projectName = appNamespace.Split('.').FirstOrDefault() ?? appNamespace;
        WriteAssemblyInfo(propertiesDir, projectName);
        WriteResourcesResx(propertiesDir);
        WriteResourcesDesigner(propertiesDir, appNamespace, CollectProjectResourceNames());
    }

    private static void WriteAssemblyInfo(string propertiesDir, string projectName)
    {
        var guid = DeterministicGuidFor(projectName).ToString().ToUpperInvariant();
        File.WriteAllText(Path.Combine(propertiesDir, "AssemblyInfo.cs"), $@"using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle(""{Escape(projectName)}"")]
[assembly: AssemblyDescription("""")]
[assembly: AssemblyConfiguration("""")]
[assembly: AssemblyCompany(""XPA Migration"")]
#if DEBUG
[assembly: AssemblyProduct(""{Escape(projectName)} Debug"")]
#else
[assembly: AssemblyProduct(""{Escape(projectName)} Release"")]
#endif
[assembly: AssemblyCopyright(""Copyright XPA Migration {DateTime.Now.Year}"")]
[assembly: AssemblyTrademark("""")]
[assembly: AssemblyCulture("""")]
[assembly: ComVisible(false)]
[assembly: Guid(""{guid}"")]
[assembly: AssemblyVersion(""1.0.0.0"")]
[assembly: AssemblyFileVersion(""1.0.0.0"")]
");
    }

    private static void WriteResourcesResx(string propertiesDir)
    {
        File.WriteAllText(Path.Combine(propertiesDir, "Resources.resx"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<root>
  <resheader name=""resmimetype"">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name=""version"">
    <value>2.0</value>
  </resheader>
  <resheader name=""reader"">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name=""writer"">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
</root>");
    }

    private static void WriteResourcesDesigner(string propertiesDir, string appNamespace, IReadOnlyList<string> resourceNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine($"namespace {appNamespace}.Properties;");
        sb.AppendLine();
        sb.AppendLine("using System.Drawing;");
        sb.AppendLine("using System.Globalization;");
        sb.AppendLine("using System.Resources;");
        sb.AppendLine();
        sb.AppendLine("public class Resources");
        sb.AppendLine("{");
        sb.AppendLine("    static ResourceManager resourceMan;");
        sb.AppendLine("    static CultureInfo resourceCulture;");
        sb.AppendLine();
        sb.AppendLine("    internal Resources()");
        sb.AppendLine("    {");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static ResourceManager ResourceManager");
        sb.AppendLine("    {");
        sb.AppendLine("        get");
        sb.AppendLine("        {");
        sb.AppendLine("            if (resourceMan == null)");
        sb.AppendLine($"                resourceMan = new ResourceManager(\"{appNamespace}.Properties.Resources\", typeof(Resources).Assembly);");
        sb.AppendLine("            return resourceMan;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static CultureInfo Culture");
        sb.AppendLine("    {");
        sb.AppendLine("        get => resourceCulture;");
        sb.AppendLine("        set => resourceCulture = value;");
        sb.AppendLine("    }");
        sb.AppendLine();
        foreach (var resourceName in resourceNames)
        {
            sb.AppendLine($"    public static Bitmap {resourceName} => ResourceManager.GetObject(\"{resourceName}\", resourceCulture) as Bitmap;");
        }
        sb.AppendLine("}");

        File.WriteAllText(Path.Combine(propertiesDir, "Resources.Designer.cs"), sb.ToString());
    }

    private static IReadOnlyList<string> CollectProjectResourceNames()
    {
        return new[]
        {
            "BrowseMode",
            "Copy",
            "Help",
            "Screen",
            "UnknownImage",
            "CustomSort",
            "Cut",
            "Delete",
            "Filter",
            "Find",
            "FindNext",
            "First",
            "ImportExportData",
            "InsertMode",
            "Last",
            "Next",
            "PageDown",
            "PageUp",
            "Paste",
            "Preview",
            "Previous",
            "Printer",
            "PrintersWindow",
            "SelectSort",
            "Undo",
            "UpdateMode"
        };
    }

    private static Guid DeterministicGuidFor(string value)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("properties:" + value));
        return new Guid(hash);
    }

    private static string EscapeMarkdownCell(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    }

    private static Dictionary<string, string> BuildDotNetReferenceAssemblyPathMap(ProjectSemantic parsed, string outputRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (parsed.DotNetComponentReferences.Count == 0)
            return result;

        foreach (var reference in parsed.DotNetComponentReferences
                     .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var hintPath = ResolveReferenceHintPath(reference, outputRoot);
            if (!IsReferenceHintPathMaterialized(outputRoot, hintPath) &&
                _dllReferenceMap.TryGetValue(reference.Name, out var mappedDllPath))
            {
                hintPath = ResolveMappedDotNetReferenceHintPath(outputRoot, reference.Name, mappedDllPath);
            }

            var fullPath = ResolveReferenceHintPathToFullPath(outputRoot, hintPath);
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                continue;

            AddDotNetReferenceAssemblyPathKey(result, reference.Name, fullPath);
            AddDotNetReferenceAssemblyPathKey(result, reference.AssemblyName, fullPath);
            AddDotNetReferenceAssemblyPathKey(result, StripAssemblyDisplayName(reference.AssemblyName), fullPath);
            AddDotNetReferenceAssemblyPathKey(result, Path.GetFileNameWithoutExtension(fullPath), fullPath);

            var resolvedAssemblyName = TryResolveAssemblyNameFromFullPath(fullPath);
            AddDotNetReferenceAssemblyPathKey(result, resolvedAssemblyName, fullPath);
        }

        if (result.Count > 0)
        {
            var uniquePaths = result.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            ConversionTelemetry.Log(
                "DOTNET_METADATA",
                $"resolvedReferencePaths={uniquePaths} keys={result.Count} sourceReferences={parsed.DotNetComponentReferences.Count}");
        }

        return result;
    }

    private static void AddDotNetReferenceAssemblyPathKey(Dictionary<string, string> result, string? key, string fullPath)
    {
        var normalizedKey = NormalizeDotNetReferenceMapKey(key ?? "");
        if (string.IsNullOrWhiteSpace(normalizedKey) || string.IsNullOrWhiteSpace(fullPath))
            return;

        result.TryAdd(normalizedKey, fullPath);
    }

    private static string? ResolveReferenceHintPathToFullPath(string outputRoot, string? hintPath)
    {
        if (string.IsNullOrWhiteSpace(hintPath))
            return null;

        try
        {
            return Path.IsPathRooted(hintPath)
                ? Path.GetFullPath(hintPath)
                : Path.GetFullPath(Path.Combine(outputRoot, hintPath));
        }
        catch
        {
            return null;
        }
    }

    private static string StripAssemblyDisplayName(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return "";

        var value = assemblyName.Trim();
        var commaIndex = value.IndexOf(',');
        return commaIndex >= 0 ? value[..commaIndex].Trim() : value;
    }

    private static string? TryResolveAssemblyNameFromFullPath(string fullPath)
    {
        try
        {
            return File.Exists(fullPath) ? AssemblyName.GetAssemblyName(fullPath).Name : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<(string Include, string? HintPath, bool? SpecificVersion)> BuildDotNetAssemblyReferences(ProjectSemantic parsed, string outputRoot)
    {
        var result = new List<(string Include, string? HintPath, bool? SpecificVersion)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddFrameworkReference(result, seen, "System.Design", @"$(WINDIR)\Microsoft.NET\Framework\v4.0.30319\System.Design.dll");
        AddFrameworkReference(result, seen, "System.Drawing.Design", @"$(WINDIR)\Microsoft.NET\Framework\v4.0.30319\System.Drawing.Design.dll");
        AddFrameworkReference(result, seen, "System.Web");

        var runtimeBoxProjectPath = ResolveRuntimeBoxProjectPath(outputRoot);
        if (!string.Equals(_runtimeCoreReferenceMode, "Project", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(runtimeBoxProjectPath))
        {
            var runtimeBoxHintPath = ResolveRuntimeBoxHintPath(outputRoot);
            result.Add((RuntimeBoxAssemblyName, runtimeBoxHintPath, false));
        }
        seen.Add(RuntimeBoxAssemblyName);

        foreach (var reference in parsed.DotNetComponentReferences
                     .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var hintPath = ResolveReferenceHintPath(reference, outputRoot);
            if (!IsReferenceHintPathMaterialized(outputRoot, hintPath) &&
                _dllReferenceMap.TryGetValue(reference.Name, out var mappedDllPath))
            {
                hintPath = ResolveMappedDotNetReferenceHintPath(outputRoot, reference.Name, mappedDllPath);
            }

            var include = ResolveReferenceInclude(reference, outputRoot, hintPath);
            if (string.IsNullOrWhiteSpace(include) || !seen.Add(include))
                continue;

            var specificVersion = ParseYesNoFlag(reference.UseSpecificVersion);
            result.Add((include, hintPath, specificVersion));
        }

        return result;
    }

    private static void AddFrameworkReference(
        List<(string Include, string? HintPath, bool? SpecificVersion)> references,
        HashSet<string> seen,
        string include,
        string? hintPath = null)
    {
        if (seen.Add(include))
            references.Add((include, hintPath, null));
    }

    private static bool IsDesignerFrameworkReference(string include)
        => string.Equals(include, "System.Design", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(include, "System.Drawing.Design", StringComparison.OrdinalIgnoreCase);

    private static bool IsReferenceHintPathMaterialized(string outputRoot, string? hintPath)
    {
        var fullPath = ResolveReferenceHintPathToFullPath(outputRoot, hintPath);
        return !string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath);
    }

    private static string? ResolveMappedDotNetReferenceHintPath(string outputRoot, string componentName, string? mappedDllPath)
    {
        if (string.IsNullOrWhiteSpace(mappedDllPath) || !File.Exists(mappedDllPath))
            return null;

        var candidates = new List<string>();
        var containerRoot = ResolveProjectContainerRoot(outputRoot);
        if (!string.IsNullOrWhiteSpace(containerRoot))
            candidates.Add(Path.Combine(containerRoot, ExternalRefsDirectoryName, componentName, Path.GetFileName(mappedDllPath)));
        candidates.Add(mappedDllPath);

        var resolved = candidates.FirstOrDefault(File.Exists);
        return string.IsNullOrWhiteSpace(resolved)
            ? null
            : NormalizeProjectPath(Path.GetRelativePath(outputRoot, resolved));
    }

    private static string? ResolveRuntimeBoxHintPath(string outputRoot)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(_solutionRoot))
        {
            candidates.Add(Path.Combine(_solutionRoot, ExternalRefsDirectoryName, RuntimeExternalRefsDirectoryName, RuntimeBoxAssemblyName + ".dll"));
            candidates.Add(Path.Combine(_solutionRoot, RuntimeBoxAssemblyName + ".dll"));
            candidates.Add(Path.Combine(_solutionRoot, RuntimeCoreProjectName, "bin", "Debug", RuntimeBoxAssemblyName + ".dll"));
            candidates.Add(Path.Combine(_solutionRoot, RuntimeCoreProjectName, "bin", "Release", RuntimeBoxAssemblyName + ".dll"));
        }

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeBoxAssemblyName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, "bin", "Debug", RuntimeBoxAssemblyName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, "bin", "Release", RuntimeBoxAssemblyName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, "bin", "Debug", RuntimeBoxAssemblyName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, "bin", "Release", RuntimeBoxAssemblyName + ".dll"));
        foreach (var baseDir in EnumerateToolSearchRoots())
        {
            candidates.Add(Path.Combine(baseDir, "assets", "runtime", RuntimeBoxAssemblyName + ".dll"));
            candidates.Add(Path.Combine(baseDir, "tools", "XpaConverterMvp", "assets", "runtime", RuntimeBoxAssemblyName + ".dll"));
        }

        var existing = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(existing))
            return null;

        return NormalizeProjectPath(Path.GetRelativePath(outputRoot, existing));
    }

    private static IEnumerable<string> EnumerateToolSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(typeof(ProjectGenerator).Assembly.Location) ?? ""
                 }.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var current = Path.GetFullPath(seed);
            while (!string.IsNullOrWhiteSpace(current) && seen.Add(current))
            {
                yield return current;
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }
        }
    }

    private static string ResolveReferenceInclude(DotNetComponentReferenceDef reference, string outputRoot, string? hintPath)
    {
        var resolvedAssemblyName = TryResolveAssemblyNameFromHintPath(outputRoot, hintPath);
        if (!string.IsNullOrWhiteSpace(resolvedAssemblyName))
            return resolvedAssemblyName;

        if (!string.IsNullOrWhiteSpace(reference.AssemblyName))
        {
            var commaIndex = reference.AssemblyName.IndexOf(',');
            return commaIndex >= 0
                ? reference.AssemblyName[..commaIndex].Trim()
                : reference.AssemblyName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(reference.Name))
            return reference.Name;

        return "";
    }

    private static string? ResolveReferenceHintPath(DotNetComponentReferenceDef reference, string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(reference.AssemblyPath))
            return null;

        var assemblyPath = reference.AssemblyPath.Trim();
        if (assemblyPath.StartsWith("%WorkingDir%", StringComparison.OrdinalIgnoreCase))
        {
            var relative = assemblyPath["%WorkingDir%".Length..]
                .TrimStart('\\', '/')
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);

            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(_solutionRoot))
                candidates.Add(Path.Combine(_solutionRoot!, relative));

            if (!string.IsNullOrWhiteSpace(_sourceRoot))
                candidates.Add(Path.Combine(_sourceRoot, relative));

            var containerRoot = ResolveProjectContainerRoot(outputRoot);
            if (!string.IsNullOrWhiteSpace(containerRoot))
                candidates.Add(Path.Combine(containerRoot, relative));

            if (!string.IsNullOrWhiteSpace(_runtimeCoreDllPath))
            {
                var runtimeDir = Path.GetDirectoryName(_runtimeCoreDllPath!);
                if (!string.IsNullOrWhiteSpace(runtimeDir))
                    candidates.Add(Path.Combine(runtimeDir, relative));
            }

            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), relative));
            candidates.Add(Path.Combine(@"D:\DLLs", relative));

            var materializedPath = candidates.FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(materializedPath))
                materializedPath = candidates.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(materializedPath))
                return null;

            return NormalizeProjectPath(Path.GetRelativePath(outputRoot, materializedPath));
        }

        var macroPath = ResolveMacroReferenceHintPath(assemblyPath, outputRoot);
        if (!string.IsNullOrWhiteSpace(macroPath))
            return macroPath;

        if (Path.IsPathRooted(assemblyPath) && File.Exists(assemblyPath))
            return NormalizeProjectPath(Path.GetRelativePath(outputRoot, assemblyPath));

        return null;
    }

    private static string? ResolveMacroReferenceHintPath(string assemblyPath, string outputRoot)
    {
        var expanded = Environment.ExpandEnvironmentVariables(assemblyPath);
        if (!string.Equals(expanded, assemblyPath, StringComparison.Ordinal) &&
            !expanded.Contains('%', StringComparison.Ordinal) &&
            File.Exists(expanded))
            return NormalizeProjectPath(Path.GetRelativePath(outputRoot, expanded));

        const string cigamInstallToken = "%CIGAM_INSTAL%";
        if (!assemblyPath.StartsWith(cigamInstallToken, StringComparison.OrdinalIgnoreCase))
            return null;

        var suffix = assemblyPath[cigamInstallToken.Length..]
            .TrimStart('\\', '/')
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(suffix))
            return null;

        var roots = new List<string>();
        var envRoot = Environment.GetEnvironmentVariable("CIGAM_INSTAL");
        if (!string.IsNullOrWhiteSpace(envRoot))
            roots.Add(envRoot);

        if (!string.IsNullOrWhiteSpace(_solutionRoot))
            roots.Add(_solutionRoot!);
        if (!string.IsNullOrWhiteSpace(_sourceRoot))
            roots.Add(_sourceRoot);

        roots.Add(@"E:\Dlls\Dlls");
        roots.Add(@"E:\Dlls");
        roots.Add(@"D:\Dlls\Dlls");
        roots.Add(@"D:\Dlls");

        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(root, suffix);
            if (File.Exists(candidate))
                return NormalizeProjectPath(Path.GetRelativePath(outputRoot, candidate));
        }

        return null;
    }

    private static bool? ParseYesNoFlag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Equals("Y", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveEnvProjectPath(string outputRoot)
    {
        if (!string.IsNullOrWhiteSpace(_solutionRoot))
        {
            var splitEnvProject = Path.Combine(_solutionRoot, RuntimeCoreProjectName, RuntimeEnvProjectRelativePath);
            if (File.Exists(splitEnvProject))
                return splitEnvProject;

            return Path.Combine(_solutionRoot, RuntimeCoreProjectName, RuntimeCoreProjectName + ".csproj");
        }

        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, RuntimeEnvProjectRelativePath),
            Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, RuntimeCoreProjectName + ".csproj"),
            Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, RuntimeCoreProjectName + ".csproj"),
            Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, LegacyRuntimeProjectName + ".csproj")
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string ResolveRuntimeBoxProjectPath(string outputRoot)
    {
        if (!string.IsNullOrWhiteSpace(_solutionRoot))
            return Path.Combine(_solutionRoot, RuntimeCoreProjectName, RuntimeBoxProjectFileName);

        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, RuntimeBoxProjectFileName),
            Path.Combine(outputRoot, "..", RuntimeCoreProjectName, RuntimeBoxProjectFileName)
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }


    private static string ResolveExternalComponentDllInclude(string outputRoot, string componentName, string dllPath, string? hintPath)
    {
        var resolvedAssemblyName = TryResolveAssemblyNameFromHintPath(outputRoot, hintPath);
        if (!string.IsNullOrWhiteSpace(resolvedAssemblyName))
            return resolvedAssemblyName;

        return Path.GetFileNameWithoutExtension(dllPath);
    }

    private static string? ResolveExternalComponentDllHintPath(string outputRoot, string componentName, string dllPath)
    {
        var candidates = new List<string>();
        var containerRoot = ResolveProjectContainerRoot(outputRoot);
        candidates.Add(Path.Combine(containerRoot, "ExternalRefs", componentName, Path.GetFileName(dllPath)));
        candidates.Add(dllPath);

        var resolved = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(resolved))
            return null;

        return NormalizeProjectPath(Path.GetRelativePath(outputRoot, resolved));
    }

    private static string ResolveProjectContainerRoot(string outputRoot)
    {
        if (!string.IsNullOrWhiteSpace(_solutionRoot))
            return _solutionRoot;
        if (_isComponentized)
            return Directory.GetParent(outputRoot)?.FullName ?? outputRoot;
        return outputRoot;
    }

    private static string NormalizeProjectPath(string path)
    {
        if (Path.IsPathRooted(path))
            return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string? TryResolveAssemblyNameFromHintPath(string outputRoot, string? hintPath)
    {
        if (string.IsNullOrWhiteSpace(hintPath))
            return null;

        try
        {
            var resolvedPath = Path.IsPathRooted(hintPath)
                ? hintPath
                : Path.GetFullPath(Path.Combine(outputRoot, hintPath));

            if (!File.Exists(resolvedPath))
                return null;

            return AssemblyName.GetAssemblyName(resolvedPath).Name;
        }
        catch
        {
            return null;
        }
    }

    private static string EscapeXml(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? value;
    }
}


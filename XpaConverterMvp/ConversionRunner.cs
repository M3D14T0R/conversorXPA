using System.Xml.Linq;
using System.Diagnostics;

namespace XpaConverterMvp;

public static class ConversionRunner
{
    private const string RuntimeCoreProjectName = "XPARuntimeCore";
    private const string LegacyRuntimeProjectName = "ENV";
    private const string RuntimeBoxAssemblyName = "XPARuntimeCore.Box";
    private const string RuntimeBoxProjectFileName = "XPARuntimeCore.Box.csproj";
    private const string ExternalRefsDirectoryName = "ExternalRefs";
    private const string RuntimeExternalRefsDirectoryName = "Runtime";
    private static readonly string RuntimeEnvProjectRelativePath = Path.Combine(LegacyRuntimeProjectName, LegacyRuntimeProjectName + ".csproj");
    private static string? _runtimeRootOverride;

    public static int Run(ConversionOptions options, TextWriter? stdOut = null, TextWriter? stdErr = null)
    {
        stdOut ??= Console.Out;
        stdErr ??= Console.Error;

        var xmlPath = options.XmlPath;
        var outputDir = options.OutputDir;
        var appNamespace = string.IsNullOrWhiteSpace(options.AppNamespace)
            ? InferNamespaceFromOutputDirectory(outputDir)
            : options.AppNamespace!;
        var folderFilter = options.FolderFilter;
        var taskFilters = options.TaskFilters
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        var taskRanges = options.TaskRanges
            .Distinct()
            .OrderBy(r => r.Start)
            .ThenBy(r => r.End)
            .ToList();
        var projectReferenceMap = NormalizeProjectReferenceMap(options.ProjectReferenceMap, options.ProjectReferencePaths, stdOut);
        var dllReferenceMap = NormalizeDllReferenceMap(options.DllReferenceMap, stdOut);
        var manifestReferenceMap = BuildManifestReferenceMap(projectReferenceMap, dllReferenceMap);
        NormalizeReferencedProjectEnvironments(projectReferenceMap, options.RuntimeCoreReferenceMode);
        var withDependencies = options.WithDependencies;
        var fullSolution = options.FullSolution;
        var partialTaskScope = taskFilters.Count > 0 || taskRanges.Count > 0;
        var parallelTaskGeneration =
            options.ParallelTaskGeneration ||
            (fullSolution && string.IsNullOrWhiteSpace(folderFilter) && !partialTaskScope);
        options.ParallelTaskGeneration = parallelTaskGeneration;

        if (!File.Exists(xmlPath))
        {
            stdErr.WriteLine($"XML not found: {xmlPath}");
            return 2;
        }

        try
        {
            _runtimeRootOverride = string.IsNullOrWhiteSpace(options.RuntimeRootPath) ? null : options.RuntimeRootPath.Trim();
            var useIncrementalOutput = options.IncrementalOutput && partialTaskScope;
            Directory.CreateDirectory(outputDir);
            if (useIncrementalOutput)
            {
                Log(stdOut, "SESSION", "Incremental output enabled: existing generated files will be preserved unless overwritten by this run.");
            }
            else if (options.IncrementalOutput)
            {
                Log(stdOut, "SESSION", "Incremental output ignored: it requires at least one --task or --task-range filter.");
                if (string.IsNullOrWhiteSpace(folderFilter))
                    CleanupOutputDirectory(outputDir);
                else
                    CleanupOutputFolder(outputDir, folderFilter);
            }
            else if (string.IsNullOrWhiteSpace(folderFilter))
            {
                CleanupOutputDirectory(outputDir);
            }
            else
            {
                CleanupOutputFolder(outputDir, folderFilter);
            }

            ConversionTelemetry.Initialize(outputDir, options, appNamespace);
            Log(stdOut, "SESSION", $"telemetry={ConversionTelemetry.LogPath}");

            var sourceRoot = Path.GetDirectoryName(xmlPath) ?? "";
            string? reducedXmlPath = null;
            var parseXmlPath = xmlPath;
            var taskScopedReduction = partialTaskScope;
            var taskScopedDependencyReduction = taskScopedReduction && withDependencies;
            var folderScopedReduction = !withDependencies && taskRanges.Count == 0 && !string.IsNullOrWhiteSpace(folderFilter);
            var useImplicitComponentXmlResolution =
                !options.DisableImplicitComponentXmlResolution &&
                manifestReferenceMap.Count == 0 &&
                !taskScopedReduction;
            if (taskScopedReduction || folderScopedReduction)
            {
                reducedXmlPath = CreateReducedMainXml(xmlPath, taskFilters, taskRanges, folderFilter, includeTaskDependencies: taskScopedDependencyReduction);
                if (!string.IsNullOrWhiteSpace(reducedXmlPath))
                {
                    parseXmlPath = reducedXmlPath;
                    var dependencyMode = taskScopedDependencyReduction ? " with dependency closure" : "";
                    Log(stdOut, "SESSION", $"Using reduced XML for scoped conversion{dependencyMode}: {Path.GetFileName(reducedXmlPath)}");
                }
            }

            ParsedXpa parsed;
            var parseStopwatch = Stopwatch.StartNew();
            try
            {
                Log(stdOut, "PHASE", "parse start");
                parsed = XpaParser.Parse(
                    parseXmlPath,
                    options.ComponentXmlPaths,
                    options.TablesXmlPaths,
                    useImplicitComponentXmlResolution: useImplicitComponentXmlResolution,
                    mainXmlBaseDirectoryOverride: sourceRoot,
                    projectReferenceMap: manifestReferenceMap);
            }
            finally
            {
                parseStopwatch.Stop();
                Log(stdOut, "PHASE", $"parse done elapsedMs={parseStopwatch.Elapsed.TotalMilliseconds:F0}");
            }

            ProjectSemantic semantic;
            ProjectSemantic? sharedAssetsSemantic = null;
            var semanticStopwatch = Stopwatch.StartNew();
            try
            {
                Log(stdOut, "PHASE", "semantic build start");
                semantic = SemanticBuilder.Build(parsed);
            }
            finally
            {
                SemanticBuilder.ReleaseBuildState();
                semanticStopwatch.Stop();
                Log(stdOut, "PHASE", $"semantic build done elapsedMs={semanticStopwatch.Elapsed.TotalMilliseconds:F0}");
            }
            // TaskSemantic contains the generation-ready representation. Keep
            // no second, equally large TaskDef graph alive during code output.
            parsed.Tasks.Clear();
            if (!string.IsNullOrWhiteSpace(reducedXmlPath) && folderScopedReduction && !taskScopedReduction)
            {
                var sharedAssetsParseStopwatch = Stopwatch.StartNew();
                try
                {
                    Log(stdOut, "PHASE", "shared assets parse start");
                    var fullParsed = XpaParser.Parse(
                        xmlPath,
                        options.ComponentXmlPaths,
                        options.TablesXmlPaths,
                        useImplicitComponentXmlResolution: useImplicitComponentXmlResolution,
                        mainXmlBaseDirectoryOverride: sourceRoot,
                        projectReferenceMap: manifestReferenceMap);
                    Log(stdOut, "PHASE", "shared assets semantic build start");
                    try
                    {
                        sharedAssetsSemantic = SemanticBuilder.Build(fullParsed);
                    }
                    finally
                    {
                        SemanticBuilder.ReleaseBuildState();
                        fullParsed.Tasks.Clear();
                    }
                }
                finally
                {
                    sharedAssetsParseStopwatch.Stop();
                    Log(stdOut, "PHASE", $"shared assets parse+semantic done elapsedMs={sharedAssetsParseStopwatch.Elapsed.TotalMilliseconds:F0}");
                }
            }
            else if (!string.IsNullOrWhiteSpace(reducedXmlPath))
            {
                Log(stdOut, "PHASE", "shared assets full parse skipped for task-scoped reduced XML");
            }
            var solutionRoot = fullSolution && string.IsNullOrWhiteSpace(folderFilter) ? outputDir : (string?)null;
            if (solutionRoot is not null)
                RemoveLegacyRuntimeLayout(solutionRoot);
            if (solutionRoot is not null)
                MaterializeBundledRuntime(solutionRoot, options.RuntimeCoreReferenceMode, options.RuntimeCoreDllPath);
            MaterializeExternalDllReferences(solutionRoot ?? outputDir, dllReferenceMap);

            var manifestNamespaces = manifestReferenceMap
                .Select(kv => new { kv.Key, Manifest = ProjectManifest.LoadForSource(kv.Value) })
                .Where(x => x.Manifest is not null)
                .ToDictionary(
                    x => x.Key,
                    x => string.IsNullOrWhiteSpace(x.Manifest!.Namespace) ? x.Key : x.Manifest.Namespace,
                    StringComparer.OrdinalIgnoreCase);

            string mainProjectOutputDir;
            if (semantic.Components.Count == 0)
            {
                mainProjectOutputDir = ResolveMainProjectOutputDir(outputDir, appNamespace, solutionRoot, useIncrementalOutput);
                Directory.CreateDirectory(mainProjectOutputDir);
                RunMeasuredPhase(stdOut, $"generate project {appNamespace}", () => ProjectGenerator.Generate(new ProjectGenerationRequest
                {
                    Parsed = semantic,
                    OutputRoot = mainProjectOutputDir,
                    AppNamespace = appNamespace,
                    SourceRoot = sourceRoot,
                    SharedAssetsSemantic = sharedAssetsSemantic,
                    SolutionRoot = solutionRoot,
                    ComponentNamespaces = manifestNamespaces,
                    ProjectReferenceMap = projectReferenceMap,
                    DllReferenceMap = dllReferenceMap,
                    OutputType = options.OutputType,
                    EnvReferenceMode = options.RuntimeCoreReferenceMode,
                    EnvDllPath = options.RuntimeCoreDllPath,
                    FolderFilter = folderFilter,
                    TaskFilters = taskRanges.Count == 0 ? taskFilters : Array.Empty<string>(),
                    ForceTaskScopedGeneration = taskRanges.Count > 0,
                    WithTaskDependencies = withDependencies,
                    IncrementalOutput = useIncrementalOutput,
                    ParallelTaskGeneration = parallelTaskGeneration
                }));
            }
            else
            {
                var componentNamespaces = semantic.Components
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(c => c, c => c, StringComparer.OrdinalIgnoreCase);
                foreach (var kv in manifestNamespaces)
                    componentNamespaces[kv.Key] = kv.Value;

                var mainFolder = Path.Combine(outputDir, appNamespace.Split('.').First());
                mainProjectOutputDir = mainFolder;
                Directory.CreateDirectory(mainFolder);
                RunMeasuredPhase(stdOut, $"generate project {appNamespace}", () => ProjectGenerator.Generate(new ProjectGenerationRequest
                {
                    Parsed = semantic,
                    OutputRoot = mainFolder,
                    AppNamespace = appNamespace,
                    SourceRoot = sourceRoot,
                    SharedAssetsSemantic = sharedAssetsSemantic,
                    SolutionRoot = solutionRoot,
                    TargetComponent = null,
                    ComponentNamespaces = componentNamespaces,
                    ProjectReferenceMap = projectReferenceMap,
                    DllReferenceMap = dllReferenceMap,
                    OutputType = options.OutputType,
                    EnvReferenceMode = options.RuntimeCoreReferenceMode,
                    EnvDllPath = options.RuntimeCoreDllPath,
                    FolderFilter = folderFilter,
                    TaskFilters = taskRanges.Count == 0 ? taskFilters : Array.Empty<string>(),
                    ForceTaskScopedGeneration = taskRanges.Count > 0,
                    WithTaskDependencies = withDependencies,
                    IncrementalOutput = useIncrementalOutput,
                    ParallelTaskGeneration = parallelTaskGeneration
                }));

                foreach (var component in semantic.Components.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var componentOut = Path.Combine(outputDir, component);
                    Directory.CreateDirectory(componentOut);
                    var componentName = component;
                    RunMeasuredPhase(stdOut, $"generate component {componentName}", () => ProjectGenerator.Generate(new ProjectGenerationRequest
                    {
                        Parsed = semantic,
                        OutputRoot = componentOut,
                        AppNamespace = componentName,
                        SourceRoot = sourceRoot,
                        SharedAssetsSemantic = sharedAssetsSemantic,
                        SolutionRoot = solutionRoot,
                        TargetComponent = componentName,
                        ComponentNamespaces = componentNamespaces,
                        ProjectReferenceMap = projectReferenceMap,
                        DllReferenceMap = dllReferenceMap,
                        OutputType = "ClassLibrary",
                        EnvReferenceMode = options.RuntimeCoreReferenceMode,
                        EnvDllPath = options.RuntimeCoreDllPath,
                        FolderFilter = folderFilter,
                        TaskFilters = taskRanges.Count == 0 ? taskFilters : Array.Empty<string>(),
                        ForceTaskScopedGeneration = taskRanges.Count > 0,
                        WithTaskDependencies = withDependencies,
                        IncrementalOutput = useIncrementalOutput,
                        ParallelTaskGeneration = parallelTaskGeneration
                    }));
                }
            }

            if (string.IsNullOrWhiteSpace(folderFilter))
            {
                RunMeasuredPhase(stdOut, "sidecar files", () => CopyProjectSidecarFiles(xmlPath, mainProjectOutputDir, appNamespace));
            }

            if (solutionRoot is not null && string.IsNullOrWhiteSpace(folderFilter))
            {
                RunMeasuredPhase(stdOut, "resource folders", () => CopyProjectResourceFolders(xmlPath, solutionRoot));
            }

            if (solutionRoot is not null)
            {
                var includeRuntimeCoreProject =
                    !string.Equals(options.RuntimeCoreReferenceMode, "Dll", StringComparison.OrdinalIgnoreCase) &&
                    (File.Exists(Path.Combine(solutionRoot, RuntimeCoreProjectName, RuntimeBoxProjectFileName)) ||
                     File.Exists(Path.Combine(solutionRoot, RuntimeCoreProjectName, RuntimeCoreProjectName + ".csproj")));
                var solutionPath = Path.Combine(solutionRoot, $"{appNamespace.Split('.').First()}.sln");
                if (useIncrementalOutput && File.Exists(solutionPath))
                {
                    Log(stdOut, "PHASE", "solution file skipped for incremental task scope");
                }
                else
                {
                    RunMeasuredPhase(stdOut, "solution file", () => WriteSolutionFile(solutionRoot, appNamespace, semantic.Components, includeEnvProject: includeRuntimeCoreProject));
                }
            }

            stdOut.WriteLine("MVP conversion completed.");
            stdOut.WriteLine($"Types:   {semantic.FieldModels.Count}");
            stdOut.WriteLine($"Models:  {semantic.DataObjects.Count}");
            stdOut.WriteLine($"Tasks:   {semantic.Tasks.Count}");
            stdOut.WriteLine($"Output:  {outputDir}");
            ConversionTelemetry.CloseSuccess(semantic.Tasks.Count, semantic.DataObjects.Count, semantic.FieldModels.Count);
            return 0;
        }
        catch (Exception ex)
        {
            stdErr.WriteLine("Conversion failed.");
            stdErr.WriteLine(ex.ToString());
            ConversionTelemetry.CloseFailure(ex);
            return 3;
        }
        finally
        {
            CleanupTemporaryReducedXml();
            _runtimeRootOverride = null;
        }
    }

    private static void RunMeasuredPhase(TextWriter stdOut, string phaseName, Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        Log(stdOut, "PHASE", $"{phaseName} start");
        try
        {
            action();
        }
        finally
        {
            stopwatch.Stop();
            Log(stdOut, "PHASE", $"{phaseName} done elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}");
        }
    }

    private static void Log(TextWriter stdOut, string category, string message)
    {
        stdOut.WriteLine($"[XpaConverterMvp] {message}");
        ConversionTelemetry.Log(category, message);
    }

    private static string ResolveMainProjectOutputDir(string outputDir, string appNamespace, string? solutionRoot, bool useIncrementalOutput)
    {
        var projectName = appNamespace.Split('.').First();
        if (solutionRoot is not null)
            return Path.Combine(outputDir, projectName);

        if (useIncrementalOutput)
        {
            var nestedProjectDir = Path.Combine(outputDir, projectName);
            var nestedProjectPath = Path.Combine(nestedProjectDir, projectName + ".csproj");
            if (File.Exists(nestedProjectPath))
                return nestedProjectDir;

            var directProjectPath = Path.Combine(outputDir, projectName + ".csproj");
            if (File.Exists(directProjectPath))
                return outputDir;
        }

        return outputDir;
    }

    private static Dictionary<string, string> NormalizeProjectReferenceMap(
        IReadOnlyDictionary<string, string> explicitMap,
        IReadOnlyList<string> legacyPaths,
        TextWriter stdOut)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in explicitMap)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                continue;

            result[kv.Key.Trim()] = kv.Value.Trim();
        }

        foreach (var path in legacyPaths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var manifest = ProjectManifest.LoadForProject(path);
            var componentName = manifest?.ComponentName;
            if (string.IsNullOrWhiteSpace(componentName))
                componentName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(componentName))
                continue;
            result[componentName] = path;
        }

        foreach (var kv in result.ToList())
        {
            var manifest = ProjectManifest.LoadForProject(kv.Value);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.ComponentName))
                continue;

            if (!string.Equals(manifest.ComponentName, kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                stdOut.WriteLine($"Aviso: mapeamento do componente '{kv.Key}' usa manifesto '{manifest.ComponentName}'. Mantendo o nome do XML e o projeto informado.");
            }
        }

        return result;
    }

    private static Dictionary<string, string> NormalizeDllReferenceMap(
        IReadOnlyDictionary<string, string> explicitMap,
        TextWriter stdOut)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in explicitMap)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                continue;

            result[kv.Key.Trim()] = kv.Value.Trim();
        }

        foreach (var kv in result.ToList())
        {
            var manifest = ProjectManifest.LoadForBinary(kv.Value);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.ComponentName))
                continue;

            if (!string.Equals(manifest.ComponentName, kv.Key, StringComparison.OrdinalIgnoreCase))
                stdOut.WriteLine($"Aviso: mapeamento DLL do componente '{kv.Key}' usa manifesto '{manifest.ComponentName}'. Mantendo o nome do XML e a DLL informada.");
        }

        return result;
    }

    private static Dictionary<string, string> BuildManifestReferenceMap(
        IReadOnlyDictionary<string, string> projectReferenceMap,
        IReadOnlyDictionary<string, string> dllReferenceMap)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in projectReferenceMap)
            result[kv.Key] = kv.Value;
        foreach (var kv in dllReferenceMap)
            result[kv.Key] = kv.Value;
        return result;
    }

    [ThreadStatic]
    private static string? _temporaryReducedXmlPath;

    public static string InferNamespaceFromOutputDirectory(string outputDir)
    {
        var leaf = Path.GetFileName(Path.GetFullPath(outputDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(leaf))
            return "Generated";
        return leaf;
    }

    public static string ResolveTaskOutputFolder(string? rawFolder)
    {
        if (string.IsNullOrWhiteSpace(rawFolder))
            return string.Empty;

        var folder = rawFolder.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(folder, @"^_\d+_[A-Za-z0-9_]+$"))
            return folder;
        var numbered = System.Text.RegularExpressions.Regex.Match(folder, @"^\s*(\d+)\s*\.\s*(.+)$");
        if (numbered.Success)
            return "_" + numbered.Groups[1].Value + "_" + ToPascalIdentifier(numbered.Groups[2].Value);

        return ToPascalIdentifier(folder);
    }

    private static void CleanupOutputDirectory(string outputDir)
    {
        foreach (var file in Directory.GetFiles(outputDir, "*.generated.cs", SearchOption.TopDirectoryOnly))
            File.Delete(file);
        DeleteMarkedGeneratedSources(outputDir);
        foreach (var dir in Directory.GetDirectories(outputDir))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, ".vs", StringComparison.OrdinalIgnoreCase))
                continue;
            TryDeleteDirectory(dir);
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ScopedTopLevelTaskIndex(
        int Index,
        bool MainProgram,
        bool MatchesRequestedScope,
        IReadOnlySet<int> TopLevelDependencies);

    private sealed class TopLevelTaskRangeSet
    {
        private readonly IReadOnlyList<TopLevelTaskRange> _ranges;

        public TopLevelTaskRangeSet(IReadOnlyList<TopLevelTaskRange> ranges)
            => _ranges = ranges;

        public bool HasRanges => _ranges.Count > 0;

        public bool Contains(int index)
        {
            foreach (var range in _ranges)
            {
                if (range.Contains(index))
                    return true;
            }

            return false;
        }

        public override string ToString()
            => _ranges.Count == 0 ? "" : string.Join(",", _ranges);
    }

    private static string? CreateReducedMainXml(
        string xmlPath,
        IReadOnlyList<string> taskFilters,
        IReadOnlyList<TopLevelTaskRange> taskRanges,
        string? folderFilter,
        bool includeTaskDependencies = false)
    {
        var normalizedFolderFilter = ResolveTaskOutputFolder(folderFilter);
        var taskFilterSet = taskFilters
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var taskRangeSet = new TopLevelTaskRangeSet(taskRanges);
        if (taskFilterSet.Count == 0 && !taskRangeSet.HasRanges && string.IsNullOrWhiteSpace(normalizedFolderFilter))
            return null;

        string? sanitizedSourcePath = null;
        var sourcePath = xmlPath;
        try
        {
            using var probe = System.Xml.XmlReader.Create(xmlPath, new System.Xml.XmlReaderSettings
            {
                CheckCharacters = false,
                DtdProcessing = System.Xml.DtdProcessing.Parse
            });
            while (probe.Read())
            {
            }
        }
        catch (System.Xml.XmlException)
        {
            sanitizedSourcePath = XpaParser.CreateSanitizedXmlTempFile(xmlPath);
            sourcePath = sanitizedSourcePath;
        }

        HashSet<int>? dependencyTopLevelIndexes = null;
        if (includeTaskDependencies && (taskFilterSet.Count > 0 || taskRangeSet.HasRanges))
            dependencyTopLevelIndexes = ResolveScopedDependencyTopLevelIndexes(sourcePath, taskFilterSet, taskRangeSet, normalizedFolderFilter);

        var tempPath = Path.Combine(Path.GetTempPath(), $"xpa_scoped_{Guid.NewGuid():N}.xml");
        try
        {
            using var reader = System.Xml.XmlReader.Create(sourcePath, new System.Xml.XmlReaderSettings
            {
                CheckCharacters = false,
                DtdProcessing = System.Xml.DtdProcessing.Parse,
                IgnoreComments = false,
                IgnoreWhitespace = false,
                IgnoreProcessingInstructions = false
            });
            using var writer = System.Xml.XmlWriter.Create(tempPath, new System.Xml.XmlWriterSettings
            {
                Indent = false,
                OmitXmlDeclaration = false,
                Encoding = System.Text.Encoding.UTF8
            });

            writer.WriteStartDocument();
            while (reader.Read())
            {
                if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.Name == "ProgramsRepository")
                {
                    WriteStartElement(reader, writer);
                    if (!reader.IsEmptyElement)
                        CopyFilteredProgramsRepository(reader, writer, taskFilterSet, taskRangeSet, normalizedFolderFilter, dependencyTopLevelIndexes);
                    continue;
                }

                CopyCurrentNode(reader, writer);
            }
            writer.WriteEndDocument();
            writer.Flush();
            var reducedBytes = new FileInfo(tempPath).Length;
            ConversionTelemetry.Log(
                "SCOPED_REDUCTION",
                $"reducedXml path={Path.GetFileName(tempPath)} bytes={reducedBytes} ranges={taskRangeSet}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sanitizedSourcePath))
            {
                try
                {
                    if (File.Exists(sanitizedSourcePath))
                        File.Delete(sanitizedSourcePath);
                }
                catch
                {
                }
            }
        }

        _temporaryReducedXmlPath = tempPath;
        return tempPath;
    }

    private static void CopyFilteredProgramsRepository(
        System.Xml.XmlReader reader,
        System.Xml.XmlWriter writer,
        HashSet<string> taskFilterSet,
        TopLevelTaskRangeSet taskRangeSet,
        string normalizedFolderFilter,
        HashSet<int>? dependencyTopLevelIndexes)
    {
        var topLevelIndex = 0;
        var selectedByRange = 0;
        var selectedByFilter = 0;
        var selectedByDependency = 0;
        while (reader.Read())
        {
            if (reader.NodeType == System.Xml.XmlNodeType.EndElement && reader.Name == "ProgramsRepository")
            {
                ConversionTelemetry.Log(
                    "SCOPED_REDUCTION",
                    $"streamingSelection topLevelTotal={topLevelIndex} selectedByRange={selectedByRange} selectedByFilter={selectedByFilter} selectedByDependency={selectedByDependency}");
                writer.WriteFullEndElement();
                return;
            }

            if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.Name == "Task")
            {
                topLevelIndex++;
                if (dependencyTopLevelIndexes is not null)
                {
                    if (dependencyTopLevelIndexes.Contains(topLevelIndex))
                    {
                        selectedByDependency++;
                        CopyScopedTopLevelTask(writer, reader, topLevelIndex);
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
                else if (taskRangeSet.Contains(topLevelIndex))
                {
                    selectedByRange++;
                    CopyScopedTopLevelTask(writer, reader, topLevelIndex);
                }
                else if (taskFilterSet.Count == 0 && string.IsNullOrWhiteSpace(normalizedFolderFilter))
                {
                    reader.Skip();
                }
                else
                {
                    var taskXml = XpaParser.SanitizeXmlFragment(reader.ReadOuterXml());
                    var taskElement = XElement.Parse(taskXml, LoadOptions.PreserveWhitespace);
                    if (ShouldKeepTopLevelTask(taskElement, taskFilterSet, normalizedFolderFilter))
                    {
                        selectedByFilter++;
                        taskElement.WriteTo(writer);
                    }
                }
                continue;
            }

            CopyCurrentNode(reader, writer);
        }
    }

    private static HashSet<int> ResolveScopedDependencyTopLevelIndexes(
        string sourcePath,
        HashSet<string> taskFilterSet,
        TopLevelTaskRangeSet taskRangeSet,
        string normalizedFolderFilter)
    {
        var entries = BuildScopedTopLevelTaskIndex(sourcePath, taskFilterSet, taskRangeSet, normalizedFolderFilter);
        var entriesByIndex = entries.ToDictionary(e => e.Index);
        var selected = new HashSet<int>();
        var queue = new Queue<int>();

        foreach (var entry in entries.Where(e => e.MatchesRequestedScope))
        {
            if (selected.Add(entry.Index))
                queue.Enqueue(entry.Index);
        }

        foreach (var entry in entries.Where(e => e.MainProgram))
            selected.Add(entry.Index);

        while (queue.Count > 0)
        {
            var currentIndex = queue.Dequeue();
            if (!entriesByIndex.TryGetValue(currentIndex, out var current))
                continue;

            foreach (var dependencyIndex in current.TopLevelDependencies)
            {
                if (!entriesByIndex.ContainsKey(dependencyIndex))
                    continue;
                if (selected.Add(dependencyIndex))
                    queue.Enqueue(dependencyIndex);
            }
        }

        var requested = selected.Count(e => entriesByIndex.TryGetValue(e, out var entry) && entry.MatchesRequestedScope);
        var dependencies = selected.Count(e => entriesByIndex.TryGetValue(e, out var entry) && !entry.MatchesRequestedScope);
        ConversionTelemetry.Log(
            "SCOPED_REDUCTION",
            $"dependencyClosure topLevelTotal={entries.Count} selected={selected.Count} requested={requested} dependencies={dependencies}");
        return selected;
    }

    private static List<ScopedTopLevelTaskIndex> BuildScopedTopLevelTaskIndex(
        string sourcePath,
        HashSet<string> taskFilterSet,
        TopLevelTaskRangeSet taskRangeSet,
        string normalizedFolderFilter)
    {
        var result = new List<ScopedTopLevelTaskIndex>();
        using var reader = System.Xml.XmlReader.Create(sourcePath, new System.Xml.XmlReaderSettings
        {
            CheckCharacters = false,
            DtdProcessing = System.Xml.DtdProcessing.Parse,
            IgnoreComments = false,
            IgnoreWhitespace = false,
            IgnoreProcessingInstructions = false
        });

        while (reader.Read())
        {
            if (reader.NodeType != System.Xml.XmlNodeType.Element || reader.Name != "ProgramsRepository")
                continue;
            if (reader.IsEmptyElement)
                break;

            while (reader.Read())
            {
                if (reader.NodeType == System.Xml.XmlNodeType.EndElement && reader.Name == "ProgramsRepository")
                    return result;

                if (reader.NodeType != System.Xml.XmlNodeType.Element || reader.Name != "Task")
                    continue;

                var topLevelIndex = result.Count + 1;
                using var taskSubtree = reader.ReadSubtree();
                result.Add(BuildScopedTopLevelTaskIndexEntry(topLevelIndex, taskSubtree, taskFilterSet, taskRangeSet, normalizedFolderFilter));
            }
        }

        return result;
    }

    private static ScopedTopLevelTaskIndex BuildScopedTopLevelTaskIndexEntry(
        int topLevelIndex,
        System.Xml.XmlReader taskReader,
        HashSet<string> taskFilterSet,
        TopLevelTaskRangeSet taskRangeSet,
        string normalizedFolderFilter)
    {
        var mainProgram = false;
        var matchesRequestedScope = taskRangeSet.Contains(topLevelIndex);
        var dependencies = new HashSet<int>();
        var taskDepth = 0;

        while (taskReader.Read())
        {
            if (taskReader.NodeType != System.Xml.XmlNodeType.Element)
                continue;

            if (taskReader.Name == "Task")
            {
                taskDepth++;
                if (taskDepth == 1)
                    mainProgram = string.Equals(taskReader.GetAttribute("MainProgram"), "Y", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (taskReader.Name == "Header")
            {
                var headerDescription = taskReader.GetAttribute("Description");
                var headerFolder = taskReader.GetAttribute("Folder");
                if (TaskHeaderMatchesScope(headerDescription, headerFolder, taskFilterSet, normalizedFolderFilter))
                    matchesRequestedScope = true;
                continue;
            }

            if (taskReader.Name == "CallTask")
            {
                using var callSubtree = taskReader.ReadSubtree();
                if (TryReadTopLevelProgramDependency(callSubtree, out var targetIndex))
                    dependencies.Add(targetIndex);
            }
        }

        return new ScopedTopLevelTaskIndex(
            topLevelIndex,
            mainProgram,
            matchesRequestedScope,
            dependencies);
    }

    private static bool TaskHeaderMatchesScope(
        string? description,
        string? folder,
        HashSet<string> taskFilterSet,
        string normalizedFolderFilter)
    {
        if (!string.IsNullOrWhiteSpace(description) && MatchesTaskFilter(description, taskFilterSet))
            return true;

        if (!string.IsNullOrWhiteSpace(normalizedFolderFilter) &&
            string.Equals(ResolveTaskOutputFolder(folder), normalizedFolderFilter, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool TryReadTopLevelProgramDependency(System.Xml.XmlReader callReader, out int targetIndex)
    {
        targetIndex = 0;
        string? operationType = null;
        string? rawTarget = null;
        var componentId = 0;

        while (callReader.Read())
        {
            if (callReader.NodeType != System.Xml.XmlNodeType.Element)
                continue;

            if (callReader.Name == "OperationType")
            {
                operationType = callReader.GetAttribute("val");
                continue;
            }

            if (callReader.Name == "TaskID")
            {
                if (int.TryParse(callReader.GetAttribute("comp"), out var parsedComponentId))
                    componentId = parsedComponentId;
                rawTarget = callReader.GetAttribute("obj") ?? callReader.GetAttribute("val");
            }
        }

        if (string.Equals(operationType, "T", StringComparison.OrdinalIgnoreCase))
            return false;
        if (componentId > 0)
            return false;
        return int.TryParse(rawTarget, out targetIndex) && targetIndex > 0;
    }

    private static void CopyScopedTopLevelTask(System.Xml.XmlWriter writer, System.Xml.XmlReader reader, int topLevelIndex)
    {
        var rootDepth = reader.Depth;
        writer.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
                writer.WriteAttributeString(reader.Prefix, reader.LocalName, reader.NamespaceURI, reader.Value);
            reader.MoveToElement();
        }

        writer.WriteAttributeString("_xpa_converter_original_top_level_index", topLevelIndex.ToString());
        if (reader.IsEmptyElement)
        {
            writer.WriteEndElement();
            return;
        }

        while (reader.Read())
        {
            if (reader.NodeType == System.Xml.XmlNodeType.EndElement && reader.Depth == rootDepth && reader.Name == "Task")
            {
                writer.WriteFullEndElement();
                return;
            }

            CopyCurrentNode(reader, writer);
        }
    }

    private static bool ShouldKeepTopLevelTask(
        XElement taskElement,
        HashSet<string> taskFilterSet,
        string normalizedFolderFilter)
    {
        if (string.Equals(taskElement.Attribute("MainProgram")?.Value, "Y", StringComparison.OrdinalIgnoreCase))
            return true;

        var header = taskElement.Element("Header");
        var description = header?.Attribute("Description")?.Value;
        var folder = header?.Attribute("Folder")?.Value;

        if (!string.IsNullOrWhiteSpace(description) && MatchesTaskFilter(description, taskFilterSet))
            return true;

        if (!string.IsNullOrWhiteSpace(normalizedFolderFilter) &&
            string.Equals(ResolveTaskOutputFolder(folder), normalizedFolderFilter, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool MatchesTaskFilter(string description, HashSet<string> taskFilterSet)
    {
        if (taskFilterSet.Contains(description))
            return true;

        var normalizedDescription = NormalizeTaskFilterName(description);
        var descriptionHasMarker = HasLeadingTaskFilterMarker(description);
        foreach (var taskFilter in taskFilterSet)
        {
            if (!string.Equals(NormalizeTaskFilterName(taskFilter), normalizedDescription, StringComparison.OrdinalIgnoreCase))
                continue;

            if (HasLeadingTaskFilterMarker(taskFilter) == descriptionHasMarker)
                return true;
        }

        return false;
    }

    private static bool HasLeadingTaskFilterMarker(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           System.Text.RegularExpressions.Regex.IsMatch(value, @"^\s*[-=<>\.]+");

    private static string NormalizeTaskFilterName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim();
        if (normalized.EndsWith("()", StringComparison.Ordinal))
            normalized = normalized[..^2].TrimEnd();
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"^[^A-Za-z0-9]+", "");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"[^A-Za-z0-9]+", "");
        return normalized.ToLowerInvariant();
    }

    private static void CopyCurrentNode(System.Xml.XmlReader reader, System.Xml.XmlWriter writer)
    {
        switch (reader.NodeType)
        {
            case System.Xml.XmlNodeType.Element:
                WriteStartElement(reader, writer);
                break;
            case System.Xml.XmlNodeType.EndElement:
                writer.WriteFullEndElement();
                break;
            case System.Xml.XmlNodeType.Text:
                writer.WriteString(reader.Value);
                break;
            case System.Xml.XmlNodeType.CDATA:
                writer.WriteCData(reader.Value);
                break;
            case System.Xml.XmlNodeType.SignificantWhitespace:
            case System.Xml.XmlNodeType.Whitespace:
                writer.WriteWhitespace(reader.Value);
                break;
            case System.Xml.XmlNodeType.Comment:
                writer.WriteComment(reader.Value);
                break;
            case System.Xml.XmlNodeType.ProcessingInstruction:
                writer.WriteProcessingInstruction(reader.Name, reader.Value);
                break;
            case System.Xml.XmlNodeType.XmlDeclaration:
                break;
        }
    }

    private static void WriteStartElement(System.Xml.XmlReader reader, System.Xml.XmlWriter writer)
    {
        writer.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
                writer.WriteAttributeString(reader.Prefix, reader.LocalName, reader.NamespaceURI, reader.Value);
            reader.MoveToElement();
        }

        if (reader.IsEmptyElement)
            writer.WriteEndElement();
    }

    private static void CleanupTemporaryReducedXml()
    {
        if (string.IsNullOrWhiteSpace(_temporaryReducedXmlPath))
            return;

        try
        {
            if (File.Exists(_temporaryReducedXmlPath))
                File.Delete(_temporaryReducedXmlPath);
        }
        catch
        {
        }
        finally
        {
            _temporaryReducedXmlPath = null;
        }
    }

    private static void CleanupOutputFolder(string outputDir, string rawFolder)
    {
        var folder = ResolveTaskOutputFolder(rawFolder);
        if (string.IsNullOrWhiteSpace(folder))
            return;

        var targetDir = Path.Combine(outputDir, folder);
        if (!Directory.Exists(targetDir))
            return;

        foreach (var file in Directory.GetFiles(targetDir, "*.generated.cs", SearchOption.TopDirectoryOnly))
            File.Delete(file);
        DeleteMarkedGeneratedSources(targetDir);

        foreach (var childDirName in new[] { "Views", "Printing", "TextIO" })
        {
            var childDir = Path.Combine(targetDir, childDirName);
            if (Directory.Exists(childDir))
                Directory.Delete(childDir, recursive: true);
        }
    }

    private static void DeleteMarkedGeneratedSources(string targetDir)
    {
        foreach (var file in Directory.GetFiles(targetDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(file).EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var firstLine = File.ReadLines(file).FirstOrDefault();
                if (string.Equals(firstLine?.Trim(), "// <auto-generated />", StringComparison.Ordinal))
                    File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string ToPascalIdentifier(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unnamed";

        var parts = System.Text.RegularExpressions.Regex.Split(raw, @"[^A-Za-z0-9]+")
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        if (parts.Length == 0)
            return "Unnamed";

        var sb = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            var lower = part.ToLowerInvariant();
            sb.Append(char.ToUpperInvariant(lower[0]));
            if (lower.Length > 1)
                sb.Append(lower[1..]);
        }

        var result = sb.ToString();
        if (char.IsDigit(result[0]))
            result = "_" + result;
        return result;
    }

    private static void CopyProjectSidecarFiles(string xmlPath, string outputDir, string appNamespace)
    {
        var sourceDir = Path.GetDirectoryName(xmlPath);
        if (string.IsNullOrWhiteSpace(sourceDir))
            return;

        var xmlBaseName = Path.GetFileNameWithoutExtension(xmlPath);
        var projectBaseName = appNamespace.Split('.').First();
        var targetProjectIni = Path.Combine(outputDir, projectBaseName + ".ini");

        foreach (var extension in new[] { ".ini", ".security" })
        {
            var source = Path.Combine(sourceDir, xmlBaseName + extension);
            if (!File.Exists(source))
                continue;
            var targetPath = extension.Equals(".ini", StringComparison.OrdinalIgnoreCase)
                ? targetProjectIni
                : Path.Combine(outputDir, Path.GetFileName(source));
            File.Copy(source, targetPath, overwrite: true);
        }

        if (!File.Exists(targetProjectIni))
        {
            var magicIniSource = Path.Combine(sourceDir, "Magic.ini");
            if (File.Exists(magicIniSource))
                File.Copy(magicIniSource, targetProjectIni, overwrite: true);
        }

        var securitySource = Path.Combine(sourceDir, "security");
        if (File.Exists(securitySource))
            File.Copy(securitySource, Path.Combine(outputDir, "security"), overwrite: true);
    }

    private static void CopyProjectResourceFolders(string xmlPath, string solutionRoot)
    {
        var sourceDir = Path.GetDirectoryName(xmlPath);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(sourceDir))
        {
            candidates.Add(Path.Combine(sourceDir, "Resources"));
            candidates.Add(Path.Combine(sourceDir, "resources"));
        }

        if (!string.IsNullOrWhiteSpace(_runtimeRootOverride))
        {
            candidates.Add(Path.Combine(_runtimeRootOverride!, "Resources"));
            candidates.Add(Path.Combine(_runtimeRootOverride!, "resources"));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(candidate))
                continue;

            var targetRoot = Path.Combine(solutionRoot, Path.GetFileName(candidate));
            foreach (var sourceFile in Directory.GetFiles(candidate, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(candidate, sourceFile);
                var targetFile = Path.Combine(targetRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                File.Copy(sourceFile, targetFile, overwrite: true);
            }

            return;
        }
    }

    private static void MaterializeBundledRuntime(string solutionRoot, string runtimeCoreReferenceMode, string? runtimeCoreDllPath)
    {
        var runtimeAssetRoot = ResolveRuntimeAssetRoot();
        var runtimeProjectZipPath = string.IsNullOrWhiteSpace(runtimeAssetRoot) ? null : Path.Combine(runtimeAssetRoot, RuntimeCoreProjectName + ".zip");
        var legacyRuntimeProjectZipPath = string.IsNullOrWhiteSpace(runtimeAssetRoot) ? null : Path.Combine(runtimeAssetRoot, "ENV.zip");
        var runtimeZipPath = !string.IsNullOrWhiteSpace(runtimeProjectZipPath) && File.Exists(runtimeProjectZipPath)
            ? runtimeProjectZipPath
            : legacyRuntimeProjectZipPath;
        var runtimeBoxDllPath = string.IsNullOrWhiteSpace(runtimeAssetRoot) ? null : Path.Combine(runtimeAssetRoot, RuntimeBoxAssemblyName + ".dll");
        var runtimeLibDir = string.IsNullOrWhiteSpace(runtimeAssetRoot) ? null : Path.Combine(runtimeAssetRoot, "lib");

        if (!string.Equals(runtimeCoreReferenceMode, "Dll", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(runtimeZipPath) && File.Exists(runtimeZipPath))
        {
            var runtimeTargetDir = Path.Combine(solutionRoot, RuntimeCoreProjectName);
            if (Directory.Exists(runtimeTargetDir))
                Directory.Delete(runtimeTargetDir, recursive: true);
            System.IO.Compression.ZipFile.ExtractToDirectory(runtimeZipPath, runtimeTargetDir, overwriteFiles: true);
            var runtimeBoxProjectPath = Path.Combine(runtimeTargetDir, RuntimeBoxProjectFileName);
            var runtimeEnvProjectPath = Path.Combine(runtimeTargetDir, RuntimeEnvProjectRelativePath);
            if (File.Exists(runtimeBoxProjectPath) && File.Exists(runtimeEnvProjectPath))
            {
                NormalizeEnvProject(runtimeEnvProjectPath, useRuntimeCoreAssemblyName: false);
                EnsureEnvSqliteSupport(Path.GetDirectoryName(runtimeEnvProjectPath) ?? runtimeTargetDir, runtimeEnvProjectPath);
            }
            else
            {
                var legacyRuntimeProjectPath = Path.Combine(runtimeTargetDir, LegacyRuntimeProjectName + ".csproj");
                var runtimeProjectPath = Path.Combine(runtimeTargetDir, RuntimeCoreProjectName + ".csproj");
                if (File.Exists(legacyRuntimeProjectPath) && !File.Exists(runtimeProjectPath))
                    File.Move(legacyRuntimeProjectPath, runtimeProjectPath);
                NormalizeEnvProject(runtimeProjectPath);
                EnsureEnvSqliteSupport(runtimeTargetDir, runtimeProjectPath);
            }
        }
        else if (!string.Equals(runtimeCoreReferenceMode, "Dll", StringComparison.OrdinalIgnoreCase))
        {
            MaterializeWorkspaceRuntimeProject(solutionRoot);
        }
        else if (string.Equals(runtimeCoreReferenceMode, "Dll", StringComparison.OrdinalIgnoreCase))
        {
            var resolvedRuntimeCoreDll = ResolveRuntimeCoreDllSource(runtimeCoreDllPath);
            if (string.IsNullOrWhiteSpace(resolvedRuntimeCoreDll) && !string.IsNullOrWhiteSpace(runtimeAssetRoot))
            {
                var bundledEnvDll = Path.Combine(runtimeAssetRoot, LegacyRuntimeProjectName + ".dll");
                if (File.Exists(bundledEnvDll))
                    resolvedRuntimeCoreDll = bundledEnvDll;
            }

            if (!string.IsNullOrWhiteSpace(resolvedRuntimeCoreDll) && File.Exists(resolvedRuntimeCoreDll))
            {
                var runtimeAssemblyName = GetAssemblyNameOrDefault(resolvedRuntimeCoreDll, RuntimeCoreProjectName);
                var runtimeCoreTargetPath = Path.Combine(GetRuntimeExternalRefsDirectory(solutionRoot), runtimeAssemblyName + ".dll");
                Directory.CreateDirectory(Path.GetDirectoryName(runtimeCoreTargetPath)!);
                File.Copy(resolvedRuntimeCoreDll, runtimeCoreTargetPath, overwrite: true);
                MaterializeManagedRuntimeDependencies(solutionRoot, resolvedRuntimeCoreDll);

                if (UsesLegacyRuntimeAssemblyIdentity(resolvedRuntimeCoreDll))
                    File.Copy(resolvedRuntimeCoreDll, Path.Combine(GetRuntimeExternalRefsDirectory(solutionRoot), LegacyRuntimeProjectName + ".dll"), overwrite: true);
            }

            var resolvedRuntimeBoxDll = ResolveRuntimeBoxDllSource(runtimeCoreDllPath, runtimeAssetRoot);
            if (!string.IsNullOrWhiteSpace(resolvedRuntimeBoxDll) && File.Exists(resolvedRuntimeBoxDll))
            {
                Directory.CreateDirectory(GetRuntimeExternalRefsDirectory(solutionRoot));
                File.Copy(resolvedRuntimeBoxDll, Path.Combine(GetRuntimeExternalRefsDirectory(solutionRoot), RuntimeBoxAssemblyName + ".dll"), overwrite: true);
                MaterializeManagedRuntimeDependencies(solutionRoot, resolvedRuntimeBoxDll);
            }
        }

        if (string.Equals(runtimeCoreReferenceMode, "Dll", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(runtimeBoxDllPath) &&
            File.Exists(runtimeBoxDllPath))
        {
            Directory.CreateDirectory(GetRuntimeExternalRefsDirectory(solutionRoot));
            File.Copy(runtimeBoxDllPath, Path.Combine(GetRuntimeExternalRefsDirectory(solutionRoot), RuntimeBoxAssemblyName + ".dll"), overwrite: true);
        }

        if (!string.IsNullOrWhiteSpace(runtimeLibDir) && Directory.Exists(runtimeLibDir))
        {
            var libTargetDir = Path.Combine(GetRuntimeExternalRefsDirectory(solutionRoot), "lib");
            Directory.CreateDirectory(libTargetDir);
            foreach (var sourceFile in Directory.GetFiles(runtimeLibDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(runtimeLibDir, sourceFile);
                var targetFile = Path.Combine(libTargetDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                File.Copy(sourceFile, targetFile, overwrite: true);
            }
        }

        MaterializeWorkspaceSqliteAssemblies(solutionRoot);
        MaterializeMagicGateways(solutionRoot);
    }

    private static void MaterializeManagedRuntimeDependencies(string solutionRoot, string anchorDllPath)
    {
        var sourceDir = Path.GetDirectoryName(anchorDllPath);
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return;

        var targetDir = GetRuntimeExternalRefsDirectory(solutionRoot);
        Directory.CreateDirectory(targetDir);
        foreach (var sourcePath in Directory.GetFiles(sourceDir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (!IsManagedAssembly(sourcePath))
                continue;

            CopyFileReplacingReadOnly(sourcePath, Path.Combine(targetDir, Path.GetFileName(sourcePath)));
        }
    }

    private static void RemoveLegacyRuntimeLayout(string solutionRoot)
    {
        var root = Path.GetFullPath(solutionRoot);
        if (!Directory.Exists(root))
            return;

        foreach (var directoryName in new[] { "x64", "x86", "Gateways" })
        {
            DeleteDirectoryIfUnderRoot(root, Path.Combine(root, directoryName));
            DeleteDirectoryIfUnderRoot(root, Path.Combine(GetRuntimeExternalRefsDirectory(root), directoryName));
        }

        foreach (var fileName in new[]
        {
            "ENV.dll",
            RuntimeCoreProjectName + ".dll",
            RuntimeBoxAssemblyName + ".dll",
            "System.Data.SQLite.dll",
            "SQLite.Interop.dll",
            "sqlite3.dll",
            "System.Resources.Extensions.dll"
        })
        {
            DeleteFileIfUnderRoot(root, Path.Combine(root, fileName));
        }
    }

    private static void DeleteDirectoryIfUnderRoot(string root, string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!IsPathUnderRoot(root, fullPath) || !Directory.Exists(fullPath))
            return;

        try
        {
            ClearReadOnlyAttributes(fullPath);
            Directory.Delete(fullPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
    }

    private static void DeleteFileIfUnderRoot(string root, string file)
    {
        var fullPath = Path.GetFullPath(file);
        if (!IsPathUnderRoot(root, fullPath) || !File.Exists(fullPath))
            return;

        try
        {
            File.SetAttributes(fullPath, File.GetAttributes(fullPath) & ~FileAttributes.ReadOnly);
            File.Delete(fullPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsPathUnderRoot(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void MaterializeWorkspaceRuntimeProject(string solutionRoot)
    {
        var sourceRuntimeDir = Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName);
        if (!Directory.Exists(sourceRuntimeDir))
            return;

        var targetRuntimeDir = Path.Combine(solutionRoot, RuntimeCoreProjectName);
        if (Directory.Exists(targetRuntimeDir))
            Directory.Delete(targetRuntimeDir, recursive: true);

        foreach (var sourcePath in Directory.GetFiles(sourceRuntimeDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRuntimeDir, sourcePath);
            if (relativePath.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;

            if (relativePath.Equals(LegacyRuntimeProjectName + ".csproj", StringComparison.OrdinalIgnoreCase))
                relativePath = RuntimeCoreProjectName + ".csproj";

            var targetPath = Path.Combine(targetRuntimeDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }

        var runtimeProjectPath = Path.Combine(targetRuntimeDir, RuntimeCoreProjectName + ".csproj");
        NormalizeEnvProject(runtimeProjectPath);
        EnsureEnvSqliteSupport(targetRuntimeDir, runtimeProjectPath);
    }

    private static void MaterializeMagicGateways(string solutionRoot)
    {
        var gatewaySourceRoot = ResolveMagicGatewayRoot();
        if (string.IsNullOrWhiteSpace(gatewaySourceRoot) || !Directory.Exists(gatewaySourceRoot))
            return;

        var gatewayTargetDir = Path.Combine(GetRuntimeExternalRefsDirectory(solutionRoot), "Gateways");
        var gatewayCopies = new List<(string SourcePath, string TargetFile)>();

        foreach (var gatewayName in new[] { "mgsqlite.dll", "mglocal.dll", "mgmemory.dll" })
        {
            var sourcePath = Directory.GetFiles(gatewaySourceRoot, gatewayName, SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                continue;
            if (!IsPeMachine(sourcePath, 0x8664))
                continue;

            gatewayCopies.Add((sourcePath, Path.Combine(gatewayTargetDir, Path.GetFileName(sourcePath))));
        }

        if (gatewayCopies.Count > 0)
        {
            Directory.CreateDirectory(gatewayTargetDir);
            foreach (var gatewayCopy in gatewayCopies)
                CopyFileReplacingReadOnly(gatewayCopy.SourcePath, gatewayCopy.TargetFile);
        }

        foreach (var sqliteRuntime in ResolveMagicSqliteRuntimeFiles())
        {
            if (File.Exists(sqliteRuntime.SourcePath))
            {
                var targetDir = Path.Combine(GetRuntimeExternalRefsDirectory(solutionRoot), sqliteRuntime.RelativeTargetDirectory);
                Directory.CreateDirectory(targetDir);
                CopyFileReplacingReadOnly(sqliteRuntime.SourcePath, Path.Combine(targetDir, Path.GetFileName(sqliteRuntime.SourcePath)));
            }
        }
    }

    private static string GetRuntimeExternalRefsDirectory(string solutionRoot)
        => Path.Combine(solutionRoot, ExternalRefsDirectoryName, RuntimeExternalRefsDirectoryName);

    private static void MaterializeExternalDllReferences(string containerRoot, IReadOnlyDictionary<string, string> dllReferenceMap)
    {
        if (dllReferenceMap.Count == 0)
            return;

        foreach (var mapping in dllReferenceMap)
        {
            if (!File.Exists(mapping.Value))
                continue;

            var targetDir = Path.Combine(containerRoot, "ExternalRefs", mapping.Key);
            Directory.CreateDirectory(targetDir);

            var targetDll = Path.Combine(targetDir, Path.GetFileName(mapping.Value));
            CopyFileReplacingReadOnly(mapping.Value, targetDll);

            var manifestPath = ProjectManifest.GetManifestPath(mapping.Value);
            if (File.Exists(manifestPath))
                CopyFileReplacingReadOnly(manifestPath, Path.Combine(targetDir, Path.GetFileName(manifestPath)));
        }
    }

    private static void CopyFileReplacingReadOnly(string sourcePath, string destinationPath)
    {
        // Loaded runtime assemblies are commonly locked by Visual Studio. Avoid
        // replacing an identical file so incremental reconversions can proceed
        // while the generated solution remains open.
        if (File.Exists(destinationPath) && FilesHaveSameContent(sourcePath, destinationPath))
            return;

        if (File.Exists(destinationPath))
        {
            var attributes = File.GetAttributes(destinationPath);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(destinationPath, attributes & ~FileAttributes.ReadOnly);
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static bool FilesHaveSameContent(string firstPath, string secondPath)
    {
        var firstInfo = new FileInfo(firstPath);
        var secondInfo = new FileInfo(secondPath);
        if (firstInfo.Length != secondInfo.Length)
            return false;

        const int bufferSize = 81920;
        using var first = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, FileOptions.SequentialScan);
        using var second = new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, FileOptions.SequentialScan);
        var firstBuffer = new byte[bufferSize];
        var secondBuffer = new byte[bufferSize];
        while (true)
        {
            var firstRead = first.Read(firstBuffer, 0, firstBuffer.Length);
            var secondRead = second.Read(secondBuffer, 0, secondBuffer.Length);
            if (firstRead != secondRead)
                return false;
            if (firstRead == 0)
                return true;
            if (!firstBuffer.AsSpan(0, firstRead).SequenceEqual(secondBuffer.AsSpan(0, secondRead)))
                return false;
        }
    }

    private static void NormalizeEnvProject(string envProjectPath, bool useRuntimeCoreAssemblyName = true)
    {
        if (!File.Exists(envProjectPath))
            return;

        var text = File.ReadAllText(envProjectPath);
        var assemblyName = useRuntimeCoreAssemblyName ? RuntimeCoreProjectName : LegacyRuntimeProjectName;

        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"<RootNamespace>.*?</RootNamespace>",
            $"<RootNamespace>{LegacyRuntimeProjectName}</RootNamespace>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"<AssemblyName>.*?</AssemblyName>",
            $"<AssemblyName>{assemblyName}</AssemblyName>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

        if (!text.Contains("<GenerateResourceUsePreserializedResources>", StringComparison.OrdinalIgnoreCase))
        {
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"(<TargetFrameworkVersion>v4\.7\.2</TargetFrameworkVersion>)",
                "$1\r\n    <GenerateResourceUsePreserializedResources>true</GenerateResourceUsePreserializedResources>\r\n    <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>\r\n    <RestoreProjectStyle>PackageReference</RestoreProjectStyle>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        if (!text.Contains("System.Resources.Extensions", StringComparison.OrdinalIgnoreCase))
        {
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"\s*<Import Project=""\$\(MSBuildBinPath\)\\Microsoft\.CSharp\.targets"" />",
                "\r\n  <ItemGroup>\r\n    <PackageReference Include=\"System.Resources.Extensions\" Version=\"8.0.0\" />\r\n  </ItemGroup>\r\n  <Import Project=\"$(MSBuildBinPath)\\Microsoft.CSharp.targets\" />",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        if (!text.Contains("<Reference Include=\"System.Resources.Extensions\"", StringComparison.OrdinalIgnoreCase))
        {
            var resourcesReference =
                "  <ItemGroup>\r\n" +
                "    <Reference Include=\"System.Resources.Extensions\">\r\n" +
                "      <SpecificVersion>False</SpecificVersion>\r\n" +
                "      <HintPath>..\\lib\\System.Resources.Extensions.dll</HintPath>\r\n" +
                "    </Reference>\r\n" +
                "  </ItemGroup>";
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"\s*<Import Project=""\$\(MSBuildBinPath\)\\Microsoft\.CSharp\.targets"" />",
                "\r\n" + resourcesReference + "\r\n  <Import Project=\"$(MSBuildBinPath)\\Microsoft.CSharp.targets\" />",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        if (!text.Contains("Reference Include=\"System.Data.SQLite\"", StringComparison.OrdinalIgnoreCase))
        {
            var itextReference = "    <Reference Include=\"itextsharp\">\r\n      <SpecificVersion>False</SpecificVersion>\r\n      <HintPath>..\\lib\\itextsharp.dll</HintPath>\r\n    </Reference>";
            if (text.Contains(itextReference, StringComparison.Ordinal))
            {
                var sqliteReference = itextReference
                    + "\r\n    <Reference Include=\"System.Data.SQLite\">\r\n      <SpecificVersion>False</SpecificVersion>\r\n      <HintPath>..\\lib\\System.Data.SQLite.dll</HintPath>\r\n    </Reference>"
                    + "\r\n    <Reference Include=\"System.Resources.Extensions\">\r\n      <SpecificVersion>False</SpecificVersion>\r\n      <HintPath>..\\lib\\System.Resources.Extensions.dll</HintPath>\r\n    </Reference>";
                text = text.Replace(itextReference, sqliteReference, StringComparison.Ordinal);
            }
        }
        else if (!text.Contains("<Reference Include=\"System.Resources.Extensions\"", StringComparison.OrdinalIgnoreCase))
        {
            var sqliteReference = "    <Reference Include=\"System.Data.SQLite\">\r\n      <SpecificVersion>False</SpecificVersion>\r\n      <HintPath>..\\lib\\System.Data.SQLite.dll</HintPath>\r\n    </Reference>";
            if (text.Contains(sqliteReference, StringComparison.Ordinal))
            {
                var updatedReferences = sqliteReference
                    + "\r\n    <Reference Include=\"System.Resources.Extensions\">\r\n      <SpecificVersion>False</SpecificVersion>\r\n      <HintPath>..\\lib\\System.Resources.Extensions.dll</HintPath>\r\n    </Reference>";
                text = text.Replace(sqliteReference, updatedReferences, StringComparison.Ordinal);
            }
        }

        var envProjectDir = Path.GetDirectoryName(envProjectPath) ?? "";
        var localSqliteProvider = Path.Combine(envProjectDir, "Data", "DataProvider", "SQLiteEntityDataProvider.cs");
        var workspaceSqliteProvider = Path.Combine(
            Directory.GetCurrentDirectory(),
            LegacyRuntimeProjectName,
            "Data",
            "DataProvider",
            "SQLiteEntityDataProvider.cs");
        if ((File.Exists(localSqliteProvider) || File.Exists(workspaceSqliteProvider)) &&
            !text.Contains("Compile Include=\"Data\\DataProvider\\SQLiteEntityDataProvider.cs\"", StringComparison.OrdinalIgnoreCase))
        {
            var marker = "    <Compile Include=\"Data\\DataProvider\\DynamicSQLSupportingDataProvider.cs\" />";
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                text = text.Replace(
                    marker,
                    marker + "\r\n    <Compile Include=\"Data\\DataProvider\\SQLiteEntityDataProvider.cs\" />",
                    StringComparison.Ordinal);
            }
        }

        File.WriteAllText(envProjectPath, text);
    }

    private static void NormalizeReferencedProjectEnvironments(IReadOnlyDictionary<string, string> projectReferenceMap, string envReferenceMode)
    {
        if (string.Equals(envReferenceMode, "Dll", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var projectPath in projectReferenceMap.Values.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var projectDir = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrWhiteSpace(projectDir))
                continue;

            var envProjectCandidates = new[]
            {
                Path.GetFullPath(Path.Combine(projectDir, "..", RuntimeCoreProjectName, RuntimeEnvProjectRelativePath)),
                Path.GetFullPath(Path.Combine(projectDir, "..", RuntimeCoreProjectName, RuntimeCoreProjectName + ".csproj")),
                Path.GetFullPath(Path.Combine(projectDir, "..", LegacyRuntimeProjectName, LegacyRuntimeProjectName + ".csproj"))
            };
            var envProjectPath = envProjectCandidates.FirstOrDefault(File.Exists) ?? envProjectCandidates[0];
            var envProjectDir = Path.GetDirectoryName(envProjectPath);
            if (!string.IsNullOrWhiteSpace(envProjectDir))
            {
                NormalizeEnvProject(
                    envProjectPath,
                    useRuntimeCoreAssemblyName: !envProjectPath.EndsWith(RuntimeEnvProjectRelativePath, StringComparison.OrdinalIgnoreCase));
                EnsureEnvSqliteSupport(envProjectDir, envProjectPath);
            }
        }
    }

    private static void EnsureEnvSqliteSupport(string envProjectDir, string envProjectPath)
    {
        if (!Directory.Exists(envProjectDir) || !File.Exists(envProjectPath))
            return;

        var workspaceRoot = Directory.GetCurrentDirectory();
        var sqliteProviderSource = Path.Combine(workspaceRoot, "ENV", "Data", "DataProvider", "SQLiteEntityDataProvider.cs");
        if (File.Exists(sqliteProviderSource))
        {
            var targetPath = Path.Combine(envProjectDir, "Data", "DataProvider", "SQLiteEntityDataProvider.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sqliteProviderSource, targetPath, overwrite: true);
        }

        foreach (var supportDll in new[] { "System.Data.SQLite.dll", "System.Resources.Extensions.dll" })
        {
            var supportDllPath = ResolveSupportDllSource(workspaceRoot, supportDll);
            if (!File.Exists(supportDllPath))
                continue;

            var targetLibDir = Path.Combine(envProjectDir, "..", "lib");
            targetLibDir = Path.GetFullPath(targetLibDir);
            Directory.CreateDirectory(targetLibDir);
            File.Copy(supportDllPath, Path.Combine(targetLibDir, supportDll), overwrite: true);
        }
    }

    private static string ResolveSupportDllSource(string workspaceRoot, string supportDll)
    {
        var candidates = new List<string>
        {
            Path.Combine(workspaceRoot, "lib", supportDll)
        };

        var runtimeAssetRoot = ResolveRuntimeAssetRoot();
        if (!string.IsNullOrWhiteSpace(runtimeAssetRoot))
            candidates.Add(Path.Combine(runtimeAssetRoot, "lib", supportDll));

        if (!string.IsNullOrWhiteSpace(_runtimeRootOverride))
        {
            var runtimeRootDll = FindFileRecursively(_runtimeRootOverride!, supportDll);
            if (!string.IsNullOrWhiteSpace(runtimeRootDll))
                candidates.Add(runtimeRootDll);
        }

        candidates.Add(Path.Combine(workspaceRoot, RuntimeCoreProjectName, "bin", "Release", "net472", supportDll));
        candidates.Add(Path.Combine(workspaceRoot, RuntimeCoreProjectName, "bin", "Debug", "net472", supportDll));
        candidates.Add(Path.Combine(@"D:\Projetos_CSharp\XPARuntimeCore\bin\Release\net472", supportDll));

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static void MaterializeWorkspaceSqliteAssemblies(string solutionRoot)
    {
        var workspaceRoot = Directory.GetCurrentDirectory();
        var workspaceLibDir = Path.Combine(workspaceRoot, "lib");
        if (!Directory.Exists(workspaceLibDir))
            return;

        var solutionLibDir = Path.Combine(GetRuntimeExternalRefsDirectory(solutionRoot), "lib");
        Directory.CreateDirectory(solutionLibDir);

        foreach (var fileName in new[] { "System.Data.SQLite.dll", "System.Resources.Extensions.dll", "SQLite.Interop.dll" })
        {
            var sourcePath = Path.Combine(workspaceLibDir, fileName);
            if (File.Exists(sourcePath))
            {
                if (string.Equals(fileName, "SQLite.Interop.dll", StringComparison.OrdinalIgnoreCase) &&
                    !IsPeMachine(sourcePath, 0x8664))
                    continue;

                if (string.Equals(fileName, "System.Data.SQLite.dll", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(GetRuntimeExternalRefsDirectory(solutionRoot), fileName)))
                    continue;

                File.Copy(sourcePath, Path.Combine(solutionLibDir, fileName), overwrite: true);
                File.Copy(sourcePath, Path.Combine(GetRuntimeExternalRefsDirectory(solutionRoot), fileName), overwrite: true);
            }
        }
    }

    private static string? ResolveRuntimeCoreDllSource(string? explicitRuntimeCoreDllPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitRuntimeCoreDllPath))
            candidates.Add(explicitRuntimeCoreDllPath!);
        if (!string.IsNullOrWhiteSpace(_runtimeRootOverride))
        {
            var runtimeEnvDll = FindFileRecursively(_runtimeRootOverride!, RuntimeCoreProjectName + ".dll")
                                ?? FindFileRecursively(_runtimeRootOverride!, LegacyRuntimeProjectName + ".dll");
            if (!string.IsNullOrWhiteSpace(runtimeEnvDll))
                candidates.Add(runtimeEnvDll);
        }
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, "bin", "Debug", RuntimeCoreProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, "bin", "Release", RuntimeCoreProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, RuntimeCoreProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, LegacyRuntimeProjectName, "bin", "Debug", LegacyRuntimeProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, LegacyRuntimeProjectName, "bin", "Release", LegacyRuntimeProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, "bin", "Debug", RuntimeCoreProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, "bin", "Release", RuntimeCoreProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, "bin", "Debug", LegacyRuntimeProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), LegacyRuntimeProjectName, "bin", "Release", LegacyRuntimeProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "Northwind", "bin", "Debug", RuntimeCoreProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "Northwind", "bin", "Release", RuntimeCoreProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "Northwind", "bin", "Debug", LegacyRuntimeProjectName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "Northwind", "bin", "Release", LegacyRuntimeProjectName + ".dll"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveRuntimeBoxDllSource(string? explicitRuntimeCoreDllPath, string? runtimeAssetRoot)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(explicitRuntimeCoreDllPath))
        {
            var explicitDir = Path.GetDirectoryName(explicitRuntimeCoreDllPath!);
            if (!string.IsNullOrWhiteSpace(explicitDir))
                candidates.Add(Path.Combine(explicitDir, RuntimeBoxAssemblyName + ".dll"));
        }

        if (!string.IsNullOrWhiteSpace(runtimeAssetRoot))
            candidates.Add(Path.Combine(runtimeAssetRoot!, RuntimeBoxAssemblyName + ".dll"));

        if (!string.IsNullOrWhiteSpace(_runtimeRootOverride))
        {
            var runtimeBoxDll = FindFileRecursively(_runtimeRootOverride!, RuntimeBoxAssemblyName + ".dll");
            if (!string.IsNullOrWhiteSpace(runtimeBoxDll))
                candidates.Add(runtimeBoxDll);
        }

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, "bin", "Debug", RuntimeBoxAssemblyName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, "bin", "Release", RuntimeBoxAssemblyName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, RuntimeBoxAssemblyName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, "ENV", "bin", "Debug", RuntimeBoxAssemblyName + ".dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), RuntimeCoreProjectName, "ENV", "bin", "Release", RuntimeBoxAssemblyName + ".dll"));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string GetAssemblyNameOrDefault(string assemblyPath, string fallback)
    {
        try
        {
            return System.Reflection.AssemblyName.GetAssemblyName(assemblyPath).Name ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool IsManagedAssembly(string assemblyPath)
    {
        try
        {
            System.Reflection.AssemblyName.GetAssemblyName(assemblyPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool UsesLegacyRuntimeAssemblyIdentity(string assemblyPath)
    {
        try
        {
            return string.Equals(
                System.Reflection.AssemblyName.GetAssemblyName(assemblyPath).Name,
                LegacyRuntimeProjectName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveRuntimeAssetRoot()
    {
        if (!string.IsNullOrWhiteSpace(_runtimeRootOverride) && Directory.Exists(_runtimeRootOverride))
            return _runtimeRootOverride;

        var probeRoots = new List<string?>
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(ConversionRunner).Assembly.Location),
            Directory.GetCurrentDirectory()
        };

        foreach (var root in probeRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var candidate in EnumerateRuntimeAssetCandidates(root!))
            {
                if (Directory.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static string? ResolveMagicGatewayRoot()
    {
        if (!string.IsNullOrWhiteSpace(_runtimeRootOverride) && Directory.Exists(_runtimeRootOverride))
        {
            var recursiveGateways = FindDirectoryRecursively(_runtimeRootOverride!, "Gateways");
            if (!string.IsNullOrWhiteSpace(recursiveGateways))
                return recursiveGateways;
        }

        var candidates = new[]
        {
            @"D:\Magic\Gateways",
            @"D:\Magic\x64\Gateways"
        };

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static IEnumerable<(string SourcePath, string RelativeTargetDirectory)> ResolveMagicSqliteRuntimeFiles()
    {
        var candidates = new List<(string SourcePath, string RelativeTargetDirectory)>();

        var sqlite64 = !string.IsNullOrWhiteSpace(_runtimeRootOverride)
            ? FirstExistingPath(
                  Path.Combine(_runtimeRootOverride!, "sqlite3.dll"),
                  Path.Combine(_runtimeRootOverride!, "x64", "sqlite3.dll"))
              ?? FindFileRecursively(Path.Combine(_runtimeRootOverride!, "x64"), "sqlite3.dll")
              ?? FindFileRecursively(_runtimeRootOverride!, "sqlite3.dll")
            : @"D:\Magic\x64\sqlite3.dll";
        if (File.Exists(sqlite64) && IsPeMachine(sqlite64, 0x8664))
            candidates.Add((sqlite64, "."));

        var systemDataSqlite = !string.IsNullOrWhiteSpace(_runtimeRootOverride)
            ? FirstExistingPath(
                  Path.Combine(_runtimeRootOverride!, "System.Data.SQLite.dll"),
                  Path.Combine(_runtimeRootOverride!, "System.Data.SQLite.DLL"))
              ?? FindFileRecursively(_runtimeRootOverride!, "System.Data.SQLite.dll")
              ?? FindFileRecursively(_runtimeRootOverride!, "System.Data.SQLite.DLL")
            : FirstExistingPath(
                  @"D:\Magic\RIAModules\DesktopModules\System.Data.SQLite.DLL",
                  @"D:\Magic\RIAModules\Desktop\System.Data.SQLite.DLL")
              ?? FindFileRecursively(@"D:\Magic\RIAModules", "System.Data.SQLite.DLL")
              ?? FindFileRecursively(@"D:\Magic", "System.Data.SQLite.DLL");
        var sqliteInterop64 = !string.IsNullOrWhiteSpace(_runtimeRootOverride)
            ? FirstExistingPath(
                  Path.Combine(_runtimeRootOverride!, "SQLite.Interop.dll"),
                  Path.Combine(_runtimeRootOverride!, "x64", "SQLite.Interop.dll"))
              ?? FindFileRecursively(Path.Combine(_runtimeRootOverride!, "x64"), "SQLite.Interop.dll")
              ?? FindFileRecursively(_runtimeRootOverride!, "SQLite.Interop.dll")
            : FirstExistingPath(
                  @"D:\Magic\RIAModules\DesktopModules\SQLite.Interop.dll",
                  @"D:\Magic\RIAModules\Desktop\SQLite.Interop.dll")
              ?? FindFileRecursively(@"D:\Magic\RIAModules", "SQLite.Interop.dll")
              ?? FindFileRecursively(@"D:\Magic", "SQLite.Interop.dll");

        var hasManagedNativePair =
            File.Exists(systemDataSqlite) &&
            File.Exists(sqliteInterop64) &&
            IsPeMachine(sqliteInterop64, 0x8664) &&
            AreSqliteRuntimeFilesVersionCompatible(systemDataSqlite, sqliteInterop64);
        if (hasManagedNativePair)
            candidates.Add((systemDataSqlite!, "."));
        if (hasManagedNativePair)
            candidates.Add((sqliteInterop64!, "."));

        return candidates;
    }

    private static bool AreSqliteRuntimeFilesVersionCompatible(string systemDataSqlitePath, string sqliteInteropPath)
    {
        try
        {
            var managedVersion = System.Reflection.AssemblyName.GetAssemblyName(systemDataSqlitePath).Version?.ToString();
            var nativeVersion = FileVersionInfo.GetVersionInfo(sqliteInteropPath).FileVersion;
            return !string.IsNullOrWhiteSpace(managedVersion) &&
                   !string.IsNullOrWhiteSpace(nativeVersion) &&
                   string.Equals(managedVersion, nativeVersion, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPeMachine(string path, ushort expectedMachine)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            stream.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = reader.ReadInt32();
            stream.Seek(peOffset + 4, SeekOrigin.Begin);
            return reader.ReadUInt16() == expectedMachine;
        }
        catch
        {
            return false;
        }
    }

    private static string? FirstExistingPath(params string[] paths)
        => paths.FirstOrDefault(File.Exists);

    private static string? FindFileRecursively(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;

        try
        {
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindDirectoryRecursively(string root, string directoryName)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;

        try
        {
            return Directory.EnumerateDirectories(root, directoryName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateRuntimeAssetCandidates(string startDirectory)
    {
        var current = Path.GetFullPath(startDirectory);
        while (!string.IsNullOrWhiteSpace(current))
        {
            yield return Path.Combine(current, "assets", "runtime");
            yield return Path.Combine(current, "XpaConverterMvp", "assets", "runtime");
            yield return Path.Combine(current, "tools", "XpaConverterMvp", "assets", "runtime");

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                yield break;
            current = parent;
        }
    }

    private static void WriteSolutionFile(string solutionRoot, string appNamespace, IReadOnlyList<string> components, bool includeEnvProject)
    {
        var solutionPath = Path.Combine(solutionRoot, $"{appNamespace.Split('.').First()}.sln");
        var projectEntries = new List<(string Name, string RelativePath, Guid TypeGuid, Guid ProjectGuid)>();
        if (includeEnvProject)
        {
            var runtimeBoxProjectPath = Path.Combine(solutionRoot, RuntimeCoreProjectName, RuntimeBoxProjectFileName);
            var runtimeEnvProjectPath = Path.Combine(solutionRoot, RuntimeCoreProjectName, RuntimeEnvProjectRelativePath);
            if (File.Exists(runtimeBoxProjectPath) && File.Exists(runtimeEnvProjectPath))
            {
                projectEntries.Add((
                    RuntimeBoxAssemblyName,
                    NormalizeSolutionPath(Path.Combine(RuntimeCoreProjectName, RuntimeBoxProjectFileName)),
                    new Guid("FAE04EC0-301F-11D3-BF4B-00C04F79EFBC"),
                    CreateDeterministicGuid(RuntimeBoxAssemblyName)));
                projectEntries.Add((
                    LegacyRuntimeProjectName,
                    NormalizeSolutionPath(Path.Combine(RuntimeCoreProjectName, RuntimeEnvProjectRelativePath)),
                    new Guid("FAE04EC0-301F-11D3-BF4B-00C04F79EFBC"),
                    CreateDeterministicGuid(LegacyRuntimeProjectName)));
            }
            else
            {
                projectEntries.Add((
                    RuntimeCoreProjectName,
                    NormalizeSolutionPath(Path.Combine(RuntimeCoreProjectName, RuntimeCoreProjectName + ".csproj")),
                    new Guid("FAE04EC0-301F-11D3-BF4B-00C04F79EFBC"),
                    CreateDeterministicGuid(RuntimeCoreProjectName)));
            }
        }

        var mainProjectFolder = appNamespace.Split('.').First();
        projectEntries.Add((
            mainProjectFolder,
            NormalizeSolutionPath(Path.Combine(mainProjectFolder, $"{mainProjectFolder}.csproj")),
            new Guid("9A19103F-16F7-4668-BE54-9A1E7A4F7556"),
            CreateDeterministicGuid(mainProjectFolder)));

        foreach (var component in components.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            projectEntries.Add((
                component,
                NormalizeSolutionPath(Path.Combine(component, $"{component}.csproj")),
                new Guid("9A19103F-16F7-4668-BE54-9A1E7A4F7556"),
                CreateDeterministicGuid(component)));
        }

        var lines = new List<string>
        {
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            "# Visual Studio Version 17"
        };

        foreach (var project in projectEntries)
        {
            lines.Add($"Project(\"{{{project.TypeGuid.ToString().ToUpperInvariant()}}}\") = \"{project.Name}\", \"{project.RelativePath}\", \"{{{project.ProjectGuid.ToString().ToUpperInvariant()}}}\"");
            lines.Add("EndProject");
        }

        lines.Add("Global");
        lines.Add("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        lines.Add("\t\tDebug|Any CPU = Debug|Any CPU");
        lines.Add("\t\tRelease|Any CPU = Release|Any CPU");
        lines.Add("\tEndGlobalSection");
        lines.Add("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (var project in projectEntries)
        {
            var guid = project.ProjectGuid.ToString().ToUpperInvariant();
            lines.Add($"\t\t{{{guid}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            lines.Add($"\t\t{{{guid}}}.Debug|Any CPU.Build.0 = Debug|Any CPU");
            lines.Add($"\t\t{{{guid}}}.Release|Any CPU.ActiveCfg = Release|Any CPU");
            lines.Add($"\t\t{{{guid}}}.Release|Any CPU.Build.0 = Release|Any CPU");
        }
        lines.Add("\tEndGlobalSection");
        lines.Add("\tGlobalSection(SolutionProperties) = preSolution");
        lines.Add("\t\tHideSolutionNode = FALSE");
        lines.Add("\tEndGlobalSection");
        lines.Add("EndGlobal");

        File.WriteAllLines(solutionPath, lines);
    }

    private static Guid CreateDeterministicGuid(string value)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash);
    }

    private static string NormalizeSolutionPath(string path) => path.Replace(Path.DirectorySeparatorChar, '\\');
}


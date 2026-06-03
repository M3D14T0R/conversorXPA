using System.Diagnostics;
using System.Text;

namespace XpaConverterMvp;

internal static class ConversionTelemetry
{
    private static readonly object _gate = new();
    private static readonly UTF8Encoding _utf8NoBom = new(false);
    private static readonly TimeSpan _statusWriteInterval = TimeSpan.FromMilliseconds(500);
    private const int LogFlushLineThreshold = 128;
    private static string? _logPath;
    private static string? _statusPath;
    private static Stopwatch? _session;
    private static StreamWriter? _logWriter;
    private static int _pendingLogLineCount;
    private static DateTimeOffset _lastStatusWriteAt;

    public static string? LogPath
    {
        get
        {
            lock (_gate)
                return _logPath;
        }
    }

    public static void Initialize(string outputDir, ConversionOptions options, string appNamespace)
    {
        lock (_gate)
        {
            DisposeState();

            var telemetryDir = Path.Combine(outputDir, "_telemetry");
            Directory.CreateDirectory(telemetryDir);
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            _logPath = Path.Combine(telemetryDir, $"conversion-{stamp}.log");
            _statusPath = Path.Combine(telemetryDir, "current-status.txt");
            _logWriter = new StreamWriter(_logPath, append: false, _utf8NoBom)
            {
                AutoFlush = false
            };
            _pendingLogLineCount = 0;
            _lastStatusWriteAt = DateTimeOffset.MinValue;
            _session = Stopwatch.StartNew();

            WriteLineUnsafe("SESSION", $"run-start xml={Quote(options.XmlPath)} output={Quote(options.OutputDir)} app={Quote(appNamespace)} withDependencies={options.WithDependencies} fullSolution={options.FullSolution} parallelTasks={options.ParallelTaskGeneration} incrementalOutput={options.IncrementalOutput} folderFilter={Quote(options.FolderFilter)} tasks={Quote(string.Join(", ", options.TaskFilters))} taskRanges={Quote(string.Join(", ", options.TaskRanges))}");
            WriteLineUnsafe("SESSION", $"command-preview={Quote(BuildCommandPreview(options, appNamespace))}");
        }
    }

    public static void Log(string category, string message)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_logPath))
                return;
            WriteLineUnsafe(category, message);
        }
    }

    public static void LogDuration(string category, string name, TimeSpan elapsed, string? extra = null)
    {
        var payload = $"{name} elapsedMs={elapsed.TotalMilliseconds:F0}";
        if (!string.IsNullOrWhiteSpace(extra))
            payload += $" {extra}";
        Log(category, payload);
    }

    public static void CloseSuccess(int taskCount, int dataObjectCount, int fieldModelCount)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_logPath))
                return;
            var elapsedMs = _session?.Elapsed.TotalMilliseconds ?? 0;
            WriteLineUnsafe("SESSION", $"run-success elapsedMs={elapsedMs:F0} tasks={taskCount} models={dataObjectCount} types={fieldModelCount}");
            DisposeState();
        }
    }

    public static void CloseFailure(Exception ex)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_logPath))
                return;
            var elapsedMs = _session?.Elapsed.TotalMilliseconds ?? 0;
            WriteLineUnsafe("SESSION", $"run-failure elapsedMs={elapsedMs:F0} type={Quote(ex.GetType().FullName)} message={Quote(ex.Message)}");
            WriteLineUnsafe("SESSION", Quote(ex.ToString()));
            DisposeState();
        }
    }

    public static void CloseAborted(string reason)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_logPath))
                return;
            var elapsedMs = _session?.Elapsed.TotalMilliseconds ?? 0;
            WriteLineUnsafe("SESSION", $"run-aborted elapsedMs={elapsedMs:F0} reason={Quote(reason)}");
            DisposeState();
        }
    }

    private static void WriteLineUnsafe(string category, string message)
    {
        var line = $"{DateTimeOffset.Now:O}\t{category}\t{message}{Environment.NewLine}";
        _logWriter!.Write(line);
        _pendingLogLineCount++;
        if (_pendingLogLineCount >= LogFlushLineThreshold || string.Equals(category, "SESSION", StringComparison.Ordinal))
            FlushLogUnsafe();
        WriteStatusUnsafe(category, message);
    }

    private static string Quote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "\"\"";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string BuildCommandPreview(ConversionOptions options, string appNamespace)
    {
        var command = ResolveExecutablePreview();
        var args = new List<string>
        {
            options.XmlPath,
            options.OutputDir,
            appNamespace,
            "--output-type",
            options.OutputType,
            "--runtime-core-ref",
            options.RuntimeCoreReferenceMode
        };

        if (!string.IsNullOrWhiteSpace(options.RuntimeCoreDllPath))
        {
            args.Add("--runtime-core-dll");
            args.Add(options.RuntimeCoreDllPath!);
        }

        if (!string.IsNullOrWhiteSpace(options.RuntimeRootPath))
        {
            args.Add("--runtime-root");
            args.Add(options.RuntimeRootPath!);
        }

        if (!string.IsNullOrWhiteSpace(options.FolderFilter))
        {
            args.Add("--folder");
            args.Add(options.FolderFilter!);
        }

        foreach (var task in options.TaskFilters.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            args.Add("--task");
            args.Add(task);
        }

        foreach (var taskRange in options.TaskRanges)
        {
            args.Add("--task-range");
            args.Add(taskRange.ToString());
        }

        foreach (var tablesXml in options.TablesXmlPaths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            args.Add("--tables-xml");
            args.Add(tablesXml);
        }

        foreach (var componentXml in options.ComponentXmlPaths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            args.Add("--component-xml");
            args.Add(componentXml);
        }

        foreach (var projectReference in options.ProjectReferencePaths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            args.Add("--project-ref");
            args.Add(projectReference);
        }

        foreach (var projectReference in options.ProjectReferenceMap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            args.Add("--project-ref-map");
            args.Add($"{projectReference.Key}={projectReference.Value}");
        }

        foreach (var dllReference in options.DllReferenceMap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            args.Add("--dll-ref-map");
            args.Add($"{dllReference.Key}={dllReference.Value}");
        }

        if (options.DisableImplicitComponentXmlResolution)
            args.Add("--no-implicit-component-xml");
        if (options.WithDependencies)
            args.Add("--with-dependencies");
        if (options.FullSolution)
            args.Add("--full-solution");
        if (options.ParallelTaskGeneration)
            args.Add("--parallel-tasks");
        if (options.IncrementalOutput)
            args.Add("--incremental-output");

        return command + " " + string.Join(" ", args.Select(QuotePowerShellArgument));
    }

    private static string ResolveExecutablePreview()
    {
        var assemblyLocation = typeof(ConversionTelemetry).Assembly.Location;
        if (!string.IsNullOrWhiteSpace(assemblyLocation) &&
            string.Equals(Path.GetExtension(assemblyLocation), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            return $"dotnet {QuotePowerShellArgument(assemblyLocation)}";
        }

        return Environment.ProcessPath ?? "XpaConverterMvp";
    }

    private static string QuotePowerShellArgument(string value)
    {
        if (value.Length == 0)
            return "''";
        if (value.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '\'', '"', '`', '&', '|', '<', '>', ';', '(', ')', '[', ']', '{', '}', '$' }) < 0)
            return value;
        return "'" + value.Replace("'", "''") + "'";
    }

    private static void WriteStatusUnsafe(string category, string message)
    {
        if (string.IsNullOrWhiteSpace(_statusPath))
            return;

        var now = DateTimeOffset.Now;
        if (!string.Equals(category, "SESSION", StringComparison.Ordinal) &&
            now - _lastStatusWriteAt < _statusWriteInterval)
            return;

        var elapsedMs = _session?.Elapsed.TotalMilliseconds ?? 0;
        var content = new StringBuilder();
        content.AppendLine($"timestamp={now:O}");
        content.AppendLine($"elapsedMs={elapsedMs:F0}");
        content.AppendLine($"category={category}");
        content.AppendLine($"message={message}");
        File.WriteAllText(_statusPath!, content.ToString(), _utf8NoBom);
        _lastStatusWriteAt = now;
    }

    private static void FlushLogUnsafe()
    {
        if (_logWriter is null)
            return;

        _logWriter.Flush();
        _pendingLogLineCount = 0;
    }

    private static void DisposeState()
    {
        _session?.Stop();
        FlushLogUnsafe();
        _logWriter?.Dispose();
        _logWriter = null;
        _pendingLogLineCount = 0;
        _lastStatusWriteAt = DateTimeOffset.MinValue;
        _session = null;
        _logPath = null;
        _statusPath = null;
    }
}

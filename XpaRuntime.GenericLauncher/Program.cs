using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace XpaRuntime.GenericLauncher
{
    internal static class Program
    {
        private static readonly HashSet<string> ProbeDirectories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object ControllerTraceSync = new object();
        private static readonly Dictionary<object, Stack<ControllerTraceState>>
            ControllerTraceStates =
                new Dictionary<object, Stack<ControllerTraceState>>(
                    ReferenceEqualityComparer.Instance);
        private static int ControllerTraceCallbacks;
        [ThreadStatic]
        private static Stack<ControllerTraceState> ActiveControllerTraceStates;
        private static bool HandlerTraceAttached;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                var options = LauncherOptions.Load(args);
                ConfigureAssemblyResolution(options);
                WriteTrace(
                    "launcher ready mode=" + options.ResolveMode() +
                    " assembly=" + options.ApplicationAssembly);

                if (options.ValidateOnly)
                    return Validate(options);

                return options.ResolveMode() == LaunchMode.Process
                    ? RunAsProcess(options)
                    : RunInCurrentProcess(options);
            }
            catch (Exception exception)
            {
                var actual = Unwrap(exception);
                WriteFailureLog(actual);
                MessageBox.Show(
                    actual.ToString(),
                    "XPA Runtime Generic Launcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
        }

        private static int Validate(LauncherOptions options)
        {
            if (!File.Exists(options.ApplicationAssembly))
                throw new FileNotFoundException(
                    "O assembly configurado não foi encontrado.",
                    options.ApplicationAssembly);

            AssemblyName.GetAssemblyName(options.ApplicationAssembly);
            if (options.ResolveMode() == LaunchMode.InProcess)
            {
                var assembly = Assembly.LoadFrom(options.ApplicationAssembly);
                ResolveEntryPoint(assembly);
            }

            return 0;
        }

        private static int RunAsProcess(LauncherOptions options)
        {
            var stagedProbeFiles = StageProcessProbeFiles(options);
            WriteTrace(
                "process probe files staged=" + stagedProbeFiles +
                " workingDirectory=" + options.WorkingDirectory);

            var startInfo = new ProcessStartInfo
            {
                FileName = options.ApplicationAssembly,
                Arguments = BuildCommandLine(options.ApplicationArguments),
                WorkingDirectory = options.WorkingDirectory,
                UseShellExecute = false
            };

            var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("Não foi possível iniciar a aplicação convertida.");

            if (!options.WaitForExit)
                return 0;

            process.WaitForExit();
            return process.ExitCode;
        }

        private static int StageProcessProbeFiles(LauncherOptions options)
        {
            if (options.ProbePaths.Count == 0)
                return 0;

            var workingDirectory = Path.GetFullPath(options.WorkingDirectory);
            var manifestPath = Path.Combine(
                workingDirectory,
                ".xpa-launcher-probe-files.txt");
            var previousFiles = ReadStagedProbeFiles(manifestPath);
            var candidates = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var probePath in options.ProbePaths)
            {
                if (string.IsNullOrWhiteSpace(probePath) ||
                    !Directory.Exists(probePath) ||
                    PathsEqual(probePath, workingDirectory))
                {
                    continue;
                }

                foreach (var sourcePath in Directory.EnumerateFiles(
                             probePath,
                             "*.dll",
                             SearchOption.TopDirectoryOnly))
                {
                    var fileName = Path.GetFileName(sourcePath);
                    if (!candidates.ContainsKey(fileName))
                        candidates.Add(fileName, sourcePath);
                }
            }

            var currentFiles = new Dictionary<string, StagedProbeFile>(
                StringComparer.OrdinalIgnoreCase);
            var stagedCount = 0;
            foreach (var candidate in candidates.OrderBy(item => item.Key))
            {
                var fileName = candidate.Key;
                var sourcePath = candidate.Value;
                var destinationPath = Path.Combine(workingDirectory, fileName);
                var canReplace = options.RefreshProbeFiles ||
                                 !File.Exists(destinationPath) ||
                                 IsUnchangedStagedFile(
                                     destinationPath,
                                     previousFiles,
                                     fileName);
                if (!canReplace)
                    continue;

                var sourceInfo = new FileInfo(sourcePath);
                var requiresCopy = !File.Exists(destinationPath);
                if (!requiresCopy)
                {
                    var destinationInfo = new FileInfo(destinationPath);
                    requiresCopy =
                        destinationInfo.Length != sourceInfo.Length ||
                        destinationInfo.LastWriteTimeUtc != sourceInfo.LastWriteTimeUtc;
                }

                if (requiresCopy)
                {
                    File.Copy(sourcePath, destinationPath, true);
                    stagedCount++;
                }

                currentFiles[fileName] = new StagedProbeFile(
                    fileName,
                    sourceInfo.Length,
                    sourceInfo.LastWriteTimeUtc.Ticks,
                    sourcePath);
            }

            foreach (var previous in previousFiles.Values)
            {
                if (currentFiles.ContainsKey(previous.FileName))
                    continue;

                var destinationPath = Path.Combine(
                    workingDirectory,
                    previous.FileName);
                if (IsUnchangedStagedFile(
                        destinationPath,
                        previousFiles,
                        previous.FileName))
                {
                    currentFiles[previous.FileName] = previous;
                }
            }

            WriteStagedProbeFiles(manifestPath, currentFiles.Values);
            return stagedCount;
        }

        private static Dictionary<string, StagedProbeFile> ReadStagedProbeFiles(
            string manifestPath)
        {
            var result = new Dictionary<string, StagedProbeFile>(
                StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(manifestPath))
                return result;

            foreach (var line in File.ReadAllLines(manifestPath))
            {
                var parts = line.Split(new[] { '\t' }, 4);
                if (parts.Length != 4 ||
                    string.IsNullOrWhiteSpace(parts[0]) ||
                    !long.TryParse(
                        parts[1],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var length) ||
                    !long.TryParse(
                        parts[2],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var writeTimeUtcTicks))
                {
                    continue;
                }

                var fileName = Path.GetFileName(parts[0]);
                result[fileName] = new StagedProbeFile(
                    fileName,
                    length,
                    writeTimeUtcTicks,
                    parts[3]);
            }

            return result;
        }

        private static bool IsUnchangedStagedFile(
            string destinationPath,
            IReadOnlyDictionary<string, StagedProbeFile> previousFiles,
            string fileName)
        {
            if (!File.Exists(destinationPath) ||
                !previousFiles.TryGetValue(fileName, out var previous))
            {
                return false;
            }

            var destinationInfo = new FileInfo(destinationPath);
            return destinationInfo.Length == previous.Length &&
                   destinationInfo.LastWriteTimeUtc.Ticks ==
                   previous.SourceWriteTimeUtcTicks;
        }

        private static void WriteStagedProbeFiles(
            string manifestPath,
            IEnumerable<StagedProbeFile> files)
        {
            var lines = files
                .OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(file =>
                    file.FileName + "\t" +
                    file.Length.ToString(CultureInfo.InvariantCulture) + "\t" +
                    file.SourceWriteTimeUtcTicks.ToString(
                        CultureInfo.InvariantCulture) + "\t" +
                    file.SourcePath)
                .ToArray();
            File.WriteAllLines(manifestPath, lines, Encoding.UTF8);
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private sealed class StagedProbeFile
        {
            public StagedProbeFile(
                string fileName,
                long length,
                long sourceWriteTimeUtcTicks,
                string sourcePath)
            {
                FileName = fileName;
                Length = length;
                SourceWriteTimeUtcTicks = sourceWriteTimeUtcTicks;
                SourcePath = sourcePath;
            }

            public string FileName { get; }
            public long Length { get; }
            public long SourceWriteTimeUtcTicks { get; }
            public string SourcePath { get; }
        }

        private static int RunInCurrentProcess(LauncherOptions options)
        {
            var stagedProbeFiles = StageProcessProbeFiles(options);
            WriteTrace(
                "in-process probe files staged=" + stagedProbeFiles +
                " workingDirectory=" + options.WorkingDirectory);
            Directory.SetCurrentDirectory(options.WorkingDirectory);
            var assembly = Assembly.LoadFrom(options.ApplicationAssembly);
            WriteTrace("application assembly loaded: " + assembly.FullName);
            AttachControllerTrace(options);
            using (StartRuntimeProfiler(options))
            {
                var entryPoint = ResolveEntryPoint(assembly);
                if (TryRunGeneratedApplication(
                        assembly,
                        entryPoint,
                        options,
                        out var generatedExitCode))
                {
                    return generatedExitCode;
                }

                ConfigureRuntimeSecurity(options);
                WriteTrace(
                    "runtime security configured user=" + options.CurrentUser +
                    " administrator=" + options.CurrentUserIsAdministrator);
                var parameters = entryPoint.GetParameters();
                object[] invocationArguments;
                if (parameters.Length == 0)
                {
                    invocationArguments = null;
                }
                else if (parameters.Length == 1 &&
                         parameters[0].ParameterType == typeof(string[]))
                {
                    invocationArguments =
                        new object[] { options.ApplicationArguments.ToArray() };
                }
                else
                {
                    throw new InvalidOperationException(
                        "O entry point deve receber nenhum argumento ou somente string[].");
                }

                WriteTrace(
                    "invoking entry point " + entryPoint.DeclaringType?.FullName +
                    "." + entryPoint.Name);
                var result = entryPoint.Invoke(null, invocationArguments);
                var exitCode = result is int explicitExitCode
                    ? explicitExitCode
                    : Environment.ExitCode;
                WriteTrace("entry point returned exitCode=" + exitCode);
                return exitCode;
            }
        }

        private static void AttachControllerTrace(LauncherOptions options)
        {
            if (options.ControllerTraceFilters.Count == 0)
                return;

            var controllerType = AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(item => item.GetType("ENV.ControllerBase", false))
                .FirstOrDefault(item => item != null);
            if (controllerType == null)
            {
                controllerType = Assembly.Load(new AssemblyName("ENV"))
                    .GetType("ENV.ControllerBase", true);
            }
            var controller = Expression.Parameter(controllerType, "controller");
            var traceMethod = typeof(Program).GetMethod(
                nameof(TraceController),
                BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var eventAndStage in new[]
                     {
                         new { EventName = "BeforeExecute", Stage = "before" },
                         new { EventName = "AfterExecute", Stage = "after" }
                     })
            {
                var executeEvent = controllerType?.GetEvent(
                    eventAndStage.EventName,
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Static);
                if (executeEvent == null)
                    throw new MissingMemberException(
                        "ENV.ControllerBase." + eventAndStage.EventName +
                        " não foi encontrado para o diagnóstico.");

                var body = Expression.Call(
                    traceMethod,
                    Expression.Convert(controller, typeof(object)),
                    Expression.Constant(options.ControllerTraceFilters),
                    Expression.Constant(options.ControllerTraceFile),
                    Expression.Constant(eventAndStage.Stage));
                var handler = Expression.Lambda(
                        executeEvent.EventHandlerType,
                        body,
                        controller)
                    .Compile();
                executeEvent.AddEventHandler(null, handler);
            }
            AttachHandlerTrace(controllerType.Assembly);
            WriteTrace(
                "controller trace enabled filters=" +
                string.Join(",", options.ControllerTraceFilters) +
                " file=" + options.ControllerTraceFile);
        }

        private static void AttachHandlerTrace(Assembly envAssembly)
        {
            if (HandlerTraceAttached)
                return;

            var wrapperType = envAssembly.GetType(
                "ENV.Advanced.HandlerCollectionWrapper",
                false);
            var beforeHandler = wrapperType?.GetEvent(
                "BeforeHandler",
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Static);
            if (beforeHandler?.EventHandlerType == null)
                return;

            var handlerParameter = Expression.Parameter(
                typeof(object),
                "handler");
            var body = Expression.Call(
                typeof(Program).GetMethod(
                    nameof(TraceHandler),
                    BindingFlags.NonPublic | BindingFlags.Static),
                handlerParameter);
            var callback = Expression.Lambda(
                    beforeHandler.EventHandlerType,
                    body,
                    handlerParameter)
                .Compile();
            beforeHandler.AddEventHandler(null, callback);
            HandlerTraceAttached = true;
            WriteTrace("handler trace enabled");
        }

        private static void TraceHandler(object handler)
        {
            try
            {
                var states = ActiveControllerTraceStates;
                if (states == null || states.Count == 0)
                    return;

                var details = new List<object>
                {
                    handler?.GetType().FullName ?? "<NULL>"
                };
                foreach (var member in new[]
                         {
                             "_levelString",
                             "_isTemplate",
                             "_isUIControllerCustomCommand",
                             "_isExpression",
                             "_root"
                         })
                {
                    if (TryGetMemberValue(handler, member, out var value))
                        details.Add(member + "=" + FormatDiagnosticValue(value));
                }

                states.Peek().WriteExternalEvent(
                    "Handler",
                    details);
            }
            catch (Exception exception)
            {
                WriteTrace("handler trace failed: " + exception);
            }
        }

        private static IDisposable StartRuntimeProfiler(LauncherOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.RuntimeProfilerFile))
                return EmptyDisposable.Instance;

            try
            {
                var profilerType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Select(item => item.GetType("ENV.Utilities.Profiler", false))
                    .FirstOrDefault(item => item != null);
                if (profilerType == null)
                {
                    profilerType = Assembly.Load(new AssemblyName("ENV"))
                        .GetType("ENV.Utilities.Profiler", true);
                }

                var directory = Path.GetDirectoryName(options.RuntimeProfilerFile);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                var tracing = profilerType.GetProperty(
                    "Tracing",
                    BindingFlags.Public | BindingFlags.Static);
                tracing?.SetValue(null, options.RuntimeProfilerTrace, null);

                var profilerFile = profilerType.GetProperty(
                    "ProfilerFile",
                    BindingFlags.Public | BindingFlags.Static);
                if (profilerFile == null)
                {
                    throw new MissingMemberException(
                        "ENV.Utilities.Profiler.ProfilerFile não foi encontrado.");
                }

                profilerFile.SetValue(
                    null,
                    options.RuntimeProfilerFile,
                    null);
                WriteTrace(
                    "runtime profiler enabled file=" +
                    options.RuntimeProfilerFile +
                    " trace=" + options.RuntimeProfilerTrace);
                return new RuntimeProfilerSession(
                    profilerType,
                    options.RuntimeProfilerFile);
            }
            catch (Exception exception)
            {
                WriteTrace("runtime profiler could not be enabled: " + exception);
                return EmptyDisposable.Instance;
            }
        }

        private static ControllerTraceState StartControllerTraceState(
            object controller,
            string traceFile,
            string controllerName)
        {
            var state = new ControllerTraceState(
                controller,
                traceFile,
                controllerName);
            state.AttachRuntimeEvents();
            lock (ControllerTraceSync)
            {
                if (!ControllerTraceStates.TryGetValue(
                        controller,
                        out var states))
                {
                    states = new Stack<ControllerTraceState>();
                    ControllerTraceStates.Add(controller, states);
                }

                states.Push(state);
            }
            if (ActiveControllerTraceStates == null)
                ActiveControllerTraceStates =
                    new Stack<ControllerTraceState>();
            ActiveControllerTraceStates.Push(state);

            return state;
        }

        private static ControllerTraceState FinishControllerTraceState(
            object controller)
        {
            ControllerTraceState state = null;
            lock (ControllerTraceSync)
            {
                if (ControllerTraceStates.TryGetValue(
                        controller,
                        out var states) &&
                    states.Count > 0)
                {
                    state = states.Pop();
                    if (states.Count == 0)
                        ControllerTraceStates.Remove(controller);
                }
            }

            state?.Finish();
            if (state != null &&
                ActiveControllerTraceStates != null &&
                ActiveControllerTraceStates.Count > 0)
            {
                if (ReferenceEquals(
                        ActiveControllerTraceStates.Peek(),
                        state))
                {
                    ActiveControllerTraceStates.Pop();
                }
                else
                {
                    var preserved = ActiveControllerTraceStates
                        .Where(item => !ReferenceEquals(item, state))
                        .Reverse()
                        .ToArray();
                    ActiveControllerTraceStates.Clear();
                    foreach (var item in preserved)
                        ActiveControllerTraceStates.Push(item);
                }
            }
            return state;
        }

        private static void AppendDataViewDiagnostics(
            object controller,
            ICollection<string> values)
        {
            AppendMember(values, controller, "Title");
            AppendMember(values, controller, "Activity");
            AppendMember(values, controller, "From");
            AppendMember(values, controller, "OrderBy");
            AppendDataViewFilterDiagnostics(controller, values);
            AppendMember(
                values,
                controller,
                "StartFromFirstRowIfStartOnRowWhereFails");
            AppendCollectionMember(values, controller, "Relations", 100);
            AppendCollectionMember(values, controller, "Columns", 250);
        }

        private static void AppendDataViewFilterDiagnostics(
            object controller,
            ICollection<string> values)
        {
            AppendFilterMember(values, controller, "Where");
            AppendFilterMember(values, controller, "NonDbWhere");
            AppendFilterMember(values, controller, "StartOnRowWhere");
        }

        private static void AppendFilterMember(
            ICollection<string> values,
            object controller,
            string memberName)
        {
            if (!TryGetMemberValue(
                    controller,
                    memberName,
                    out var filter) ||
                filter == null)
            {
                return;
            }

            TryGetMemberValue(controller, "From", out var from);
            values.Add(
                "dataview." + memberName + "=" +
                FormatRelationFilter(filter, from));
        }

        private static void AppendMember(
            ICollection<string> values,
            object instance,
            string memberName)
        {
            if (TryGetMemberValue(instance, memberName, out var value))
            {
                values.Add(
                    "dataview." + memberName + "=" +
                    FormatDiagnosticValue(value));
            }
        }

        private static void AppendCollectionMember(
            ICollection<string> values,
            object instance,
            string memberName,
            int maximumItems)
        {
            if (!TryGetMemberValue(instance, memberName, out var collection) ||
                collection == null)
            {
                return;
            }

            if (!(collection is System.Collections.IEnumerable enumerable) ||
                collection is string)
            {
                values.Add(
                    "dataview." + memberName + "=" +
                    FormatDiagnosticValue(collection));
                return;
            }

            var index = 0;
            foreach (var item in enumerable)
            {
                if (index >= maximumItems)
                {
                    values.Add(
                        "dataview." + memberName +
                        "[...]=<truncated after " + maximumItems + " items>");
                    break;
                }

                values.Add(
                    "dataview." + memberName + "[" + index + "]=" +
                    FormatDiagnosticValue(item));
                index++;
            }

            values.Add("dataview." + memberName + ".Count=" + index);
        }

        private static bool TryGetMemberValue(
            object instance,
            string memberName,
            out object value)
        {
            value = null;
            if (instance == null)
                return false;

            for (var type = instance.GetType();
                 type != null && type != typeof(object);
                 type = type.BaseType)
            {
                var property = type.GetProperty(
                    memberName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
                if (property != null &&
                    property.GetIndexParameters().Length == 0)
                {
                    try
                    {
                        value = property.GetValue(instance, null);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }

                var field = type.GetField(
                    memberName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    try
                    {
                        value = field.GetValue(instance);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            return false;
        }

        private static string FormatDiagnosticValue(object value)
        {
            if (value == null)
                return "<NULL>";

            var type = value.GetType();
            var details = new List<string>();
            foreach (var name in new[]
                     {
                         "EntityName",
                         "Caption",
                         "Name",
                         "RelationType",
                         "RowFound"
                     })
            {
                if (TryGetMemberValue(value, name, out var detail) &&
                    detail != null)
                {
                    details.Add(name + "=" + detail);
                }
            }

            if (TryGetMemberValue(value, "_type", out var relationType) &&
                relationType != null)
            {
                details.Add("Type=" + relationType);
            }
            if (TryGetMemberValue(value, "From", out var relationFrom) &&
                relationFrom != null &&
                !ReferenceEquals(relationFrom, value))
            {
                details.Add(
                    "From=" + FormatDiagnosticIdentity(relationFrom));
            }
            if (TryGetMemberValue(value, "_filter", out var relationFilter) &&
                relationFilter != null)
            {
                details.Add(
                    "Filter=" + FormatRelationFilter(
                        relationFilter,
                        relationFrom));
            }

            string text;
            try
            {
                text = Convert.ToString(
                    value,
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                text = "<ToString failed>";
            }

            if (text != null && text.Length > 4096)
                text = text.Substring(0, 4096) + "...<truncated>";

            return type.FullName +
                   (details.Count == 0
                       ? string.Empty
                       : " {" + string.Join(", ", details) + "}") +
                   " value=" + text;
        }

        private static string FormatRelationFilter(
            object relationFilter,
            object relationFrom)
        {
            try
            {
                var filterType = relationFilter.GetType();
                var formatterField = filterType.GetField(
                    "FilterToDebugString",
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.FlattenHierarchy);
                var formatter = formatterField?.GetValue(null) as Delegate;
                if (formatter == null)
                {
                    return Convert.ToString(
                        relationFilter,
                        CultureInfo.InvariantCulture);
                }

                Type entityType = null;
                for (var type = relationFrom?.GetType();
                     type != null && type != typeof(object);
                     type = type.BaseType)
                {
                    if (string.Equals(
                            type.FullName,
                            "XPARuntimeCore.Box.Data.Entity",
                            StringComparison.Ordinal))
                    {
                        entityType = type;
                        break;
                    }
                }

                if (entityType == null)
                    return "<entity type unavailable>";

                var entities = Array.CreateInstance(
                    entityType,
                    relationFrom == null ? 0 : 1);
                if (relationFrom != null)
                    entities.SetValue(relationFrom, 0);

                var result = formatter.DynamicInvoke(
                    relationFilter,
                    entities);
                return Convert.ToString(
                    result,
                    CultureInfo.InvariantCulture);
            }
            catch (Exception exception)
            {
                var actual = exception is TargetInvocationException &&
                             exception.InnerException != null
                    ? exception.InnerException
                    : exception;
                return "<filter format failed: " +
                       actual.GetType().Name + ": " +
                       actual.Message + ">";
            }
        }

        private static string FormatDiagnosticIdentity(object value)
        {
            if (value == null)
                return "<NULL>";

            var details = new List<string>();
            foreach (var name in new[] { "EntityName", "Caption", "Name" })
            {
                if (TryGetMemberValue(value, name, out var member) &&
                    member != null)
                {
                    details.Add(name + "=" + member);
                }
            }

            return value.GetType().FullName +
                   (details.Count == 0
                       ? string.Empty
                       : "{" + string.Join(",", details) + "}");
        }

        private static Dictionary<string, string>
            CaptureControllerColumnValues(object controller)
        {
            var result = new Dictionary<string, string>(
                StringComparer.Ordinal);
            if (controller == null)
                return result;

            for (var type = controller.GetType();
                 type != null && type != typeof(object);
                 type = type.BaseType)
            {
                foreach (var field in type.GetFields(
                             BindingFlags.Instance |
                             BindingFlags.Public |
                             BindingFlags.NonPublic |
                             BindingFlags.DeclaredOnly))
                {
                    object column;
                    try
                    {
                        column = field.GetValue(controller);
                    }
                    catch
                    {
                        continue;
                    }

                    if (column == null ||
                        !IsColumnType(column.GetType()))
                    {
                        continue;
                    }

                    var key = field.Name;
                    if (result.ContainsKey(key))
                        key = type.FullName + "." + field.Name;

                    if (IsSensitiveDiagnosticName(field.Name))
                    {
                        result[key] = "<REDACTED>";
                        continue;
                    }

                    try
                    {
                        var valueProperty = column.GetType()
                            .GetProperties(
                                BindingFlags.Instance |
                                BindingFlags.Public)
                            .FirstOrDefault(property =>
                                string.Equals(
                                    property.Name,
                                    "Value",
                                    StringComparison.Ordinal) &&
                                property.GetIndexParameters().Length == 0);
                        var value = valueProperty?.GetValue(column, null);
                        var formatProperty = column.GetType()
                            .GetProperties(
                                BindingFlags.Instance |
                                BindingFlags.Public)
                            .FirstOrDefault(property =>
                                string.Equals(
                                    property.Name,
                                    "Format",
                                    StringComparison.Ordinal) &&
                                property.GetIndexParameters().Length == 0);
                        var format = formatProperty?.GetValue(column, null);
                        var valueText = value == null
                            ? "<NULL>"
                            : Convert.ToString(
                                    value,
                                    CultureInfo.InvariantCulture)
                                .Replace("\r", "\\r")
                                .Replace("\n", "\\n");
                        if (valueText.Length > 1024)
                        {
                            valueText =
                                valueText.Substring(0, 1024) +
                                "...<truncated>";
                        }
                        result[key] =
                            valueText +
                            (format == null
                                ? string.Empty
                                : " [format=" + format + "]");
                    }
                    catch (Exception exception)
                    {
                        result[key] =
                            "<read failed: " +
                            exception.GetType().Name + ">";
                    }
                }
            }

            return result;
        }

        private static bool IsSensitiveDiagnosticName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var normalized = name.ToLowerInvariant();
            return normalized.Contains("password") ||
                   normalized.Contains("passwd") ||
                   normalized.Contains("senha") ||
                   normalized.Contains("secret") ||
                   normalized.Contains("token") ||
                   normalized.Contains("connectionstring") ||
                   normalized.Contains("privatekey");
        }

        private static void TraceController(
            object controller,
            IReadOnlyList<string> filters,
            string traceFile,
            string stage)
        {
            try
            {
                var controllerType = controller?.GetType();
                var fullName = controllerType?.FullName ?? string.Empty;
                var callback = Interlocked.Increment(
                    ref ControllerTraceCallbacks);
                if (callback <= 20)
                {
                    WriteTrace(
                        "controller trace callback=" + callback +
                        " stage=" + stage +
                        " controller=" + fullName);
                }
                if (!filters.Any(filter =>
                        fullName.IndexOf(
                            filter,
                            StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return;
                }

                var values = new List<string>();
                ControllerTraceState traceState;
                if (string.Equals(
                        stage,
                        "before",
                        StringComparison.OrdinalIgnoreCase))
                {
                    traceState = StartControllerTraceState(
                        controller,
                        traceFile,
                        fullName);
                    values.Add(
                        "trace.startUtc=" +
                        traceState.StartUtc.ToString(
                            "O",
                            CultureInfo.InvariantCulture));
                    AppendDataViewDiagnostics(controller, values);
                }
                else
                {
                    traceState = FinishControllerTraceState(controller);
                    if (traceState != null)
                    {
                        values.Add(
                            "trace.elapsedMs=" +
                            traceState.ElapsedMilliseconds);
                        values.Add(
                            "trace.enterRows=" + traceState.EnterRows);
                        values.Add(
                            "trace.leaveRows=" + traceState.LeaveRows);
                        values.Add(
                            "trace.lastEvent=" + traceState.LastEvent);
                    }
                    else
                    {
                        values.Add("trace.state=<missing>");
                    }
                    AppendDataViewDiagnostics(controller, values);
                }
                values.Add(
                    "trace.thread=" +
                    Thread.CurrentThread.ManagedThreadId);
                foreach (var item in
                         CaptureControllerColumnValues(controller))
                {
                    values.Add(item.Key + "=" + item.Value);
                }

                TryAppendSpecialCharCleanUpProbe(
                    controller,
                    fullName,
                    values);
                TryAppendDynamicVariableProbe(
                    controller,
                    fullName,
                    values);

                var traceDirectory = Path.GetDirectoryName(traceFile);
                if (!string.IsNullOrWhiteSpace(traceDirectory))
                    Directory.CreateDirectory(traceDirectory);
                lock (ControllerTraceSync)
                {
                    File.AppendAllText(
                        traceFile,
                        DateTime.Now.ToString(
                            "yyyy-MM-dd HH:mm:ss.fff",
                            CultureInfo.InvariantCulture) +
                        " [" + stage + "] " + fullName +
                        " assembly=" + controllerType.Assembly.Location +
                        Environment.NewLine +
                        string.Join(Environment.NewLine, values) +
                        Environment.NewLine + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch (Exception exception)
            {
                WriteTrace("controller trace failed: " + exception);
            }
        }

        private static void TryAppendDynamicVariableProbe(
            object controller,
            string controllerName,
            ICollection<string> values)
        {
            if (!controllerName.EndsWith(
                    ".CK05547_5547MontaComandoSQLStoredProc",
                    StringComparison.Ordinal))
            {
                return;
            }

            var resolver = controller.GetType().GetMethod(
                "ResolveVariableCurrentByName",
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic);
            if (resolver == null)
            {
                values.Add("@probe.ResolveVariableCurrentByName=<MISSING>");
                return;
            }

            var userMethodsType = AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(item => item.GetType("ENV.UserMethods", false))
                .FirstOrDefault(item => item != null);
            var castToText = userMethodsType?.GetMethod(
                "CastToText",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(object) },
                null);
            if (userMethodsType == null || castToText == null)
            {
                values.Add("@probe.CastToText=<MISSING>");
                return;
            }

            var userMethods = Activator.CreateInstance(userMethodsType);
            foreach (var name in new[]
                     {
                         "P_Tipo 1",
                         "P_Parametro 1",
                         "P_Tipo 9",
                         "P_Parametro 9"
                     })
            {
                var selectedName = castToText.Invoke(
                    userMethods,
                    new object[] { name });
                var result = resolver.Invoke(
                    controller,
                    new[] { selectedName });
                values.Add(
                    "@probe.Resolve[" + name + "]=" +
                    (result == null ? "<NULL>" : result.ToString()));
            }

            var numberType = AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(item => item.GetType("XPARuntimeCore.Box.Number", false))
                .FirstOrDefault(item => item != null);
            var textType = AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(item => item.GetType("XPARuntimeCore.Box.Text", false))
                .FirstOrDefault(item => item != null);
            var parseNumber = numberType?.GetMethod(
                "Parse",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);
            var str = userMethodsType.GetMethod(
                "Str",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { numberType, textType },
                null);
            var trim = userMethodsType.GetMethod(
                "Trim",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { textType },
                null);
            var concatenate = textType?.GetMethod(
                "op_Addition",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { textType, textType },
                null);
            if (parseNumber == null ||
                str == null ||
                trim == null ||
                concatenate == null)
            {
                values.Add("@probe.DynamicNameConstruction=<MISSING>");
                return;
            }

            foreach (var index in new[] { 1, 9 })
            {
                var number = parseNumber.Invoke(
                    null,
                    new object[] { index.ToString(CultureInfo.InvariantCulture) });
                var format = castToText.Invoke(
                    userMethods,
                    new object[] { "2" });
                var formatted = str.Invoke(
                    userMethods,
                    new[] { number, format });
                var suffix = trim.Invoke(
                    userMethods,
                    new[] { formatted });
                var prefix = castToText.Invoke(
                    userMethods,
                    new object[] { "P_Tipo " });
                var selectedName = concatenate.Invoke(
                    null,
                    new[] { prefix, suffix });
                var nameText = selectedName?.ToString() ?? string.Empty;
                var result = resolver.Invoke(
                    controller,
                    new[] { selectedName });
                values.Add(
                    "@probe.DynamicResolve[index=" + index +
                    ",name=[" + nameText +
                    "],length=" + nameText.Length +
                    ",chars=" + string.Join(
                        ",",
                        nameText.Select(character =>
                            ((int)character).ToString(
                                CultureInfo.InvariantCulture))) +
                    "]=" +
                    (result == null ? "<NULL>" : result.ToString()));
            }
        }

        private static void TryAppendSpecialCharCleanUpProbe(
            object controller,
            string controllerName,
            ICollection<string> values)
        {
            if (!controllerName.EndsWith(
                    ".CG00590_BuscaCodigoMaterial",
                    StringComparison.Ordinal))
            {
                return;
            }

            var materialField = controller.GetType().GetField(
                "r_codigoMaterial",
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic);
            var materialColumn = materialField?.GetValue(controller);
            if (materialColumn == null)
                return;

            var userMethodsType = AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(item => item.GetType("ENV.UserMethods", false))
                .FirstOrDefault(item => item != null);
            var castToByteArray = userMethodsType?.GetMethod(
                "CastToByteArray",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(object) },
                null);
            if (userMethodsType == null || castToByteArray == null)
                return;

            var userMethods = Activator.CreateInstance(userMethodsType);
            var input = castToByteArray.Invoke(
                userMethods,
                new[] { materialColumn });
            values.Add(
                "@probe.MaterialColumnType=" +
                materialColumn.GetType().AssemblyQualifiedName);
            foreach (var property in materialColumn.GetType()
                         .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(property =>
                             string.Equals(
                                 property.Name,
                                 "Value",
                                 StringComparison.Ordinal) &&
                             property.GetIndexParameters().Length == 0))
            {
                var propertyValue = property.GetValue(materialColumn, null);
                values.Add(
                    "@probe.MaterialValue[" +
                    property.DeclaringType?.FullName +
                    ":" +
                    property.PropertyType.FullName +
                    "]=" +
                    (propertyValue == null
                        ? "<NULL>"
                        : propertyValue.ToString()));
            }
            if (input is byte[] inputBytes)
            {
                values.Add(
                    "@probe.CastInput=" +
                    BitConverter.ToString(inputBytes) +
                    " text=" +
                    Encoding.Default.GetString(inputBytes));
            }
            var functionsAssembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(item =>
                    string.Equals(
                        item.GetName().Name,
                        "CGFunctions",
                        StringComparison.OrdinalIgnoreCase))
                ?? Assembly.Load(new AssemblyName("CGFunctions"));
            var cleanup = functionsAssembly
                .GetType("CGFunctions.ComponentFunctions", true)
                .GetMethod(
                    "SpecialCharCleanUp",
                    BindingFlags.Public | BindingFlags.Static);
            var result = cleanup?.Invoke(null, new[] { input });
            values.Add(
                "@probe.SpecialCharCleanUp=" +
                (result == null ? "<NULL>" : result.ToString()));

            var applicationType = functionsAssembly.GetType(
                "CGFunctions.Application",
                true);
            var application = applicationType
                .GetProperty(
                    "Instance",
                    BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null, null);
            foreach (var fieldName in new[]
                     {
                         "p_textIn",
                         "v_textLen",
                         "v_textOut",
                         "v_currentChar",
                         "v_listIndex"
                     })
            {
                var field = applicationType.GetField(
                    fieldName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);
                var column = field?.GetValue(application);
                var valueProperty = column?.GetType()
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(property =>
                        string.Equals(
                            property.Name,
                            "Value",
                            StringComparison.Ordinal) &&
                        property.GetIndexParameters().Length == 0);
                var value = valueProperty?.GetValue(column, null);
                var display = value is byte[] bytes
                    ? BitConverter.ToString(bytes) +
                      " text=" +
                      Encoding.Default.GetString(bytes)
                    : value?.ToString();
                values.Add(
                    "@probe." + fieldName + "=" +
                    (display ?? "<NULL>"));
            }
        }

        private static bool IsColumnType(Type type)
        {
            while (type != null)
            {
                if (string.Equals(
                        type.FullName,
                        "XPARuntimeCore.Box.Data.Advanced.ColumnBase",
                        StringComparison.Ordinal))
                {
                    return true;
                }

                type = type.BaseType;
            }

            return false;
        }

        private static bool TryRunGeneratedApplication(
            Assembly assembly,
            MethodInfo entryPoint,
            LauncherOptions options,
            out int exitCode)
        {
            exitCode = 0;
            var programType = entryPoint.DeclaringType;
            var applicationType = programType == null
                ? null
                : assembly.GetType(programType.Namespace + ".Application", false);
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var byRefStringArray = typeof(string[]).MakeByRefType();
            var extractStartProgram = programType?.GetMethod(
                "ExtractStartProgram",
                flags,
                null,
                new[] { byRefStringArray },
                null);
            var extractRuntimeParameters = programType?.GetMethod(
                "ExtractRuntimeParameters",
                flags,
                null,
                new[] { byRefStringArray },
                null);
            var init = programType?.GetMethod(
                "Init",
                flags,
                null,
                new[] { typeof(string[]) },
                null);
            var applyRuntimeParameters = programType?.GetMethod(
                "ApplyRuntimeParameters",
                flags);
            var run = applicationType?.GetMethod(
                "Run",
                flags,
                null,
                new[] { typeof(string) },
                null);
            if (extractStartProgram == null ||
                extractRuntimeParameters == null ||
                init == null ||
                applyRuntimeParameters == null ||
                run == null)
            {
                return false;
            }

            var runtimeArguments = options.ApplicationArguments.ToArray();
            var startArguments = new object[] { runtimeArguments };
            var startProgram = extractStartProgram.Invoke(null, startArguments) as string;
            runtimeArguments = (string[])startArguments[0];

            var parameterArguments = new object[] { runtimeArguments };
            var runtimeParameters = extractRuntimeParameters.Invoke(null, parameterArguments);
            runtimeArguments = (string[])parameterArguments[0];

            WriteTrace("generated application init start");
            init.Invoke(null, new object[] { runtimeArguments });
            applyRuntimeParameters.Invoke(null, new[] { runtimeParameters });
            ConfigureRuntimeSecurity(options);
            WriteTrace(
                "runtime security configured after application init user=" +
                options.CurrentUser +
                " administrator=" +
                options.CurrentUserIsAdministrator);
            WriteTrace("generated application run startProgram=" + startProgram);
            run.Invoke(null, new object[] { startProgram });

            var userSettingsType = AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(item => item.GetType("ENV.UserSettings", false))
                .FirstOrDefault(item => item != null);
            userSettingsType?.GetMethod(
                    "FinalizeINI",
                    BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, null);
            exitCode = Environment.ExitCode;
            WriteTrace("generated application returned exitCode=" + exitCode);
            return true;
        }

        private static void ConfigureRuntimeSecurity(LauncherOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.CurrentUser))
                return;

            var userManagerType = AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(item => item.GetType("ENV.Security.UserManager", false))
                .FirstOrDefault(item => item != null);
            if (userManagerType == null)
            {
                foreach (var directory in ProbeDirectories)
                {
                    foreach (var runtimeAssemblyName in new[] { "ENV.dll", "XPARuntimeCore.Box.dll" })
                    {
                        var runtimeAssemblyPath = Path.Combine(directory, runtimeAssemblyName);
                        if (!File.Exists(runtimeAssemblyPath))
                            continue;

                        var runtimeAssembly = Assembly.LoadFrom(runtimeAssemblyPath);
                        userManagerType = runtimeAssembly.GetType("ENV.Security.UserManager", false);
                        if (userManagerType != null)
                            break;
                    }

                    if (userManagerType != null)
                        break;
                }
            }

            if (userManagerType == null)
                throw new TypeLoadException(
                    "Não foi possível localizar ENV.Security.UserManager para aplicar o login do launcher.");

            var useThisUser = userManagerType.GetMethod(
                "UseThisUser",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(bool) },
                null);
            if (useThisUser == null)
                throw new MissingMethodException(userManagerType.FullName, "UseThisUser(string, bool)");

            useThisUser.Invoke(
                null,
                new object[] { options.CurrentUser, options.CurrentUserIsAdministrator });

            var displayLoginDialog = userManagerType.GetField(
                "DisplayLoginDialog",
                BindingFlags.Public | BindingFlags.Static);
            displayLoginDialog?.SetValue(null, false);
        }

        private static MethodInfo ResolveEntryPoint(Assembly assembly)
        {
            if (assembly.EntryPoint != null)
                return assembly.EntryPoint;

            var programType = assembly.GetTypes().FirstOrDefault(type =>
                string.Equals(type.Name, "Program", StringComparison.Ordinal) ||
                string.Equals(type.FullName, "Program", StringComparison.Ordinal));
            var main = programType?.GetMethod(
                "Main",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (main == null)
            {
                throw new MissingMethodException(
                    assembly.FullName,
                    "EntryPoint ou Program.Main");
            }

            return main;
        }

        private static void ConfigureAssemblyResolution(LauncherOptions options)
        {
            AddProbeDirectory(AppDomain.CurrentDomain.BaseDirectory);
            AddProbeDirectory(Path.GetDirectoryName(options.ApplicationAssembly));
            foreach (var path in options.ProbePaths)
                AddProbeDirectory(path);

            AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
            {
                var requestedName = new AssemblyName(eventArgs.Name).Name;
                var loaded = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(item =>
                        !item.IsDynamic &&
                        string.Equals(
                            item.GetName().Name,
                            requestedName,
                            StringComparison.OrdinalIgnoreCase));
                if (loaded != null)
                    return loaded;

                foreach (var directory in ProbeDirectories)
                {
                    foreach (var extension in new[] { ".dll", ".exe" })
                    {
                        var candidate = Path.Combine(directory, requestedName + extension);
                        if (File.Exists(candidate))
                            return Assembly.LoadFrom(candidate);
                    }
                }

                return null;
            };
        }

        private static void AddProbeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
            if (Directory.Exists(expanded))
                ProbeDirectories.Add(Path.GetFullPath(expanded));
        }

        private static string BuildCommandLine(IEnumerable<string> arguments)
        {
            return string.Join(" ", arguments.Select(QuoteCommandLineArgument));
        }

        private static string QuoteCommandLineArgument(string value)
        {
            value = value ?? string.Empty;
            if (value.Length > 0 && value.All(ch => !char.IsWhiteSpace(ch) && ch != '"'))
                return value;

            var builder = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                builder.Append('\\', backslashes);
                backslashes = 0;
                builder.Append(character);
            }

            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }

        private static Exception Unwrap(Exception exception)
        {
            while (exception is TargetInvocationException invocation &&
                   invocation.InnerException != null)
            {
                exception = invocation.InnerException;
            }

            return exception;
        }

        private static void WriteFailureLog(Exception exception)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "XpaRuntime.GenericLauncher.error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                    Environment.NewLine +
                    exception +
                    Environment.NewLine +
                    Environment.NewLine);
            }
            catch
            {
                // O log não pode esconder o erro original.
            }
        }

        private sealed class ControllerTraceState
        {
            private static readonly string[] RuntimeEventNames =
            {
                "Load",
                "Start",
                "EnterRow",
                "RowChanging",
                "SavingRow",
                "AfterSavingRow",
                "LeaveRow",
                "ProcessingCommand",
                "ActivityChanged",
                "NoDataStateEntered",
                "BeforeExit",
                "AbortRowOccurred",
                "PreviewDatabaseError",
                "DatabaseErrorOccurred",
                "End"
            };

            private readonly object _controller;
            private readonly string _traceFile;
            private readonly string _controllerName;
            private readonly List<RuntimeEventSubscription> _subscriptions =
                new List<RuntimeEventSubscription>();
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private readonly Dictionary<string, string> _initialColumns;
            private int _eventSequence;
            private int _enterRows;
            private int _leaveRows;
            private string _lastEvent = "<none>";

            public ControllerTraceState(
                object controller,
                string traceFile,
                string controllerName)
            {
                _controller = controller;
                _traceFile = traceFile;
                _controllerName = controllerName;
                _initialColumns =
                    CaptureControllerColumnValues(controller);
                StartUtc = DateTime.UtcNow;
            }

            public DateTime StartUtc { get; }
            public int EnterRows => _enterRows;
            public int LeaveRows => _leaveRows;
            public string LastEvent => _lastEvent;
            public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;

            public void AttachRuntimeEvents()
            {
                object runtimeTask = null;
                string runtimeKind = null;
                if (TryGetMemberValue(
                        _controller,
                        "_businessProcess",
                        out runtimeTask) &&
                    runtimeTask != null)
                {
                    runtimeKind = "BusinessProcess";
                }
                else if (TryGetMemberValue(
                             _controller,
                             "_uiController",
                             out runtimeTask) &&
                         runtimeTask != null)
                {
                    runtimeKind = "UIController";
                }

                WriteLiveEvent(
                    "TaskAttached",
                    new object[]
                    {
                        "kind=" + (runtimeKind ?? "Unknown"),
                        "runtime=" +
                        (runtimeTask?.GetType().FullName ?? "<not found>")
                    });
                if (runtimeTask == null)
                    return;

                foreach (var eventName in RuntimeEventNames)
                {
                    SubscribeRuntimeEvent(
                        runtimeTask,
                        eventName,
                        eventName);
                }

                if (TryGetMemberValue(
                        _controller,
                        "Groups",
                        out var groups) &&
                    groups is System.Collections.IEnumerable enumerable)
                {
                    var groupIndex = 0;
                    foreach (var group in enumerable)
                    {
                        SubscribeRuntimeEvent(
                            group,
                            "Enter",
                            "Group[" + groupIndex + "].Enter");
                        SubscribeRuntimeEvent(
                            group,
                            "Leave",
                            "Group[" + groupIndex + "].Leave");
                        groupIndex++;
                    }
                }
            }

            private void SubscribeRuntimeEvent(
                object target,
                string runtimeEventName,
                string loggedEventName)
            {
                var eventInfo = target?.GetType().GetEvent(
                    runtimeEventName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);
                if (eventInfo?.EventHandlerType == null)
                    return;

                try
                {
                    var invoke = eventInfo.EventHandlerType.GetMethod(
                        "Invoke");
                    var parameters = invoke.GetParameters()
                        .Select(parameter =>
                            Expression.Parameter(
                                parameter.ParameterType,
                                parameter.Name))
                        .ToArray();
                    var arguments = Expression.NewArrayInit(
                        typeof(object),
                        parameters.Select(parameter =>
                            Expression.Convert(
                                parameter,
                                typeof(object))));
                    var body = Expression.Call(
                        Expression.Constant(this),
                        GetType().GetMethod(
                            nameof(OnRuntimeEvent),
                            BindingFlags.Instance |
                            BindingFlags.Public),
                        Expression.Constant(loggedEventName),
                        arguments);
                    var handler = Expression.Lambda(
                            eventInfo.EventHandlerType,
                            body,
                            parameters)
                        .Compile();
                    eventInfo.AddEventHandler(target, handler);
                    _subscriptions.Add(
                        new RuntimeEventSubscription(
                            target,
                            eventInfo,
                            handler));
                }
                catch (Exception exception)
                {
                    WriteLiveEvent(
                        "InstrumentationError",
                        new object[]
                        {
                            loggedEventName,
                            exception.GetType().Name,
                            exception.Message
                        });
                }
            }

            public void Finish()
            {
                _stopwatch.Stop();
                WriteColumnChanges();
                foreach (var subscription in _subscriptions)
                {
                    try
                    {
                        subscription.Event.RemoveEventHandler(
                            subscription.Target,
                            subscription.Handler);
                    }
                    catch (Exception exception)
                    {
                        WriteTrace(
                            "controller event trace detach failed: " +
                            exception);
                    }
                }

                _subscriptions.Clear();
                WriteLiveEvent(
                    "TaskDetached",
                    new object[]
                    {
                        "elapsedMs=" + ElapsedMilliseconds,
                        "enterRows=" + EnterRows,
                        "leaveRows=" + LeaveRows,
                        "lastEvent=" + LastEvent
                    });
            }

            private void WriteColumnChanges()
            {
                var finalColumns =
                    CaptureControllerColumnValues(_controller);
                var changes = new List<object>();
                foreach (var key in _initialColumns.Keys
                             .Union(finalColumns.Keys)
                             .OrderBy(item => item, StringComparer.Ordinal))
                {
                    _initialColumns.TryGetValue(
                        key,
                        out var initialValue);
                    finalColumns.TryGetValue(
                        key,
                        out var finalValue);
                    if (string.Equals(
                            initialValue,
                            finalValue,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    changes.Add(
                        key + ": " +
                        (initialValue ?? "<not present>") +
                        " -> " +
                        (finalValue ?? "<not present>"));
                    if (changes.Count >= 250)
                    {
                        changes.Add("<truncated after 250 changes>");
                        break;
                    }
                }

                WriteLiveEvent(
                    "ColumnChanges",
                    changes.Count == 0
                        ? new object[] { "<none>" }
                        : changes);
            }

            public void OnRuntimeEvent(
                string eventName,
                object[] arguments)
            {
                _lastEvent = eventName;
                if (string.Equals(
                        eventName,
                        "EnterRow",
                        StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _enterRows);
                }
                else if (string.Equals(
                             eventName,
                             "LeaveRow",
                             StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _leaveRows);
                }

                WriteLiveEvent(eventName, arguments);
                if (string.Equals(
                        eventName,
                        "EnterRow",
                        StringComparison.Ordinal) ||
                    string.Equals(
                        eventName,
                        "SavingRow",
                        StringComparison.Ordinal) ||
                    string.Equals(
                        eventName,
                        "LeaveRow",
                        StringComparison.Ordinal))
                {
                    WriteDataViewSnapshot(eventName);
                }
            }

            public void WriteExternalEvent(
                string eventName,
                IEnumerable<object> arguments)
            {
                WriteLiveEvent(eventName, arguments);
            }

            private void WriteDataViewSnapshot(string eventName)
            {
                var details = new List<object>();
                if (TryGetMemberValue(
                        _controller,
                        "Activity",
                        out var activity))
                {
                    details.Add("Activity=" + activity);
                }

                var filters = new List<string>();
                AppendDataViewFilterDiagnostics(_controller, filters);
                foreach (var filter in filters)
                    details.Add(filter);

                if (TryGetMemberValue(
                        _controller,
                        "Relations",
                        out var relations) &&
                    relations is System.Collections.IEnumerable enumerable)
                {
                    var index = 0;
                    foreach (var relation in enumerable)
                    {
                        details.Add(
                            "Relation[" + index + "]=" +
                            FormatDiagnosticValue(relation));
                        index++;
                    }
                    details.Add("RelationCount=" + index);
                }

                var columnIndex = 0;
                foreach (var column in
                         CaptureControllerColumnValues(_controller))
                {
                    if (columnIndex >= 250)
                    {
                        details.Add(
                            "Columns=<truncated after 250 items>");
                        break;
                    }

                    details.Add(
                        "Column[" + column.Key + "]=" +
                        column.Value);
                    columnIndex++;
                }
                details.Add("ColumnCount=" + columnIndex);

                WriteLiveEvent(
                    eventName + ".DataView",
                    details);
            }

            private void WriteLiveEvent(
                string eventName,
                IEnumerable<object> arguments)
            {
                try
                {
                    var directory = Path.GetDirectoryName(_traceFile);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);

                    var sequence = Interlocked.Increment(ref _eventSequence);
                    var argumentText = arguments == null
                        ? string.Empty
                        : string.Join(
                            " | ",
                            arguments.Select(FormatEventArgument));
                    var line =
                        DateTime.Now.ToString(
                            "yyyy-MM-dd HH:mm:ss.fff",
                            CultureInfo.InvariantCulture) +
                        " [event] " + _controllerName +
                        " #" + sequence +
                        " +" + _stopwatch.ElapsedMilliseconds + "ms" +
                        " thread=" + Thread.CurrentThread.ManagedThreadId +
                        " event=" + eventName +
                        (string.IsNullOrWhiteSpace(argumentText)
                            ? string.Empty
                            : " args=" + argumentText) +
                        Environment.NewLine;
                    lock (ControllerTraceSync)
                    {
                        File.AppendAllText(
                            _traceFile,
                            line,
                            Encoding.UTF8);
                    }
                }
                catch (Exception exception)
                {
                    WriteTrace(
                        "controller live event trace failed: " + exception);
                }
            }

            private static string FormatEventArgument(object argument)
            {
                if (argument == null)
                    return "<NULL>";

                var text = FormatDiagnosticValue(argument)
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n");
                return text.Length <= 2048
                    ? text
                    : text.Substring(0, 2048) + "...<truncated>";
            }
        }

        private sealed class RuntimeEventSubscription
        {
            public RuntimeEventSubscription(
                object target,
                EventInfo eventInfo,
                Delegate handler)
            {
                Target = target;
                Event = eventInfo;
                Handler = handler;
            }

            public object Target { get; }
            public EventInfo Event { get; }
            public Delegate Handler { get; }
        }

        private sealed class RuntimeProfilerSession : IDisposable
        {
            private readonly Type _profilerType;
            private readonly string _file;
            private bool _disposed;

            public RuntimeProfilerSession(Type profilerType, string file)
            {
                _profilerType = profilerType;
                _file = file;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;

                try
                {
                    _profilerType.GetMethod(
                            "ForceSaveCurrentProfilerFile",
                            BindingFlags.Public | BindingFlags.Static)
                        ?.Invoke(null, null);

                    var doNotProfile = _profilerType.GetMethod(
                        "DoNotProfile",
                        BindingFlags.Public | BindingFlags.Static);
                    var inactive =
                        doNotProfile != null &&
                        (bool)doNotProfile.Invoke(null, null);
                    if (!inactive)
                    {
                        _profilerType.GetMethod(
                                "EndProfilingSession",
                                BindingFlags.Public | BindingFlags.Static,
                                null,
                                new[] { typeof(string) },
                                null)
                            ?.Invoke(null, new object[] { _file });
                    }

                    WriteTrace("runtime profiler saved file=" + _file);
                }
                catch (Exception exception)
                {
                    WriteTrace(
                        "runtime profiler could not be finalized: " +
                        exception);
                }
            }
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance =
                new EmptyDisposable();

            public void Dispose()
            {
            }
        }

        private sealed class ReferenceEqualityComparer :
            IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance =
                new ReferenceEqualityComparer();

            public new bool Equals(object left, object right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(object value)
            {
                return RuntimeHelpers.GetHashCode(value);
            }
        }

        private static void WriteTrace(string message)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "XpaRuntime.GenericLauncher.trace.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                    " " + message + Environment.NewLine);
            }
            catch
            {
            }
        }
    }

    internal enum LaunchMode
    {
        Auto,
        Process,
        InProcess
    }

    internal sealed class LauncherOptions
    {
        private LauncherOptions()
        {
        }

        public string ApplicationAssembly { get; private set; }
        public string WorkingDirectory { get; private set; }
        public LaunchMode Mode { get; private set; }
        public bool WaitForExit { get; private set; }
        public bool ValidateOnly { get; private set; }
        public IReadOnlyList<string> ApplicationArguments { get; private set; }
        public IReadOnlyList<string> ProbePaths { get; private set; }
        public bool RefreshProbeFiles { get; private set; }
        public string StartProgram { get; private set; }
        public string MagicIni { get; private set; }
        public string CurrentUser { get; private set; }
        public bool CurrentUserIsAdministrator { get; private set; }
        public IReadOnlyList<string> ControllerTraceFilters { get; private set; }
        public string ControllerTraceFile { get; private set; }
        public string RuntimeProfilerFile { get; private set; }
        public bool RuntimeProfilerTrace { get; private set; }

        public LaunchMode ResolveMode()
        {
            if (Mode != LaunchMode.Auto)
                return Mode;

            return string.Equals(
                Path.GetExtension(ApplicationAssembly),
                ".exe",
                StringComparison.OrdinalIgnoreCase)
                ? LaunchMode.Process
                : LaunchMode.InProcess;
        }

        public static LauncherOptions Load(string[] args)
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var iniPath = Path.Combine(baseDirectory, "XpaRuntime.GenericLauncher.ini");
            string assemblyOverride = null;
            string startProgramOverride = null;
            string magicIniOverride = null;
            var validateOnly = false;
            bool? waitOverride = null;
            var forwardedArguments = new List<string>();

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (string.Equals(argument, "--", StringComparison.Ordinal))
                {
                    forwardedArguments.AddRange(args.Skip(index + 1));
                    break;
                }

                if (string.Equals(argument, "--ini", StringComparison.OrdinalIgnoreCase) &&
                    index + 1 < args.Length)
                {
                    iniPath = args[++index];
                    continue;
                }

                if (string.Equals(argument, "--assembly", StringComparison.OrdinalIgnoreCase) &&
                    index + 1 < args.Length)
                {
                    assemblyOverride = args[++index];
                    continue;
                }

                if (string.Equals(argument, "--program", StringComparison.OrdinalIgnoreCase) &&
                    index + 1 < args.Length)
                {
                    startProgramOverride = args[++index];
                    continue;
                }

                if (string.Equals(argument, "--magic-ini", StringComparison.OrdinalIgnoreCase) &&
                    index + 1 < args.Length)
                {
                    magicIniOverride = args[++index];
                    continue;
                }

                if (string.Equals(argument, "--validate", StringComparison.OrdinalIgnoreCase))
                {
                    validateOnly = true;
                    continue;
                }

                if (string.Equals(argument, "--wait", StringComparison.OrdinalIgnoreCase))
                {
                    waitOverride = true;
                    continue;
                }

                forwardedArguments.Add(argument);
            }

            iniPath = ResolvePath(Environment.CurrentDirectory, iniPath);
            if (!File.Exists(iniPath))
                throw new FileNotFoundException("Arquivo INI do launcher não encontrado.", iniPath);

            var ini = IniFile.Load(iniPath);
            var iniDirectory = Path.GetDirectoryName(iniPath);
            var configuredAssembly = string.IsNullOrWhiteSpace(assemblyOverride)
                ? ini.Get("XPA.Runtime", "ApplicationAssembly")
                : assemblyOverride;
            if (string.IsNullOrWhiteSpace(configuredAssembly))
                throw new InvalidOperationException("Configure [XPA.Runtime] ApplicationAssembly.");

            var assemblyPath = ResolvePath(iniDirectory, configuredAssembly);
            var configuredWorkingDirectory = ini.Get("XPA.Runtime", "WorkingDirectory");
            var workingDirectory = string.IsNullOrWhiteSpace(configuredWorkingDirectory)
                ? Path.GetDirectoryName(assemblyPath)
                : ResolvePath(iniDirectory, configuredWorkingDirectory);
            if (string.IsNullOrWhiteSpace(workingDirectory))
                workingDirectory = iniDirectory;

            var configuredArguments = SplitCommandLine(ini.Get("XPA.Runtime", "Arguments"));
            foreach (var parameter in ini.GetSection("Parameters"))
            {
                if (!string.IsNullOrWhiteSpace(parameter.Key))
                {
                    configuredArguments.Add(
                        "--xpa-param=" + parameter.Key.Trim() + "=" + parameter.Value);
                }
            }
            var currentUser = ini.Get("Security", "CurrentUser");
            var currentUserIsAdministrator = IsYes(ini.Get("Security", "Administrator"));
            if (!string.IsNullOrWhiteSpace(currentUser))
            {
                configuredArguments.Add(
                    "--xpa-param=XPA_CURRENT_USER=" + currentUser.Trim());
                configuredArguments.Add(
                    "--xpa-param=XPA_CURRENT_USER_ADMIN=" +
                    (currentUserIsAdministrator ? "SIM" : "NAO"));
            }
            configuredArguments.AddRange(forwardedArguments);
            var configuredStartProgram = string.IsNullOrWhiteSpace(startProgramOverride)
                ? ini.Get("XPA.Runtime", "StartProgram")
                : startProgramOverride;
            var configuredMagicIni = string.IsNullOrWhiteSpace(magicIniOverride)
                ? ini.Get("Data", "MagicIni")
                : magicIniOverride;
            var magicIniPath = string.IsNullOrWhiteSpace(configuredMagicIni)
                ? string.Empty
                : ResolvePath(iniDirectory, configuredMagicIni);
            if (!string.IsNullOrWhiteSpace(magicIniPath))
            {
                if (!File.Exists(magicIniPath))
                    throw new FileNotFoundException(
                        "O magic.ini configurado não foi encontrado.",
                        magicIniPath);

                configuredArguments.RemoveAll(IsIniArgument);
                configuredArguments.Insert(0, "/ini=" + magicIniPath);
            }
            if (!string.IsNullOrWhiteSpace(configuredStartProgram))
                configuredArguments.Add("--program=" + configuredStartProgram.Trim());

            var mode = ParseMode(ini.Get("XPA.Runtime", "Mode"));
            var waitForExit = waitOverride ??
                              IsYes(ini.Get("XPA.Runtime", "WaitForExit"));
            var probePaths = SplitList(ini.Get("XPA.Runtime", "ProbePaths"))
                .Select(path => ResolvePath(iniDirectory, path))
                .ToArray();
            var refreshProbeFiles =
                IsYes(ini.Get("XPA.Runtime", "RefreshProbeFiles"));
            var controllerTraceFilters =
                SplitList(ini.Get("Diagnostics", "ControllerTrace"));
            var configuredControllerTraceFile =
                ini.Get("Diagnostics", "ControllerTraceFile");
            var controllerTraceFile = string.IsNullOrWhiteSpace(
                    configuredControllerTraceFile)
                ? Path.Combine(workingDirectory, "xpa-controller-trace.log")
                : ResolvePath(workingDirectory, configuredControllerTraceFile);
            var configuredRuntimeProfilerFile =
                ini.Get("Diagnostics", "RuntimeProfilerFile");
            var runtimeProfilerFile = string.IsNullOrWhiteSpace(
                    configuredRuntimeProfilerFile)
                ? string.Empty
                : ResolvePath(workingDirectory, configuredRuntimeProfilerFile);
            var runtimeProfilerTrace =
                IsYes(ini.Get("Diagnostics", "RuntimeProfilerTrace"));

            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException(
                    "O assembly configurado não foi encontrado.",
                    assemblyPath);
            if (!Directory.Exists(workingDirectory))
                throw new DirectoryNotFoundException(
                    "O diretório de trabalho não foi encontrado: " + workingDirectory);

            return new LauncherOptions
            {
                ApplicationAssembly = assemblyPath,
                WorkingDirectory = workingDirectory,
                Mode = mode,
                WaitForExit = waitForExit,
                ValidateOnly = validateOnly,
                ApplicationArguments = configuredArguments,
                ProbePaths = probePaths,
                RefreshProbeFiles = refreshProbeFiles,
                StartProgram = configuredStartProgram?.Trim(),
                MagicIni = magicIniPath,
                CurrentUser = currentUser?.Trim(),
                CurrentUserIsAdministrator = currentUserIsAdministrator,
                ControllerTraceFilters = controllerTraceFilters,
                ControllerTraceFile = controllerTraceFile,
                RuntimeProfilerFile = runtimeProfilerFile,
                RuntimeProfilerTrace = runtimeProfilerTrace
            };
        }

        private static bool IsIniArgument(string argument)
        {
            return !string.IsNullOrWhiteSpace(argument) &&
                   argument.StartsWith("/ini", StringComparison.OrdinalIgnoreCase);
        }

        private static LaunchMode ParseMode(string value)
        {
            if (Enum.TryParse(value, true, out LaunchMode mode))
                return mode;
            return LaunchMode.Auto;
        }

        private static bool IsYes(string value)
        {
            return string.Equals(value, "Y", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "S", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "YES", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "SIM", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolvePath(string baseDirectory, string value)
        {
            var expanded = Environment.ExpandEnvironmentVariables(value ?? string.Empty).Trim();
            if (Path.IsPathRooted(expanded))
                return Path.GetFullPath(expanded);
            return Path.GetFullPath(Path.Combine(baseDirectory, expanded));
        }

        private static string[] SplitList(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .ToArray();
        }

        private static List<string> SplitCommandLine(string commandLine)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(commandLine))
                return result;

            var current = new StringBuilder();
            var quoted = false;
            for (var index = 0; index < commandLine.Length; index++)
            {
                var character = commandLine[index];
                if (character == '"')
                {
                    quoted = !quoted;
                    continue;
                }

                if (char.IsWhiteSpace(character) && !quoted)
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }

                current.Append(character);
            }

            if (quoted)
                throw new FormatException("Aspas não fechadas em [XPA.Runtime] Arguments.");
            if (current.Length > 0)
                result.Add(current.ToString());
            return result;
        }
    }

    internal sealed class IniFile
    {
        private readonly Dictionary<string, Dictionary<string, string>> _sections =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public string Get(string section, string key)
        {
            return _sections.TryGetValue(section, out var values) &&
                   values.TryGetValue(key, out var value)
                ? value
                : string.Empty;
        }

        public IEnumerable<KeyValuePair<string, string>> GetSection(string section)
        {
            return _sections.TryGetValue(section, out var values)
                ? values
                : Enumerable.Empty<KeyValuePair<string, string>>();
        }

        public static IniFile Load(string path)
        {
            var result = new IniFile();
            var section = string.Empty;
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("[", StringComparison.Ordinal) &&
                    line.EndsWith("]", StringComparison.Ordinal))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                if (!result._sections.TryGetValue(section, out var values))
                {
                    values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result._sections[section] = values;
                }

                values[line.Substring(0, separator).Trim()] =
                    line.Substring(separator + 1).Trim();
            }

            return result;
        }
    }
}

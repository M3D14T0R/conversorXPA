using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace XpaRuntime.GenericLauncher
{
    internal static class Program
    {
        private static readonly HashSet<string> ProbeDirectories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                var options = LauncherOptions.Load(args);
                ConfigureAssemblyResolution(options);

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

        private static int RunInCurrentProcess(LauncherOptions options)
        {
            Directory.SetCurrentDirectory(options.WorkingDirectory);
            var assembly = Assembly.LoadFrom(options.ApplicationAssembly);
            var entryPoint = ResolveEntryPoint(assembly);
            var parameters = entryPoint.GetParameters();
            object[] invocationArguments;
            if (parameters.Length == 0)
            {
                invocationArguments = null;
            }
            else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string[]))
            {
                invocationArguments = new object[] { options.ApplicationArguments.ToArray() };
            }
            else
            {
                throw new InvalidOperationException(
                    "O entry point deve receber nenhum argumento ou somente string[].");
            }

            var result = entryPoint.Invoke(null, invocationArguments);
            return result is int exitCode ? exitCode : 0;
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
            configuredArguments.AddRange(forwardedArguments);

            var mode = ParseMode(ini.Get("XPA.Runtime", "Mode"));
            var waitForExit = waitOverride ??
                              IsYes(ini.Get("XPA.Runtime", "WaitForExit"));
            var probePaths = SplitList(ini.Get("XPA.Runtime", "ProbePaths"))
                .Select(path => ResolvePath(iniDirectory, path))
                .ToArray();

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
                ProbePaths = probePaths
            };
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

namespace XpaConverterMvp;

internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  XpaConverterMvp <path-to-xpa-xml> <output-dir> <namespace> [--output-type <WinExe|ClassLibrary>] [--runtime-core-ref <Project|Dll>] [--runtime-core-dll <path>] [--runtime-root <folder>] [--folder <folder>] [--task <task-name>] [--task-range <start-end>] [--tables-xml <xml>] [--component-xml <xml>] [--project-ref <csproj>] [--project-ref-map <component=csproj>] [--dll-ref-map <component=dll>] [--no-implicit-component-xml] [--with-dependencies] [--full-solution] [--parallel-tasks] [--incremental-output]");
            Console.Error.WriteLine("  Compatibilidade: os aliases antigos --env-ref e --env-dll continuam aceitos.");
            Console.Error.WriteLine("Example:");
            Console.Error.WriteLine("  XpaConverterMvp \"C:\\path\\Northwind.xml\" \"C:\\out\\Generated\" \"Northwind\"");
            return 1;
        }

        var options = new ConversionOptions
        {
            XmlPath = args[0],
            OutputDir = args[1]
        };

        var optionStart = 2;
        if (args.Length >= 3 && !args[2].StartsWith("--", StringComparison.Ordinal))
        {
            options.AppNamespace = args[2];
            optionStart = 3;
        }

        for (var i = optionStart; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--folder", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                options.FolderFilter = args[i + 1];
                i++;
            }
            else if (string.Equals(args[i], "--output-type", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                options.OutputType = args[i + 1];
                i++;
            }
            else if ((string.Equals(args[i], "--env-ref", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(args[i], "--runtime-core-ref", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                options.RuntimeCoreReferenceMode = args[i + 1];
                i++;
            }
            else if ((string.Equals(args[i], "--env-dll", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(args[i], "--runtime-core-dll", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                options.RuntimeCoreDllPath = args[i + 1];
                i++;
            }
            else if (string.Equals(args[i], "--runtime-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                options.RuntimeRootPath = args[i + 1];
                i++;
            }
            else if (string.Equals(args[i], "--task", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                options.TaskFilters.Add(args[i + 1]);
                i++;
            }
            else if (string.Equals(args[i], "--task-range", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (!TopLevelTaskRange.TryParse(args[i + 1], out var range))
                {
                    Console.Error.WriteLine($"Invalid --task-range value: {args[i + 1]}");
                    return 1;
                }

                options.TaskRanges.Add(range);
                i++;
            }
            else if (string.Equals(args[i], "--tables-xml", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                options.TablesXmlPaths.Add(args[i + 1]);
                i++;
            }
            else if (string.Equals(args[i], "--component-xml", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                options.ComponentXmlPaths.Add(args[i + 1]);
                i++;
            }
            else if (string.Equals(args[i], "--project-ref", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                options.ProjectReferencePaths.Add(args[i + 1]);
                i++;
            }
            else if (string.Equals(args[i], "--project-ref-map", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var raw = args[i + 1];
                var splitIndex = raw.IndexOf('=');
                if (splitIndex > 0 && splitIndex < raw.Length - 1)
                {
                    var componentName = raw[..splitIndex].Trim();
                    var projectPath = raw[(splitIndex + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(componentName) && !string.IsNullOrWhiteSpace(projectPath))
                        options.ProjectReferenceMap[componentName] = projectPath;
                }
                i++;
            }
            else if (string.Equals(args[i], "--dll-ref-map", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var raw = args[i + 1];
                var splitIndex = raw.IndexOf('=');
                if (splitIndex > 0 && splitIndex < raw.Length - 1)
                {
                    var componentName = raw[..splitIndex].Trim();
                    var dllPath = raw[(splitIndex + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(componentName) && !string.IsNullOrWhiteSpace(dllPath))
                        options.DllReferenceMap[componentName] = dllPath;
                }
                i++;
            }
            else if (string.Equals(args[i], "--no-implicit-component-xml", StringComparison.OrdinalIgnoreCase))
            {
                options.DisableImplicitComponentXmlResolution = true;
            }
            else if (string.Equals(args[i], "--with-dependencies", StringComparison.OrdinalIgnoreCase))
            {
                options.WithDependencies = true;
            }
            else if (string.Equals(args[i], "--full-solution", StringComparison.OrdinalIgnoreCase))
            {
                options.FullSolution = true;
            }
            else if (string.Equals(args[i], "--parallel-tasks", StringComparison.OrdinalIgnoreCase))
            {
                options.ParallelTaskGeneration = true;
            }
            else if (string.Equals(args[i], "--incremental-output", StringComparison.OrdinalIgnoreCase))
            {
                options.IncrementalOutput = true;
            }
        }

        return ConversionRunner.Run(options);
    }
}

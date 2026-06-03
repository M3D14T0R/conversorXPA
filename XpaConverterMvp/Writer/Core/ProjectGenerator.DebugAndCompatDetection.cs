using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteSharedDebugHelper(string sharedDir, string appNamespace)
    {
        File.WriteAllText(Path.Combine(sharedDir, "DebugHelper.cs"), $@"using ENV.IO;
using ENV.Printing;
using ENV;

namespace {appNamespace}.Shared;

public static class DebugHelper
{{
    public static void Init()
    {{
        if (_inited)
            return;
        _inited = true;

        #if !DEBUG
        return;
        #endif
        ;
        ENV.Advanced.HandlerCollectionWrapper.BeforeHandler += HandlerInvokes;
        ControllerBase.OnProcessingCommand += ProcessingCommand;
        ControllerBase.OnRaise += Raise;
        ControllerBase.BeforeExecute += ControllerExecute;
        ReportSection.BeforeWrite += SectionWrite;
        TextSection.BeforeWrite += SectionWrite;
        TextTemplate.BeforeWrite += SectionWrite;
        TextSection.BeforeRead += SectionRead;
    }}

    static void HandlerInvokes(object handler)
    {{
    }}

    static void ProcessingCommand(object controller, XPARuntimeCore.Box.Command command)
    {{
    }}

    static void Raise(object command)
    {{
    }}

    static void ControllerExecute(ControllerBase controller)
    {{
    }}

    static void SectionWrite()
    {{
    }}

    static void SectionRead()
    {{
    }}

    static bool _inited;
}}
");
    }

    private static bool UsesJsonCompat(ProjectSemantic parsed)
    {
        string[] jsonFunctions = ["JSONInsert", "JSONModify", "JSONDelete", "JSONFind", "JSONExist", "JSONGet", "JSONCnt"];
        foreach (var task in parsed.Tasks)
        {
            foreach (var expression in task.Expressions)
            {
                var syntax = expression.Syntax ?? "";
                if (jsonFunctions.Any(fn => syntax.IndexOf(fn, StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            }
        }

        return false;
    }

    private static bool UsesEnterpriseServerCompat(ProjectSemantic parsed)
    {
        string[] requesterFunctions =
        [
            "RqContexts",
            "RqCtxInf",
            "RqCtxTrm",
            "RqRtBlock",
            "RqRtCtxs",
            "RqRtCtx",
            "RqTrmTimeout",
            "RqHTTPStatusCode"
        ];

        foreach (var task in parsed.Tasks)
        {
            foreach (var expression in task.Expressions)
            {
                var syntax = expression.Syntax ?? "";
                if (requesterFunctions.Any(fn => syntax.IndexOf(fn, StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            }
        }

        return false;
    }

    private static string? ResolveNewtonsoftJsonHintPath(string outputRoot)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_solutionRoot))
        {
            candidates.Add(Path.Combine(_solutionRoot, "Resources", "PushSharp", "Newtonsoft.Json.dll"));
            candidates.Add(Path.Combine(_solutionRoot, "Resources", "Newtonsoft.Json.dll"));
        }

        var containerRoot = ResolveProjectContainerRoot(outputRoot);
        if (!string.IsNullOrWhiteSpace(containerRoot))
        {
            candidates.Add(Path.Combine(containerRoot, "Resources", "PushSharp", "Newtonsoft.Json.dll"));
            candidates.Add(Path.Combine(containerRoot, "Resources", "Newtonsoft.Json.dll"));
        }

        if (!string.IsNullOrWhiteSpace(_runtimeCoreDllPath))
        {
            var runtimeDir = Path.GetDirectoryName(_runtimeCoreDllPath!);
            if (!string.IsNullOrWhiteSpace(runtimeDir))
            {
                candidates.Add(Path.Combine(runtimeDir, "Resources", "PushSharp", "Newtonsoft.Json.dll"));
                candidates.Add(Path.Combine(runtimeDir, "Resources", "Newtonsoft.Json.dll"));
                candidates.Add(Path.Combine(runtimeDir, "Newtonsoft.Json.dll"));
            }
        }

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "Resources", "PushSharp", "Newtonsoft.Json.dll"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "Resources", "Newtonsoft.Json.dll"));
        candidates.Add(Path.Combine(@"D:\DLLs", "Resources", "PushSharp", "Newtonsoft.Json.dll"));
        candidates.Add(Path.Combine(@"D:\DLLs", "Resources", "Newtonsoft.Json.dll"));

        var resolved = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(resolved))
            return null;

        return NormalizeProjectPath(Path.GetRelativePath(outputRoot, resolved));
    }
}


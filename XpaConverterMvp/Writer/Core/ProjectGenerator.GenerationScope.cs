using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void IncludeTaskWithStructure(
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyDictionary<int, TaskSemantic> tasksByOrdinal,
        HashSet<int> selected)
    {
        if (selected.Add(task.Ordinal))
        {
            foreach (var child in GetChildTasks(task.Ordinal, allTasks))
                IncludeTaskWithStructure(child, allTasks, tasksByOrdinal, selected);
        }

        var current = task;
        while (current.ParentOrdinal.HasValue && tasksByOrdinal.TryGetValue(current.ParentOrdinal.Value, out var parent))
        {
            if (!selected.Add(parent.Ordinal))
                break;
            current = parent;
        }
    }

    private static bool IsRelatedTo(int rootOrdinal, int candidateOrdinal, IReadOnlyDictionary<int, TaskSemantic> tasksByOrdinal)
    {
        if (rootOrdinal == candidateOrdinal)
            return true;
        if (!tasksByOrdinal.TryGetValue(candidateOrdinal, out var current))
            return false;
        while (current.ParentOrdinal.HasValue && tasksByOrdinal.TryGetValue(current.ParentOrdinal.Value, out var parent))
        {
            if (parent.Ordinal == rootOrdinal)
                return true;
            current = parent;
        }
        return false;
    }

    private static bool IsStructuralTask(TaskSemantic t)
    {
        if (t.IsEmptyTask && Regex.IsMatch(t.Description, @"^Task_\d+$"))
            return true;
        if (!Regex.IsMatch(t.Description, @"^Task_\d+$"))
            return false;
        return t.ResourceDataObjects.Count == 0
               && t.ResourcesSemantic.Ordered.Count == 0
               && t.SelectsSemantic.Items.Count == 0
               && t.DataView.Links.Count == 0
               && t.DataView.TabCalls.Count == 0
               && t.Logic.StartLogics.Count == 0
               && t.Logic.EndLogics.Count == 0
               && !t.HasStartLogicUnit
               && !t.HasEndLogicUnit
               && t.Logic.RowLogics.Count == 0
               && t.HandlersSemantic.Items.Count == 0;
    }

    private static bool ShouldGenerateForTarget(FieldModelDef m) => ShouldGenerateForTarget(m.SourceComponent);
    private static bool ShouldGenerateForTarget(DataObjectDef d) => ShouldGenerateForTarget(d.SourceComponent);
    private static bool ShouldGenerateForTarget(TaskSemantic t) => ShouldGenerateForTarget(t.SourceComponent);

    private static bool ShouldGenerateForTarget(string? sourceComponent)
    {
        if (!_isComponentized)
            return true;
        if (string.IsNullOrWhiteSpace(_targetComponent))
            return string.IsNullOrWhiteSpace(sourceComponent);
        return string.Equals(sourceComponent, _targetComponent, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveNamespaceForComponent(string? sourceComponent)
    {
        if (string.IsNullOrWhiteSpace(sourceComponent))
            return _targetNamespace;
        if (_componentNamespaces.TryGetValue(sourceComponent, out var ns))
            return ns;
        if (_projectReferenceManifests.TryGetValue(sourceComponent, out var manifest) && !string.IsNullOrWhiteSpace(manifest.Namespace))
            return manifest.Namespace;
        if (_externalMagicComponentNames.Contains(sourceComponent))
            return sourceComponent;
        if (!_isComponentized)
            return _targetNamespace;
        return sourceComponent;
    }

    private static void WriteSupportingTypesAndModelsForScopedGeneration(
        ProjectSemantic parsed,
        IReadOnlyList<TaskSemantic> generatedTasks,
        string typesDir,
        string modelsDir,
        string appNamespace)
    {
        var requiredDataObjectIds = new HashSet<int>();
        foreach (var task in generatedTasks)
        {
            if (task.DataView.PrimaryDataObject.HasValue)
                requiredDataObjectIds.Add(task.DataView.PrimaryDataObject.Value);
            foreach (var dbObj in task.DataView.ResourceDataObjects)
                requiredDataObjectIds.Add(dbObj);
            foreach (var link in task.DataView.Links)
                requiredDataObjectIds.Add(link.DbObj);
            foreach (var select in task.SelectsSemantic.Items)
            {
                if (select.SourceDbObj.HasValue)
                    requiredDataObjectIds.Add(select.SourceDbObj.Value);
            }
        }

        if (requiredDataObjectIds.Count == 0)
            return;

        var requiredDataObjects = parsed.DataObjects
            .Where(d => requiredDataObjectIds.Contains(d.Ordinal))
            .ToList();
        if (requiredDataObjects.Count == 0)
            return;

        Directory.CreateDirectory(typesDir);
        Directory.CreateDirectory(modelsDir);

        var requiredFieldModelOrdinals = requiredDataObjects
            .SelectMany(d => d.Columns)
            .Select(c => c.ModelRefObj)
            .Where(x => int.TryParse(x, out _))
            .Select(x => int.Parse(x!))
            .ToHashSet();

        foreach (var model in parsed.FieldModels
                     .Where(m => requiredFieldModelOrdinals.Contains(m.Ordinal) && ShouldGenerateForTarget(m)))
            WriteType(model, typesDir, appNamespace, parsed.Tasks);

        foreach (var dataObject in requiredDataObjects.Where(ShouldGenerateForTarget))
            WriteModel(dataObject, modelsDir, appNamespace, parsed.FieldModels);
    }

    private static HashSet<string> GetUserMethodsPublicNames()
    {
        if (_userMethodsPublicNames is not null)
            return _userMethodsPublicNames;

        _userMethodsPublicNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _userMethodsPublicNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var methodName in RuntimeUserMethodsCatalog.Names)
            RegisterUserMethodName(methodName);

        var externalCatalogs = 0;
        foreach (var root in EnumerateUserMethodsSearchRoots())
        {
            if (TryLoadUserMethodsPublicNamesFromRoot(root))
                externalCatalogs++;
        }

        ConversionTelemetry.Log(
            "RUNTIME_FUNCTIONS",
            $"catalog builtin={RuntimeUserMethodsCatalog.Names.Count} externalUserMethodsFiles={externalCatalogs} total={_userMethodsPublicNames.Count}");
        return _userMethodsPublicNames;
    }

    private static IEnumerable<string> EnumerateUserMethodsSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            string? dir = start;
            while (!string.IsNullOrWhiteSpace(dir))
            {
                if (seen.Add(dir))
                    yield return dir;
                dir = Path.GetDirectoryName(dir);
            }
        }
    }

    private static bool TryLoadUserMethodsPublicNamesFromRoot(string root)
    {
        var candidate = Path.Combine(root, "ENV", "UserMethods.cs");
        if (!File.Exists(candidate))
            return false;

        var lines = File.ReadAllLines(candidate);
        var rx = new Regex(@"^\s*public\s+[^\(]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);
        foreach (var line in lines)
        {
            var m = rx.Match(line);
            if (!m.Success)
                continue;

            RegisterUserMethodName(m.Groups[1].Value);
        }

        return true;
    }

    private static void RegisterUserMethodName(string methodName)
    {
        if (string.IsNullOrWhiteSpace(methodName))
            return;

        _userMethodsPublicNames!.Add(methodName);
        _userMethodsPublicNameMap![methodName] = methodName;
    }

}


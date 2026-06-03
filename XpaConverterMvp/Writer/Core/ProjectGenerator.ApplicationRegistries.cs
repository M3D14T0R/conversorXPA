using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteApplicationRegistries(ProjectSemantic parsed, string outputRoot, string appNamespace)
    {
        var appEntities = new StringBuilder();
        appEntities.AppendLine($"namespace {appNamespace};");
        appEntities.AppendLine();
        appEntities.AppendLine("internal class ApplicationEntitiesMvp : ENV.ApplicationEntityCollection");
        appEntities.AppendLine("{");
        appEntities.AppendLine("    public ApplicationEntitiesMvp()");
        appEntities.AppendLine("    {");
        var entitiesForTarget = parsed.DataObjects
            .Where(d => ShouldGenerateForTarget(d) || (string.IsNullOrWhiteSpace(_targetComponent) && !string.IsNullOrWhiteSpace(d.SourceComponent)))
            .ToList();
        foreach (var d in entitiesForTarget)
            appEntities.AppendLine($"        Add({d.Ordinal}, typeof({ResolveEntityTypeReferenceForRegistry(d)}));");
        appEntities.AppendLine("    }");
        appEntities.AppendLine("}");
        WriteGeneratedSourceFile(outputRoot, "ApplicationEntitiesMvp", appEntities.ToString());

        var appPrograms = new StringBuilder();
        appPrograms.AppendLine($"namespace {appNamespace};");
        appPrograms.AppendLine();
        appPrograms.AppendLine("internal class ApplicationProgramsMvp : ENV.ProgramCollection");
        appPrograms.AppendLine("{");
        appPrograms.AppendLine("    public ApplicationProgramsMvp()");
        appPrograms.AppendLine("    {");
        var topLevel = parsed.Tasks
            .Where(t => t.ParentOrdinal is null)
            .Where(t => ShouldGenerateForTarget(t) || (string.IsNullOrWhiteSpace(_targetComponent) && !string.IsNullOrWhiteSpace(t.SourceComponent)))
            .OrderBy(t => t.Ordinal)
            .ToList();
        for (var i = 0; i < topLevel.Count; i++)
        {
            var t = topLevel[i];
            var programNumber = t.TopLevelProgramIndex ?? (i + 1);
            if (t.MainProgram || t.IsEmptyTask || Regex.IsMatch(t.Description, @"^Task_\d+$"))
                continue;
            var typeRef = ResolveTaskTypeReferenceForRegistry(t);
            if (!string.IsNullOrWhiteSpace(t.PublicName))
                appPrograms.AppendLine($"        Add({programNumber}, \"{Escape(t.Description)}\", \"{Escape(t.PublicName)}\", true, typeof({typeRef}));");
            else
                appPrograms.AppendLine($"        Add({programNumber}, \"{Escape(t.Description)}\", \"\", typeof({typeRef}));");
        }
        appPrograms.AppendLine("    }");
        appPrograms.AppendLine("}");
        WriteGeneratedSourceFile(outputRoot, "ApplicationProgramsMvp", appPrograms.ToString());
    }

    private static string ResolveEntityTypeReferenceForRegistry(DataObjectDef d)
    {
        var modelType = ResolveDataObjectTypeName(d);
        if (!_isComponentized)
            return $"Models.{modelType}";
        var sourceNs = ResolveNamespaceForComponent(d.SourceComponent);
        return string.Equals(sourceNs, _targetNamespace, StringComparison.Ordinal)
            ? $"Models.{modelType}"
            : $"{sourceNs}.Models.{modelType}";
    }

}


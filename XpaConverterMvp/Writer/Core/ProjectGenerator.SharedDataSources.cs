using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteSharedDataSources(string sourceRoot, string sharedDir, string appNamespace, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> tasks)
    {
        var databaseKinds = ParseMagicDatabaseKinds(sourceRoot);
        var dataSources = dataObjects
            .Select(d => d.DataSource)
            .Concat(tasks.Select(t => t.SqlForm?.DatabaseName))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("using ENV.Data.DataProvider;");
        sb.AppendLine("using XPARuntimeCore.Box.Data.DataProvider;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace}.Shared;");
        sb.AppendLine();
        sb.AppendLine("public static class DataSources");
        sb.AppendLine("{");
        foreach (var dataSource in dataSources)
        {
            var propertyName = ToPascalIdentifier(dataSource);
            sb.AppendLine($"    public static {ResolveDataSourcePropertyType(dataSource, databaseKinds)} {propertyName}");
            sb.AppendLine("    {");
            sb.AppendLine("        get");
            sb.AppendLine("        {");
            sb.AppendLine($"            return {BuildDataSourceAccessorExpression(dataSource, databaseKinds)};");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");

        File.WriteAllText(Path.Combine(sharedDir, "DataSources.cs"), sb.ToString());
    }

    private static string ResolveDataSourcePropertyType(string dataSource, IReadOnlyDictionary<string, string> databaseKinds)
    {
        if (databaseKinds.TryGetValue(dataSource, out var databaseKind))
        {
            if (string.Equals(databaseKind, "MEMORY", StringComparison.OrdinalIgnoreCase))
                return "XPARuntimeCore.Box.Data.DataProvider.IEntityDataProvider";
            if (string.Equals(databaseKind, "XML", StringComparison.OrdinalIgnoreCase))
                return "XmlEntityDataProvider";
            return "DynamicSQLSupportingDataProvider";
        }

        if (string.Equals(dataSource, "Memory", StringComparison.OrdinalIgnoreCase) ||
            dataSource.Contains("Memory", StringComparison.OrdinalIgnoreCase))
            return "XPARuntimeCore.Box.Data.DataProvider.IEntityDataProvider";
        if (dataSource.Contains("XML", StringComparison.OrdinalIgnoreCase))
            return "XmlEntityDataProvider";
        return "DynamicSQLSupportingDataProvider";
    }

    private static string BuildDataSourceAccessorExpression(string dataSource, IReadOnlyDictionary<string, string> databaseKinds)
    {
        if (databaseKinds.TryGetValue(dataSource, out var databaseKind))
        {
            if (string.Equals(databaseKind, "MEMORY", StringComparison.OrdinalIgnoreCase))
                return "MemoryDatabase.Instance";
            if (string.Equals(databaseKind, "XML", StringComparison.OrdinalIgnoreCase))
                return $"ConnectionManager.GetXmlDataProvider(\"{Escape(dataSource)}\")";
            return $"ConnectionManager.GetSQLDataProvider(\"{Escape(dataSource)}\")";
        }

        if (string.Equals(dataSource, "Memory", StringComparison.OrdinalIgnoreCase) ||
            dataSource.Contains("Memory", StringComparison.OrdinalIgnoreCase))
            return "MemoryDatabase.Instance";
        if (dataSource.Contains("XML", StringComparison.OrdinalIgnoreCase))
            return $"ConnectionManager.GetXmlDataProvider(\"{Escape(dataSource)}\")";
        return $"ConnectionManager.GetSQLDataProvider(\"{Escape(dataSource)}\")";
    }

    private static Dictionary<string, string> ParseMagicDatabaseKinds(string sourceRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var iniPath = Path.Combine(sourceRoot, "Magic.ini");
        if (!File.Exists(iniPath))
            return result;

        var inMagicDatabases = false;
        foreach (var raw in File.ReadAllLines(iniPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";"))
                continue;

            if (line.StartsWith("["))
            {
                inMagicDatabases = line.Equals("[MAGIC_DATABASES]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inMagicDatabases)
                continue;

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
                continue;

            var name = line[..equalsIndex].Trim();
            var value = line[(equalsIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
                continue;

            if (value.StartsWith("XML", StringComparison.OrdinalIgnoreCase))
            {
                result[name] = "XML";
                continue;
            }

            if (value.StartsWith("JSON", StringComparison.OrdinalIgnoreCase))
            {
                result[name] = "JSON";
                continue;
            }

            if (value.StartsWith("DBMS", StringComparison.OrdinalIgnoreCase))
            {
                var parts = value.Split(',');
                var dbmsCode = parts.Length > 1 ? parts[1].Trim() : "";
                result[name] = dbmsCode switch
                {
                    "10" => "SQLITE",
                    "22" => "MEMORY",
                    _ => "SQL"
                };
            }
        }

        return result;
    }
}

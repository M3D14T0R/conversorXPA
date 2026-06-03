using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteModel(DataObjectDef d, string modelsDir, string appNamespace, IReadOnlyList<FieldModelDef> fieldModels)
    {
        var className = ResolveDataObjectTypeName(d);
        var databaseKinds = ParseMagicDatabaseKinds(_sourceRoot);
        var dbObjectName = BuildDbObjectName(d, databaseKinds);
        var isSqlite = IsSqliteDataObject(d, databaseKinds);
        var sb = new StringBuilder();
        sb.AppendLine("using ENV.Data;");
        sb.AppendLine("using XPARuntimeCore.Box;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace}.Models;");
        sb.AppendLine();
        sb.AppendLine($"public class {className} : {(isSqlite ? "SQLiteEntity" : "Entity")}");
        sb.AppendLine("{");
        sb.AppendLine("    #region Columns");
        var columnMemberNames = ResolveDataObjectColumnMemberNames(d, className);
        foreach (var c in d.Columns.OrderBy(x => x.Id))
        {
            var columnType = MapColumnType(c, fieldModels);
            var propName = columnMemberNames[c.Id];
            var fmt = GetColumnFormat(c, fieldModels, columnType);
            var initParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(c.DbType) && SupportsDbTypeInitializer(c))
                initParts.Add($"DbType = \"{Escape(c.DbType!)}\"");
            if (c.AllowedNull.HasValue)
                initParts.Add($"AllowNull = {(c.AllowedNull.Value ? "true" : "false")}");
            var initSuffix = initParts.Count > 0 ? $" {{ {string.Join(", ", initParts)} }}" : "";
            sb.AppendLine($"    public readonly {columnType} {propName} = new {columnType}(\"{Escape(c.DbColumnName ?? c.Name)}\", \"{fmt}\"){initSuffix};");
        }
        sb.AppendLine("    #endregion");
        sb.AppendLine();
        var indexMemberNames = ResolveDataObjectIndexMemberNames(d, className);
        sb.AppendLine("    #region Indexes");
        foreach (var idx in d.Indexes.OrderBy(x => x.Id))
        {
            var idxName = indexMemberNames[idx.Id];
            var unique = idx.Unique ? ", Unique = true" : "";
            sb.AppendLine($"    public readonly Index {idxName} = new Index {{ Caption = \"{Escape(idx.Name)}\", Name = \"{Escape(idx.Name)}\", AutoCreate = true{unique} }};");
        }
        sb.AppendLine("    #endregion");
        sb.AppendLine();
        var dataSourceMember = ToPascalIdentifier(d.DataSource ?? "Default");
        sb.AppendLine($"    public {className}() : base(\"{Escape(dbObjectName)}\", \"{Escape(d.Name)}\", Shared.DataSources.{dataSourceMember})");
        sb.AppendLine("    {");
        sb.AppendLine($"        Cached = {(d.Resident == true ? "true" : "false")};");
        var primaryKeyMembers = ResolveExplicitPrimaryKeyMemberNames(d, columnMemberNames);
        if (primaryKeyMembers.Length > 0)
            sb.AppendLine($"        SetPrimaryKey({string.Join(", ", primaryKeyMembers)});");
        else if (isSqlite)
            sb.AppendLine("        UseRowIdAsPrimaryKey();");
        else
        {
            var uniqueKeyMembers = ResolveUniqueIndexLogicalKeyMemberNames(d, columnMemberNames);
            if (uniqueKeyMembers.Length > 0)
                sb.AppendLine($"        SetPrimaryKey({string.Join(", ", uniqueKeyMembers)});");
        }
        sb.AppendLine("        InitializeIndexes();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    void InitializeIndexes()");
        sb.AppendLine("    {");
        foreach (var idx in d.Indexes.OrderBy(x => x.Id))
        {
            var idxName = indexMemberNames[idx.Id];
            var colNames = idx.Segments
                .Select(s => d.Columns.FirstOrDefault(c => c.Id == s.ColumnId))
                .Where(c => c != null)
                .Select(c => columnMemberNames[c!.Id])
                .ToArray();
            if (colNames.Length > 0)
                sb.AppendLine($"        {idxName}.Add({string.Join(", ", colNames)});");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");

        File.WriteAllText(Path.Combine(modelsDir, $"{className}.cs"), sb.ToString());
    }

    private static bool IsSqliteDataObject(DataObjectDef d, IReadOnlyDictionary<string, string> databaseKinds)
        => !string.IsNullOrWhiteSpace(d.DataSource) &&
           databaseKinds.TryGetValue(d.DataSource, out var databaseKind) &&
           string.Equals(databaseKind, "SQLITE", StringComparison.OrdinalIgnoreCase);

    private static string[] ResolveExplicitPrimaryKeyMemberNames(DataObjectDef d, IReadOnlyDictionary<int, string> columnMemberNames)
    {
        foreach (var index in d.Indexes.OrderBy(x => x.Id))
        {
            if (!index.Primary || index.Segments.Count == 0)
                continue;

            var result = new string[index.Segments.Count];
            var seenColumns = new HashSet<int>();
            var valid = true;
            for (var i = 0; i < index.Segments.Count; i++)
            {
                var columnId = index.Segments[i].ColumnId;
                if (!seenColumns.Add(columnId) || !columnMemberNames.TryGetValue(columnId, out var memberName))
                {
                    valid = false;
                    break;
                }

                result[i] = memberName;
            }

            if (valid)
                return result;
        }

        return Array.Empty<string>();
    }

    private static string[] ResolveUniqueIndexLogicalKeyMemberNames(DataObjectDef d, IReadOnlyDictionary<int, string> columnMemberNames)
    {
        foreach (var index in d.Indexes
                     .Where(x => x.Unique && x.Segments.Count > 0)
                     .OrderBy(x => x.Segments.Count)
                     .ThenBy(x => x.Id))
        {
            var result = new string[index.Segments.Count];
            var seenColumns = new HashSet<int>();
            var valid = true;
            for (var i = 0; i < index.Segments.Count; i++)
            {
                var columnId = index.Segments[i].ColumnId;
                var column = d.Columns.FirstOrDefault(c => c.Id == columnId);
                if (column is null ||
                    column.AllowedNull == true ||
                    !seenColumns.Add(columnId) ||
                    !columnMemberNames.TryGetValue(columnId, out var memberName))
                {
                    valid = false;
                    break;
                }

                result[i] = memberName;
            }

            if (valid)
                return result;
        }

        return Array.Empty<string>();
    }

}

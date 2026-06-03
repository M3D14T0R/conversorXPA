using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteType(FieldModelDef model, string typesDir, string appNamespace, IReadOnlyList<TaskSemantic> tasks)
    {
        var className = ResolveFieldModelTypeName(model);
        var baseType = model.AttrObj switch
        {
            "FIELD_NUMERIC" => "NumberColumn",
            "FIELD_DATE" => "DateColumn",
            "FIELD_TIME" => "TimeColumn",
            "FIELD_BOOLEAN" => "BoolColumn",
            "FIELD_LOGICAL" => "BoolColumn",
            "FIELD_BLOB" => "ByteArrayColumn",
            _ => "TextColumn"
        };
        var format = string.IsNullOrWhiteSpace(model.Picture) ? DefaultFormatFor(baseType) : Escape(model.Picture);

        var sb = new StringBuilder();
        sb.AppendLine("using ENV.Data;");
        sb.AppendLine("using XPARuntimeCore.Box;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace}.Types;");
        sb.AppendLine();
        sb.AppendLine($"public class {className} : {baseType}");
        sb.AppendLine("{");
        if (string.Equals(baseType, "ByteArrayColumn", StringComparison.Ordinal))
            sb.AppendLine($"    public {className}(string name = \"{Escape(model.Name)}\", string caption = null) : base(name, caption)");
        else
            sb.AppendLine($"    public {className}(string name = \"{Escape(model.Name)}\", string format = \"{format}\", string caption = null) : base(name, format, caption)");
        sb.AppendLine("    {");
        if (!string.IsNullOrWhiteSpace(model.SelectProgramObj))
        {
            var taskName = ResolveTaskNameByObj(model.SelectProgramObj!, tasks);
            if (!string.IsNullOrWhiteSpace(taskName) && int.TryParse(model.SelectProgramObj, out var taskOrdinal))
            {
                var targetTask = GetTaskByOrdinal(taskOrdinal, tasks);
                if (targetTask is not null)
                {
                    var parameters = GetTaskParameters(targetTask);
                    if (parameters.Count == 0)
                        sb.AppendLine($"        Expand += () => new {taskName}().Run();");
                    else if (parameters.Count == 1)
                        sb.AppendLine($"        Expand += () => new {taskName}().Run(this);");
                }
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");

        File.WriteAllText(Path.Combine(typesDir, $"{className}.cs"), sb.ToString());
    }

}


using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitTaskSqlWhere(
        StringBuilder sb,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        if (task.SqlWhere is null || string.IsNullOrWhiteSpace(task.SqlWhere.Format))
            return;

        var args = new List<string>();
        foreach (var arg in task.SqlWhere.Arguments)
        {
            var expr = ResolveSqlWhereArgumentExpression(task, arg, dataObjects, allTasks);
            if (string.IsNullOrWhiteSpace(expr))
                return;
            if (arg.ContentAsIs)
                expr = $"db.ContentAsIs({expr})";
            args.Add(expr);
        }

        var formatLiteral = ToCSharpLiteral(task.SqlWhere.Format);
        if (args.Count == 0)
            sb.AppendLine($"        Where.Add({formatLiteral});");
        else
            sb.AppendLine($"        Where.Add({formatLiteral}, {string.Join(", ", args)});");
    }

    private static void EmitTaskSqlForm(
        StringBuilder sb,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (task.SqlForm is null || string.IsNullOrWhiteSpace(task.SqlForm.Statement))
            return;

        var dataSourceName = ToPascalIdentifier(task.SqlForm.DatabaseName ?? "DynamicSql");
        var timeNullFallbackRewrite = RewriteSqlFormTimeNullFallbacks(
            task.SqlForm.Statement,
            dataObjects,
            task.SqlForm.InputArguments.Count);
        sb.AppendLine($"        var sqlEntity = new DynamicSQLEntity(Shared.DataSources.{dataSourceName}, {ToCSharpLiteral(timeNullFallbackRewrite.Statement)});");
        var dateNullFallbackArguments = ResolveSqlFormDateNullFallbackArguments(
            task.SqlForm.Statement,
            dataObjects);
        var rawInputExpressions = new List<string>(task.SqlForm.InputArguments.Count);
        for (var argumentIndex = 0; argumentIndex < task.SqlForm.InputArguments.Count; argumentIndex++)
        {
            var argument = task.SqlForm.InputArguments[argumentIndex];
            var expr = argument.ExpressionId is > 0
                ? ResolveExpressionCode(argument.ExpressionId.Value.ToString(), task, dataObjects, CreateSqlExpressionEmissionContext())
                : ResolveSelectExpressionByName(argument.Variable ?? "", task, dataObjects);
            if (string.IsNullOrWhiteSpace(expr))
            {
                ConversionTelemetry.Log(
                    "SQL_FORM_ARGUMENT_UNRESOLVED",
                    $"task={task.Ordinal} argument={argumentIndex + 1} exp={argument.ExpressionId?.ToString() ?? ""} var={argument.Variable ?? ""}");
                expr = "u.Null()";
            }
            rawInputExpressions.Add(expr);
            if (dateNullFallbackArguments.Contains(argumentIndex + 1))
                expr = $"Shared.XpaSqlDateStorage.NormalizeDynamicSqlDateNullFallback({expr})";
            sb.AppendLine($"        sqlEntity.AddParameter(() => {expr});");
        }
        foreach (var prefixArgumentIndex in timeNullFallbackRewrite.PrefixArgumentIndexes)
        {
            var prefixExpression = rawInputExpressions[prefixArgumentIndex - 1];
            sb.AppendLine(
                "        sqlEntity.AddParameter(() => " +
                $"Shared.XpaSqlDateStorage.ResolveDynamicSqlTimeNullFallbackSuffix({prefixExpression}));");
        }

        foreach (var outputVar in task.SqlForm.OutputVariables)
        {
            var columnExpr = ResolveSelectExpressionByName(outputVar, task, dataObjects);
            if (!string.IsNullOrWhiteSpace(columnExpr))
                sb.AppendLine($"        sqlEntity.Columns.Add({columnExpr});");
        }

        sb.AppendLine("        From = sqlEntity;");
    }

    private static (string Statement, IReadOnlyList<int> PrefixArgumentIndexes)
        RewriteSqlFormTimeNullFallbacks(
            string statement,
            IReadOnlyList<DataObjectDef> dataObjects,
            int inputArgumentCount)
    {
        var timePhysicalColumns = dataObjects
            .SelectMany(dataObject => dataObject.Columns)
            .Where(column => string.Equals(
                NormalizeAttrObjKind(column.AttrObj),
                "FIELD_TIME",
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(column => new[]
            {
                column.DbColumnName,
                column.FieldPhysicalName,
                column.Name
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (timePhysicalColumns.Count == 0)
            return (statement, Array.Empty<int>());

        var syntheticArgumentByPrefix = new Dictionary<int, int>();
        var prefixArgumentIndexes = new List<int>();
        var rewritten = Regex.Replace(
            statement,
            @":(?<prefix>\d+)(?<qualified>(?:[A-Za-z_][A-Za-z0-9_]*\.)?(?<column>[A-Za-z_][A-Za-z0-9_]*))\s*,\s*0\)",
            match =>
            {
                if (!timePhysicalColumns.Contains(match.Groups["column"].Value) ||
                    !int.TryParse(match.Groups["prefix"].Value, out var prefixArgumentIndex) ||
                    prefixArgumentIndex <= 0 ||
                    prefixArgumentIndex > inputArgumentCount)
                {
                    return match.Value;
                }

                if (!syntheticArgumentByPrefix.TryGetValue(prefixArgumentIndex, out var syntheticArgumentIndex))
                {
                    syntheticArgumentIndex = inputArgumentCount + syntheticArgumentByPrefix.Count + 1;
                    syntheticArgumentByPrefix.Add(prefixArgumentIndex, syntheticArgumentIndex);
                    prefixArgumentIndexes.Add(prefixArgumentIndex);
                }

                return
                    $":{prefixArgumentIndex}" +
                    $"{match.Groups["qualified"].Value}" +
                    $":{syntheticArgumentIndex}";
            },
            RegexOptions.CultureInvariant);

        return (rewritten, prefixArgumentIndexes);
    }

    private static HashSet<int> ResolveSqlFormDateNullFallbackArguments(
        string statement,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        var datePhysicalColumns = dataObjects
            .SelectMany(dataObject => dataObject.Columns)
            .Where(column => string.Equals(
                NormalizeAttrObjKind(column.AttrObj),
                "FIELD_DATE",
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(column => new[]
            {
                column.DbColumnName,
                column.FieldPhysicalName,
                column.Name
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new HashSet<int>();
        if (datePhysicalColumns.Count == 0)
            return result;

        foreach (Match match in Regex.Matches(
                     statement,
                     @"(?<column>[A-Za-z_][A-Za-z0-9_]*)\s*:(?<argument>\d+)",
                     RegexOptions.CultureInvariant))
        {
            if (!datePhysicalColumns.Contains(match.Groups["column"].Value))
                continue;
            if (int.TryParse(match.Groups["argument"].Value, out var argument) && argument > 0)
                result.Add(argument);
        }

        return result;
    }

    private static string? ResolveSqlWhereArgumentExpression(
        TaskSemantic task,
        TaskSqlWhereArgumentDef arg,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        var current = task;
        for (var i = 0; i < Math.Max(0, arg.ParentLevel); i++)
        {
            if (!current.ParentOrdinal.HasValue)
                return null;
            if (!_tasksByOrdinal.TryGetValue(current.ParentOrdinal.Value, out var parentTask) || parentTask is null)
                return null;
            current = parentTask;
        }

        var selectableItems = current.SelectsSemantic.Items
            .Where(s => !s.IsFunctionSelect)
            .ToList();
        string? currentExpr = null;
        if (arg.VariableOrdinal > 0 && arg.VariableOrdinal <= selectableItems.Count)
            currentExpr = ResolveSelectExpression(selectableItems[arg.VariableOrdinal - 1], current, dataObjects, "");

        if (string.IsNullOrWhiteSpace(currentExpr))
        {
            if (arg.VariableOrdinal <= 0 || arg.VariableOrdinal > current.ResourcesSemantic.Ordered.Count)
                return null;
            var resource = current.ResourcesSemantic.Ordered[arg.VariableOrdinal - 1];
            currentExpr = ResolveTaskResourceMemberName(current, resource);
        }

        if (ReferenceEquals(current, task))
            return currentExpr;

        var prefix = "_parent";
        var parent = task;
        while (parent.ParentOrdinal.HasValue && parent.ParentOrdinal.Value != current.Ordinal)
        {
            prefix += "._parent";
            if (!_tasksByOrdinal.TryGetValue(parent.ParentOrdinal.Value, out var parentTask) || parentTask is null)
                break;
            parent = parentTask;
        }
        return currentExpr.StartsWith("Application.", StringComparison.Ordinal)
            ? currentExpr
            : $"{prefix}.{currentExpr}";
    }
}


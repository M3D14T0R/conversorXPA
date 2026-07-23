using System;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitTaskPrintAndMergeMembers(StringBuilder sb, TaskSemantic t)
    {
        if (DeclaresPrintStream(t))
        {
            sb.AppendLine("    #region Streams");
            foreach (var streamVar in ResolvePrintStreamVariableNames(t).Distinct())
                sb.AppendLine($"    ENV.Printing.PrinterWriter {streamVar};");
            sb.AppendLine("    #endregion");
            sb.AppendLine();
        }

        if (HasPrintLayout(t))
        {
            var layoutClass = BuildPrintLayoutClassName(t);
            var printingNamespaceSegment = ResolvePrintingNamespaceSegment(t, _allTasks);
            sb.AppendLine("    #region Layouts");
            sb.AppendLine($"    {printingNamespaceSegment}.{layoutClass} _layout => Cached<{printingNamespaceSegment}.{layoutClass}>();");
            sb.AppendLine("    #endregion");
            sb.AppendLine();
        }

        if (HasTextIoLayout(t))
        {
            sb.AppendLine("    #region Streams");
            foreach (var stream in ResolveTextIoStreams(t))
                sb.AppendLine($"    {ResolveTextIoStreamFieldType(stream.StreamType)} {stream.VariableName};");
            sb.AppendLine("    #endregion");
            sb.AppendLine();

            var allTasks = _allTasks ?? Array.Empty<TaskSemantic>();
            var textIoNamespaceSegment = ResolveTextIoNamespaceSegment(t, allTasks);
            var textForms = GetEffectiveTextIoForms(t);
            if (textForms.Count > 0)
            {
                sb.AppendLine("    #region Layouts");
                var emittedVariables = new HashSet<string>(StringComparer.Ordinal);
                foreach (var formEntry in textForms)
                {
                    var layoutClass = BuildTextIoLayoutClassName(t, allTasks, formEntry);
                    var variableName = ResolveTextIoLayoutVariableName(t, formEntry.Index);
                    if (!emittedVariables.Add(variableName))
                        continue;
                    sb.AppendLine($"    {textIoNamespaceSegment}.{layoutClass} {variableName} => Cached<{textIoNamespaceSegment}.{layoutClass}>();");
                }
                sb.AppendLine("    #endregion");
                sb.AppendLine();
            }
        }

        var ownedMergeIo = ResolveOwnedMergeIoDefinition(t);
        if (ownedMergeIo is not null)
        {
            sb.AppendLine("    #region Streams");
            var mergeStreamType = string.Equals(ownedMergeIo.Media, "S", StringComparison.OrdinalIgnoreCase)
                ? "FileWriter"
                : "WebWriter";
            sb.AppendLine($"    {mergeStreamType} {ResolveMergeStreamVariableName(t)};");
            sb.AppendLine("    #endregion");
            sb.AppendLine();
        }

        if (HasMergeLayout(t))
        {
            sb.AppendLine("    #region Layouts");
            foreach (var formEntry in t.Layout.MergeForms.OrderBy(x => x.Index))
            {
                var variableName = t.Layout.MergeTemplateVariableNamesByFormEntryIndex.TryGetValue(formEntry.Index, out var mapped)
                    ? mapped
                    : ResolveMergeTemplateVariableName(formEntry);
                sb.AppendLine($"    TextTemplate {variableName};");
            }
            sb.AppendLine("    #endregion");
            sb.AppendLine();
        }
    }
}


using System;
using System.Collections.Generic;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitOnLoad(StringBuilder sb, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks)
    {
        sb.AppendLine("    protected override void OnLoad()");
        sb.AppendLine("    {");
        var localExpressionCache = new Dictionary<string, string>(StringComparer.Ordinal);
        string ResolveOnLoadExpression(int expressionId, ExpressionEmissionContext context)
        {
            var key = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{expressionId}|{context.SinkKind}|{context.Expected.ReturnType}|{context.ParameterType}|{context.PreserveBinding}|{context.BlobTarget}");
            if (localExpressionCache.TryGetValue(key, out var cached))
                return cached;
            var resolved = ResolveExpressionCode(expressionId.ToString(), t, dataObjects, context);
            localExpressionCache[key] = resolved;
            return resolved;
        }
        var baseClass = ResolveBaseClass(t);
        var allowUserAbortEmitted = false;
        void EmitAllowUserAbortIfNeeded()
        {
            if (baseClass == "BusinessProcessBase" && !allowUserAbortEmitted)
            {
                sb.AppendLine("        AllowUserAbort = true;");
                allowUserAbortEmitted = true;
            }
        }

        var emittedExit = false;
        var activity = t.Execution.Activity;
        var activityExprId = t.Execution.ActivityExpressionId;
        var rowLocking = t.Execution.RowLocking;
        if (!string.IsNullOrWhiteSpace(t.Icon))
            sb.AppendLine($"        Icon = {ToCSharpLiteral(t.Icon)};");
        if (!string.IsNullOrWhiteSpace(rowLocking) &&
            !(baseClass == "BusinessProcessBase" && !TaskHasConcreteDataSource(t)) &&
            !ShouldSuppressExplicitRowLocking(t, rowLocking))
            sb.AppendLine($"        RowLocking = {rowLocking};");
        if (t.Execution.KeepChildRelationCacheAlive)
            sb.AppendLine("        KeepChildRelationCacheAlive = true;");

        var allowTransactionScopeWithoutDataView =
            !t.DataView.HasFrom &&
            baseClass == "UIControllerBase" &&
            string.Equals(t.Execution.TransactionScope, "TransactionScopes.Row", StringComparison.Ordinal) &&
            (IsInvokedByParentDatabaseErrorHandler(t) || t.ResourceDbs.Count == 0);
        if (baseClass == "UIControllerBase" &&
            !t.DataView.HasFrom &&
            string.IsNullOrWhiteSpace(t.Execution.TransactionScope) &&
            IsInvokedByParentDatabaseErrorHandler(t))
        {
            sb.AppendLine("        TransactionScope = TransactionScopes.Row;");
        }
        if (!string.IsNullOrWhiteSpace(t.Execution.TransactionScope) &&
            (!((baseClass == "UIControllerBase" || baseClass == "BusinessProcessBase") && !t.DataView.HasFrom) ||
             allowTransactionScopeWithoutDataView ||
             (baseClass == "BusinessProcessBase" &&
              !TaskHasConcreteDataSource(t) &&
              string.Equals(t.Execution.TransactionScope, "TransactionScopes.Task", StringComparison.Ordinal) &&
              string.Equals(t.TransactionBegin, "T", StringComparison.OrdinalIgnoreCase))) &&
            !(baseClass == "BusinessProcessBase" &&
              !TaskHasConcreteDataSource(t) &&
              string.Equals(t.Execution.TransactionScope, "TransactionScopes.Task", StringComparison.Ordinal) &&
              !string.Equals(t.TransactionBegin, "T", StringComparison.OrdinalIgnoreCase)) &&
            !(baseClass == "UIControllerBase" &&
              t.ParentOrdinal.HasValue &&
              string.Equals(t.Execution.TransactionScope, "TransactionScopes.Task", StringComparison.Ordinal)))
            sb.AppendLine($"        TransactionScope = {t.Execution.TransactionScope};");

        foreach (var resourceDb in t.ResourceDbs)
        {
            var entityNameExpr = ResolveResourceDbEntityNameExpression(t, resourceDb, dataObjects);
            if (string.IsNullOrWhiteSpace(entityNameExpr))
                continue;
            var member = ResolvePrimaryMember(t, resourceDb.DataObject, dataObjects);
            if (string.IsNullOrWhiteSpace(member))
                continue;
            sb.AppendLine($"        {member}.EntityName = {entityNameExpr};");
        }

        if (t.Execution.RetryOnDatabaseError)
            sb.AppendLine("        OnDatabaseErrorRetry = true;");

        var shouldEmitActivity = !string.IsNullOrWhiteSpace(activity) &&
                                 (baseClass == "UIControllerBase" || baseClass == "BusinessProcessBase");
        if ((baseClass == "UIControllerBase" || baseClass == "BusinessProcessBase") && activityExprId.HasValue)
        {
            var activityExpr = ResolveOnLoadExpression(activityExprId.Value, CreateProgramReferenceEmissionContext());
            if (!string.IsNullOrWhiteSpace(activityExpr))
                sb.AppendLine($"        Activity = u.TranslateTaskActivity({activityExpr});");
        }
        else if (shouldEmitActivity)
            sb.AppendLine($"        Activity = {activity};");
        if (baseClass != "BusinessProcessBase" && t.Execution.SwitchToInsertWhenNoRows)
            sb.AppendLine("        SwitchToInsertWhenNoRows = true;");
        if (baseClass != "BusinessProcessBase" && t.Execution.EnableSelectionTableFlow)
        {
            sb.AppendLine("        AllowSelect = true;");
        }
        if (baseClass != "BusinessProcessBase" &&
            t.Execution.AllowExportData &&
            (t.Report.HasDetailLine || t.Report.HasPageHeader || t.Report.HasPageFooter || t.Report.HasNewPage))
            sb.AppendLine("        AllowExportData = true;");
        if (baseClass != "BusinessProcessBase" && t.AllowActivitySwitch.HasValue && t.AllowActivitySwitch.Value == false)
            sb.AppendLine("        AllowActivitySwitch = false;");
        if (baseClass != "BusinessProcessBase" && t.AllowDelete.HasValue && t.AllowDelete.Value == false)
            sb.AppendLine("        AllowDelete = false;");
        if (baseClass != "BusinessProcessBase" && t.AllowCreate.HasValue && t.AllowCreate.Value == false)
            sb.AppendLine("        AllowInsert = false;");
        if (baseClass != "BusinessProcessBase" && t.AllowModify.HasValue && t.AllowModify.Value == false)
            sb.AppendLine("        AllowUpdate = false;");
        if (baseClass != "BusinessProcessBase" && t.ForceRecordSuffix)
            sb.AppendLine("        ForceSaveRow = true;");
        if (baseClass != "BusinessProcessBase" && t.PreloadView.HasValue && t.PreloadView.Value)
            sb.AppendLine("        PreloadData = true;");

        if (!emittedExit && t.Execution.EndTaskCondition)
        {
            if (string.Equals(t.Execution.EvaluateEndCondition, "I", StringComparison.OrdinalIgnoreCase) &&
                t.Execution.EndTaskConditionExpressionId.HasValue)
            {
                var exitCondition = ResolveExpressionCode(t.Execution.EndTaskConditionExpressionId.Value.ToString(), t, dataObjects, CreateBooleanConditionEmissionContext());
                exitCondition = ResolveTextIoEndOfFileExpression(t, exitCondition);
                var reevaluateArg = ResolveImmediateExitReevaluationArgument(exitCondition, t);
                if (!string.IsNullOrWhiteSpace(exitCondition) && !string.IsNullOrWhiteSpace(reevaluateArg))
                {
                    sb.AppendLine($"        BindExitAsSoonAsPossible(() => {exitCondition}, {reevaluateArg});");
                    emittedExit = true;
                }
            }
            var timing = t.Execution.ExitTiming;
            if (!emittedExit && !string.IsNullOrWhiteSpace(timing))
            {
                if (t.Execution.EndTaskConditionExpressionId.HasValue)
                {
                    var exitCondition = ResolveExpressionCode(t.Execution.EndTaskConditionExpressionId.Value.ToString(), t, dataObjects, CreateBooleanConditionEmissionContext());
                    exitCondition = ResolveTextIoEndOfFileExpression(t, exitCondition);
                    if (!string.IsNullOrWhiteSpace(exitCondition))
                        sb.AppendLine($"        Exit({timing}, () => {exitCondition});");
                    else
                        sb.AppendLine($"        Exit({timing});");
                }
                else
                {
                    sb.AppendLine($"        Exit({timing});");
                }
            }
        }
        if (!t.CloseTaskWindow)
            sb.AppendLine("        KeepViewVisibleAfterExit = true;");
        EmitAllowUserAbortIfNeeded();

        if (baseClass != "BusinessProcessBase" &&
            !string.IsNullOrWhiteSpace(t.AllowCreateExpression))
        {
            var expCode = ResolveExpressionCode(t.AllowCreateExpression, t, dataObjects, CreateBooleanConditionEmissionContext());
            var expComment = ResolveExpressionComment(t.AllowCreateExpression, t);
            if (string.IsNullOrWhiteSpace(expCode))
                expCode = $"true /* {expComment} */";
            sb.AppendLine($"        BindAllowInsert(() => {expCode});");
        }

        if (TryEmitMultiFormViewSwitch(sb, t, dataObjects, allTasks))
        {
        }
        else if (t.View.ShouldGenerate && !ShouldSuppressViewForBusinessProcessTextIo(t))
        {
            var viewClass = ResolveViewClassName(t, allTasks);
            sb.AppendLine($"        View = () => new Views.{viewClass}(this);");
        }
        if (DeclaresPrintStream(t))
        {
            var taskIos = t.Ios.Count > 0 ? t.Ios : (t.Io is null ? Array.Empty<TaskIoDef>() : new[] { t.Io });
            for (var ioIndex = 0; ioIndex < taskIos.Count; ioIndex++)
            {
                var taskIo = taskIos[ioIndex];
                var streamVar = ResolvePrintStreamVariableName(t, ioIndex);
                var ioName = !string.IsNullOrWhiteSpace(taskIo.Description) ? taskIo.Description! : t.Description;
                var printPreview = taskIo.PrintPreview == false ? "false" : "true";
                var ctorArg = taskIo.IoExpressionId.HasValue
                    ? ResolveOnLoadExpression(taskIo.IoExpressionId.Value, CreateIoArgumentEmissionContext())
                    : null;
                var ioProps = $"Name = \"{Escape(ioName)}\", PrintPreview = {printPreview}";
                if (!string.IsNullOrWhiteSpace(taskIo.Machine))
                    ioProps += $", PrinterName = Shared.Printing.Printers.{ResolvePrinterIdentifier(taskIo.Machine!)}.PrinterName";
                if (taskIo.Pdf == true)
                    ioProps += ", Pdf = true";
                if (!string.IsNullOrWhiteSpace(ctorArg))
                    sb.AppendLine($"        {streamVar} = new ENV.Printing.PrinterWriter({ctorArg}) {{ {ioProps} }};");
                else
                    sb.AppendLine($"        {streamVar} = new ENV.Printing.PrinterWriter() {{ {ioProps} }};");
                if (HasPrintLayout(t))
                {
                    var headerSection = ResolvePageHeaderSectionName(t, ioIndex);
                    if (!string.IsNullOrWhiteSpace(headerSection))
                        sb.AppendLine($"        {streamVar}.PageHeader = _layout.{headerSection};");
                    var footerSection = ResolvePageFooterSectionName(t, ioIndex);
                    if (!string.IsNullOrWhiteSpace(footerSection))
                        sb.AppendLine($"        {streamVar}.PageFooter = _layout.{footerSection};");
                }
                sb.AppendLine($"        Streams.Add({streamVar});");
            }
            EmitAllowUserAbortIfNeeded();
        }
        if (ShouldDeclareTaskTextIoStreams(t))
        {
            foreach (var stream in ResolveTextIoStreams(t))
            {
                var ioDef = stream.Definition;
                var ioName = !string.IsNullOrWhiteSpace(ioDef?.Description) ? ioDef!.Description! : t.Description;
                var ioExpr = ioDef?.IoExpressionId.HasValue == true
                    ? ResolveOnLoadExpression(ioDef.IoExpressionId.Value, CreateIoArgumentEmissionContext())
                    : "\"\"";
                if (string.IsNullOrWhiteSpace(ioExpr))
                    ioExpr = "\"\"";
                if (IsByteArrayTextIo(t, ioDef))
                {
                    var byteArrayExpr = ResolveTextIoColumnExpression(t, dataObjects, allTasks, ioDef);
                    if (!IsByteArrayCompatibleTextIoSource(t, byteArrayExpr))
                        byteArrayExpr = "";
                    if (string.IsNullOrWhiteSpace(byteArrayExpr))
                        byteArrayExpr = ResolveParentBlobExpression(t, allTasks);
                    if (string.IsNullOrWhiteSpace(byteArrayExpr))
                        byteArrayExpr = ioExpr;
                    sb.AppendLine($"        {stream.VariableName} = new ENV.IO.{stream.StreamType}({byteArrayExpr}) {{ Name = \"{Escape(ioName)}\" }};");
                }
                else
                {
                    if (string.Equals(stream.StreamType, "FileReader", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"        {stream.VariableName} = new ENV.IO.FileReader({ioExpr}) {{ Name = \"{Escape(ioName)}\" }};");
                    }
                    else if (string.Equals(stream.StreamType, "TextPrinterWriter", StringComparison.OrdinalIgnoreCase))
                    {
                        var printerProp = !string.IsNullOrWhiteSpace(ioDef?.Machine)
                            ? $", Printer = Shared.Printing.Printers.{ResolvePrinterIdentifier(ioDef.Machine!)}"
                            : "";
                        sb.AppendLine($"        {stream.VariableName} = new ENV.Printing.TextPrinterWriter({ioExpr}) {{ Name = \"{Escape(ioName)}\", IgnoreNewPage = true{printerProp} }};");
                    }
                    else if (string.Equals(stream.StreamType, "WebWriter", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"        {stream.VariableName} = new ENV.IO.WebWriter({ioExpr}) {{ Name = \"{Escape(ioName)}\" }};");
                    }
                    else if (NeedsUnicodeFileWriterEncoding(t, allTasks))
                        sb.AppendLine($"        {stream.VariableName} = new ENV.IO.FileWriter({ioExpr}, System.Text.Encoding.Unicode) {{ Name = \"{Escape(ioName)}\" }};");
                    else
                        sb.AppendLine($"        {stream.VariableName} = new ENV.IO.FileWriter({ioExpr}) {{ Name = \"{Escape(ioName)}\" }};");
                    if (!string.Equals(stream.StreamType, "FileReader", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(stream.StreamType, "TextPrinterWriter", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(stream.StreamType, "WebWriter", StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine($"        {stream.VariableName}.Open();");
                }
                sb.AppendLine($"        Streams.Add({stream.VariableName});");
                if (string.Equals(stream.StreamType, "FileReader", StringComparison.OrdinalIgnoreCase))
                    EmitTextIoReaderDataViewBindings(sb, t, dataObjects);
            }
            EmitAllowUserAbortIfNeeded();
        }
        var ownedMergeIo = ResolveOwnedMergeIoDefinition(t);
        if (HasMergeLayout(t) || ownedMergeIo is not null)
        {
            var streamVar = ResolveMergeStreamVariableName(t);
            var effectiveStreamExpr = ResolveMergeStreamExpression(t, allTasks);
            var mergeIo = ownedMergeIo;
            if (mergeIo is not null && !ShouldDeclareTaskTextIoStreams(t))
            {
                var ioName = !string.IsNullOrWhiteSpace(mergeIo.Description) ? mergeIo.Description! : t.Description;
                var ioExpr = mergeIo.IoExpressionId.HasValue
                    ? ResolveOnLoadExpression(mergeIo.IoExpressionId.Value, CreateIoArgumentEmissionContext())
                    : "\"\"";
                if (string.IsNullOrWhiteSpace(ioExpr))
                    ioExpr = "\"\"";
                if (string.Equals(mergeIo.Media, "S", StringComparison.OrdinalIgnoreCase))
                {
                    if (mergeIo.IoToUseColumnId.HasValue)
                    {
                        var ioNameExpr = "";
                        var ioNameResource = t.ResourcesSemantic.Ordered.FirstOrDefault(x => LooksLikeIoNameResource(x.Name));
                        if (ioNameResource is not null)
                            ioNameExpr = ToLegacyVariableName(ioNameResource.Name);
                        var ioToUseParam = ResolveIoToUseParameterSelect(t);
                        if (ioToUseParam is not null && string.IsNullOrWhiteSpace(ioNameExpr))
                            ioNameExpr = ResolveSelectExpression(ioToUseParam, t, dataObjects, "");
                        if (string.IsNullOrWhiteSpace(ioNameExpr) &&
                            ResolveTaskResourceColumn(t, mergeIo.IoToUseColumnId.Value) is TaskResourceColumnDef ioToUseColumn)
                        {
                            ioNameExpr = ToLegacyVariableName(ioToUseColumn.Name);
                        }
                        if (!string.IsNullOrWhiteSpace(ioNameExpr))
                        {
                            sb.AppendLine($"        {streamVar} = FileWriter.FindIOByName({ioNameExpr});");
                            sb.AppendLine($"        if ({streamVar} == null)");
                            sb.AppendLine("        {");
                            sb.AppendLine($"            {streamVar} = new FileWriter() {{ Name = \"{Escape(ioName)}\" }};");
                            sb.AppendLine($"            {streamVar}.Open();");
                            sb.AppendLine("        }");
                        }
                        else
                        {
                            sb.AppendLine($"        {streamVar} = new FileWriter({ioExpr}) {{ Name = \"{Escape(ioName)}\" }};");
                            sb.AppendLine($"        {streamVar}.Open();");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"        {streamVar} = new FileWriter({ioExpr}) {{ Name = \"{Escape(ioName)}\" }};");
                        sb.AppendLine($"        {streamVar}.Open();");
                    }
                }
                else
                {
                    sb.AppendLine($"        {streamVar} = new WebWriter({ioExpr}) {{ Name = \"{Escape(ioName)}\" }};");
                }
                sb.AppendLine($"        Streams.Add({streamVar});");
            }
            foreach (var formEntry in t.Layout.MergeForms)
            {
                var viewVar = t.Layout.MergeTemplateVariableNamesByFormEntryIndex.TryGetValue(formEntry.Index, out var mappedTemplateVar)
                    ? mappedTemplateVar
                    : ResolveMergeTemplateVariableName(formEntry);
                var fileExprId = formEntry.Form.MergeFileNameExpressionId ?? mergeIo?.IoExpressionId;
                var fileExpr = fileExprId.HasValue ? ResolveOnLoadExpression(fileExprId.Value, CreateIoArgumentEmissionContext()) : "";
                if (string.IsNullOrWhiteSpace(fileExpr) && !string.IsNullOrWhiteSpace(formEntry.Form.MergeFileName))
                    fileExpr = ToCSharpLiteral(formEntry.Form.MergeFileName!);
                if (string.IsNullOrWhiteSpace(fileExpr))
                    fileExpr = "\"\"";
                sb.AppendLine($"        {viewVar} = new TextTemplate({fileExpr});");
                if (formEntry.Form.MergeTags.Count > 0)
                {
                    var tagItems = formEntry.Form.MergeTags
                        .Select(tag =>
                        {
                            if (!string.IsNullOrWhiteSpace(tag.Column))
                            {
                                var tagColumnExpr = ResolveExpressionOrdinalBinding(tag.Column!, t, allTasks, dataObjects);
                                if (!string.IsNullOrWhiteSpace(tagColumnExpr) && IsSimpleMemberAccess(tagColumnExpr))
                                    return $"new Tag(\"{Escape(tag.Name)}\", {tagColumnExpr})";
                            }

                            var tagExpr = tag.ExpressionId.HasValue ? ResolveOnLoadExpression(tag.ExpressionId.Value, CreateMessageTextEmissionContext()) : "\"\"";
                            if (string.IsNullOrWhiteSpace(tagExpr) && !string.IsNullOrWhiteSpace(tag.Column))
                                tagExpr = ResolveExpressionOrdinalBinding(tag.Column!, t, allTasks, dataObjects);
                            if (string.IsNullOrWhiteSpace(tagExpr))
                                tagExpr = "\"\"";
                            var pic = string.IsNullOrWhiteSpace(tag.Picture) ? "30" : Escape(tag.Picture!);
                            return $"new Tag(\"{Escape(tag.Name)}\", () => {tagExpr}, \"{pic}\")";
                        })
                        .ToList();
                    if (tagItems.Count == 1)
                        sb.AppendLine($"        {viewVar}.Add({tagItems[0]});");
                    else
                        sb.AppendLine($"        {viewVar}.Add({string.Join(", ", tagItems)});");
                }
            }
            EmitAllowUserAbortIfNeeded();
        }
        sb.AppendLine("    }");
    }

    private static string ResolveTextIoEndOfFileExpression(TaskSemantic task, string exitCondition)
    {
        if (string.IsNullOrWhiteSpace(exitCondition) ||
            exitCondition.IndexOf("u.EOF(0,1)", StringComparison.OrdinalIgnoreCase) < 0 ||
            !HasTextIoLayout(task) ||
            !IsTextIoReaderStream(task))
        {
            return exitCondition;
        }

        var reader = ResolveTextIoStreams(task)
            .FirstOrDefault(stream =>
                string.Equals(stream.StreamType, "FileReader", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stream.StreamType, "ByteArrayReader", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(reader?.VariableName))
            return exitCondition;

        return exitCondition.Replace("u.EOF(0,1)", $"{reader.VariableName}.EndOfFile", StringComparison.OrdinalIgnoreCase);
    }
}


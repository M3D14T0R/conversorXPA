using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string BuildBindValueSuffix(string bindExpr, TaskSemantic? task = null)
    {
        if (LooksLikeTaskRunConstruction(bindExpr))
            return $".BindValueToColumnChange(() => {bindExpr})";
        var simpleMember = IsSimpleMemberPath(bindExpr);
        var hasOperatorsOrCalls = ContainsDynamicOperators(bindExpr);
        var isNowProperty = bindExpr.EndsWith(".Now", StringComparison.Ordinal);
        var isLiteral =
            string.Equals(bindExpr, "true", StringComparison.Ordinal) ||
            string.Equals(bindExpr, "false", StringComparison.Ordinal) ||
            string.Equals(bindExpr, "null", StringComparison.Ordinal) ||
            IsWholeStringLiteralExpression(bindExpr) ||
            IsNumericLiteralExpressionCentral(bindExpr);
        if (simpleMember && !hasOperatorsOrCalls && !isLiteral)
        {
            if (isNowProperty)
                return $".BindValue(() => {bindExpr})";
            return $".BindValue({bindExpr})";
        }
        return $".BindValue(() => {bindExpr})";
    }

    private static bool IsResourceDisplayedInView(TaskSemantic task, TaskResourceColumnDef resource)
    {
        var legacy = ResolveTaskResourceMemberName(task, resource);
        foreach (var control in task.View.SelectedFormControls)
        {
            if (string.IsNullOrWhiteSpace(control.DataColumn))
                continue;

            var resolved = ResolveDataColumnOrdinalBinding(control.DataColumn, task, _allTasks ?? Array.Empty<TaskSemantic>());
            if (string.Equals(resolved, legacy, StringComparison.Ordinal))
                return true;

            if (task.ResourcesSemantic.ByName.TryGetValue(control.DataColumn, out var byName) &&
                byName.Id == resource.Id)
                return true;
        }
        return false;
    }


    private static string ResolveExpressionOrdinalBinding(string token, TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(token))
            return "";
        var normalized = token.Trim().ToUpperInvariant();
        if (!IsAlphabeticBindingToken(normalized))
            return "";

        var directSelect = task.SelectsSemantic.Items
            .FirstOrDefault(s => string.Equals(s.Name, normalized, StringComparison.OrdinalIgnoreCase));
        if (directSelect is null &&
            task.SelectsSemantic.ItemsByName.TryGetValue(normalized, out var mappedSelect) &&
            mappedSelect is not null)
            directSelect = mappedSelect;

        if (directSelect is not null)
        {
            var directExpr = ResolveSelectExpression(directSelect, task, dataObjects, "");
            if (!string.IsNullOrWhiteSpace(directExpr))
                return directExpr;
        }

        // Resolve inherited selects with the writer's complete data-object/model
        // context.  The semantic map is intentionally kept as a fallback because
        // it is built before external model metadata is fully available and can
        // therefore retain a synthetic resource name for relational selects.
        var parentOrdinal = task.ParentOrdinal;
        var parentDepth = 1;
        while (parentOrdinal.HasValue)
        {
            var parentTask = GetTaskByOrdinal(parentOrdinal, allTasks);
            if (parentTask is null)
                break;

            var inheritedSelect = parentTask.SelectsSemantic.Items
                .FirstOrDefault(s => string.Equals(s.Name, normalized, StringComparison.OrdinalIgnoreCase));
            if (inheritedSelect is null &&
                parentTask.SelectsSemantic.ItemsByName.TryGetValue(normalized, out var mappedParentSelect) &&
                mappedParentSelect is not null)
                inheritedSelect = mappedParentSelect;

            if (inheritedSelect is not null)
            {
                var inheritedExpression = ResolveSelectExpression(inheritedSelect, parentTask, dataObjects, "");
                if (!string.IsNullOrWhiteSpace(inheritedExpression))
                {
                    if (inheritedExpression.StartsWith("Application.", StringComparison.Ordinal))
                        return inheritedExpression;

                    var prefix = string.Concat(Enumerable.Repeat("_parent.", parentDepth));
                    return prefix + inheritedExpression;
                }
            }

            parentOrdinal = parentTask.ParentOrdinal;
            parentDepth++;
        }

        if (_parentSelectMapByTaskOrdinal.TryGetValue(task.Ordinal, out var parentSelectMap) &&
            parentSelectMap.TryGetValue(normalized, out var parentSelectExpr) &&
            !string.IsNullOrWhiteSpace(parentSelectExpr))
        {
            return parentSelectExpr;
        }

        long slotLong = 0;
        foreach (var ch in normalized)
        {
            slotLong = (slotLong * 26) + (ch - 'A' + 1);
            if (slotLong > int.MaxValue)
                return "";
        }

        var slot = (int)slotLong;

        // In task expressions, single-letter ordinal bindings are offset from C:
        // C -> column 1, D -> column 2, etc.
        var columnIndex = slot - 2;
        if (columnIndex <= 0)
        {
            var applicationBinding = ResolveApplicationOrdinalBinding(slot, allTasks);
            return applicationBinding ?? "";
        }

        var minLocalSelectSlot = task.SelectsSemantic.Items
            .Select(s => ToAlphabeticSlot(s.Name))
            .Where(slotValue => slotValue > 0)
            .DefaultIfEmpty(int.MaxValue)
            .Min();

        if (slot < minLocalSelectSlot && minLocalSelectSlot != int.MaxValue)
        {
            if (task.ParentOrdinal.HasValue)
            {
                var parentBinding = ResolveOrdinalBindingFromParentChain(columnIndex, task, allTasks, dataObjects);
                if (!string.IsNullOrWhiteSpace(parentBinding))
                    return parentBinding;
            }

            var applicationBinding = ResolveApplicationOrdinalBinding(slot, allTasks);
            if (!string.IsNullOrWhiteSpace(applicationBinding))
                return applicationBinding;
        }

        if (columnIndex <= task.ResourcesSemantic.Ordered.Count)
            return ResolveTaskResourceBindingExpression(
                task,
                task.ResourcesSemantic.Ordered[columnIndex - 1],
                dataObjects);

        var fallbackParentBinding = ResolveOrdinalBindingFromParentChain(columnIndex, task, allTasks, dataObjects);
        if (!string.IsNullOrWhiteSpace(fallbackParentBinding))
            return fallbackParentBinding;

        // Application-level single-letter aliases are valid only as a fallback. Local
        // task resources and parent resources must win, especially for source VAR
        // literals that feed u.IndexOf/u.Level-style runtime calls.
        if (normalized.Length == 1)
        {
            var applicationSelectMap = BuildApplicationSelectMap(allTasks, dataObjects);
            if (applicationSelectMap.TryGetValue(normalized, out var singleLetterApplicationSelectBinding) &&
                !string.IsNullOrWhiteSpace(singleLetterApplicationSelectBinding))
                return singleLetterApplicationSelectBinding;

            var singleLetterApplicationBinding = ResolveApplicationOrdinalBinding(slot, allTasks);
            if (!string.IsNullOrWhiteSpace(singleLetterApplicationBinding))
                return singleLetterApplicationBinding!;
        }

        return "";
    }

    private static bool IsAlphabeticBindingToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        for (var i = 0; i < token.Length; i++)
        {
            var ch = token[i];
            if (ch < 'A' || ch > 'Z')
                return false;
        }
        return true;
    }

    private static string? ResolveApplicationOrdinalBinding(int slot, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (slot <= 0)
            return null;

        var appTask = _applicationTask;
        if (appTask is null)
            return null;

        if (slot <= appTask.ResourcesSemantic.Ordered.Count)
        {
            var memberName = ResolveTaskResourceMemberName(appTask, appTask.ResourcesSemantic.Ordered[slot - 1]);
            return IsRuntimeCounterBinding(appTask.ResourcesSemantic.Ordered[slot - 1].Name ?? "", memberName)
                ? "Counter"
                : $"Application.Instance.{memberName}";
        }

        return null;
    }

    private static string ResolveOrdinalBindingFromParentChain(
        int columnIndex,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (columnIndex <= 0)
            return "";

        var parentPrefix = "_parent";
        var parentOrdinal = task.ParentOrdinal;
        while (parentOrdinal.HasValue)
        {
            var parentTask = GetTaskByOrdinal(parentOrdinal, allTasks);
            if (parentTask is null)
                break;

            if (columnIndex <= parentTask.ResourcesSemantic.Ordered.Count)
            {
                var binding = ResolveTaskResourceBindingExpression(
                    parentTask,
                    parentTask.ResourcesSemantic.Ordered[columnIndex - 1],
                    dataObjects);
                return $"{parentPrefix}.{binding}";
            }

            parentOrdinal = parentTask.ParentOrdinal;
            parentPrefix += "._parent";
        }

        return "";
    }

    private static string ResolveTaskResourceBindingExpression(
        TaskSemantic task,
        TaskResourceColumnDef resource,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        foreach (var select in task.SelectsSemantic.Items)
        {
            if (!string.Equals(select.Type, "R", StringComparison.OrdinalIgnoreCase))
                continue;

            var selectResource = ResolveTaskResourceColumn(task, select.ColumnId);
            if (selectResource is null || selectResource.Id != resource.Id)
                continue;

            var binding = ResolveSelectExpression(select, task, dataObjects, "");
            if (!string.IsNullOrWhiteSpace(binding))
                return binding;
        }

        return ResolveTaskResourceMemberName(task, resource);
    }

    private static int ToAlphabeticSlot(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return 0;
        var normalized = token.Trim().ToUpperInvariant();
        if (!IsAlphabeticBindingToken(normalized))
            return 0;
        long slot = 0;
        foreach (var ch in normalized)
        {
            slot = (slot * 26) + (ch - 'A' + 1);
            if (slot > int.MaxValue)
                return 0;
        }
        return (int)slot;
    }

    private static string TranslateXpaExpressionToCSharp(
        string xpaExpr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        string? expressionAttr = null)
        => TryTranslateTypedXpaExpression(xpaExpr, task, dataObjects, expressionAttr, out var emitted)
            ? emitted.Code
            : WebUtility.HtmlDecode(xpaExpr ?? "").Trim();

    private static bool TryTranslateTypedXpaExpression(
        string xpaExpr,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        string? expressionAttr,
        out XpaTypedExpression emitted)
    {
        emitted = default;
        if (string.IsNullOrWhiteSpace(xpaExpr))
            return false;

        var source = WebUtility.HtmlDecode(xpaExpr);
        var expectedReturnType = ResolveSimpleReturnTypeForExpressionAttribute(expressionAttr);
        var destination = new XpaExpressionDestination(
            expectedReturnType,
            XpaExpressionTypeMap.FromReturnType(expectedReturnType));
        var context = new XpaTypedExpressionContext(
            name => ResolveTypedXpaSymbol(name, task, dataObjects),
            (name, arguments) => ResolveTypedXpaFunction(name, arguments, task, destination),
            (suffix, literalValue) => ResolveTypedXpaLiteralSuffix(
                suffix,
                literalValue,
                task,
                dataObjects),
            destination);

        if (XpaTypedExpressionEmitter.TryEmit(source, context, out emitted))
            return true;

        ConversionTelemetry.Log(
            "TYPED_EXPRESSION_PARSE_GAP",
            string.Create(
                CultureInfo.InvariantCulture,
                $"task={task.Ordinal} attr={expressionAttr ?? ""} source={QuoteTelemetry(TruncateTelemetryValue(source))}"));
        return false;
    }

    private static XpaTypedExpression? ResolveTypedXpaLiteralSuffix(
        string suffix,
        string literalValue,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (!string.Equals(suffix, "DSOURCE", StringComparison.OrdinalIgnoreCase))
            return null;

        var commaIndex = literalValue.IndexOf(',');
        var objectToken = commaIndex >= 0
            ? literalValue[..commaIndex]
            : literalValue;
        if (!int.TryParse(
                objectToken.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var objectOrdinal))
        {
            return null;
        }

        var dataObject = ResolveDataObjectByOrdinal(dataObjects, objectOrdinal);
        if (dataObject is null)
            return null;

        return new XpaTypedExpression(
            $"typeof({ResolveModelTypeReference(dataObject, task)})",
            "System.Type",
            XpaType.Object);
    }

    private static XpaTypedExpression? ResolveTypedXpaSymbol(
        string name,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        TaskResourceColumnDef? resolvedResource = null;
        var binding = ResolveExpressionOrdinalBinding(
            name,
            task,
            _allTasks ?? Array.Empty<TaskSemantic>(),
            dataObjects);

        if (string.IsNullOrWhiteSpace(binding) &&
            task.SelectsSemantic.ItemsByName.TryGetValue(name, out var select) &&
            select is not null)
        {
            binding = ResolveSelectExpression(select, task, dataObjects, "");
        }

        if (string.IsNullOrWhiteSpace(binding) &&
            task.ResourcesSemantic.ByName.TryGetValue(name, out var namedResource) &&
            namedResource is not null)
        {
            resolvedResource = namedResource;
            binding = ResolveTaskResourceMemberName(task, namedResource);
        }

        if (string.IsNullOrWhiteSpace(binding) &&
            task.ResourcesSemantic.ByLegacyName.TryGetValue(name, out var legacyResource) &&
            legacyResource is not null)
        {
            resolvedResource = legacyResource;
            binding = ResolveTaskResourceMemberName(task, legacyResource);
        }

        if (string.IsNullOrWhiteSpace(binding))
        {
            var accessibleResources = BuildAccessibleLegacyResourceReferenceMap(task, dataObjects);
            if (accessibleResources.TryGetValue(name, out var accessibleBinding) &&
                !string.IsNullOrWhiteSpace(accessibleBinding))
            {
                binding = accessibleBinding;
                resolvedResource = ResolveResourceByTargetPath(
                    task,
                    binding,
                    _allTasks ?? Array.Empty<TaskSemantic>());
            }
        }

        if (string.IsNullOrWhiteSpace(binding))
            binding = name;

        var returnType = "";
        if (resolvedResource is not null)
            TryResolveTaskResourceStrictReturnType(resolvedResource, task, out returnType);
        if (string.IsNullOrWhiteSpace(returnType))
            TryResolveSimpleSourceReturnTypeFromResourcePath(task, binding, out returnType);

        var type = XpaExpressionTypeMap.FromReturnType(returnType);
        return new XpaTypedExpression(
            binding,
            string.IsNullOrWhiteSpace(returnType) ? "object" : returnType,
            type == XpaType.Unknown ? XpaType.Object : type);
    }

    private static XpaTypedExpression? ResolveTypedXpaFunction(
        string name,
        IReadOnlyList<XpaTypedExpression> arguments,
        TaskSemantic task,
        XpaExpressionDestination expressionDestination)
    {
        var normalizedFunction = NormalizeXpaFunctionContractName(name);
        if (normalizedFunction == "DNSET" && arguments.Count == 2)
        {
            return new XpaTypedExpression(
                $"{arguments[0].Code} = {arguments[1].Code}",
                "object",
                XpaType.Object);
        }

        if (normalizedFunction == "DNCAST" &&
            arguments.Count == 2 &&
            TryResolveDotNetCastTypeName(arguments[1].Code, out var castTypeName))
        {
            return new XpaTypedExpression(
                $"ExternalTypeCompat.ConvertTo<{castTypeName}>({arguments[0].Code})",
                castTypeName,
                XpaType.Object);
        }

        if (string.Equals(name, "ExpCalc", StringComparison.OrdinalIgnoreCase) &&
            arguments.Count == 1 &&
            TryReadRegisteredExpressionOrdinalArgument(arguments[0].Code, out var expressionOrdinal))
        {
            var expressionReturnType = "";
            if (task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expressionOrdinal, out var expression) &&
                expression is not null)
            {
                expressionReturnType = NormalizeReturnTypeToken(
                    ResolveIntrinsicReturnTypeForExpressionEntry(expression));
            }

            var expressionType = XpaExpressionTypeMap.FromReturnType(expressionReturnType);
            return new XpaTypedExpression(
                $"Exp_{expressionOrdinal}()",
                string.IsNullOrWhiteSpace(expressionReturnType) ? "object" : expressionReturnType,
                expressionType == XpaType.Unknown ? XpaType.Object : expressionType);
        }

        var target = ResolveTypedXpaFunctionTarget(name, task);
        var renderedArguments = new string[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (i == 0 &&
                RequiresVariableIndexArgument(name) &&
                argument.Type != XpaType.Number)
            {
                argument = new XpaTypedExpression(
                    $"u.IndexOf({argument.Code})",
                    "Number",
                    XpaType.Number);
            }
            else if (ShouldApplyExpressionDestinationToFunctionResultArgument(
                         normalizedFunction,
                         i,
                         arguments.Count,
                         expressionDestination) &&
                     XpaExpressionTypeMap.TryApply(argument, expressionDestination, out var contextualArgument))
            {
                argument = contextualArgument;
            }
            else if (TryReadDotNetMethodArgumentTypeEvidence(
                         target,
                         task,
                         i,
                         arguments.Count,
                         out var dotNetParameterType) &&
                     string.Equals(
                         NormalizeReturnTypeToken(dotNetParameterType),
                         "System.Char",
                         StringComparison.Ordinal) &&
                     argument.LiteralValue is { Length: 1 } charLiteral)
            {
                argument = new XpaTypedExpression(
                    ToCSharpCharLiteral(charLiteral[0]),
                    "System.Char",
                    XpaType.Object);
            }
            else if (TryResolveTypedFunctionArgumentDestination(
                    name,
                    target,
                    i,
                    arguments.Count,
                    task,
                    out var argumentDestination) &&
                XpaExpressionTypeMap.TryApply(argument, argumentDestination, out var converted))
            {
                argument = converted;
            }
            renderedArguments[i] = argument.Code;
        }

        var renderedCode = $"{target}({string.Join(", ", renderedArguments)})";
        var returnType = "";
        if (TryGetAccessibleFunctionContract(task, name, out var accessibleContract, out _))
            returnType = accessibleContract.ReturnType;
        else if (TryGetComponentFunctionCallContract(name, out var componentContract))
            returnType = componentContract.ReturnType;
        else if (TryReadDotNetMethodCallEvidence(renderedCode, task, out var dotNetReturnType))
            returnType = dotNetReturnType;
        else
            TryResolveKnownXpaFunctionReturnType(target, renderedArguments, out returnType);

        var type = XpaExpressionTypeMap.FromReturnType(returnType);
        return new XpaTypedExpression(
            renderedCode,
            string.IsNullOrWhiteSpace(returnType) ? "object" : returnType,
            type == XpaType.Unknown ? XpaType.Object : type);
    }

    private static bool ShouldApplyExpressionDestinationToFunctionResultArgument(
        string normalizedFunction,
        int argumentIndex,
        int argumentCount,
        XpaExpressionDestination destination)
    {
        if (destination.Type is XpaType.Unknown or XpaType.Object)
            return false;

        if (normalizedFunction == "IF")
            return argumentCount >= 3 && argumentIndex is 1 or 2;

        if (normalizedFunction is not ("CASE" or "CASEUNTYPED") || argumentCount < 4)
            return false;

        return (argumentIndex >= 2 && argumentIndex % 2 == 0) ||
               argumentIndex == argumentCount - 1;
    }

    private static bool TryResolveDotNetCastTypeName(string typeExpression, out string typeName)
    {
        typeName = (typeExpression ?? "").Trim();
        if (typeName.StartsWith("DotNet.", StringComparison.OrdinalIgnoreCase))
            typeName = typeName["DotNet.".Length..];
        if (typeName.StartsWith("global::", StringComparison.Ordinal))
            typeName = typeName["global::".Length..];

        return !string.IsNullOrWhiteSpace(typeName) &&
               IsSimpleIdentifierPath(typeName);
    }

    private static bool RequiresVariableIndexArgument(string functionName)
    {
        var normalized = NormalizeXpaFunctionContractName(functionName);
        return normalized is "VARMOD" or "VARPREV" or "VARCURR" or "VARSET" or "VECSET";
    }

    private static string ResolveTypedXpaFunctionTarget(string name, TaskSemantic task)
    {
        if (task.FunctionOverridesSemantic.Any(
                function => string.Equals(function.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return ToCodeIdentifierPreservingCase(name);
        }

        var parentFunctions = ResolveAccessibleParentFunctionTargets(task);
        if (parentFunctions.TryGetValue(name, out var parentTarget))
            return parentTarget;

        var applicationFunctions = ResolveAccessibleApplicationFunctionTargets(task);
        if (applicationFunctions.TryGetValue(name, out var applicationTarget))
            return applicationTarget;

        if (TryGetComponentFunctionCallContract(name, out var componentContract))
            return componentContract.TargetName;

        var userMethods = GetUserMethodsPublicNameMap();
        if (userMethods.TryGetValue(name, out var userMethod))
            return $"u.{userMethod}";

        if (_xpaFunctionMap.TryGetValue(name, out var xpaTarget))
            return xpaTarget;

        if (TryResolveComponentFunctionTargetWithoutContract(name, out var componentTarget))
            return componentTarget;

        return name.Contains('.', StringComparison.Ordinal)
            ? name
            : $"u.{name}";
    }

    private static bool TryResolveComponentFunctionTargetWithoutContract(
        string functionName,
        out string target)
    {
        target = "";
        var lookupName = ExtractFunctionLookupName(functionName);
        if (string.IsNullOrWhiteSpace(lookupName) ||
            ReservedRuntimeFunctionNames.Contains(lookupName) ||
            !_componentFunctionSourceByName.TryGetValue(lookupName, out var componentName) ||
            string.IsNullOrWhiteSpace(componentName))
        {
            return false;
        }

        var componentNamespace = ResolveNamespaceForComponent(componentName);
        var methodName = ToCodeIdentifierPreservingCase(lookupName);
        target = $"global::{componentNamespace}.ComponentFunctions.{methodName}";
        return true;
    }

    private static bool TryResolveTypedFunctionArgumentDestination(
        string sourceName,
        string targetName,
        int argumentIndex,
        int argumentCount,
        TaskSemantic task,
        out XpaExpressionDestination destination)
    {
        destination = default;
        string returnType;
        if (!TryGetAccessibleFunctionArgumentType(task, sourceName, argumentIndex, out returnType) &&
            !TryGetComponentFunctionArgumentType(sourceName, argumentIndex, out returnType) &&
            !TryReadDotNetMethodArgumentTypeEvidence(
                targetName,
                task,
                argumentIndex,
                argumentCount,
                out returnType) &&
            !TryResolveXpaFunctionEmissionArgumentReturnTypeContract(
                targetName,
                argumentIndex,
                argumentCount,
                out returnType))
        {
            return false;
        }

        var type = XpaExpressionTypeMap.FromReturnType(returnType);
        if (type == XpaType.Unknown)
            return false;

        destination = new XpaExpressionDestination(returnType, type);
        return true;
    }

    private static bool BuildTypeValuedCaseExpression(IReadOnlyList<string> args, out string expression)
    {
        expression = "";
        if (args.Count < 4 || args.Count % 2 != 0)
            return false;

        var selector = StripRedundantOuterParentheses(args[0].Trim());
        if (string.IsNullOrWhiteSpace(selector) || !IsTypeOfExpression(args[^1]))
            return false;

        for (var i = 2; i < args.Count - 1; i += 2)
        {
            if (!IsTypeOfExpression(args[i]))
                return false;
        }

        var fallback = args[^1].Trim();
        for (var valueIndex = args.Count - 3; valueIndex >= 1; valueIndex -= 2)
        {
            var value = args[valueIndex].Trim();
            var result = args[valueIndex + 1].Trim();
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(result))
                return false;

            fallback = $"({selector} == {value} ? {result} : {fallback})";
        }

        expression = fallback;
        return true;
    }

    private static bool HasLikelyOperandBefore(string expression, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var ch = expression[i];
            if (char.IsWhiteSpace(ch))
                continue;

            return char.IsLetterOrDigit(ch) ||
                   ch == '_' ||
                   ch == ')' ||
                   ch == ']' ||
                   ch == '\'' ||
                   ch == '"';
        }

        return false;
    }

    private static Dictionary<string, string> BuildAccessibleLegacyResourceReferenceMap(TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (_accessibleLegacyResourceReferenceMapCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddSelectKeys(task, "");
        AddResourceKeys(task, "", includeSlotAliases: true, slotAliasBase: 3);

        void AddResourceKeys(TaskSemantic owner, string prefix, bool includeSlotAliases, int slotAliasBase, int maxSlotAliasLength = int.MaxValue)
        {
            for (var i = 0; i < owner.ResourcesSemantic.Ordered.Count; i++)
            {
                var resource = owner.ResourcesSemantic.Ordered[i];
                var memberName = ResolveTaskResourceMemberName(owner, resource);
                if (prefix.StartsWith("Application.Instance.", StringComparison.Ordinal) &&
                    IsRuntimeCounterBinding(resource.Name ?? "", memberName))
                    continue;
                var reference = prefix + memberName;

                if (!string.IsNullOrWhiteSpace(resource.Name))
                    result.TryAdd(resource.Name, reference);

                if (includeSlotAliases)
                {
                    var slotAlias = ToLegacyExpressionAlias(i + slotAliasBase);
                    if (!string.IsNullOrWhiteSpace(slotAlias) &&
                        slotAlias.Length <= maxSlotAliasLength)
                        result.TryAdd(slotAlias, reference);
                }
            }

            foreach (var pair in owner.ResourcesSemantic.ByLegacyName)
                result.TryAdd(pair.Key, prefix + ResolveTaskResourceMemberName(owner, pair.Value));
        }

        void AddSelectKeys(TaskSemantic owner, string prefix)
        {
            foreach (var pair in BuildSelectNameToExpressionMap(owner, dataObjects))
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                    continue;
                if (IsRuntimeCounterBinding(pair.Key, pair.Value))
                    continue;

                var reference = pair.Value.StartsWith("Application.Instance.", StringComparison.Ordinal)
                    ? pair.Value
                    : prefix + pair.Value;
                result.TryAdd(pair.Key, reference);
            }
        }

        if (_allTasks is not null)
        {
            var depth = 0;
            var parentOrdinal = task.ParentOrdinal;
            while (parentOrdinal.HasValue)
            {
                var parentTask = GetTaskByOrdinal(parentOrdinal, _allTasks);
                if (parentTask is null)
                    break;

                depth++;
                var prefix = string.Concat(Enumerable.Repeat("_parent.", depth));
                AddSelectKeys(parentTask, prefix);
                AddResourceKeys(parentTask, prefix, includeSlotAliases: true, slotAliasBase: 3);
                parentOrdinal = parentTask.ParentOrdinal;
            }

            var appTask = _applicationTask;
            if (appTask is not null && !ReferenceEquals(appTask, task))
            {
                AddSelectKeys(appTask, "Application.Instance.");
                AddResourceKeys(appTask, "Application.Instance.", includeSlotAliases: true, slotAliasBase: 1, maxSlotAliasLength: 1);
            }
        }

        _accessibleLegacyResourceReferenceMapCache[task.Ordinal] = result;
        return result;

    }

    private static string ToLegacyExpressionAlias(int ordinal)
    {
        if (ordinal <= 0)
            return "";

        var value = ordinal;
        var sb = new StringBuilder();
        while (value > 0)
        {
            value--;
            sb.Insert(0, (char)('A' + (value % 26)));
            value /= 26;
        }

        return sb.ToString();
    }

    private static bool TryResolveTaskResourceSourceReturnTypeFromTarget(
        TaskSemantic task,
        string target,
        out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(target) || !IsSimpleIdentifierPath(target))
            return false;

        var resource = ResolveTaskResourceForAssignment(task, null, target);
        if (resource is null)
            return false;

        var ownerTask = ResolveTaskOwnerFromTargetPath(task, target) ??
                        (task.ResourcesSemantic.Ordered.Any(r => ReferenceEquals(r, resource))
                            ? task
                            : ResolveOwningTaskForResource(resource) ?? task);
        if (TryMapSourceResourceReturnType(resource, ownerTask, out returnType))
            return true;

        var attrObj = ResolveEffectiveTargetAttrObj(
            NormalizeAttrObjKind(resource.AttrObj),
            NormalizeAttrObjKind(resource.CellModelAttrObj));
        if (string.IsNullOrWhiteSpace(attrObj))
        {
            var resolvedType = ResolveTaskResourceColumnType(resource, _allFieldModels, ownerTask);
            attrObj = ResolveAttrObjForColumnType(resolvedType, _allFieldModels, ownerTask);
        }

        returnType = NormalizeReturnTypeToken(MapAttrObjToReturnType(attrObj));
        return !string.IsNullOrWhiteSpace(returnType);
    }


}


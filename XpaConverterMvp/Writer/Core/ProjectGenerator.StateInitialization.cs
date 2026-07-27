using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void InitializeGenerationState(ProjectGenerationRequest request)
    {
        ResetTypedExpressionTelemetryCounters();
        ResetXpaFunctionContractTelemetryCounters();

        _targetComponent = request.TargetComponent;
        _solutionRoot = request.SolutionRoot;
        _sourceRoot = request.SourceRoot;
        _targetNamespace = request.AppNamespace;
        _outputType = string.Equals(request.OutputType, "ClassLibrary", StringComparison.OrdinalIgnoreCase) ? "Library" : "WinExe";
        _runtimeCoreReferenceMode = string.Equals(request.EnvReferenceMode, "Dll", StringComparison.OrdinalIgnoreCase) ? "Dll" : "Project";
        _runtimeCoreDllPath = request.EnvDllPath;
        _componentNamespaces = request.ComponentNamespaces ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _isComponentized = _componentNamespaces.Count > 0;
        _projectReferenceMap = (request.ProjectReferenceMap ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        _dllReferenceMap = (request.DllReferenceMap ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        _dotNetReferenceAssemblyPathMap = BuildDotNetReferenceAssemblyPathMap(request.Parsed, request.OutputRoot);
        _projectReferenceManifests = _projectReferenceMap
            .Concat(_dllReferenceMap)
            .Select(kv => (Component: kv.Key, Manifest: ProjectManifest.LoadForSource(kv.Value)))
            .Where(x => x.Manifest is not null)
            .ToDictionary(x => x.Component, x => x.Manifest!, StringComparer.OrdinalIgnoreCase);
        _currentProjectManifest = null;
        if (request.IncrementalOutput &&
            (request.ForceTaskScopedGeneration || request.TaskFilters is { Count: > 0 }))
        {
            var currentProjectName = request.AppNamespace.Split('.').FirstOrDefault() ?? request.AppNamespace;
            _currentProjectManifest = ProjectManifest.LoadForProject(
                System.IO.Path.Combine(request.OutputRoot, currentProjectName + ".csproj"));
        }
        _externalManifestColumnAttrObjIndex = BuildExternalManifestColumnAttrObjIndex();
        var componentFunctionSourceByName = request.Parsed.ComponentFunctions
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.ComponentName))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ComponentName, StringComparer.OrdinalIgnoreCase);
        foreach (var manifestEntry in _projectReferenceManifests)
        {
            foreach (var functionName in manifestEntry.Value.Functions.Keys)
            {
                if (string.IsNullOrWhiteSpace(functionName) ||
                    ReservedRuntimeFunctionNames.Contains(functionName))
                {
                    continue;
                }

                componentFunctionSourceByName.TryAdd(functionName, manifestEntry.Key);
            }
        }
        _componentFunctionSourceByName = componentFunctionSourceByName;
        _componentFunctionReturnTypeByName = BuildComponentFunctionReturnTypeMap(request.Parsed);
        _externalMagicComponentNames = request.Parsed.ExternalMagicComponents
            .Select(x => x.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _componentRightLiteralMap = request.Parsed.ComponentRightsByLiteral.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var ns = ResolveNamespaceForComponent(kv.Value.ComponentName);
                return $"global::{ns}.Roles.{kv.Value.RoleMemberName}";
            },
            StringComparer.OrdinalIgnoreCase);
        _applicationSelectMap = new Dictionary<string, string>(request.Parsed.ApplicationSelectMap, StringComparer.OrdinalIgnoreCase);
        _parentSelectMapByTaskOrdinal = request.Parsed.ParentSelectMapByTaskOrdinal.ToDictionary(
            kv => kv.Key,
            kv => new Dictionary<string, string>(kv.Value, StringComparer.OrdinalIgnoreCase));
        _dataObjectsByOrdinal = request.Parsed.DataObjects.ToDictionary(d => d.Ordinal);
        _allFieldModels = request.Parsed.FieldModels;
        _fieldModelTypeNameByOrdinal = BuildFieldModelTypeNameMap(_allFieldModels);
        _dataSourceTypeByObjectOrdinal = request.Parsed.DataObjects
            .ToDictionary(d => d.Ordinal, d => $"typeof({ResolveEntityTypeReferenceForRegistry(d)})");
        _allTasks = request.Parsed.Tasks;
        var functionContractsByLeaf = new Dictionary<string, List<FunctionOverrideSemantic>>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidateTask in _allTasks)
        {
            foreach (var functionContract in candidateTask.FunctionOverridesSemantic)
            {
                foreach (var leaf in new[] { functionContract.Name, functionContract.MethodName }
                             .Where(name => !string.IsNullOrWhiteSpace(name))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!functionContractsByLeaf.TryGetValue(leaf, out var contracts))
                    {
                        contracts = new List<FunctionOverrideSemantic>();
                        functionContractsByLeaf[leaf] = contracts;
                    }
                    contracts.Add(functionContract);
                }
            }
        }
        _uniqueFunctionContractByLeaf = functionContractsByLeaf.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
                .Select(BuildFunctionContractSignature)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .Count() == 1
                    ? pair.Value[0]
                    : null,
            StringComparer.OrdinalIgnoreCase);
        _applicationTask = request.Parsed.Tasks.FirstOrDefault(t => t.MainProgram)
            ?? request.Parsed.Tasks.FirstOrDefault(t => !t.ParentOrdinal.HasValue)
            ?? request.Parsed.Tasks.FirstOrDefault();
        _tasksByOrdinal = request.Parsed.Tasks.ToDictionary(t => t.Ordinal);
        _tasksByPublicName = request.Parsed.Tasks
            .Where(t => !string.IsNullOrWhiteSpace(t.PublicName))
            .GroupBy(t => t.PublicName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _eventCommandByDescription = new Dictionary<string, string>(StringComparer.Ordinal);
        _expressionInvocationByOrdinal = new Dictionary<int, string>();
        _handlerKindByReference = new Dictionary<TaskHandlerDef, byte>(ReferenceEqualityComparer.Instance);
        _handlerBodyByReference = new Dictionary<TaskHandlerDef, HandlerBodySemantic>(ReferenceEqualityComparer.Instance);
        foreach (var task in request.Parsed.Tasks)
        {
            foreach (var entry in task.EventsSemantic.CommandByDescription)
                _eventCommandByDescription.TryAdd(entry.Key, entry.Value);
            foreach (var entry in task.ExpressionsSemantic.InvocationByOrdinal)
                _expressionInvocationByOrdinal.TryAdd(entry.Key, entry.Value);
            RegisterHandlerKind(task.HandlersSemantic.ValueChangedHandlers, 1);
            RegisterHandlerKind(task.HandlersSemantic.UserCommandHandlers, 2);
            RegisterHandlerKind(task.HandlersSemantic.InternalHandlers, 3);
            RegisterHandlerKind(task.HandlersSemantic.ExpressionHandlers, 4);
            RegisterHandlerKind(task.HandlersSemantic.TimerHandlers, 5);
            RegisterHandlerKind(task.HandlersSemantic.SystemHandlers, 6);
            RegisterHandlerKind(task.HandlersSemantic.RecordHandlers, 7);
            foreach (var body in task.HandlersSemantic.Bodies)
                _handlerBodyByReference.TryAdd(body.Handler, body);
        }
        _resourceOwnerByReference = new Dictionary<TaskResourceColumnDef, TaskSemantic>(ReferenceEqualityComparer.Instance);
        foreach (var task in request.Parsed.Tasks)
        {
            foreach (var resource in task.ResourcesSemantic.Ordered)
                _resourceOwnerByReference.TryAdd(resource, task);
        }
        _tasksByDeclaredTaskId = request.Parsed.Tasks
            .Where(t => int.TryParse(t.TaskId, out _))
            .GroupBy(t => int.Parse(t.TaskId!, System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.First());
        _topLevelTasksByProgramIndex = request.Parsed.Tasks
            .Where(t => !t.ParentOrdinal.HasValue && t.TopLevelProgramIndex.HasValue)
            .GroupBy(t => t.TopLevelProgramIndex!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        _childTasksByParentOrdinal = request.Parsed.Tasks
            .Where(t => t.ParentOrdinal.HasValue)
            .GroupBy(t => t.ParentOrdinal!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TaskSemantic>)g
                    .OrderBy(x => x.SubtaskIndex ?? int.MaxValue)
                    .ThenBy(x => x.Ordinal)
                    .ToList());
        _topLevelTasks = request.Parsed.Tasks
            .Where(t => !t.ParentOrdinal.HasValue)
            .OrderBy(t => t.Ordinal)
            .ToList();
        _topLevelAccessibleResourceKeys = _topLevelTasks
            .Select(t => t.ResourcesSemantic.ByName.Keys
                .Concat(t.ResourcesSemantic.ByLegacyName.Keys)
                .Concat(Enumerable.Range(0, t.ResourcesSemantic.Ordered.Count)
                    .Select(i => ToLegacyExpressionAlias(i + 1))
                    .Where(alias => alias.Length == 1))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            .SelectMany(keys => keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _subformTargetTaskOrdinals = request.Parsed.Tasks
            .SelectMany(t => t.View.SubformBindings)
            .Where(b => b.Kind == ViewSubformBindingKind.Task && b.TargetTaskOrdinal.HasValue)
            .Select(b => b.TargetTaskOrdinal!.Value)
            .ToHashSet();
        _reservedViewClassNames = request.Parsed.Tasks
            .Select(t => t.View.ClassName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        _multiFormCandidateCounts = BuildMultiFormCandidateCounts(request.Parsed.Tasks);
        _viewClassNameCounts = request.Parsed.Tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.View.ClassName))
            .GroupBy(task => task.View.ClassName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        BuildTaskClassNameIndexes(request.Parsed.Tasks);
        _taskClassNameByOrdinal = new Dictionary<int, string>();
        _taskClassNameAssignedRegistry = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _taskClassNameResolutionInProgress = new HashSet<int>();
        _taskTypeReferenceByOrdinal = new Dictionary<int, string>();
        _resourceMemberNameCache = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        _resourceMemberNameByTaskOrdinal = new Dictionary<int, Dictionary<int, string>>();
        _resourceMemberNameCacheBuiltTaskOrdinals = new HashSet<int>();
        _taskResourceByMemberNameCache = new Dictionary<int, Dictionary<string, TaskResourceColumnDef>>();
        _reservedTaskMemberNameCache = new Dictionary<int, HashSet<string>>();
        _reservedTaskCommandNameCache = new Dictionary<int, HashSet<string>>();
        _loadedSourceComponents = request.Parsed.Tasks
            .Select(t => t.SourceComponent)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        _resolvedCallTargetOrdinalCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _incomingTaskCallsByTargetOrdinal = new Dictionary<int, IReadOnlyList<(TaskSemantic Caller, TaskCallDef Call)>>();
        _optionalRunParameterStartIndexCache = BuildOptionalRunParameterStartIndexCache(request.Parsed.Tasks);
        _externalTaskTypeReferenceCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _expressionCodeCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _rawExpressionCodeCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _sharedExpressionEntryCodeCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _sharedRawExpressionEntryCodeCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _typedExpressionEntryCodeCache = new Dictionary<string, ProjectGenerator.EmittedExpression>(StringComparer.Ordinal);
        _typedExpressionReturnTypeByCodeCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _sourceExpressionReturnTypeCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _sourceFunctionCallTypeInfoCache = new Dictionary<string, ProjectGenerator.SourceFunctionCallTypeInfo>(StringComparer.Ordinal);
        _sourceExpressionReturnTypeResolutionInProgress = new HashSet<string>(StringComparer.Ordinal);
        _expressionCodeResolutionInProgress = new HashSet<string>(StringComparer.Ordinal);
        _selectNameToExpressionMapCache = new Dictionary<int, Dictionary<string, string>>();
        _selectNameToExpressionMapResolutionInProgress = new HashSet<int>();
        _modelMembersCache = new Dictionary<int, List<(int DbObj, string ModelType, string MemberName)>>();
        _dataObjectColumnMemberNamesByObjectOrdinal = new Dictionary<int, Dictionary<int, string>>();
        _modelMembersResolutionInProgress = new HashSet<int>();
        _linkMembersCache = new Dictionary<string, List<(TaskLogicLinkDef Link, string MemberName)>>(StringComparer.Ordinal);
        _selectExpressionCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _selectExpressionResolutionInProgress = new HashSet<string>(StringComparer.Ordinal);
        _selectBindValueExpressionCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _selectSourceMemberCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _legacyExpressionAliasMapCache = new Dictionary<int, Dictionary<string, string>>();
        _accessibleLegacyResourceReferenceMapCache = new Dictionary<int, Dictionary<string, string>>();
        _accessibleResourceKeysCache = new Dictionary<int, IReadOnlyList<string>>();
        _applicationResourceBindingMap = null;
        _ancestorResourceBindingMapCache = new Dictionary<int, Dictionary<string, string>>();
        _taskResourceAliasMapCache = new Dictionary<int, Dictionary<string, string>>();
        _taskResolvedMemberNameSetCache = new Dictionary<int, HashSet<string>>();
        _identifierBindingPreparationCache = new Dictionary<int, IdentifierBindingPreparation>();
        _taskCommandMemberMapCache = new Dictionary<int, Dictionary<string, string>>();
        _allowedParameterSelectNamesCache = new Dictionary<int, HashSet<string>>();
        _updateTargetExpressionCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _updateValueExpressionCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _targetValueInfoCache = new Dictionary<string, ProjectGenerator.TargetValueInfo>(StringComparer.Ordinal);
        _dataViewMemberColumnIndexCache = new Dictionary<int, IReadOnlyDictionary<string, DataColumnDef>>();
        _dataObjectMemberColumnIndexCache = null;
        _taskResourceForAssignmentCache = new Dictionary<string, TaskResourceColumnDef?>(StringComparer.Ordinal);
        _parentBindingExpressionCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _linkKeyColumnsCache = new Dictionary<string, IReadOnlyList<DataColumnDef>>(StringComparer.Ordinal);
        _filterOperandExpressionCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _linkFilterConditionCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _linkFilterConditionTemplateCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _effectiveRangeParameterSelectsCache = new Dictionary<int, IReadOnlyList<TaskLogicSelectDef>>();
        _parameterExpressionOrderCache = new Dictionary<int, Dictionary<string, int>>();
        _contextualInferenceCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _expectedTypeEvidenceCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _textualNumericResourceOverrideCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        _numericTextResourceOverrideCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        _logicalBlobResourceOverrideCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        _effectiveTaskResourceAttrObjCache = new Dictionary<int, Dictionary<TaskResourceColumnDef, string>>();
        _taskResourceColumnTypeCache = new Dictionary<int, Dictionary<TaskResourceColumnDef, string>>();
        _attrObjForColumnTypeCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _taskUpdatesForArrayItemInferenceCache = new Dictionary<int, TaskUpdateDef[]>();
        _taskTextsForArrayItemInferenceCache = new Dictionary<int, string[]>();
        _preparedRunArgumentsCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _nonInputArgumentBindingCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _nonInputArgumentCandidatesByTaskOrdinal = new Dictionary<int, IReadOnlyList<(string Expression, string Member, string Direction, string ParameterType, int Depth)>>();
        _observedTaskParameterEvidenceCache = new Dictionary<int, IReadOnlyList<Dictionary<string, int>>>();
        _observedTaskParameterEvidenceInProgress = new HashSet<int>();
        _incomingParameterCountCache = new Dictionary<int, int>();
        _taskParametersCache = new Dictionary<int, List<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)>>();
        _resolvedCallArgumentsCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        _rowActionConditionCodeCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _sharedActionConditionCodeCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _sharedConditionExpressionCodeCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _strippedActionCache = new Dictionary<string, TaskRowActionDef>(StringComparer.Ordinal);
        _normalizedStructuredConditionCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _structuredConditionConjunctionCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        _coveredStructuredConditionStripCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _statementBooleanConditionSyntaxCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _remarkLinesCache = new Dictionary<string, string[]>(StringComparer.Ordinal);
        _controlDataExpressionCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _viewDotNetResourceByRootOrdinal = new Dictionary<int, IReadOnlyDictionary<string, TaskResourceColumnDef?>>();
        _viewPrefixSymbolBindingMapCache = new Dictionary<int, Dictionary<string, string>>();
        _controlHandlerControlCache = new Dictionary<string, TaskFormControlDef?>(StringComparer.OrdinalIgnoreCase);
        _controlHandlerExactControlMapCache = new Dictionary<int, Dictionary<string, TaskFormControlDef>>();
        _controlHandlerMethodNameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _accessibleParentFunctionTargetsCache = new Dictionary<int, Dictionary<string, string>>();
        _state.TextIoLayoutClassOwners = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        _state.TextIoLayoutClassOwnersInitialized = false;
        PrecomputeStructuralGenerationIndexes(request.Parsed.Tasks);
        EnsureTextIoLayoutClassOwners(request.Parsed.Tasks);
        _viewDotNetResourceByRootOrdinal = BuildViewDotNetResourceIndex(request.Parsed.Tasks);
        _uniqueResourceReturnTypeByMemberName = BuildUniqueResourceReturnTypeByMemberNameIndex();
    }

    private static void PrecomputeStructuralGenerationIndexes(IReadOnlyList<TaskSemantic> tasks)
    {
        // These values depend only on the semantic model. Build them once before
        // starting the parallel emitters so every worker can safely share the
        // same read-only dictionaries instead of rebuilding the whole project.
        foreach (var task in tasks)
            ResolveTaskClassName(task, tasks);

        foreach (var task in tasks)
            ResolveTaskTypeReference(task, tasks);

        foreach (var task in tasks)
        {
            GetReservedTaskMemberNames(task);
            BuildTaskResourceMemberNameCache(task);
        }

        foreach (var task in tasks)
        {
            BuildReservedCommandNames(task);
            BuildTaskCommandMemberMap(task);
        }

    }

    private static void RegisterHandlerKind(IReadOnlyList<TaskHandlerDef> handlers, byte kind)
    {
        foreach (var handler in handlers)
            _handlerKindByReference.TryAdd(handler, kind);
    }
}

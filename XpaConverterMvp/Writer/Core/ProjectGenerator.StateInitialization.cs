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
        ResetLegacyExpressionTelemetryCounters();
        ResetExpressionEmissionAuditCounters();

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
                return $"{ns}.Roles.{kv.Value.RoleMemberName}.Allowed";
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
        _tasksByOrdinal = request.Parsed.Tasks.ToDictionary(t => t.Ordinal);
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
        BuildTaskClassNameIndexes(request.Parsed.Tasks);
        _taskClassNameByOrdinal = new Dictionary<int, string>();
        _taskClassNameResolutionInProgress = new HashSet<int>();
        _taskTypeReferenceByOrdinal = new Dictionary<int, string>();
        _resourceMemberNameCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _resourceMemberNameByTaskOrdinal = new Dictionary<int, Dictionary<int, string>>();
        _resourceMemberNameCacheBuiltTaskOrdinals = new HashSet<int>();
        _taskResourceByMemberNameCache = new Dictionary<int, Dictionary<string, TaskResourceColumnDef>>();
        _reservedTaskMemberNameCache = new Dictionary<int, HashSet<string>>();
        _loadedSourceComponents = request.Parsed.Tasks
            .Select(t => t.SourceComponent)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        _resolvedCallTargetOrdinalCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _optionalRunParameterStartIndexCache = BuildOptionalRunParameterStartIndexCache(request.Parsed.Tasks);
        _externalTaskTypeReferenceCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _expressionCodeCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _rawExpressionCodeCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _sharedExpressionEntryCodeCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _sharedRawExpressionEntryCodeCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _typedExpressionEntryCodeCache = new Dictionary<string, ProjectGenerator.EmittedExpression>(StringComparer.Ordinal);
        _typedExpressionReturnTypeByCodeCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _expressionCodeResolutionInProgress = new HashSet<string>(StringComparer.Ordinal);
        _selectNameToExpressionMapCache = new Dictionary<int, Dictionary<string, string>>();
        _selectNameToExpressionMapResolutionInProgress = new HashSet<int>();
        _modelMembersCache = new Dictionary<int, List<(int DbObj, string ModelType, string MemberName)>>();
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
        _allowedParameterSelectNamesCache = new Dictionary<int, HashSet<string>>();
        _updateTargetExpressionCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _updateValueExpressionCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _targetValueInfoCache = new Dictionary<string, ProjectGenerator.TargetValueInfo>(StringComparer.Ordinal);
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
        _preparedRunArgumentsCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _nonInputArgumentBindingCache = new Dictionary<string, string>(StringComparer.Ordinal);
        _observedTaskParameterEvidenceCache = new Dictionary<int, IReadOnlyList<Dictionary<string, int>>>();
        _observedTaskParameterEvidenceInProgress = new HashSet<int>();
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
        _viewPrefixSymbolBindingMapCache = new Dictionary<int, Dictionary<string, string>>();
        _controlHandlerControlCache = new Dictionary<string, TaskFormControlDef?>(StringComparer.OrdinalIgnoreCase);
        _controlHandlerExactControlMapCache = new Dictionary<int, Dictionary<string, TaskFormControlDef>>();
        _controlHandlerMethodNameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}

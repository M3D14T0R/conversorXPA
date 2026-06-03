using System.Xml;
using System.Xml.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static class XpaParser
{
    private static void TouchKnownXmlAttributes(XElement? node)
    {
        if (node is null)
            return;
        _ = node.Attribute("allowed_null")?.Value;
        _ = node.Attribute("ATTR")?.Value;
        _ = node.Attribute("attribute")?.Value;
        _ = node.Attribute("bottom")?.Value;
        _ = node.Attribute("CLSS")?.Value;
        _ = node.Attribute("CNFU")?.Value;
        _ = node.Attribute("Comment")?.Value;
        _ = node.Attribute("context_cookies")?.Value;
        _ = node.Attribute("database_definition")?.Value;
        _ = node.Attribute("DataTypeIdx")?.Value;
        _ = node.Attribute("DB_DEF_VAL_U")?.Value;
        _ = node.Attribute("END")?.Value;
        _ = node.Attribute("EndBlock")?.Value;
        _ = node.Attribute("EndBlockSegment")?.Value;
        _ = node.Attribute("ERR_LOG_DEF_CHG")?.Value;
        _ = node.Attribute("EVL_CND")?.Value;
        _ = node.Attribute("FldType")?.Value;
        _ = node.Attribute("IMG_DEF_CHG")?.Value;
        _ = node.Attribute("MgAttr")?.Value;
        _ = node.Attribute("ParentJsonDbhId")?.Value;
        _ = node.Attribute("PDOD")?.Value;
        _ = node.Attribute("PICT_U")?.Value;
        _ = node.Attribute("propagate")?.Value;
        _ = node.Attribute("Resident")?.Value;
        _ = node.Attribute("START")?.Value;
        _ = node.Attribute("storage")?.Value;
        _ = node.Attribute("translate")?.Value;
        _ = node.Attribute("TTL_DEF_CHG")?.Value;
        _ = node.Attribute("TXT_LEN")?.Value;
        _ = node.Attribute("VarRangeVeeIsn")?.Value;
        _ = node.Attribute("VIEW")?.Value;
        _ = node.Attribute("VIEWS")?.Value;
        _ = node.Attribute("VR_DISP")?.Value;
    }

    private sealed class ComponentMetadata
    {
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string? Folder { get; init; }
        public string? ComponentType { get; init; }
        public string? AssemblyName { get; init; }
        public string? AssemblyPath { get; init; }
        public string? UseSpecificVersion { get; init; }
        public string XmlPath { get; set; } = "";
        public Dictionary<int, string> ModelById { get; } = new();
        public List<string> ModelsByOrder { get; } = new();
        public Dictionary<int, string> DataObjectById { get; } = new();
        public List<string> DataObjectsByOrder { get; } = new();
        public Dictionary<int, string> ProgramById { get; } = new();
        public List<string> ProgramsByOrder { get; } = new();
        public Dictionary<int, string> FunctionById { get; } = new();
        public List<string> FunctionsByOrder { get; } = new();
        public Dictionary<int, string> RightById { get; } = new();
        public List<string> RightsByOrder { get; } = new();
    }

    private sealed class ParseContext
    {
        public required XDocument Document { get; init; }
        public int ComponentId { get; init; }
        public string Name { get; init; } = "";
        public string XmlPath { get; init; } = "";
        public bool IsMain { get; init; }
        public bool IsTablesOnlySupport { get; init; }
        public ComponentMetadata? Metadata { get; init; }
        public Dictionary<int, int> ModelLocalToGlobal { get; } = new();
        public Dictionary<int, int> DataObjectLocalToGlobal { get; } = new();
        public Dictionary<int, int> ProgramLocalTopLevelToGlobal { get; } = new();
        public Dictionary<string, int> ModelPublicToGlobal { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> DataObjectPublicToGlobal { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ProgramPublicToGlobalTopLevel { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, ComponentMetadata> ReferencedComponentMetadataCache { get; } = new();
    }

    public static ParsedXpa Parse(
        string xmlPath,
        IReadOnlyList<string>? componentXmlPaths = null,
        IReadOnlyList<string>? tablesXmlPaths = null,
        bool useImplicitComponentXmlResolution = true,
        string? mainXmlBaseDirectoryOverride = null,
        IReadOnlyDictionary<string, string>? projectReferenceMap = null)
    {
        var parsed = new ParsedXpa();
        var contexts = BuildContexts(xmlPath, parsed, componentXmlPaths, tablesXmlPaths, useImplicitComponentXmlResolution, mainXmlBaseDirectoryOverride, projectReferenceMap);
        parsed.HasRepositoryProperties = contexts.Any(c => c.Document.Descendants("RepositoryProperties").Any());
        parsed.HasDataRepositoryProperties = contexts.Any(c => c.Document.Descendants("DataRepositoryProperties").Any());
        parsed.HasHelpRepository = contexts.Any(c => c.Document.Descendants("HelpRepository").Any());
        parsed.HasXsdUnderViewCompound = contexts.Any(c => c.Document.Descendants("_XsdUnderViewCompound").Any());

        var modelOrdinal = 0;
        var controlButtonObj = 0;
        var dataObjectOrdinal = 0;
        foreach (var ctx in contexts)
        {
            ParseFieldModels(ctx, parsed, ref modelOrdinal);
            ParseControlButtonModels(ctx.Document, parsed, ref controlButtonObj);
            ParseDataObjects(ctx, parsed, ref dataObjectOrdinal);
            ParseRights(ctx, parsed);
        }

        InjectExternalManifestDataObjects(contexts, parsed, projectReferenceMap, ref dataObjectOrdinal);

        var taskOrdinal = 0;
        var topLevelProgramIndex = 0;
        foreach (var ctx in contexts)
            ParseTasks(ctx, contexts, parsed, ref taskOrdinal, ref topLevelProgramIndex);

        var main = contexts.FirstOrDefault(c => c.IsMain);
        if (main is not null)
            ParseMenus(main.Document, parsed);

        return parsed;
    }

    private static List<ParseContext> BuildContexts(
        string mainXmlPath,
        ParsedXpa? parsed = null,
        IReadOnlyList<string>? suppliedComponentXmlPaths = null,
        IReadOnlyList<string>? suppliedTablesXmlPaths = null,
        bool useImplicitComponentXmlResolution = true,
        string? mainXmlBaseDirectoryOverride = null,
        IReadOnlyDictionary<string, string>? projectReferenceMap = null)
    {
        var result = new List<ParseContext>();
        var mainDoc = LoadXmlDocument(mainXmlPath);
        var mainDir = string.IsNullOrWhiteSpace(mainXmlBaseDirectoryOverride)
            ? Path.GetDirectoryName(mainXmlPath) ?? ""
            : mainXmlBaseDirectoryOverride!;
        var componentXmlLookup = BuildSuppliedXmlLookup(suppliedComponentXmlPaths);
        var tableXmlCandidates = BuildTableXmlCandidates(suppliedTablesXmlPaths);
        var componentNodes = mainDoc.Descendants("ComponentsRepository")
            .Descendants("Components")
            .Elements("Component")
            .ToList();

        for (var i = 0; i < componentNodes.Count; i++)
        {
            var component = componentNodes[i];
            var metadata = ParseComponentMetadata(component);
            AddComponentFunctions(parsed, metadata);
            if (string.Equals(metadata.ComponentType, ".NET", StringComparison.OrdinalIgnoreCase))
            {
                parsed?.DotNetComponentReferences.Add(new DotNetComponentReferenceDef(
                    metadata.Name,
                    metadata.Description,
                    metadata.Folder,
                    metadata.AssemblyName,
                    metadata.AssemblyPath,
                    metadata.UseSpecificVersion));
                continue;
            }
            string? componentPath = ResolveSuppliedComponentXmlPath(componentXmlLookup, metadata);
            if (string.IsNullOrWhiteSpace(componentPath) && useImplicitComponentXmlResolution)
                componentPath = ResolveComponentXmlPath(mainDir, component, metadata.Name);
            var hasFullComponentXml = !string.IsNullOrWhiteSpace(componentPath) && File.Exists(componentPath);
            if (hasFullComponentXml)
            {
                metadata.XmlPath = componentPath!;
                var doc = LoadXmlDocument(componentPath!);
                result.Add(new ParseContext
                {
                    Document = doc,
                    ComponentId = i + 1,
                    Name = metadata.Name,
                    XmlPath = componentPath!,
                    IsMain = false,
                    Metadata = metadata
                });
                parsed?.Components.Add(metadata.Name);
            }
            else
            {
                PopulateMetadataFromProjectManifest(metadata, projectReferenceMap);
                AddComponentFunctions(parsed, metadata);
                var tableCandidate = ResolveSupportingTablesXml(tableXmlCandidates, metadata);
                if (tableCandidate is not null)
                {
                    metadata.XmlPath = tableCandidate.Path;
                    result.Add(new ParseContext
                    {
                        Document = tableCandidate.Document,
                        ComponentId = i + 1,
                        Name = metadata.Name,
                        XmlPath = tableCandidate.Path,
                        IsMain = false,
                        IsTablesOnlySupport = true,
                        Metadata = metadata
                    });
                }
                else
                {
                    result.Add(new ParseContext
                    {
                        Document = new XDocument(new XElement("Application")),
                        ComponentId = i + 1,
                        Name = metadata.Name,
                        XmlPath = metadata.XmlPath,
                        IsMain = false,
                        Metadata = metadata
                    });
                }

                parsed?.ExternalMagicComponents.Add(new ExternalMagicComponentDef(
                    metadata.Name,
                    metadata.Description,
                    metadata.ModelsByOrder.ToList(),
                    metadata.DataObjectsByOrder.ToList(),
                    metadata.ProgramsByOrder.ToList(),
                    metadata.RightsByOrder.ToList(),
                    tableCandidate is not null));
            }

            if (parsed is not null)
            {
                foreach (var kv in metadata.RightById)
                    parsed.ComponentRightRefs.Add(new ComponentRightRefDef(i + 1, kv.Key, kv.Value, metadata.Name));
            }
        }

        result.Add(new ParseContext
        {
            Document = mainDoc,
            ComponentId = 0,
            Name = Path.GetFileNameWithoutExtension(mainXmlPath),
            XmlPath = mainXmlPath,
            IsMain = true
        });

        return result;
    }

    private static void AddComponentFunctions(ParsedXpa? parsed, ComponentMetadata metadata)
    {
        if (parsed is null)
            return;

        foreach (var functionName in metadata.FunctionsByOrder)
        {
            if (string.IsNullOrWhiteSpace(functionName))
                continue;
            var exists = parsed.ComponentFunctions.Any(x =>
                string.Equals(x.Name, functionName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.ComponentName, metadata.Name, StringComparison.OrdinalIgnoreCase));
            if (!exists)
                parsed.ComponentFunctions.Add(new ComponentFunctionDef(functionName, metadata.Name));
        }
    }

    private static void PopulateMetadataFromProjectManifest(ComponentMetadata metadata, IReadOnlyDictionary<string, string>? projectReferenceMap)
    {
        if (projectReferenceMap is null || string.IsNullOrWhiteSpace(metadata.Name))
            return;
        if (!projectReferenceMap.TryGetValue(metadata.Name, out var projectPath))
            return;

        var manifest = ProjectManifest.LoadForSource(projectPath);
        if (manifest is null)
            return;

        foreach (var kv in manifest.ProgramsByIndex)
            metadata.ProgramById[kv.Key] = kv.Value;
        foreach (var name in manifest.ProgramsByIndex.OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            if (!metadata.ProgramsByOrder.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
                metadata.ProgramsByOrder.Add(name);
        }

        foreach (var kv in manifest.DataObjectsByIndex)
            metadata.DataObjectById[kv.Key] = kv.Value;
        foreach (var name in manifest.DataObjectsByIndex.OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            if (!metadata.DataObjectsByOrder.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
                metadata.DataObjectsByOrder.Add(name);
        }

        foreach (var right in manifest.Rights.Values)
        {
            if (!metadata.RightsByOrder.Any(existing => string.Equals(existing, right, StringComparison.OrdinalIgnoreCase)))
                metadata.RightsByOrder.Add(right);
        }

        foreach (var functionName in manifest.Functions.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!metadata.FunctionsByOrder.Any(existing => string.Equals(existing, functionName, StringComparison.OrdinalIgnoreCase)))
                metadata.FunctionsByOrder.Add(functionName);
        }
    }

    private static XDocument LoadXmlDocument(string path)
    {
        var options = LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo;
        try
        {
            return XDocument.Load(path, options);
        }
        catch (XmlException)
        {
            return LoadSanitizedXmlDocument(path, options);
        }
    }

    private static XDocument LoadSanitizedXmlDocument(string path, LoadOptions options)
    {
        var tempPath = CreateSanitizedXmlTempFile(path);
        try
        {
            using var fs = File.OpenRead(tempPath);
            using var xmlReader = XmlReader.Create(fs, new XmlReaderSettings
            {
                CheckCharacters = false,
                DtdProcessing = DtdProcessing.Parse
            });
            return XDocument.Load(xmlReader, options);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }

    internal static string CreateSanitizedXmlTempFile(string sourcePath)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"xpa_sanitized_{Guid.NewGuid():N}.xml");
        SanitizeXmlFile(sourcePath, tempPath);
        return tempPath;
    }

    private static void SanitizeXmlFile(string sourcePath, string destinationPath)
    {
        const int chunkSize = 64 * 1024;
        const int tailSize = 64;

        using var reader = new StreamReader(sourcePath, detectEncodingFromByteOrderMarks: true);
        using var writer = new StreamWriter(destinationPath, false, reader.CurrentEncoding);

        var buffer = new char[chunkSize];
        var carry = "";

        while (true)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                break;

            var chunk = carry + new string(buffer, 0, read);
            if (chunk.Length <= tailSize)
            {
                carry = chunk;
                continue;
            }

            var flushLength = chunk.Length - tailSize;
            var flushPart = chunk[..flushLength];
            carry = chunk[flushLength..];
            writer.Write(SanitizeXmlChunk(flushPart));
        }

        if (!string.IsNullOrEmpty(carry))
            writer.Write(SanitizeXmlChunk(carry));
    }

    internal static string SanitizeXmlFragment(string raw) => SanitizeXmlChunk(raw);

    private static string SanitizeXmlChunk(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;

        raw = Regex.Replace(raw, @"&#x(?<hex>[0-9A-Fa-f]+);|&#(?<dec>\d+);", m =>
        {
            var codePoint =
                m.Groups["hex"].Success ? Convert.ToInt32(m.Groups["hex"].Value, 16) :
                m.Groups["dec"].Success ? Convert.ToInt32(m.Groups["dec"].Value, 10) :
                -1;
            return IsValidXmlCodePoint(codePoint) ? m.Value : "";
        });

        var sb = new StringBuilder(raw.Length);
        foreach (var rune in raw.EnumerateRunes())
        {
            if (IsValidXmlCodePoint(rune.Value))
                sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    private static bool IsValidXmlCodePoint(int codePoint)
    {
        return codePoint == 0x9 ||
               codePoint == 0xA ||
               codePoint == 0xD ||
               (codePoint >= 0x20 && codePoint <= 0xD7FF) ||
               (codePoint >= 0xE000 && codePoint <= 0xFFFD) ||
               (codePoint >= 0x10000 && codePoint <= 0x10FFFF);
    }

    private sealed class TableXmlCandidate
    {
        public required string Path { get; init; }
        public required XDocument Document { get; init; }
        public required HashSet<string> PublicNames { get; init; }
        public bool Claimed { get; set; }
    }

    private static Dictionary<string, string> BuildSuppliedXmlLookup(IReadOnlyList<string>? xmlPaths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in xmlPaths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;
            var key = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(key) && !result.ContainsKey(key))
                result[key] = path;
        }
        return result;
    }

    private static List<TableXmlCandidate> BuildTableXmlCandidates(IReadOnlyList<string>? xmlPaths)
    {
        var result = new List<TableXmlCandidate>();
        foreach (var path in xmlPaths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;
            var doc = LoadXmlDocument(path);
            var publicNames = doc.Descendants("DataSourceRepository")
                .Descendants("DataObject")
                .Select(d => d.Attribute("Public")?.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            result.Add(new TableXmlCandidate
            {
                Path = path,
                Document = doc,
                PublicNames = publicNames
            });
        }
        return result;
    }

    private static string? ResolveSuppliedComponentXmlPath(IReadOnlyDictionary<string, string> lookup, ComponentMetadata metadata)
    {
        foreach (var key in new[]
                 {
                     metadata.Name,
                     Path.GetFileNameWithoutExtension(metadata.XmlPath ?? ""),
                     metadata.Name + ".xml",
                     metadata.Name.Replace(".xml", "", StringComparison.OrdinalIgnoreCase)
                 }.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (lookup.TryGetValue(Path.GetFileNameWithoutExtension(key!), out var path))
                return path;
        }
        return null;
    }

    private static TableXmlCandidate? ResolveSupportingTablesXml(IReadOnlyList<TableXmlCandidate> candidates, ComponentMetadata metadata)
    {
        TableXmlCandidate? best = null;
        var bestScore = 0;
        foreach (var candidate in candidates.Where(c => !c.Claimed))
        {
            var score = metadata.DataObjectsByOrder.Count(name => candidate.PublicNames.Contains(name));
            if (score <= bestScore)
                continue;
            best = candidate;
            bestScore = score;
        }

        if (bestScore == 0 || best is null)
            return null;

        best.Claimed = true;
        return best;
    }

    private static ComponentMetadata ParseComponentMetadata(XElement component)
    {
        var metadata = new ComponentMetadata
        {
            Name = XmlHelpers.Attr(component, "name"),
            Description = XmlHelpers.Attr(component.Element("Description") ?? new XElement("x"), "val"),
            Folder = XmlHelpers.Attr(component, "Folder"),
            ComponentType = XmlHelpers.Attr(component.Element("ComponentType") ?? new XElement("x"), "val"),
            AssemblyName = XmlHelpers.Attr(component.Element("AssemblyName") ?? new XElement("x"), "val"),
            AssemblyPath = XmlHelpers.Attr(component.Element("AssemblyPath") ?? new XElement("x"), "val"),
            UseSpecificVersion = XmlHelpers.Attr(component.Element("UseSpecificVersion") ?? new XElement("x"), "val")
        };

        ReadComponentObjectList(component.Element("ComponentModels"), metadata.ModelById, metadata.ModelsByOrder);
        ReadComponentObjectList(component.Element("ComponentDataObjects"), metadata.DataObjectById, metadata.DataObjectsByOrder);
        ReadComponentObjectList(component.Element("ComponentPrograms"), metadata.ProgramById, metadata.ProgramsByOrder);
        ReadComponentObjectList(component.Element("ComponentFuncs"), metadata.FunctionById, metadata.FunctionsByOrder);
        ReadComponentObjectList(component.Element("ComponentRights"), metadata.RightById, metadata.RightsByOrder);
        return metadata;
    }

    private static void ReadComponentObjectList(XElement? parent, Dictionary<int, string> byId, List<string> byOrder)
    {
        if (parent is null)
            return;
        var objects = parent.Elements("Object").ToList();
        for (var i = 0; i < objects.Count; i++)
        {
            var obj = objects[i];
            var publicName = XmlHelpers.Attr(obj.Element("PublicName") ?? new XElement("x"), "val");
            if (string.IsNullOrWhiteSpace(publicName))
                continue;
            if (int.TryParse(obj.Element("id")?.Attribute("val")?.Value, out var id))
                byId[id] = publicName;
            byOrder.Add(publicName);
        }
    }

    private static string ResolveComponentXmlPath(string mainDir, XElement component, string componentName)
    {
        var projectFile = component.Element("ProjectFile")?.Attribute("val")?.Value;
        if (!string.IsNullOrWhiteSpace(projectFile))
        {
            var candidate = projectFile.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                ? projectFile
                : projectFile + ".xml";
            var full = Path.Combine(mainDir, candidate);
            if (File.Exists(full))
                return full;
        }

        var nameCandidate = Path.Combine(mainDir, componentName + ".xml");
        if (File.Exists(nameCandidate))
            return nameCandidate;

        return Path.Combine(mainDir, (projectFile ?? componentName) + ".xml");
    }

    private static void ParseFieldModels(ParseContext ctx, ParsedXpa parsed, ref int modelOrdinal)
    {
        var models = ctx.Document.Descendants("ModelsRepository")
            .Descendants("Object");

        var ordered = models.ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var obj = ordered[i];
            TouchKnownXmlAttributes(obj);
            var propertyList = obj.Element("PropertyList");
            if (propertyList == null || XmlHelpers.Attr(propertyList, "model") != "FIELD")
                continue;
            TouchKnownXmlAttributes(propertyList);

            var model = propertyList.Element("Model");
            var picture = propertyList.Element("Picture");
            var selectProgram = propertyList.Element("SelectProgram");
            var dec = propertyList.Element("_Dec")?.Attribute("val")?.Value;
            var whole = propertyList.Element("_Whole")?.Attribute("val")?.Value;
            var negative = ParseBool(propertyList.Element("_Negative")?.Attribute("val")?.Value);
            var flip = ParseBool(propertyList.Element("_Flip")?.Attribute("val")?.Value);
            var fieldStyle = ParseInt(propertyList.Element("_FieldStyle")?.Attribute("val")?.Value);
            var comment = XmlHelpers.NormalizeText(
                propertyList.Element("_Comment")?.Attribute("valUnicode")?.Value
                ?? propertyList.Element("_Comment")?.Attribute("val")?.Value);
            var ditIndexForToolkit = ParseInt(propertyList.Element("_DitIndexForToolkit")?.Attribute("val")?.Value);
            if (model == null)
                continue;
            modelOrdinal++;
            ctx.ModelLocalToGlobal[i + 1] = modelOrdinal;

            var publicName = obj.Attribute("Public")?.Value;
            if (!string.IsNullOrWhiteSpace(publicName))
                ctx.ModelPublicToGlobal[publicName] = modelOrdinal;

            parsed.FieldModels.Add(new FieldModelDef(
                Ordinal: modelOrdinal,
                Name: XmlHelpers.Attr(obj, "name"),
                AttrObj: XmlHelpers.Attr(model, "attr_obj"),
                Picture: XmlHelpers.Attr(picture ?? new XElement("x"), "valUnicode"),
                Dec: dec,
                Whole: whole,
                Negative: negative,
                Flip: flip,
                FieldStyle: fieldStyle,
                Comment: comment,
                DitIndexForToolkit: ditIndexForToolkit,
                SelectProgramObj: selectProgram?.Attribute("obj")?.Value,
                PublicName: publicName,
                SourceComponent: ctx.IsMain ? null : ctx.Name
            ));
        }
    }

    private static void ParseControlButtonModels(XDocument doc, ParsedXpa parsed, ref int controlButtonObj)
    {
        var models = doc.Descendants("ModelsRepository")
            .Descendants("Object")
            .ToList();

        for (var i = 0; i < models.Count; i++)
        {
            var obj = models[i];
            var propertyList = obj.Element("PropertyList");
            if (propertyList == null || XmlHelpers.Attr(propertyList, "model") != "CTRL_GUI0_PUSH_BUTTON")
                continue;

            var raise = propertyList.Element("RaiseEvent");
            var eventType = XmlHelpers.Attr(raise?.Element("EventType") ?? new XElement("x"), "val");
            var internalId = ParseInt(raise?.Element("InternalEventID")?.Attribute("val")?.Value);
            controlButtonObj++;
            parsed.ControlButtonModels.Add(new ControlButtonModelDef(
                Obj: controlButtonObj,
                Name: XmlHelpers.Attr(obj, "name"),
                Format: XmlHelpers.NormalizeText(propertyList.Element("Format")?.Attribute("valUnicode")?.Value),
                InternalEventId: string.Equals(eventType, "I", StringComparison.OrdinalIgnoreCase) ? internalId : null,
                FontSchemeId: ParseInt(propertyList.Element("Font")?.Attribute("val")?.Value)
            ));
        }
    }

    private static void ParseDataObjects(ParseContext ctx, ParsedXpa parsed, ref int dataObjectOrdinal)
    {
        var dataObjects = ctx.Document.Descendants("DataSourceRepository")
            .Descendants("DataObject")
            .ToList();

        for (var i = 0; i < dataObjects.Count; i++)
        {
            var d = dataObjects[i];
            TouchKnownXmlAttributes(d);
            var columns = ParseColumns(ctx, d);
            var indexes = ParseIndexes(d);
            dataObjectOrdinal++;
            ctx.DataObjectLocalToGlobal[i + 1] = dataObjectOrdinal;
            var resident = ParseBool(d.Attribute("Resident")?.Value);
            var comment = d.Attribute("Comment")?.Value;

            var publicName = d.Attribute("Public")?.Value;
            if (!string.IsNullOrWhiteSpace(publicName))
                ctx.DataObjectPublicToGlobal[publicName] = dataObjectOrdinal;

            parsed.DataObjects.Add(new DataObjectDef(
                Ordinal: dataObjectOrdinal,
                Name: XmlHelpers.Attr(d, "name"),
                PhysicalName: XmlHelpers.Attr(d, "PhysicalName"),
                Comment: comment,
                Resident: resident,
                PublicName: publicName,
                SourceComponent: ctx.IsMain || ctx.IsTablesOnlySupport ? null : ctx.Name,
                Owner: d.Element("Owner")?.Attribute("val")?.Value,
                DataSource: XmlHelpers.Attr(d, "data_source"),
                Columns: columns,
                Indexes: indexes
            ));
        }
    }

    private static void InjectExternalManifestDataObjects(
        IReadOnlyList<ParseContext> contexts,
        ParsedXpa parsed,
        IReadOnlyDictionary<string, string>? projectReferenceMap,
        ref int dataObjectOrdinal)
    {
        if (projectReferenceMap is null || projectReferenceMap.Count == 0)
            return;

        foreach (var ctx in contexts.Where(c => !c.IsMain && c.Metadata is not null))
        {
            if (string.IsNullOrWhiteSpace(ctx.Name))
                continue;
            if (!projectReferenceMap.TryGetValue(ctx.Name, out var projectPath))
                continue;

            var manifest = ProjectManifest.LoadForSource(projectPath);
            if (manifest is null)
                continue;

            foreach (var item in manifest.DataObjectDetails.OrderBy(d => d.ObjectIndex))
            {
                dataObjectOrdinal++;
                ctx.DataObjectLocalToGlobal[item.ObjectIndex] = dataObjectOrdinal;
                AddExternalDataObjectLookup(ctx, item.PublicName, dataObjectOrdinal);
                AddExternalDataObjectLookup(ctx, item.Name, dataObjectOrdinal);
                manifest.DataObjectsByIndex.TryGetValue(item.ObjectIndex, out var generatedTypeName);
                if (!string.IsNullOrWhiteSpace(generatedTypeName))
                    AddExternalDataObjectLookup(ctx, generatedTypeName, dataObjectOrdinal);

                parsed.DataObjects.Add(new DataObjectDef(
                    Ordinal: dataObjectOrdinal,
                    Name: item.Name,
                    PhysicalName: item.PhysicalName,
                    Comment: item.Comment,
                    Resident: item.Resident,
                    PublicName: item.PublicName,
                    SourceComponent: ctx.Name,
                    Owner: item.Owner,
                    DataSource: item.DataSource,
                    Columns: item.Columns.Select(c => new DataColumnDef(
                        Id: c.Id,
                        Name: c.Name,
                        AttrObj: c.AttrObj,
                        Picture: c.Picture,
                        FieldPhysicalName: c.FieldPhysicalName,
                        FieldPhysicalPicture: c.FieldPhysicalPicture,
                        FieldPhysicalSize: c.FieldPhysicalSize,
                        AllowedNull: c.AllowedNull,
                        Attribute: c.Attribute,
                        ContextCookies: c.ContextCookies,
                        DatabaseDefinition: c.DatabaseDefinition,
                        Storage: c.Storage,
                        Translate: c.Translate,
                        DbColumnName: c.DbColumnName,
                        DbType: c.DbType,
                        ModelRefObj: c.ModelRefObj
                    )).ToList(),
                    Indexes: item.Indexes.Select(i => new DataIndexDef(
                        Id: i.Id,
                        Name: i.Name,
                        Unique: i.Unique,
                        Primary: i.Primary,
                        Segments: i.Segments.Select(s => new IndexSegmentDef(s.ColumnId, s.Order)).ToList()
                    )).ToList(),
                    TypeNameOverride: generatedTypeName
                ));
            }
        }
    }

    private static void AddExternalDataObjectLookup(ParseContext ctx, string? key, int dataObjectOrdinal)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        ctx.DataObjectPublicToGlobal[key] = dataObjectOrdinal;
    }

    private static IReadOnlyList<DataColumnDef> ParseColumns(ParseContext ctx, XElement dataObject)
    {
        var result = new List<DataColumnDef>();
        var columns = dataObject.Element("Columns")?.Elements("Column") ?? Enumerable.Empty<XElement>();
        foreach (var c in columns)
        {
            TouchKnownXmlAttributes(c);
            if (!int.TryParse(XmlHelpers.Attr(c, "id", "0"), out var id))
                continue;
            var plist = c.Element("PropertyList");
            if (plist == null)
                continue;
            TouchKnownXmlAttributes(plist);
            var model = plist.Element("Model");
            var picture = plist.Element("Picture");
            var dbColName = plist.Element("DbColumnName");
            var dbType = plist.Element("Type");
            var fieldPhysical = plist.Element("_FieldPhysical");
            TouchKnownXmlAttributes(fieldPhysical);

            result.Add(new DataColumnDef(
                Id: id,
                Name: XmlHelpers.Attr(c, "name"),
                AttrObj: XmlHelpers.Attr(model ?? new XElement("x"), "attr_obj"),
                Picture: XmlHelpers.Attr(picture ?? new XElement("x"), "valUnicode"),
                FieldPhysicalName: fieldPhysical?.Attribute("Name")?.Value,
                FieldPhysicalPicture: fieldPhysical?.Attribute("PIC_U")?.Value,
                FieldPhysicalSize: ParseInt(fieldPhysical?.Attribute("Size")?.Value),
                AllowedNull: ParseBool(fieldPhysical?.Attribute("allowed_null")?.Value),
                Attribute: fieldPhysical?.Attribute("attribute")?.Value,
                ContextCookies: fieldPhysical?.Attribute("context_cookies")?.Value,
                DatabaseDefinition: fieldPhysical?.Attribute("database_definition")?.Value,
                Storage: fieldPhysical?.Attribute("storage")?.Value,
                Translate: fieldPhysical?.Attribute("translate")?.Value,
                DbColumnName: dbColName?.Attribute("val")?.Value,
                DbType: dbType?.Attribute("val")?.Value,
                ModelRefObj: ResolveModelRef(ctx, null, model)
            ));
        }

        return result;
    }

    private static IReadOnlyList<DataIndexDef> ParseIndexes(XElement dataObject)
    {
        var result = new List<DataIndexDef>();
        var indexes = dataObject.Element("Indexes")?.Elements("Index") ?? Enumerable.Empty<XElement>();
        foreach (var idx in indexes)
        {
            if (!int.TryParse(XmlHelpers.Attr(idx, "id", "0"), out var id))
                continue;

            var unique = string.Equals(XmlHelpers.Attr(idx.Element("Mode") ?? new XElement("x"), "val"), "S", StringComparison.OrdinalIgnoreCase);
            var primary = string.Equals(XmlHelpers.Attr(idx.Element("Primary") ?? new XElement("x"), "val"), "Y", StringComparison.OrdinalIgnoreCase);
            var segments = new List<IndexSegmentDef>();
            foreach (var seg in idx.Descendants("Segment"))
            {
                var colStr = XmlHelpers.Attr(seg.Element("Column") ?? new XElement("x"), "val");
                if (!int.TryParse(colStr, out var colId))
                    continue;
                segments.Add(new IndexSegmentDef(
                    ColumnId: colId,
                    Order: XmlHelpers.Attr(seg.Element("Order") ?? new XElement("x"), "val", "A")
                ));
            }

            result.Add(new DataIndexDef(
                Id: id,
                Name: XmlHelpers.Attr(idx, "name"),
                Unique: unique,
                Primary: primary,
                Segments: segments
            ));
        }
        return result;
    }

    private static void ParseTasks(ParseContext ctx, IReadOnlyList<ParseContext> contexts, ParsedXpa parsed, ref int ordinal, ref int topLevelProgramIndex)
    {
        var repo = ctx.Document.Descendants("ProgramsRepository").FirstOrDefault();
        if (repo is null)
            return;

        var rootTasks = repo.Element("Programs")?.Elements("Task").ToList() ?? new List<XElement>();
        for (var i = 0; i < rootTasks.Count; i++)
        {
            var originalTopLevelIndex = ParseInt(rootTasks[i].Attribute("_xpa_converter_original_top_level_index")?.Value);
            var localTopLevelIndex = originalTopLevelIndex ?? (i + 1);
            var globalTopLevelIndex = originalTopLevelIndex ?? (topLevelProgramIndex + 1);
            topLevelProgramIndex = Math.Max(topLevelProgramIndex + (originalTopLevelIndex.HasValue ? 0 : 1), globalTopLevelIndex);
            ctx.ProgramLocalTopLevelToGlobal[localTopLevelIndex] = globalTopLevelIndex;
            ParseTaskNode(ctx, contexts, rootTasks[i], parsed, ref ordinal, globalTopLevelIndex, localTopLevelIndex, null, null);
        }
    }

    private static void ParseTaskNode(
        ParseContext ctx,
        IReadOnlyList<ParseContext> contexts,
        XElement t,
        ParsedXpa parsed,
        ref int ordinal,
        int? topLevelProgramIndex,
        int? topLevelProgramIndexLocal,
        int? parentOrdinal,
        int? subtaskIndex)
    {
        TouchKnownXmlAttributes(t);
        var header = t.Element("Header");
        if (header == null)
            return;
        TouchKnownXmlAttributes(header);
        ordinal++;

        var currentOrdinal = ordinal;
        var resourceDataObjects = ParseTaskResourceDataObjects(ctx, contexts, t);
        var resourceDbs = ParseTaskResourceDbs(ctx, contexts, t);
        var resourceColumns = ParseTaskResourceColumns(ctx, contexts, t);
        var infoDbObj = ParseInformationDbObj(ctx, contexts, t);
        var initialKeyIndexId = ParseInt(t.Element("Information")?.Element("Key")?.Element("Column")?.Attribute("val")?.Value);
        var initialKeyExpressionId = ParseInt(t.Element("Information")?.Element("Key")?.Element("Exp")?.Attribute("val")?.Value);
        var sortSegments = ParseTaskSortSegments(t);
        var events = ParseTaskEvents(t);
        var expressions = ParseTaskExpressions(t).ToList();
        var initialMode = XmlHelpers.Attr(t.Element("Information")?.Element("InitialMode") ?? new XElement("x"), "val");
        var locateDirection = XmlHelpers.Attr(t.Element("Information")?.Element("Locate") ?? new XElement("x"), "Direction");
        var rangeDirection = XmlHelpers.Attr(t.Element("Information")?.Element("Range") ?? new XElement("x"), "Direction");
        var locateExpressionId = ParseInt(t.Element("Information")?.Element("Locate")?.Attribute("Exp")?.Value);
        var rangeExpressionId = ParseInt(t.Element("Information")?.Element("Range")?.Attribute("Exp")?.Value);
        var initialModeExpId = ParseInt(t.Element("Information")?.Element("InitialMode")?.Attribute("Exp")?.Value);
        var endTaskConditionNode = t.Element("Information")?.Element("EndTaskCondition");
        var endTaskConditionExpId = ParseInt(endTaskConditionNode?.Attribute("Exp")?.Value);
        var endTaskCondition = XmlHelpers.Attr(endTaskConditionNode ?? new XElement("x"), "val") == "Y" || endTaskConditionExpId.HasValue;
        var evaluateEndCondition = XmlHelpers.Attr(t.Element("Information")?.Element("EvaluateEndCondition") ?? new XElement("x"), "val");
        var taskProps = t.Element("Information")?.Element("TaskProperties");
        var lockingStrategy = XmlHelpers.Attr(taskProps?.Element("LockingStrategy") ?? new XElement("x"), "val");
        var cacheStrategy = XmlHelpers.Attr(taskProps?.Element("CacheStrategy") ?? new XElement("x"), "val");
        var forceRecordSuffix = ParseBool(taskProps?.Element("ForceRecordSuffix")?.Attribute("val")?.Value) ?? false;
        var transactionMode = XmlHelpers.Attr(taskProps?.Element("TransactionMode") ?? new XElement("x"), "val");
        var transactionBegin = XmlHelpers.Attr(taskProps?.Element("TransactionBegin") ?? new XElement("x"), "val");
        var errorStrategy = XmlHelpers.Attr(taskProps?.Element("ErrorStrategy") ?? new XElement("x"), "val");
        var selectionTable = XmlHelpers.Attr(taskProps?.Element("SelectionTable") ?? new XElement("x"), "val") == "Y";
        var allowEmptyDataview = ParseBool(taskProps?.Element("AllowEmptyDataview")?.Attribute("val")?.Value);
        var preloadView = ParseBool(taskProps?.Element("PreloadView")?.Attribute("val")?.Value);
        var openTaskWindow = XmlHelpers.Attr(t.Element("Information")?.Element("WIN")?.Element("OpenTaskWindow") ?? new XElement("x"), "val") != "N";
        var closeTaskWindow = XmlHelpers.Attr(t.Element("Information")?.Element("WIN")?.Element("CloseTaskWindow") ?? new XElement("x"), "val") != "N";
        var sideWin = t.Element("Information")?.Element("SIDE_WIN");
        var allowEvents = XmlHelpers.Attr(sideWin?.Element("AllowEvents") ?? new XElement("x"), "val") == "Y";
        var allowOptions = ParseBool(sideWin?.Element("AllowOptions")?.Attribute("val")?.Value);
        var allowQuery = ParseBool(sideWin?.Element("AllowQuery")?.Attribute("val")?.Value);
        var allowModify = ParseBool(sideWin?.Element("AllowModify")?.Attribute("val")?.Value);
        var allowCreate = ParseBool(sideWin?.Element("AllowCreate")?.Attribute("val")?.Value);
        var allowDelete = ParseBool(sideWin?.Element("AllowDelete")?.Attribute("val")?.Value);
        var allowPrintingData = XmlHelpers.Attr(sideWin?.Element("AllowPrintingData") ?? new XElement("x"), "val") == "Y";
        var allowCreateExp = sideWin?.Element("AllowCreate")?.Attribute("Exp")?.Value;
        var cnfu = t.Attribute("CNFU")?.Value;
        var clss = t.Attribute("CLSS")?.Value;
        var mgAttr = t.Attribute("MgAttr")?.Value;
        var pdod = t.Attribute("PDOD")?.Value;
        var propagate = t.Attribute("propagate")?.Value;
        var totalVariabls = ParseInt(t.Element("Information")?.Element("_TotalVariabls")?.Attribute("val")?.Value);
        var totalVirtuals = ParseInt(t.Element("Information")?.Element("_TotalVirtuals")?.Attribute("val")?.Value);
        var ios = ParseTaskIos(t);
        var io = ios.FirstOrDefault();
        var formEntries = ParseTaskForms(ctx, contexts, t);
        var displayExpressionId = ParseInt(t.Element("DISPLAY")?.Attribute("val")?.Value);
        var primaryFormEntry = SelectPrimaryFormEntry(formEntries);
        var formName = primaryFormEntry?.Form.FormName;
        var form = primaryFormEntry?.Form;
        var sqlWhere = ParseTaskSqlWhere(t.Element("SQL_WHERE_U"));
        var varRangeInfos = ParseTaskVarRangeInfos(t);
        var sqlForm = ParseTaskSqlForm(t);
        var logic = ParseTaskLogic(ctx, contexts, t, resourceDataObjects, topLevelProgramIndexLocal, expressions);

        var taskPublicName = header.Element("Public")?.Attribute("val")?.Value;
        var declaredParameterCount = ParseInt(header.Element("ReturnValue")?.Element("TSK_PARAMS")?.Attribute("val")?.Value);
        var taskReturnValue = ParseReturnValueDef(header.Element("ReturnValue"));
        var returnValueExpressionId = ParseInt(t.Element("ReturnValueExpression")?.Attribute("val")?.Value);
        var parallelNode = header.Element("PARALLEL_EXECUTION");
        var parallelExecution = parallelNode is not null;
        var copyGlobalParameters = XmlHelpers.Attr(parallelNode?.Element("COPY_GLOBAL_PARAMS") ?? new XElement("x"), "val") == "Y";
        var singleInstance = XmlHelpers.Attr(parallelNode?.Element("SingleInstance") ?? new XElement("x"), "val") == "Y";
        var isEmptyTask = string.Equals(header.Attribute("ISEMPTY_TSK")?.Value, "1", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(header.Attribute("ISEMPTY_TSK")?.Value, "Y", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(taskPublicName) && topLevelProgramIndex.HasValue)
            ctx.ProgramPublicToGlobalTopLevel[taskPublicName] = topLevelProgramIndex.Value;

        parsed.Tasks.Add(new TaskDef(
            Ordinal: currentOrdinal,
            TopLevelProgramIndex: topLevelProgramIndex,
            TopLevelProgramIndexLocal: topLevelProgramIndexLocal,
            ParentOrdinal: parentOrdinal,
            SubtaskIndex: subtaskIndex,
            Description: XmlHelpers.Attr(header, "Description", $"Task_{currentOrdinal}"),
            Folder: header.Attribute("Folder")?.Value,
            IsEmptyTask: isEmptyTask,
            PublicName: taskPublicName,
            Resident: XmlHelpers.Attr(header.Element("Resident") ?? new XElement("x"), "val") == "Y",
            TaskType: XmlHelpers.Attr(header.Element("TaskType") ?? new XElement("x"), "val"),
            DeclaredParameterCount: declaredParameterCount,
            ReturnValue: taskReturnValue,
            ReturnValueExpressionId: returnValueExpressionId,
            ParallelExecution: parallelExecution,
            CopyGlobalParameters: copyGlobalParameters,
            SingleInstance: singleInstance,
            TaskId: header.Element("TaskID")?.Attribute("val")?.Value,
            Icon: t.Attribute("Icon")?.Value,
            MainProgram: XmlHelpers.Attr(t, "MainProgram") == "Y",
            Cnfu: cnfu,
            Clss: clss,
            MgAttr: mgAttr,
            Pdod: pdod,
            Propagate: propagate,
            InitialMode: initialMode,
            LocateDirection: locateDirection,
            RangeDirection: rangeDirection,
            LocateExpressionId: locateExpressionId,
            RangeExpressionId: rangeExpressionId,
            InitialModeExpressionId: initialModeExpId,
            TotalVariabls: totalVariabls,
            TotalVirtuals: totalVirtuals,
            EndTaskCondition: endTaskCondition,
            EndTaskConditionExpressionId: endTaskConditionExpId,
            EvaluateEndCondition: evaluateEndCondition,
            LockingStrategy: lockingStrategy,
            TransactionMode: transactionMode,
            TransactionBegin: transactionBegin,
            ErrorStrategy: errorStrategy,
            SelectionTable: selectionTable,
            CacheStrategy: cacheStrategy,
            ForceRecordSuffix: forceRecordSuffix,
            AllowEmptyDataview: allowEmptyDataview,
            PreloadView: preloadView,
            AllowActivitySwitch: allowOptions,
            AllowQuery: allowQuery,
            AllowModify: allowModify,
            AllowCreate: allowCreate,
            AllowDelete: allowDelete,
            AllowEvents: allowEvents,
            OpenTaskWindow: openTaskWindow,
            CloseTaskWindow: closeTaskWindow,
            AllowPrintingData: allowPrintingData,
            AllowCreateExpression: allowCreateExp,
            SqlWhere: sqlWhere,
            VarRangeInfos: varRangeInfos,
            SqlForm: sqlForm,
            FormName: formName,
            Form: form,
            ResourceDataObjects: resourceDataObjects,
            ResourceDbs: resourceDbs,
            ResourceColumns: resourceColumns,
            InformationDbObj: infoDbObj,
            InitialKeyIndexId: initialKeyIndexId,
            InitialKeyExpressionId: initialKeyExpressionId,
            PrimaryDbObj: logic.PrimaryDbObj,
            SortSegments: sortSegments,
            Selects: logic.Selects,
            Links: logic.Links,
            DataViewSources: logic.DataViewSources,
            Blocks: logic.Blocks,
            EndBlocks: logic.EndBlocks,
            EndLinks: logic.EndLinks,
            TabCalls: logic.TabCalls,
            StartLogics: logic.StartLogics,
            StartRaises: logic.StartRaises,
            EndLogics: logic.EndLogics,
            EndRaises: logic.EndRaises,
            HasStartLogicUnit: logic.HasStartLogicUnit,
            HasEndLogicUnit: logic.HasEndLogicUnit,
            RowLogics: logic.RowLogics,
            SavingRowLogics: logic.SavingRowLogics,
            FlowValidations: logic.FlowValidations,
            Events: events,
            Expressions: expressions,
            FunctionOverrides: logic.FunctionOverrides,
            Handlers: logic.Handlers,
            GroupLogics: logic.GroupLogics,
            Gaps: logic.Gaps,
            DisplayExpressionId: displayExpressionId,
            FormEntries: formEntries,
            FormIos: logic.FormIos,
            Io: io,
            Ios: ios,
            SourceComponent: ctx.IsMain ? null : ctx.Name
        ));

        var children = t.Elements("Task").ToList();
        for (var i = 0; i < children.Count; i++)
            ParseTaskNode(ctx, contexts, children[i], parsed, ref ordinal, topLevelProgramIndex, topLevelProgramIndexLocal, currentOrdinal, i + 1);
    }

    private static IReadOnlyList<int> ParseTaskResourceDataObjects(ParseContext ctx, IReadOnlyList<ParseContext> contexts, XElement task)
    {
        var result = new List<int>();
        var dbNodes = task.Element("Resource")?.Elements("DB") ?? Enumerable.Empty<XElement>();
        foreach (var db in dbNodes)
        {
            var obj = ResolveDataObjectRef(ctx, contexts, db.Element("DataObject"));
            if (obj.HasValue)
                result.Add(obj.Value);
        }
        return result;
    }

    private static TaskFormEntryDef? SelectPrimaryFormEntry(IReadOnlyList<TaskFormEntryDef> formEntries)
    {
        if (formEntries.Count == 0)
            return null;

        var guiForms = formEntries.Where(x => x.Model == "FORM_GUI0").ToList();
        if (guiForms.Count == 0)
            return formEntries[0];

        int Score(TaskFormEntryDef f)
        {
            var controls = f.Form.Controls;
            var totalControls = controls.Count;
            var dataBound = controls.Count(c => c.DataExpressionId.HasValue || !string.IsNullOrWhiteSpace(c.DataColumn));
            var interactive = controls.Count(c =>
                c.Model is "CTRL_GUI0_EDIT" or "CTRL_GUI0_COMBOBOX" or "CTRL_GUI0_PUSH_BUTTON" or "CTRL_GUI0_CHECKBOX" or "CTRL_GUI0_SUBFORM");
            var staticOnly = totalControls > 0 && controls.All(c =>
                c.Model is "CTRL_GUI0_STATIC" or "CTRL_GUI0_LINE" or "CTRL_GUI0_IMAGE" or "CTRL_GUI1_IMAGE");
            var namePenalty = (!string.IsNullOrWhiteSpace(f.Form.FormName) &&
                               f.Form.FormName.IndexOf("title", StringComparison.OrdinalIgnoreCase) >= 0) ? 40 : 0;

            var score = 0;
            score += dataBound * 30;
            score += interactive * 12;
            score += totalControls;
            if (staticOnly)
                score -= 60;
            score -= namePenalty;
            return score;
        }

        return guiForms
            .OrderByDescending(Score)
            .ThenByDescending(x => x.Form.Controls.Count)
            .ThenBy(x => x.Index)
            .FirstOrDefault();
    }

    private static IReadOnlyList<TaskResourceDbDef> ParseTaskResourceDbs(ParseContext ctx, IReadOnlyList<ParseContext> contexts, XElement task)
    {
        var result = new List<TaskResourceDbDef>();
        var dbNodes = task.Element("Resource")?.Elements("DB") ?? Enumerable.Empty<XElement>();
        foreach (var db in dbNodes)
        {
            var obj = ResolveDataObjectRef(ctx, contexts, db.Element("DataObject"));
            if (!obj.HasValue)
                continue;
            result.Add(new TaskResourceDbDef(
                DataObject: obj.Value,
                Access: db.Element("Access")?.Attribute("val")?.Value,
                Cache: ParseBool(db.Element("Cache")?.Attribute("val")?.Value),
                EntityNameExpressionRef: db.Element("Exp")?.Attribute("val")?.Value
            ));
        }
        return result;
    }

    private static IReadOnlyList<TaskResourceColumnDef> ParseTaskResourceColumns(ParseContext ctx, IReadOnlyList<ParseContext> contexts, XElement task)
    {
        var result = new List<TaskResourceColumnDef>();
        var columns = task.Element("Resource")?.Element("Columns")?.Elements("Column") ?? Enumerable.Empty<XElement>();
        foreach (var c in columns)
        {
            if (!int.TryParse(XmlHelpers.Attr(c, "id", "0"), out var id))
                continue;
            var plist = c.Element("PropertyList");
            var model = plist?.Element("Model");
            var picture = plist?.Element("Picture");
            var nullAllowedEl = plist?.Element("NullAllowed");
            bool? allowNull = null;
            if (nullAllowedEl is not null)
            {
                var nullAllowedVal = nullAllowedEl.Attribute("val")?.Value;
                allowNull = string.Equals(nullAllowedVal, "Y", StringComparison.OrdinalIgnoreCase);
            }
            var nullDisplayText = XmlHelpers.NormalizeText(
                plist?.Element("NullDisplay")?.Attribute("valUnicode")?.Value
                ?? plist?.Element("NullDisplay")?.Attribute("val")?.Value);
            var defaultValue = plist?.Element("DefaultValue")?.Attribute("val")?.Value;
            var inputRange = XmlHelpers.NormalizeText(
                plist?.Element("Range")?.Attribute("valUnicode")?.Value
                ?? plist?.Element("Range")?.Attribute("val")?.Value);
            var definitionId = ParseInt(plist?.Element("Definition")?.Attribute("val")?.Value);
            var selectProgram = plist?.Element("SelectProgram");
            var cellModel = plist?.Element("CellModel");
            var cellModelAttrObj = XmlHelpers.Attr(cellModel ?? new XElement("x"), "attr_obj");
            var cellModelObj = ResolveModelRefInt(ctx, contexts, cellModel)
                               ?? ParseInt(cellModel?.Attribute("obj")?.Value);
            var clearsInheritedExpandEvent =
                model is not null &&
                !string.IsNullOrWhiteSpace(ResolveModelRef(ctx, contexts, model)) &&
                selectProgram is not null &&
                string.IsNullOrWhiteSpace(selectProgram.Attribute("obj")?.Value) &&
                string.IsNullOrWhiteSpace(selectProgram.Attribute("val")?.Value) &&
                string.IsNullOrWhiteSpace(selectProgram.Attribute("valUnicode")?.Value);
            var hasControlModelOverride = plist?.Element("GuiDisplay") is not null
                                          || plist?.Element("GuiDisplayTable") is not null
                                          || plist?.Element("GuiOutput") is not null
                                          || plist?.Element("GuiOutputTable") is not null
                                          || plist?.Element("Browser") is not null
                                          || plist?.Element("BrowserTable") is not null
                                          || plist?.Element("TextBased") is not null
                                          || plist?.Element("RichClient") is not null
                                          || plist?.Element("RichClientTable") is not null;
            result.Add(new TaskResourceColumnDef(
                Id: id,
                Name: XmlHelpers.Attr(c, "name"),
                AttrObj: XmlHelpers.Attr(model ?? new XElement("x"), "attr_obj"),
                ModelRefObj: ResolveModelRef(ctx, contexts, model),
                ObjectType: XmlHelpers.Attr(plist?.Element("ObjectType") ?? new XElement("x"), "val"),
                Picture: XmlHelpers.Attr(picture ?? new XElement("x"), "valUnicode"),
                InputRange: inputRange,
                AllowNull: allowNull,
                NullDisplayText: nullDisplayText,
                DefaultValue: defaultValue,
                HasControlModelOverride: hasControlModelOverride,
                ClearsInheritedExpandEvent: clearsInheritedExpandEvent,
                DefinitionId: definitionId,
                CellModelAttrObj: cellModelAttrObj,
                CellModelObj: cellModelObj
            ));
        }
        return result;
    }

    private static int? ParseInformationDbObj(ParseContext ctx, IReadOnlyList<ParseContext> contexts, XElement task)
    {
        return ResolveDataObjectRef(ctx, contexts, task.Element("Information")?.Element("DB"));
    }

    private static IReadOnlyList<TaskSortSegmentDef> ParseTaskSortSegments(XElement task)
    {
        var segments = task.Element("Information")?.Element("Sort")?.Elements("Segment") ?? Enumerable.Empty<XElement>();
        var result = new List<TaskSortSegmentDef>();
        foreach (var seg in segments)
        {
            var fieldId = ParseInt(seg.Element("Field")?.Attribute("val")?.Value);
            if (!fieldId.HasValue)
                continue;
            var direction = XmlHelpers.Attr(seg.Element("Direction") ?? new XElement("x"), "val", "A");
            result.Add(new TaskSortSegmentDef(fieldId.Value, direction));
        }
        return result;
    }

    private static IReadOnlyList<TaskIoDef> ParseTaskIos(XElement task)
    {
        var result = new List<TaskIoDef>();
        foreach (var io in task.Element("Resource")?.Elements("IO") ?? Enumerable.Empty<XElement>())
        {
            result.Add(new TaskIoDef(
                Description: io.Element("Description")?.Attribute("val")?.Value,
                Machine: io.Element("MACH")?.Attribute("val")?.Value,
                PrintPreview: ParseBool(io.Element("PrintPreview")?.Attribute("val")?.Value),
                OpenPrintDialog: ParseBool(io.Element("OpenPrintDialog")?.Attribute("val")?.Value),
                PrintingAllowed: ParseBool(io.Element("PrintingAllowed")?.Attribute("val")?.Value),
                PaperSize: io.Element("PaperSize")?.Attribute("val")?.Value,
                Copies: ParseInt(io.Element("Copies")?.Attribute("val")?.Value),
                Pdf: ParseBool(io.Element("PDF")?.Attribute("val")?.Value),
                ContentCopyingAllowed: ParseBool(io.Element("ContentCopyingAllowed")?.Attribute("val")?.Value),
                ChangesAllowed: ParseBool(io.Element("ChangesAllowed")?.Attribute("val")?.Value),
                PageLayoutAllowed: ParseBool(io.Element("PageLayoutAllowed")?.Attribute("val")?.Value),
                Vis2LogTranslation: io.Element("Vis2LogTranslation")?.Attribute("val")?.Value,
                FlipLines: ParseBool(io.Element("FlipLines")?.Attribute("val")?.Value),
                PageHeaderFormEntryIndex: ParseInt(io.Element("PageHeaderForm")?.Attribute("val")?.Value),
                PageFooterFormEntryIndex: ParseInt(io.Element("PageFooterForm")?.Attribute("val")?.Value),
                IoExpressionId: ParseInt(io.Element("IOExpression")?.Attribute("val")?.Value),
                IoToUseColumnId: ParseInt(io.Element("IOTOUSE")?.Attribute("val")?.Value),
                Media: io.Element("Media")?.Attribute("val")?.Value,
                Access: io.Element("Access")?.Attribute("val")?.Value,
                Bottom: ParseInt(io.Element("Bottom")?.Attribute("val")?.Value),
                Charset: io.Element("CHARSET")?.Attribute("val")?.Value,
                ColumnRef: io.Attribute("Column")?.Value
            ));
        }
        return result;
    }

    private static IReadOnlyList<TaskFormEntryDef> ParseTaskForms(ParseContext ctx, IReadOnlyList<ParseContext> contexts, XElement task)
    {
        var result = new List<TaskFormEntryDef>();
        var entries = task.Elements("TaskForms").SelectMany(tf => tf.Elements("FormEntry")).ToList();
        for (var i = 0; i < entries.Count; i++)
        {
            var formEntry = entries[i];
            var plist = formEntry.Element("PropertyList");
            var width = ParseInt(plist?.Element("Width")?.Attribute("val")?.Value) ?? 320;
            var height = ParseInt(plist?.Element("Height")?.Attribute("val")?.Value) ?? 200;
            var formX = ParseInt(plist?.Element("X")?.Attribute("val")?.Value) ?? 0;
            var formY = ParseInt(plist?.Element("Y")?.Attribute("val")?.Value) ?? 0;
            var formName = XmlHelpers.NormalizeText(plist?.Element("FormName")?.Attribute("valUnicode")?.Value);
            var formNameExpressionId = ParseInt(plist?.Element("FormName")?.Attribute("Exp")?.Value);
            var formText = XmlHelpers.NormalizeText(
                               plist?.Element("Text")?.Attribute("valUnicode")?.Value
                               ?? plist?.Element("Text")?.Attribute("val")?.Value)
                           ?? formName;
            var formTextExpressionId = ParseInt(plist?.Element("Text")?.Attribute("Exp")?.Value) ?? formNameExpressionId;
            var xExpressionId = ParseInt(plist?.Element("X")?.Attribute("Exp")?.Value);
            var yExpressionId = ParseInt(plist?.Element("Y")?.Attribute("Exp")?.Value);
            var formColorSchemeId = ParseInt(plist?.Element("Color")?.Attribute("val")?.Value);
            var formFontSchemeId = ParseInt(plist?.Element("Font")?.Attribute("val")?.Value);
            var pulldownMenuObj = ParseInt(plist?.Element("_PulldownMenu")?.Attribute("obj")?.Value);
            var systemMenu = ParseBool(plist?.Element("SystemMenu")?.Attribute("val")?.Value) ?? (plist?.Element("SystemMenu") is not null ? true : null);
            var minimizeButton = ParseBool(plist?.Element("MinimizeButton")?.Attribute("val")?.Value) ?? (plist?.Element("MinimizeButton") is not null ? true : null);
            var maximizeButton = ParseBool(plist?.Element("MaximizeButton")?.Attribute("val")?.Value) ?? (plist?.Element("MaximizeButton") is not null ? true : null);
            var formUnits = XmlHelpers.Attr(plist?.Element("FormUnits") ?? new XElement("x"), "val");
            var verticalFactor = ParseInt(plist?.Element("VerticalFactor")?.Attribute("val")?.Value);
            var horizontalFactor = ParseInt(plist?.Element("HorizontalFactor")?.Attribute("val")?.Value);
            var windowType = XmlHelpers.Attr(plist?.Element("WindowType") ?? new XElement("x"), "val");
            var startupMode = XmlHelpers.Attr(plist?.Element("StartupMode") ?? new XElement("x"), "val");
            var persistentFormState = XmlHelpers.Attr(plist?.Element("PersistentFormState") ?? new XElement("x"), "val");
            var placement = plist?.Element("Placement");
            var placementValue = XmlHelpers.Attr(placement ?? new XElement("x"), "val");
            var placementTop = ParseInt(placement?.Attribute("top")?.Value);
            var placementBottom = ParseInt(placement?.Attribute("bottom")?.Value);
            var placementLeft = ParseInt(placement?.Attribute("left")?.Value);
            var placementRight = ParseInt(placement?.Attribute("right")?.Value);
            var titleBar = ParseBool(plist?.Element("TitleBar")?.Attribute("val")?.Value) ?? (plist?.Element("TitleBar") is not null ? true : null);
            var formModel = XmlHelpers.Attr(plist ?? new XElement("x"), "model");
            var mergeFileName = XmlHelpers.NormalizeText(
                plist?.Element("FileName")?.Attribute("val")?.Value
                ?? plist?.Element("FileName")?.Attribute("valUnicode")?.Value);
            var mergeFileNameExpressionId = ParseInt(plist?.Element("FileName")?.Attribute("Exp")?.Value);
            var mergeTags = (plist?.Element("TagsTable")?.Elements("MERGE_PARM") ?? Enumerable.Empty<XElement>())
                .Select(x => new TaskMergeTagDef(
                    Id: ParseInt(x.Attribute("id")?.Value) ?? 0,
                    Name: x.Attribute("TXT_U")?.Value ?? "",
                    Picture: x.Attribute("PIC_U")?.Value,
                    ExpressionId: ParseInt(x.Attribute("Exp")?.Value),
                    Column: x.Attribute("Column")?.Value))
                .ToList();

            var controls = new List<TaskFormControlDef>();
            var rawControls = formEntry.Elements("Control").ToList();
            var controlOrdinalToId = new Dictionary<int, int>();
            var usedControlIds = new HashSet<int>();
            var nextSyntheticControlId = rawControls
                .Select((control, index) => ParseInt(control.Attribute("_test_id")?.Value) ?? (index + 1))
                .DefaultIfEmpty(0)
                .Max() + 1;
            for (var controlIndex = 0; controlIndex < rawControls.Count; controlIndex++)
            {
                var c = rawControls[controlIndex];
                TouchKnownXmlAttributes(c);
                var id = ParseInt(c.Attribute("_test_id")?.Value) ?? (controlIndex + 1);
                if (!usedControlIds.Add(id))
                {
                    while (usedControlIds.Contains(nextSyntheticControlId))
                        nextSyntheticControlId++;
                    id = nextSyntheticControlId++;
                    usedControlIds.Add(id);
                }
                controlOrdinalToId[controlIndex + 1] = id;
                var parentOrdinal = ParseInt(c.Attribute("ISN_FATHER")?.Value);
                int? parentId = null;
                if (parentOrdinal.HasValue)
                {
                    if (controlOrdinalToId.TryGetValue(parentOrdinal.Value, out var mappedParent))
                        parentId = mappedParent;
                    else
                        parentId = parentOrdinal;
                }
                var cp = c.Element("PropertyList");
                if (cp is null)
                    continue;
                TouchKnownXmlAttributes(cp);
                var model = XmlHelpers.Attr(cp, "model");
                var isLine =
                    string.Equals(model, "CTRL_GUI0_LINE", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(model, "CTRL_GUI1_LINE", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(model, "CTRL_TEXT_LINE", StringComparison.OrdinalIgnoreCase);
                var x = ParseInt((isLine ? cp.Element("X1") : cp.Element("X"))?.Attribute("val")?.Value) ?? 0;
                var y = ParseInt((isLine ? cp.Element("Y1") : cp.Element("Y"))?.Attribute("val")?.Value) ?? 0;
                var w = ParseInt((isLine ? cp.Element("X2") : cp.Element("Width"))?.Attribute("val")?.Value) ?? (isLine ? x + 80 : 80);
                var h = ParseInt((isLine ? cp.Element("Y2") : cp.Element("Height"))?.Attribute("val")?.Value) ?? (isLine ? y : 20);
                var controlLayer = ParseInt(cp.Element("ControlLayer")?.Attribute("val")?.Value);
                var borderStyle = XmlHelpers.Attr(cp.Element("BorderStyle") ?? new XElement("x"), "val");
                var titleHeight = ParseInt(cp.Element("TitleHeight")?.Attribute("val")?.Value);
                var rowHeight = ParseInt(cp.Element("RowHeight")?.Attribute("val")?.Value);
                var styleValue = ParseInt(cp.Element("Style")?.Attribute("val")?.Value);
                var internalStyleValue = ParseInt(cp.Element("_Style")?.Attribute("val")?.Value);
                var internalLineStyleValue = ParseInt(cp.Element("_LineStyle")?.Attribute("val")?.Value);
                var orientation = cp.Element("_Orientation")?.Attribute("val")?.Value ?? cp.Element("Orientation")?.Attribute("val")?.Value;
                var verticalScroll = ParseBool(cp.Element("_VerticalScroll")?.Attribute("val")?.Value);
                var tabbingOrder = ParseInt(cp.Element("_TabbingOrder")?.Attribute("val")?.Value);
                var bottom = ParseInt(cp.Element("bottom")?.Attribute("val")?.Value);
                var staticType = cp.Element("_StaticType")?.Attribute("val")?.Value
                                 ?? cp.Element("StaticType")?.Attribute("val")?.Value;
                var windowSort = cp.Element("_WindowSort")?.Attribute("val")?.Value;
                var windowSortBy = cp.Element("WindowSortBy")?.Attribute("val")?.Value;
                var windowWidth = ParseInt(cp.Element("_WindowWidth")?.Attribute("val")?.Value);
                var tab = ParseInt(cp.Element("TabOrder")?.Attribute("val")?.Value);
                var horizontalAlignment = ParseInt(cp.Element("HorizontalAlignment")?.Attribute("val")?.Value);
                var text = XmlHelpers.NormalizeText(
                    cp.Element("Text")?.Attribute("valUnicode")?.Value
                    ?? cp.Element("Format")?.Attribute("valUnicode")?.Value);
                var defaultImageFile = XmlHelpers.NormalizeText(
                    cp.Element("DefaultImageFile")?.Attribute("valUnicode")?.Value
                    ?? cp.Element("DefaultImageFile")?.Attribute("val")?.Value);
                var itemsList = XmlHelpers.NormalizeText(
                    cp.Element("ItemsList")?.Attribute("valUnicode")?.Value
                    ?? cp.Element("ItemsList")?.Attribute("val")?.Value);
                var gridX = ParseInt(cp.Element("GridX")?.Attribute("val")?.Value);
                var gridY = ParseInt(cp.Element("GridY")?.Attribute("val")?.Value);
                var modifiable = ParseBool(cp.Element("Modifiable")?.Attribute("val")?.Value) ?? (cp.Element("Modifiable") is not null ? false : null);
                var modifyInQuery = ParseBool(cp.Element("ModifyInQuery")?.Attribute("val")?.Value);
                var multiLineEdit = ParseBool(cp.Element("MultiLineEdit")?.Attribute("val")?.Value) ?? ParseBool(cp.Element("MultiLine")?.Attribute("val")?.Value);
                var allowCrInData = ParseBool(cp.Element("AllowCRInData")?.Attribute("val")?.Value);
                var enableRtf = ParseBool(cp.Element("EnableRTF")?.Attribute("val")?.Value) ?? (cp.Element("EnableRTF") is not null ? true : null);
                var line3D = ParseBool(cp.Element("Line3D")?.Attribute("val")?.Value) ?? (cp.Element("Line3D") is not null ? true : null);
                var lineWidth = ParseInt(cp.Element("LineWidth")?.Attribute("val")?.Value);
                var autoExpand = ParseBool(cp.Element("AutoExpand")?.Attribute("val")?.Value) ?? (cp.Element("AutoExpand") is not null ? true : null);
                var expansionWindow = XmlHelpers.Attr(cp.Element("ExpansionWindow") ?? new XElement("x"), "val");
                var parkOnClick = ParseBool(cp.Element("ParkOnClick")?.Attribute("val")?.Value) ?? (cp.Element("ParkOnClick") is not null ? true : null);
                var lineDivider = ParseBool(cp.Element("LineDivider")?.Attribute("val")?.Value) ?? (cp.Element("LineDivider") is not null ? true : null);
                var columnDivider = ParseBool(cp.Element("ColumnDivider")?.Attribute("val")?.Value) ?? (cp.Element("ColumnDivider") is not null ? true : null);
                var setTableColorBy = XmlHelpers.Attr(cp.Element("SetTableColorBy") ?? new XElement("x"), "val");
                var alternatingBgColor = ParseInt(cp.Element("AlternatingBgColor")?.Attribute("val")?.Value);
                var gradientStyle = XmlHelpers.Attr(cp.Element("GradientStyle") ?? new XElement("x"), "val");
                var gradientColor = XmlHelpers.Attr(cp.Element("GradientColor") ?? new XElement("x"), "val");
                var imageStyle = XmlHelpers.Attr(cp.Element("ImageStyle") ?? new XElement("x"), "val");
                var rowHighlightStyle = XmlHelpers.Attr(cp.Element("RowHighlightStyle") ?? new XElement("x"), "val");
                var wallpaperStyle = XmlHelpers.Attr(cp.Element("WallpaperStyle") ?? new XElement("x"), "val");
                var checkboxMainStyle = XmlHelpers.Attr(cp.Element("CheckboxMainStyle") ?? new XElement("x"), "val");
                var updateStyle = XmlHelpers.Attr(cp.Element("UpdateStyle") ?? new XElement("x"), "val");
                var showGrid = ParseBool(cp.Element("ShowGrid")?.Attribute("val")?.Value) ?? (cp.Element("ShowGrid") is not null ? true : null);
                var showButtons = ParseBool(cp.Element("ShowButtons")?.Attribute("val")?.Value) ?? (cp.Element("ShowButtons") is not null ? true : null);
                var showLines = ParseBool(cp.Element("ShowLines")?.Attribute("val")?.Value) ?? (cp.Element("ShowLines") is not null ? true : null);
                var showRoot = ParseBool(cp.Element("ShowRoot")?.Attribute("val")?.Value) ?? (cp.Element("ShowRoot") is not null ? true : null);
                var showScrollBars = ParseBool(cp.Element("ShowScrollBars")?.Attribute("val")?.Value) ?? (cp.Element("ShowScrollBars") is not null ? true : null);
                var showInWindowMenu = ParseBool(cp.Element("ShowInWindowMenu")?.Attribute("val")?.Value) ?? (cp.Element("ShowInWindowMenu") is not null ? true : null);
                var topBorder = ParseBool(cp.Element("TopBorder")?.Attribute("val")?.Value) ?? (cp.Element("TopBorder") is not null ? true : null);
                var topBorderMargin = ParseInt(cp.Element("TopBorderMargin")?.Attribute("val")?.Value);
                var selectionMode = XmlHelpers.Attr(cp.Element("SelectionMode") ?? new XElement("x"), "val");
                var tabControlSide = XmlHelpers.Attr(cp.Element("TabControlSide") ?? new XElement("x"), "val");
                var visibleValue = ParseBool(cp.Element("Visible")?.Attribute("val")?.Value);
                var visibleExpressionId = ParseInt(cp.Element("Visible")?.Attribute("Exp")?.Value);
                var enabledValue = ParseBool(cp.Element("Enabled")?.Attribute("val")?.Value);
                var enabledExpressionId = ParseInt(cp.Element("Enabled")?.Attribute("Exp")?.Value);
                var columnTitle = XmlHelpers.NormalizeText(cp.Element("ColumnTitle")?.Attribute("valUnicode")?.Value);
                var sortable = ParseBool(cp.Element("Sortable")?.Attribute("val")?.Value);
                var data = cp.Element("Data");
                var dataColumn = data?.Attribute("Column")?.Value;
                var dataExp = ParseInt(data?.Attribute("Exp")?.Value);
                var toolTipNode = cp.Element("ToolTip") ?? cp.Element("Tooltip");
                var toolTipText = XmlHelpers.NormalizeText(
                    toolTipNode?.Attribute("valUnicode")?.Value
                    ?? toolTipNode?.Attribute("val")?.Value);
                var toolTipExp = ParseInt(toolTipNode?.Attribute("Exp")?.Value);
                var sourceTableObj = ResolveDataObjectRef(ctx, contexts, cp.Element("SourceTable"));
                var displayFieldObj = ResolveModelRefInt(ctx, contexts, cp.Element("_DisplayField"));
                var linkFieldObj = ResolveModelRefInt(ctx, contexts, cp.Element("_LinkField"));
                var indexObj = ParseInt(cp.Element("_Index")?.Attribute("obj")?.Value);
                var treeDescription = cp.Element("DescriptionVariable");
                var treeDescriptionColumn = treeDescription?.Attribute("Column")?.Value;
                var treeDescriptionExp = ParseInt(treeDescription?.Attribute("Exp")?.Value);
                var treeNodeId = cp.Element("NodeId");
                var treeNodeIdColumn = treeNodeId?.Attribute("Column")?.Value;
                var treeNodeIdExp = ParseInt(treeNodeId?.Attribute("Exp")?.Value);
                var treeParentId = cp.Element("ParentId");
                var treeParentIdColumn = treeParentId?.Attribute("Column")?.Value;
                var treeParentIdExp = ParseInt(treeParentId?.Attribute("Exp")?.Value);
                var treeRootExp = ParseInt(cp.Element("RootValue")?.Attribute("Exp")?.Value);
                var modelRefObj = ResolveModelRefInt(ctx, contexts, cp.Element("Model"));
                var modelVarColumn = ParseInt(cp.Element("Model")?.Element("Var")?.Attribute("Column")?.Value);
                var controlName = cp.Element("ControlName")?.Attribute("val")?.Value;
                var colorSchemeId = ParseInt(cp.Element("Color")?.Attribute("val")?.Value);
                var hoveringColorSchemeId = ParseInt(cp.Element("HoveringColor")?.Attribute("val")?.Value);
                var visitedColorSchemeId = ParseInt(cp.Element("VisitedColor")?.Attribute("val")?.Value);
                var fontSchemeId = ParseInt(cp.Element("Font")?.Attribute("val")?.Value);
                var buttonStyleValue = ParseInt(cp.Element("ButtonStyle")?.Attribute("val")?.Value);
                var tabInto = ParseBool(cp.Element("TabInto")?.Attribute("val")?.Value) ?? (cp.Element("TabInto") is not null ? false : null);
                var allowParking = ParseBool(cp.Element("AllowParking")?.Attribute("val")?.Value) ?? (cp.Element("AllowParking") is not null ? false : null);
                var raise = cp.Element("RaiseEvent");
                var raiseEventType = raise?.Element("Event")?.Element("EventType")?.Attribute("val")?.Value
                                     ?? raise?.Element("EventType")?.Attribute("val")?.Value;
                var raiseEventObj = raise?.Element("Event")?.Element("PublicObject")?.Attribute("obj")?.Value
                                    ?? raise?.Element("PublicObject")?.Attribute("obj")?.Value;
                var raiseEventParent = ParseInt(raise?.Element("Event")?.Element("Parent")?.Attribute("val")?.Value
                                                ?? raise?.Element("Parent")?.Attribute("val")?.Value);
                var raiseEventPublicComponentId = ParseInt(raise?.Element("Event")?.Element("PublicObject")?.Attribute("comp")?.Value
                                                           ?? raise?.Element("PublicObject")?.Attribute("comp")?.Value);
                var raiseInternalEventId = ParseInt(raise?.Element("Event")?.Element("InternalEventID")?.Attribute("val")?.Value
                                                    ?? raise?.Element("InternalEventID")?.Attribute("val")?.Value);
                var raiseKeyCombinationId = ParseInt(raise?.Element("Event")?.Element("KeyCombinationID")?.Attribute("val")?.Value
                                                     ?? raise?.Element("KeyCombinationID")?.Attribute("val")?.Value);
                var subformTaskNumber = ParseInt(cp.Element("TaskNumber")?.Attribute("obj")?.Value);
                var subformConnectTo = ParseInt(cp.Element("ConnectTo")?.Attribute("val")?.Value);
                var subformArguments = ParseSubformArguments(cp).ToList();
                var subformArgumentDefs = ParseArgumentDefs(cp.Element("Arguments"));
                var propertyExpressionIds = cp
                    .DescendantsAndSelf()
                    .Attributes("Exp")
                    .Select(a => ParseInt(a.Value))
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .Distinct()
                    .OrderBy(v => v)
                    .ToList();

                controls.Add(new TaskFormControlDef(
                    Id: id,
                    ParentId: parentId,
                    Model: model,
                    X: x,
                    Y: y,
                    Width: w,
                    Height: h,
                    ControlLayer: controlLayer,
                    BorderStyle: string.IsNullOrWhiteSpace(borderStyle) ? null : borderStyle,
                    TitleHeight: titleHeight,
                    RowHeight: rowHeight,
                    StyleValue: styleValue,
                    InternalStyleValue: internalStyleValue,
                    InternalLineStyleValue: internalLineStyleValue,
                    Orientation: orientation,
                    VerticalScroll: verticalScroll,
                    TabbingOrder: tabbingOrder,
                    Bottom: bottom,
                    StaticType: staticType,
                    WindowSort: windowSort,
                    WindowSortBy: string.IsNullOrWhiteSpace(windowSortBy) ? null : windowSortBy,
                    WindowWidth: windowWidth,
                    VisibleValue: visibleValue,
                    VisibleExpressionId: visibleExpressionId,
                    EnabledValue: enabledValue,
                    EnabledExpressionId: enabledExpressionId,
                    TabOrder: tab,
                    HorizontalAlignment: horizontalAlignment,
                    Text: text,
                    DefaultImageFile: defaultImageFile,
                    ItemsList: itemsList,
                    GridX: gridX,
                    GridY: gridY,
                    Modifiable: modifiable,
                    ModifyInQuery: modifyInQuery,
                    MultiLineEdit: multiLineEdit,
                    AllowCrInData: allowCrInData,
                    EnableRtf: enableRtf,
                    Line3D: line3D,
                    LineWidth: lineWidth,
                    AutoExpand: autoExpand,
                    ExpansionWindow: string.IsNullOrWhiteSpace(expansionWindow) ? null : expansionWindow,
                    ParkOnClick: parkOnClick,
                    LineDivider: lineDivider,
                    ColumnDivider: columnDivider,
                    SetTableColorBy: string.IsNullOrWhiteSpace(setTableColorBy) ? null : setTableColorBy,
                    AlternatingBgColor: alternatingBgColor,
                    GradientStyle: string.IsNullOrWhiteSpace(gradientStyle) ? null : gradientStyle,
                    GradientColor: string.IsNullOrWhiteSpace(gradientColor) ? null : gradientColor,
                    ImageStyle: string.IsNullOrWhiteSpace(imageStyle) ? null : imageStyle,
                    RowHighlightStyle: string.IsNullOrWhiteSpace(rowHighlightStyle) ? null : rowHighlightStyle,
                    WallpaperStyle: string.IsNullOrWhiteSpace(wallpaperStyle) ? null : wallpaperStyle,
                    CheckboxMainStyle: string.IsNullOrWhiteSpace(checkboxMainStyle) ? null : checkboxMainStyle,
                    UpdateStyle: string.IsNullOrWhiteSpace(updateStyle) ? null : updateStyle,
                    ShowGrid: showGrid,
                    ShowButtons: showButtons,
                    ShowLines: showLines,
                    ShowRoot: showRoot,
                    ShowScrollBars: showScrollBars,
                    ShowInWindowMenu: showInWindowMenu,
                    TopBorder: topBorder,
                    TopBorderMargin: topBorderMargin,
                    SelectionMode: string.IsNullOrWhiteSpace(selectionMode) ? null : selectionMode,
                    TabControlSide: string.IsNullOrWhiteSpace(tabControlSide) ? null : tabControlSide,
                    ColumnTitle: columnTitle,
                    Sortable: sortable,
                    DataColumn: dataColumn,
                    DataExpressionId: dataExp,
                    ToolTipText: toolTipText,
                    ToolTipExpressionId: toolTipExp,
                    SourceTableObj: sourceTableObj,
                    DisplayFieldObj: displayFieldObj,
                    LinkFieldObj: linkFieldObj,
                    IndexObj: indexObj,
                    ModelRefObj: modelRefObj,
                    ModelVarColumn: modelVarColumn,
                    ControlName: controlName,
                    ColorSchemeId: colorSchemeId,
                    HoveringColorSchemeId: hoveringColorSchemeId,
                    VisitedColorSchemeId: visitedColorSchemeId,
                    FontSchemeId: fontSchemeId,
                    ButtonStyleValue: buttonStyleValue,
                    TabInto: tabInto,
                    AllowParking: allowParking,
                    RaiseEventType: raiseEventType,
                    RaiseEventObject: raiseEventObj,
                    RaiseEventParent: raiseEventParent,
                    RaiseEventPublicComponentId: raiseEventPublicComponentId,
                    RaiseEventInternalEventId: raiseInternalEventId,
                    RaiseEventKeyCombinationId: raiseKeyCombinationId,
                    SubformTaskNumber: subformTaskNumber,
                    SubformConnectTo: subformConnectTo,
                    SubformArguments: subformArguments,
                    SubformArgumentDefs: subformArgumentDefs,
                    TreeDescriptionColumn: treeDescriptionColumn,
                    TreeDescriptionExpressionId: treeDescriptionExp,
                    TreeNodeIdColumn: treeNodeIdColumn,
                    TreeNodeIdExpressionId: treeNodeIdExp,
                    TreeParentIdColumn: treeParentIdColumn,
                    TreeParentIdExpressionId: treeParentIdExp,
                    TreeRootExpressionId: treeRootExp,
                    PropertyExpressionIds: propertyExpressionIds
                ));
            }

            var form = new TaskFormDef(width, height, formX, formY, formName, formText, formTextExpressionId, xExpressionId, yExpressionId, formColorSchemeId, formFontSchemeId, pulldownMenuObj, systemMenu, minimizeButton, maximizeButton, formUnits, verticalFactor, horizontalFactor, windowType, startupMode, persistentFormState, placementValue, placementTop, placementBottom, placementLeft, placementRight, titleBar, controls, mergeFileName, mergeFileNameExpressionId, mergeTags);
            var classIndex = ParseInt(formEntry.Attribute("CLSS")?.Value);
            result.Add(new TaskFormEntryDef(i + 1, classIndex, formModel, form));
        }

        return result;
    }

    private static IEnumerable<string> ParseSubformArguments(XElement propertyList)
    {
        var argsRoot = propertyList.Element("Arguments");
        if (argsRoot is null)
            yield break;

        var argumentNodes = argsRoot.Elements("Arguments").SelectMany(x => x.Elements("Argument"));
        if (!argumentNodes.Any())
            argumentNodes = argsRoot.Elements("Argument");

        foreach (var argument in argumentNodes)
        {
            var skip = XmlHelpers.Attr(argument.Element("Skip") ?? new XElement("x"), "val");
            if (string.Equals(skip, "Y", StringComparison.OrdinalIgnoreCase))
                continue;

            var varRef = XmlHelpers.Attr(argument.Element("Var") ?? new XElement("x"), "val");
            if (!string.IsNullOrWhiteSpace(varRef))
            {
                yield return varRef;
                continue;
            }

            var expressionRef = XmlHelpers.Attr(argument.Element("Expression") ?? new XElement("x"), "val");
            if (!string.IsNullOrWhiteSpace(expressionRef))
                yield return "EXP:" + expressionRef;
        }
    }

    private static IReadOnlyList<TaskArgumentDef> ParseArgumentDefs(XElement? argsRoot)
    {
        if (argsRoot is null)
            return Array.Empty<TaskArgumentDef>();

        var argumentNodes = argsRoot.Elements("Arguments").SelectMany(x => x.Elements("Argument"));
        if (!argumentNodes.Any())
            argumentNodes = argsRoot.Elements("Argument");

        return argumentNodes.Select(argument => new TaskArgumentDef(
            Id: ParseInt(argument.Element("id")?.Attribute("val")?.Value),
            Variable: XmlHelpers.Attr(argument.Element("Var") ?? argument.Element("Variable") ?? new XElement("x"), "val"),
            Skip: ParseBool(argument.Element("Skip")?.Attribute("val")?.Value),
            ExpressionId: ParseInt(argument.Element("Expression")?.Attribute("val")?.Value),
            Parent: ParseInt(argument.Element("Parent")?.Attribute("val")?.Value),
            Name: XmlHelpers.Attr(argument.Element("Name") ?? new XElement("x"), "val"),
            Type: XmlHelpers.Attr(argument.Element("Type") ?? new XElement("x"), "val"),
            VtType: XmlHelpers.Attr(argument.Element("VT_Type") ?? new XElement("x"), "val"),
            TypeLibrary: XmlHelpers.Attr(argument.Element("TypeLibrary") ?? new XElement("x"), "val"),
            ObjectName: XmlHelpers.Attr(argument.Element("ObjectName") ?? new XElement("x"), "val"),
            DotNetType: XmlHelpers.Attr(argument.Element("DotNetType") ?? new XElement("x"), "val"),
            Exp: ParseInt(argument.Element("Exp")?.Attribute("val")?.Value)
        )).ToList();
    }

    private static TaskReturnValueDef? ParseReturnValueDef(XElement? returnValueNode)
    {
        if (returnValueNode is null)
            return null;

        var parameterAttributes = (returnValueNode.Element("ParametersAttributes")?.Elements("Attr") ?? Enumerable.Empty<XElement>())
            .Select(a => XmlHelpers.Attr(a, "val"))
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToList();

        return new TaskReturnValueDef(
            Value: XmlHelpers.Attr(returnValueNode, "val"),
            MgAttr: returnValueNode.Element("ReturnValueAttr")?.Attribute("MgAttr")?.Value,
            ParameterAttributes: parameterAttributes,
            TaskParameters: ParseInt(returnValueNode.Element("TSK_PARAMS")?.Attribute("val")?.Value),
            ParametersCount: ParseInt(returnValueNode.Element("ParametersCount")?.Attribute("val")?.Value)
        );
    }

    private static void ParseMenus(XDocument doc, ParsedXpa parsed)
    {
        var menus = doc.Descendants("MenusRepository").Elements("Menus").Elements("Menu").ToList();
        for (var i = 0; i < menus.Count; i++)
        {
            var menu = menus[i];
            var entries = menu.Elements("MenuEntry").Select(x => ParseMenuEntry(doc, x)).ToList();
            parsed.Menus.Add(new MenuDef(
                Obj: i + 1,
                Name: XmlHelpers.Attr(menu.Element("Name") ?? new XElement("x"), "val"),
                MenuType: menu.Element("MenuType")?.Attribute("val")?.Value,
                ToolNumber: ParseInt(menu.Element("ToolNumber")?.Attribute("val")?.Value),
                Entries: entries
            ));
        }

        var project = doc.Descendants("ProjectProperties").Elements("ProjectData").FirstOrDefault();
        parsed.ProjectMenuSettings = new ProjectMenuSettingsDef(
            SystemPulldownMenuObj: ParseInt(project?.Element("_SystemPulldownMenu")?.Attribute("obj")?.Value),
            SystemContextMenuObj: ParseInt(project?.Element("_SystemContextMenu")?.Attribute("obj")?.Value)
        );
    }

    private static void ParseRights(ParseContext ctx, ParsedXpa parsed)
    {
        var rights = ctx.Document.Descendants("RightsRepository")
            .Descendants("Rights")
            .Elements("Right")
            .ToList();
        foreach (var right in rights)
        {
            var name = XmlHelpers.Attr(right, "name");
            var key = XmlHelpers.Attr(right.Element("Key") ?? new XElement("x"), "val");
            var publicName = right.Element("PublicName")?.Attribute("val")?.Value;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            parsed.Rights.Add(new RightDef(
                Name: name,
                Key: key,
                PublicName: publicName,
                SourceComponent: ctx.IsMain ? null : ctx.Name
            ));
        }
    }

    private static MenuEntryDef ParseMenuEntry(XDocument doc, XElement menuEntry)
    {
        var eventNode = menuEntry.Element("Event");
        var ev = eventNode is null
            ? null
            : new MenuEventDef(
                EventType: XmlHelpers.Attr(eventNode.Element("EventType") ?? new XElement("x"), "val"),
                InternalEventId: ParseInt(eventNode.Element("InternalEventID")?.Attribute("val")?.Value),
                PublicObject: eventNode.Element("PublicObject")?.Attribute("obj")?.Value
            );

        var programObj = ParseInt(menuEntry.Element("Program")?.Attribute("obj")?.Value);
        var subEntries = menuEntry.Element("Menu")?.Elements("MenuEntry").Select(x => ParseMenuEntry(doc, x)).ToList() ?? new List<MenuEntryDef>();
        return new MenuEntryDef(
            MenuType: XmlHelpers.Attr(menuEntry.Element("MenuType") ?? new XElement("x"), "val"),
            Description: menuEntry.Element("Description_U")?.Attribute("val")?.Value,
            ProgramObj: programObj,
            ProgramDescription: ResolveMenuProgramDescription(doc, programObj),
            Event: ev,
            ToolNumber: ParseInt(menuEntry.Element("Tool")?.Element("ToolNumber")?.Attribute("val")?.Value),
            ToolGroup: ParseInt(menuEntry.Element("Tool")?.Element("ToolGroup")?.Attribute("val")?.Value),
            Children: subEntries
        );
    }

    private static string? ResolveMenuProgramDescription(XDocument doc, int? programObj)
    {
        if (!programObj.HasValue || programObj.Value <= 0)
            return null;

        var rootTasks = doc.Descendants("ProgramsRepository").Elements("Programs").Elements("Task").ToList();
        if (programObj.Value > rootTasks.Count)
            return null;

        return rootTasks[programObj.Value - 1].Element("Header")?.Attribute("Description")?.Value;
    }

    private static (int? PrimaryDbObj, IReadOnlyList<TaskLogicSelectDef> Selects, IReadOnlyList<TaskLogicLinkDef> Links, IReadOnlyList<TaskDataViewSourceDef> DataViewSources, IReadOnlyList<TaskBlockDef> Blocks, IReadOnlyList<TaskEndBlockDef> EndBlocks, IReadOnlyList<TaskEndLinkDef> EndLinks, IReadOnlyList<TaskCallDef> TabCalls, IReadOnlyList<TaskRowLogicDef> StartLogics, IReadOnlyList<TaskRaiseEventDef> StartRaises, IReadOnlyList<TaskRowLogicDef> EndLogics, IReadOnlyList<TaskRaiseEventDef> EndRaises, bool HasStartLogicUnit, bool HasEndLogicUnit, IReadOnlyList<TaskRowLogicDef> RowLogics, IReadOnlyList<TaskRowLogicDef> SavingRowLogics, IReadOnlyList<TaskValidationDef> FlowValidations, IReadOnlyList<TaskFunctionOverrideDef> FunctionOverrides, IReadOnlyList<TaskHandlerDef> Handlers, IReadOnlyList<TaskGroupLogicDef> GroupLogics, IReadOnlyList<TaskGapDef> Gaps, IReadOnlyList<TaskFormIoDef> FormIos) ParseTaskLogic(
        ParseContext ctx,
        IReadOnlyList<ParseContext> contexts,
        XElement task,
        IReadOnlyList<int> resourceDataObjects,
        int? topLevelProgramIndexLocal,
        List<TaskExpressionDef> expressions)
    {
        var resourceColumnIdsInOrder = (task.Element("Resource")?.Element("Columns")?.Elements("Column") ?? Enumerable.Empty<XElement>())
            .Select(x => ParseInt(x.Attribute("id")?.Value))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToList();
        var logicalResourceColumns = (task.Element("Resource")?.Element("Columns")?.Elements("Column") ?? Enumerable.Empty<XElement>())
            .Select(x => new
            {
                Id = ParseInt(x.Attribute("id")?.Value),
                Attr = x.Element("PropertyList")?.Element("Model")?.Attribute("attr_obj")?.Value
            })
            .Where(x => x.Id.HasValue && string.Equals(x.Attr, "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id!.Value)
            .ToHashSet();
        int? primaryDbObj = null;
        var selects = new List<TaskLogicSelectDef>();
        var links = new List<TaskLogicLinkDef>();
        var dataViewSources = new List<TaskDataViewSourceDef>();
        var blocks = new List<TaskBlockDef>();
        var endBlocks = new List<TaskEndBlockDef>();
        var endLinks = new List<TaskEndLinkDef>();
        var tabCalls = new List<TaskCallDef>();
        var startLogics = new List<TaskRowLogicDef>();
        var startRaises = new List<TaskRaiseEventDef>();
        var endLogics = new List<TaskRowLogicDef>();
        var endRaises = new List<TaskRaiseEventDef>();
        var hasStartLogicUnit = false;
        var hasEndLogicUnit = false;
        var rowLogics = new List<TaskRowLogicDef>();
        var savingRowLogics = new List<TaskRowLogicDef>();
        var groupLogics = new List<TaskGroupLogicDef>();
        var flowValidations = new List<TaskValidationDef>();
        var functionOverrides = new List<TaskFunctionOverrideDef>();
        var handlers = new List<TaskHandlerDef>();
        var formIos = new List<TaskFormIoDef>();
        var gaps = new List<TaskGapDef>();

        int? EnsureSyntheticExpression(string syntax, string attribute = "B")
        {
            if (string.IsNullOrWhiteSpace(syntax))
                return null;
            var existing = expressions.FirstOrDefault(e =>
                string.Equals(e.Syntax, syntax, StringComparison.Ordinal) &&
                string.Equals(e.Attribute, attribute, StringComparison.Ordinal));
            if (existing is not null)
                return existing.Ordinal;
            var nextId = expressions.Count == 0 ? 1 : expressions.Max(e => e.Ordinal) + 1;
            expressions.Add(new TaskExpressionDef(nextId, syntax, attribute));
            return nextId;
        }

        string? GetExpressionSyntax(int? id)
        {
            if (!id.HasValue)
                return null;
            return expressions.FirstOrDefault(e => e.Ordinal == id.Value)?.Syntax;
        }

        int? NegateCondition(int? conditionExpressionId)
        {
            var syntax = GetExpressionSyntax(conditionExpressionId);
            if (string.IsNullOrWhiteSpace(syntax))
                return null;
            return EnsureSyntheticExpression($"NOT({syntax})", "B");
        }

        int? CombineConditions(int? leftConditionExpressionId, int? rightConditionExpressionId)
        {
            if (!leftConditionExpressionId.HasValue)
                return rightConditionExpressionId;
            if (!rightConditionExpressionId.HasValue)
                return leftConditionExpressionId;
            var leftSyntax = GetExpressionSyntax(leftConditionExpressionId);
            var rightSyntax = GetExpressionSyntax(rightConditionExpressionId);
            if (string.IsNullOrWhiteSpace(leftSyntax))
                return rightConditionExpressionId;
            if (string.IsNullOrWhiteSpace(rightSyntax))
                return leftConditionExpressionId;
            return EnsureSyntheticExpression($"({leftSyntax}) AND ({rightSyntax})", "B");
        }

        static int? TryGetXmlLine(XElement? node)
        {
            if (node is IXmlLineInfo info && info.HasLineInfo())
                return info.LineNumber;
            return null;
        }

        var taskDescription = XmlHelpers.Attr(task.Element("Header") ?? new XElement("x"), "Description");
        string BuildXmlTrace(XElement luNode, XElement logicLineNode, XElement statementNode, int luIndex, int lineIndex)
        {
            var luLine = TryGetXmlLine(luNode);
            var llLine = TryGetXmlLine(logicLineNode);
            var stLine = TryGetXmlLine(statementNode);
            var luPart = luLine.HasValue ? $"luLine={luLine.Value}" : "luLine=?";
            var llPart = llLine.HasValue ? $"llLine={llLine.Value}" : "llLine=?";
            var stPart = stLine.HasValue ? $"stLine={stLine.Value}" : "stLine=?";
            return $"Task=\"{taskDescription}\" LogicUnit[{luIndex}]/{statementNode.Name.LocalName} LogicLine[{lineIndex}] ({luPart}, {llPart}, {stPart})";
        }

        var logicUnits = (task.Element("TaskLogic")?.Elements("LogicUnit") ?? Enumerable.Empty<XElement>()).ToList();
        for (var luIdx = 0; luIdx < logicUnits.Count; luIdx++)
        {
            var lu = logicUnits[luIdx];
            var luIndex = luIdx + 1;
            var propagate = lu.Attribute("propagate")?.Value;
            var level = XmlHelpers.Attr(lu.Element("Level") ?? new XElement("x"), "val");
            var type = XmlHelpers.Attr(lu.Element("Type") ?? new XElement("x"), "val");
            var scope = XmlHelpers.Attr(lu.Element("Scope") ?? new XElement("x"), "val");
            var luConditionExp = lu.Element("Condition")?.Attribute("Exp")?.Value;
            int? luConditionExpressionId = null;
            if (int.TryParse(luConditionExp, out var luCondRaw))
            {
                var luCondIdx = Math.Abs(luCondRaw);
                if (luCondIdx > 0 && expressions.Any(e => e.Ordinal == luCondIdx))
                    luConditionExpressionId = luCondIdx;
            }
            var eventNode = lu.Element("Event");
            var eventType = XmlHelpers.Attr(eventNode?.Element("EventType") ?? new XElement("x"), "val");
            var eventTime = ParseInt(eventNode?.Element("Time")?.Attribute("val")?.Value);
            var eventInternalEventId = ParseInt(eventNode?.Element("InternalEventID")?.Attribute("val")?.Value);
            var eventParent = ParseInt(eventNode?.Element("Parent")?.Attribute("val")?.Value);
            var eventComp = ParseInt(eventNode?.Element("PublicObject")?.Attribute("comp")?.Value);
            var eventObj = eventNode?.Element("PublicObject")?.Attribute("obj")?.Value;
            var eventExp = eventNode?.Element("Exp")?.Attribute("val")?.Value;
            var keyCombinationId = ParseInt(eventNode?.Element("KeyCombinationID")?.Attribute("val")?.Value);
            var reference = XmlHelpers.Attr(lu.Element("Refernce") ?? new XElement("x"), "val");
            if (string.IsNullOrWhiteSpace(reference))
                reference = XmlHelpers.Attr(lu.Element("TXT") ?? new XElement("x"), "val");

            var calls = new List<TaskCallDef>();
            var updates = new List<TaskUpdateDef>();
            var stops = new List<TaskStopDef>();
            var raises = new List<TaskRaiseEventDef>();
            var invokes = new List<TaskInvokeDef>();
            var remarks = new List<TaskRemarkDef>();
            var functionParameters = new List<TaskFunctionParameterDef>();
            var rowActions = new List<TaskRowActionDef>();
            var rowBlocks = new List<TaskBlockDef>();
            var rowEndBlocks = new List<TaskEndBlockDef>();
            var handlerActions = new List<TaskRowActionDef>();
            var handlerBlocks = new List<TaskBlockDef>();
            var handlerEndBlocks = new List<TaskEndBlockDef>();
            var functionActions = new List<TaskRowActionDef>();
            var functionBlocks = new List<TaskBlockDef>();
            var functionEndBlocks = new List<TaskEndBlockDef>();
            var handlerFormIos = new List<TaskFormIoDef>();
            var handlerParameterColumnIds = new List<int>();
            int? activeBlockConditionExpId = null;
            var blockStack = new Stack<(string Type, int? ParentCondition, int? RawIfCondition, int? EffectiveIfCondition)>();
            int? CurrentLoopConditionExpressionId()
            {
                foreach (var block in blockStack)
                {
                    if (string.Equals(block.Type, "L", StringComparison.OrdinalIgnoreCase))
                        return block.EffectiveIfCondition;
                }
                return null;
            }
            var isHandlerLogic =
                string.Equals(level, "C", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(level, "V", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(level, "H", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(type, "U", StringComparison.OrdinalIgnoreCase));
            var isFunctionLogic = string.Equals(level, "F", StringComparison.OrdinalIgnoreCase);
            if (level == "T" && type == "P")
                hasStartLogicUnit = true;
            if (level == "T" && type == "S")
                hasEndLogicUnit = true;

            var currentDbObj = primaryDbObj;
            int? currentLinkSequence = null;
            var logicLines = (lu.Element("LogicLines")?.Elements("LogicLine") ?? Enumerable.Empty<XElement>()).ToList();
            string? ResolveImplicitLinkReturnValueName(int currentLineIndex)
            {
                if (currentLineIndex <= 0)
                    return null;

                var previous = logicLines[currentLineIndex - 1].Elements().FirstOrDefault();
                if (previous is null || !string.Equals(previous.Name.LocalName, "Select", StringComparison.Ordinal))
                    return null;

                var name = XmlHelpers.Attr(previous, "Name");
                if (string.IsNullOrWhiteSpace(name))
                    return null;

                var typeVal = XmlHelpers.Attr(previous.Element("Type") ?? new XElement("x"), "val");
                if (!string.Equals(typeVal, "V", StringComparison.OrdinalIgnoreCase))
                    return null;

                if (XmlHelpers.Attr(previous.Element("IsParameter") ?? new XElement("x"), "val") == "Y")
                    return null;

                if (ParseBool(previous.Element("PartOfDataview")?.Attribute("val")?.Value) != true)
                    return null;

                var columnId = ParseInt(XmlHelpers.Attr(previous.Element("Column") ?? new XElement("x"), "val"));
                if (!columnId.HasValue || columnId.Value <= 0 || columnId.Value > resourceColumnIdsInOrder.Count)
                    return null;

                var resolvedColumnId = resourceColumnIdsInOrder[columnId.Value - 1];
                return logicalResourceColumns.Contains(resolvedColumnId) ? name : null;
            }

            for (var lineIdx = 0; lineIdx < logicLines.Count; lineIdx++)
            {
                var line = logicLines[lineIdx];
                var lineIndex = lineIdx + 1;
                var first = line.Elements().FirstOrDefault();
                if (first == null)
                    continue;
                TouchKnownXmlAttributes(first);
                var xmlTrace = BuildXmlTrace(lu, line, first, luIndex, lineIndex);
                switch (first.Name.LocalName)
                {
                    case "DATAVIEW_SRC":
                    {
                        var idxStr = XmlHelpers.Attr(first, "IDX");
                        var dataViewSourceType = XmlHelpers.Attr(first, "Type");
                        var idx = ParseInt(idxStr);
                        dataViewSources.Add(new TaskDataViewSourceDef(idx, string.IsNullOrWhiteSpace(dataViewSourceType) ? null : dataViewSourceType, xmlTrace));
                        if (idx.HasValue)
                        {
                            if (idx.Value > 0 && idx.Value <= resourceDataObjects.Count)
                            {
                                primaryDbObj = resourceDataObjects[idx.Value - 1];
                                currentDbObj = primaryDbObj;
                            }
                        }
                        break;
                    }
                    case "LNK":
                    {
                        var linkDbObj = ResolveDataObjectRef(ctx, contexts, first.Element("DB"));
                        if (linkDbObj.HasValue)
                        {
                            var direction = XmlHelpers.Attr(first, "Direction");
                            int? keyId = int.TryParse(XmlHelpers.Attr(first, "Key"), out var k) ? k : null;
                            var sortType = first.Attribute("SortType")?.Value;
                            var mode = first.Attribute("Mode")?.Value;
                            var returnValue = first.Attribute("ReturnValue")?.Value;
                            if (string.IsNullOrWhiteSpace(returnValue))
                                returnValue = ResolveImplicitLinkReturnValueName(lineIdx);
                            var evaluateConditionMode = first.Attribute("EVL_CND")?.Value;
                            var view = first.Attribute("VIEW")?.Value;
                            var views = first.Attribute("VIEWS")?.Value;
                            var fieldId = ParseInt(first.Attribute("FieldID")?.Value);
                            var expanded = string.Equals(first.Element("Expanded")?.Attribute("val")?.Value, "1", StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(first.Element("Expanded")?.Attribute("val")?.Value, "Y", StringComparison.OrdinalIgnoreCase);
                            var conditionExpId = ParseInt(first.Element("Condition")?.Attribute("Exp")?.Value);
                            links.Add(new TaskLogicLinkDef(linkDbObj.Value, direction, keyId, sortType, mode, returnValue, conditionExpId, evaluateConditionMode, view, views, fieldId, expanded, xmlTrace));
                            currentDbObj = linkDbObj.Value;
                            currentLinkSequence = links.Count;
                        }
                        break;
                    }
                    case "END_LINK":
                        endLinks.Add(new TaskEndLinkDef(xmlTrace));
                        currentDbObj = primaryDbObj;
                        currentLinkSequence = null;
                        break;
                    case "Select":
                    {
                        var name = XmlHelpers.Attr(first, "Name");
                        var columnStr = XmlHelpers.Attr(first.Element("Column") ?? new XElement("x"), "val");
                        var typeVal = XmlHelpers.Attr(first.Element("Type") ?? new XElement("x"), "val");
                        var isParam = XmlHelpers.Attr(first.Element("IsParameter") ?? new XElement("x"), "val") == "Y";
                        if (int.TryParse(columnStr, out var columnId))
                        {
                            var resolvedColumnId = columnId;
                            if (typeVal == "V" && columnId > 0 && columnId <= resourceColumnIdsInOrder.Count)
                                resolvedColumnId = resourceColumnIdsInOrder[columnId - 1];
                            var sourceDbObj = typeVal == "R" ? currentDbObj : null;
                            var assId = ParseInt(first.Element("ASS")?.Attribute("val")?.Value);
                            var oleSubformInfo = ParseInt(first.Element("INT_OLESUBFORM_INFO")?.Element("v")?.Value);
                            var rangeNode = first.Element("Range");
                            var hasRange = rangeNode is not null;
                            var rangeMin = ParseInt(rangeNode?.Attribute("MIN")?.Value);
                            var rangeMax = ParseInt(rangeNode?.Attribute("MAX")?.Value);
                            var locateNode = first.Element("Locate");
                            var hasLocate = locateNode is not null;
                            var locateMin = ParseInt(locateNode?.Attribute("MIN")?.Value);
                            var locateMax = ParseInt(locateNode?.Attribute("MAX")?.Value);
                            var realVarName = first.Element("REAL_VNAME_TXT")?.Attribute("val")?.Value;
                            var partOfDataview = ParseBool(first.Element("PartOfDataview")?.Attribute("val")?.Value);
                            var exposedToRoute = first.Element("ExposedToRoute")?.Attribute("val")?.Value;
                            var displayName =
                                first.Element("DisplayName")?.Attribute("valUnicode")?.Value
                                ?? first.Element("DisplayName")?.Attribute("val")?.Value;
                            var internalCompareInfo = (first.Element("INT_RCMP_INFO")?.Elements("v")
                                .Select(v => ParseInt(v.Value))
                                .Where(v => v.HasValue)
                                .Select(v => v!.Value)
                                .ToList()) ?? new List<int>();
                            var internalDitInfo = (first.Element("INT_DIT_INFO")?.Elements("v")
                                .Select(v => ParseInt(v.Value))
                                .Where(v => v.HasValue)
                                .Select(v => v!.Value)
                                .ToList()) ?? new List<int>();
                            selects.Add(new TaskLogicSelectDef(
                                Name: name,
                                ColumnId: resolvedColumnId,
                                Type: typeVal,
                                OriginLevel: level,
                                OriginType: type,
                                IsParameter: isParam,
                                IsFunctionSelect: level == "F",
                                SourceDbObj: sourceDbObj,
                                SourceLinkSequence: currentLinkSequence,
                                AssignmentExpressionId: assId,
                                OleSubformInfo: oleSubformInfo,
                                HasRange: hasRange,
                                RangeMin: rangeMin,
                                RangeMax: rangeMax,
                                HasLocate: hasLocate,
                                LocateMin: locateMin,
                                LocateMax: locateMax,
                                RealVarName: realVarName,
                                PartOfDataview: partOfDataview,
                                ExposedToRoute: exposedToRoute,
                                DisplayName: displayName,
                                InternalCompareInfo: internalCompareInfo,
                                InternalDitInfo: internalDitInfo,
                                XmlTrace: xmlTrace
                            ));
                            if (string.Equals(level, "F", StringComparison.OrdinalIgnoreCase) && isParam)
                            {
                                functionParameters.Add(new TaskFunctionParameterDef(
                                    SelectName: name,
                                    ColumnId: resolvedColumnId,
                                    AssignmentExpressionId: assId,
                                    XmlTrace: xmlTrace
                                ));
                            }
                            if (string.Equals(level, "H", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(type, "U", StringComparison.OrdinalIgnoreCase) &&
                                isParam &&
                                !handlerParameterColumnIds.Contains(resolvedColumnId))
                            {
                                handlerParameterColumnIds.Add(resolvedColumnId);
                            }
                            else if (string.Equals(level, "H", StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(type, "U", StringComparison.OrdinalIgnoreCase) &&
                                     !string.IsNullOrWhiteSpace(reference) &&
                                     string.Equals(typeVal, "V", StringComparison.OrdinalIgnoreCase) &&
                                     !handlerParameterColumnIds.Contains(resolvedColumnId))
                            {
                                handlerParameterColumnIds.Add(resolvedColumnId);
                            }
                        }
                        break;
                    }
                    case "CallTask":
                    {
                        var operationType = XmlHelpers.Attr(first.Element("OperationType") ?? new XElement("x"), "val");
                        var taskIdElement = first.Element("TaskID");
                        var taskId = ResolveTaskRef(ctx, contexts, taskIdElement, topLevelProgramIndexLocal, operationType);
                        var targetComponentId = ParseInt(taskIdElement?.Attribute("comp")?.Value);
                        var targetObjectId = ParseInt(taskIdElement?.Attribute("obj")?.Value);
                        var targetComponentName = ResolveTaskRefComponentName(ctx, contexts, taskIdElement);
                        var targetPublicName = ResolveTaskRefPublicName(ctx, contexts, taskIdElement);
                        var argsRoot = first.Element("Arguments");
                        var args = new List<string>();
                        var argNodes = argsRoot?.Elements("Argument") ?? Enumerable.Empty<XElement>();
                        foreach (var a in argNodes)
                        {
                            var varVal = XmlHelpers.Attr(a.Element("Var") ?? new XElement("x"), "val");
                            if (!string.IsNullOrWhiteSpace(varVal))
                            {
                                args.Add(varVal);
                                continue;
                            }
                            var exprVal = XmlHelpers.Attr(a.Element("Expression") ?? new XElement("x"), "val");
                            if (!string.IsNullOrWhiteSpace(exprVal))
                                args.Add("EXP:" + exprVal);
                        }
                        var argumentDefs = ParseArgumentDefs(argsRoot);
                        var page = first.Element("Page")?.Attribute("val")?.Value;
                        var ioDeviceIndex = ParseInt(first.Element("IODeviceInfo")?.Element("IoDeviceIndex")?.Attribute("val")?.Value);
                        var formEntryIndex = ParseInt(first.Element("FormEntryInfo")?.Element("FormEntryIndex")?.Attribute("val")?.Value);
                        var returnVariable = XmlHelpers.Attr(first.Element("RetRefName") ?? new XElement("x"), "val");
                        var waitForCompletion = ParseBool(first.Element("Wait")?.Attribute("val")?.Value);
                        var lineConditionLiteral = ParseBool(first.Element("Condition")?.Attribute("val")?.Value);
                        var lineConditionExpId = ParseInt(first.Element("Condition")?.Attribute("Exp")?.Value);
                        var conditionExpId = CombineConditions(activeBlockConditionExpId, lineConditionExpId);
                        var direction = XmlHelpers.Attr(first.Element("Direction") ?? new XElement("x"), "val");
                        var modifier = XmlHelpers.Attr(first.Element("Modifier") ?? new XElement("x"), "val");
                        var lockMode = XmlHelpers.Attr(first.Element("Lock") ?? new XElement("x"), "val");
                        var syncData = XmlHelpers.Attr(first.Element("SyncData") ?? new XElement("x"), "val");
                        var retainFocus = ParseBool(first.Element("RetainFocus")?.Attribute("val")?.Value);
                        var callEventType = XmlHelpers.Attr(first.Element("Event")?.Element("EventType") ?? new XElement("x"), "val");
                        var callEventInternalEventId = ParseInt(first.Element("Event")?.Element("InternalEventID")?.Attribute("val")?.Value);
                        var destSubformName = XmlHelpers.Attr(first.Element("DestSubformName") ?? new XElement("x"), "val");
                        var disabled = string.Equals(first.Element("Disabled")?.Attribute("val")?.Value, "Y", StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(first.Element("Disabled")?.Attribute("val")?.Value, "1", StringComparison.OrdinalIgnoreCase);
                        var functionName = XmlHelpers.Attr(first.Element("FunctionName") ?? new XElement("x"), "val");
                        var snippetCode = XmlHelpers.Attr(first.Element("SnippetCode") ?? new XElement("x"), "val");
                        var compiledCode = XmlHelpers.Attr(first.Element("CompiledCode") ?? new XElement("x"), "val");
                        var isRoute = ParseBool(first.Element("IsRoute")?.Attribute("val")?.Value);
                        var routePath = XmlHelpers.Attr(first.Element("RoutePath") ?? new XElement("x"), "val");
                        var returnValue = ParseReturnValueDef(first.Element("ReturnValue"));
                        var call = new TaskCallDef(taskId, targetComponentId, targetObjectId, targetComponentName, targetPublicName, operationType, args, argumentDefs, returnVariable, returnValue, conditionExpId, direction, modifier, page, ioDeviceIndex, formEntryIndex, waitForCompletion, lockMode, syncData, retainFocus, callEventType, callEventInternalEventId, destSubformName, disabled, functionName, snippetCode, compiledCode, isRoute, routePath, xmlTrace);
                        calls.Add(call);
                        if (operationType == "T" && string.Equals(level, "H", StringComparison.OrdinalIgnoreCase))
                            tabCalls.Add(call);
                            if ((level == "R" || level == "T") && (type == "P" || type == "S") && (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                                rowActions.Add(new TaskRowActionDef("Call", call, null, null, null, null, null, conditionExpId, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                            if (isFunctionLogic && (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                                functionActions.Add(new TaskRowActionDef("Call", call, null, null, null, null, null, conditionExpId, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                            if (isHandlerLogic && (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                                handlerActions.Add(new TaskRowActionDef("Call", call, null, null, null, null, null, conditionExpId, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                        break;
                    }
                    case "Update":
                    {
                        var variable = XmlHelpers.Attr(first.Element("Variable") ?? new XElement("x"), "val");
                        var withValue = XmlHelpers.Attr(first.Element("WithValue") ?? new XElement("x"), "val");
                        var forcedUpdate = string.Equals(first.Element("ForcedUpdate")?.Attribute("val")?.Value, "Y", StringComparison.OrdinalIgnoreCase);
                        var incremental = string.Equals(first.Element("Incremental")?.Attribute("val")?.Value, "I", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(first.Element("Incremental")?.Attribute("val")?.Value, "Y", StringComparison.OrdinalIgnoreCase);
                        var parent = first.Element("Parent")?.Attribute("val")?.Value;
                        var modifier = XmlHelpers.Attr(first.Element("Modifier") ?? new XElement("x"), "val");
                        var direction = XmlHelpers.Attr(first.Element("Direction") ?? new XElement("x"), "val");
                        var disabled = string.Equals(first.Element("Disabled")?.Attribute("val")?.Value, "Y", StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(first.Element("Disabled")?.Attribute("val")?.Value, "1", StringComparison.OrdinalIgnoreCase);
                        var lineConditionExp = ParseInt(first.Element("Condition")?.Attribute("Exp")?.Value);
                        var effectiveCondition = CombineConditions(activeBlockConditionExpId, lineConditionExp);
                        var update = new TaskUpdateDef(variable, withValue, effectiveCondition, forcedUpdate, incremental, parent, modifier, direction, disabled, xmlTrace);
                        updates.Add(update);
                        var lineConditionLiteral = ParseBool(first.Element("Condition")?.Attribute("val")?.Value);
                        if ((level == "R" || level == "T" || level == "G") && (type == "P" || type == "S") && modifier == "B")
                        {
                            rowActions.Add(new TaskRowActionDef("Update", null, update, null, null, null, null, effectiveCondition, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                        }
                        if (isFunctionLogic && (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                            functionActions.Add(new TaskRowActionDef("Update", null, update, null, null, null, null, effectiveCondition, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                        if (isHandlerLogic && (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                            handlerActions.Add(new TaskRowActionDef("Update", null, update, null, null, null, null, effectiveCondition, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                        break;
                    }
                    case "STP":
                    {
                        var mode = XmlHelpers.Attr(first, "Mode");
                        var text = first.Attribute("TXT")?.Value;
                        var titleText = first.Attribute("TitleTxt")?.Value;
                        var buttons = first.Attribute("Buttons")?.Value;
                        var image = first.Attribute("Image")?.Value;
                        var visualDisplay = first.Attribute("VR_DISP")?.Value;
                        var defaultButton = ParseInt(first.Attribute("DefaultButton")?.Value);
                        var appendToErrorLog = first.Attribute("ERR_LOG_DEF_CHG")?.Value switch
                        {
                            "Y" => true,
                            "N" => false,
                            _ => ParseBool(first.Element("AppendToErrorLog")?.Attribute("val")?.Value)
                        };
                        var expId = ParseInt(first.Attribute("Exp")?.Value);
                        var lineCondExpId = ParseInt(first.Element("Condition")?.Attribute("Exp")?.Value);
                        var condExpId = CombineConditions(activeBlockConditionExpId, lineCondExpId);
                        var stop = new TaskStopDef(mode, text, expId, titleText, buttons, image, visualDisplay, defaultButton, appendToErrorLog, condExpId, xmlTrace);
                        stops.Add(stop);
                        var modifier = XmlHelpers.Attr(first.Element("Modifier") ?? new XElement("x"), "val");
                        var lineConditionLiteral = ParseBool(first.Element("Condition")?.Attribute("val")?.Value);
                        if ((level == "R" || level == "T") && (type == "P" || type == "S") && modifier == "B")
                            rowActions.Add(new TaskRowActionDef("Stop", null, null, stop, null, null, null, condExpId, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                        if (isFunctionLogic && (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                            functionActions.Add(new TaskRowActionDef("Stop", null, null, stop, null, null, null, condExpId, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                        if (isHandlerLogic && (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                            handlerActions.Add(new TaskRowActionDef("Stop", null, null, stop, null, null, null, condExpId, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                        if (level == "R" && type == "M")
                            flowValidations.Add(new TaskValidationDef(condExpId, expId));
                        break;
                    }
                    case "Evaluate":
                    {
                        var exprId = ParseInt(first.Element("Expression")?.Attribute("val")?.Value);
                        var returnValue = XmlHelpers.Attr(first.Element("ReturnValue") ?? new XElement("x"), "val");
                        var lineConditionLiteral = ParseBool(first.Element("Condition")?.Attribute("val")?.Value);
                        var lineConditionExpId = ParseInt(first.Element("Condition")?.Attribute("Exp")?.Value);
                        var effectiveConditionExpId = CombineConditions(activeBlockConditionExpId, lineConditionExpId);
                        var modifier = XmlHelpers.Attr(first.Element("Modifier") ?? new XElement("x"), "val");
                            if (exprId.HasValue && !string.IsNullOrWhiteSpace(returnValue))
                            {
                                var update = new TaskUpdateDef(returnValue, exprId.Value.ToString(), effectiveConditionExpId, false, false, null, modifier, null, false, xmlTrace);
                                updates.Add(update);
                                if ((level == "R" || level == "T" || level == "G") && (type == "P" || type == "S") && modifier == "B")
                                    rowActions.Add(new TaskRowActionDef("Evaluate", null, null, null, null, exprId, returnValue, effectiveConditionExpId, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                                if (exprId.HasValue && isFunctionLogic && (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                                    functionActions.Add(new TaskRowActionDef("Evaluate", null, null, null, null, exprId, returnValue, effectiveConditionExpId, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                                if (exprId.HasValue && isHandlerLogic && (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                                    handlerActions.Add(new TaskRowActionDef("Evaluate", null, null, null, null, exprId, returnValue, effectiveConditionExpId, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                            }
                            else
                            {
                                if (exprId.HasValue && (level == "R" || level == "T" || level == "G") && (type == "P" || type == "S") && modifier == "B")
                                    rowActions.Add(new TaskRowActionDef("Evaluate", null, null, null, null, exprId, null, effectiveConditionExpId, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                                if (exprId.HasValue && isFunctionLogic && (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                                    functionActions.Add(new TaskRowActionDef("Evaluate", null, null, null, null, exprId, null, effectiveConditionExpId, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                                if (exprId.HasValue && isHandlerLogic && (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                                    handlerActions.Add(new TaskRowActionDef("Evaluate", null, null, null, null, exprId, null, effectiveConditionExpId, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                            }
                        break;
                    }
                    case "Invoke":
                    {
                        var opType = XmlHelpers.Attr(first.Element("OperationType") ?? new XElement("x"), "val");
                        var invokeEventType = XmlHelpers.Attr(first.Element("Event")?.Element("EventType") ?? new XElement("x"), "val");
                        var taskIdExprId = ParseInt(first.Element("TaskID")?.Attribute("obj")?.Value);
                        if (!taskIdExprId.HasValue)
                            taskIdExprId = ParseInt(first.Element("TaskID")?.Attribute("val")?.Value);
                        var cabinetExpId = ParseInt(first.Element("CabinetName")?.Attribute("Exp")?.Value);
                        var programExpId = ParseInt(first.Element("ProgramName")?.Attribute("Exp")?.Value);
                        var commandExpId = ParseInt(first.Element("Command")?.Attribute("val")?.Value);
                        var retDVal = XmlHelpers.Attr(first.Element("RETDVAL") ?? new XElement("x"), "val");
                        var functionName = XmlHelpers.Attr(first.Element("FunctionName") ?? new XElement("x"), "val");
                        var snippetCode = XmlHelpers.Attr(first.Element("SnippetCode") ?? new XElement("x"), "val");
                        var snippetLanguage = XmlHelpers.Attr(first.Element("SnippetLanguage") ?? new XElement("x"), "val");
                        var invokeArgsRoot = first.Element("Arguments");
                        var invokeArgs = new List<string>();
                        var invokeArgNodes = invokeArgsRoot?.Elements("Argument") ?? Enumerable.Empty<XElement>();
                        foreach (var a in invokeArgNodes)
                        {
                            var varVal = XmlHelpers.Attr(a.Element("Var") ?? new XElement("x"), "val");
                            if (!string.IsNullOrWhiteSpace(varVal))
                            {
                                invokeArgs.Add(varVal);
                                continue;
                            }
                            var exprVal = XmlHelpers.Attr(a.Element("Expression") ?? new XElement("x"), "val");
                            if (!string.IsNullOrWhiteSpace(exprVal))
                                invokeArgs.Add("EXP:" + exprVal);
                        }
                        var invokeArgumentDefs = ParseArgumentDefs(invokeArgsRoot);
                        var lineConditionLiteral = ParseBool(first.Element("Condition")?.Attribute("val")?.Value);
                        var lineInvokeCondExpId = ParseInt(first.Element("Condition")?.Attribute("Exp")?.Value);
                        var invokeCondExpId = CombineConditions(activeBlockConditionExpId, lineInvokeCondExpId);
                        var waitForCompletion = ParseBool(first.Element("Wait")?.Attribute("val")?.Value);
                        var show = XmlHelpers.Attr(first.Element("Show") ?? new XElement("x"), "val");
                        var lockMode = XmlHelpers.Attr(first.Element("Lock") ?? new XElement("x"), "val");
                        var syncData = XmlHelpers.Attr(first.Element("SyncData") ?? new XElement("x"), "val");
                        var retainFocus = ParseBool(first.Element("RetainFocus")?.Attribute("val")?.Value);
                        var methodName = XmlHelpers.Attr(first.Element("MethodName") ?? new XElement("x"), "val");
                        var option = XmlHelpers.Attr(first.Element("Option") ?? new XElement("x"), "val");
                        var propertyName = XmlHelpers.Attr(first.Element("PropertyName") ?? new XElement("x"), "val");
                        var convention = XmlHelpers.Attr(first.Element("Convention") ?? new XElement("x"), "val");
                        var serviceName = XmlHelpers.Attr(first.Element("ServiceName_LT") ?? new XElement("x"), "val");
                        var soapAction = XmlHelpers.Attr(first.Element("SoapAction_LT") ?? new XElement("x"), "val");
                        var operationName = XmlHelpers.Attr(first.Element("OperationName_LT") ?? new XElement("x"), "val");
                        var xmlNamespace = XmlHelpers.Attr(first.Element("Namespace_LT") ?? new XElement("x"), "val");
                        var returnValue = ParseReturnValueDef(first.Element("ReturnValue"));
                        var invoke = new TaskInvokeDef(
                            opType,
                            invokeEventType,
                            taskIdExprId,
                            cabinetExpId,
                            programExpId,
                            commandExpId,
                            string.IsNullOrWhiteSpace(retDVal) ? null : retDVal,
                            string.IsNullOrWhiteSpace(functionName) ? null : functionName,
                            string.IsNullOrWhiteSpace(snippetCode) ? null : snippetCode,
                            string.IsNullOrWhiteSpace(snippetLanguage) ? null : snippetLanguage,
                            invokeArgs,
                            invokeArgumentDefs,
                            returnValue,
                            invokeCondExpId,
                            waitForCompletion,
                            show,
                            lockMode,
                            syncData,
                            retainFocus,
                            string.IsNullOrWhiteSpace(methodName) ? null : methodName,
                            string.IsNullOrWhiteSpace(option) ? null : option,
                            string.IsNullOrWhiteSpace(propertyName) ? null : propertyName,
                            string.IsNullOrWhiteSpace(convention) ? null : convention,
                            string.IsNullOrWhiteSpace(serviceName) ? null : serviceName,
                            string.IsNullOrWhiteSpace(soapAction) ? null : soapAction,
                            string.IsNullOrWhiteSpace(operationName) ? null : operationName,
                            string.IsNullOrWhiteSpace(xmlNamespace) ? null : xmlNamespace,
                            ParseInt(first.Attribute("FieldID1")?.Value),
                            ParseInt(first.Attribute("FieldID2")?.Value),
                            ParseInt(first.Attribute("FieldID3")?.Value),
                            xmlTrace);
                        if (lineConditionLiteral != false)
                            invokes.Add(invoke);
                        var modifier = XmlHelpers.Attr(first.Element("Modifier") ?? new XElement("x"), "val");
                        if (lineConditionLiteral != false && (level == "R" || level == "T" || level == "G") && (type == "P" || type == "S") && modifier == "B")
                            rowActions.Add(new TaskRowActionDef("Invoke", null, null, null, invoke, null, null, invokeCondExpId, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                        if (lineConditionLiteral != false && isFunctionLogic && (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                            functionActions.Add(new TaskRowActionDef("Invoke", null, null, null, invoke, null, null, invokeCondExpId, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                        if (lineConditionLiteral != false && isHandlerLogic && (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                            handlerActions.Add(new TaskRowActionDef("Invoke", null, null, null, invoke, null, null, invokeCondExpId, lineConditionLiteral, CurrentLoopConditionExpressionId(), xmlTrace, null));
                        break;
                    }
                    case "Remark":
                    {
                        var text =
                            first.Attribute("TXT")?.Value ??
                            first.Attribute("valUnicode")?.Value ??
                            first.Attribute("val")?.Value ??
                            first.Element("Text")?.Attribute("valUnicode")?.Value ??
                            first.Element("Text")?.Attribute("val")?.Value ??
                            first.Value;
                        text = (text ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            var remark = new TaskRemarkDef(text, xmlTrace);
                            remarks.Add(remark);
                            var modifier = XmlHelpers.Attr(first.Element("Modifier") ?? new XElement("x"), "val");
                            if ((level == "R" || level == "T" || level == "G") &&
                                (type == "P" || type == "S") &&
                                (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                            {
                                rowActions.Add(new TaskRowActionDef("Remark", null, null, null, null, null, null, activeBlockConditionExpId, null, CurrentLoopConditionExpressionId(), xmlTrace, text));
                            }
                            if (isFunctionLogic &&
                                (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                            {
                                functionActions.Add(new TaskRowActionDef("Remark", null, null, null, null, null, null, activeBlockConditionExpId, null, CurrentLoopConditionExpressionId(), xmlTrace, text));
                            }
                            if (isHandlerLogic &&
                                (modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier)))
                            {
                                handlerActions.Add(new TaskRowActionDef("Remark", null, null, null, null, null, null, activeBlockConditionExpId, null, CurrentLoopConditionExpressionId(), xmlTrace, text));
                            }
                        }
                        break;
                    }
                    case "BLOCK":
                    {
                        var blockType = XmlHelpers.Attr(first, "Type");
                        var blockConditionExpId = ParseInt(first.Element("Condition")?.Attribute("Exp")?.Value);
                        var endBlockLine = ParseInt(first.Attribute("EndBlock")?.Value);
                        var endBlockSegmentLine = ParseInt(first.Attribute("EndBlockSegment")?.Value);
                        var blockDef = new TaskBlockDef(blockType, blockConditionExpId, lineIndex, endBlockLine, endBlockSegmentLine, xmlTrace);
                        blocks.Add(blockDef);
                        if ((level == "R" || level == "T") && (type == "P" || type == "S"))
                            rowBlocks.Add(blockDef);
                        if (isFunctionLogic)
                            functionBlocks.Add(blockDef);
                        if (isHandlerLogic)
                            handlerBlocks.Add(blockDef);
                        if (string.Equals(blockType, "E", StringComparison.OrdinalIgnoreCase))
                        {
                            if (blockStack.Count > 0)
                            {
                                var top = blockStack.Peek();
                                var elseCondition = blockConditionExpId ?? NegateCondition(top.RawIfCondition);
                                activeBlockConditionExpId = CombineConditions(top.ParentCondition, elseCondition);
                            }
                            else
                            {
                                activeBlockConditionExpId = blockConditionExpId;
                            }
                        }
                        else if (string.Equals(blockType, "L", StringComparison.OrdinalIgnoreCase))
                        {
                            var parentCondition = activeBlockConditionExpId;
                            // Loop conditions are emitted separately through LoopConditionExpressionId.
                            // Do not fold them into activeBlockConditionExpId, otherwise nested IF
                            // conditions inside the loop get merged with the loop predicate and are
                            // later stripped together with it.
                            blockStack.Push((blockType, parentCondition, blockConditionExpId, blockConditionExpId));
                            activeBlockConditionExpId = parentCondition;
                        }
                        else
                        {
                            var parentCondition = activeBlockConditionExpId;
                            var effectiveIfCondition = CombineConditions(parentCondition, blockConditionExpId);
                            blockStack.Push((blockType, parentCondition, blockConditionExpId, effectiveIfCondition));
                            activeBlockConditionExpId = effectiveIfCondition;
                        }
                        break;
                    }
                    case "END_BLK":
                    {
                        var endBlockDef = new TaskEndBlockDef(lineIndex, xmlTrace);
                        endBlocks.Add(endBlockDef);
                        if ((level == "R" || level == "T") && (type == "P" || type == "S"))
                            rowEndBlocks.Add(endBlockDef);
                        if (isFunctionLogic)
                            functionEndBlocks.Add(endBlockDef);
                        if (isHandlerLogic)
                            handlerEndBlocks.Add(endBlockDef);
                        if (blockStack.Count > 0)
                        {
                            var ended = blockStack.Pop();
                            activeBlockConditionExpId = ended.ParentCondition;
                        }
                        else
                        {
                            activeBlockConditionExpId = null;
                        }
                        break;
                    }
                    case "RaiseEvent":
                    {
                        var ev = first.Element("Event");
                        var raiseType = XmlHelpers.Attr(ev?.Element("EventType") ?? new XElement("x"), "val");
                        var raiseInternalEventId = ParseInt(ev?.Element("InternalEventID")?.Attribute("val")?.Value);
                        var raiseKeyCombinationId = ParseInt(ev?.Element("KeyCombinationID")?.Attribute("val")?.Value);
                        var raiseParent = ParseInt(ev?.Element("Parent")?.Attribute("val")?.Value);
                        var raiseComp = ParseInt(ev?.Element("PublicObject")?.Attribute("comp")?.Value);
                        var raiseObj = ev?.Element("PublicObject")?.Attribute("obj")?.Value;
                        var destinationContext = first.Element("DestinationContext")?.Attribute("val")?.Value;
                        var lineConditionExpId = ParseInt(first.Element("Condition")?.Attribute("Exp")?.Value);
                        var conditionExpId = CombineConditions(activeBlockConditionExpId, lineConditionExpId);
                        var modifier = XmlHelpers.Attr(first.Element("Modifier") ?? new XElement("x"), "val");
                        var direction = XmlHelpers.Attr(first.Element("Direction") ?? new XElement("x"), "val");
                        var waitForCompletion = ParseBool(first.Element("Wait")?.Attribute("val")?.Value);
                        var disabled = string.Equals(first.Element("Disabled")?.Attribute("val")?.Value, "Y", StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(first.Element("Disabled")?.Attribute("val")?.Value, "1", StringComparison.OrdinalIgnoreCase);
                        var raiseArgsRoot = first.Element("Arguments");
                        var argExprIds = new List<string>();
                        var argNodes = raiseArgsRoot?.Elements("Argument") ?? Enumerable.Empty<XElement>();
                        foreach (var a in argNodes)
                        {
                            var varVal = XmlHelpers.Attr(a.Element("Var") ?? new XElement("x"), "val");
                            if (!string.IsNullOrWhiteSpace(varVal))
                            {
                                argExprIds.Add(varVal);
                                continue;
                            }
                            var expId = ParseInt(a.Element("Expression")?.Attribute("val")?.Value);
                            if (expId.HasValue)
                                argExprIds.Add("EXP:" + expId.Value);
                        }
                        var raiseArgumentDefs = ParseArgumentDefs(raiseArgsRoot);
                        var raise = new TaskRaiseEventDef(raiseType, raiseInternalEventId, raiseKeyCombinationId, raiseParent, raiseComp, raiseObj, destinationContext, conditionExpId, modifier, direction, waitForCompletion, disabled, argExprIds, raiseArgumentDefs);
                        raises.Add(raise);
                        var allowedInRow = modifier == "B" || modifier == "S" || string.IsNullOrWhiteSpace(modifier);
                        if (allowedInRow && level == "T" && type == "P")
                            startRaises.Add(raise);
                        if (allowedInRow && level == "T" && type == "S")
                            endRaises.Add(raise);
                        break;
                    }
                    case "FormIO":
                    {
                        var op = XmlHelpers.Attr(first.Element("OperationType") ?? new XElement("x"), "val");
                        var page = XmlHelpers.Attr(first.Element("Page") ?? new XElement("x"), "val");
                        var delimiter = XmlHelpers.Attr(first.Element("Delimiter") ?? new XElement("x"), "val");
                        var delimiterChar = ParseInt(first.Element("DelimiterChar")?.Attribute("val")?.Value);
                        var ioDeviceIndex = ParseInt(first.Element("IODeviceInfo")?.Element("IoDeviceIndex")?.Attribute("val")?.Value);
                        var ioDeviceParent = ParseInt(first.Element("IODeviceInfo")?.Element("IoDeviceParent")?.Attribute("val")?.Value);
                        var formEntryIndex = ParseInt(first.Element("FormEntryInfo")?.Element("FormEntryIndex")?.Attribute("val")?.Value);
                        var lineConditionExpId = ParseInt(first.Element("Condition")?.Attribute("Exp")?.Value);
                        var conditionExpId = CombineConditions(activeBlockConditionExpId, lineConditionExpId);
                        var modifier = XmlHelpers.Attr(first.Element("Modifier") ?? new XElement("x"), "val");
                        var direction = XmlHelpers.Attr(first.Element("Direction") ?? new XElement("x"), "val");
                        var formIo = new TaskFormIoDef(level, type, reference, op, formEntryIndex, page, delimiter, delimiterChar, ioDeviceIndex, conditionExpId, CurrentLoopConditionExpressionId(), modifier, direction, ioDeviceParent, xmlTrace);
                        formIos.Add(formIo);
                        if (isHandlerLogic)
                            handlerFormIos.Add(formIo);
                        break;
                    }
                    default:
                    {
                        gaps.Add(new TaskGapDef(
                            Scope: "TaskLogic",
                            Message: $"LogicLine '{first.Name.LocalName}' not mapped",
                            XmlTrace: xmlTrace
                        ));
                        break;
                    }
                }
            }

            if (level == "R" && type == "P" && rowActions.Count > 0)
                rowLogics.Add(new TaskRowLogicDef(rowActions, rowBlocks.ToList(), rowEndBlocks.ToList()));
            if (level == "R" && type == "S" && rowActions.Count > 0)
                savingRowLogics.Add(new TaskRowLogicDef(rowActions, rowBlocks.ToList(), rowEndBlocks.ToList()));
            if (level == "T" && type == "P" && rowActions.Count > 0)
                startLogics.Add(new TaskRowLogicDef(rowActions, rowBlocks.ToList(), rowEndBlocks.ToList()));
            if (level == "T" && type == "S" && rowActions.Count > 0)
                endLogics.Add(new TaskRowLogicDef(rowActions, rowBlocks.ToList(), rowEndBlocks.ToList()));
            if (level == "G" && (type == "P" || type == "S") && rowActions.Count > 0)
                groupLogics.Add(new TaskGroupLogicDef(reference, type, rowActions));

            if (string.Equals(level, "F", StringComparison.OrdinalIgnoreCase))
            {
                var returnExpressionId = ParseInt(lu.Element("ReturnValueExpression")?.Attribute("val")?.Value);
                var luComment = lu.Element("Comment")?.Attribute("val")?.Value;
                functionOverrides.Add(new TaskFunctionOverrideDef(
                    Name: reference,
                    Scope: scope,
                    ReturnExpressionId: returnExpressionId,
                    Comment: luComment,
                    Parameters: functionParameters,
                    Remarks: remarks,
                    Actions: functionActions,
                    Blocks: functionBlocks.ToList(),
                    EndBlocks: functionEndBlocks.ToList(),
                    XmlTrace: $"Task=\"{taskDescription}\" LogicUnit[{luIndex}] EventType={eventType} Level={level} Type={type}"
                ));
            }

            if (level == "H" || level == "V" || level == "C" || type == "U" || eventType == "U" || eventType == "E")
            {
                handlers.Add(new TaskHandlerDef(
                    Level: level,
                    Type: type,
                    Scope: scope,
                    Propagate: propagate,
                    Reference: reference,
                    ConditionExpressionId: luConditionExpressionId,
                    EventType: eventType,
                    EventTime: eventTime,
                    EventInternalEventId: eventInternalEventId,
                    EventKeyCombinationId: keyCombinationId,
                    EventParent: eventParent,
                    EventPublicComponentId: eventComp,
                    EventPublicObject: eventObj,
                    EventExpression: eventExp,
                    ParameterColumnIds: handlerParameterColumnIds,
                    Raises: raises,
                    Invokes: invokes,
                    Calls: calls,
                    Updates: updates,
                    Stops: stops,
                    Remarks: remarks,
                    FormIos: handlerFormIos,
                    Actions: handlerActions,
                    Blocks: handlerBlocks.ToList(),
                    EndBlocks: handlerEndBlocks.ToList(),
                    XmlTrace: $"Task=\"{taskDescription}\" LogicUnit[{luIndex}] EventType={eventType} Level={level} Type={type}"
                ));
            }
        }

        return (primaryDbObj, selects, links, dataViewSources, blocks, endBlocks, endLinks, tabCalls, startLogics, startRaises, endLogics, endRaises, hasStartLogicUnit, hasEndLogicUnit, rowLogics, savingRowLogics, flowValidations, functionOverrides, handlers, groupLogics, gaps, formIos);
    }
    private static IReadOnlyList<TaskEventDef> ParseTaskEvents(XElement task)
    {
        var result = new List<TaskEventDef>();
        var evnts = task.Elements("EVNT").ToList();
        for (var i = 0; i < evnts.Count; i++)
        {
            var desc = XmlHelpers.Attr(evnts[i], "DESC", $"Event_{i + 1}");
            var eventNode = evnts[i].Element("Event");
            var eventType = eventNode?.Element("EventType")?.Attribute("val")?.Value;
            var internalEventId = ParseInt(eventNode?.Element("InternalEventID")?.Attribute("val")?.Value);
            var eventKeyCombinationId = ParseInt(eventNode?.Element("KeyCombinationID")?.Attribute("val")?.Value);
            var fieldId = ParseInt(eventNode?.Attribute("FieldID")?.Value);
            var forceExit = evnts[i].Attribute("FORCE_EXIT")?.Value
                            ?? eventNode?.Attribute("FORCE_EXIT")?.Value;
            var publicName = evnts[i].Attribute("Public")?.Value;
            var eventText = XmlHelpers.AttrUnicodeOrVal(eventNode?.Element("EventText") ?? new XElement("x"));
            var errorTrigger = XmlHelpers.Attr(eventNode?.Element("ErrorTrigger") ?? new XElement("x"), "val");
            var parameters = evnts[i]
                .Elements("EVENT_PARAMETER")
                .Select(p => new TaskEventParameterDef(
                    Name: XmlHelpers.Attr(p, "NAME", "Parameter"),
                    Attr: p.Attribute("ATTR")?.Value,
                    Picture: p.Attribute("PICT_U")?.Value))
                .ToList();
            result.Add(new TaskEventDef(i + 1, desc, eventType, internalEventId, eventKeyCombinationId, fieldId, forceExit, publicName, string.IsNullOrWhiteSpace(eventText) ? null : eventText, string.IsNullOrWhiteSpace(errorTrigger) ? null : errorTrigger, parameters));
        }
        return result;
    }

    private static TaskSqlWhereDef? ParseTaskSqlWhere(XElement? sqlWhereNode)
    {
        if (sqlWhereNode is null)
            return null;

        var format = new System.Text.StringBuilder();
        var args = new List<TaskSqlWhereArgumentDef>();
        foreach (var token in sqlWhereNode.Elements("TOKEN"))
        {
            var code = XmlHelpers.Attr(token.Element("CODE") ?? new XElement("x"), "val");
            switch (code)
            {
                case "3":
                    format.Append(XmlHelpers.Attr(token.Element("STR_U") ?? new XElement("x"), "val"));
                    break;
                case "1":
                case "2":
                {
                    var parentLevel = code == "2"
                        ? 0
                        : ParseInt(token.Element("Parent")?.Attribute("val")?.Value) ?? 0;
                    var variableOrdinal = ParseInt(token.Element("VAR_ISN")?.Attribute("val")?.Value);
                    if (!variableOrdinal.HasValue || variableOrdinal.Value <= 0)
                        return null;
                    format.Append("{").Append(args.Count).Append("}");
                    var contentAsIs = string.Equals(token.Element("SqlWhereFlags")?.Attribute("val")?.Value, "1", StringComparison.OrdinalIgnoreCase);
                    args.Add(new TaskSqlWhereArgumentDef(parentLevel, variableOrdinal.Value, contentAsIs));
                    break;
                }
                default:
                    return null;
            }
        }

        return format.Length == 0 ? null : new TaskSqlWhereDef(format.ToString(), args);
    }

    private static IReadOnlyList<TaskVarRangeInfoDef> ParseTaskVarRangeInfos(XElement task)
    {
        return task.Elements("VarRangeInfo")
            .Select(x => new TaskVarRangeInfoDef(
                Mode: x.Attribute("Mode")?.Value ?? "",
                VarRangeVeeIsn: ParseInt(x.Attribute("VarRangeVeeIsn")?.Value) ?? 0))
            .Where(x => x.VarRangeVeeIsn > 0)
            .ToList();
    }

    private static TaskSqlFormDef? ParseTaskSqlForm(XElement task)
    {
        var sqlForm = task.Element("SQL_FORM");
        if (sqlForm is null)
            return null;

        var statement = XmlHelpers.AttrUnicodeOrVal(sqlForm.Element("SQL_STMT_U") ?? new XElement("x"));
        if (string.IsNullOrWhiteSpace(statement))
            return null;

        var inputExpressionIds = (sqlForm.Element("INARG")?.Descendants("Argument") ?? Enumerable.Empty<XElement>())
            .Select(a => ParseInt(a.Element("Exp")?.Attribute("val")?.Value))
            .Where(x => x.HasValue && x.Value > 0)
            .Select(x => x!.Value)
            .ToList();

        var outputVariables = (sqlForm.Element("OUTARG")?.Descendants("Argument") ?? Enumerable.Empty<XElement>())
            .Select(a => a.Attribute("Var")?.Value ?? "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        return new TaskSqlFormDef(
            DatabaseName: sqlForm.Attribute("DB")?.Value,
            Restab: sqlForm.Attribute("RESTAB")?.Value,
            Statement: statement,
            InputExpressionIds: inputExpressionIds,
            OutputVariables: outputVariables);
    }

    private static IReadOnlyList<TaskExpressionDef> ParseTaskExpressions(XElement task)
    {
        var result = new List<TaskExpressionDef>();
        var exprs = task.Element("Expressions")?.Elements("Expression").ToList() ?? new List<XElement>();
        for (var i = 0; i < exprs.Count; i++)
        {
            var syntax = XmlHelpers.AttrUnicodeOrVal(exprs[i].Element("ExpSyntax") ?? new XElement("x"));
            var attr = XmlHelpers.AttrUnicodeOrVal(exprs[i].Element("ExpAttribute") ?? new XElement("x"));
            result.Add(new TaskExpressionDef(i + 1, syntax, attr));
        }
        return result;
    }

    private static string? ResolveModelRef(ParseContext ctx, IReadOnlyList<ParseContext>? contexts, XElement? element)
    {
        var resolved = ResolveComponentAwareRef(ctx, contexts, element, c => c.ModelLocalToGlobal, c => c.ModelPublicToGlobal, meta => meta.ModelById, meta => meta.ModelsByOrder);
        return resolved?.ToString();
    }

    private static int? ResolveModelRefInt(ParseContext ctx, IReadOnlyList<ParseContext>? contexts, XElement? element)
    {
        return ResolveComponentAwareRef(ctx, contexts, element, c => c.ModelLocalToGlobal, c => c.ModelPublicToGlobal, meta => meta.ModelById, meta => meta.ModelsByOrder);
    }

    private static int? ResolveDataObjectRef(ParseContext ctx, IReadOnlyList<ParseContext>? contexts, XElement? element)
    {
        return ResolveComponentAwareRef(ctx, contexts, element, c => c.DataObjectLocalToGlobal, c => c.DataObjectPublicToGlobal, meta => meta.DataObjectById, meta => meta.DataObjectsByOrder);
    }

    private static int? ResolveTaskRef(ParseContext ctx, IReadOnlyList<ParseContext> contexts, XElement? element, int? localTopLevelProgramIndex, string? operationType)
    {
        if (element is null)
            return null;

        if (int.TryParse(element.Attribute("comp")?.Value, out var compId) && compId > 0)
        {
            static int? ResolveLocalTaskFallback(ParseContext localCtx, int localObj)
            {
                if (localCtx.ProgramLocalTopLevelToGlobal.TryGetValue(localObj, out var mapped))
                    return mapped;
                return localObj;
            }

            if (!int.TryParse(element.Attribute("obj")?.Value, out var obj))
                return null;

            var targetCtx = contexts.FirstOrDefault(c => c.ComponentId == compId);
            if (targetCtx?.Metadata is null)
                return ResolveLocalTaskFallback(ctx, obj);

            var publicName =
                ResolveReferencedComponentProgramPublicName(ctx, compId, obj)
                ?? ResolvePublicName(targetCtx.Metadata, obj, targetCtx.Metadata.ProgramById, targetCtx.Metadata.ProgramsByOrder);
            if (string.IsNullOrWhiteSpace(publicName))
                return ResolveLocalTaskFallback(ctx, obj);

            if (targetCtx.ProgramPublicToGlobalTopLevel.TryGetValue(publicName, out var topLevel))
                return topLevel;
            return ResolveLocalTaskFallback(ctx, obj);
        }

        if (!int.TryParse(element.Attribute("obj")?.Value, out var localObj))
            return null;

        if (string.Equals(operationType, "T", StringComparison.OrdinalIgnoreCase))
            return localObj;

        if (ctx.ProgramLocalTopLevelToGlobal.TryGetValue(localObj, out var mapped))
            return mapped;

        return localObj;
    }

    private static string? ResolveTaskRefComponentName(ParseContext ctx, IReadOnlyList<ParseContext> contexts, XElement? element)
    {
        if (element is null)
            return null;

        if (!int.TryParse(element.Attribute("comp")?.Value, out var compId) || compId <= 0)
            return null;

        return contexts.FirstOrDefault(c => c.ComponentId == compId)?.Metadata?.Name;
    }

    private static string? ResolveTaskRefPublicName(ParseContext ctx, IReadOnlyList<ParseContext> contexts, XElement? element)
    {
        if (element is null)
            return null;

        if (!int.TryParse(element.Attribute("comp")?.Value, out var compId) || compId <= 0)
            return null;
        if (!int.TryParse(element.Attribute("obj")?.Value, out var objId))
            return null;

        var localPublicName = ResolveReferencedComponentProgramPublicName(ctx, compId, objId);
        if (!string.IsNullOrWhiteSpace(localPublicName))
            return localPublicName;

        var targetCtx = contexts.FirstOrDefault(c => c.ComponentId == compId);
        if (targetCtx?.Metadata is null)
            return null;

        return ResolvePublicName(targetCtx.Metadata, objId, targetCtx.Metadata.ProgramById, targetCtx.Metadata.ProgramsByOrder);
    }

    private static string? ResolveReferencedComponentProgramPublicName(ParseContext ctx, int compId, int objId)
    {
        var metadata = ResolveReferencedComponentMetadataFromCallingContext(ctx, compId);
        if (metadata is null)
            return null;

        // Cross-component TaskID obj values follow the caller's ComponentPrograms order.
        // Prefer the local repository ordering before falling back to explicit ids.
        return ResolvePublicNamePreferOrder(metadata, objId, metadata.ProgramById, metadata.ProgramsByOrder);
    }

    private static ComponentMetadata? ResolveReferencedComponentMetadataFromCallingContext(ParseContext ctx, int compId)
    {
        if (ctx.ReferencedComponentMetadataCache.TryGetValue(compId, out var cached))
            return cached;

        if (ctx.Document.Root is null)
            return null;

        var component = ctx.Document
            .Descendants("ComponentsRepository")
            .Descendants("Components")
            .Elements("Component")
            .ElementAtOrDefault(compId - 1);
        if (component is null)
            return null;

        var parsed = ParseComponentMetadata(component);
        ctx.ReferencedComponentMetadataCache[compId] = parsed;
        return parsed;
    }

    private static int? ResolveComponentAwareRef(
        ParseContext current,
        IReadOnlyList<ParseContext>? contexts,
        XElement? element,
        Func<ParseContext, Dictionary<int, int>> localMapSelector,
        Func<ParseContext, Dictionary<string, int>> publicMapSelector,
        Func<ComponentMetadata, Dictionary<int, string>> byIdSelector,
        Func<ComponentMetadata, List<string>> byOrderSelector)
    {
        if (element is null)
            return null;

        var localObj = ParseInt(element.Attribute("obj")?.Value);
        if (!localObj.HasValue)
            return null;

        var compId = ParseInt(element.Attribute("comp")?.Value);
        if (!compId.HasValue || compId.Value == 0 || compId.Value == current.ComponentId)
        {
            var localMap = localMapSelector(current);
            if (localMap.TryGetValue(localObj.Value, out var mappedLocal))
                return mappedLocal;
            return localObj;
        }

        if (contexts is null)
        {
            var localMapFallback = localMapSelector(current);
            if (localMapFallback.TryGetValue(localObj.Value, out var mappedLocalFallback))
                return mappedLocalFallback;
            return localObj;
        }

        var targetContext = contexts.FirstOrDefault(c => c.ComponentId == compId.Value);
        if (targetContext?.Metadata is null)
        {
            var localMapFallback = localMapSelector(current);
            if (localMapFallback.TryGetValue(localObj.Value, out var mappedLocalFallback))
                return mappedLocalFallback;
            return localObj;
        }

        var targetMetadata = targetContext.Metadata;
        var targetPublicName = ResolvePublicName(targetMetadata, localObj.Value, byIdSelector(targetMetadata), byOrderSelector(targetMetadata));
        if (string.IsNullOrWhiteSpace(targetPublicName))
        {
            var localMapFallback = localMapSelector(current);
            if (localMapFallback.TryGetValue(localObj.Value, out var mappedLocalFallback))
                return mappedLocalFallback;
            return localObj;
        }

        var targetPublicMap = publicMapSelector(targetContext);
        if (targetPublicMap.TryGetValue(targetPublicName, out var mappedTarget))
            return mappedTarget;
        var finalLocalMap = localMapSelector(current);
        if (finalLocalMap.TryGetValue(localObj.Value, out var finalMappedLocal))
            return finalMappedLocal;
        return localObj;
    }

    private static string? ResolvePublicName(
        ComponentMetadata metadata,
        int objId,
        Dictionary<int, string> byId,
        List<string> byOrder)
    {
        if (byId.TryGetValue(objId, out var byIdName))
            return byIdName;
        if (objId > 0 && objId <= byOrder.Count)
            return byOrder[objId - 1];
        return null;
    }

    private static string? ResolvePublicNamePreferOrder(
        ComponentMetadata metadata,
        int objId,
        Dictionary<int, string> byId,
        List<string> byOrder)
    {
        if (objId > 0 && objId <= byOrder.Count)
            return byOrder[objId - 1];
        if (byId.TryGetValue(objId, out var byIdName))
            return byIdName;
        return null;
    }

    private static int? ParseInt(string? value)
    {
        return int.TryParse(value, out var n) ? n : null;
    }

    private static bool? ParseBool(string? value)
    {
        return value switch
        {
            "Y" => true,
            "N" => false,
            _ => null
        };
    }
}

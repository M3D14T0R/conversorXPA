using System.Xml;

namespace XpaConverterMvp;

public static class XmlCatalogService
{
    public static XmlCatalog Load(string xmlPath)
    {
        var catalog = new XmlCatalog();
        if (string.IsNullOrWhiteSpace(xmlPath) || !File.Exists(xmlPath))
            return catalog;

        try
        {
            using var reader = CreateReader(xmlPath);
            ReadCatalog(reader, catalog);
            return catalog;
        }
        catch (XmlException)
        {
            var tempPath = XpaParser.CreateSanitizedXmlTempFile(xmlPath);
            try
            {
                using var reader = CreateReader(tempPath);
                ReadCatalog(reader, catalog);
                return catalog;
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
    }

    private static XmlReader CreateReader(string path)
    {
        return XmlReader.Create(path, new XmlReaderSettings
        {
            CheckCharacters = false,
            DtdProcessing = DtdProcessing.Parse,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        });
    }

    private static void ReadCatalog(XmlReader reader, XmlCatalog catalog)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new Dictionary<string, XmlCatalogReference>(StringComparer.OrdinalIgnoreCase);

        var insideProgramsRepository = false;
        var insideComponentsRepository = false;
        var taskDepth = 0;
        var currentTopLevelTask = new CatalogTask();

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "ComponentsRepository")
                {
                    insideComponentsRepository = true;
                    continue;
                }

                if (reader.Name == "ProgramsRepository")
                {
                    insideProgramsRepository = true;
                    continue;
                }

                if (insideComponentsRepository && reader.Name == "Component")
                {
                    var raw = XpaParser.SanitizeXmlFragment(reader.ReadOuterXml());
                    var component = System.Xml.Linq.XElement.Parse(raw, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                    var componentType = component.Element("ComponentType")?.Attribute("val")?.Value;
                    var name = component.Attribute("name")?.Value
                               ?? component.Element("Name")?.Attribute("val")?.Value;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    if (string.Equals(componentType, "Magic xpa", StringComparison.OrdinalIgnoreCase))
                    {
                        components.Add(name);
                        references[name] = new XmlCatalogReference(name, "XPA", null);
                    }
                    else if (string.Equals(componentType, ".NET", StringComparison.OrdinalIgnoreCase))
                    {
                        var assemblyName = component.Element("AssemblyName")?.Attribute("val")?.Value;
                        var assemblyPath = component.Element("AssemblyPath")?.Attribute("val")?.Value;
                        var xmlHint = !string.IsNullOrWhiteSpace(assemblyPath) ? assemblyPath : assemblyName;
                        references[name] = new XmlCatalogReference(name, ".NET/DLL", xmlHint);
                    }
                    continue;
                }

                if (!insideProgramsRepository)
                    continue;

                if (reader.Name == "Task")
                {
                    taskDepth++;
                    if (taskDepth == 1)
                    {
                        currentTopLevelTask = new CatalogTask
                        {
                            MainProgram = string.Equals(reader.GetAttribute("MainProgram"), "Y", StringComparison.OrdinalIgnoreCase)
                        };
                    }
                    continue;
                }

                if (taskDepth == 1 && reader.Name == "Header")
                {
                    currentTopLevelTask.Description = reader.GetAttribute("Description");
                    currentTopLevelTask.Folder = reader.GetAttribute("Folder");
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.EndElement)
                continue;

            if (reader.Name == "Task")
            {
                if (taskDepth == 1 && !currentTopLevelTask.MainProgram)
                {
                    if (!string.IsNullOrWhiteSpace(currentTopLevelTask.Description))
                        tasks.Add(currentTopLevelTask.Description);
                    if (!string.IsNullOrWhiteSpace(currentTopLevelTask.Folder))
                        folders.Add(currentTopLevelTask.Folder);
                }

                if (taskDepth > 0)
                    taskDepth--;

                continue;
            }

            if (reader.Name == "ProgramsRepository")
            {
                insideProgramsRepository = false;
                continue;
            }

            if (reader.Name == "ComponentsRepository")
                insideComponentsRepository = false;
        }

        foreach (var folder in folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            catalog.Folders.Add(folder);

        foreach (var task in tasks.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            catalog.Tasks.Add(task);

        foreach (var component in components.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            catalog.Components.Add(component);

        foreach (var reference in references.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            catalog.References.Add(reference);
    }

    private sealed class CatalogTask
    {
        public string? Description { get; set; }
        public string? Folder { get; set; }
        public bool MainProgram { get; set; }
    }
}

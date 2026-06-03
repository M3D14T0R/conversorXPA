using System;
using System.IO;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool UsesXmlCompat(ProjectSemantic parsed)
    {
        string[] xmlCompatFunctions =
        [
            "XMLBlobGet",
            "DbXmlMixedGet"
        ];

        foreach (var task in parsed.Tasks)
        {
            foreach (var expression in task.Expressions)
            {
                var syntax = expression.Syntax ?? "";
                if (xmlCompatFunctions.Any(fn => syntax.IndexOf(fn, StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            }
        }

        return false;
    }

    private static bool UsesJavaCompat(ProjectSemantic parsed)
    {
        string[] javaCompatFunctions =
        [
            "JSet(",
            "JSetStatic(",
            "JGetStatic(",
            "JInstanceOf("
        ];

        foreach (var task in parsed.Tasks)
        {
            foreach (var expression in task.Expressions)
            {
                var syntax = expression.Syntax ?? "";
                if (javaCompatFunctions.Any(fn => syntax.IndexOf(fn, StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            }
        }

        return false;
    }

    private static bool UsesHandlingGuiCompat(ProjectSemantic parsed)
    {
        foreach (var task in parsed.Tasks)
        {
            foreach (var expression in task.Expressions)
            {
                var syntax = expression.Syntax ?? "";
                if (syntax.IndexOf("BrowserScriptExecute", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }

        return false;
    }

    private static void WriteJsonCompatAsset(string outputRoot, string appNamespace)
    {
        File.WriteAllText(Path.Combine(outputRoot, "JsonCompat.cs"), $@"global using static {appNamespace}.JsonCompat;

using System;
using System.Collections.Generic;
using System.Linq;
using ENV.Data;
using XPARuntimeCore.Box;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace {appNamespace};

internal static class JsonCompat
{{
    sealed class JsonSourceState
    {{
        public JToken Root;
        public Func<byte[]> Read;
        public Action<byte[]> Write;
    }}

    public static Number JSONInsert(object source, Text path, Text key, object value)
    {{
        if (!TryLoad(source, out var state, out var error))
            return error;
        if (!TryResolveParentForInsert(state.Root, path, out var parent, out var terminal, out error))
            return error;

        var valueToken = ParseValueToken(value);
        var keyText = (key ?? """").ToString().Trim();

        if (parent is JArray parentArray)
        {{
            if (!string.IsNullOrEmpty(keyText))
                return -13;

            if (terminal.Index.HasValue)
            {{
                if (terminal.Index.Value < 1 || terminal.Index.Value > parentArray.Count + 1)
                    return -14;
                parentArray.Insert(terminal.Index.Value - 1, valueToken);
            }}
            else
            {{
                parentArray.Add(valueToken);
            }}
        }}
        else if (parent is JObject parentObject)
        {{
            if (terminal.Index.HasValue)
                return -12;

            if (string.IsNullOrEmpty(keyText))
            {{
                if (valueToken is JObject valueObject)
                    parentObject[terminal.Name] = valueObject;
                else
                    return -14;
            }}
            else
            {{
                var targetObject = ResolveObjectTarget(parentObject, terminal.Name);
                if (targetObject == null)
                    return -1;
                if (targetObject[keyText] != null)
                    return -3;
                targetObject[keyText] = valueToken;
            }}
        }}
        else
        {{
            return -1;
        }}

        Save(state);
        return 0;
    }}

    public static Number JSONModify(object source, Text path, object value)
    {{
        if (!TryLoad(source, out var state, out var error))
            return error;
        if (!TryResolveTarget(state.Root, path, out var target, out error))
            return error;

        if (target.Parent is null)
            return -10;

        var valueToken = ParseValueToken(value);
        if (target.Parent is JProperty prop)
            prop.Value = valueToken;
        else if (target.Parent is JArray arr)
            arr[target.Index.Value] = valueToken;
        else
            return -1;

        Save(state);
        return 0;
    }}

    public static Number JSONDelete(object source, Text path)
    {{
        if (!TryLoad(source, out var state, out var error))
            return error;
        if (!TryResolveTarget(state.Root, path, out var target, out error))
            return error;

        if (target.Parent is JProperty prop)
            prop.Remove();
        else if (target.Parent is JArray arr)
            arr.RemoveAt(target.Index.Value);
        else
            return -10;

        Save(state);
        return 0;
    }}

    public static Bool JSONExist(object source, Text path)
    {{
        if (!TryLoad(source, out var state, out _))
            return false;
        return TryResolveTarget(state.Root, path, out _, out _);
    }}

    public static Number JSONFind(object source, Text path, Text key, object value)
    {{
        return JSONFind(source, path, key, value, 0);
    }}

    public static Number JSONFind(object source, Text path, Text key, object value, Number beginAt)
    {{
        if (!TryLoad(source, out var state, out var error))
            return error;
        if (!TryResolveTarget(state.Root, path, out var target, out error))
            return error;
        if (target.Token is not JArray arr)
            return -10;

        var keyText = (key ?? """").ToString().Trim();
        var valueToken = ParseValueToken(value);
        var startIndex = Math.Max(0, ((int?)beginAt ?? 0) - 1);

        for (var i = startIndex; i < arr.Count; i++)
        {{
            var item = arr[i];
            if (item is JObject obj)
            {{
                if (obj[keyText] is JToken existing && JToken.DeepEquals(existing, valueToken))
                    return i + 1;
            }}
            else if (JToken.DeepEquals(item, valueToken))
            {{
                return i + 1;
            }}
        }}

        return -11;
    }}

    public static Number JSONCnt(object source, Text path)
    {{
        if (!TryLoad(source, out var state, out var error))
            return error;
        if (!TryResolveTarget(state.Root, path, out var target, out error))
            return error;
        if (target.Token is JArray arr)
            return arr.Count;
        return -10;
    }}

    public static Text JSONGet(object source, Text path)
    {{
        if (!TryLoad(source, out var state, out _))
            return """";
        if (!TryResolveTarget(state.Root, path, out var target, out _))
            return """";

        switch (target.Token.Type)
        {{
            case JTokenType.Object:
            case JTokenType.Array:
                return target.Token.ToString(Formatting.None);
            case JTokenType.Null:
                return """";
            default:
                return target.Token.ToString();
        }}
    }}

    static bool TryLoad(object source, out JsonSourceState state, out Number error)
    {{
        state = null;
        error = 0;

        if (source is string missing && missing.Contains(""???""))
        {{
            error = -6;
            return false;
        }}

        if (source is Text || source is TextColumn)
        {{
            error = -7;
            return false;
        }}

        byte[] bytes = null;
        Action<byte[]> writer = _ => {{ }};

        if (source is ByteArrayColumn byteArrayColumn)
        {{
            bytes = byteArrayColumn.Value;
            writer = b => byteArrayColumn.Value = b;
        }}
        else if (source is byte[] byteArray)
        {{
            bytes = byteArray;
        }}
        else
        {{
            error = -7;
            return false;
        }}

        if (bytes == null)
        {{
            error = -8;
            return false;
        }}

        try
        {{
            var jsonText = System.Text.Encoding.UTF8.GetString(bytes);
            if (string.IsNullOrWhiteSpace(jsonText))
            {{
                error = -8;
                return false;
            }}

            state = new JsonSourceState
            {{
                Root = JToken.Parse(jsonText),
                Read = () => bytes,
                Write = writer
            }};
            return true;
        }}
        catch
        {{
            error = -5;
            return false;
        }}
    }}

    static void Save(JsonSourceState state)
    {{
        var bytes = System.Text.Encoding.UTF8.GetBytes(state.Root.ToString(Formatting.Indented));
        state.Write(bytes);
    }}

    sealed class PathSegment
    {{
        public string Name;
        public int? Index;
        public bool ArrayWithoutIndex;
    }}

    sealed class ResolvedToken
    {{
        public JToken Token;
        public JContainer Parent;
        public int? Index;
    }}

    static bool TryResolveTarget(JToken root, Text path, out ResolvedToken result, out Number error)
    {{
        result = null;
        error = 0;
        var segments = ParsePath(path);
        if (segments.Count == 0)
        {{
            error = -1;
            return false;
        }}

        JToken current = root;
        JContainer parent = null;
        int? indexInParent = null;

        foreach (var segment in segments)
        {{
            if (segment.ArrayWithoutIndex)
            {{
                if (current is not JObject objForArray || !(objForArray[segment.Name] is JArray arrayWithoutIndex))
                {{
                    error = -9;
                    return false;
                }}

                parent = objForArray;
                current = arrayWithoutIndex;
                indexInParent = null;
                continue;
            }}

            if (current is JObject obj)
            {{
                if (obj[segment.Name] == null)
                {{
                    error = -1;
                    return false;
                }}

                parent = obj;
                current = obj[segment.Name];
                indexInParent = null;
            }}
            else
            {{
                error = -10;
                return false;
            }}

            if (segment.Index.HasValue)
            {{
                if (current is not JArray arr)
                {{
                    error = -12;
                    return false;
                }}

                var idx = segment.Index.Value - 1;
                if (idx < 0 || idx >= arr.Count)
                {{
                    error = -4;
                    return false;
                }}

                parent = arr;
                current = arr[idx];
                indexInParent = idx;
            }}
        }}

        result = new ResolvedToken {{ Token = current, Parent = parent, Index = indexInParent }};
        return true;
    }}

    static bool TryResolveParentForInsert(JToken root, Text path, out JToken parent, out PathSegment terminal, out Number error)
    {{
        parent = null;
        terminal = null;
        error = 0;
        var segments = ParsePath(path);
        if (segments.Count == 0)
        {{
            error = -1;
            return false;
        }}

        if (segments.Count == 1)
        {{
            parent = root;
            terminal = segments[0];
            return true;
        }}

        var parentPath = segments.Take(segments.Count - 1).ToList();
        if (!TryResolveSegments(root, parentPath, out parent, out error))
            return false;

        terminal = segments[segments.Count - 1];
        return true;
    }}

    static bool TryResolveSegments(JToken root, IList<PathSegment> segments, out JToken current, out Number error)
    {{
        current = root;
        error = 0;
        foreach (var segment in segments)
        {{
            if (segment.ArrayWithoutIndex)
            {{
                if (current is not JObject obj || !(obj[segment.Name] is JArray arrNoIndex))
                {{
                    error = -9;
                    return false;
                }}

                current = arrNoIndex;
                continue;
            }}

            if (current is not JObject obj2 || obj2[segment.Name] == null)
            {{
                error = -1;
                return false;
            }}

            current = obj2[segment.Name];
            if (segment.Index.HasValue)
            {{
                if (current is not JArray arr)
                {{
                    error = -12;
                    return false;
                }}

                var idx = segment.Index.Value - 1;
                if (idx < 0 || idx >= arr.Count)
                {{
                    error = -4;
                    return false;
                }}

                current = arr[idx];
            }}
        }}

        return true;
    }}

    static JObject ResolveObjectTarget(JObject parentObject, string propertyName)
    {{
        if (string.IsNullOrEmpty(propertyName))
            return parentObject;

        if (parentObject[propertyName] == null)
        {{
            var obj = new JObject();
            parentObject[propertyName] = obj;
            return obj;
        }}

        return parentObject[propertyName] as JObject;
    }}

    static JToken ParseValueToken(object value)
    {{
        if (value is ByteArrayColumn col)
            value = col.Value == null ? null : System.Text.Encoding.UTF8.GetString(col.Value);

        if (value is byte[] bytes)
            value = bytes == null ? null : System.Text.Encoding.UTF8.GetString(bytes);

        var text = value?.ToString() ?? """";
        if (string.IsNullOrWhiteSpace(text))
            return JValue.CreateString("""");

        try
        {{
            return JToken.Parse(text);
        }}
        catch
        {{
            return new JValue(text);
        }}
    }}

    static List<PathSegment> ParsePath(Text path)
    {{
        var value = path?.ToString() ?? """";
        return value.Split(new[] {{ '/' }}, StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseSegment)
            .ToList();
    }}

    static PathSegment ParseSegment(string segment)
    {{
        var result = new PathSegment {{ Name = segment }};
        var bracket = segment.IndexOf('[');
        if (bracket < 0 || !segment.EndsWith(""]""))
            return result;

        result.Name = segment.Substring(0, bracket);
        var indexPart = segment.Substring(bracket + 1, segment.Length - bracket - 2);
        if (indexPart.Length == 0)
        {{
            result.ArrayWithoutIndex = true;
            return result;
        }}

        if (int.TryParse(indexPart, out var index))
            result.Index = index;

        return result;
    }}
}}
", Encoding.UTF8);
    }

}


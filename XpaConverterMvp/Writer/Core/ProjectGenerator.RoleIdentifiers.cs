using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ToRoleMemberIdentifier(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Role_";
        var sb = new StringBuilder();
        foreach (var ch in raw)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        var id = sb.ToString();
        if (char.IsDigit(id[0]))
            id = "_" + id;
        return id;
    }
}


using System.IO;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteAsyncHelperBaseSkeleton(string outputRoot, string appNamespace)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"namespace {appNamespace};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>AsyncHelperBase</summary>");
        sb.AppendLine("public abstract class AsyncHelperBase : ENV.AsyncHelperBase");
        sb.AppendLine("{");
        sb.AppendLine("    internal AsyncHelperBase() : base(ApplicationClassType)");
        sb.AppendLine("    {");
        sb.AppendLine("    }");
        sb.AppendLine("    public static System.Type ApplicationClassType;");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(outputRoot, "AsyncHelperBase.cs"), sb.ToString());
    }

    private static void WriteBusinessProcessBaseSkeleton(string outputRoot, string appNamespace)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"namespace {appNamespace};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>BusinessProcessBase</summary>");
        sb.AppendLine("public abstract class BusinessProcessBase : ENV.BusinessProcessBase");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Application that will be used by all inheriting classes</summary>");
        sb.AppendLine($"    internal readonly Application Application = {appNamespace}.Application.Instance;");
        sb.AppendLine("    internal BusinessProcessBase()");
        sb.AppendLine("    {");
        sb.AppendLine("        setApplication(Application);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(outputRoot, "BusinessProcessBase.cs"), sb.ToString());
    }

}


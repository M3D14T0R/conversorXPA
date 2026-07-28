using System.IO;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteSharedXpaSqlStorage(string sharedDir, string appNamespace)
    {
        var source = $$"""
using System;
using System.Globalization;
using System.Linq;
using XPARuntimeCore.Box;
using XPARuntimeCore.Box.Data.DataProvider;

namespace {{appNamespace}}.Shared;

/// <summary>
/// Preserves XPA dates when a single logical date definition is backed by
/// heterogeneous SQL columns (date, char and legacy numeric columns).
/// </summary>
public sealed class XpaSqlDateStorage : IColumnStorageSrategy<Date>
{
    /// <summary>
    /// XPA SQL forms commonly share an NVL/ISNULL suffix between database
    /// engines. SQL Server cannot compare a date column with the numeric zero
    /// and cannot convert an int directly to date. Preserve the source branch
    /// while using the SQL Server zero-date equivalent as a typed literal.
    /// </summary>
    public static Text NormalizeDynamicSqlDateNullFallback(object value)
    {
        var text = value?.ToString() ?? "";
        return string.Equals(text.Trim(), ",0)", StringComparison.OrdinalIgnoreCase)
            ? (Text)",CONVERT(date,'19000101',112))"
            : (Text)text;
    }

    /// <summary>
    /// Produces the null suffix for an XPA time column according to the
    /// database-specific null function already selected by the SQL form.
    /// SQL Server time columns require a typed midnight value; the legacy
    /// numeric zero remains unchanged for the non-SQL Server branch.
    /// </summary>
    public static Text ResolveDynamicSqlTimeNullFallbackSuffix(object nullFunctionPrefix)
    {
        var prefix = nullFunctionPrefix?.ToString()?.Trim() ?? "";
        return prefix.StartsWith("isnull(", StringComparison.OrdinalIgnoreCase)
            ? (Text)",CONVERT(time,'00:00:00'))"
            : (Text)",0)";
    }

    public Date LoadFrom(IValueLoader loader)
    {
        if (loader.IsNull())
            return null;

        try
        {
            var value = Date.FromDateTime(loader.GetDateTime());
            return value == new Date(1, 1, 16) ? Date.Empty : value;
        }
        catch
        {
        }

        try
        {
            var text = (loader.GetString() ?? "").Trim();
            if (text.Length == 0 || text == "0" || text == "00000000")
                return Date.Empty;

            var digits = new string(text.Where(char.IsDigit).ToArray());
            if (digits.Length == 8)
                return Date.Parse(digits, "YYYYMMDD");

            if (DateTime.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var parsed))
            {
                return Date.FromDateTime(parsed);
            }
        }
        catch
        {
        }

        try
        {
            return ENV.UserMethods.Instance.ToDate(loader.GetNumber());
        }
        catch
        {
            return ENV.Data.DateColumn.ErrorDate;
        }
    }

    public void SaveTo(Date value, IValueSaver saver)
    {
        if (value == null || value <= Date.Empty)
        {
            saver.SaveNull();
            return;
        }

        saver.SaveAnsiString(Date.ToString(value, "YYYYMMDD"), 8, true);
    }
}
""";

        File.WriteAllText(Path.Combine(sharedDir, "XpaSqlStorage.cs"), source);
    }
}

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

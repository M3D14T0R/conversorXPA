using System;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string? ResolveMenuImageResource(int? internalEventId, bool isSecondaryPulldownOrContext)
    {
        if (!internalEventId.HasValue)
            return null;
        var known = internalEventId.Value switch
        {
            11 => "Cut",
            23 => "UpdateMode",
            24 => "InsertMode",
            25 => "BrowseMode",
            122 => "Find",
            123 => "Filter",
            124 => "SelectSort",
            125 => "CustomSort",
            165 => "FindNext",
            180 => "Undo",
            239 => "Copy",
            240 => "Paste",
            162 => "Printer",
            64 => "Previous",
            65 => "Next",
            66 => "PageUp",
            67 => "PageDown",
            72 => "First",
            73 => "Last",
            156 => "Help",
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(known))
            return known;
        if (!isSecondaryPulldownOrContext)
            return null;
        return internalEventId.Value switch
        {
            34 or 35 or 36 or 37 or 77 or 160 or 209 or 286 or 370 or 462 or 463 => "UnknownImage",
            _ => null
        };
    }
}


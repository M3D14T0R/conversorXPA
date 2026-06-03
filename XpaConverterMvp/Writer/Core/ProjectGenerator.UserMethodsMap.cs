using System;
using System.Collections.Generic;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static Dictionary<string, string> GetUserMethodsPublicNameMap()
    {
        if (_userMethodsPublicNameMap is not null)
            return _userMethodsPublicNameMap;
        _ = GetUserMethodsPublicNames();
        return _userMethodsPublicNameMap ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}


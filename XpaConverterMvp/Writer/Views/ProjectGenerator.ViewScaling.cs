using System;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static int ScaleViewX(int value) => (int)Math.Round(value * 5d / 4d, MidpointRounding.AwayFromZero);

    private static int ScaleViewY(int value) => (int)Math.Round(value * 13d / 8d, MidpointRounding.AwayFromZero);
}

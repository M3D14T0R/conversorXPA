using System;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private readonly record struct ViewScale(double FactorX, double FactorY);

    private static ViewScale ResolveViewScale(TaskFormDef? form)
    {
        var units = int.TryParse(form?.FormUnits, out var parsedUnits) ? parsedUnits : 1;
        var horizontal = Math.Max(1, form?.HorizontalFactor ?? 4);
        var vertical = Math.Max(1, form?.VerticalFactor ?? 8);

        if (units == 2)
            return new ViewScale(96d / 2.54d / horizontal, 96d / 2.54d / vertical);
        if (units == 3)
            return new ViewScale(96d / horizontal, 96d / vertical);

        // In ordinary XPA GUI forms HorizontalFactor/VerticalFactor describe
        // expression coordinates; they are not the divisor for the controls'
        // stored rectangle. The WinForms-compatible conversion uses the same
        // 4x8 character grid and 5x13 pixel scale as the runtime.
        return new ViewScale(5d / 4d, 13d / 8d);
    }

    private static int ScaleViewXForForm(int value, TaskFormDef? form)
        => (int)Math.Round(value * ResolveViewScale(form).FactorX, MidpointRounding.AwayFromZero);

    private static int ScaleViewYForForm(int value, TaskFormDef? form)
        => (int)Math.Round(value * ResolveViewScale(form).FactorY, MidpointRounding.AwayFromZero);
}

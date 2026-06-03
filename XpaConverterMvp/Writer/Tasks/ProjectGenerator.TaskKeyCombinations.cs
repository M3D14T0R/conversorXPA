namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveKeyCombination(int? keyCombinationId)
    {
        return keyCombinationId switch
        {
            8 => "(System.Windows.Forms.Keys.Control|System.Windows.Forms.Keys.Space)",
            11 => "(System.Windows.Forms.Keys.Control|System.Windows.Forms.Keys.D)",
            12 => "(System.Windows.Forms.Keys.Control|System.Windows.Forms.Keys.E)",
            21 => "(System.Windows.Forms.Keys.Control|System.Windows.Forms.Keys.N)",
            23 => "(System.Windows.Forms.Keys.Control|System.Windows.Forms.Keys.P)",
            27 => "(System.Windows.Forms.Keys.Control|System.Windows.Forms.Keys.T)",
            30 => "(System.Windows.Forms.Keys.Control|System.Windows.Forms.Keys.W)",
            82 => "System.Windows.Forms.Keys.F3",
            83 => "System.Windows.Forms.Keys.F4",
            84 => "System.Windows.Forms.Keys.F5",
            86 => "System.Windows.Forms.Keys.F7",
            88 => "System.Windows.Forms.Keys.F9",
            104 => "(System.Windows.Forms.Keys.Control|System.Windows.Forms.Keys.F5)",
            109 => "(System.Windows.Forms.Keys.Control|System.Windows.Forms.Keys.F10)",
            165 => "(System.Windows.Forms.Keys.Control|System.Windows.Forms.Keys.A)",
            189 => "System.Windows.Forms.Keys.F12",
            _ => ""
        };
    }
}


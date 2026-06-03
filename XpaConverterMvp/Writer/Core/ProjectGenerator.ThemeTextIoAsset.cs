using System.IO;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteThemeTextIo(string textIoDir, string appNamespace)
    {
        File.WriteAllText(Path.Combine(textIoDir, "TextLayout.cs"), $@"namespace {appNamespace}.Shared.Theme.TextIO
{{
    public partial class TextLayout : ENV.IO.TextLayout
    {{
        public TextLayout()
        {{
            SectionType = typeof(TextSection);
            InitializeComponent();
        }}
        public TextLayout(ENV.BusinessProcessBase controller) : base(controller)
        {{
            InitializeComponent();
        }}
        public TextLayout(ENV.AbstractUIController controller) : base(controller)
        {{
            InitializeComponent();
        }}
        public TextLayout(ENV.ApplicationControllerBase controller) : base(controller)
        {{
            InitializeComponent();
        }}
    }}
}}
");
        File.WriteAllText(Path.Combine(textIoDir, "TextLayout.Designer.cs"), $@"namespace {appNamespace}.Shared.Theme.TextIO
{{
    partial class TextLayout
    {{
        void InitializeComponent()
        {{
        }}
    }}
}}
");
        File.WriteAllText(Path.Combine(textIoDir, "TextSection.cs"), $@"namespace {appNamespace}.Shared.Theme.TextIO
{{
    public partial class TextSection : ENV.IO.TextSection
    {{
        public TextSection()
        {{
            DefaultLabelType = typeof(TextLabel);
            DefaultTextBoxType = typeof(TextBox);
            InitializeComponent();
        }}
    }}
}}
");
        File.WriteAllText(Path.Combine(textIoDir, "TextSection.Designer.cs"), $@"namespace {appNamespace}.Shared.Theme.TextIO
{{
    partial class TextSection
    {{
        void InitializeComponent()
        {{
            WidthInChars = 40;
            HeightInChars = 18;
        }}
    }}
}}
");
        File.WriteAllText(Path.Combine(textIoDir, "TextBox.cs"), $@"namespace {appNamespace}.Shared.Theme.TextIO
{{
    public class TextBox : ENV.IO.TextBox
    {{
        public TextBox()
        {{
            Alignment = System.Drawing.ContentAlignment.TopLeft;
            HeightInChars = 1;
        }}
    }}
}}
");
        File.WriteAllText(Path.Combine(textIoDir, "TextLabel.cs"), $@"namespace {appNamespace}.Shared.Theme.TextIO
{{
    public class TextLabel : ENV.IO.TextLabel
    {{
        public TextLabel()
        {{
            Alignment = System.Drawing.ContentAlignment.MiddleLeft;
        }}
    }}
}}
");
        File.WriteAllText(Path.Combine(textIoDir, "Line.cs"), $@"namespace {appNamespace}.Shared.Theme.TextIO
{{
    public class Line : ENV.IO.Line
    {{
    }}
}}
");
        File.WriteAllText(Path.Combine(textIoDir, "Shape.cs"), $@"namespace {appNamespace}.Shared.Theme.TextIO
{{
    public class Shape : ENV.IO.Shape
    {{
    }}
}}
");
        File.WriteAllText(Path.Combine(textIoDir, "TextTemplate.cs"), $@"namespace {appNamespace}.Shared.Theme.TextIO
{{
    public class TextTemplate : ENV.IO.TextTemplate
    {{
        public TextTemplate(string templateFileName) : base(templateFileName)
        {{
        }}
    }}
}}
");
    }



}


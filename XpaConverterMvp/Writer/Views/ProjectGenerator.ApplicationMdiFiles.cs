using System.IO;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteApplicationMdiFiles(string viewsDir, string appNamespace, string appRoot)
    {
        var mdi = new StringBuilder();
        mdi.AppendLine("using ENV;");
        mdi.AppendLine("using XPARuntimeCore.Box;");
        mdi.AppendLine("using System;");
        mdi.AppendLine($"namespace {appNamespace}.Views");
        mdi.AppendLine("{");
        mdi.AppendLine("    partial class ApplicationMdi : System.Windows.Forms.Form, IHaveAMenu");
        mdi.AppendLine("    {");
        mdi.AppendLine("        MenuManager _menuManager;");
        mdi.AppendLine("        public override System.Drawing.Size MinimumSize { get { return Common.GetMDIMinimumSize(this, base.MinimumSize); } set { base.MinimumSize = value; } }");
        mdi.AppendLine("        internal System.Windows.Forms.ContextMenuStrip _optionsContextMenuStrip;");
        mdi.AppendLine("        public ApplicationMdi()");
        mdi.AppendLine("        {");
        mdi.AppendLine("            Icon = Common.DefaultIcon;");
        mdi.AppendLine("            InitializeComponent();");
        mdi.AppendLine("            _menuManager = new MenuManager(this, new ContextMenuMap(components));");
        mdi.AppendLine("            Common.MDILoad(this);");
        mdi.AppendLine("            new ENV.UI.Menus.DeveloperToolsMenu(Application.Instance, typeof(Shared.DataSources), Roles.UserManager).AddToContextMenu(_menuManager, _optionsContextMenuStrip);");
        mdi.AppendLine("        }");
        mdi.AppendLine("        public void DoOnMenu(Action<MenuManager> action)");
        mdi.AppendLine("        {");
        mdi.AppendLine("            action(_menuManager);");
        mdi.AppendLine("        }");
        mdi.AppendLine("        [System.Diagnostics.DebuggerStepThrough]");
        mdi.AppendLine("        protected override void WndProc(ref System.Windows.Forms.Message m)");
        mdi.AppendLine("        {");
        mdi.AppendLine("            Common.ProcessMDIMessage(this, m);");
        mdi.AppendLine("            base.WndProc(ref m);");
        mdi.AppendLine("            Application.Instance.RunRequestedStartProgram();");
        mdi.AppendLine("            Common.ProcessMDIMessageAfterMDI(this, m, SizeFromClientSize);");
        mdi.AppendLine("        }");
        mdi.AppendLine("        protected override void OnClosed(EventArgs e)");
        mdi.AppendLine("        {");
        mdi.AppendLine("            Common.MDIClose(this);");
        mdi.AppendLine("            base.OnClosed(e);");
        mdi.AppendLine("        }");
        mdi.AppendLine("        protected override void ScaleControl(System.Drawing.SizeF factor, System.Windows.Forms.BoundsSpecified specified)");
        mdi.AppendLine("        {");
        mdi.AppendLine("            Common.MDIScale(this, factor);");
        mdi.AppendLine("            base.ScaleControl(factor, specified);");
        mdi.AppendLine("        }");
        mdi.AppendLine("        void expandStatusLabel_Click(object sender, EventArgs e)");
        mdi.AppendLine("        {");
        mdi.AppendLine("            _menuManager.RunOnActiveContext(() => _menuManager.Raise(Command.Expand));");
        mdi.AppendLine("        }");
        mdi.AppendLine("        void expandTextBoxStatusLabel_Click(object sender, EventArgs e)");
        mdi.AppendLine("        {");
        mdi.AppendLine("            _menuManager.RunOnActiveContext(() => _menuManager.Raise(Command.ExpandTextBox));");
        mdi.AppendLine("        }");
        mdi.AppendLine("    }");
        mdi.AppendLine("}");
        File.WriteAllText(Path.Combine(viewsDir, "ApplicationMdi.cs"), mdi.ToString());

        var designer = new StringBuilder();
        designer.AppendLine("using ENV;");
        designer.AppendLine("using XPARuntimeCore.Box;");
        designer.AppendLine("using System;");
        designer.AppendLine($"namespace {appNamespace}.Views");
        designer.AppendLine("{");
        designer.AppendLine("    partial class ApplicationMdi");
        designer.AppendLine("    {");
        designer.AppendLine("        System.ComponentModel.IContainer components;");
        designer.AppendLine("        System.Windows.Forms.StatusStrip StatusStrip;");
        designer.AppendLine("        internal System.Windows.Forms.ToolStripStatusLabel mainStatusLabel;");
        designer.AppendLine("        internal System.Windows.Forms.ToolStripStatusLabel userStatusLabel;");
        designer.AppendLine("        internal System.Windows.Forms.ToolStripStatusLabel versionStatusLabel;");
        designer.AppendLine("        internal System.Windows.Forms.ToolStripStatusLabel activityStatusLabel;");
        designer.AppendLine("        internal System.Windows.Forms.ToolStripStatusLabel expandStatusLabel;");
        designer.AppendLine("        internal System.Windows.Forms.ToolStripStatusLabel expandTextBoxStatusLabel;");
        designer.AppendLine("        internal System.Windows.Forms.ToolStripStatusLabel insertOverrideStatusLabel;");
        designer.AppendLine("        ApplicationMdiMenu mainMenu;");
        designer.AppendLine("        System.Windows.Forms.ToolStrip mainMenuToolStrip;");
        designer.AppendLine("        protected override void Dispose(bool disposing)");
        designer.AppendLine("        {");
        designer.AppendLine("            if (disposing && (components != null))");
        designer.AppendLine("                components.Dispose();");
        designer.AppendLine("            base.Dispose(disposing);");
        designer.AppendLine("        }");
        designer.AppendLine("        void InitializeComponent()");
        designer.AppendLine("        {");
        designer.AppendLine("            components = new System.ComponentModel.Container();");
        designer.AppendLine("            this.StatusStrip = new System.Windows.Forms.StatusStrip();");
        designer.AppendLine("            this.mainStatusLabel = new System.Windows.Forms.ToolStripStatusLabel();");
        designer.AppendLine("            this.userStatusLabel = new System.Windows.Forms.ToolStripStatusLabel();");
        designer.AppendLine("            this.versionStatusLabel = new System.Windows.Forms.ToolStripStatusLabel();");
        designer.AppendLine("            this.activityStatusLabel = new System.Windows.Forms.ToolStripStatusLabel();");
        designer.AppendLine("            this.expandStatusLabel = new System.Windows.Forms.ToolStripStatusLabel();");
        designer.AppendLine("            this.expandTextBoxStatusLabel = new System.Windows.Forms.ToolStripStatusLabel();");
        designer.AppendLine("            this.insertOverrideStatusLabel = new System.Windows.Forms.ToolStripStatusLabel();");
        designer.AppendLine("            _optionsContextMenuStrip = new System.Windows.Forms.ContextMenuStrip(components);");
        designer.AppendLine("            this.mainMenu = new ApplicationMdiMenu();");
        designer.AppendLine("            this.mainMenuToolStrip = new System.Windows.Forms.ToolStrip();");
        designer.AppendLine("            this.StatusStrip.SuspendLayout();");
        designer.AppendLine("            this.mainMenu.SuspendLayout();");
        designer.AppendLine("            this.mainMenuToolStrip.SuspendLayout();");
        designer.AppendLine("            this.SuspendLayout();");
        designer.AppendLine("            // ");
        designer.AppendLine("            // StatusStrip");
        designer.AppendLine("            // ");
        designer.AppendLine("            this.StatusStrip.BackColor = System.Drawing.SystemColors.Control;");
        designer.AppendLine("            this.StatusStrip.ForeColor = System.Drawing.SystemColors.MenuText;");
        designer.AppendLine("            this.StatusStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {");
        designer.AppendLine("                this.mainStatusLabel,");
        designer.AppendLine("                this.userStatusLabel,");
        designer.AppendLine("                this.versionStatusLabel,");
        designer.AppendLine("                this.activityStatusLabel,");
        designer.AppendLine("                this.expandStatusLabel,");
        designer.AppendLine("                this.expandTextBoxStatusLabel,");
        designer.AppendLine("                this.insertOverrideStatusLabel});");
        designer.AppendLine("            this.StatusStrip.Location = new System.Drawing.Point(0, 420);");
        designer.AppendLine("            this.StatusStrip.Name = \"StatusStrip\";");
        designer.AppendLine("            this.StatusStrip.Size = new System.Drawing.Size(600, 20);");
        designer.AppendLine("            this.StatusStrip.TabIndex = 1;");
        designer.AppendLine("            this.StatusStrip.ContextMenuStrip = _optionsContextMenuStrip;");
        designer.AppendLine("            // ");
        designer.AppendLine("            // mainStatusLabel");
        designer.AppendLine("            // ");
        designer.AppendLine("            this.mainStatusLabel.AutoSize = false;");
        designer.AppendLine("            this.mainStatusLabel.Name = \"mainStatusLabel\";");
        designer.AppendLine("            this.mainStatusLabel.Size = new System.Drawing.Size(255, 15);");
        designer.AppendLine("            this.mainStatusLabel.Spring = true;");
        designer.AppendLine("            this.mainStatusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;");
        designer.AppendLine("            // ");
        designer.AppendLine("            // userStatusLabel");
        designer.AppendLine("            // ");
        designer.AppendLine("            this.userStatusLabel.AutoSize = false;");
        designer.AppendLine("            this.userStatusLabel.BorderSides = ((System.Windows.Forms.ToolStripStatusLabelBorderSides)((((System.Windows.Forms.ToolStripStatusLabelBorderSides.Left | System.Windows.Forms.ToolStripStatusLabelBorderSides.Top)");
        designer.AppendLine("                        | System.Windows.Forms.ToolStripStatusLabelBorderSides.Right)");
        designer.AppendLine("                        | System.Windows.Forms.ToolStripStatusLabelBorderSides.Bottom)));");
        designer.AppendLine("            this.userStatusLabel.BorderStyle = System.Windows.Forms.Border3DStyle.SunkenOuter;");
        designer.AppendLine("            this.userStatusLabel.Name = \"userStatusLabel\";");
        designer.AppendLine("            this.userStatusLabel.Size = new System.Drawing.Size(120, 15);");
        designer.AppendLine("            this.userStatusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;");
        designer.AppendLine("            // ");
        designer.AppendLine("            // versionStatusLabel");
        designer.AppendLine("            // ");
        designer.AppendLine("            this.versionStatusLabel.BorderSides = ((System.Windows.Forms.ToolStripStatusLabelBorderSides)((((System.Windows.Forms.ToolStripStatusLabelBorderSides.Left | System.Windows.Forms.ToolStripStatusLabelBorderSides.Top)");
        designer.AppendLine("                        | System.Windows.Forms.ToolStripStatusLabelBorderSides.Right)");
        designer.AppendLine("                        | System.Windows.Forms.ToolStripStatusLabelBorderSides.Bottom)));");
        designer.AppendLine("            this.versionStatusLabel.BorderStyle = System.Windows.Forms.Border3DStyle.SunkenOuter;");
        designer.AppendLine("            this.versionStatusLabel.Name = \"versionStatusLabel\";");
        designer.AppendLine("            this.versionStatusLabel.Size = new System.Drawing.Size(120, 15);");
        designer.AppendLine("            this.versionStatusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;");
        designer.AppendLine("            // ");
        designer.AppendLine("            // activityStatusLabel");
        designer.AppendLine("            // ");
        designer.AppendLine("            this.activityStatusLabel.AutoSize = false;");
        designer.AppendLine("            this.activityStatusLabel.BorderSides = ((System.Windows.Forms.ToolStripStatusLabelBorderSides)((((System.Windows.Forms.ToolStripStatusLabelBorderSides.Left | System.Windows.Forms.ToolStripStatusLabelBorderSides.Top)");
        designer.AppendLine("                        | System.Windows.Forms.ToolStripStatusLabelBorderSides.Right)");
        designer.AppendLine("                        | System.Windows.Forms.ToolStripStatusLabelBorderSides.Bottom)));");
        designer.AppendLine("            this.activityStatusLabel.BorderStyle = System.Windows.Forms.Border3DStyle.SunkenOuter;");
        designer.AppendLine("            this.activityStatusLabel.Name = \"activityStatusLabel\";");
        designer.AppendLine("            this.activityStatusLabel.Size = new System.Drawing.Size(60, 15);");
        designer.AppendLine("            this.activityStatusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;");
        designer.AppendLine("            // ");
        designer.AppendLine("            // expandStatusLabel");
        designer.AppendLine("            // ");
        designer.AppendLine("            this.expandStatusLabel.AutoSize = false;");
        designer.AppendLine("            this.expandStatusLabel.BorderSides = ((System.Windows.Forms.ToolStripStatusLabelBorderSides)((((System.Windows.Forms.ToolStripStatusLabelBorderSides.Left | System.Windows.Forms.ToolStripStatusLabelBorderSides.Top)");
        designer.AppendLine("                        | System.Windows.Forms.ToolStripStatusLabelBorderSides.Right)");
        designer.AppendLine("                        | System.Windows.Forms.ToolStripStatusLabelBorderSides.Bottom)));");
        designer.AppendLine("            this.expandStatusLabel.BorderStyle = System.Windows.Forms.Border3DStyle.SunkenOuter;");
        designer.AppendLine("            this.expandStatusLabel.Name = \"expandStatusLabel\";");
        designer.AppendLine("            this.expandStatusLabel.Size = new System.Drawing.Size(60, 15);");
        designer.AppendLine("            this.expandStatusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;");
        designer.AppendLine("            this.expandStatusLabel.Click += new System.EventHandler(this.expandStatusLabel_Click);");
        designer.AppendLine("            // ");
        designer.AppendLine("            // expandTextBoxStatusLabel");
        designer.AppendLine("            // ");
        designer.AppendLine("            this.expandTextBoxStatusLabel.AutoSize = false;");
        designer.AppendLine("            this.expandTextBoxStatusLabel.BorderSides = ((System.Windows.Forms.ToolStripStatusLabelBorderSides)((((System.Windows.Forms.ToolStripStatusLabelBorderSides.Left | System.Windows.Forms.ToolStripStatusLabelBorderSides.Top)");
        designer.AppendLine("                        | System.Windows.Forms.ToolStripStatusLabelBorderSides.Right)");
        designer.AppendLine("                        | System.Windows.Forms.ToolStripStatusLabelBorderSides.Bottom)));");
        designer.AppendLine("            this.expandTextBoxStatusLabel.BorderStyle = System.Windows.Forms.Border3DStyle.SunkenOuter;");
        designer.AppendLine("            this.expandTextBoxStatusLabel.Name = \"expandTextBoxStatusLabel\";");
        designer.AppendLine("            this.expandTextBoxStatusLabel.Size = new System.Drawing.Size(60, 15);");
        designer.AppendLine("            this.expandTextBoxStatusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;");
        designer.AppendLine("            this.expandTextBoxStatusLabel.Click += new System.EventHandler(this.expandTextBoxStatusLabel_Click);");
        designer.AppendLine("            // ");
        designer.AppendLine("            // insertOverrideStatusLabel");
        designer.AppendLine("            // ");
        designer.AppendLine("            this.insertOverrideStatusLabel.AutoSize = false;");
        designer.AppendLine("            this.insertOverrideStatusLabel.BorderSides = ((System.Windows.Forms.ToolStripStatusLabelBorderSides)((((System.Windows.Forms.ToolStripStatusLabelBorderSides.Left | System.Windows.Forms.ToolStripStatusLabelBorderSides.Top)");
        designer.AppendLine("                        | System.Windows.Forms.ToolStripStatusLabelBorderSides.Right)");
        designer.AppendLine("                        | System.Windows.Forms.ToolStripStatusLabelBorderSides.Bottom)));");
        designer.AppendLine("            this.insertOverrideStatusLabel.BorderStyle = System.Windows.Forms.Border3DStyle.SunkenOuter;");
        designer.AppendLine("            this.insertOverrideStatusLabel.Name = \"insertOverrideStatusLabel\";");
        designer.AppendLine("            this.insertOverrideStatusLabel.Size = new System.Drawing.Size(30, 15);");
        designer.AppendLine("            this.insertOverrideStatusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;");
        designer.AppendLine("            // ");
        designer.AppendLine("            // _optionsContextMenuStrip");
        designer.AppendLine("            // ");
        designer.AppendLine("            _optionsContextMenuStrip.Name = \"_optionsContextMenuStrip\";");
        designer.AppendLine("            // ");
        designer.AppendLine("            // mainMenu");
        designer.AppendLine("            // ");
        designer.AppendLine("            this.mainMenu.LayoutStyle = System.Windows.Forms.ToolStripLayoutStyle.Flow;");
        designer.AppendLine("            this.mainMenu.Name = \"mainMenu\";");
        designer.AppendLine("            this.mainMenu.Padding = new System.Windows.Forms.Padding(0);");
        designer.AppendLine("            this.mainMenu.Size = new System.Drawing.Size(600, 19);");
        designer.AppendLine("            // ");
        designer.AppendLine("            // mainMenuToolStrip");
        designer.AppendLine("            // ");
        designer.AppendLine("            this.mainMenuToolStrip.GripStyle = System.Windows.Forms.ToolStripGripStyle.Hidden;");
        designer.AppendLine("            this.mainMenuToolStrip.Name = \"mainMenuToolStrip\";");
        designer.AppendLine("            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);");
        designer.AppendLine("            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;");
        designer.AppendLine("            this.ClientSize = new System.Drawing.Size(600, 440);");
        designer.AppendLine("            this.Controls.Add(this.StatusStrip);");
        designer.AppendLine("            this.Controls.Add(this.mainMenuToolStrip);");
        designer.AppendLine("            this.Controls.Add(this.mainMenu);");
        designer.AppendLine("            this.IsMdiContainer = true;");
        designer.AppendLine("            this.MainMenuStrip = this.mainMenu;");
        designer.AppendLine("            this.Name = \"ApplicationMdi\";");
        designer.AppendLine($"            this.Text = \"{Escape(appRoot)}\";");
        designer.AppendLine("            this.WindowState = System.Windows.Forms.FormWindowState.Maximized;");
        designer.AppendLine("            this.StatusStrip.ResumeLayout(false);");
        designer.AppendLine("            this.StatusStrip.PerformLayout();");
        designer.AppendLine("            this.mainMenu.ResumeLayout(false);");
        designer.AppendLine("            this.mainMenu.PerformLayout();");
        designer.AppendLine("            this.mainMenuToolStrip.ResumeLayout(false);");
        designer.AppendLine("            this.mainMenuToolStrip.PerformLayout();");
        designer.AppendLine("            this.ResumeLayout(false);");
        designer.AppendLine("            this.PerformLayout();");
        designer.AppendLine("        }");
        designer.AppendLine("    }");
        designer.AppendLine("}");
        File.WriteAllText(Path.Combine(viewsDir, "ApplicationMdi.Designer.cs"), designer.ToString());

        var resx = """
<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
</root>
""";
        File.WriteAllText(Path.Combine(viewsDir, "ApplicationMdi.resx"), resx);
    }
}


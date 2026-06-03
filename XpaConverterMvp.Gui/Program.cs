using System.Windows.Forms;

namespace XpaConverterMvp.Gui;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) =>
        {
            MessageBox.Show(
                args.Exception.ToString(),
                "Erro inesperado na GUI",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                MessageBox.Show(
                    ex.ToString(),
                    "Erro fatal na GUI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        };
        Application.Run(new MainForm());
    }
}

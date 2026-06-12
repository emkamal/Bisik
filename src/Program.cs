namespace Bisik;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Start the WinForms message loop without a placeholder main window.
        Application.Run();
    }
}

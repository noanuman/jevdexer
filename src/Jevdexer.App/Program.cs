using Velopack;

namespace Jevdexer.App;

/// <summary>
/// Entry point. Velopack runs first: during install, update and uninstall it launches the exe with
/// special arguments, runs the matching hook, and exits before any window exists.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => Installation.OnInstalled())
            .OnBeforeUninstallFastCallback(_ => Installation.OnUninstalling())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

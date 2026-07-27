using System.Windows;

namespace AgentRelay.App;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var services = RelayServices.Create();
        if (e.Args.Length > 0)
        {
            ConsoleHost.Attach();
            try
            {
                var exitCode = await CommandLine.RunAsync(services, e.Args).ConfigureAwait(true);
                Shutdown(exitCode);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                Shutdown(1);
            }
            return;
        }

        ConsoleHost.Hide();
        var window = new MainWindow(services);
        MainWindow = window;
        window.Show();
    }
}

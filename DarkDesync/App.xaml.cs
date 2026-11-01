using System.Windows;
using System.Windows.Threading;

namespace DarkDesync;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global exception safety nets
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CRITICAL] Unhandled Domain Exception: {ex}");
            }
        };

        DispatcherUnhandledException += (s, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"[CRITICAL] Unhandled Dispatcher Exception: {args.Exception}");
            MessageBox.Show($"Ocorreu um erro inesperado:\n{args.Exception.Message}",
                "DarkDesync - Erro Crítico", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}

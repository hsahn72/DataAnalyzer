using System.Windows;

namespace DataAnalyzer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ev) =>
        {
            MessageBox.Show(ev.Exception.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            ev.Handled = true;
        };
    }
}

using System.Windows;
using System.Windows.Threading;

namespace KopCashDesk.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }
    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Произошла ошибка. Данные не будут изменены.\n\n" + e.Exception.Message, "Учет доходов", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

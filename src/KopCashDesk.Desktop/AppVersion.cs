using System.Reflection;
using System.Windows;

namespace KopCashDesk.Desktop;

internal static class AppVersion
{
    public static string Current
    {
        get
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
                return informational.Split('+', 2)[0];

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static string Display => "v" + Current;
}

public partial class MainWindow
{
    private void AboutVNext_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, $"Учет доходов {AppVersion.Current}\nC# / .NET 10 / WPF", "О программе");
}

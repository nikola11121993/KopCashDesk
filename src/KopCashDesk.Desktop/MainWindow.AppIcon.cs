using System.Windows.Media.Imaging;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/KopCashDesk.ico", UriKind.Absolute));
        }
        catch
        {
            // The executable icon is also configured by the project file.
        }
    }
}

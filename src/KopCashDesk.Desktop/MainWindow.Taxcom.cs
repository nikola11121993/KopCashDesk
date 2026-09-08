using KopCashDesk.Core;
using System.Windows;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private void TaxcomSettings_Click(object sender, RoutedEventArgs e)
    {
        var organizations = _db.Organizations();
        if (organizations.Count == 0)
        {
            MessageBox.Show(this, "Сначала добавьте организацию.", "Такском", MessageBoxButton.OK, MessageBoxImage.Information);
            Navigate("organizations");
            return;
        }
        var selected = _page == "integrations" ? null : _db.Integrations().FirstOrDefault(x => x.Kind == IntegrationKind.Taxcom && x.OrganizationId == SelectedOrganizationId);
        var editor = new TaxcomSettingsWindow(this, _db, _dataDirectory, organizations, SelectedOrganizationId, selected);
        editor.ShowDialog();
        if (editor.Changed)
        {
            RefreshAll();
            StatusText.Text = "Параметры Такском сохранены";
        }
    }
}

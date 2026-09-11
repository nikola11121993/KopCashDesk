using System.Windows;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private void TaxcomShiftImport_Click(object sender, RoutedEventArgs e)
    {
        var window = new TaxcomShiftImportWindow(_db, SelectedOrganizationId, () =>
        {
            RefreshAll();
            StatusText.Text = "Кассовые отчёты Такском обработаны";
            if (_page == "summary") PageContent.Content = RenderSummary();
        }) { Owner = this };
        window.ShowDialog();
    }

    private void FrontolReportImport_Click(object sender, RoutedEventArgs e)
    {
        var window = new FrontolReportImportWindow(_db, SelectedOrganizationId, () =>
        {
            RefreshAll();
            StatusText.Text = "Выгрузка Frontol 6 обработана";
            if (_page == "summary") PageContent.Content = RenderSummary();
        }) { Owner = this };
        window.ShowDialog();
    }
}

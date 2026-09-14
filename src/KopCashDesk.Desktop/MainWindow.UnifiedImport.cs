using System.Windows;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private void UnifiedImport_Click(object sender, RoutedEventArgs e)
    {
        var window = new UnifiedImportWindow(
            _db,
            SelectedOrganizationId,
            () =>
            {
                RefreshAll();
                if (_page == "summary")
                    PageContent.Content = RenderSummaryV051();
                else if (_page == "reconciliation")
                    PageContent.Content = RenderReconciliation();
            })
        {
            Owner = this
        };
        window.ShowDialog();
    }
}

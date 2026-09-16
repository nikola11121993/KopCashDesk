using KopCashDesk.Data;
using System.Windows;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private void UnifiedImport_Click(object sender, RoutedEventArgs e)
    {
        var window = new SmartUnifiedImportWindow(
            _db,
            SelectedOrganizationId,
            () =>
            {
                KnownPointDirectory.Apply(_db);
                RefreshAll();
                if (_page == "summary")
                {
                    PageContent.Content = RenderSummaryV051();
                    PageSubtitle.Text = SummaryHint;
                }
                else if (_page == "reconciliation")
                {
                    PageContent.Content = RenderReconciliation();
                    PageSubtitle.Text = ReconciliationHint;
                }
            })
        {
            Owner = this
        };
        window.ShowDialog();
    }
}

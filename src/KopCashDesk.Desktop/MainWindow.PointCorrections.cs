using KopCashDesk.Data;
using System.Windows;
using System.Windows.Controls;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        VersionText.Text = AppVersion.Display;
        _db.EnsureManualCashPostings();
        var repairedDuplicates = _db.EnsureV053Fixes();
        var applied = KnownBusinessRules.ApplyPending(_db);
        if (repairedDuplicates > 0 || applied > 0)
        {
            RefreshAll();
            StatusText.Text = repairedDuplicates > 0
                ? $"Исправлено задвоенных кассовых записей: {repairedDuplicates}. Применено правил: {applied}"
                : $"Применено правил сопоставления: {applied}";
        }
    }

    private void Summary_Click(object sender, RoutedEventArgs e)
    {
        var repairedDuplicates = _db.EnsureV053Fixes();
        if (repairedDuplicates > 0)
        {
            RefreshAll();
            StatusText.Text = $"Исправлено задвоенных кассовых записей: {repairedDuplicates}";
        }

        _page = "summary";
        SearchBox.Text = "";
        SearchBox.IsEnabled = false;
        PrimaryButton.Visibility = Visibility.Collapsed;
        PageContent.Content = RenderSummaryV051();
    }

    private void OrganizationFilter_SelectionChangedV04(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (_page == "summary")
        {
            SearchBox.IsEnabled = false;
            PrimaryButton.Visibility = Visibility.Collapsed;
            PageContent.Content = RenderSummaryV051();
            return;
        }
        if (_page == "reconciliation")
        {
            SearchBox.IsEnabled = false;
            PrimaryButton.Visibility = Visibility.Collapsed;
            PageContent.Content = RenderReconciliation();
            return;
        }
        if (_page == "terminals")
        {
            SearchBox.IsEnabled = false;
            PrimaryButton.Visibility = Visibility.Collapsed;
            PageContent.Content = RenderTerminals();
            return;
        }
        Render();
    }

    private void RefreshV04_Click(object sender, RoutedEventArgs e)
    {
        RefreshAll();
        if (_page == "summary")
        {
            SearchBox.IsEnabled = false;
            PrimaryButton.Visibility = Visibility.Collapsed;
            PageContent.Content = RenderSummaryV051();
        }
        else if (_page == "reconciliation")
        {
            SearchBox.IsEnabled = false;
            PrimaryButton.Visibility = Visibility.Collapsed;
            PageContent.Content = RenderReconciliation();
        }
        else if (_page == "terminals")
        {
            SearchBox.IsEnabled = false;
            PrimaryButton.Visibility = Visibility.Collapsed;
            PageContent.Content = RenderTerminals();
        }
    }
}

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
        var applied = KnownBusinessRules.ApplyPending(_db);
        if (applied > 0)
        {
            RefreshAll();
            StatusText.Text = $"Применено правил сопоставления: {applied}";
        }
    }

    private void Summary_Click(object sender, RoutedEventArgs e)
    {
        _page = "summary";
        SearchBox.Text = "";
        SearchBox.IsEnabled = false;
        PrimaryButton.Visibility = Visibility.Collapsed;
        PageContent.Content = RenderSummary();
    }

    private void OrganizationFilter_SelectionChangedV04(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (_page == "summary")
        {
            SearchBox.IsEnabled = false;
            PrimaryButton.Visibility = Visibility.Collapsed;
            PageContent.Content = RenderSummary();
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
            PageContent.Content = RenderSummary();
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

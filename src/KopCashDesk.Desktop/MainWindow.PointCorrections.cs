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
        var matching = _db.RebuildCrossSourceShiftMatches();
        var applied = KnownBusinessRules.ApplyPending(_db);
        if (matching.NewMatches > 0 || matching.NewConflicts > 0 || applied > 0)
        {
            RefreshAll();
            StatusText.Text = matching.NewConflicts > 0
                ? $"Сопоставлено смен: {matching.MatchedPairs}. Конфликтов источников: {matching.Conflicts}. Применено правил: {applied}"
                : $"Сопоставлено смен: {matching.MatchedPairs}. Применено правил: {applied}";
        }
    }

    private void Summary_Click(object sender, RoutedEventArgs e)
    {
        var matching = _db.RebuildCrossSourceShiftMatches();
        if (matching.NewMatches > 0 || matching.NewConflicts > 0)
        {
            RefreshAll();
            StatusText.Text = $"Сопоставлено кассовых смен: {matching.MatchedPairs}; конфликтов: {matching.Conflicts}";
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

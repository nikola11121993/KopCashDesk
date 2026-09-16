using KopCashDesk.Data;
using System.Windows;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private void ModernMainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        KnownOrganizations.Ensure(_db);
        RefreshAll();
        Navigate("summary");
        UpdateSearchPanelVisibility();
    }

    private void SearchBox_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        => UpdateSearchPanelVisibility();

    private void UpdateSearchPanelVisibility()
    {
        if (SearchPanel is null || SearchBox is null) return;
        SearchPanel.Visibility = SearchBox.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
    }
}

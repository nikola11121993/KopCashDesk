using KopCashDesk.Data;
using System.Windows;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private const string SummaryHint = "ОФД — основной кассовый источник (Такском / Первый ОФД). Frontol используется только для проверки и второй раз в итог не складывается.";
    private const string ReconciliationHint = "Сверка терминала с кассой: кассовая сумма берётся из ОФД (Такском / Первый ОФД). Frontol только проверяет её и не складывается повторно. Более поздняя касса может закрывать более ранний терминальный остаток.";

    private void ModernMainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        KnownOrganizations.Ensure(_db);
        KnownPointDirectory.Apply(_db);
        RefreshAll();
        Navigate("summary");
        PageSubtitle.Text = SummaryHint;
        UpdateSearchPanelVisibility();
    }

    private void ModernSummary_Click(object sender, RoutedEventArgs e)
    {
        Navigate("summary");
        PageSubtitle.Text = SummaryHint;
        UpdateSearchPanelVisibility();
    }

    private void ModernReconciliation_Click(object sender, RoutedEventArgs e)
    {
        Navigate("reconciliation");
        PageSubtitle.Text = ReconciliationHint;
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

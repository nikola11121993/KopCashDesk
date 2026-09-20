using KopCashDesk.Data;
using System.Windows;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private const string SummaryHint = "Такском — основной кассовый источник. Frontol используется только для проверки. Если суммы различаются, в итог всё равно берётся Такском, а строка помечается «Расхождение с Frontol».";
    private const string ReconciliationHint = "Сверка терминала с кассой: кассовая сумма берётся из Такском. Frontol только проверяет её и не складывается повторно. Более поздняя касса может закрывать более ранний терминальный остаток.";

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

using KopCashDesk.Data;
using System.Windows;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private const string SummaryHint = "Суммы терминала и кассы по дням. «Конфликт Taxcom ↔ Frontol» означает: одна и та же смена пришла из двух кассовых источников с разными суммами. Двойной клик по строке покажет обе суммы.";
    private const string ReconciliationHint = "Накопительная сверка: показывает, чем более поздняя сумма кассы закрыла более раннюю сумму терминала. Двойной клик по дню — подробное объяснение погашения.";

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

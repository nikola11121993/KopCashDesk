using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private sealed record ReconciliationLocationOption(Guid? Id, string Name);
    private sealed record ReconciliationMonthOption(int? Number, string Name);

    private sealed record ReconciliationRow(ReconciliationDay Day)
    {
        public string Date => Day.Date.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU"));
        public string Point => Day.Location;
        public decimal? Terminal => Day.BankElectronic;
        public decimal? Cash => Day.CashElectronic;
        public decimal Prior => Day.PriorOutstanding;
        public decimal CashApplied => Day.CashAppliedOnDate;
        public decimal ClosedLater => Day.ClosedLater;
        public decimal DayRemaining => Day.DayRemaining;
        public decimal TotalRemaining => Day.CumulativeOutstanding;
        public string Status => Day.Status;
        public string ClosedAt => Day.LastShiftClosedAt?.LocalDateTime.ToString("dd.MM.yyyy HH:mm") ?? "";
        public string Shift => Day.ShiftNumbers;
    }

    private void Reconciliation_Click(object sender, RoutedEventArgs e)
    {
        _page = "reconciliation";
        SearchBox.Text = "";
        SearchBox.IsEnabled = false;
        PrimaryButton.Visibility = Visibility.Collapsed;
        PageContent.Content = RenderReconciliation();
    }

    private UIElement RenderReconciliation()
    {
        PageTitle.Text = "Сверка касса ↔ терминал";
        PageSubtitle.Text = "Накопительная FIFO-сверка: поздняя касса закрывает самые старые непробитые терминальные суммы";

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var all = _db.ReconciliationDays(SelectedOrganizationId);
        var years = all.Select(x => x.Date.Year).Distinct().OrderByDescending(x => x).ToList();
        if (years.Count == 0) years.Add(DateTime.Today.Year);

        var yearBox = new ComboBox { Width = 105, ItemsSource = years, SelectedItem = years[0], Margin = new Thickness(0, 0, 12, 0) };
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var monthOptions = new List<ReconciliationMonthOption> { new(null, "Все месяцы") };
        for (var month = 1; month <= 12; month++) monthOptions.Add(new(month, culture.DateTimeFormat.GetMonthName(month)));
        var monthBox = new ComboBox { Width = 155, ItemsSource = monthOptions, DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new Thickness(0, 0, 12, 0) };

        var locationOptions = new List<ReconciliationLocationOption> { new(null, "Все точки") };
        locationOptions.AddRange(_locations
            .Where(x => SelectedOrganizationId is null || x.OrganizationId == SelectedOrganizationId)
            .OrderBy(x => x.Name)
            .Select(x => new ReconciliationLocationOption(x.Id, x.Name)));
        var locationBox = new ComboBox { Width = 285, ItemsSource = locationOptions, DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new Thickness(0, 0, 12, 0) };

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        filters.Children.Add(new TextBlock { Text = "Год:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        filters.Children.Add(yearBox);
        filters.Children.Add(new TextBlock { Text = "Месяц:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        filters.Children.Add(monthBox);
        filters.Children.Add(new TextBlock { Text = "Точка:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        filters.Children.Add(locationBox);
        root.Children.Add(filters);

        var totals = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(totals, 1);
        root.Children.Add(totals);

        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            FrozenColumnCount = 2
        };
        grid.Columns.Add(ReconciliationTextColumn("Дата", "Date", 100));
        grid.Columns.Add(ReconciliationTextColumn("Точка", "Point", new DataGridLength(2, DataGridLengthUnitType.Star)));
        grid.Columns.Add(ReconciliationMoneyColumn("Терминалы", "Terminal", 110));
        grid.Columns.Add(ReconciliationMoneyColumn("Касса безнал", "Cash", 115));
        grid.Columns.Add(ReconciliationMoneyColumn("Непробито ранее", "Prior", 125));
        grid.Columns.Add(ReconciliationMoneyColumn("Погашено кассой", "CashApplied", 125));
        grid.Columns.Add(ReconciliationMoneyColumn("Пробито позже", "ClosedLater", 115));
        grid.Columns.Add(ReconciliationMoneyColumn("Остаток за день", "DayRemaining", 120));
        grid.Columns.Add(ReconciliationMoneyColumn("Общий остаток", "TotalRemaining", 120));
        grid.Columns.Add(ReconciliationTextColumn("Статус", "Status", 210));
        grid.Columns.Add(ReconciliationTextColumn("Закрытие смены", "ClosedAt", 145));
        grid.Columns.Add(ReconciliationTextColumn("№ смены", "Shift", 95));
        Grid.SetRow(grid, 2);
        root.Children.Add(grid);

        void RefreshRows()
        {
            if (yearBox.SelectedItem is not int year) return;
            var month = (monthBox.SelectedItem as ReconciliationMonthOption)?.Number;
            var locationId = (locationBox.SelectedItem as ReconciliationLocationOption)?.Id;
            var conflictKeys = _db.FiscalSourceConflicts()
                .Select(x => (x.OrganizationId, x.LocationId, x.BusinessDate))
                .ToHashSet();
            var days = _db.ReconciliationDays(SelectedOrganizationId, year, month, locationId)
                .Select(x => conflictKeys.Contains((x.OrganizationId, x.LocationId, x.Date))
                    ? x with
                    {
                        CashElectronic = null,
                        RequiresReview = true,
                        Status = "Конфликт кассовых источников — требуется проверка"
                    }
                    : x)
                .ToArray();
            var rows = days.Select(x => new ReconciliationRow(x)).ToArray();
            grid.ItemsSource = rows;

            var terminalKnown = days.Where(x => x.BankElectronic is not null).Select(x => x.BankElectronic!.Value).ToArray();
            var cashKnown = days.Where(x => x.CashElectronic is not null).Select(x => x.CashElectronic!.Value).ToArray();
            var latestByPoint = days.GroupBy(x => x.LocationId).Select(g => g.OrderByDescending(x => x.Date).First()).ToArray();
            var remaining = latestByPoint.Sum(x => x.CumulativeOutstanding);
            var missingCash = days.Count(x => x.HasBankData && !x.HasCashData && x.DayRemaining > 0m);
            var review = days.Count(x => x.RequiresReview);

            totals.Text =
                $"Терминалы: {(terminalKnown.Length == 0 ? "нет данных" : terminalKnown.Sum().ToString("N2") + " ₽")}     •     " +
                $"Касса: {(cashKnown.Length == 0 ? "нет данных" : cashKnown.Sum().ToString("N2") + " ₽")}     •     " +
                $"Текущий непробитый остаток: {remaining:N2} ₽     •     " +
                $"Дней без кассовых данных: {missingCash}     •     Требует проверки: {review}";
        }

        yearBox.SelectionChanged += (_, _) => RefreshRows();
        monthBox.SelectionChanged += (_, _) => RefreshRows();
        locationBox.SelectionChanged += (_, _) => RefreshRows();

        grid.MouseDoubleClick += (_, _) =>
        {
            if (grid.SelectedItem is not ReconciliationRow row) return;
            ShowReconciliationExplanation(row.Day);
        };
        var menu = new ContextMenu();
        var explain = new MenuItem { Header = "Показать, чем закрыта сумма..." };
        explain.Click += (_, _) => { if (grid.SelectedItem is ReconciliationRow row) ShowReconciliationExplanation(row.Day); };
        menu.Items.Add(explain);
        grid.ContextMenu = menu;

        RefreshRows();
        return root;
    }

    private void ShowReconciliationExplanation(ReconciliationDay day)
    {
        var allocations = _db.ReconciliationAllocations(day.OrganizationId, day.LocationId)
            .Where(x => x.TerminalDate == day.Date)
            .OrderBy(x => x.SettlementDate)
            .ThenBy(x => x.SettlementSource)
            .ToArray();

        var lines = new List<string>
        {
            $"{day.Location} — {day.Date:dd.MM.yyyy}",
            $"Терминалы: {(day.BankElectronic is null ? "нет данных" : day.BankElectronic.Value.ToString("N2") + " ₽")}",
            $"Остаток этого дня: {day.DayRemaining:N2} ₽",
            ""
        };

        if (allocations.Length == 0)
        {
            lines.Add("Связей погашения пока нет.");
        }
        else
        {
            lines.Add("Погашения:");
            foreach (var allocation in allocations)
            {
                var kind = allocation.Kind == ReconciliationAllocationKind.Cash ? "касса" : "возврат/корректировка терминального потока";
                lines.Add($"{allocation.SettlementDate:dd.MM.yyyy} — {kind} — {allocation.Amount:N2} ₽ — {allocation.SettlementSource}");
            }
        }

        MessageBox.Show(this, string.Join(Environment.NewLine, lines), "Объяснение сверки", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static DataGridTextColumn ReconciliationTextColumn(string header, string property, double width) =>
        ReconciliationTextColumn(header, property, new DataGridLength(width));

    private static DataGridTextColumn ReconciliationTextColumn(string header, string property, DataGridLength width) => new()
    {
        Header = header,
        Binding = new Binding(property),
        Width = width,
        IsReadOnly = true
    };

    private static DataGridTextColumn ReconciliationMoneyColumn(string header, string property, double width) => new()
    {
        Header = header,
        Binding = new Binding(property) { StringFormat = "N2", TargetNullValue = "—" },
        Width = width,
        IsReadOnly = true
    };
}

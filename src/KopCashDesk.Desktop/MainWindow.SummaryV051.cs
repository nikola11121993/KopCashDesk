using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private UIElement RenderSummaryV051()
    {
        PageTitle.Text = "Свод по точкам";
        PageSubtitle.Text = "Терминалы, касса, закрытия смен и ручные корректировки";

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var allRows = _db.PointDaySummaries(SelectedOrganizationId);
        var years = allRows.Select(x => x.Date.Year).Distinct().OrderByDescending(x => x).ToList();
        if (years.Count == 0) years.Add(DateTime.Today.Year);

        var yearBox = new ComboBox { Width = 105, ItemsSource = years, SelectedItem = years[0], Margin = new Thickness(0, 0, 12, 0) };
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var monthOptions = new List<MonthOption> { new(null, "Все месяцы") };
        for (var month = 1; month <= 12; month++) monthOptions.Add(new(month, culture.DateTimeFormat.GetMonthName(month)));
        var monthBox = new ComboBox { Width = 155, ItemsSource = monthOptions, DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new Thickness(0, 0, 12, 0) };

        var locationOptions = new List<LocationOption> { new(null, "Все точки") };
        locationOptions.AddRange(_locations
            .Where(x => SelectedOrganizationId is null || x.OrganizationId == SelectedOrganizationId)
            .OrderBy(x => x.Name)
            .Select(x => new LocationOption(x.Id, x.Name)));
        var locationBox = new ComboBox { Width = 265, ItemsSource = locationOptions, DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new Thickness(0, 0, 12, 0) };

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        filters.Children.Add(new TextBlock { Text = "Год:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        filters.Children.Add(yearBox);
        filters.Children.Add(new TextBlock { Text = "Месяц:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        filters.Children.Add(monthBox);
        filters.Children.Add(new TextBlock { Text = "Точка:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        filters.Children.Add(locationBox);
        root.Children.Add(filters);

        var totals = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(totals, 1);
        root.Children.Add(totals);

        DataGrid dailyGrid = null!;
        DataGrid monthlyGrid = null!;

        bool HasManual(DaySummaryRow row) => _db.ManualCashPostings(row.OrganizationId, row.DateValue.Year, row.DateValue.Month, row.LocationId)
            .Any(x => x.Date == row.DateValue);

        void ToggleSberCopy(DaySummaryRow row, bool isChecked)
        {
            if (row.Sber is null)
            {
                MessageBox.Show(this, "За этот день нет суммы Сбера.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshData();
                return;
            }

            var hasManual = HasManual(row);
            if (isChecked && row.HasActualFiscal && !hasManual)
            {
                var answer = MessageBox.Show(this,
                    "За этот день уже есть кассовый отчёт.\n\nПоставить сумму Сбера как ручную корректировку? Исходный отчёт останется в базе, а снятие галочки вернёт его сумму.",
                    "Ручная корректировка кассы", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    RefreshData();
                    return;
                }
            }

            if (isChecked)
            {
                _db.SetManualCash(row.OrganizationId, row.LocationId, row.DateValue, row.Sber.Value);
                StatusText.Text = $"{row.Point}: касса за {row.Date} = {row.Sber.Value:N2} ₽";
            }
            else
            {
                _db.ClearManualCash(row.OrganizationId, row.LocationId, row.DateValue);
                StatusText.Text = $"{row.Point}: ручная корректировка за {row.Date} снята";
            }
            RefreshData();
        }

        void EditManualCash(DaySummaryRow row)
        {
            var dialog = new ManualCashEditWindow(row.Point, row.DateValue, row.CashElectronic) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            if (dialog.ClearRequested)
            {
                _db.ClearManualCash(row.OrganizationId, row.LocationId, row.DateValue);
                StatusText.Text = $"{row.Point}: ручная корректировка за {row.Date} очищена";
            }
            else if (dialog.Value is decimal value)
            {
                _db.SetManualCash(row.OrganizationId, row.LocationId, row.DateValue, value);
                StatusText.Text = $"{row.Point}: касса за {row.Date} вручную = {value:N2} ₽";
            }
            RefreshData();
        }

        dailyGrid = BuildDailySummaryGrid(ToggleSberCopy, EditManualCash);
        monthlyGrid = BuildMonthlySummaryGrid();
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "По дням", Content = dailyGrid });
        tabs.Items.Add(new TabItem { Header = "По месяцам", Content = monthlyGrid });
        Grid.SetRow(tabs, 2);
        root.Children.Add(tabs);

        void RefreshData()
        {
            if (yearBox.SelectedItem is not int year) return;
            var month = (monthBox.SelectedItem as MonthOption)?.Number;
            var locationId = (locationBox.SelectedItem as LocationOption)?.Id;
            var rows = _db.PointDaySummaries(SelectedOrganizationId, year, month, locationId);
            var manual = _db.ManualCashPostings(SelectedOrganizationId, year, month, locationId)
                .ToDictionary(x => (x.OrganizationId, x.LocationId, x.Date), x => x.Electronic);

            var dayRows = rows.Select(x => ToDayRowV051(x, manual)).OrderByDescending(x => x.DateValue).ThenBy(x => x.Point).ToArray();
            dailyGrid.ItemsSource = dayRows;

            var monthRows = dayRows
                .GroupBy(x => new { x.DateValue.Year, x.DateValue.Month, x.OrganizationId, x.Organization, x.LocationId, x.Point })
                .Select(g =>
                {
                    var bank = SumNullable(g.Select(x => x.Sber));
                    var cash = SumNullable(g.Select(x => x.CashElectronic));
                    var shiftTotal = SumNullable(g.Select(x => x.ShiftTotal));
                    var missingCash = g.Any(x => x.Sber is not null && x.CashElectronic is null);
                    var missingBank = g.Any(x => x.Sber is null && x.CashElectronic is not null);
                    var complete = !missingCash && !missingBank;
                    var difference = complete && bank is not null && cash is not null ? cash - bank : null;
                    var lastClosed = g.Where(x => x.LastClosedAt is not null).Select(x => x.LastClosedAt).Max();
                    var status = complete ? SummaryStatus(bank, cash, shiftTotal, g.Sum(x => x.ShiftCount), difference, g.Any(x => x.CashFromSber)) : "Неполные данные";
                    return new MonthSummaryRow(g.Key.Year, g.Key.Month, g.Key.OrganizationId, g.Key.LocationId, g.Key.Organization, g.Key.Point,
                        bank, cash, shiftTotal, g.Sum(x => x.ShiftCount), lastClosed, difference, status);
                })
                .OrderByDescending(x => x.Year).ThenByDescending(x => x.Month).ThenBy(x => x.Point).ToArray();
            monthlyGrid.ItemsSource = monthRows;

            var bankTotal = SumNullable(dayRows.Select(x => x.Sber));
            var cashTotal = SumNullable(dayRows.Select(x => x.CashElectronic));
            var shiftGrandTotal = SumNullable(dayRows.Select(x => x.ShiftTotal));
            var incompleteDays = dayRows.Count(x => (x.Sber is null) != (x.CashElectronic is null));
            var manualDays = manual.Count;
            totals.Text = $"Сбер за период: {MoneyText(bankTotal)}     •     Касса безнал: {MoneyText(cashTotal)}     •     Закрыто сменами: {MoneyText(shiftGrandTotal)}" +
                          (manualDays > 0 ? $"     •     Ручных корректировок: {manualDays}" : "") +
                          (incompleteDays > 0 ? $"     •     Неполных дней: {incompleteDays}" : "");
        }

        yearBox.SelectionChanged += (_, _) => RefreshData();
        monthBox.SelectionChanged += (_, _) => RefreshData();
        locationBox.SelectionChanged += (_, _) => RefreshData();
        RefreshData();
        return root;
    }

    private static DaySummaryRow ToDayRowV051(
        PointDaySummary row,
        IReadOnlyDictionary<(Guid OrganizationId, Guid LocationId, DateOnly Date), decimal> manual)
    {
        var hasManual = manual.TryGetValue((row.OrganizationId, row.LocationId, row.Date), out var manualElectronic);
        var cash = hasManual ? manualElectronic : row.FiscalElectronic;
        var copied = hasManual && row.BankElectronic is not null && manualElectronic == row.BankElectronic.Value;
        var difference = row.BankElectronic is not null && cash is not null ? cash - row.BankElectronic : null;
        var status = SummaryStatus(row.BankElectronic, cash, row.ShiftTotal, row.ShiftCount, difference, copied);
        if (hasManual && !copied) status += " — ручная корректировка";

        return new DaySummaryRow(
            row.Date, row.OrganizationId, row.LocationId, row.Organization, row.Location,
            row.BankElectronic, cash, row.FiscalElectronic is not null, copied,
            row.BankElectronic is not null, row.ShiftTotal, row.ShiftCount, row.LastShiftClosedAt,
            difference, status);
    }
}

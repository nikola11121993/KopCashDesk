using KopCashDesk.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private sealed record LocationOption(Guid? Id, string Name);
    private sealed record MonthOption(int? Number, string Name);

    private sealed record DaySummaryRow(
        DateOnly DateValue,
        Guid OrganizationId,
        Guid LocationId,
        string Organization,
        string Point,
        decimal? Sber,
        decimal? FiscalElectronic,
        decimal? ShiftTotal,
        int ShiftCount,
        DateTimeOffset? LastClosedAt,
        decimal? Difference,
        string Status)
    {
        public string Date => DateValue.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU"));
        public string ClosedAt => LastClosedAt?.LocalDateTime.ToString("dd.MM.yyyy HH:mm") ?? "";
    }

    private sealed record MonthSummaryRow(
        int Year,
        int Month,
        Guid OrganizationId,
        Guid LocationId,
        string Organization,
        string Point,
        decimal? Sber,
        decimal? FiscalElectronic,
        decimal? ShiftTotal,
        int ShiftCount,
        DateTimeOffset? LastClosedAt,
        decimal? Difference,
        string Status)
    {
        public string Period
        {
            get
            {
                var culture = CultureInfo.GetCultureInfo("ru-RU");
                var name = culture.DateTimeFormat.GetMonthName(Month);
                return $"{Month:00}.{Year} — {name}";
            }
        }

        public string ClosedAt => LastClosedAt?.LocalDateTime.ToString("dd.MM.yyyy HH:mm") ?? "";
    }

    private UIElement RenderSummary()
    {
        PageTitle.Text = "Свод по точкам";
        PageSubtitle.Text = "Терминалы Сбер, касса и закрытия смен по дням и месяцам";

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var allRows = _db.PointDaySummaries(SelectedOrganizationId);
        var years = allRows.Select(x => x.Date.Year).Distinct().OrderByDescending(x => x).ToList();
        if (years.Count == 0) years.Add(DateTime.Today.Year);

        var yearBox = new ComboBox { Width = 105, ItemsSource = years, SelectedItem = years[0], Margin = new Thickness(0, 0, 12, 0) };
        var monthOptions = new List<MonthOption> { new(null, "Все месяцы") };
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        for (var month = 1; month <= 12; month++)
            monthOptions.Add(new(month, culture.DateTimeFormat.GetMonthName(month)));
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

        var dailyGrid = BuildDailySummaryGrid();
        var monthlyGrid = BuildMonthlySummaryGrid();

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

            var dayRows = rows.Select(ToDayRow).OrderByDescending(x => x.DateValue).ThenBy(x => x.Point).ToArray();
            dailyGrid.ItemsSource = dayRows;

            var monthRows = rows
                .GroupBy(x => new { x.Date.Year, x.Date.Month, x.OrganizationId, x.Organization, x.LocationId, x.Location })
                .Select(g =>
                {
                    var bank = SumNullable(g.Select(x => x.BankElectronic));
                    var fiscal = SumNullable(g.Select(x => x.FiscalElectronic));
                    var shiftTotal = SumNullable(g.Select(x => x.ShiftTotal));
                    var difference = bank is not null && fiscal is not null ? fiscal - bank : null;
                    var lastClosed = g.Where(x => x.LastShiftClosedAt is not null).Select(x => x.LastShiftClosedAt).Max();
                    return new MonthSummaryRow(
                        g.Key.Year, g.Key.Month, g.Key.OrganizationId, g.Key.LocationId, g.Key.Organization, g.Key.Location,
                        bank, fiscal, shiftTotal, g.Sum(x => x.ShiftCount), lastClosed, difference,
                        SummaryStatus(bank, fiscal, shiftTotal, g.Sum(x => x.ShiftCount), difference));
                })
                .OrderByDescending(x => x.Year)
                .ThenByDescending(x => x.Month)
                .ThenBy(x => x.Point)
                .ToArray();
            monthlyGrid.ItemsSource = monthRows;

            var bankTotal = SumNullable(rows.Select(x => x.BankElectronic));
            var fiscalTotal = SumNullable(rows.Select(x => x.FiscalElectronic));
            var shiftGrandTotal = SumNullable(rows.Select(x => x.ShiftTotal));
            totals.Text = $"Сбер за период: {MoneyText(bankTotal)}     •     Касса безнал: {MoneyText(fiscalTotal)}     •     Закрыто сменами: {MoneyText(shiftGrandTotal)}";
        }

        yearBox.SelectionChanged += (_, _) => RefreshData();
        monthBox.SelectionChanged += (_, _) => RefreshData();
        locationBox.SelectionChanged += (_, _) => RefreshData();
        RefreshData();
        return root;
    }

    private static DataGrid BuildDailySummaryGrid()
    {
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single };
        grid.Columns.Add(new DataGridTextColumn { Header = "Дата", Binding = new Binding("Date"), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Точка", Binding = new Binding("Point"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(MoneyColumn("Терминалы Сбер", "Sber", 130));
        grid.Columns.Add(MoneyColumn("Касса безнал", "FiscalElectronic", 125));
        grid.Columns.Add(MoneyColumn("Закрыто сменой", "ShiftTotal", 135));
        grid.Columns.Add(new DataGridTextColumn { Header = "Смен", Binding = new Binding("ShiftCount"), Width = 60 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Когда закрыли", Binding = new Binding("ClosedAt"), Width = 145 });
        grid.Columns.Add(MoneyColumn("Разница", "Difference", 110));
        grid.Columns.Add(new DataGridTextColumn { Header = "Статус", Binding = new Binding("Status"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        return grid;
    }

    private static DataGrid BuildMonthlySummaryGrid()
    {
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single };
        grid.Columns.Add(new DataGridTextColumn { Header = "Месяц", Binding = new Binding("Period"), Width = 175 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Точка", Binding = new Binding("Point"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(MoneyColumn("Терминалы Сбер", "Sber", 130));
        grid.Columns.Add(MoneyColumn("Касса безнал", "FiscalElectronic", 125));
        grid.Columns.Add(MoneyColumn("Закрыто сменами", "ShiftTotal", 140));
        grid.Columns.Add(new DataGridTextColumn { Header = "Смен", Binding = new Binding("ShiftCount"), Width = 60 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Последнее закрытие", Binding = new Binding("ClosedAt"), Width = 155 });
        grid.Columns.Add(MoneyColumn("Разница", "Difference", 110));
        grid.Columns.Add(new DataGridTextColumn { Header = "Статус", Binding = new Binding("Status"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        return grid;
    }

    private static DataGridTextColumn MoneyColumn(string header, string property, double width) => new()
    {
        Header = header,
        Binding = new Binding(property) { StringFormat = "N2", TargetNullValue = "—" },
        Width = width
    };

    private static DaySummaryRow ToDayRow(PointDaySummary row)
    {
        var difference = row.BankElectronic is not null && row.FiscalElectronic is not null
            ? row.FiscalElectronic - row.BankElectronic
            : null;
        return new DaySummaryRow(
            row.Date, row.OrganizationId, row.LocationId, row.Organization, row.Location,
            row.BankElectronic, row.FiscalElectronic, row.ShiftTotal, row.ShiftCount, row.LastShiftClosedAt,
            difference, SummaryStatus(row.BankElectronic, row.FiscalElectronic, row.ShiftTotal, row.ShiftCount, difference));
    }

    private static string SummaryStatus(decimal? bank, decimal? fiscal, decimal? shiftTotal, int shiftCount, decimal? difference)
    {
        if (bank is not null && fiscal is null && shiftCount == 0) return "Сбер загружен, кассы нет";
        if (bank is null && (fiscal is not null || shiftCount > 0)) return "Касса есть, Сбера нет";
        if (bank is not null && fiscal is not null)
            return difference == 0m ? "Сошлось" : "Есть расхождение";
        if (shiftCount > 0 || shiftTotal is not null) return "Есть закрытие смены";
        return "Нет данных";
    }

    private static decimal? SumNullable(IEnumerable<decimal?> values)
    {
        var materialized = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return materialized.Length == 0 ? null : materialized.Sum();
    }

    private static string MoneyText(decimal? value) => value is null ? "нет данных" : $"{value:N2} ₽";
}

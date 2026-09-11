using KopCashDesk.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private sealed record ReconciliationPointOption(Guid? Id, string Name);
    private sealed record ReconciliationMonthOption(int? Number, string Name);
    private sealed record ReconciliationStatusOption(string Key, string Name);

    private sealed record ReconciliationDayRow(
        DateOnly DateValue,
        Guid LocationId,
        string Point,
        decimal? CashElectronic,
        decimal? BankElectronic,
        decimal? Difference,
        int ShiftCount,
        string Status)
    {
        public string Date => DateValue.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU"));
    }

    private UIElement RenderReconciliation()
    {
        PageTitle.Text = "Сверка касса ↔ терминал";
        PageSubtitle.Text = "Безнал кассы против безнала терминалов Сбер. Разница = касса − терминал.";

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var allRows = _db.PointDaySummaries(SelectedOrganizationId);
        var years = allRows.Select(x => x.Date.Year).Distinct().OrderByDescending(x => x).ToList();
        if (years.Count == 0) years.Add(DateTime.Today.Year);

        var yearBox = new ComboBox
        {
            Width = 105,
            ItemsSource = years,
            SelectedItem = years[0],
            Margin = new Thickness(0, 0, 12, 0)
        };

        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var monthOptions = new List<ReconciliationMonthOption> { new(null, "Все месяцы") };
        for (var month = 1; month <= 12; month++)
            monthOptions.Add(new(month, culture.DateTimeFormat.GetMonthName(month)));
        var monthBox = new ComboBox
        {
            Width = 155,
            ItemsSource = monthOptions,
            DisplayMemberPath = "Name",
            SelectedIndex = 0,
            Margin = new Thickness(0, 0, 12, 0)
        };

        var pointOptions = new List<ReconciliationPointOption> { new(null, "Все точки") };
        pointOptions.AddRange(_locations
            .Where(x => !x.IsExcluded && (SelectedOrganizationId is null || x.OrganizationId == SelectedOrganizationId))
            .OrderBy(x => x.Name)
            .Select(x => new ReconciliationPointOption(x.Id, x.Name)));
        var pointBox = new ComboBox
        {
            Width = 270,
            ItemsSource = pointOptions,
            DisplayMemberPath = "Name",
            SelectedIndex = 0,
            Margin = new Thickness(0, 0, 12, 0)
        };

        var statusOptions = new[]
        {
            new ReconciliationStatusOption("all", "Все"),
            new ReconciliationStatusOption("mismatch", "Только расхождения"),
            new ReconciliationStatusOption("matched", "Только сошлось"),
            new ReconciliationStatusOption("missing", "Нет одной стороны")
        };
        var statusBox = new ComboBox
        {
            Width = 175,
            ItemsSource = statusOptions,
            DisplayMemberPath = "Name",
            SelectedIndex = 0
        };

        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        filters.Children.Add(FilterLabel("Год:"));
        filters.Children.Add(yearBox);
        filters.Children.Add(FilterLabel("Месяц:"));
        filters.Children.Add(monthBox);
        filters.Children.Add(FilterLabel("Точка:"));
        filters.Children.Add(pointBox);
        filters.Children.Add(FilterLabel("Показать:"));
        filters.Children.Add(statusBox);
        root.Children.Add(filters);

        var totals = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(totals, 1);
        root.Children.Add(totals);

        var grid = BuildReconciliationGrid();
        Grid.SetRow(grid, 2);
        root.Children.Add(grid);

        void RefreshData()
        {
            if (yearBox.SelectedItem is not int year) return;
            var month = (monthBox.SelectedItem as ReconciliationMonthOption)?.Number;
            var pointId = (pointBox.SelectedItem as ReconciliationPointOption)?.Id;
            var statusKey = (statusBox.SelectedItem as ReconciliationStatusOption)?.Key ?? "all";

            var excluded = _locations.Where(x => x.IsExcluded).Select(x => x.Id).ToHashSet();
            var manual = _db.ManualCashPostings(SelectedOrganizationId, year, month, pointId)
                .ToDictionary(x => (x.OrganizationId, x.LocationId, x.Date), x => x.Electronic);

            var rows = _db.PointDaySummaries(SelectedOrganizationId, year, month, pointId)
                .Where(x => !excluded.Contains(x.LocationId))
                .Select(x =>
                {
                    var hasManual = manual.TryGetValue((x.OrganizationId, x.LocationId, x.Date), out var manualElectronic);
                    var cash = x.FiscalElectronic ?? (hasManual ? manualElectronic : null);
                    var bank = x.BankElectronic;
                    var difference = cash is not null && bank is not null ? Money.Normalize(cash.Value - bank.Value) : null;
                    var status = ReconciliationStatus(cash, bank, difference);
                    return new ReconciliationDayRow(x.Date, x.LocationId, x.Location, cash, bank, difference, x.ShiftCount, status);
                })
                .Where(x => statusKey switch
                {
                    "mismatch" => x.Status == "Расхождение",
                    "matched" => x.Status == "Сошлось",
                    "missing" => x.Status is "Нет кассы" or "Нет терминала",
                    _ => true
                })
                .OrderByDescending(x => x.DateValue)
                .ThenBy(x => x.Point)
                .ToArray();

            grid.ItemsSource = rows;

            var cashTotal = SumNullable(rows.Select(x => x.CashElectronic));
            var bankTotal = SumNullable(rows.Select(x => x.BankElectronic));
            var comparable = rows.Where(x => x.CashElectronic is not null && x.BankElectronic is not null).ToArray();
            var differenceTotal = comparable.Length == 0 ? (decimal?)null : comparable.Sum(x => x.Difference ?? 0m);
            var mismatchCount = rows.Count(x => x.Status == "Расхождение");
            var matchedCount = rows.Count(x => x.Status == "Сошлось");
            var missingCount = rows.Count(x => x.Status is "Нет кассы" or "Нет терминала");

            totals.Text = $"Касса безнал: {MoneyText(cashTotal)}     •     Терминалы: {MoneyText(bankTotal)}     •     Разница: {MoneyText(differenceTotal)}\n" +
                          $"Сошлось: {matchedCount}     •     Расхождений: {mismatchCount}     •     Нет одной стороны: {missingCount}";
        }

        yearBox.SelectionChanged += (_, _) => RefreshData();
        monthBox.SelectionChanged += (_, _) => RefreshData();
        pointBox.SelectionChanged += (_, _) => RefreshData();
        statusBox.SelectionChanged += (_, _) => RefreshData();
        RefreshData();
        return root;
    }

    private static TextBlock FilterLabel(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 7, 0)
    };

    private static DataGrid BuildReconciliationGrid()
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            SelectionMode = DataGridSelectionMode.Single,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(248, 250, 252))
        };

        grid.Columns.Add(TextColumn("Дата", "Date", 105));
        grid.Columns.Add(TextColumn("Точка", "Point", new DataGridLength(2, DataGridLengthUnitType.Star)));
        grid.Columns.Add(MoneyColumn("Касса безнал", "CashElectronic", 135));
        grid.Columns.Add(MoneyColumn("Терминал безнал", "BankElectronic", 145));
        grid.Columns.Add(MoneyColumn("Разница", "Difference", 120));
        grid.Columns.Add(TextColumn("Смен", "ShiftCount", 65));
        grid.Columns.Add(TextColumn("Статус", "Status", 150));

        var rowStyle = new Style(typeof(DataGridRow));
        var mismatch = new DataTrigger { Binding = new Binding("Status"), Value = "Расхождение" };
        mismatch.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(254, 226, 226))));
        rowStyle.Triggers.Add(mismatch);
        var matched = new DataTrigger { Binding = new Binding("Status"), Value = "Сошлось" };
        matched.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(220, 252, 231))));
        rowStyle.Triggers.Add(matched);
        var missingCash = new DataTrigger { Binding = new Binding("Status"), Value = "Нет кассы" };
        missingCash.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(254, 249, 195))));
        rowStyle.Triggers.Add(missingCash);
        var missingBank = new DataTrigger { Binding = new Binding("Status"), Value = "Нет терминала" };
        missingBank.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(254, 249, 195))));
        rowStyle.Triggers.Add(missingBank);
        grid.RowStyle = rowStyle;
        return grid;
    }

    private static string ReconciliationStatus(decimal? cash, decimal? bank, decimal? difference)
    {
        if (cash is null && bank is not null) return "Нет кассы";
        if (cash is not null && bank is null) return "Нет терминала";
        if (cash is null && bank is null) return "Нет данных";
        return difference == 0m ? "Сошлось" : "Расхождение";
    }
}

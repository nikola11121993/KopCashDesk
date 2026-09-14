using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

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
        decimal? CashElectronic,
        bool HasActualFiscal,
        bool CashFromSber,
        bool CanCopySber,
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
        decimal? CashElectronic,
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
        PageSubtitle.Text = "Фактические импортированные данные: терминалы, касса и закрытия смен";

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

        DataGrid dailyGrid = null!;
        DataGrid monthlyGrid = null!;

        void ToggleSberCopy(DaySummaryRow row, bool isChecked)
        {
            if (!row.CanCopySber || row.Sber is null)
            {
                MessageBox.Show("На этот день нельзя автоматически перенести сумму: либо нет Сбера, либо уже загружены реальные кассовые данные.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshData();
                return;
            }

            if (isChecked)
            {
                _db.SetManualCash(row.OrganizationId, row.LocationId, row.DateValue, row.Sber.Value);
                StatusText.Text = $"{row.Point}: {row.Sber.Value:N2} ₽ внесено в кассу за {row.Date}";
            }
            else
            {
                _db.ClearManualCash(row.OrganizationId, row.LocationId, row.DateValue);
                StatusText.Text = $"{row.Point}: ручная сумма кассы за {row.Date} снята";
            }

            RefreshData();
        }

        void EditManualCash(DaySummaryRow row)
        {
            if (row.HasActualFiscal)
            {
                MessageBox.Show("За этот день уже загружены реальные кассовые данные. Ручная сумма их не заменяет.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new ManualCashEditWindow(row.Point, row.DateValue, row.CashElectronic) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            if (dialog.ClearRequested)
            {
                _db.ClearManualCash(row.OrganizationId, row.LocationId, row.DateValue);
                StatusText.Text = $"{row.Point}: ручная сумма кассы за {row.Date} очищена";
            }
            else if (dialog.Value is decimal value)
            {
                _db.SetManualCash(row.OrganizationId, row.LocationId, row.DateValue, value);
                StatusText.Text = $"{row.Point}: касса за {row.Date} = {value:N2} ₽";
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

            var dayRows = rows.Select(x => ToDayRow(x, manual)).OrderByDescending(x => x.DateValue).ThenBy(x => x.Point).ToArray();
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
                    var copied = g.Any(x => x.CashFromSber);
                    var status = complete
                        ? SummaryStatus(bank, cash, shiftTotal, g.Sum(x => x.ShiftCount), difference, copied)
                        : "Неполные данные";
                    return new MonthSummaryRow(
                        g.Key.Year, g.Key.Month, g.Key.OrganizationId, g.Key.LocationId, g.Key.Organization, g.Key.Point,
                        bank, cash, shiftTotal, g.Sum(x => x.ShiftCount), lastClosed, difference, status);
                })
                .OrderByDescending(x => x.Year)
                .ThenByDescending(x => x.Month)
                .ThenBy(x => x.Point)
                .ToArray();
            monthlyGrid.ItemsSource = monthRows;

            var bankTotal = SumNullable(dayRows.Select(x => x.Sber));
            var cashTotal = SumNullable(dayRows.Select(x => x.CashElectronic));
            var shiftGrandTotal = SumNullable(dayRows.Select(x => x.ShiftTotal));
            var incompleteDays = dayRows.Count(x => (x.Sber is null) != (x.CashElectronic is null));
            totals.Text = $"Сбер за период: {MoneyText(bankTotal)}     •     Касса безнал: {MoneyText(cashTotal)}     •     Закрыто сменами: {MoneyText(shiftGrandTotal)}" +
                          (incompleteDays > 0 ? $"     •     Неполных дней: {incompleteDays}" : "");
        }

        yearBox.SelectionChanged += (_, _) => RefreshData();
        monthBox.SelectionChanged += (_, _) => RefreshData();
        locationBox.SelectionChanged += (_, _) => RefreshData();
        RefreshData();
        return root;
    }

    private static DataGrid BuildDailySummaryGrid(
        Action<DaySummaryRow, bool> toggleSberCopy,
        Action<DaySummaryRow> editManualCash,
        Action<DaySummaryRow>? editManualTerminal = null,
        Action<DaySummaryRow>? deleteManualDay = null)
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            SelectionMode = DataGridSelectionMode.Single,
            CanUserAddRows = false,
            CanUserDeleteRows = false
        };
        grid.Columns.Add(TextColumn("Дата", "Date", 105));
        grid.Columns.Add(TextColumn("Точка", "Point", new DataGridLength(2, DataGridLengthUnitType.Star)));
        grid.Columns.Add(MoneyColumn("Терминал безнал", "Sber", 130));
        grid.Columns.Add(MoneyColumn("Касса безнал", "CashElectronic", 125));
        grid.Columns.Add(MoneyColumn("Закрыто сменой", "ShiftTotal", 135));
        grid.Columns.Add(TextColumn("Смен", "ShiftCount", 60));
        grid.Columns.Add(TextColumn("Когда закрыли", "ClosedAt", 145));
        grid.Columns.Add(MoneyColumn("Разница", "Difference", 110));
        grid.Columns.Add(TextColumn("Статус", "Status", new DataGridLength(2, DataGridLengthUnitType.Star)));

        var factory = new FrameworkElementFactory(typeof(CheckBox));
        factory.SetBinding(CheckBox.IsCheckedProperty, new Binding("CashFromSber") { Mode = BindingMode.OneWay });
        factory.SetBinding(CheckBox.IsEnabledProperty, new Binding("CanCopySber") { Mode = BindingMode.OneWay });
        factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.SetValue(FrameworkElement.ToolTipProperty, "Поставить сумму терминала в кассу за этот день. Галочка не создаёт фискальный чек.");
        factory.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is CheckBox checkBox && checkBox.DataContext is DaySummaryRow row)
                toggleSberCopy(row, checkBox.IsChecked == true);
        }));
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "В кассу",
            CellTemplate = new DataTemplate { VisualTree = factory },
            Width = 75
        });

        grid.MouseDoubleClick += (_, _) =>
        {
            if (grid.SelectedItem is not DaySummaryRow row) return;
            var header = grid.CurrentCell.Column?.Header?.ToString();
            if (string.Equals(header, "Касса безнал", StringComparison.Ordinal))
                editManualCash(row);
            else if (string.Equals(header, "Терминал безнал", StringComparison.Ordinal) && editManualTerminal is not null)
                editManualTerminal(row);
        };

        grid.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && deleteManualDay is not null && grid.SelectedItem is DaySummaryRow row)
            {
                deleteManualDay(row);
                e.Handled = true;
            }
        };

        var menu = new ContextMenu();
        if (editManualTerminal is not null)
        {
            var editTerminal = new MenuItem { Header = "Изменить сумму терминала..." };
            editTerminal.Click += (_, _) =>
            {
                if (grid.SelectedItem is DaySummaryRow row) editManualTerminal(row);
            };
            menu.Items.Add(editTerminal);
        }

        var edit = new MenuItem { Header = "Изменить сумму кассы..." };
        edit.Click += (_, _) =>
        {
            if (grid.SelectedItem is DaySummaryRow row) editManualCash(row);
        };
        menu.Items.Add(edit);

        var copy = new MenuItem { Header = "Поставить сумму терминала в кассу" };
        copy.Click += (_, _) =>
        {
            if (grid.SelectedItem is DaySummaryRow row) toggleSberCopy(row, true);
        };
        menu.Items.Add(copy);

        if (deleteManualDay is not null)
        {
            menu.Items.Add(new Separator());
            var delete = new MenuItem { Header = "Удалить ручные данные за день" };
            delete.Click += (_, _) =>
            {
                if (grid.SelectedItem is DaySummaryRow row) deleteManualDay(row);
            };
            menu.Items.Add(delete);
        }
        grid.ContextMenu = menu;

        return grid;
    }

    private static DataGrid BuildMonthlySummaryGrid()
    {
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single };
        grid.Columns.Add(TextColumn("Месяц", "Period", 175));
        grid.Columns.Add(TextColumn("Точка", "Point", new DataGridLength(2, DataGridLengthUnitType.Star)));
        grid.Columns.Add(MoneyColumn("Терминал безнал", "Sber", 130));
        grid.Columns.Add(MoneyColumn("Касса безнал", "CashElectronic", 125));
        grid.Columns.Add(MoneyColumn("Закрыто сменами", "ShiftTotal", 140));
        grid.Columns.Add(TextColumn("Смен", "ShiftCount", 60));
        grid.Columns.Add(TextColumn("Последнее закрытие", "ClosedAt", 155));
        grid.Columns.Add(MoneyColumn("Разница", "Difference", 110));
        grid.Columns.Add(TextColumn("Статус", "Status", new DataGridLength(2, DataGridLengthUnitType.Star)));
        return grid;
    }

    private static DataGridTextColumn TextColumn(string header, string property, double width) =>
        TextColumn(header, property, new DataGridLength(width));

    private static DataGridTextColumn TextColumn(string header, string property, DataGridLength width) => new()
    {
        Header = header,
        Binding = new Binding(property),
        Width = width,
        IsReadOnly = true
    };

    private static DataGridTextColumn MoneyColumn(string header, string property, double width) => new()
    {
        Header = header,
        Binding = new Binding(property) { StringFormat = "N2", TargetNullValue = "—" },
        Width = width,
        IsReadOnly = true
    };

    private static DaySummaryRow ToDayRow(
        PointDaySummary row,
        IReadOnlyDictionary<(Guid OrganizationId, Guid LocationId, DateOnly Date), decimal> manual)
    {
        var hasManual = manual.TryGetValue((row.OrganizationId, row.LocationId, row.Date), out var manualElectronic);
        var hasActualFiscal = row.FiscalElectronic is not null;
        var cashElectronic = row.FiscalElectronic ?? (hasManual ? manualElectronic : null);
        var copiedFromSber = !hasActualFiscal && hasManual && row.BankElectronic is not null && manualElectronic == row.BankElectronic.Value;
        var difference = row.BankElectronic is not null && cashElectronic is not null
            ? cashElectronic - row.BankElectronic
            : null;

        return new DaySummaryRow(
            row.Date, row.OrganizationId, row.LocationId, row.Organization, row.Location,
            row.BankElectronic, cashElectronic, hasActualFiscal, copiedFromSber,
            row.BankElectronic is not null && !hasActualFiscal,
            row.ShiftTotal, row.ShiftCount, row.LastShiftClosedAt,
            difference, SummaryStatus(row.BankElectronic, cashElectronic, row.ShiftTotal, row.ShiftCount, difference, copiedFromSber));
    }

    private static string SummaryStatus(decimal? bank, decimal? cash, decimal? shiftTotal, int shiftCount, decimal? difference, bool copiedFromSber)
    {
        if (bank is not null && cash is null && shiftCount == 0) return "Сбер загружен, кассы нет";
        if (bank is null && (cash is not null || shiftCount > 0)) return "Касса есть, Сбера нет";
        if (bank is not null && cash is not null)
        {
            if (difference == 0m) return copiedFromSber ? "Сошлось — внесено из Сбера" : "Сошлось";
            return "Есть расхождение";
        }
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

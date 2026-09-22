using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Core;
using KopCashDesk.Data;
using Microsoft.Win32;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private sealed record ReconciliationLocationOption(Guid? Id, string Name);
    private sealed record ReconciliationMonthOption(int? Number, string Name);

    private sealed record ReconciliationRow(ReconciliationDay Day, decimal AccumulatedNeedToPunch)
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
        public decimal AccumulatedBalance => AccumulatedNeedToPunch;
        public string AccumulatedAction => AccumulatedNeedToPunch > 0m
            ? $"Пробить {AccumulatedNeedToPunch:N2} ₽"
            : AccumulatedNeedToPunch < 0m
                ? $"Перебито {Math.Abs(AccumulatedNeedToPunch):N2} ₽"
                : "Сошлось";
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
        var exportButton = new Button
        {
            Content = "Выгрузить Excel",
            Padding = new Thickness(12, 5, 12, 5)
        };
        filters.Children.Add(exportButton);
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
        grid.Columns.Add(ReconciliationTextColumn("Накопительно", "AccumulatedAction", 170));
        grid.Columns.Add(ReconciliationTextColumn("Статус", "Status", 210));
        grid.Columns.Add(ReconciliationTextColumn("Закрытие смены", "ClosedAt", 145));
        grid.Columns.Add(ReconciliationTextColumn("№ смены", "Shift", 95));
        Grid.SetRow(grid, 2);
        root.Children.Add(grid);

        ReconciliationRow[] currentRows = [];
        ReconciliationDay[] currentAllTimeDays = [];

        void RefreshRows()
        {
            if (yearBox.SelectedItem is not int year) return;
            var month = (monthBox.SelectedItem as ReconciliationMonthOption)?.Number;
            var locationId = (locationBox.SelectedItem as ReconciliationLocationOption)?.Id;
            var conflictKeys = _db.FiscalSourceConflicts()
                .Select(x => (x.OrganizationId, x.LocationId, x.BusinessDate))
                .ToHashSet();

            // Баланс считаем по всей истории выбранной точки.
            // Год и месяц меняют только видимые строки и не обнуляют старые ошибки.
            var allTimeDays = _db.ReconciliationDays(SelectedOrganizationId, locationId: locationId)
                .Select(x => conflictKeys.Contains((x.OrganizationId, x.LocationId, x.Date))
                    ? x with
                    {
                        CashElectronic = null,
                        RequiresReview = true,
                        Status = "Конфликт кассовых источников — требуется проверка"
                    }
                    : x)
                .ToArray();

            var accumulatedByDay = new Dictionary<(Guid LocationId, DateOnly Date), decimal>();
            foreach (var pointDays in allTimeDays.GroupBy(x => x.LocationId))
            {
                var accumulated = 0m;
                foreach (var day in pointDays.OrderBy(x => x.Date))
                {
                    accumulated += (day.BankElectronic ?? 0m) - (day.CashElectronic ?? 0m);
                    accumulatedByDay[(day.LocationId, day.Date)] = Money.Normalize(accumulated);
                }
            }

            var days = allTimeDays
                .Where(x => x.Date.Year == year && (month is null || x.Date.Month == month))
                .ToArray();
            var rows = days
                .Select(x => new ReconciliationRow(x, accumulatedByDay.GetValueOrDefault((x.LocationId, x.Date))))
                .ToArray();
            currentRows = rows;
            currentAllTimeDays = allTimeDays;
            grid.ItemsSource = rows;

            var terminalKnown = days.Where(x => x.BankElectronic is not null).Select(x => x.BankElectronic!.Value).ToArray();
            var cashKnown = days.Where(x => x.CashElectronic is not null).Select(x => x.CashElectronic!.Value).ToArray();
            var latestByPoint = days.GroupBy(x => x.LocationId).Select(g => g.OrderByDescending(x => x.Date).First()).ToArray();
            var remaining = latestByPoint.Sum(x => x.CumulativeOutstanding);
            var missingCash = days.Count(x => x.HasBankData && !x.HasCashData && x.DayRemaining > 0m);
            var review = days.Count(x => x.RequiresReview);

            var allTimeTerminal = allTimeDays.Sum(x => x.BankElectronic ?? 0m);
            var allTimeCash = allTimeDays.Sum(x => x.CashElectronic ?? 0m);
            var needToPunch = Money.Normalize(allTimeTerminal - allTimeCash);
            var allTimeReview = allTimeDays.Count(x => x.RequiresReview);
            var action = needToPunch > 0m
                ? $"НАДО ПРОБИТЬ: {needToPunch:N2} ₽"
                : needToPunch < 0m
                    ? $"ПЕРЕБИТО: {Math.Abs(needToPunch):N2} ₽"
                    : "СОШЛОСЬ: 0,00 ₽";
            var accumulatedHeader = locationId is null
                ? $"ВСЕ ТОЧКИ — общий баланс: {needToPunch:N2} ₽; точную сумму для каждой точки см. в колонке «Накопительно»"
                : $"НАКОПИТЕЛЬНО ЗА ВСЁ ВРЕМЯ — {action}";

            totals.Text =
                $"{accumulatedHeader}     •     Терминалы: {allTimeTerminal:N2} ₽     •     Касса: {allTimeCash:N2} ₽" +
                (allTimeReview > 0 ? $"     •     На проверке: {allTimeReview}" : "") +
                Environment.NewLine +
                $"Выбранный период — Терминалы: {(terminalKnown.Length == 0 ? "нет данных" : terminalKnown.Sum().ToString("N2") + " ₽")}     •     " +
                $"Касса: {(cashKnown.Length == 0 ? "нет данных" : cashKnown.Sum().ToString("N2") + " ₽")}     •     " +
                $"FIFO-остаток: {remaining:N2} ₽     •     " +
                $"Дней без кассовых данных: {missingCash}     •     Требует проверки: {review}";
        }

        exportButton.Click += (_, _) =>
        {
            if (yearBox.SelectedItem is not int selectedYear) return;
            var selectedMonth = (monthBox.SelectedItem as ReconciliationMonthOption)?.Number;
            var selectedLocation = locationBox.SelectedItem as ReconciliationLocationOption;
            ExportReconciliationExcel(currentRows, currentAllTimeDays, selectedYear, selectedMonth, selectedLocation?.Name ?? "Все точки");
        };

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

    private void ExportReconciliationExcel(
        IReadOnlyList<ReconciliationRow> rows,
        IReadOnlyList<ReconciliationDay> allTimeDays,
        int year,
        int? month,
        string locationName)
    {
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "За выбранный период нет строк для выгрузки.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var period = month is int m
            ? $"{culture.DateTimeFormat.GetMonthName(m)} {year}"
            : $"весь {year} год";
        var safePoint = string.Concat(locationName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var dialog = new SaveFileDialog
        {
            Title = "Выгрузить сверку в Excel",
            Filter = "Excel (*.xlsx)|*.xlsx",
            FileName = $"Сверка_{safePoint}_{year}{(month is int selectedMonth ? $"-{selectedMonth:00}" : "")}.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx"
        };
        if (dialog.ShowDialog(this) != true) return;

        var allTimeTerminal = allTimeDays.Sum(x => x.BankElectronic ?? 0m);
        var allTimeCash = allTimeDays.Sum(x => x.CashElectronic ?? 0m);
        var allTimeBalance = Money.Normalize(allTimeTerminal - allTimeCash);
        var action = allTimeBalance > 0m
            ? $"Надо пробить {allTimeBalance:N2} ₽"
            : allTimeBalance < 0m
                ? $"Перебито {Math.Abs(allTimeBalance):N2} ₽"
                : "Сошлось 0,00 ₽";

        using var document = SpreadsheetDocument.Create(dialog.FileName, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = BuildReconciliationStyles();
        stylesPart.Stylesheet.Save();

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var columns = new Columns(
            new Column { Min = 1, Max = 1, Width = 13, CustomWidth = true },
            new Column { Min = 2, Max = 2, Width = 28, CustomWidth = true },
            new Column { Min = 3, Max = 10, Width = 16, CustomWidth = true },
            new Column { Min = 11, Max = 11, Width = 23, CustomWidth = true },
            new Column { Min = 12, Max = 12, Width = 38, CustomWidth = true },
            new Column { Min = 13, Max = 14, Width = 20, CustomWidth = true });
        var sheetData = new SheetData();
        worksheetPart.Worksheet = new Worksheet(columns, sheetData);

        sheetData.Append(ExcelTextRow("Сверка касса ↔ терминал", 1));
        sheetData.Append(ExcelTextRow($"Период: {period}"));
        sheetData.Append(ExcelTextRow($"Точка: {locationName}"));
        sheetData.Append(ExcelMixedRow(
            ("Терминалы за всё время", allTimeTerminal),
            ("Касса за всё время", allTimeCash),
            ("Накопительный баланс", allTimeBalance)));
        sheetData.Append(ExcelTextRow($"Итог: {action}", 1));
        sheetData.Append(new Row());

        var headers = new[]
        {
            "Дата", "Точка", "Терминалы", "Касса безнал", "Непробито ранее", "Погашено кассой",
            "Пробито позже", "Остаток за день", "Общий FIFO остаток", "Накопительный баланс",
            "Действие", "Статус", "Закрытие смены", "№ смены"
        };
        var headerRow = new Row();
        foreach (var header in headers) headerRow.Append(ExcelTextCell(header, 1));
        sheetData.Append(headerRow);

        foreach (var row in rows)
        {
            var excelRow = new Row();
            excelRow.Append(ExcelTextCell(row.Date));
            excelRow.Append(ExcelTextCell(row.Point));
            excelRow.Append(ExcelMoneyCell(row.Terminal));
            excelRow.Append(ExcelMoneyCell(row.Cash));
            excelRow.Append(ExcelMoneyCell(row.Prior));
            excelRow.Append(ExcelMoneyCell(row.CashApplied));
            excelRow.Append(ExcelMoneyCell(row.ClosedLater));
            excelRow.Append(ExcelMoneyCell(row.DayRemaining));
            excelRow.Append(ExcelMoneyCell(row.TotalRemaining));
            excelRow.Append(ExcelMoneyCell(row.AccumulatedBalance));
            excelRow.Append(ExcelTextCell(row.AccumulatedAction));
            excelRow.Append(ExcelTextCell(row.Status));
            excelRow.Append(ExcelTextCell(row.ClosedAt));
            excelRow.Append(ExcelTextCell(row.Shift));
            sheetData.Append(excelRow);
        }

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Сверка"
        });
        worksheetPart.Worksheet.Save();
        workbookPart.Workbook.Save();

        StatusText.Text = $"Excel сохранён: {dialog.FileName}";
        MessageBox.Show(this, $"Excel-файл сохранён.\n\n{dialog.FileName}", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static Stylesheet BuildReconciliationStyles() => new(
        new Fonts(
            new Font(),
            new Font(new Bold())),
        new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 })),
        new Borders(new Border()),
        new CellStyleFormats(new CellFormat()),
        new CellFormats(
            new CellFormat(),
            new CellFormat { FontId = 1, ApplyFont = true },
            new CellFormat { NumberFormatId = 4, ApplyNumberFormat = true }));

    private static Row ExcelTextRow(string text, uint styleIndex = 0)
    {
        var row = new Row();
        row.Append(ExcelTextCell(text, styleIndex));
        return row;
    }

    private static Row ExcelMixedRow(params (string Label, decimal Value)[] values)
    {
        var row = new Row();
        foreach (var item in values)
        {
            row.Append(ExcelTextCell(item.Label, 1));
            row.Append(ExcelMoneyCell(item.Value));
        }
        return row;
    }

    private static Cell ExcelTextCell(string? value, uint styleIndex = 0) => new()
    {
        DataType = CellValues.InlineString,
        StyleIndex = styleIndex,
        InlineString = new InlineString(new Text(value ?? string.Empty))
    };

    private static Cell ExcelMoneyCell(decimal? value)
    {
        if (value is null) return ExcelTextCell("—");
        return new Cell
        {
            DataType = CellValues.Number,
            StyleIndex = 2,
            CellValue = new CellValue(value.Value.ToString(CultureInfo.InvariantCulture))
        };
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

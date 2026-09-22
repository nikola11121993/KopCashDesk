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
        public decimal DayDifference => Money.Normalize((Day.BankElectronic ?? 0m) - (Day.CashElectronic ?? 0m));
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

    private sealed record ReconciliationMonthRow(
        int Year,
        int Month,
        Guid LocationId,
        string Point,
        decimal Terminal,
        decimal Cash,
        decimal Difference,
        decimal AccumulatedBalance,
        int ReviewDays)
    {
        public string Period => $"{Month:00}.{Year}";
        public string Action => AccumulatedBalance > 0m
            ? $"Пробить {AccumulatedBalance:N2} ₽"
            : AccumulatedBalance < 0m
                ? $"Перебито {Math.Abs(AccumulatedBalance):N2} ₽"
                : "Сошлось";
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
        PageSubtitle.Text = "Главный год сверки — 2026. Данные прошлых лет полностью исключены из расчёта.";

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        const int primaryYear = 2026;
        var years = new[] { primaryYear };
        var yearBox = new ComboBox
        {
            Width = 105,
            ItemsSource = years,
            SelectedItem = primaryYear,
            IsEnabled = false,
            Margin = new Thickness(0, 0, 12, 6),
            ToolTip = "Сверка фиксирована на 2026 год. Прошлые годы не участвуют в расчёте."
        };
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var monthOptions = new List<ReconciliationMonthOption> { new(null, "Все месяцы") };
        for (var month = 1; month <= 12; month++)
            monthOptions.Add(new(month, culture.DateTimeFormat.GetMonthName(month)));
        var monthBox = new ComboBox { Width = 155, ItemsSource = monthOptions, DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new Thickness(0, 0, 12, 6) };

        var locationOptions = new List<ReconciliationLocationOption> { new(null, "Все точки") };
        locationOptions.AddRange(_locations
            .Where(x => SelectedOrganizationId is null || x.OrganizationId == SelectedOrganizationId)
            .OrderBy(x => x.Name)
            .Select(x => new ReconciliationLocationOption(x.Id, x.Name)));
        var locationBox = new ComboBox { Width = 285, ItemsSource = locationOptions, DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new Thickness(0, 0, 12, 6) };

        var exportButton = new Button
        {
            Content = "ЭКСПОРТ В EXCEL — ДНИ + МЕСЯЦЫ",
            MinWidth = 245,
            Padding = new Thickness(14, 7, 14, 7),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 6),
            ToolTip = "Один Excel-файл: Итоги, По дням и По месяцам"
        };

        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        filters.Children.Add(new TextBlock { Text = "Год:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 6) });
        filters.Children.Add(yearBox);
        filters.Children.Add(new TextBlock { Text = "Месяц:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 6) });
        filters.Children.Add(monthBox);
        filters.Children.Add(new TextBlock { Text = "Точка:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 6) });
        filters.Children.Add(locationBox);
        filters.Children.Add(exportButton);
        root.Children.Add(filters);

        var accumulatedTitle = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap
        };
        var accumulatedDetails = new TextBlock
        {
            FontSize = 14,
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        var totalsPanel = new StackPanel();
        totalsPanel.Children.Add(accumulatedTitle);
        totalsPanel.Children.Add(accumulatedDetails);
        var totalsCard = new System.Windows.Controls.Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 12),
            Child = totalsPanel
        };
        Grid.SetRow(totalsCard, 1);
        root.Children.Add(totalsCard);

        var dailyGrid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            FrozenColumnCount = 2
        };
        dailyGrid.Columns.Add(ReconciliationTextColumn("Дата", "Date", 100));
        dailyGrid.Columns.Add(ReconciliationTextColumn("Точка", "Point", new DataGridLength(2, DataGridLengthUnitType.Star)));
        dailyGrid.Columns.Add(ReconciliationMoneyColumn("Терминалы", "Terminal", 110));
        dailyGrid.Columns.Add(ReconciliationMoneyColumn("Касса безнал", "Cash", 115));
        dailyGrid.Columns.Add(ReconciliationMoneyColumn("Разница за день", "DayDifference", 125));
        dailyGrid.Columns.Add(ReconciliationMoneyColumn("Накопительно с начала года", "AccumulatedBalance", 165));
        dailyGrid.Columns.Add(ReconciliationTextColumn("Что делать", "AccumulatedAction", 170));
        dailyGrid.Columns.Add(ReconciliationTextColumn("Статус", "Status", 220));
        dailyGrid.Columns.Add(ReconciliationTextColumn("Закрытие смены", "ClosedAt", 145));
        dailyGrid.Columns.Add(ReconciliationTextColumn("№ смены", "Shift", 95));

        var monthlyGrid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            FrozenColumnCount = 2
        };
        monthlyGrid.Columns.Add(ReconciliationTextColumn("Месяц", "Period", 95));
        monthlyGrid.Columns.Add(ReconciliationTextColumn("Точка", "Point", new DataGridLength(2, DataGridLengthUnitType.Star)));
        monthlyGrid.Columns.Add(ReconciliationMoneyColumn("Терминалы", "Terminal", 120));
        monthlyGrid.Columns.Add(ReconciliationMoneyColumn("Касса безнал", "Cash", 120));
        monthlyGrid.Columns.Add(ReconciliationMoneyColumn("Разница месяца", "Difference", 130));
        monthlyGrid.Columns.Add(ReconciliationMoneyColumn("Накопительно с начала года", "AccumulatedBalance", 170));
        monthlyGrid.Columns.Add(ReconciliationTextColumn("Что делать", "Action", 175));
        monthlyGrid.Columns.Add(ReconciliationTextColumn("Дней на проверке", "ReviewDays", 125));

        var selectedMonthsTitle = new TextBlock
        {
            Text = "Выдели нужные месяцы мышкой. Несколько подряд — Shift, выборочно — Ctrl.",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        var selectedMonthsTotals = new TextBlock
        {
            Text = "Выбранные месяцы: нет",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        };
        var selectedMonthsPanel = new StackPanel();
        selectedMonthsPanel.Children.Add(selectedMonthsTitle);
        selectedMonthsPanel.Children.Add(selectedMonthsTotals);

        var selectedMonthsCard = new System.Windows.Controls.Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = selectedMonthsPanel
        };

        var monthlyTabRoot = new Grid();
        monthlyTabRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        monthlyTabRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        monthlyTabRoot.Children.Add(selectedMonthsCard);
        Grid.SetRow(monthlyGrid, 1);
        monthlyTabRoot.Children.Add(monthlyGrid);

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "По дням", Content = dailyGrid });
        tabs.Items.Add(new TabItem { Header = "По месяцам", Content = monthlyTabRoot });
        Grid.SetRow(tabs, 2);
        root.Children.Add(tabs);

        ReconciliationRow[] currentDailyRows = [];
        ReconciliationMonthRow[] currentMonthlyRows = [];
        ReconciliationDay[] currentAllTimeDays = [];

        void RefreshSelectedMonthsTotals()
        {
            var selected = monthlyGrid.SelectedItems
                .OfType<ReconciliationMonthRow>()
                .ToArray();

            if (selected.Length == 0)
            {
                selectedMonthsTotals.Text = "Выбранные месяцы: нет";
                return;
            }

            var terminal = Money.Normalize(selected.Sum(x => x.Terminal));
            var cash = Money.Normalize(selected.Sum(x => x.Cash));
            var difference = Money.Normalize(terminal - cash);
            var action = difference > 0m
                ? $"НАДО ПРОБИТЬ {difference:N2} ₽"
                : difference < 0m
                    ? $"ПЕРЕБИТО {Math.Abs(difference):N2} ₽"
                    : "СОШЛОСЬ 0,00 ₽";

            var distinctMonths = selected
                .Select(x => x.Period)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();

            selectedMonthsTotals.Text =
                $"Выбрано месяцев: {distinctMonths.Length} ({string.Join(", ", distinctMonths)})     •     " +
                $"Терминалы: {terminal:N2} ₽     •     Касса: {cash:N2} ₽     •     " +
                $"Разница: {difference:N2} ₽     •     {action}";
        }

        void RefreshRows()
        {
            if (yearBox.SelectedItem is not int year) return;
            var month = (monthBox.SelectedItem as ReconciliationMonthOption)?.Number;
            var locationId = (locationBox.SelectedItem as ReconciliationLocationOption)?.Id;

            // Сверка и накопительный остаток считаются ТОЛЬКО внутри выбранного года.
            // Старые кассовые данные 2023–2025 не должны влиять на 2026 год.
            // Конфликтный день не обнуляем: ReconciliationDays сохраняет сумму Frontol
            // и отдельно помечает такой день как требующий проверки.
            var yearDays = _db.ReconciliationDays(
                    SelectedOrganizationId,
                    year: year,
                    locationId: locationId)
                .ToArray();

            var accumulatedByDay = new Dictionary<(Guid LocationId, DateOnly Date), decimal>();
            foreach (var pointDays in yearDays.GroupBy(x => x.LocationId))
            {
                var accumulated = 0m;
                foreach (var day in pointDays.OrderBy(x => x.Date))
                {
                    accumulated += (day.BankElectronic ?? 0m) - (day.CashElectronic ?? 0m);
                    accumulatedByDay[(day.LocationId, day.Date)] = Money.Normalize(accumulated);
                }
            }

            var visibleDays = yearDays
                .Where(x => month is null || x.Date.Month == month)
                .OrderByDescending(x => x.Date)
                .ThenBy(x => x.Location)
                .ToArray();

            currentDailyRows = visibleDays
                .Select(x => new ReconciliationRow(x, accumulatedByDay.GetValueOrDefault((x.LocationId, x.Date))))
                .ToArray();
            dailyGrid.ItemsSource = currentDailyRows;

            currentMonthlyRows = yearDays
                .GroupBy(x => new { x.Date.Year, x.Date.Month, x.LocationId, Point = x.Location })
                .Select(g =>
                {
                    var terminal = Money.Normalize(g.Sum(x => x.BankElectronic ?? 0m));
                    var cash = Money.Normalize(g.Sum(x => x.CashElectronic ?? 0m));
                    var lastDate = g.Max(x => x.Date);
                    var accumulated = accumulatedByDay.GetValueOrDefault((g.Key.LocationId, lastDate));
                    return new ReconciliationMonthRow(
                        g.Key.Year,
                        g.Key.Month,
                        g.Key.LocationId,
                        g.Key.Point,
                        terminal,
                        cash,
                        Money.Normalize(terminal - cash),
                        accumulated,
                        g.Count(x => x.RequiresReview));
                })
                .Where(x => x.Year == year && (month is null || x.Month == month))
                .OrderByDescending(x => x.Year)
                .ThenByDescending(x => x.Month)
                .ThenBy(x => x.Point)
                .ToArray();
            monthlyGrid.ItemsSource = currentMonthlyRows;
            monthlyGrid.SelectedItems.Clear();
            RefreshSelectedMonthsTotals();

            currentAllTimeDays = yearDays;

            var yearTerminal = Money.Normalize(yearDays.Sum(x => x.BankElectronic ?? 0m));
            var yearCash = Money.Normalize(yearDays.Sum(x => x.CashElectronic ?? 0m));
            var needToPunch = Money.Normalize(yearTerminal - yearCash);
            var review = yearDays.Count(x => x.RequiresReview);
            var selectedPointName = (locationBox.SelectedItem as ReconciliationLocationOption)?.Name ?? "Все точки";

            if (locationId is null)
            {
                accumulatedTitle.Text = $"ВСЕ ТОЧКИ — СУММАРНАЯ РАЗНИЦА: {needToPunch:N2} ₽";
                accumulatedDetails.Text =
                    $"Терминалы за {year} год: {yearTerminal:N2} ₽     •     Касса безнал за {year} год: {yearCash:N2} ₽     •     " +
                    $"Для точной суммы «сколько пробить» по конкретной кассе выбери точку. На проверке дней: {review}.";
            }
            else if (needToPunch > 0m)
            {
                accumulatedTitle.Text = $"НАДО ПРОБИТЬ НА КАССЕ: {needToPunch:N2} ₽";
                accumulatedDetails.Text =
                    $"{selectedPointName}. Накопительно за {year} год: терминалы {yearTerminal:N2} ₽ − касса {yearCash:N2} ₽. " +
                    $"После исправления эта сумма должна стать 0,00 ₽. На проверке дней: {review}.";
            }
            else if (needToPunch < 0m)
            {
                accumulatedTitle.Text = $"НА КАССЕ ПЕРЕБИТО: {Math.Abs(needToPunch):N2} ₽";
                accumulatedDetails.Text =
                    $"{selectedPointName}. Накопительно за {year} год: терминалы {yearTerminal:N2} ₽ − касса {yearCash:N2} ₽. " +
                    $"На проверке дней: {review}.";
            }
            else
            {
                accumulatedTitle.Text = "КАССА И ТЕРМИНАЛ СОШЛИСЬ: 0,00 ₽";
                accumulatedDetails.Text =
                    $"{selectedPointName}. Накопительно за {year} год: терминалы {yearTerminal:N2} ₽, касса {yearCash:N2} ₽. " +
                    $"На проверке дней: {review}.";
            }
        }

        exportButton.Click += (_, _) =>
        {
            if (yearBox.SelectedItem is not int selectedYear) return;
            var selectedMonth = (monthBox.SelectedItem as ReconciliationMonthOption)?.Number;
            var selectedLocation = locationBox.SelectedItem as ReconciliationLocationOption;
            ExportReconciliationExcel(
                currentDailyRows,
                currentMonthlyRows,
                currentAllTimeDays,
                selectedYear,
                selectedMonth,
                selectedLocation?.Name ?? "Все точки");
        };

        yearBox.SelectionChanged += (_, _) => RefreshRows();
        monthBox.SelectionChanged += (_, _) => RefreshRows();
        locationBox.SelectionChanged += (_, _) => RefreshRows();
        monthlyGrid.SelectionChanged += (_, _) => RefreshSelectedMonthsTotals();

        dailyGrid.MouseDoubleClick += (_, _) =>
        {
            if (dailyGrid.SelectedItem is not ReconciliationRow row) return;
            ShowReconciliationExplanation(row.Day);
        };
        var menu = new ContextMenu();
        var explain = new MenuItem { Header = "Показать, чем закрыта сумма..." };
        explain.Click += (_, _) =>
        {
            if (dailyGrid.SelectedItem is ReconciliationRow row)
                ShowReconciliationExplanation(row.Day);
        };
        menu.Items.Add(explain);
        dailyGrid.ContextMenu = menu;

        RefreshRows();
        return root;
    }

    private void ExportReconciliationExcel(
        IReadOnlyList<ReconciliationRow> dailyRows,
        IReadOnlyList<ReconciliationMonthRow> monthlyRows,
        IReadOnlyList<ReconciliationDay> allTimeDays,
        int year,
        int? month,
        string locationName)
    {
        if (dailyRows.Count == 0 && monthlyRows.Count == 0)
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
            Title = "Экспорт сверки в Excel",
            Filter = "Excel (*.xlsx)|*.xlsx",
            FileName = $"Сверка_{safePoint}_{year}{(month is int selectedMonth ? $"-{selectedMonth:00}" : "")}.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx"
        };
        if (dialog.ShowDialog(this) != true) return;

        var yearTerminal = Money.Normalize(allTimeDays.Sum(x => x.BankElectronic ?? 0m));
        var yearCash = Money.Normalize(allTimeDays.Sum(x => x.CashElectronic ?? 0m));
        var allTimeBalance = Money.Normalize(yearTerminal - yearCash);
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

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());

        var summaryRows = new List<Row>
        {
            ExcelTextRow("Сверка касса ↔ терминал", 1),
            ExcelTextRow($"Период выгрузки: {period}"),
            ExcelTextRow($"Точка: {locationName}"),
            new Row(),
            ExcelLabelMoneyRow("Терминалы за выбранный год", yearTerminal),
            ExcelLabelMoneyRow("Касса безнал за выбранный год", yearCash),
            ExcelLabelMoneyRow("Накопительно с начала года", allTimeBalance),
            ExcelTextRow($"Итог: {action}", 1),
            ExcelTextRow($"Дней на проверке за выбранный год: {allTimeDays.Count(x => x.RequiresReview)}")
        };
        AddReconciliationSheet(
            workbookPart,
            sheets,
            1,
            "Итоги",
            new Columns(
                new Column { Min = 1, Max = 1, Width = 34, CustomWidth = true },
                new Column { Min = 2, Max = 2, Width = 20, CustomWidth = true }),
            summaryRows);

        var dailySheetRows = new List<Row>();
        var dailyHeader = new Row();
        foreach (var header in new[]
        {
            "Дата", "Точка", "Терминалы", "Касса безнал", "Разница за день",
            "Накопительно с начала года", "Что делать", "Статус", "Закрытие смены", "№ смены"
        })
            dailyHeader.Append(ExcelTextCell(header, 1));
        dailySheetRows.Add(dailyHeader);

        foreach (var row in dailyRows)
        {
            var excelRow = new Row();
            excelRow.Append(ExcelTextCell(row.Date));
            excelRow.Append(ExcelTextCell(row.Point));
            excelRow.Append(ExcelMoneyCell(row.Terminal));
            excelRow.Append(ExcelMoneyCell(row.Cash));
            excelRow.Append(ExcelMoneyCell(row.DayDifference));
            excelRow.Append(ExcelMoneyCell(row.AccumulatedBalance));
            excelRow.Append(ExcelTextCell(row.AccumulatedAction));
            excelRow.Append(ExcelTextCell(row.Status));
            excelRow.Append(ExcelTextCell(row.ClosedAt));
            excelRow.Append(ExcelTextCell(row.Shift));
            dailySheetRows.Add(excelRow);
        }

        AddReconciliationSheet(
            workbookPart,
            sheets,
            2,
            "По дням",
            new Columns(
                new Column { Min = 1, Max = 1, Width = 13, CustomWidth = true },
                new Column { Min = 2, Max = 2, Width = 30, CustomWidth = true },
                new Column { Min = 3, Max = 6, Width = 19, CustomWidth = true },
                new Column { Min = 7, Max = 7, Width = 24, CustomWidth = true },
                new Column { Min = 8, Max = 8, Width = 40, CustomWidth = true },
                new Column { Min = 9, Max = 10, Width = 20, CustomWidth = true }),
            dailySheetRows);

        var monthlySheetRows = new List<Row>();
        var monthlyHeader = new Row();
        foreach (var header in new[]
        {
            "Месяц", "Точка", "Терминалы", "Касса безнал", "Разница месяца",
            "Накопительно с начала года", "Что делать", "Дней на проверке"
        })
            monthlyHeader.Append(ExcelTextCell(header, 1));
        monthlySheetRows.Add(monthlyHeader);

        foreach (var row in monthlyRows)
        {
            var excelRow = new Row();
            excelRow.Append(ExcelTextCell(row.Period));
            excelRow.Append(ExcelTextCell(row.Point));
            excelRow.Append(ExcelMoneyCell(row.Terminal));
            excelRow.Append(ExcelMoneyCell(row.Cash));
            excelRow.Append(ExcelMoneyCell(row.Difference));
            excelRow.Append(ExcelMoneyCell(row.AccumulatedBalance));
            excelRow.Append(ExcelTextCell(row.Action));
            excelRow.Append(ExcelNumberCell(row.ReviewDays));
            monthlySheetRows.Add(excelRow);
        }

        AddReconciliationSheet(
            workbookPart,
            sheets,
            3,
            "По месяцам",
            new Columns(
                new Column { Min = 1, Max = 1, Width = 13, CustomWidth = true },
                new Column { Min = 2, Max = 2, Width = 30, CustomWidth = true },
                new Column { Min = 3, Max = 6, Width = 20, CustomWidth = true },
                new Column { Min = 7, Max = 7, Width = 24, CustomWidth = true },
                new Column { Min = 8, Max = 8, Width = 18, CustomWidth = true }),
            monthlySheetRows);

        workbookPart.Workbook.Save();

        StatusText.Text = $"Excel сохранён: {dialog.FileName}";
        MessageBox.Show(this,
            $"Excel сохранён.\n\nВ файле 3 листа: «Итоги», «По дням», «По месяцам».\n\n{dialog.FileName}",
            "КОП Кассы",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static void AddReconciliationSheet(
        WorkbookPart workbookPart,
        Sheets sheets,
        uint sheetId,
        string name,
        Columns columns,
        IEnumerable<Row> rows)
    {
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        worksheetPart.Worksheet = new Worksheet(columns, sheetData);
        foreach (var row in rows) sheetData.Append(row);
        worksheetPart.Worksheet.Save();

        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = sheetId,
            Name = name
        });
    }

    private static Stylesheet BuildReconciliationStyles() => new(
        new Fonts(
            new Font(),
            new Font(new Bold())),
        new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 })),
        new Borders(new DocumentFormat.OpenXml.Spreadsheet.Border()),
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

    private static Row ExcelLabelMoneyRow(string label, decimal value)
    {
        var row = new Row();
        row.Append(ExcelTextCell(label, 1));
        row.Append(ExcelMoneyCell(value));
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

    private static Cell ExcelNumberCell(int value) => new()
    {
        DataType = CellValues.Number,
        CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture))
    };

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

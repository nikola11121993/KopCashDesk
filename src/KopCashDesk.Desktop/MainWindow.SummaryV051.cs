using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private static readonly DateOnly Gres6OverageStartDate = new(2026, 9, 9);
    private const decimal Gres6InitialOverage = 634022m;

    private static bool IsGres6CorrectionPoint(string name)
    {
        var key = new string(name.ToLowerInvariant().Replace('ё', 'е').Where(char.IsLetterOrDigit).ToArray());
        return key.Contains("рефтинскаягрэс6", StringComparison.Ordinal) ||
               key.Contains("рефтинскаягрэс6столовая", StringComparison.Ordinal);
    }

    private UIElement RenderSummaryV051()
    {
        PageTitle.Text = "Свод по точкам";
        PageSubtitle.Text = "Касса безнал • терминал безнал • текущий остаток";

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var allRows = _db.CanonicalPointDaySummaries(SelectedOrganizationId);
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
        var addDayButton = new Button
        {
            Content = "+ Добавить день вручную",
            Padding = new Thickness(12, 5, 12, 5),
            ToolTip = "Добавить дату, сумму терминала и сумму кассы без загрузки отчётов"
        };
        filters.Children.Add(addDayButton);
        root.Children.Add(filters);

        var totals = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(totals, 1);
        root.Children.Add(totals);

        DataGrid dailyGrid = null!;
        DataGrid monthlyGrid = null!;

        bool HasManualCash(DaySummaryRow row) => _db.ManualCashPostings(row.OrganizationId, row.DateValue.Year, row.DateValue.Month, row.LocationId)
            .Any(x => x.Date == row.DateValue);

        bool HasManualTerminal(DaySummaryRow row) => _db.ManualTerminalPostings(row.OrganizationId, row.DateValue.Year, row.DateValue.Month, row.LocationId)
            .Any(x => x.Date == row.DateValue);

        void ToggleSberCopy(DaySummaryRow row, bool isChecked)
        {
            if (row.Sber is null)
            {
                MessageBox.Show(this, "За этот день нет суммы терминала.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshData();
                return;
            }

            if (row.Status.Contains("Конфликт кассовых источников", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "За этот день есть конфликт Taxcom/Frontol. Сначала проверьте кассовые источники.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshData();
                return;
            }

            var hasManual = HasManualCash(row);
            if (isChecked && row.HasActualFiscal && !hasManual)
            {
                var answer = MessageBox.Show(this,
                    "За этот день уже есть кассовый отчёт.\n\nПоставить сумму терминала как ручную корректировку кассы? Исходный отчёт останется в базе, а снятие галочки вернёт его сумму.",
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
                StatusText.Text = $"{row.Point}: ручная корректировка кассы за {row.Date} снята";
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
                StatusText.Text = $"{row.Point}: ручная корректировка кассы за {row.Date} очищена";
            }
            else if (dialog.Value is decimal value)
            {
                _db.SetManualCash(row.OrganizationId, row.LocationId, row.DateValue, value);
                StatusText.Text = $"{row.Point}: касса за {row.Date} вручную = {value:N2} ₽";
            }
            RefreshData();
        }

        void EditManualTerminal(DaySummaryRow row)
        {
            var dialog = new ManualCashEditWindow(
                row.Point,
                row.DateValue,
                row.Sber,
                "Терминал — ручная сумма",
                "Терминал безнал:")
            { Owner = this };
            if (dialog.ShowDialog() != true) return;

            if (dialog.ClearRequested)
            {
                _db.ClearManualTerminal(row.OrganizationId, row.LocationId, row.DateValue);
                StatusText.Text = $"{row.Point}: ручная сумма терминала за {row.Date} очищена";
            }
            else if (dialog.Value is decimal value)
            {
                _db.SetManualTerminal(row.OrganizationId, row.LocationId, row.DateValue, value);
                StatusText.Text = $"{row.Point}: терминал за {row.Date} вручную = {value:N2} ₽";
            }
            RefreshData();
        }

        void DeleteManualDay(DaySummaryRow row)
        {
            var hasCash = HasManualCash(row);
            var hasTerminal = HasManualTerminal(row);
            if (!hasCash && !hasTerminal)
            {
                MessageBox.Show(this,
                    "За этот день нет ручных данных. Импортированные отчёты программа отсюда не удаляет.",
                    "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(this,
                $"Удалить ручные данные за {row.Date}, {row.Point}?\n\n" +
                "Будут удалены только введённые вручную суммы терминала и кассы. Импортированные Сбер/Такском/Frontol останутся в базе.",
                "Удалить ручной день", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            if (hasTerminal) _db.ClearManualTerminal(row.OrganizationId, row.LocationId, row.DateValue);
            if (hasCash) _db.ClearManualCash(row.OrganizationId, row.LocationId, row.DateValue);
            StatusText.Text = $"{row.Point}: ручные данные за {row.Date} удалены";
            RefreshData();
        }

        void AddManualDay()
        {
            var selectedLocationId = (locationBox.SelectedItem as LocationOption)?.Id;
            var selectedYear = yearBox.SelectedItem is int year ? year : DateTime.Today.Year;
            var selectedMonth = (monthBox.SelectedItem as MonthOption)?.Number ?? DateTime.Today.Month;
            var today = DateTime.Today;
            var preferredDay = selectedYear == today.Year && selectedMonth == today.Month ? today.Day : 1;
            var preferredDate = new DateOnly(selectedYear, selectedMonth, preferredDay);

            var allowedOrganizations = _organizations
                .Where(x => SelectedOrganizationId is null || x.Id == SelectedOrganizationId)
                .ToArray();
            var allowedOrganizationIds = allowedOrganizations.Select(x => x.Id).ToHashSet();
            var allowedLocations = _locations.Where(x => allowedOrganizationIds.Contains(x.OrganizationId)).ToArray();

            if (allowedOrganizations.Length == 0 || allowedLocations.Length == 0)
            {
                MessageBox.Show(this, "Нет доступной организации или торговой точки.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new ManualDayAddWindow(
                allowedOrganizations,
                allowedLocations,
                SelectedOrganizationId,
                selectedLocationId,
                preferredDate)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true) return;

            var alreadyManualCash = _db.ManualCashPostings(dialog.OrganizationId, dialog.Date.Year, dialog.Date.Month, dialog.LocationId)
                .Any(x => x.Date == dialog.Date);
            var alreadyManualTerminal = _db.ManualTerminalPostings(dialog.OrganizationId, dialog.Date.Year, dialog.Date.Month, dialog.LocationId)
                .Any(x => x.Date == dialog.Date);
            var existing = _db.CanonicalPointDaySummaries(dialog.OrganizationId, dialog.Date.Year, dialog.Date.Month, dialog.LocationId)
                .FirstOrDefault(x => x.Date == dialog.Date);

            if (alreadyManualCash || alreadyManualTerminal)
            {
                var answer = MessageBox.Show(this,
                    $"За {dialog.Date:dd.MM.yyyy} уже есть ручные данные.\n\nЗаменить их?\nТерминал: {dialog.TerminalAmount:N2} ₽\nКасса безнал: {dialog.CashAmount:N2} ₽",
                    "КОП Кассы", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
            }
            else if (existing is not null && (existing.BankElectronic is not null || existing.FiscalElectronic is not null || existing.ShiftCount > 0))
            {
                var answer = MessageBox.Show(this,
                    $"За {dialog.Date:dd.MM.yyyy} уже есть импортированные данные.\n\nСохранить ручные значения? Исходные отчёты останутся в базе.\n\nТерминал: {dialog.TerminalAmount:N2} ₽\nКасса безнал: {dialog.CashAmount:N2} ₽",
                    "Ручная корректировка дня", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
            }

            _db.SetManualTerminal(dialog.OrganizationId, dialog.LocationId, dialog.Date, dialog.TerminalAmount);
            _db.SetManualCash(dialog.OrganizationId, dialog.LocationId, dialog.Date, dialog.CashAmount);
            var point = _locations.FirstOrDefault(x => x.Id == dialog.LocationId)?.Name ?? "Точка";
            StatusText.Text = $"{point}: {dialog.Date:dd.MM.yyyy} добавлен вручную. Терминал {dialog.TerminalAmount:N2} ₽, касса {dialog.CashAmount:N2} ₽.";

            if (years.Contains(dialog.Date.Year))
                yearBox.SelectedItem = dialog.Date.Year;
            var monthOption = monthOptions.FirstOrDefault(x => x.Number == dialog.Date.Month);
            if (monthOption is not null) monthBox.SelectedItem = monthOption;
            var locationOption = locationOptions.FirstOrDefault(x => x.Id == dialog.LocationId);
            if (locationOption is not null) locationBox.SelectedItem = locationOption;

            if (!years.Contains(dialog.Date.Year))
                PageContent.Content = RenderSummaryV051();
            else
                RefreshData();
        }

        dailyGrid = BuildDailySummaryGrid(ToggleSberCopy, EditManualCash, EditManualTerminal, DeleteManualDay);
        monthlyGrid = BuildMonthlySummaryGrid();
        addDayButton.Click += (_, _) => AddManualDay();
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
            var rows = _db.CanonicalPointDaySummaries(SelectedOrganizationId, year, month, locationId);
            var manualCash = _db.ManualCashPostings(SelectedOrganizationId, year, month, locationId)
                .ToDictionary(x => (x.OrganizationId, x.LocationId, x.Date), x => x.Electronic);
            var manualTerminal = _db.ManualTerminalPostings(SelectedOrganizationId, year, month, locationId)
                .ToDictionary(x => (x.OrganizationId, x.LocationId, x.Date), x => x.Electronic);

            var dayRows = rows.Select(x => ToDayRowV051(x, manualCash, manualTerminal)).OrderByDescending(x => x.DateValue).ThenBy(x => x.Point).ToArray();
            dailyGrid.ItemsSource = dayRows;

            var monthRows = dayRows
                .GroupBy(x => new { x.DateValue.Year, x.DateValue.Month, x.OrganizationId, x.Organization, x.LocationId, x.Point })
                .Select(g =>
                {
                    var bank = SumNullable(g.Select(x => x.Sber));
                    var cash = SumNullable(g.Select(x => x.CashElectronic));
                    var shiftTotal = SumNullable(g.Select(x => x.ShiftTotal));
                    var hasConflict = g.Any(x => x.Status.Contains("Конфликт кассовых источников", StringComparison.OrdinalIgnoreCase));
                    var missingCash = g.Any(x => x.Sber is not null && x.CashElectronic is null);
                    var missingBank = g.Any(x => x.Sber is null && x.CashElectronic is not null);
                    var complete = !missingCash && !missingBank && !hasConflict;
                    var difference = complete && bank is not null && cash is not null ? cash - bank : null;
                    var lastClosed = g.Where(x => x.LastClosedAt is not null).Select(x => x.LastClosedAt).Max();
                    var status = hasConflict
                        ? "Конфликт кассовых источников — требуется проверка"
                        : complete
                            ? SummaryStatus(bank, cash, shiftTotal, g.Sum(x => x.ShiftCount), difference, g.Any(x => x.CashFromSber))
                            : "Неполные данные";
                    return new MonthSummaryRow(g.Key.Year, g.Key.Month, g.Key.OrganizationId, g.Key.LocationId, g.Key.Organization, g.Key.Point,
                        bank, cash, shiftTotal, g.Sum(x => x.ShiftCount), lastClosed, difference, status);
                })
                .OrderByDescending(x => x.Year).ThenByDescending(x => x.Month).ThenBy(x => x.Point).ToArray();
            monthlyGrid.ItemsSource = monthRows;

            // Верхняя строка не зависит от выбранного месяца: это рабочий накопительный итог за 2026 год.
            // Для ГРЭС-6 действует подтверждённая пользователем контрольная точка:
            // с 09.09.2026 кассу не пробивают, стартовое перепробитие = 634 022 ₽,
            // новые терминальные оплаты постепенно гасят этот остаток.
            var selectedLocation = locationId is Guid selectedLocationId
                ? _locations.FirstOrDefault(x => x.Id == selectedLocationId)
                : null;

            if (selectedLocation is not null && IsGres6CorrectionPoint(selectedLocation.Name))
            {
                var trackerRowsSource = _db.CanonicalPointDaySummaries(
                    SelectedOrganizationId,
                    2026,
                    null,
                    selectedLocation.Id);
                var trackerManualCash = _db.ManualCashPostings(
                        SelectedOrganizationId,
                        2026,
                        null,
                        selectedLocation.Id)
                    .ToDictionary(x => (x.OrganizationId, x.LocationId, x.Date), x => x.Electronic);
                var trackerManualTerminal = _db.ManualTerminalPostings(
                        SelectedOrganizationId,
                        2026,
                        null,
                        selectedLocation.Id)
                    .ToDictionary(x => (x.OrganizationId, x.LocationId, x.Date), x => x.Electronic);

                var trackerRows = trackerRowsSource
                    .Select(x => ToDayRowV051(x, trackerManualCash, trackerManualTerminal))
                    .Where(x => x.DateValue >= Gres6OverageStartDate)
                    .ToArray();

                var terminalSinceStart = Money.Normalize(trackerRows.Sum(x => x.Sber ?? 0m));
                var cashSinceStart = Money.Normalize(trackerRows.Sum(x => x.CashElectronic ?? 0m));
                var remainingOverage = Money.Normalize(Gres6InitialOverage + cashSinceStart - terminalSinceStart);

                var state = remainingOverage > 0m
                    ? $"ПЕРЕБИТО: {remainingOverage:N2} ₽"
                    : remainingOverage < 0m
                        ? $"НАДО ПРОБИТЬ: {Math.Abs(remainingOverage):N2} ₽"
                        : "СОШЛОСЬ: 0,00 ₽";

                totals.Text =
                    $"Касса безнал: {cashSinceStart:N2} ₽     •     " +
                    $"Терминал безнал: {terminalSinceStart:N2} ₽     •     " +
                    state;
            }
            else
            {
                var totalRowsSource = _db.CanonicalPointDaySummaries(
                    SelectedOrganizationId,
                    2026,
                    null,
                    locationId);
                var totalManualCash = _db.ManualCashPostings(
                        SelectedOrganizationId,
                        2026,
                        null,
                        locationId)
                    .ToDictionary(x => (x.OrganizationId, x.LocationId, x.Date), x => x.Electronic);
                var totalManualTerminal = _db.ManualTerminalPostings(
                        SelectedOrganizationId,
                        2026,
                        null,
                        locationId)
                    .ToDictionary(x => (x.OrganizationId, x.LocationId, x.Date), x => x.Electronic);

                var totalRows = totalRowsSource
                    .Select(x => ToDayRowV051(x, totalManualCash, totalManualTerminal))
                    .ToArray();

                var terminalTotal = Money.Normalize(totalRows.Sum(x => x.Sber ?? 0m));
                var cashTotal = Money.Normalize(totalRows.Sum(x => x.CashElectronic ?? 0m));
                var balance = Money.Normalize(cashTotal - terminalTotal);
                var state = balance > 0m
                    ? $"ПЕРЕБИТО: {balance:N2} ₽"
                    : balance < 0m
                        ? $"НАДО ПРОБИТЬ: {Math.Abs(balance):N2} ₽"
                        : "СОШЛОСЬ: 0,00 ₽";

                totals.Text =
                    $"Касса безнал: {cashTotal:N2} ₽     •     " +
                    $"Терминал безнал: {terminalTotal:N2} ₽     •     " +
                    state;
            }
        }

        yearBox.SelectionChanged += (_, _) => RefreshData();
        monthBox.SelectionChanged += (_, _) => RefreshData();
        locationBox.SelectionChanged += (_, _) => RefreshData();
        RefreshData();
        return root;
    }

    private static DaySummaryRow ToDayRowV051(
        PointDaySummary row,
        IReadOnlyDictionary<(Guid OrganizationId, Guid LocationId, DateOnly Date), decimal> manualCash,
        IReadOnlyDictionary<(Guid OrganizationId, Guid LocationId, DateOnly Date), decimal> manualTerminal)
    {
        var key = (row.OrganizationId, row.LocationId, row.Date);
        var hasManualCash = manualCash.TryGetValue(key, out var manualElectronic);
        var hasManualTerminal = manualTerminal.ContainsKey(key);
        var cash = hasManualCash ? manualElectronic : row.FiscalElectronic;
        var copied = !row.HasSourceConflict && hasManualCash && row.BankElectronic is not null && manualElectronic == row.BankElectronic.Value;
        var difference = !row.HasSourceConflict && row.BankElectronic is not null && cash is not null ? cash - row.BankElectronic : null;
        var status = row.HasSourceConflict
            ? $"Конфликт кассовых источников — требуется проверка{SourceSuffix(row.FiscalSources)}"
            : SummaryStatus(row.BankElectronic, cash, row.ShiftTotal, row.ShiftCount, difference, copied) + SourceSuffix(row.FiscalSources);
        if (hasManualTerminal) status += " — терминал вручную";
        if (hasManualCash && !copied) status += " — касса вручную";

        return new DaySummaryRow(
            row.Date, row.OrganizationId, row.LocationId, row.Organization, row.Location,
            row.BankElectronic, cash, row.FiscalElectronic is not null, copied,
            row.BankElectronic is not null && !row.HasSourceConflict,
            row.ShiftTotal, row.ShiftCount, row.LastShiftClosedAt,
            difference, status);
    }

    private static string SourceSuffix(string sources) =>
        string.IsNullOrWhiteSpace(sources) ? string.Empty : $" — {sources}";
}

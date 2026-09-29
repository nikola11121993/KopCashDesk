using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private sealed record DvvsDailyRegisterRow(
        DateOnly? DateValue,
        string Date,
        Guid OrganizationId,
        Guid LocationId,
        decimal Kassa1,
        decimal Kassa2,
        decimal Spare,
        decimal Total,
        bool IsTotal = false);

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

        // Do not build the full summary just to populate the year filter.
        // The application currently works with data starting from 2026.
        var currentYear = DateTime.Today.Year;
        var years = Enumerable.Range(2026, Math.Max(1, currentYear - 2026 + 1))
            .OrderByDescending(x => x)
            .ToList();

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

        var totals = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(totals, 1);
        root.Children.Add(totals);

        DataGrid dailyGrid = null!;
        DataGrid monthlyGrid = null!;
        DataGrid registerGrid = null!;
        DataGrid dvvsGrid = null!;
        TabItem dvvsTab = null!;
        var fullYearCache = new Dictionary<string, DaySummaryRow[]>(StringComparer.Ordinal);

        DaySummaryRow[] FullYearRows(Guid? selectedLocationId)
        {
            var cacheKey = selectedLocationId?.ToString("N") ?? "*";
            if (fullYearCache.TryGetValue(cacheKey, out var cached)) return cached;

            var source = _db.CanonicalPointDaySummaries(SelectedOrganizationId, 2026, null, selectedLocationId);
            var manualCash = _db.ManualCashPostings(SelectedOrganizationId, 2026, null, selectedLocationId)
                .ToDictionary(x => (x.OrganizationId, x.LocationId, x.Date), x => x.Electronic);
            var manualTerminal = _db.ManualTerminalPostings(SelectedOrganizationId, 2026, null, selectedLocationId)
                .ToDictionary(x => (x.OrganizationId, x.LocationId, x.Date), x => x.Electronic);
            cached = source.Select(x => ToDayRowV051(x, manualCash, manualTerminal)).ToArray();
            fullYearCache[cacheKey] = cached;
            return cached;
        }

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
                fullYearCache.Clear();
                StatusText.Text = $"{row.Point}: касса за {row.Date} = {row.Sber.Value:N2} ₽";
            }
            else
            {
                _db.ClearManualCash(row.OrganizationId, row.LocationId, row.DateValue);
                fullYearCache.Clear();
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
                fullYearCache.Clear();
                StatusText.Text = $"{row.Point}: ручная корректировка кассы за {row.Date} очищена";
            }
            else if (dialog.Value is decimal value)
            {
                _db.SetManualCash(row.OrganizationId, row.LocationId, row.DateValue, value);
                fullYearCache.Clear();
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
                fullYearCache.Clear();
                StatusText.Text = $"{row.Point}: ручная сумма терминала за {row.Date} очищена";
            }
            else if (dialog.Value is decimal value)
            {
                _db.SetManualTerminal(row.OrganizationId, row.LocationId, row.DateValue, value);
                fullYearCache.Clear();
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
            fullYearCache.Clear();
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
            fullYearCache.Clear();
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
        registerGrid = BuildRegisterRevenueGrid();
        dvvsGrid = BuildDvvsRegisterDailyGrid();
        addDayButton.Click += (_, _) => AddManualDay();
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "По дням", Content = dailyGrid });
        tabs.Items.Add(new TabItem { Header = "По месяцам", Content = monthlyGrid });
        tabs.Items.Add(new TabItem { Header = "По кассам", Content = registerGrid });
        dvvsTab = new TabItem { Header = "ДВВС — 3 кассы", Content = dvvsGrid, Visibility = Visibility.Collapsed };
        tabs.Items.Add(dvvsTab);
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
            registerGrid.ItemsSource = _db.RegisterRevenueSummaries(SelectedOrganizationId, year, month, locationId);

            var isDvvs = _db.IsGarantDvvs(SelectedOrganizationId, locationId);
            dvvsTab.Visibility = isDvvs ? Visibility.Visible : Visibility.Collapsed;
            if (isDvvs)
            {
                var dvvs = _db.GarantDvvsRegisterDaySummaries(year, month, SelectedOrganizationId, locationId);
                var dvvsRows = dvvs.Select(x => new DvvsDailyRegisterRow(
                    x.Date,
                    x.Date.ToString("dd.MM.yyyy"),
                    x.OrganizationId,
                    x.LocationId,
                    x.Kassa1Electronic,
                    x.Kassa2Electronic,
                    x.SpareElectronic,
                    x.TotalElectronic)).ToList();

                var label = month is int selectedMonth
                    ? $"ИТОГО {culture.DateTimeFormat.GetMonthName(selectedMonth).ToUpper(culture)}"
                    : $"ИТОГО {year}";
                dvvsRows.Add(new DvvsDailyRegisterRow(
                    null,
                    label,
                    SelectedOrganizationId!.Value,
                    locationId!.Value,
                    Money.Normalize(dvvs.Sum(x => x.Kassa1Electronic)),
                    Money.Normalize(dvvs.Sum(x => x.Kassa2Electronic)),
                    Money.Normalize(dvvs.Sum(x => x.SpareElectronic)),
                    Money.Normalize(dvvs.Sum(x => x.TotalElectronic)),
                    true));
                dvvsGrid.ItemsSource = dvvsRows;
            }
            else
            {
                dvvsGrid.ItemsSource = Array.Empty<DvvsDailyRegisterRow>();
            }

            // Верхняя строка не зависит от выбранного месяца: это рабочий накопительный итог за 2026 год.
            // Для ГРЭС-6 действует подтверждённая пользователем контрольная точка:
            // с 09.09.2026 кассу не пробивают, стартовое перепробитие = 634 022 ₽,
            // новые терминальные оплаты постепенно гасят этот остаток.
            var selectedLocation = locationId is Guid selectedLocationId
                ? _locations.FirstOrDefault(x => x.Id == selectedLocationId)
                : null;

            if (isDvvs)
            {
                var dvvsYear = _db.GarantDvvsRegisterDaySummaries(year, null, SelectedOrganizationId, locationId);
                var kassa1 = Money.Normalize(dvvsYear.Sum(x => x.Kassa1Electronic));
                var kassa2 = Money.Normalize(dvvsYear.Sum(x => x.Kassa2Electronic));
                var spare = Money.Normalize(dvvsYear.Sum(x => x.SpareElectronic));
                var all = Money.Normalize(kassa1 + kassa2 + spare);
                totals.Text =
                    $"ДВВС — безнал по фискальным документам:     " +
                    $"Касса 1 (iiko): {kassa1:N2} ₽     •     " +
                    $"Касса 2 (iiko): {kassa2:N2} ₽     •     " +
                    $"Запасная: {spare:N2} ₽     •     " +
                    $"ИТОГО: {all:N2} ₽";
            }
            else if (selectedLocation is not null && IsGres6CorrectionPoint(selectedLocation.Name))
            {
                var trackerRows = (year == 2026 && month is null
                        ? dayRows
                        : FullYearRows(selectedLocation.Id))
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
                var totalRows = year == 2026 && month is null
                    ? dayRows
                    : FullYearRows(locationId);

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

    private static DataGrid BuildDvvsRegisterDailyGrid()
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            SelectionMode = DataGridSelectionMode.Single,
            CanUserAddRows = false,
            CanUserSortColumns = false,
            ToolTip = "ДВВС: суммы из отчётов Такском по фискальным документам. Физическая ККТ определяется по заводскому номеру; замена ФН не создаёт новую кассу."
        };

        foreach (var column in new[]
        {
            ("Дата", "Date", 125),
            ("Касса 1 (iiko), безнал", "Kassa1", 165),
            ("Касса 2 (iiko), безнал", "Kassa2", 165),
            ("Запасная ДВВС, безнал", "Spare", 180),
            ("ИТОГО ДВВС", "Total", 140)
        })
        {
            var binding = new Binding(column.Item2);
            if (column.Item2 is "Kassa1" or "Kassa2" or "Spare" or "Total") binding.StringFormat = "N2";
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = column.Item1,
                Binding = binding,
                Width = column.Item3
            });
        }

        grid.LoadingRow += (_, e) =>
        {
            if (e.Row.Item is DvvsDailyRegisterRow row && row.IsTotal)
                e.Row.FontWeight = FontWeights.Bold;
        };
        return grid;
    }

    private static DataGrid BuildRegisterRevenueGrid()
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            SelectionMode = DataGridSelectionMode.Single,
            CanUserAddRows = false
        };

        foreach (var column in new[]
        {
            ("Точка", "Point", 150),
            ("ККТ / касса", "Register", 180),
            ("РНМ", "Rnm", 145),
            ("ФН", "Fn", 145),
            ("Зав. №", "KktSerial", 135),
            ("Смен", "ShiftCount", 60),
            ("Наличные", "Cash", 105),
            ("Безналичные", "Electronic", 115),
            ("Выручка", "Total", 110),
            ("Последнее закрытие", "LastClosed", 145)
        })
        {
            var binding = new Binding(column.Item2);
            if (column.Item2 is "Cash" or "Electronic" or "Total") binding.StringFormat = "N2";
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = column.Item1,
                Binding = binding,
                Width = column.Item3
            });
        }

        return grid;
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

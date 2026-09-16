using KopCashDesk.Core;
using KopCashDesk.Data;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KopCashDesk.Desktop;

public sealed class SmartUnifiedImportWindow : Window
{
    private sealed class FileItem
    {
        public FileItem(string path, SmartImportKind kind) { Path = path; Kind = kind; }
        public string Path { get; }
        public SmartImportKind Kind { get; }
        public Guid? OrganizationId { get; set; }
        public Guid? LocationId { get; set; }
        public string OrganizationName { get; set; } = string.Empty;
        public string LocationName { get; set; } = string.Empty;
        public string FileName => System.IO.Path.GetFileName(Path);
        public string Type => KindText(Kind);
        public string Organization => Kind == SmartImportKind.Frontol ? Pending(OrganizationName) : "авто";
        public string Location => Kind == SmartImportKind.Frontol ? Pending(LocationName) : "авто";
        public string Status => Kind switch
        {
            SmartImportKind.Unknown => "Не удалось определить формат",
            SmartImportKind.Frontol when OrganizationId is null || LocationId is null => "Нужно назначить точку",
            _ => "Готово"
        };
        private static string Pending(string value) => string.IsNullOrWhiteSpace(value) ? "не назначено" : value;
    }

    private readonly Database _database;
    private readonly Guid? _fallbackOrganizationId;
    private readonly Action _afterImport;
    private readonly List<FileItem> _files = [];
    private readonly DataGrid _grid = new();
    private readonly TextBlock _result = new();
    private readonly Button _importButton = new();
    private readonly Button _assignButton = new();
    private readonly ProgressBar _progressBar = new()
    {
        Height = 18,
        Minimum = 0,
        Maximum = 100,
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 12, 0, 0)
    };
    private readonly TextBlock _progressText = new()
    {
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 6, 0, 0),
        Foreground = Brushes.DimGray,
        TextWrapping = TextWrapping.Wrap
    };

    public SmartUnifiedImportWindow(Database database, Guid? fallbackOrganizationId, Action afterImport)
    {
        _database = database;
        _fallbackOrganizationId = fallbackOrganizationId;
        _afterImport = afterImport;

        Title = "Импорт файлов — Сбер, кассы, Такском, УБРиР, Frontol, CRPT";
        Width = 1180;
        Height = 760;
        MinWidth = 940;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        AllowDrop = true;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += async (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await AddFilesAsync(paths); };

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock { Text = "Один импорт для всех отчётов", FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = "Программа определяет полные и сокращённые отчёты Сбер, ZIP регулярного эквайринга, Такском, альтернативные «Закрытые смены», УБРиР, Frontol и CRPT. Перекрывающиеся банковские отчёты не удваивают операции.",
            Margin = new Thickness(0, 6, 0, 0), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(heading);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var add = new Button { Content = "Добавить файлы...", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
        add.Click += async (_, _) => await ChooseFilesAsync();
        _assignButton.Content = "Назначить Frontol...";
        _assignButton.Padding = new Thickness(16, 8, 16, 8);
        _assignButton.Margin = new Thickness(0, 0, 8, 0);
        _assignButton.IsEnabled = false;
        _assignButton.Click += (_, _) => AssignFrontol();
        var clear = new Button { Content = "Очистить", Padding = new Thickness(16, 8, 16, 8) };
        clear.Click += (_, _) =>
        {
            _files.Clear();
            RefreshGrid();
            _result.Text = "Добавьте отчёты.";
            HideProgress();
        };
        tools.Children.Add(add); tools.Children.Add(_assignButton); tools.Children.Add(clear);
        Grid.SetRow(tools, 1); root.Children.Add(tools);

        var center = new Grid();
        center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        ConfigureGrid();
        center.Children.Add(new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(6), Child = _grid });
        var resultPanel = new StackPanel();
        resultPanel.Children.Add(new TextBlock { Text = "Результат", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        _result.Text = "Добавьте отчёты."; _result.TextWrapping = TextWrapping.Wrap;
        resultPanel.Children.Add(_result);
        resultPanel.Children.Add(_progressText);
        resultPanel.Children.Add(_progressBar);
        var resultBorder = new Border
        {
            BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(14), Margin = new Thickness(12, 0, 0, 0),
            Child = new ScrollViewer { Content = resultPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        Grid.SetColumn(resultBorder, 1); center.Children.Add(resultBorder);
        Grid.SetRow(center, 2); root.Children.Add(center);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        _importButton.Content = "Импортировать всё"; _importButton.Padding = new Thickness(18, 8, 18, 8); _importButton.Margin = new Thickness(0, 0, 8, 0); _importButton.IsDefault = true; _importButton.IsEnabled = false;
        _importButton.Click += Import_Click;
        var close = new Button { Content = "Закрыть", Padding = new Thickness(18, 8, 18, 8), IsCancel = true };
        close.Click += (_, _) => Close();
        bottom.Children.Add(_importButton); bottom.Children.Add(close);
        Grid.SetRow(bottom, 3); root.Children.Add(bottom);
        Content = root;
    }

    private void ConfigureGrid()
    {
        _grid.AutoGenerateColumns = false; _grid.IsReadOnly = true; _grid.CanUserAddRows = false; _grid.CanUserDeleteRows = false; _grid.SelectionMode = DataGridSelectionMode.Extended;
        _grid.Columns.Add(new DataGridTextColumn { Header = "Файл", Binding = new System.Windows.Data.Binding(nameof(FileItem.FileName)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Тип", Binding = new System.Windows.Data.Binding(nameof(FileItem.Type)), Width = 180 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Организация", Binding = new System.Windows.Data.Binding(nameof(FileItem.Organization)), Width = 150 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Торговая точка", Binding = new System.Windows.Data.Binding(nameof(FileItem.Location)), Width = 190 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Статус", Binding = new System.Windows.Data.Binding(nameof(FileItem.Status)), Width = 190 });
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.MouseDoubleClick += (_, _) => { if (_grid.SelectedItem is FileItem { Kind: SmartImportKind.Frontol }) AssignFrontol(); };
        _grid.KeyDown += (_, e) => { if (e.Key == Key.Delete) RemoveSelected(); };
        var menu = new ContextMenu();
        var assign = new MenuItem { Header = "Назначить Frontol..." }; assign.Click += (_, _) => AssignFrontol();
        var remove = new MenuItem { Header = "Убрать из списка" }; remove.Click += (_, _) => RemoveSelected();
        menu.Items.Add(assign); menu.Items.Add(remove); _grid.ContextMenu = menu;
    }

    private async Task ChooseFilesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Поддерживаемые отчёты (*.zip;*.xlsx;*.txt;*.crpt)|*.zip;*.xlsx;*.txt;*.crpt|Все файлы (*.*)|*.*",
            Multiselect = true,
            Title = "Выберите отчёты"
        };
        if (dialog.ShowDialog(this) == true) await AddFilesAsync(dialog.FileNames);
    }

    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var pending = paths
            .Where(path => !_files.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (pending.Length == 0) return;

        var unknown = new List<string>();
        Cursor = Cursors.Wait;
        ShowDeterminateProgress(0, $"Определяю форматы: 0 из {pending.Length}");
        _result.Text = "Читаю выбранные файлы. Программа работает — дождитесь окончания распознавания.";

        try
        {
            for (var i = 0; i < pending.Length; i++)
            {
                var path = pending[i];
                var fileName = System.IO.Path.GetFileName(path);
                _progressText.Text = $"Определяю формат {i + 1} из {pending.Length}: {fileName}";
                var kind = await Task.Run(() => SmartReportDetector.Detect(path));
                _files.Add(new FileItem(path, kind));
                if (kind == SmartImportKind.Unknown) unknown.Add(fileName);
                _progressBar.Value = (i + 1) * 100d / pending.Length;
                RefreshGrid();
            }

            _result.Text = unknown.Count == 0
                ? $"Готово к импорту: {_files.Count} файл(а/ов). Все форматы определены."
                : "Не удалось определить тип: " + string.Join(", ", unknown) + ".";
            _progressText.Text = unknown.Count == 0
                ? "Распознавание файлов завершено."
                : $"Распознавание завершено. Неизвестных файлов: {unknown.Count}.";
            _progressBar.Value = 100;
        }
        finally
        {
            Cursor = null;
        }
    }

    private void RefreshGrid() { _grid.ItemsSource = null; _grid.ItemsSource = _files; UpdateButtons(); }
    private void RemoveSelected() { foreach (var item in _grid.SelectedItems.Cast<FileItem>().ToArray()) _files.Remove(item); RefreshGrid(); }

    private void UpdateButtons()
    {
        var unknown = _files.Any(x => x.Kind == SmartImportKind.Unknown);
        var frontolReady = _files.Where(x => x.Kind == SmartImportKind.Frontol).All(x => x.OrganizationId is not null && x.LocationId is not null);
        _importButton.IsEnabled = _files.Count > 0 && !unknown && frontolReady;
        _assignButton.IsEnabled = _grid.SelectedItem is FileItem { Kind: SmartImportKind.Frontol };
    }

    private void AssignFrontol()
    {
        if (_grid.SelectedItem is not FileItem item || item.Kind != SmartImportKind.Frontol)
        {
            MessageBox.Show(this, "Выберите файл Frontol report.txt.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information); return;
        }
        var organizations = _database.Organizations().OrderBy(x => x.Name).ToArray();
        var dialog = new Window { Owner = this, Title = "Назначение Frontol — " + item.FileName, Width = 620, Height = 250, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White };
        var panel = new Grid { Margin = new Thickness(22) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) }); panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var orgBox = new ComboBox { ItemsSource = organizations, DisplayMemberPath = nameof(Organization.Name), Margin = new Thickness(0, 0, 0, 10) };
        var locBox = new ComboBox { DisplayMemberPath = nameof(Location.Name), Margin = new Thickness(0, 0, 0, 10) };
        var l1 = new TextBlock { Text = "Организация:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) };
        var l2 = new TextBlock { Text = "Торговая точка:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) };
        Grid.SetRow(l1, 0); Grid.SetColumn(l1, 0); Grid.SetRow(orgBox, 0); Grid.SetColumn(orgBox, 1); Grid.SetRow(l2, 1); Grid.SetColumn(l2, 0); Grid.SetRow(locBox, 1); Grid.SetColumn(locBox, 1);
        panel.Children.Add(l1); panel.Children.Add(orgBox); panel.Children.Add(l2); panel.Children.Add(locBox);
        void RefreshLocations()
        {
            var org = orgBox.SelectedItem as Organization;
            var locations = org is null ? [] : _database.Locations().Where(x => x.OrganizationId == org.Id && x.IsActive).OrderBy(x => x.Name).ToArray();
            locBox.ItemsSource = locations;
            if (item.LocationId is Guid id) locBox.SelectedItem = locations.FirstOrDefault(x => x.Id == id);
            locBox.SelectedItem ??= locations.FirstOrDefault();
        }
        orgBox.SelectionChanged += (_, _) => RefreshLocations();
        if (item.OrganizationId is Guid oid) orgBox.SelectedItem = organizations.FirstOrDefault(x => x.Id == oid);
        else if (_fallbackOrganizationId is Guid fallback) orgBox.SelectedItem = organizations.FirstOrDefault(x => x.Id == fallback);
        orgBox.SelectedItem ??= organizations.FirstOrDefault(); RefreshLocations();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Сохранить", Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(18, 7, 18, 7), IsCancel = true };
        ok.Click += (_, _) => { if (orgBox.SelectedItem is not Organization org || locBox.SelectedItem is not Location loc) return; item.OrganizationId = org.Id; item.LocationId = loc.Id; item.OrganizationName = org.Name; item.LocationName = loc.Name; dialog.DialogResult = true; };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); Grid.SetRow(buttons, 2); Grid.SetColumnSpan(buttons, 2); panel.Children.Add(buttons); dialog.Content = panel;
        if (dialog.ShowDialog() == true) RefreshGrid();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!_importButton.IsEnabled) return;
        var files = _files.ToArray();
        _importButton.IsEnabled = false;
        Cursor = Cursors.Wait;
        ShowIndeterminateProgress("Начинаю импорт...");
        _result.Text = "Импорт идёт. Полоса ниже двигается, пока программа читает и записывает данные. После успешного завершения это окно закроется автоматически.";

        var stages = 2;
        if (files.Any(x => x.Kind == SmartImportKind.Sber)) stages++;
        if (files.Any(x => x.Kind == SmartImportKind.UbrdDaily)) stages++;
        if (files.Any(x => x.Kind == SmartImportKind.ClosedShifts)) stages++;
        if (files.Any(x => x.Kind == SmartImportKind.Taxcom)) stages++;
        if (files.Any(x => x.Kind == SmartImportKind.TaxcomFiscalDocuments)) stages++;
        if (files.Any(x => x.Kind == SmartImportKind.Crpt)) stages++;
        stages += files.Where(x => x.Kind == SmartImportKind.Frontol)
            .Select(x => (x.OrganizationId, x.LocationId)).Distinct().Count();

        var progress = new Progress<string>(message => _progressText.Text = message);
        try
        {
            var text = await Task.Run(() =>
            {
                var result = new List<string>();
                var stage = 0;
                void Report(string message)
                {
                    stage++;
                    ((IProgress<string>)progress).Report($"Этап {stage} из {stages}: {message}");
                }

                var sber = files.Where(x => x.Kind == SmartImportKind.Sber).Select(x => x.Path).ToArray();
                if (sber.Length > 0)
                {
                    Report($"Сбер — читаю {sber.Length} файл(а/ов)");
                    var summary = new SmartSberAcquiringImporter(_database).ImportFiles(sber);
                    result.Add("СБЕР — ВСЕ ФОРМАТЫ\n" + summary.ToDisplayText());
                }

                Report("подготавливаю торговые точки и правила привязки касс");
                SmartKnownRules.PrepareCleanDatabase(_database);

                var ubrd = files.Where(x => x.Kind == SmartImportKind.UbrdDaily).Select(x => x.Path).ToArray();
                if (ubrd.Length > 0)
                {
                    Report($"УБРиР — читаю {ubrd.Length} файл(а/ов)");
                    var summary = new UbrdDailyImporter(_database).ImportFiles(ubrd);
                    result.Add("УБРиР — СВОД ПО ДНЯМ\n" + summary.ToDisplayText());
                }

                var closed = files.Where(x => x.Kind == SmartImportKind.ClosedShifts).Select(x => x.Path).ToArray();
                if (closed.Length > 0)
                {
                    Report($"Закрытые смены — читаю {closed.Length} файл(а/ов)");
                    var summary = new ClosedShiftReportImporter(_database).ImportFiles(closed);
                    result.Add("ЗАКРЫТЫЕ СМЕНЫ\n" + summary.ToDisplayText());
                }

                var taxcom = files.Where(x => x.Kind == SmartImportKind.Taxcom).Select(x => x.Path).ToArray();
                if (taxcom.Length > 0)
                {
                    Report($"Такском — смены, {taxcom.Length} файл(а/ов)");
                    var summary = new TaxcomShiftReportImporter(_database, _fallbackOrganizationId).ImportFiles(taxcom);
                    result.Add("ТАКСКОМ — СМЕНЫ\n" + summary.ToDisplayText());
                }

                var fiscal = files.Where(x => x.Kind == SmartImportKind.TaxcomFiscalDocuments).Select(x => x.Path).ToArray();
                if (fiscal.Length > 0)
                {
                    Report($"Такском — фискальные документы, {fiscal.Length} файл(а/ов)");
                    var summary = new TaxcomFiscalDocumentImporter(_database, _fallbackOrganizationId).ImportFiles(fiscal);
                    result.Add("ТАКСКОМ — ФИСКАЛЬНЫЕ ДОКУМЕНТЫ\n" + summary.ToDisplayText());
                }

                var crpt = files.Where(x => x.Kind == SmartImportKind.Crpt).Select(x => x.Path).ToArray();
                if (crpt.Length > 0)
                {
                    Report($"CRPT — читаю {crpt.Length} файл(а/ов)");
                    var summary = new CrptArchiveImporter(_database).ImportFiles(crpt);
                    result.Add("CRPT\n" + summary.ToDisplayText());
                }

                foreach (var group in files.Where(x => x.Kind == SmartImportKind.Frontol)
                             .GroupBy(x => (OrganizationId: x.OrganizationId!.Value, LocationId: x.LocationId!.Value)))
                {
                    Report($"Frontol — {group.First().LocationName}");
                    var summary = new FrontolReportImporter(_database, group.Key.OrganizationId, group.Key.LocationId).ImportFiles(group.Select(x => x.Path));
                    result.Add($"FRONTOL — {group.First().LocationName}\n" + summary.ToDisplayText());
                }

                SmartKnownRules.PrepareCleanDatabase(_database);
                Report("проверяю совпадения источников и завершаю импорт");
                var matching = _database.RebuildCrossSourceShiftMatches();
                result.Add($"ПРОВЕРКА ИСТОЧНИКОВ\nСовпавших Taxcom + Frontol: {matching.MatchedPairs}\nКонфликтов, требующих проверки: {matching.Conflicts}");
                return string.Join("\n\n------------------------------\n\n", result);
            });

            _result.Text = text;
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = 100;
            _progressText.Text = "Готово. Импорт завершён успешно. Закрываю окно...";
            _afterImport();
            await Task.Delay(900);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = 0;
            _progressText.Text = "Импорт остановлен из-за ошибки.";
            _result.Text = "Импорт не выполнен: " + ex.Message;
        }
        finally
        {
            Cursor = null;
            if (IsVisible) UpdateButtons();
        }
    }

    private void ShowDeterminateProgress(double value, string message)
    {
        _progressBar.Visibility = Visibility.Visible;
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = value;
        _progressText.Visibility = Visibility.Visible;
        _progressText.Text = message;
    }

    private void ShowIndeterminateProgress(string message)
    {
        _progressBar.Visibility = Visibility.Visible;
        _progressBar.IsIndeterminate = true;
        _progressText.Visibility = Visibility.Visible;
        _progressText.Text = message;
    }

    private void HideProgress()
    {
        _progressBar.Visibility = Visibility.Collapsed;
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 0;
        _progressText.Visibility = Visibility.Collapsed;
        _progressText.Text = string.Empty;
    }

    private static string KindText(SmartImportKind kind) => kind switch
    {
        SmartImportKind.Sber => "Сбер / эквайринг",
        SmartImportKind.Taxcom => "Такском — смены",
        SmartImportKind.TaxcomFiscalDocuments => "Такском — чеки",
        SmartImportKind.ClosedShifts => "Закрытые смены",
        SmartImportKind.UbrdDaily => "УБРиР — по дням",
        SmartImportKind.Frontol => "Frontol",
        SmartImportKind.Crpt => "CRPT",
        _ => "Неизвестно"
    };
}

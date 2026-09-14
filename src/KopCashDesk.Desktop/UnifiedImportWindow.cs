using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Core;
using KopCashDesk.Data;
using Microsoft.Win32;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KopCashDesk.Desktop;

public enum UnifiedImportKind
{
    Unknown,
    Sber,
    Taxcom,
    Frontol,
    Crpt
}

public sealed class UnifiedImportWindow : Window
{
    private sealed class FileItem
    {
        public FileItem(string path, UnifiedImportKind kind)
        {
            Path = path;
            Kind = kind;
        }

        public string Path { get; }
        public UnifiedImportKind Kind { get; }
        public Guid? OrganizationId { get; set; }
        public Guid? LocationId { get; set; }
        public string OrganizationName { get; set; } = string.Empty;
        public string LocationName { get; set; } = string.Empty;
        public string FileName => System.IO.Path.GetFileName(Path);
        public string Type => KindText(Kind);
        public string Organization => Kind == UnifiedImportKind.Frontol ? EmptyAsPending(OrganizationName) : "авто";
        public string Location => Kind == UnifiedImportKind.Frontol ? EmptyAsPending(LocationName) : "авто";
        public string Status => Kind switch
        {
            UnifiedImportKind.Unknown => "Не удалось определить формат",
            UnifiedImportKind.Frontol when OrganizationId is null || LocationId is null => "Нужно назначить точку",
            _ => "Готово"
        };

        private static string EmptyAsPending(string value) => string.IsNullOrWhiteSpace(value) ? "не назначено" : value;
    }

    private readonly Database _database;
    private readonly Guid? _fallbackOrganizationId;
    private readonly Action _afterImport;
    private readonly List<FileItem> _files = [];
    private readonly DataGrid _fileGrid = new();
    private readonly TextBlock _result = new();
    private readonly Button _importButton;
    private readonly Button _assignFrontolButton;

    public UnifiedImportWindow(Database database, Guid? fallbackOrganizationId, Action afterImport)
    {
        _database = database;
        _fallbackOrganizationId = fallbackOrganizationId;
        _afterImport = afterImport;

        Title = "Импорт файлов — Сбер, касса, Frontol, CRPT";
        Width = 1120;
        Height = 720;
        MinWidth = 900;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        AllowDrop = true;
        DragOver += Window_DragOver;
        Drop += Window_Drop;

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        title.Children.Add(new TextBlock
        {
            Text = "Один импорт для всех отчётов",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold
        });
        title.Children.Add(new TextBlock
        {
            Text = "Сбер, обычный кассовый отчёт Такском и CRPT определяются автоматически. Для каждого Frontol report.txt точка назначается отдельно.",
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(title);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var add = new Button
        {
            Content = "Добавить файлы...",
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        add.Click += Add_Click;

        _assignFrontolButton = new Button
        {
            Content = "Назначить Frontol...",
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = false,
            ToolTip = "Организация и точка задаются только для выбранного report.txt"
        };
        _assignFrontolButton.Click += (_, _) => AssignSelectedFrontol();

        var clear = new Button { Content = "Очистить", Padding = new Thickness(16, 8, 16, 8) };
        clear.Click += (_, _) =>
        {
            _files.Clear();
            RefreshGrid();
            _result.Text = "Добавьте отчёты.";
        };
        tools.Children.Add(add);
        tools.Children.Add(_assignFrontolButton);
        tools.Children.Add(clear);
        Grid.SetRow(tools, 1);
        root.Children.Add(tools);

        var center = new Grid();
        center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        ConfigureFileGrid();
        center.Children.Add(new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6),
            Child = _fileGrid
        });

        var resultPanel = new StackPanel();
        resultPanel.Children.Add(new TextBlock
        {
            Text = "Результат",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        _result.Text = "Добавьте отчёты.";
        _result.TextWrapping = TextWrapping.Wrap;
        resultPanel.Children.Add(_result);

        var resultBorder = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14),
            Margin = new Thickness(12, 0, 0, 0),
            Child = new ScrollViewer { Content = resultPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        Grid.SetColumn(resultBorder, 1);
        center.Children.Add(resultBorder);
        Grid.SetRow(center, 2);
        root.Children.Add(center);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        _importButton = new Button
        {
            Content = "Импортировать всё",
            Padding = new Thickness(18, 8, 18, 8),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
            IsEnabled = false
        };
        _importButton.Click += Import_Click;
        var close = new Button { Content = "Закрыть", Padding = new Thickness(18, 8, 18, 8), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(_importButton);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        Content = root;
    }

    private void ConfigureFileGrid()
    {
        _fileGrid.AutoGenerateColumns = false;
        _fileGrid.IsReadOnly = true;
        _fileGrid.SelectionMode = DataGridSelectionMode.Extended;
        _fileGrid.CanUserAddRows = false;
        _fileGrid.CanUserDeleteRows = false;
        _fileGrid.Columns.Add(new DataGridTextColumn { Header = "Файл", Binding = new System.Windows.Data.Binding(nameof(FileItem.FileName)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _fileGrid.Columns.Add(new DataGridTextColumn { Header = "Тип", Binding = new System.Windows.Data.Binding(nameof(FileItem.Type)), Width = 100 });
        _fileGrid.Columns.Add(new DataGridTextColumn { Header = "Организация", Binding = new System.Windows.Data.Binding(nameof(FileItem.Organization)), Width = 150 });
        _fileGrid.Columns.Add(new DataGridTextColumn { Header = "Торговая точка", Binding = new System.Windows.Data.Binding(nameof(FileItem.Location)), Width = 180 });
        _fileGrid.Columns.Add(new DataGridTextColumn { Header = "Статус", Binding = new System.Windows.Data.Binding(nameof(FileItem.Status)), Width = 185 });
        _fileGrid.SelectionChanged += (_, _) => UpdateImportEnabled();
        _fileGrid.MouseDoubleClick += (_, _) =>
        {
            if (_fileGrid.SelectedItem is FileItem { Kind: UnifiedImportKind.Frontol }) AssignSelectedFrontol();
        };
        _fileGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) RemoveSelected();
        };

        var menu = new ContextMenu();
        var assign = new MenuItem { Header = "Назначить Frontol..." };
        assign.Click += (_, _) => AssignSelectedFrontol();
        var remove = new MenuItem { Header = "Убрать из списка" };
        remove.Click += (_, _) => RemoveSelected();
        menu.Items.Add(assign);
        menu.Items.Add(remove);
        _fileGrid.ContextMenu = menu;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Поддерживаемые отчёты (*.zip;*.xlsx;*.txt;*.crpt)|*.zip;*.xlsx;*.txt;*.crpt|Все файлы (*.*)|*.*",
            Multiselect = true,
            Title = "Выберите отчёты Сбер, Такском, Frontol или CRPT"
        };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths);
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var unknown = new List<string>();
        foreach (var path in paths)
        {
            if (_files.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var kind = Detect(path);
            _files.Add(new FileItem(path, kind));
            if (kind == UnifiedImportKind.Unknown) unknown.Add(System.IO.Path.GetFileName(path));
        }

        RefreshGrid();
        _result.Text = unknown.Count == 0
            ? $"Готово к импорту: {_files.Count} файл(а/ов)."
            : "Не удалось определить тип: " + string.Join(", ", unknown) + ". Если это обычный кассовый отчёт Такском, обновите программу до версии с исправленным распознаванием.";
    }

    private void RefreshGrid()
    {
        _fileGrid.ItemsSource = null;
        _fileGrid.ItemsSource = _files;
        UpdateImportEnabled();
    }

    private void RemoveSelected()
    {
        var selected = _fileGrid.SelectedItems.Cast<FileItem>().ToArray();
        foreach (var item in selected) _files.Remove(item);
        RefreshGrid();
    }

    private void AssignSelectedFrontol()
    {
        if (_fileGrid.SelectedItem is not FileItem item || item.Kind != UnifiedImportKind.Frontol)
        {
            MessageBox.Show(this, "Выберите в таблице файл Frontol report.txt.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var organizations = _database.Organizations().OrderBy(x => x.Name).ToArray();
        var dialog = new Window
        {
            Owner = this,
            Title = "Назначение Frontol — " + item.FileName,
            Width = 620,
            Height = 260,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
        var panel = new Grid { Margin = new Thickness(22) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var orgBox = new ComboBox { ItemsSource = organizations, DisplayMemberPath = nameof(Organization.Name), Margin = new Thickness(0, 0, 0, 10) };
        var locBox = new ComboBox { DisplayMemberPath = nameof(Location.Name), Margin = new Thickness(0, 0, 0, 10) };
        var orgLabel = new TextBlock { Text = "Организация:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) };
        var locLabel = new TextBlock { Text = "Торговая точка:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) };
        Grid.SetRow(orgLabel, 0); Grid.SetColumn(orgLabel, 0);
        Grid.SetRow(orgBox, 0); Grid.SetColumn(orgBox, 1);
        Grid.SetRow(locLabel, 1); Grid.SetColumn(locLabel, 0);
        Grid.SetRow(locBox, 1); Grid.SetColumn(locBox, 1);
        panel.Children.Add(orgLabel); panel.Children.Add(orgBox); panel.Children.Add(locLabel); panel.Children.Add(locBox);

        void RefreshLocations()
        {
            var org = orgBox.SelectedItem as Organization;
            var locations = org is null
                ? Array.Empty<Location>()
                : _database.Locations().Where(x => x.OrganizationId == org.Id && x.IsActive).OrderBy(x => x.Name).ToArray();
            locBox.ItemsSource = locations;
            if (item.LocationId is Guid current) locBox.SelectedItem = locations.FirstOrDefault(x => x.Id == current);
            if (locBox.SelectedItem is null) locBox.SelectedItem = locations.FirstOrDefault();
        }

        orgBox.SelectionChanged += (_, _) => RefreshLocations();
        if (item.OrganizationId is Guid orgId)
            orgBox.SelectedItem = organizations.FirstOrDefault(x => x.Id == orgId);
        else if (_fallbackOrganizationId is Guid fallback)
            orgBox.SelectedItem = organizations.FirstOrDefault(x => x.Id == fallback);
        else
            orgBox.SelectedItem = organizations.FirstOrDefault();
        RefreshLocations();

        var hint = new TextBlock
        {
            Text = "Это назначение применяется только к выбранному report.txt.",
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 14)
        };
        Grid.SetRow(hint, 2); Grid.SetColumnSpan(hint, 2); panel.Children.Add(hint);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Сохранить", Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(18, 7, 18, 7), IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (orgBox.SelectedItem is not Organization org || locBox.SelectedItem is not Location loc)
            {
                MessageBox.Show(dialog, "Выберите организацию и торговую точку.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            item.OrganizationId = org.Id;
            item.LocationId = loc.Id;
            item.OrganizationName = org.Name;
            item.LocationName = loc.Name;
            dialog.DialogResult = true;
        };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3); Grid.SetColumnSpan(buttons, 2); panel.Children.Add(buttons);
        dialog.Content = panel;

        if (dialog.ShowDialog() == true) RefreshGrid();
    }

    private void UpdateImportEnabled()
    {
        var hasUnknown = _files.Any(x => x.Kind == UnifiedImportKind.Unknown);
        var frontolReady = _files.Where(x => x.Kind == UnifiedImportKind.Frontol)
            .All(x => x.OrganizationId is not null && x.LocationId is not null);
        _importButton.IsEnabled = _files.Count > 0 && !hasUnknown && frontolReady;
        _assignFrontolButton.IsEnabled = _fileGrid.SelectedItem is FileItem { Kind: UnifiedImportKind.Frontol };
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!_importButton.IsEnabled) return;
        var files = _files.ToArray();
        _importButton.IsEnabled = false;
        _result.Text = "Импортирую файлы...";

        try
        {
            var text = await Task.Run(() =>
            {
                var result = new List<string>();

                var sber = files.Where(x => x.Kind == UnifiedImportKind.Sber).Select(x => x.Path).ToArray();
                if (sber.Length > 0)
                {
                    var summary = new SberAcquiringImporter(_database).ImportFiles(sber);
                    result.Add("СБЕР\n" + summary.ToDisplayText());
                }

                var taxcom = files.Where(x => x.Kind == UnifiedImportKind.Taxcom).Select(x => x.Path).ToArray();
                if (taxcom.Length > 0)
                {
                    var summary = new TaxcomShiftReportImporter(_database, _fallbackOrganizationId).ImportFiles(taxcom);
                    result.Add("ТАКСКОМ\n" + summary.ToDisplayText());
                }

                var crpt = files.Where(x => x.Kind == UnifiedImportKind.Crpt).Select(x => x.Path).ToArray();
                if (crpt.Length > 0)
                {
                    var summary = new CrptArchiveImporter(_database).ImportFiles(crpt);
                    result.Add("CRPT\n" + summary.ToDisplayText());
                }

                var frontolGroups = files.Where(x => x.Kind == UnifiedImportKind.Frontol)
                    .GroupBy(x => (OrganizationId: x.OrganizationId!.Value, LocationId: x.LocationId!.Value));
                foreach (var group in frontolGroups)
                {
                    var summary = new FrontolReportImporter(_database, group.Key.OrganizationId, group.Key.LocationId)
                        .ImportFiles(group.Select(x => x.Path));
                    result.Add($"FRONTOL — {group.First().LocationName}\n" + summary.ToDisplayText());
                }

                var matching = _database.RebuildCrossSourceShiftMatches();
                result.Add($"Сопоставление кассовых источников\nСовпавших Taxcom + Frontol: {matching.MatchedPairs}\nКонфликтов, требующих проверки: {matching.Conflicts}");
                return string.Join("\n\n------------------------------\n\n", result);
            });

            _result.Text = text;
            _afterImport();
        }
        catch (Exception ex)
        {
            _result.Text = "Импорт не выполнен: " + ex.Message;
        }
        finally
        {
            UpdateImportEnabled();
        }
    }

    public static UnifiedImportKind Detect(string path)
    {
        if (!File.Exists(path)) return UnifiedImportKind.Unknown;
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();

        try
        {
            if (extension == ".crpt")
            {
                using var stream = File.OpenRead(path);
                Span<byte> header = stackalloc byte[4];
                return stream.Read(header) == 4 && header.SequenceEqual("CRPT"u8)
                    ? UnifiedImportKind.Crpt
                    : UnifiedImportKind.Unknown;
            }

            if (extension == ".txt") return LooksLikeFrontol(path) ? UnifiedImportKind.Frontol : UnifiedImportKind.Unknown;

            if (extension == ".xlsx")
            {
                using var stream = File.OpenRead(path);
                return DetectWorkbook(stream, System.IO.Path.GetFileName(path));
            }

            if (extension == ".zip")
            {
                using var archive = ZipFile.OpenRead(path);
                var detected = new HashSet<UnifiedImportKind>();
                foreach (var entry in archive.Entries.Where(x => x.FullName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)).Take(50))
                {
                    using var input = entry.Open();
                    using var memory = new MemoryStream();
                    input.CopyTo(memory);
                    memory.Position = 0;
                    var kind = DetectWorkbook(memory, entry.Name);
                    if (kind != UnifiedImportKind.Unknown) detected.Add(kind);
                }
                return detected.Count == 1 ? detected.Single() : UnifiedImportKind.Unknown;
            }
        }
        catch
        {
            return UnifiedImportKind.Unknown;
        }

        return UnifiedImportKind.Unknown;
    }

    private static bool LooksLikeFrontol(string path)
    {
        using var reader = new StreamReader(path);
        for (var i = 0; i < 200 && !reader.EndOfStream; i++)
        {
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var fields = line.Split(';');
            if (fields.Length < 14) continue;
            if (int.TryParse(fields.ElementAtOrDefault(3), out var transactionType) && transactionType is 40 or 55 or 56 or 61)
                return true;
        }
        return false;
    }

    private static UnifiedImportKind DetectWorkbook(Stream stream, string fileName)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart;
        if (workbookPart?.Workbook.Sheets is null) return UnifiedImportKind.Unknown;

        var shared = workbookPart.SharedStringTablePart?.SharedStringTable
            .Elements<SharedStringItem>().Select(x => x.InnerText).ToArray() ?? [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheet in workbookPart.Workbook.Sheets.Elements<Sheet>().Take(20))
        {
            var id = sheet.Id?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (workbookPart.GetPartById(id) is not WorksheetPart worksheetPart) continue;
            var data = worksheetPart.Worksheet.GetFirstChild<SheetData>();
            if (data is null) continue;

            foreach (var row in data.Elements<Row>().Take(100))
            {
                foreach (var cell in row.Elements<Cell>())
                {
                    var value = NormalizeToken(ReadCell(cell, shared));
                    if (!string.IsNullOrWhiteSpace(value)) seen.Add(value);
                }
            }
        }

        var taxcomCore = new[] { "дата закрытия", "№ смены", "выручка нал.", "выручка безнал." };
        var taxcomIdentity = new[] { "название ккт", "зав. № фн", "рег. № ккт", "зав. № ккт" };
        var taxcomTitle = seen.Contains("такском-касса") ||
                          seen.Contains("сводный отчет по сменам") ||
                          NormalizeToken(fileName).Contains("сводный отчет по сменам", StringComparison.Ordinal);
        if (taxcomCore.All(seen.Contains) && (taxcomTitle || taxcomIdentity.Any(seen.Contains)))
            return UnifiedImportKind.Taxcom;

        var sberCore = new[] { "инн", "наименование тст", "номер терминала", "дата операции", "сумма операции" };
        var sberMarker = seen.Contains("наименование юридического лица") || seen.Contains("rrn");
        if (sberCore.All(seen.Contains) && sberMarker)
            return UnifiedImportKind.Sber;

        return UnifiedImportKind.Unknown;
    }

    private static string NormalizeToken(string value)
    {
        var text = (value ?? string.Empty).Replace('\u00A0', ' ').Trim().ToLowerInvariant();
        text = Regex.Replace(text, @"\s+", " ");
        text = text.Replace('ё', 'е');
        return text;
    }

    private static string ReadCell(Cell cell, string[] shared)
    {
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(cell.CellValue?.InnerText, out var index) &&
            index >= 0 && index < shared.Length)
            return shared[index];
        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? string.Empty;
        return cell.CellValue?.InnerText ?? cell.InnerText ?? string.Empty;
    }

    private static string KindText(UnifiedImportKind kind) => kind switch
    {
        UnifiedImportKind.Sber => "Сбер",
        UnifiedImportKind.Taxcom => "Такском",
        UnifiedImportKind.Frontol => "Frontol",
        UnifiedImportKind.Crpt => "CRPT",
        _ => "Неизвестно"
    };
}

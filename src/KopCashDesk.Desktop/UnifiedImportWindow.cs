using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Core;
using KopCashDesk.Data;
using Microsoft.Win32;
using System.IO.Compression;
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
    private sealed record FileItem(string Path, UnifiedImportKind Kind)
    {
        public string Display => $"{KindText(Kind),-10}  {System.IO.Path.GetFileName(Path)}";
    }

    private readonly Database _database;
    private readonly Action _afterImport;
    private readonly List<FileItem> _files = [];
    private readonly ListBox _fileList = new();
    private readonly TextBlock _result = new();
    private readonly ComboBox _organizationBox = new();
    private readonly ComboBox _locationBox = new();
    private readonly Button _importButton;

    public UnifiedImportWindow(Database database, Guid? fallbackOrganizationId, Action afterImport)
    {
        _database = database;
        _afterImport = afterImport;

        Title = "Импорт файлов — Сбер, касса, Frontol, CRPT";
        Width = 980;
        Height = 690;
        MinWidth = 800;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        AllowDrop = true;
        DragOver += Window_DragOver;
        Drop += Window_Drop;

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
            Text = "Можно выбрать сразу ZIP/XLSX Сбера, XLSX/ZIP Такском, Frontol report.txt и архивы ККТ .crpt. Тип файла определяется автоматически.",
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(title);

        var frontolTarget = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        frontolTarget.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        frontolTarget.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        frontolTarget.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        frontolTarget.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        frontolTarget.Children.Add(new TextBlock
        {
            Text = "Frontol — организация:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });

        _organizationBox.DisplayMemberPath = nameof(Organization.Name);
        _organizationBox.MinWidth = 240;
        _organizationBox.Margin = new Thickness(0, 0, 18, 0);
        _organizationBox.SelectionChanged += (_, _) => RefreshLocations();
        Grid.SetColumn(_organizationBox, 1);
        frontolTarget.Children.Add(_organizationBox);

        var pointLabel = new TextBlock
        {
            Text = "точка:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(pointLabel, 2);
        frontolTarget.Children.Add(pointLabel);

        _locationBox.DisplayMemberPath = nameof(Location.Name);
        _locationBox.MinWidth = 280;
        _locationBox.SelectionChanged += (_, _) => UpdateImportEnabled();
        Grid.SetColumn(_locationBox, 3);
        frontolTarget.Children.Add(_locationBox);

        Grid.SetRow(frontolTarget, 1);
        root.Children.Add(frontolTarget);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var add = new Button
        {
            Content = "Добавить файлы...",
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        add.Click += Add_Click;

        var clear = new Button { Content = "Очистить", Padding = new Thickness(16, 8, 16, 8) };
        clear.Click += (_, _) =>
        {
            _files.Clear();
            RefreshList();
            _result.Text = "Добавьте отчёты.";
        };
        tools.Children.Add(add);
        tools.Children.Add(clear);
        Grid.SetRow(tools, 2);
        root.Children.Add(tools);

        var center = new Grid();
        center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        var listBorder = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6)
        };
        _fileList.SelectionMode = SelectionMode.Extended;
        _fileList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) RemoveSelected();
        };
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = "Убрать из списка" };
        remove.Click += (_, _) => RemoveSelected();
        menu.Items.Add(remove);
        _fileList.ContextMenu = menu;
        listBorder.Child = _fileList;
        center.Children.Add(listBorder);

        var resultBorder = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14),
            Margin = new Thickness(12, 0, 0, 0)
        };
        var resultPanel = new StackPanel();
        resultPanel.Children.Add(new TextBlock
        {
            Text = "Результат",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        _result.Text = "Добавьте отчёты. Неизвестный формат программа не будет угадывать.";
        _result.TextWrapping = TextWrapping.Wrap;
        resultPanel.Children.Add(_result);
        resultBorder.Child = new ScrollViewer { Content = resultPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(resultBorder, 1);
        center.Children.Add(resultBorder);

        Grid.SetRow(center, 3);
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
        var close = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(18, 8, 18, 8),
            IsCancel = true
        };
        close.Click += (_, _) => Close();
        buttons.Children.Add(_importButton);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        Content = root;
        LoadOrganizations(fallbackOrganizationId);
    }

    private void LoadOrganizations(Guid? fallbackOrganizationId)
    {
        var organizations = _database.Organizations().ToArray();
        _organizationBox.ItemsSource = organizations;
        _organizationBox.SelectedItem = fallbackOrganizationId is Guid id
            ? organizations.FirstOrDefault(x => x.Id == id)
            : organizations.Length == 1 ? organizations[0] : null;
        RefreshLocations();
    }

    private void RefreshLocations()
    {
        var organization = _organizationBox.SelectedItem as Organization;
        var locations = organization is null
            ? Array.Empty<Location>()
            : _database.Locations()
                .Where(x => x.OrganizationId == organization.Id && !x.IsExcluded)
                .OrderBy(x => x.Name)
                .ToArray();

        _locationBox.ItemsSource = locations;
        var preferred = locations.FirstOrDefault(IsCafeteria6);
        if (preferred is null && locations.Length == 1) preferred = locations[0];
        _locationBox.SelectedItem = preferred;
        UpdateImportEnabled();
    }

    private static bool IsCafeteria6(Location location)
    {
        var name = SberAcquiringImporter.NormalizeForMatch(location.Name);
        return name.Contains("рефтин", StringComparison.Ordinal)
               && name.Contains("грэс", StringComparison.Ordinal)
               && (name.Contains("6 стол", StringComparison.Ordinal) || name.Contains("столовая 6", StringComparison.Ordinal));
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

        RefreshList();
        _result.Text = unknown.Count == 0
            ? $"Готово к импорту: {_files.Count} файл(а/ов)."
            : "Не удалось определить тип: " + string.Join(", ", unknown);
    }

    private void RefreshList()
    {
        _fileList.ItemsSource = null;
        _fileList.ItemsSource = _files.Select(x => x.Display).ToArray();
        UpdateImportEnabled();
    }

    private void RemoveSelected()
    {
        var indices = _fileList.SelectedItems.Cast<object>()
            .Select(item => _fileList.Items.IndexOf(item))
            .Where(index => index >= 0)
            .OrderByDescending(index => index)
            .ToArray();
        foreach (var index in indices) _files.RemoveAt(index);
        RefreshList();
    }

    private void UpdateImportEnabled()
    {
        var hasUnknown = _files.Any(x => x.Kind == UnifiedImportKind.Unknown);
        var hasFrontol = _files.Any(x => x.Kind == UnifiedImportKind.Frontol);
        var frontolReady = !hasFrontol ||
                           (_organizationBox.SelectedItem is Organization && _locationBox.SelectedItem is Location);
        _importButton.IsEnabled = _files.Count > 0 && !hasUnknown && frontolReady;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!_importButton.IsEnabled) return;

        var organization = _organizationBox.SelectedItem as Organization;
        var location = _locationBox.SelectedItem as Location;
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
                    var summary = new TaxcomShiftReportImporter(_database, organization?.Id).ImportFiles(taxcom);
                    result.Add("ТАКСКОМ\n" + summary.ToDisplayText());
                }

                var crpt = files.Where(x => x.Kind == UnifiedImportKind.Crpt).Select(x => x.Path).ToArray();
                if (crpt.Length > 0)
                {
                    var summary = new CrptArchiveImporter(_database).ImportFiles(crpt);
                    result.Add("CRPT\n" + summary.ToDisplayText());
                }

                var frontol = files.Where(x => x.Kind == UnifiedImportKind.Frontol).Select(x => x.Path).ToArray();
                if (frontol.Length > 0)
                {
                    if (organization is null || location is null)
                        throw new InvalidDataException("Для Frontol нужно выбрать организацию и точку.");
                    var summary = new FrontolReportImporter(_database, organization.Id, location.Id).ImportFiles(frontol);
                    result.Add("FRONTOL\n" + summary.ToDisplayText());
                }

                var repaired = _database.EnsureV052Fixes();
                if (repaired > 0) result.Add($"Проверка дублей: исправлено {repaired} записей.");

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
                return stream.Read(header) == 4 &&
                       header[0] == (byte)'C' && header[1] == (byte)'R' &&
                       header[2] == (byte)'P' && header[3] == (byte)'T'
                    ? UnifiedImportKind.Crpt
                    : UnifiedImportKind.Unknown;
            }

            if (extension == ".txt")
            {
                using var reader = new StreamReader(path);
                for (var i = 0; i < 50 && !reader.EndOfStream; i++)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                    var fields = line.Split(';');
                    if (fields.Length >= 14 && int.TryParse(fields[3], out var transactionType) &&
                        transactionType is 40 or 55 or 56 or 61)
                        return UnifiedImportKind.Frontol;
                }
                return UnifiedImportKind.Unknown;
            }

            if (extension == ".xlsx")
            {
                using var stream = File.OpenRead(path);
                return DetectWorkbook(stream);
            }

            if (extension == ".zip")
            {
                using var archive = ZipFile.OpenRead(path);
                var detected = new HashSet<UnifiedImportKind>();
                foreach (var entry in archive.Entries.Where(x => x.FullName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)).Take(20))
                {
                    using var input = entry.Open();
                    using var memory = new MemoryStream();
                    input.CopyTo(memory);
                    memory.Position = 0;
                    var kind = DetectWorkbook(memory);
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

    private static UnifiedImportKind DetectWorkbook(Stream stream)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart;
        if (workbookPart?.Workbook.Sheets is null) return UnifiedImportKind.Unknown;

        var shared = workbookPart.SharedStringTablePart?.SharedStringTable
            .Elements<SharedStringItem>().Select(x => x.InnerText).ToArray() ?? [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheet in workbookPart.Workbook.Sheets.Elements<Sheet>().Take(12))
        {
            var id = sheet.Id?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (workbookPart.GetPartById(id) is not WorksheetPart worksheetPart) continue;
            var data = worksheetPart.Worksheet.GetFirstChild<SheetData>();
            if (data is null) continue;

            foreach (var row in data.Elements<Row>().Take(50))
            {
                foreach (var cell in row.Elements<Cell>())
                {
                    var value = ReadCell(cell, shared).Trim();
                    if (!string.IsNullOrWhiteSpace(value)) seen.Add(value);
                }
            }
        }

        string[] taxcom = ["Дата закрытия", "№ смены", "Выручка нал.", "Выручка безнал.", "Название ККТ", "Зав. № ФН"];
        if (taxcom.All(seen.Contains)) return UnifiedImportKind.Taxcom;

        string[] sber = ["Наименование юридического лица", "ИНН", "Наименование ТСТ", "Номер терминала", "RRN", "Дата операции", "Сумма операции"];
        if (sber.All(seen.Contains)) return UnifiedImportKind.Sber;

        return UnifiedImportKind.Unknown;
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

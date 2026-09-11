using KopCashDesk.Core;
using KopCashDesk.Data;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KopCashDesk.Desktop;

public sealed class FrontolReportImportWindow : Window
{
    private readonly Database _database;
    private readonly Action _afterImport;
    private readonly List<string> _files = [];
    private readonly ListBox _fileList = new();
    private readonly TextBlock _result = new();
    private readonly ComboBox _organizationBox = new();
    private readonly ComboBox _locationBox = new();
    private readonly Button _importButton;

    public FrontolReportImportWindow(Database database, Guid? fallbackOrganizationId, Action afterImport)
    {
        _database = database;
        _afterImport = afterImport;

        Title = "Frontol 6 — импорт report.txt";
        Width = 940;
        Height = 660;
        MinWidth = 760;
        MinHeight = 520;
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
        title.Children.Add(new TextBlock { Text = "Импорт полной выгрузки Frontol 6", FontSize = 24, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock
        {
            Text = "Выберите report.txt, выгруженный из Frontol. Программа сама соберёт закрытые смены, наличные и безнал. Отменённые чеки в суммы не попадут. Большой файл читается потоком и не загружается целиком в память.",
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(title);

        var target = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        target.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        target.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        target.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        target.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        target.Children.Add(new TextBlock { Text = "Организация:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _organizationBox.DisplayMemberPath = nameof(Organization.Name);
        _organizationBox.MinWidth = 240;
        _organizationBox.Margin = new Thickness(0, 0, 18, 0);
        _organizationBox.SelectionChanged += (_, _) => RefreshLocations();
        Grid.SetColumn(_organizationBox, 1);
        target.Children.Add(_organizationBox);
        var pointLabel = new TextBlock { Text = "Точка Frontol:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(pointLabel, 2);
        target.Children.Add(pointLabel);
        _locationBox.DisplayMemberPath = nameof(Location.Name);
        _locationBox.MinWidth = 280;
        _locationBox.SelectionChanged += (_, _) => UpdateImportEnabled();
        Grid.SetColumn(_locationBox, 3);
        target.Children.Add(_locationBox);
        Grid.SetRow(target, 1);
        root.Children.Add(target);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var add = new Button { Content = "Добавить report.txt...", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        add.Click += Add_Click;
        var clear = new Button { Content = "Очистить список", Padding = new Thickness(14, 7, 14, 7) };
        clear.Click += (_, _) => { _files.Clear(); RefreshList(); };
        tools.Children.Add(add);
        tools.Children.Add(clear);
        Grid.SetRow(tools, 2);
        root.Children.Add(tools);

        var center = new Grid();
        center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        var listBorder = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(6) };
        _fileList.SelectionMode = SelectionMode.Extended;
        _fileList.KeyDown += (_, e) => { if (e.Key == Key.Delete) RemoveSelected(); };
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = "Убрать из списка" };
        remove.Click += (_, _) => RemoveSelected();
        menu.Items.Add(remove);
        _fileList.ContextMenu = menu;
        listBorder.Child = _fileList;
        center.Children.Add(listBorder);

        var resultBorder = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(14), Margin = new Thickness(12, 0, 0, 0) };
        var resultPanel = new StackPanel();
        resultPanel.Children.Add(new TextBlock { Text = "Результат", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        _result.Text = "Выберите организацию, точку и report.txt.";
        _result.TextWrapping = TextWrapping.Wrap;
        resultPanel.Children.Add(_result);
        resultBorder.Child = new ScrollViewer { Content = resultPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(resultBorder, 1);
        center.Children.Add(resultBorder);
        Grid.SetRow(center, 3);
        root.Children.Add(center);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        _importButton = new Button { Content = "Импортировать", Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 8, 0), IsDefault = true, IsEnabled = false };
        _importButton.Click += Import_Click;
        var close = new Button { Content = "Закрыть", Padding = new Thickness(18, 8, 18, 8), IsCancel = true };
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
            : _database.Locations().Where(x => x.OrganizationId == organization.Id && !x.IsExcluded).OrderBy(x => x.Name).ToArray();

        _locationBox.ItemsSource = locations;
        Location? preferred = locations.FirstOrDefault(IsCafeteria6);
        if (preferred is null && locations.Length == 1) preferred = locations[0];
        _locationBox.SelectedItem = preferred;
        UpdateImportEnabled();
    }

    private static bool IsCafeteria6(Location location)
    {
        var name = SberAcquiringImporter.NormalizeForMatch(location.Name);
        return name.Contains("рефтин", StringComparison.Ordinal) &&
               name.Contains("грэс", StringComparison.Ordinal) &&
               (name.Contains("6 стол", StringComparison.Ordinal) || name.Contains("столовая 6", StringComparison.Ordinal));
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Выгрузка Frontol 6 (report.txt;*.txt)|report.txt;*.txt|Текстовые файлы (*.txt)|*.txt",
            Multiselect = true,
            Title = "Выберите report.txt из Frontol 6"
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
        foreach (var path in paths)
        {
            if (!Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)) continue;
            if (!_files.Contains(path, StringComparer.OrdinalIgnoreCase)) _files.Add(path);
        }
        RefreshList();
        _result.Text = _files.Count == 0 ? "Нет подходящих TXT-файлов." : $"Готово к импорту: {_files.Count} файл(а/ов).";
    }

    private void RefreshList()
    {
        _fileList.ItemsSource = null;
        _fileList.ItemsSource = _files.Select(Path.GetFileName).ToArray();
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

    private void UpdateImportEnabled() =>
        _importButton.IsEnabled = _files.Count > 0 && _organizationBox.SelectedItem is Organization && _locationBox.SelectedItem is Location;

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_files.Count == 0 || _organizationBox.SelectedItem is not Organization organization || _locationBox.SelectedItem is not Location location) return;

        _importButton.IsEnabled = false;
        _result.Text = "Читаю Frontol report.txt. Большой файл может обрабатываться несколько минут...";
        try
        {
            var files = _files.ToArray();
            var summary = await Task.Run(() => new FrontolReportImporter(_database, organization.Id, location.Id).ImportFiles(files));
            _result.Text = summary.ToDisplayText();
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
}

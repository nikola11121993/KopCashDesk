using KopCashDesk.Data;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KopCashDesk.Desktop;

public sealed class TaxcomShiftImportWindow : Window
{
    private readonly Database _database;
    private readonly Guid? _fallbackOrganizationId;
    private readonly Action _afterImport;
    private readonly List<string> _files = [];
    private readonly ListBox _fileList = new();
    private readonly TextBlock _result = new();
    private readonly Button _importButton;

    public TaxcomShiftImportWindow(Database database, Guid? fallbackOrganizationId, Action afterImport)
    {
        _database = database;
        _fallbackOrganizationId = fallbackOrganizationId;
        _afterImport = afterImport;

        Title = "Такском — импорт кассовых отчётов";
        Width = 900;
        Height = 620;
        MinWidth = 720;
        MinHeight = 480;
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
        title.Children.Add(new TextBlock { Text = "Импорт кассовых смен Такском", FontSize = 24, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock
        {
            Text = "Перетащите XLSX или ZIP с отчётом «Сводный отчет по сменам». ИНН берётся из имени файла, ККТ определяется по ФН и названию, суммы смен загружаются автоматически.",
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(title);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var add = new Button { Content = "Добавить файлы...", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        add.Click += Add_Click;
        var clear = new Button { Content = "Очистить список", Padding = new Thickness(14, 7, 14, 7) };
        clear.Click += (_, _) => { _files.Clear(); RefreshList(); };
        tools.Children.Add(add);
        tools.Children.Add(clear);
        Grid.SetRow(tools, 1);
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
        _result.Text = "Добавьте один или несколько кассовых отчётов Такском.";
        _result.TextWrapping = TextWrapping.Wrap;
        resultPanel.Children.Add(_result);
        resultBorder.Child = new ScrollViewer { Content = resultPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(resultBorder, 1);
        center.Children.Add(resultBorder);
        Grid.SetRow(center, 2);
        root.Children.Add(center);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        _importButton = new Button { Content = "Импортировать", Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 8, 0), IsDefault = true, IsEnabled = false };
        _importButton.Click += Import_Click;
        var close = new Button { Content = "Закрыть", Padding = new Thickness(18, 8, 18, 8), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(_importButton);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        Content = root;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Кассовые отчёты Такском (*.xlsx;*.zip)|*.xlsx;*.zip|Excel (*.xlsx)|*.xlsx|ZIP (*.zip)|*.zip",
            Multiselect = true,
            Title = "Выберите сводные отчёты Такском по сменам"
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
            var extension = System.IO.Path.GetExtension(path);
            if (!extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (!_files.Contains(path, StringComparer.OrdinalIgnoreCase)) _files.Add(path);
        }
        RefreshList();
        _result.Text = _files.Count == 0 ? "Нет подходящих файлов." : $"Готово к импорту: {_files.Count} файл(а/ов).";
    }

    private void RefreshList()
    {
        _fileList.ItemsSource = null;
        _fileList.ItemsSource = _files.Select(System.IO.Path.GetFileName).ToArray();
        _importButton.IsEnabled = _files.Count > 0;
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

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_files.Count == 0) return;
        _importButton.IsEnabled = false;
        _result.Text = "Импорт...";
        try
        {
            var files = _files.ToArray();
            var summary = await Task.Run(() => new TaxcomShiftReportImporter(_database, _fallbackOrganizationId).ImportFiles(files));
            _result.Text = summary.ToDisplayText();
            _afterImport();
        }
        catch (Exception ex)
        {
            _result.Text = "Импорт не выполнен: " + ex.Message;
        }
        finally
        {
            _importButton.IsEnabled = _files.Count > 0;
        }
    }
}

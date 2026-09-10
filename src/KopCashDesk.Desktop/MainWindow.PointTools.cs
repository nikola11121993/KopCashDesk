using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private sealed record TerminalRow(
        TerminalBinding Binding,
        string Organization,
        string Point,
        string Provider,
        string Tid,
        string Mid,
        string Method);

    private void MergeLocations_Click(object sender, RoutedEventArgs e)
    {
        var organizationId = SelectedOrganizationId;
        if (organizationId is null)
        {
            MessageBox.Show(this, "Сначала выберите организацию.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var points = _locations
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Address)
            .ToArray();
        if (points.Length < 2)
        {
            MessageBox.Show(this, "Для объединения нужно минимум две точки одной организации.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var source = new ComboBox { ItemsSource = points, DisplayMemberPath = "Name", MinWidth = 420 };
        var target = new ComboBox { MinWidth = 420 };
        var sourceAddress = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 10) };
        var targetAddress = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 10) };

        void RefreshTargets()
        {
            var selected = source.SelectedItem as Location;
            sourceAddress.Text = selected?.Address ?? "";
            var available = points.Where(x => selected is null || x.Id != selected.Id).ToArray();
            target.ItemsSource = available;
            if (target.SelectedItem is not Location current || available.All(x => x.Id != current.Id))
                target.SelectedItem = available.FirstOrDefault();
            targetAddress.Text = (target.SelectedItem as Location)?.Address ?? "";
        }

        source.SelectionChanged += (_, _) => RefreshTargets();
        target.SelectionChanged += (_, _) => targetAddress.Text = (target.SelectedItem as Location)?.Address ?? "";
        source.SelectedItem = points[0];
        RefreshTargets();

        var warning = Text("Все операции, смены, кассы, ККТ и терминалы исходной точки будут перенесены в выбранную основную точку. Исходная точка после этого удалится.");
        warning.FontWeight = FontWeights.SemiBold;

        var content = Stack(
            Text("Что объединяем"), source, sourceAddress,
            Text("Куда переносим — эта точка останется"), target, targetAddress,
            warning);

        if (!Dialog("Объединить торговые точки", content, () => source.SelectedItem is Location && target.SelectedItem is Location)) return;

        var sourcePoint = (Location)source.SelectedItem;
        var targetPoint = (Location)target.SelectedItem;
        if (sourcePoint.Id == targetPoint.Id) return;

        var answer = MessageBox.Show(this,
            $"Объединить «{sourcePoint.Name}» в «{targetPoint.Name}»?\n\nПосле объединения «{sourcePoint.Name}» исчезнет из списка, а вся история останется у «{targetPoint.Name}».",
            "Подтверждение объединения",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            if (_db.MergeLocations(sourcePoint.Id, targetPoint.Id, "ручное объединение пользователем"))
            {
                RefreshAll();
                StatusText.Text = $"Объединено: {sourcePoint.Name} → {targetPoint.Name}";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось объединить точки", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Terminals_Click(object sender, RoutedEventArgs e)
    {
        _page = "terminals";
        SearchBox.Text = "";
        SearchBox.IsEnabled = false;
        PrimaryButton.Visibility = Visibility.Collapsed;
        PageContent.Content = RenderTerminals();
    }

    private UIElement RenderTerminals()
    {
        PageTitle.Text = "Терминалы";
        PageSubtitle.Text = "Редактирование TID, точки, способа оплаты и других привязок";

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var organizationId = SelectedOrganizationId;
        var pointOptions = new List<Location?> { null };
        pointOptions.AddRange(_locations
            .Where(x => organizationId is null || x.OrganizationId == organizationId)
            .OrderBy(x => x.Name));

        var pointBox = new ComboBox { Width = 310, Margin = new Thickness(0, 0, 10, 10) };
        pointBox.ItemsSource = pointOptions;
        pointBox.ItemTemplate = TerminalLocationTemplate();
        pointBox.SelectedIndex = 0;

        var add = new Button { Content = "+ Терминал", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 10) };
        add.Click += (_, _) => EditTerminal(null);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(new TextBlock { Text = "Точка:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 10) });
        tools.Children.Add(pointBox);
        tools.Children.Add(add);
        root.Children.Add(tools);

        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            SelectionMode = DataGridSelectionMode.Single
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Точка", Binding = new Binding("Point"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Провайдер", Binding = new Binding("Provider"), Width = 115 });
        grid.Columns.Add(new DataGridTextColumn { Header = "TID", Binding = new Binding("Tid"), Width = 145 });
        grid.Columns.Add(new DataGridTextColumn { Header = "MID", Binding = new Binding("Mid"), Width = 155 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Способ", Binding = new Binding("Method"), Width = 100 });

        void RefreshRows()
        {
            var selectedPoint = pointBox.SelectedItem as Location;
            var locations = _db.Locations();
            var organizations = _db.Organizations();
            var rows = _db.TerminalBindings()
                .Where(x => organizationId is null || x.OrganizationId == organizationId)
                .Where(x => selectedPoint is null || x.LocationId == selectedPoint.Id)
                .Select(x => new TerminalRow(
                    x,
                    organizations.FirstOrDefault(o => o.Id == x.OrganizationId)?.Name ?? "",
                    locations.FirstOrDefault(l => l.Id == x.LocationId)?.Name ?? "Неизвестная точка",
                    x.Provider,
                    x.TerminalId,
                    x.MerchantId,
                    x.PaymentMethod))
                .OrderBy(x => x.Point)
                .ThenBy(x => x.Tid)
                .ToArray();
            grid.ItemsSource = rows;
        }

        pointBox.SelectionChanged += (_, _) => RefreshRows();
        grid.MouseDoubleClick += (_, _) =>
        {
            if (grid.SelectedItem is TerminalRow row) EditTerminal(row.Binding);
        };

        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Изменить терминал..." };
        edit.Click += (_, _) => { if (grid.SelectedItem is TerminalRow row) EditTerminal(row.Binding); };
        var remove = new MenuItem { Header = "Удалить привязку терминала" };
        remove.Click += (_, _) =>
        {
            if (grid.SelectedItem is not TerminalRow row) return;
            if (MessageBox.Show(this,
                    $"Удалить привязку TID {row.Tid} к точке «{row.Point}»?\nСами уже загруженные операции не удалятся.",
                    "Удалить терминал", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _db.DeleteTerminal(row.Binding.Id);
            RefreshRows();
            StatusText.Text = $"Удалена привязка терминала {row.Tid}";
        };
        menu.Items.Add(edit);
        menu.Items.Add(remove);
        grid.ContextMenu = menu;

        Grid.SetRow(grid, 1);
        root.Children.Add(grid);
        RefreshRows();
        return root;
    }

    private void EditTerminal(TerminalBinding? original)
    {
        var organizationId = original?.OrganizationId ?? SelectedOrganizationId;
        if (organizationId is null)
        {
            MessageBox.Show(this, "Сначала выберите организацию.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var points = _locations.Where(x => x.OrganizationId == organizationId).OrderBy(x => x.Name).ToArray();
        if (points.Length == 0)
        {
            MessageBox.Show(this, "У этой организации нет торговых точек.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var point = new ComboBox { ItemsSource = points, DisplayMemberPath = "Name", SelectedItem = points.FirstOrDefault(x => x.Id == original?.LocationId) ?? points[0] };
        var provider = new TextBox { Text = original?.Provider ?? "Sber" };
        var tid = new TextBox { Text = original?.TerminalId ?? "" };
        var mid = new TextBox { Text = original?.MerchantId ?? "" };
        var method = new ComboBox
        {
            ItemsSource = new[] { "POS", "QR", "SBP", "FACE", "SBERPAY" },
            IsEditable = true,
            Text = original?.PaymentMethod ?? "POS"
        };

        var content = Stack(
            Text("Торговая точка"), point,
            Text("Провайдер"), provider,
            Text("TID"), tid,
            Text("MID"), mid,
            Text("Способ оплаты"), method);

        if (!Dialog(original is null ? "Новый терминал" : "Изменить терминал", content,
                () => point.SelectedItem is Location && !string.IsNullOrWhiteSpace(provider.Text) && !string.IsNullOrWhiteSpace(tid.Text))) return;

        var binding = new TerminalBinding(
            original?.Id ?? Guid.NewGuid(),
            organizationId.Value,
            ((Location)point.SelectedItem).Id,
            provider.Text.Trim(),
            tid.Text.Trim(),
            mid.Text.Trim(),
            string.IsNullOrWhiteSpace(method.Text) ? "POS" : method.Text.Trim().ToUpperInvariant());

        try
        {
            _db.SaveEditableTerminal(binding);
            RefreshAll();
            if (_page == "terminals") PageContent.Content = RenderTerminals();
            StatusText.Text = $"Терминал {binding.TerminalId} сохранён";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось сохранить терминал", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static DataTemplate TerminalLocationTemplate()
    {
        var template = new DataTemplate();
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("Name") { TargetNullValue = "Все точки" });
        template.VisualTree = text;
        return template;
    }
}

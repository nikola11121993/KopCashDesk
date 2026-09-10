using System.Windows;
using System.Windows.Controls;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private void SberImport_Click(object sender, RoutedEventArgs e)
    {
        var window = new SberImportWindow(_db, () =>
        {
            RefreshAll();
            StatusText.Text = "Архивы Сбер обработаны";
        }) { Owner = this };
        window.ShowDialog();
    }

    private UIElement RenderOperations()
    {
        PageTitle.Text = "Операции";
        PageSubtitle.Text = "Загруженные операции по кассам и эквайрингу";

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var import = new Button { Content = "Импорт архивов Сбер...", Padding = new Thickness(14, 7, 14, 7) };
        import.Click += SberImport_Click;
        toolbar.Children.Add(import);
        root.Children.Add(toolbar);

        var rows = _db.Operations(SelectedOrganizationId)
            .Select(x => new
            {
                Date = x.OccurredAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss"),
                x.Organization,
                Point = x.Location,
                Source = x.Source == "Sber.Acquiring" ? "Сбер" : x.Source,
                Type = x.Kind == KopCashDesk.Core.OperationKind.Return ? "Возврат" : x.Kind == KopCashDesk.Core.OperationKind.Sale ? "Оплата" : x.Kind.ToString(),
                Amount = x.Amount
            })
            .ToArray();

        if (rows.Length == 0)
        {
            var empty = Text("Операций пока нет. Перетащите архивы Сбер в окно импорта или выберите их через кнопку выше.", 15);
            Grid.SetRow(empty, 1);
            root.Children.Add(empty);
            return root;
        }

        var grid = new DataGrid { ItemsSource = rows, IsReadOnly = true };
        grid.Columns.Add(new DataGridTextColumn { Header = "Дата и время", Binding = new System.Windows.Data.Binding("Date"), Width = 155 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Организация", Binding = new System.Windows.Data.Binding("Organization"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Точка", Binding = new System.Windows.Data.Binding("Point"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Источник", Binding = new System.Windows.Data.Binding("Source"), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Операция", Binding = new System.Windows.Data.Binding("Type"), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Сумма", Binding = new System.Windows.Data.Binding("Amount") { StringFormat = "N2" }, Width = 115 });

        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "Копировать строку" };
        copy.Click += (_, _) =>
        {
            if (grid.SelectedItem is null) return;
            var date = grid.SelectedItem.GetType().GetProperty("Date")?.GetValue(grid.SelectedItem);
            var organization = grid.SelectedItem.GetType().GetProperty("Organization")?.GetValue(grid.SelectedItem);
            var point = grid.SelectedItem.GetType().GetProperty("Point")?.GetValue(grid.SelectedItem);
            var type = grid.SelectedItem.GetType().GetProperty("Type")?.GetValue(grid.SelectedItem);
            var amount = grid.SelectedItem.GetType().GetProperty("Amount")?.GetValue(grid.SelectedItem);
            Clipboard.SetText($"{date}\t{organization}\t{point}\t{type}\t{amount}");
        };
        menu.Items.Add(copy);
        grid.ContextMenu = menu;

        Grid.SetRow(grid, 1);
        root.Children.Add(grid);
        return root;
    }
}

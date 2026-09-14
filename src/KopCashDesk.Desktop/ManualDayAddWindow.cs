using KopCashDesk.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KopCashDesk.Desktop;

public sealed class ManualDayAddWindow : Window
{
    private readonly IReadOnlyList<Organization> _organizations;
    private readonly IReadOnlyList<Location> _locations;
    private readonly ComboBox _organizationBox = new();
    private readonly ComboBox _locationBox = new();
    private readonly DatePicker _datePicker = new();
    private readonly TextBox _amountBox = new();
    private readonly CultureInfo _ru = CultureInfo.GetCultureInfo("ru-RU");

    public Guid OrganizationId { get; private set; }
    public Guid LocationId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }

    public ManualDayAddWindow(
        IReadOnlyList<Organization> organizations,
        IReadOnlyList<Location> locations,
        Guid? preferredOrganizationId,
        Guid? preferredLocationId,
        DateOnly? preferredDate = null)
    {
        _organizations = organizations;
        _locations = locations;

        Title = "Добавить день вручную";
        Width = 520;
        Height = 390;
        MinWidth = 500;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new Grid { Margin = new Thickness(22) };
        for (var i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = "Добавить день без загрузки кассового отчёта",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        var hint = new TextBlock
        {
            Text = "Укажите дату, точку и сумму «Касса безнал». Никакой отчёт Taxcom/Frontol не импортируется — сохранится только ручная сумма.",
            Margin = new Thickness(0, 6, 0, 18),
            Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(hint, 1);
        root.Children.Add(hint);

        var orgPanel = LabeledRow("Организация:", _organizationBox);
        Grid.SetRow(orgPanel, 2);
        root.Children.Add(orgPanel);

        _organizationBox.DisplayMemberPath = nameof(Organization.Name);
        _organizationBox.ItemsSource = _organizations;
        _organizationBox.SelectionChanged += (_, _) => RefreshLocations(preferredLocationId);
        _organizationBox.SelectedItem = preferredOrganizationId is Guid orgId
            ? _organizations.FirstOrDefault(x => x.Id == orgId)
            : _organizations.FirstOrDefault();

        var locationPanel = LabeledRow("Точка:", _locationBox);
        Grid.SetRow(locationPanel, 3);
        root.Children.Add(locationPanel);
        _locationBox.DisplayMemberPath = nameof(Location.Name);

        var entryGrid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        entryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        entryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var datePanel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        datePanel.Children.Add(new TextBlock { Text = "Дата", Margin = new Thickness(0, 0, 0, 5) });
        _datePicker.SelectedDate = (preferredDate ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue);
        _datePicker.FontSize = 16;
        datePanel.Children.Add(_datePicker);
        entryGrid.Children.Add(datePanel);

        var amountPanel = new StackPanel();
        amountPanel.Children.Add(new TextBlock { Text = "Касса безнал, ₽", Margin = new Thickness(0, 0, 0, 5) });
        _amountBox.FontSize = 17;
        _amountBox.ToolTip = "Введите сумму безнала на кассе. Можно использовать запятую или точку.";
        _amountBox.KeyDown += AmountBox_KeyDown;
        amountPanel.Children.Add(_amountBox);
        Grid.SetColumn(amountPanel, 1);
        entryGrid.Children.Add(amountPanel);

        Grid.SetRow(entryGrid, 4);
        root.Children.Add(entryGrid);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var cancel = new Button { Content = "Отмена", MinWidth = 95, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(cancel);

        var save = new Button { Content = "Добавить день", MinWidth = 120, IsDefault = true };
        save.Click += (_, _) => Save();
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 6);
        root.Children.Add(buttons);

        Content = root;
        RefreshLocations(preferredLocationId);

        Loaded += (_, _) =>
        {
            _amountBox.Focus();
            _amountBox.SelectAll();
        };
    }

    private static Grid LabeledRow(string label, Control control)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        control.MinWidth = 280;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private void RefreshLocations(Guid? preferredLocationId)
    {
        if (_organizationBox.SelectedItem is not Organization organization)
        {
            _locationBox.ItemsSource = Array.Empty<Location>();
            _locationBox.SelectedItem = null;
            return;
        }

        var locations = _locations
            .Where(x => x.OrganizationId == organization.Id && x.IsActive)
            .OrderBy(x => x.Name)
            .ToArray();
        _locationBox.ItemsSource = locations;
        _locationBox.SelectedItem = preferredLocationId is Guid locationId
            ? locations.FirstOrDefault(x => x.Id == locationId) ?? locations.FirstOrDefault()
            : locations.FirstOrDefault();
    }

    private void AmountBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Save();
        e.Handled = true;
    }

    private void Save()
    {
        if (_organizationBox.SelectedItem is not Organization organization ||
            _locationBox.SelectedItem is not Location location)
        {
            MessageBox.Show(this, "Выберите организацию и точку.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_datePicker.SelectedDate is not DateTime selectedDate)
        {
            MessageBox.Show(this, "Выберите дату.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var text = (_amountBox.Text ?? string.Empty)
            .Replace("\u00A0", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (!decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, _ru, out var value) &&
            !decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
        {
            MessageBox.Show(this, "Сумма введена неверно.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Warning);
            _amountBox.Focus();
            _amountBox.SelectAll();
            return;
        }

        OrganizationId = organization.Id;
        LocationId = location.Id;
        Date = DateOnly.FromDateTime(selectedDate);
        Amount = Money.Normalize(value);
        DialogResult = true;
    }
}

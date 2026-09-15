using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KopCashDesk.Desktop;

public partial class MainWindow : Window
{
    private readonly Database _db;
    private string _page = "home";
    private IReadOnlyList<Organization> _organizations = [];
    private IReadOnlyList<Location> _locations = [];
    private readonly IntegrationService _integrations;
    private readonly string _dataDirectory;

    public MainWindow()
    {
        InitializeComponent();
        _dataDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KopCashDesk");
        _db = new Database(System.IO.Path.Combine(_dataDirectory, "cashdesk.db"));
        _db.Initialize();
        _integrations = new IntegrationService(_db, _dataDirectory);
        DatabaseText.Text = _db.Path;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Help, (_, _) => Navigate("help")));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Help, Key.F1, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RoutedCommand("Refresh", typeof(MainWindow)), Key.F5, ModifierKeys.None));
        RefreshAll();
        Navigate("home");
    }

    private Guid? SelectedOrganizationId => (OrganizationFilter.SelectedItem as Organization)?.Id;

    private void RefreshAll()
    {
        var previous = SelectedOrganizationId;
        _organizations = _db.Organizations();
        _locations = _db.Locations();
        OrganizationFilter.ItemsSource = _organizations;
        OrganizationFilter.SelectedItem = _organizations.FirstOrDefault(x => x.Id == previous) ?? _organizations.FirstOrDefault();
        Render();
    }

    private void Navigate(string page)
    {
        _page = page;
        SearchBox.Text = "";
        Render();
    }

    private void Render()
    {
        if (PageContent is null) return;
        PrimaryButton.Visibility = Visibility.Collapsed;
        SearchBox.IsEnabled = _page is "organizations" or "locations" or "integrations";
        PageContent.Content = _page switch
        {
            "organizations" => RenderOrganizations(),
            "locations" => RenderLocations(),
            "integrations" => RenderIntegrations(),
            "operations" => RenderOperations(),
            "registers" => RenderRegisters(),
            "summary" => RenderSummaryV051(),
            "terminals" => RenderTerminals(),
            "reconciliation" => RenderReconciliation(),
            "settings" => RenderSettings(),
            "journal" => RenderJournal(),
            "help" => RenderHelp(),
            _ => RenderHome()
        };
    }

    private static TextBlock Text(string value, int size = 14, bool bold = false) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10)
    };

    private static StackPanel Stack(params UIElement[] elements)
    {
        var panel = new StackPanel();
        foreach (var element in elements) panel.Children.Add(element);
        return panel;
    }

    private static Button Action(string title, RoutedEventHandler handler)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += handler;
        return button;
    }

    private UIElement RenderHome()
    {
        PageTitle.Text = "Рабочий стол";
        PageSubtitle.Text = "Обзор состояния системы";
        return Stack(
            Text("КОП Кассы", 23, true),
            Text($"Организаций: {_organizations.Count}    •    Торговых точек: {_locations.Count}    •    Операций: {_db.CountOperations()}"),
            Action("Импортировать архивы Сбер...", SberImport_Click),
            Action("Добавить организацию", (_, _) => EditOrganization(null)),
            Action("Настроить интеграции", (_, _) => Navigate("integrations")));
    }

    private UIElement RenderEmpty(string title, string message)
    {
        PageTitle.Text = title;
        PageSubtitle.Text = "Данные отсутствуют";
        return Stack(Text("Пока нет данных", 22, true), Text(message));
    }

    private UIElement RenderOrganizations()
    {
        PageTitle.Text = "Организации";
        PageSubtitle.Text = "Юридические лица и их реквизиты";
        PrimaryButton.Content = "+ Организация";
        PrimaryButton.Visibility = Visibility.Visible;
        var grid = new DataGrid { ItemsSource = _organizations.Where(x => Matches(x.Name, x.TaxId)).ToArray(), IsReadOnly = true };
        grid.Columns.Add(new DataGridTextColumn { Header = "Наименование", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "ИНН", Binding = new System.Windows.Data.Binding("TaxId"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Изменить..." };
        edit.Click += (_, _) => { if (grid.SelectedItem is Organization organization) EditOrganization(organization); };
        menu.Items.Add(edit);
        grid.ContextMenu = menu;
        grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is Organization organization) EditOrganization(organization); };
        return grid;
    }

    private UIElement RenderLocations()
    {
        PageTitle.Text = "Торговые точки";
        PageSubtitle.Text = "Справочник адресов и принадлежности организаций";
        PrimaryButton.Content = "+ Торговая точка";
        PrimaryButton.Visibility = Visibility.Visible;
        var rows = _locations.Where(x => (SelectedOrganizationId is null || x.OrganizationId == SelectedOrganizationId) && Matches(x.Name, x.Address)).ToArray();
        var grid = new DataGrid { ItemsSource = rows, IsReadOnly = true };
        grid.Columns.Add(new DataGridTextColumn { Header = "Точка", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Адрес", Binding = new System.Windows.Data.Binding("Address"), Width = new DataGridLength(3, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Исключена", Binding = new System.Windows.Data.Binding("IsExcluded"), Width = 100 });
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Изменить..." };
        edit.Click += (_, _) => { if (grid.SelectedItem is Location location) EditLocation(location); };
        menu.Items.Add(edit);
        grid.ContextMenu = menu;
        grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is Location location) EditLocation(location); };
        return grid;
    }

    private bool Matches(params string[] values) => string.IsNullOrWhiteSpace(SearchBox.Text) || values.Any(x => x.Contains(SearchBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));

    private void EditOrganization(Organization? original)
    {
        var name = new TextBox { Text = original?.Name ?? "" };
        var tax = new TextBox { Text = original?.TaxId ?? "" };
        var content = Stack(Text("Наименование организации"), name, Text("ИНН"), tax);
        if (!Dialog("Организация", content, () => !string.IsNullOrWhiteSpace(name.Text))) return;
        var organization = new Organization(original?.Id ?? Guid.NewGuid(), name.Text.Trim(), tax.Text.Trim());
        _db.Save(organization);
        _db.Audit("organization.save", organization.Id.ToString());
        RefreshAll();
        StatusText.Text = "Организация сохранена";
    }

    private void EditLocation(Location? original)
    {
        if (_organizations.Count == 0)
        {
            MessageBox.Show("Сначала добавьте организацию.", "КОП Кассы");
            return;
        }
        var organization = new ComboBox
        {
            ItemsSource = _organizations,
            DisplayMemberPath = "Name",
            SelectedItem = _organizations.FirstOrDefault(x => x.Id == original?.OrganizationId) ?? _organizations.FirstOrDefault(x => x.Id == SelectedOrganizationId) ?? _organizations[0]
        };
        var name = new TextBox { Text = original?.Name ?? "" };
        var address = new TextBox { Text = original?.Address ?? "" };
        var excluded = new CheckBox { Content = "Исключить из автоматической сверки", IsChecked = original?.IsExcluded ?? false, Margin = new Thickness(2, 10, 2, 6) };
        var content = Stack(Text("Организация"), organization, Text("Название точки"), name, Text("Адрес"), address, excluded);
        if (!Dialog("Торговая точка", content, () => !string.IsNullOrWhiteSpace(name.Text))) return;
        var location = new Location(original?.Id ?? Guid.NewGuid(), ((Organization)organization.SelectedItem).Id, name.Text.Trim(), address.Text.Trim(), excluded.IsChecked == true);
        _db.Save(location);
        _db.Audit("location.save", location.Id.ToString());
        RefreshAll();
        StatusText.Text = "Торговая точка сохранена";
    }

    private bool Dialog(string title, UIElement content, Func<bool> validate)
    {
        var window = new Window
        {
            Title = title,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            MinHeight = 220,
            MaxHeight = 750,
            ResizeMode = ResizeMode.CanResize,
            Background = Brushes.White
        };
        var root = new DockPanel { Margin = new Thickness(22) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = Action("Отмена", (_, _) => window.DialogResult = false);
        var save = Action("Сохранить", (_, _) =>
        {
            if (!validate())
            {
                MessageBox.Show(window, "Заполните обязательные поля.", "Проверка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            window.DialogResult = true;
        });
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.Content = root;
        return window.ShowDialog() == true;
    }

    private UIElement RenderIntegrations()
    {
        PageTitle.Text = "Интеграции";
        PageSubtitle.Text = "Подключения ОФД и почтовых источников";
        PrimaryButton.Content = "+ Подключение";
        PrimaryButton.Visibility = Visibility.Visible;
        var rows = _db.Integrations().Where(x => SelectedOrganizationId is null || x.OrganizationId == SelectedOrganizationId).Where(x => Matches(x.Name, x.Kind.ToString())).ToArray();
        var grid = new DataGrid { ItemsSource = rows, IsReadOnly = true };
        grid.Columns.Add(new DataGridTextColumn { Header = "Наименование", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Провайдер", Binding = new System.Windows.Data.Binding("Kind"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Включено", Binding = new System.Windows.Data.Binding("Enabled"), Width = 100 });
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Параметры..." };
        edit.Click += (_, _) => { if (grid.SelectedItem is IntegrationProfile profile) EditIntegration(profile); };
        menu.Items.Add(edit);
        var check = new MenuItem { Header = "Проверить соединение" };
        check.Click += async (_, _) => { if (grid.SelectedItem is IntegrationProfile profile) await CheckIntegration(profile); };
        menu.Items.Add(check);
        grid.ContextMenu = menu;
        grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is IntegrationProfile profile) EditIntegration(profile); };
        return grid;
    }

    private void EditIntegration(IntegrationProfile? original)
    {
        if (_organizations.Count == 0)
        {
            MessageBox.Show("Сначала добавьте организацию.");
            return;
        }
        var organization = new ComboBox { ItemsSource = _organizations, DisplayMemberPath = "Name", SelectedItem = _organizations.FirstOrDefault(x => x.Id == original?.OrganizationId) ?? _organizations[0] };
        var kind = new ComboBox { ItemsSource = Enum.GetValues<IntegrationKind>(), SelectedItem = original?.Kind ?? IntegrationKind.Taxcom };
        var name = new TextBox { Text = original?.Name ?? "" };
        var settings = new TextBox { Text = original?.SettingsJson ?? "{}", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas") };
        var enabled = new CheckBox { Content = "Включить подключение", IsChecked = original?.Enabled ?? false };
        var content = Stack(Text("Организация"), organization, Text("Провайдер"), kind, Text("Название подключения"), name, Text("Параметры"), settings, enabled);
        if (!Dialog("Параметры подключения", content, () => !string.IsNullOrWhiteSpace(name.Text) && IntegrationService.IsSafeSettings(settings.Text))) return;
        var profile = new IntegrationProfile(original?.Id ?? Guid.NewGuid(), ((Organization)organization.SelectedItem).Id, (IntegrationKind)kind.SelectedItem, name.Text.Trim(), settings.Text, enabled.IsChecked == true);
        _db.Save(profile);
        _db.Audit("integration.save", profile.Id.ToString());
        RefreshAll();
    }

    private async Task CheckIntegration(IntegrationProfile profile)
    {
        StatusText.Text = "Проверка подключения...";
        var result = await _integrations.CheckAsync(profile);
        StatusText.Text = result.Message;
        MessageBox.Show(this, result.Message, "Проверка подключения", MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private UIElement RenderSettings()
    {
        PageTitle.Text = "Настройки";
        PageSubtitle.Text = "Параметры приложения";
        return Stack(
            Text("Хранение данных", 20, true),
            Text(_db.Path),
            Action("Создать резервную копию...", Backup_Click),
            Text("Интеграции", 20, true),
            Action("Открыть настройки интеграций", (_, _) => Navigate("integrations")),
            Action("Импорт архивов Сбер...", SberImport_Click));
    }

    private UIElement RenderJournal()
    {
        PageTitle.Text = "Журнал событий";
        PageSubtitle.Text = "Последние 500 действий";
        var grid = new DataGrid { ItemsSource = _db.AuditEntries().Select(x => new { Date = x.Date, Action = x.Action, Details = x.Details }).ToArray(), IsReadOnly = true };
        grid.Columns.Add(new DataGridTextColumn { Header = "Дата и время", Binding = new System.Windows.Data.Binding("Date"), Width = 200 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Действие", Binding = new System.Windows.Data.Binding("Action"), Width = 220 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Описание", Binding = new System.Windows.Data.Binding("Details"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        return grid;
    }

    private UIElement RenderHelp()
    {
        PageTitle.Text = "Помощь";
        PageSubtitle.Text = "Краткое руководство";
        return new ScrollViewer
        {
            Content = Stack(
                Text("Архивы Сбер", 20, true),
                Text("Откройте «Сбер — импорт архивов», перетащите ZIP/XLSX отчёты и нажмите «Импортировать». Организация определяется по ИНН, варианты POS/СБП/QR одной точки объединяются, терминалы привязываются автоматически, дубли операций пропускаются."),
                Text("Контекстные меню", 20, true),
                Text("Щёлкните правой кнопкой мыши по строке. Двойной щелчок открывает редактирование там, где оно доступно. В окне импорта Delete или контекстное меню убирают выбранный файл из списка."),
                Text("Такском", 20, true),
                Text("Логин и пароль сохраняются через отдельное окно настроек. Для реального API-подключения потребуется Integrator-ID.")),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Резервная копия SQLite (*.db)|*.db",
            FileName = "KopCashDesk_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".db"
        };
        if (dialog.ShowDialog(this) != true) return;
        _db.Backup(dialog.FileName);
        _db.Audit("backup.create", "Резервная копия создана");
        StatusText.Text = "Резервная копия сохранена";
        MessageBox.Show(this, "Резервная копия успешно создана.", "Резервная копия", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Navigate_Click(object sender, RoutedEventArgs e) => Navigate((string)((Button)sender).Tag);
    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_page == "organizations") EditOrganization(null);
        else if (_page == "locations") EditLocation(null);
        else if (_page == "integrations") EditIntegration(null);
    }
    private void NewOrganization_Click(object sender, RoutedEventArgs e) => EditOrganization(null);
    private void Organizations_Click(object sender, RoutedEventArgs e) => Navigate("organizations");
    private void Locations_Click(object sender, RoutedEventArgs e) => Navigate("locations");
    private void Integrations_Click(object sender, RoutedEventArgs e) => Navigate("integrations");
    private void Settings_Click(object sender, RoutedEventArgs e) => Navigate("settings");
    private void Journal_Click(object sender, RoutedEventArgs e) => Navigate("journal");
    private void Help_Click(object sender, RoutedEventArgs e) => Navigate("help");
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "КОП Кассы 0.3.0\nC# / .NET 10 / WPF", "О программе");
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshAll();
    private void OrganizationFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Render(); }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) Render(); }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) => SearchBox.Text = "";
}

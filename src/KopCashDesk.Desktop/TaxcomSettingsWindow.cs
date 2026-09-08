using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KopCashDesk.Desktop;

public sealed class TaxcomSettingsWindow : Window
{
    private readonly Database _database;
    private readonly TaxcomCredentialStore _secrets;
    private readonly IntegrationService _service;
    private readonly ComboBox _organization = new();
    private readonly ComboBox _profiles = new();
    private readonly TextBox _name = new();
    private readonly TextBox _server = new();
    private readonly TextBox _integrator = new();
    private readonly TextBox _login = new();
    private readonly PasswordBox _password = new();
    private readonly CheckBox _enabled = new() { Content = "Включить подключение", IsChecked = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly Button _save = new() { Content = "Сохранить", MinWidth = 110 };
    private readonly Button _test = new() { Content = "Проверить", MinWidth = 110 };
    private IntegrationProfile? _current;
    private bool _loading;
    public bool Changed { get; private set; }

    public TaxcomSettingsWindow(Window owner, Database database, string dataDirectory, IReadOnlyList<Organization> organizations, Guid? selectedOrganizationId, IntegrationProfile? selected = null)
    {
        Owner = owner;
        Title = "Такском — настройки подключения";
        Width = 650; Height = 680; MinWidth = 520; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        _database = database;
        _secrets = new TaxcomCredentialStore(dataDirectory);
        _service = new IntegrationService(database, dataDirectory);
        _organization.ItemsSource = organizations;
        _organization.DisplayMemberPath = "Name";
        _organization.SelectedItem = organizations.FirstOrDefault(x => x.Id == selected?.OrganizationId)
            ?? organizations.FirstOrDefault(x => x.Id == selectedOrganizationId) ?? organizations.FirstOrDefault();
        _organization.SelectionChanged += (_, _) => { if (!_loading) RefreshProfiles(null); };
        _profiles.DisplayMemberPath = "Name";
        _profiles.SelectionChanged += (_, _) => { if (!_loading) LoadProfile(_profiles.SelectedItem as IntegrationProfile); };
        _server.Text = "https://tlk-ofd.taxcom.ru";
        _server.ToolTip = "Адрес API-сервера, выданный Такском. Не указывайте адрес постороннего сайта.";
        _integrator.ToolTip = "Идентификатор интегратора из договора или ответа технической поддержки.";
        _login.ToolTip = "Логин личного кабинета Такском Касса.";
        _password.ToolTip = "Новый пароль. При редактировании оставьте пустым, чтобы сохранить прежний.";
        _password.ContextMenu = new ContextMenu();
        var paste = new MenuItem { Header = "Вставить" }; paste.Click += (_, _) => _password.Paste();
        var clear = new MenuItem { Header = "Очистить" }; clear.Click += (_, _) => _password.Clear();
        _password.ContextMenu.Items.Add(paste); _password.ContextMenu.Items.Add(clear);
        _save.Click += (_, _) => SaveProfile();
        _test.Click += async (_, _) => await TestConnectionAsync();
        BuildLayout();
        RefreshProfiles(selected);
    }

    private void BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(24, 18, 24, 18) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var newProfile = new Button { Content = "Новое подключение" };
        newProfile.Click += (_, _) => { _profiles.SelectedItem = null; LoadProfile(null); };
        var close = new Button { Content = "Закрыть", MinWidth = 90 };
        close.Click += (_, _) => Close();
        footer.Children.Add(newProfile); footer.Children.Add(_test); footer.Children.Add(_save); footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var form = new StackPanel();
        form.Children.Add(new TextBlock { Text = "Такском", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        AddField(form, "Организация", _organization);
        AddField(form, "Подключение", _profiles);
        AddField(form, "Название", _name);
        AddField(form, "Адрес API-сервера", _server);
        AddField(form, "Integrator-ID", _integrator);
        AddField(form, "Логин", _login);
        AddField(form, "Пароль", _password);
        form.Children.Add(new TextBlock { Text = "Пароль хранится в защищённом хранилище Windows и не записывается в базу или GitHub.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(2, 5, 2, 10) });
        form.Children.Add(_enabled);
        form.Children.Add(_status);
        var help = new Button { Content = "Где получить Integrator-ID?", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        help.Click += (_, _) => MessageBox.Show(this, "Integrator-ID выдаёт Такском для доступа к API. Логин и пароль личного кабинета сами по себе не гарантируют доступ к API. Уточните у поддержки разрешённый сервер и идентификатор интегратора.", "Помощь", MessageBoxButton.OK, MessageBoxImage.Information);
        form.Children.Add(help);
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    private static void AddField(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.Medium, Margin = new Thickness(2, 8, 2, 4) });
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.Children.Add(control);
    }

    private void RefreshProfiles(IntegrationProfile? selected)
    {
        _loading = true;
        var organizationId = (_organization.SelectedItem as Organization)?.Id;
        var profiles = _database.Integrations().Where(x => x.Kind == IntegrationKind.Taxcom && x.OrganizationId == organizationId).ToArray();
        _profiles.ItemsSource = profiles;
        _profiles.SelectedItem = profiles.FirstOrDefault(x => x.Id == selected?.Id);
        _loading = false;
        LoadProfile(_profiles.SelectedItem as IntegrationProfile);
    }

    private void LoadProfile(IntegrationProfile? profile)
    {
        _current = profile;
        _name.Text = profile?.Name ?? "Такском";
        _server.Text = "https://tlk-ofd.taxcom.ru";
        _integrator.Clear(); _login.Clear(); _password.Clear();
        _enabled.IsChecked = profile?.Enabled ?? true;
        if (profile is not null)
        {
            try
            {
                using var settings = JsonDocument.Parse(profile.SettingsJson);
                if (settings.RootElement.TryGetProperty("server", out var server) && server.ValueKind == JsonValueKind.String) _server.Text = server.GetString() ?? "";
                if (settings.RootElement.TryGetProperty("integratorId", out var id) && id.ValueKind == JsonValueKind.String) _integrator.Text = id.GetString() ?? "";
                var credentials = _secrets.Load(profile.Id);
                _login.Text = credentials?.Login ?? "";
                _status.Text = credentials is null ? "Пароль не сохранён." : "Пароль сохранён. Для замены введите новый.";
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or JsonException)
            {
                _status.Text = "Не удалось открыть сохранённые параметры. Проверьте хранилище Windows.";
            }
        }
        else _status.Text = "Введите параметры нового подключения.";
    }

    private IntegrationProfile? SaveProfile()
    {
        if (_organization.SelectedItem is not Organization organization || string.IsNullOrWhiteSpace(_name.Text))
        {
            MessageBox.Show(this, "Выберите организацию и укажите название.", "Проверка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        TaxcomCredentials? previous = null;
        if (_current is not null)
        {
            try { previous = _secrets.Load(_current.Id); }
            catch (Exception ex) when (ex is CryptographicException or IOException or JsonException)
            {
                MessageBox.Show(this, "Не удалось прочитать прежний пароль. Введите новый пароль или восстановите доступ к хранилищу Windows.", "Проверка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }
        var login = _login.Text.Trim();
        var password = _password.Password;
        if (string.IsNullOrWhiteSpace(login) || (string.IsNullOrEmpty(password) && (previous is null || previous.Login != login)))
        {
            MessageBox.Show(this, "Укажите логин и пароль. Если логин изменён, необходимо ввести новый пароль.", "Проверка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        if (string.IsNullOrEmpty(password)) password = previous!.Password;
        if (!string.IsNullOrWhiteSpace(_server.Text) && !TaxcomConnectionSettings.TryNormalizeServer(_server.Text, out _))
        {
            MessageBox.Show(this, "Адрес сервера должен быть HTTPS-адресом Такском без параметров и логина.", "Проверка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        var profile = new IntegrationProfile(_current?.Id ?? Guid.NewGuid(), organization.Id, IntegrationKind.Taxcom,
            _name.Text.Trim(), JsonSerializer.Serialize(new { server = _server.Text.Trim().TrimEnd('/'), integratorId = _integrator.Text.Trim() }), _enabled.IsChecked == true);
        try
        {
            _secrets.Save(profile.Id, new TaxcomCredentials(login, password));
            _database.Save(profile);
            _database.Audit("integration.save", profile.Id.ToString());
            _current = profile; Changed = true;
            _password.Clear();
            RefreshProfiles(profile);
            _status.Text = "Параметры и пароль сохранены.";
            return profile;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            MessageBox.Show(this, "Не удалось сохранить подключение. " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    private async Task TestConnectionAsync()
    {
        var profile = SaveProfile();
        if (profile is null) return;
        _save.IsEnabled = false; _test.IsEnabled = false;
        _status.Text = "Проверка авторизации...";
        try
        {
            var result = await _service.CheckAsync(profile);
            _status.Text = result.Message;
            MessageBox.Show(this, result.Message, "Такском", MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally { _save.IsEnabled = true; _test.IsEnabled = true; }
    }
}

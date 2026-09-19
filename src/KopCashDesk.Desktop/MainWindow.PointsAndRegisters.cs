using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private sealed record PointRegisterRow(
        Location? Location,
        RegisterBinding? Binding,
        string Point,
        string Address,
        string Register,
        string Serial,
        string Rnm,
        string Fn,
        string Organization,
        string Status,
        string Period);

    private void Registers_Click(object sender, RoutedEventArgs e) => Navigate("registers");

    private UIElement RenderRegisters()
    {
        PageTitle.Text = "Точки и ККТ";
        PageSubtitle.Text = "Адреса торговых точек, заводские номера ККТ, РНМ и ФН";

        var root = new DockPanel();
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var editPoint = new Button { Content = "Изменить точку…", Padding = new Thickness(10, 4, 10, 4), MinHeight = 28 };
        var addRegister = new Button { Content = "+ ККТ", Padding = new Thickness(10, 4, 10, 4), MinHeight = 28 };
        var bindRegister = new Button { Content = "Назначить ККТ / период…", Padding = new Thickness(10, 4, 10, 4), MinHeight = 28 };
        var pending = new Button { Content = "Непривязанные смены…", Padding = new Thickness(10, 4, 10, 4), MinHeight = 28 };
        var history = new CheckBox { Content = "Архив привязок", Margin = new Thickness(12, 6, 0, 6), VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(editPoint);
        tools.Children.Add(addRegister);
        tools.Children.Add(bindRegister);
        tools.Children.Add(pending);
        tools.Children.Add(history);
        DockPanel.SetDock(tools, Dock.Top);
        root.Children.Add(tools);

        var reviews = _db.RegisterRepairReviews(SelectedOrganizationId);
        if (reviews.Count > 0)
        {
            var notice = Text("Есть записи, которым нужна ручная проверка истории. Нажмите «Непривязанные смены…» или откройте строку ККТ.", 13);
            notice.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(notice, Dock.Top);
            root.Children.Add(notice);
        }

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            FrozenColumnCount = 2
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Торговая точка", Binding = new Binding(nameof(PointRegisterRow.Point)), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Адрес", Binding = new Binding(nameof(PointRegisterRow.Address)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Название ККТ", Binding = new Binding(nameof(PointRegisterRow.Register)), Width = 155 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Зав. № ККТ", Binding = new Binding(nameof(PointRegisterRow.Serial)), Width = 145 });
        grid.Columns.Add(new DataGridTextColumn { Header = "РНМ", Binding = new Binding(nameof(PointRegisterRow.Rnm)), Width = 145 });
        grid.Columns.Add(new DataGridTextColumn { Header = "ФН", Binding = new Binding(nameof(PointRegisterRow.Fn)), Width = 145 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Организация", Binding = new Binding(nameof(PointRegisterRow.Organization)), Width = 125 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Статус", Binding = new Binding(nameof(PointRegisterRow.Status)), Width = 160 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Период", Binding = new Binding(nameof(PointRegisterRow.Period)), Width = 180 });

        void Load()
        {
            var orgs = _db.Organizations();
            var locations = _db.Locations(includeInactive: true)
                .Where(x => x.IsActive && (SelectedOrganizationId is null || x.OrganizationId == SelectedOrganizationId))
                .OrderBy(x => x.Name)
                .ToArray();
            var bindings = RegisterBindingService.Read(_db, history.IsChecked == true)
                .Where(x => SelectedOrganizationId is null || x.OrganizationId == SelectedOrganizationId)
                .ToArray();

            var rows = new List<PointRegisterRow>();
            foreach (var location in locations)
            {
                var pointBindings = bindings.Where(x => x.LocationId == location.Id).ToArray();
                if (pointBindings.Length == 0)
                {
                    rows.Add(new PointRegisterRow(
                        location, null, location.Name, string.IsNullOrWhiteSpace(location.Address) ? "—" : location.Address,
                        "—", "—", "—", "—",
                        orgs.FirstOrDefault(x => x.Id == location.OrganizationId)?.Name ?? "",
                        "Точка без ККТ", "—"));
                    continue;
                }

                foreach (var binding in pointBindings)
                    rows.Add(ToRow(location, binding, orgs));
            }

            foreach (var binding in bindings.Where(x => x.LocationId is null))
                rows.Add(ToRow(null, binding, orgs));

            grid.ItemsSource = rows
                .OrderBy(x => x.Organization)
                .ThenBy(x => x.Point)
                .ThenBy(x => x.Register)
                .ToArray();
        }

        PointRegisterRow ToRow(Location? location, RegisterBinding binding, IReadOnlyList<Organization> orgs) => new(
            location,
            binding,
            location?.Name ?? "ККТ не привязана",
            location is null || string.IsNullOrWhiteSpace(location.Address) ? "—" : location.Address,
            string.IsNullOrWhiteSpace(binding.DisplayName) ? "—" : binding.DisplayName,
            string.IsNullOrWhiteSpace(binding.KktSerial) ? "—" : binding.KktSerial,
            string.IsNullOrWhiteSpace(binding.RegisterNumber) ? "—" : binding.RegisterNumber,
            string.IsNullOrWhiteSpace(binding.FiscalDriveNumber) ? "—" : binding.FiscalDriveNumber,
            orgs.FirstOrDefault(x => x.Id == binding.OrganizationId)?.Name ?? "",
            !binding.IsActive ? "Архив" : binding.LocationId is null ? "Требуется привязка" : binding.IsLocked ? "Закреплена вручную" : "Привязана",
            $"{(binding.ValidFrom is null ? "без начала" : binding.ValidFrom.Value.ToString("dd.MM.yyyy"))} — {(binding.ValidTo is null ? "без окончания" : binding.ValidTo.Value.ToString("dd.MM.yyyy"))}");

        void EditSelectedPoint()
        {
            if (grid.SelectedItem is PointRegisterRow { Location: not null } row)
                EditLocation(row.Location);
            else
                MessageBox.Show(this, "У этой ККТ пока нет торговой точки.", "Контроль выручки", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void EditSelectedRegister()
        {
            if (grid.SelectedItem is PointRegisterRow { Binding: not null } row && row.Binding.IsActive)
                EditRegister(row.Binding);
        }

        editPoint.Click += (_, _) => EditSelectedPoint();
        addRegister.Click += (_, _) => EditRegister(null);
        bindRegister.Click += (_, _) => EditSelectedRegister();
        history.Checked += (_, _) => Load();
        history.Unchecked += (_, _) => Load();
        pending.Click += (_, _) =>
        {
            if (SelectedOrganizationId is Guid org)
                ShowShiftDetails(org, null, null, "Непривязанные смены — суммы сохранены, но пока не относятся к точке");
        };
        grid.MouseDoubleClick += (_, _) =>
        {
            if (grid.SelectedItem is PointRegisterRow { Binding: not null } row && row.Binding.IsActive) EditRegister(row.Binding);
            else EditSelectedPoint();
        };

        var menu = new ContextMenu();
        var pointMenu = new MenuItem { Header = "Изменить торговую точку…" };
        pointMenu.Click += (_, _) => EditSelectedPoint();
        var registerMenu = new MenuItem { Header = "Назначить ККТ / период…" };
        registerMenu.Click += (_, _) => EditSelectedRegister();
        menu.Items.Add(pointMenu);
        menu.Items.Add(registerMenu);
        grid.ContextMenu = menu;

        root.Children.Add(grid);
        Load();
        return root;
    }

    private void EditRegister(RegisterBinding? original)
    {
        var org = original?.OrganizationId ?? SelectedOrganizationId;
        if (org is null) { MessageBox.Show(this, "Выберите организацию."); return; }
        var points = _db.Locations().Where(x => x.OrganizationId == org).OrderBy(x => x.Name).ToArray();
        var point = new ComboBox { ItemsSource = points, DisplayMemberPath = "Name", SelectedItem = points.FirstOrDefault(x => x.Id == original?.LocationId) };
        var name = new TextBox { Text = original?.DisplayName ?? "" };
        var serial = new TextBox { Text = original?.KktSerial ?? "" };
        var fn = new TextBox { Text = original?.FiscalDriveNumber ?? "" };
        var rnm = new TextBox { Text = original?.RegisterNumber ?? "" };
        serial.IsReadOnly = !string.IsNullOrEmpty(original?.KktSerial);
        fn.IsReadOnly = !string.IsNullOrEmpty(original?.FiscalDriveNumber);
        rnm.IsReadOnly = !string.IsNullOrEmpty(original?.RegisterNumber);
        var from = new DatePicker { SelectedDate = original?.ValidFrom?.ToDateTime(TimeOnly.MinValue) };
        var to = new DatePicker { SelectedDate = original?.ValidTo?.ToDateTime(TimeOnly.MinValue) };
        var all = new CheckBox { Content = "Исправить ошибочную точку за весь период (это не переезд)", Margin = new Thickness(0, 8, 0, 8) };
        var content = Stack(Text("Название ККТ"), name, Text("Заводской номер ККТ"), serial, Text("РНМ"), rnm, Text("ФН"), fn,
            Text("Физическая торговая точка"), point, Text("Действует с (при переезде обязательно)"), from, Text("По включительно (пусто — без окончания)"), to, all,
            Text("При переезде прежние даты останутся на старой точке. Банковская история не переносится автоматически."));
        if (!Dialog("ККТ — точка и период", content, () => point.SelectedItem is Location &&
            (!string.IsNullOrWhiteSpace(serial.Text) || !string.IsNullOrWhiteSpace(fn.Text) || !string.IsNullOrWhiteSpace(rnm.Text)) &&
            (original?.LocationId is null || original.LocationId == ((Location)point.SelectedItem).Id || from.SelectedDate is not null || all.IsChecked == true))) return;
        if (point.SelectedItem is not Location selectedPoint) return;
        var binding = (original ?? new RegisterBinding(Guid.NewGuid(), org.Value, null, "")) with
        {
            DisplayName = name.Text.Trim(),
            KktSerial = serial.Text.Trim(),
            FiscalDriveNumber = fn.Text.Trim(),
            RegisterNumber = rnm.Text.Trim()
        };
        try
        {
            RegisterBindingService.Assign(_db, binding, selectedPoint.Id,
                from.SelectedDate is DateTime f ? DateOnly.FromDateTime(f) : null,
                to.SelectedDate is DateTime t ? DateOnly.FromDateTime(t) : null);
            RefreshAll();
            Navigate("registers");
            StatusText.Text = "Привязка ККТ сохранена вручную и защищена от автоматического изменения";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Привязка не сохранена", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowDayDetails(DaySummaryRow row)
        => ShowShiftDetails(row.OrganizationId, row.LocationId, row.DateValue, $"{row.Point} — {row.DateValue:dd.MM.yyyy}");

    private void ShowShiftDetails(Guid org, Guid? location, DateOnly? day, string title)
    {
        var rows = _db.ShiftDetails(org, location, day);
        var root = new DockPanel { Margin = new Thickness(12) };
        var summary = day is DateOnly d && location is Guid l
            ? _db.CanonicalPointDaySummaries(org, d.Year, d.Month, l).FirstOrDefault(x => x.Date == d)
            : null;

        string text;
        if (summary is null)
        {
            text = title;
        }
        else if (summary.HasSourceConflict)
        {
            var taxcom = rows.Where(x => x.Source.Contains("Taxcom", StringComparison.OrdinalIgnoreCase)).ToArray();
            var frontol = rows.Where(x => x.Source.Contains("Frontol", StringComparison.OrdinalIgnoreCase)).ToArray();
            text = title + "\n\n" +
                   "Почему конфликт: Taxcom и Frontol нашли смены одной точки примерно в одно время, но суммы в источниках различаются. " +
                   "Программа специально не выбирает одну из них сама.\n" +
                   $"Taxcom: выручка {taxcom.Sum(x => x.Total):N2} ₽; наличные {taxcom.Sum(x => x.Cash):N2} ₽; безнал {taxcom.Sum(x => x.Electronic):N2} ₽.\n" +
                   $"Frontol: выручка {frontol.Sum(x => x.Total):N2} ₽; наличные {frontol.Sum(x => x.Cash):N2} ₽; безнал {frontol.Sum(x => x.Electronic):N2} ₽.\n" +
                   $"Разница безнала: {taxcom.Sum(x => x.Electronic) - frontol.Sum(x => x.Electronic):N2} ₽. Ниже показаны обе исходные записи.";
        }
        else
        {
            text = $"{title}\nКасса безнал: {(summary.FiscalElectronic is decimal amount ? amount.ToString("N2") + " ₽" : "нет данных")}\n" +
                   $"Учтённые смены: {rows.Where(x => x.Included).Sum(x => x.Electronic):N2} ₽ безнал. Подтверждения повторно не складываются.";
        }

        if (summary is not null && day is DateOnly businessDay && _db.ManualCashPostings(org, businessDay.Year, businessDay.Month, location).Any(x => x.Date == businessDay))
            text += "\nИтог кассы задан вручную; исходные суммы смен показаны без изменения.";

        var header = Text(text, 15, true);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var grid = new DataGrid { ItemsSource = rows, IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false };
        foreach (var col in new[] { ("ККТ", "Register", 180), ("Зав. №", "KktSerial", 135), ("ФН", "Fn", 145), ("РНМ", "Rnm", 145), ("Смена", "ShiftNumber", 60), ("Закрытие", "ClosedAt", 175), ("Выручка", "Total", 105), ("Наличные", "Cash", 100), ("Безналичные", "Electronic", 110), ("Источник", "Source", 150), ("Учёт", "Status", 250), ("Документ", "Document", 200) })
            grid.Columns.Add(new DataGridTextColumn { Header = col.Item1, Binding = new Binding(col.Item2) { StringFormat = col.Item2 is "Total" or "Cash" or "Electronic" ? "N2" : col.Item2 == "ClosedAt" ? "dd.MM.yyyy HH:mm:ss" : null }, Width = col.Item3 });

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Смены и подтверждения", Content = grid });
        var operations = new DataGrid { ItemsSource = _db.DayOperations(org, location, day), IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false };
        foreach (var col in new[] { ("Дата", "Date", 160), ("Источник", "Source", 145), ("ККТ", "Register", 170), ("ФН", "Fn", 145), ("Зав. №", "Serial", 135), ("РНМ", "Rnm", 135), ("Смена", "Shift", 60), ("Операция", "Kind", 85), ("Оплата", "Payment", 85), ("Сумма", "Amount", 110), ("Учёт", "Status", 280), ("Документ", "Document", 210) })
            operations.Columns.Add(new DataGridTextColumn { Header = col.Item1, Binding = new Binding(col.Item2) { StringFormat = col.Item2 == "Amount" ? "N2" : col.Item2 == "Date" ? "dd.MM.yyyy HH:mm:ss" : null }, Width = col.Item3 });
        tabs.Items.Add(new TabItem { Header = "Исходные операции (касса и банк)", Content = operations });
        root.Children.Add(tabs);

        new Window
        {
            Owner = this,
            Title = summary?.HasSourceConflict == true ? "Объяснение конфликта" : title,
            Content = root,
            Width = 1220,
            Height = 680,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        }.ShowDialog();
    }
}

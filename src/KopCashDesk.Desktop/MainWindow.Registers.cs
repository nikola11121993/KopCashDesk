using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private sealed record RegisterRow(RegisterBinding Binding, string Name, string Serial, string Fn, string Rnm, string Organization, string Point, string Source, string Status, string Period);
    private void Registers_Click(object sender, RoutedEventArgs e) => Navigate("registers");
    private UIElement RenderRegisters()
    {
        PageTitle.Text = "ККТ / Кассы";
        PageSubtitle.Text = "Одна физическая точка может иметь несколько ККТ. Переезд задаётся отдельным периодом.";
        var root = new DockPanel();
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        var add = new Button { Content = "+ ККТ" }; var edit = new Button { Content = "Назначить точку / период…" };
        var history = new CheckBox { Content = "Показать архив изменений", Margin = new Thickness(12, 8, 0, 8) };
        tools.Children.Add(add); tools.Children.Add(edit); tools.Children.Add(history);
        var pending = new Button { Content = "Непривязанные смены…" }; tools.Children.Add(pending);
        DockPanel.SetDock(tools, Dock.Top); root.Children.Add(tools);
        var reviews = _db.RegisterRepairReviews(SelectedOrganizationId);
        if (reviews.Count > 0)
        {
            var notice = Text("Нужна проверка истории:\n" + string.Join("\n", reviews), 13);
            DockPanel.SetDock(notice, Dock.Top); root.Children.Add(notice);
        }
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false };
        foreach (var column in new[] { ("Название ККТ", "Name", 170), ("Зав. № ККТ", "Serial", 145), ("ФН", "Fn", 145), ("РНМ", "Rnm", 145), ("Организация", "Organization", 120), ("Торговая точка", "Point", 190), ("Источник привязки", "Source", 100), ("Статус", "Status", 160), ("Период", "Period", 180) })
            grid.Columns.Add(new DataGridTextColumn { Header = column.Item1, Binding = new Binding(column.Item2), Width = column.Item3 });
        void Load()
        {
            var locations = _db.Locations(true); var orgs = _db.Organizations();
            grid.ItemsSource = RegisterBindingService.Read(_db, history.IsChecked == true).Where(x => SelectedOrganizationId is null || x.OrganizationId == SelectedOrganizationId)
                .Select(b => new RegisterRow(b, b.DisplayName, b.KktSerial, b.FiscalDriveNumber, b.RegisterNumber,
                    orgs.FirstOrDefault(x => x.Id == b.OrganizationId)?.Name ?? "",
                    locations.FirstOrDefault(x => x.Id == b.LocationId)?.Name ?? "ККТ не привязана",
                    b.BindingSource == BindingSource.Manual ? "Вручную" : b.BindingSource == BindingSource.Rule ? "Правило" : "Автоматически",
                    !b.IsActive ? "Архив изменения" : b.LocationId is null ? "Требуется привязка" : b.IsLocked ? "Привязка закреплена" : "Привязана",
                    $"{(b.ValidFrom is null ? "без начала" : b.ValidFrom.Value.ToString("dd.MM.yyyy"))} — {(b.ValidTo is null ? "без окончания" : b.ValidTo.Value.ToString("dd.MM.yyyy"))}")).ToArray();
        }
        void Edit() { if (grid.SelectedItem is RegisterRow row && row.Binding.IsActive) EditRegister(row.Binding); }
        add.Click += (_, _) => EditRegister(null); edit.Click += (_, _) => Edit(); grid.MouseDoubleClick += (_, _) => Edit();
        history.Checked += (_, _) => Load(); history.Unchecked += (_, _) => Load();
        pending.Click += (_, _) => { if (SelectedOrganizationId is Guid org) ShowShiftDetails(org, null, null, "Непривязанные смены — суммы сохранены, в итоги точек не включены"); };
        root.Children.Add(grid); Load(); return root;
    }
    private void EditRegister(RegisterBinding? original)
    {
        var org = original?.OrganizationId ?? SelectedOrganizationId;
        if (org is null) { MessageBox.Show(this, "Выберите организацию."); return; }
        var points = _db.Locations().Where(x => x.OrganizationId == org).ToArray();
        var point = new ComboBox { ItemsSource = points, DisplayMemberPath = "Name", SelectedItem = points.FirstOrDefault(x => x.Id == original?.LocationId) };
        var name = new TextBox { Text = original?.DisplayName ?? "" }; var serial = new TextBox { Text = original?.KktSerial ?? "" }; var fn = new TextBox { Text = original?.FiscalDriveNumber ?? "" }; var rnm = new TextBox { Text = original?.RegisterNumber ?? "" };
        serial.IsReadOnly = !string.IsNullOrEmpty(original?.KktSerial);
        fn.IsReadOnly = !string.IsNullOrEmpty(original?.FiscalDriveNumber);
        rnm.IsReadOnly = !string.IsNullOrEmpty(original?.RegisterNumber);
        var from = new DatePicker { SelectedDate = original?.ValidFrom?.ToDateTime(TimeOnly.MinValue) };
        var to = new DatePicker { SelectedDate = original?.ValidTo?.ToDateTime(TimeOnly.MinValue) };
        var all = new CheckBox { Content = "Исправить ошибочную точку за весь период (это не переезд)", Margin = new Thickness(0, 8, 0, 8) };
        var content = Stack(Text("Название ККТ"), name, Text("Заводской номер ККТ"), serial, Text("ФН"), fn, Text("РНМ"), rnm,
            Text("Физическая торговая точка"), point, Text("Действует с (при переезде обязательно)"), from, Text("По включительно (пусто — без окончания)"), to, all,
            Text("При переезде прежние даты останутся на старой точке. Банковская история, включая закрытый УБРиР, не переносится."));
        if (!Dialog("ККТ — точка и период", content, () => point.SelectedItem is Location &&
            (!string.IsNullOrWhiteSpace(serial.Text) || !string.IsNullOrWhiteSpace(fn.Text) || !string.IsNullOrWhiteSpace(rnm.Text)) &&
            (original?.LocationId is null || original.LocationId == ((Location)point.SelectedItem).Id || from.SelectedDate is not null || all.IsChecked == true))) return;
        if (point.SelectedItem is not Location selectedPoint) return;
        var binding = (original ?? new RegisterBinding(Guid.NewGuid(), org.Value, null, "")) with { DisplayName = name.Text.Trim(), KktSerial = serial.Text.Trim(), FiscalDriveNumber = fn.Text.Trim(), RegisterNumber = rnm.Text.Trim() };
        try
        {
            RegisterBindingService.Assign(_db, binding, selectedPoint.Id,
                from.SelectedDate is DateTime f ? DateOnly.FromDateTime(f) : null, to.SelectedDate is DateTime t ? DateOnly.FromDateTime(t) : null);
            RefreshAll(); Navigate("registers"); StatusText.Text = "Привязка ККТ сохранена вручную и защищена от автоматического изменения";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Привязка не сохранена", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void ShowDayDetails(DaySummaryRow row) => ShowShiftDetails(row.OrganizationId, row.LocationId, row.DateValue, $"{row.Point} — {row.DateValue:dd.MM.yyyy}");
    private void ShowShiftDetails(Guid org, Guid? location, DateOnly? day, string title)
    {
        var rows = _db.ShiftDetails(org, location, day); var root = new DockPanel { Margin = new Thickness(12) };
        var summary = day is DateOnly d && location is Guid l ? _db.CanonicalPointDaySummaries(org, d.Year, d.Month, l).FirstOrDefault(x => x.Date == d) : null;
        var text = summary is null ? title : summary.HasSourceConflict ? title + "\nКонфликт источников — итог кассы не определён. Ниже обе исходные суммы." :
            $"{title}\nКасса безнал: {(summary.FiscalElectronic is decimal amount ? amount.ToString("N2") + " ₽" : "нет данных")}\nУчтённые смены: {rows.Where(x => x.Included).Sum(x => x.Electronic):N2} ₽ безнал. Подтверждения повторно не складываются.";
        if (summary is not null && _db.ManualCashPostings(org, day!.Value.Year, day.Value.Month, location).Any(x => x.Date == day)) text += "\nИтог кассы задан вручную; исходные суммы смен показаны без изменения.";
        var header = Text(text, 16, true); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var grid = new DataGrid { ItemsSource = rows, IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false };
        foreach (var col in new[] { ("ККТ", "Register", 180), ("Зав. №", "KktSerial", 135), ("ФН", "Fn", 145), ("РНМ", "Rnm", 145), ("Смена", "ShiftNumber", 60), ("Закрытие", "ClosedAt", 175), ("Выручка", "Total", 105), ("Наличные", "Cash", 100), ("Безналичные", "Electronic", 110), ("Источник", "Source", 150), ("Учёт", "Status", 250), ("Документ", "Document", 200) })
            grid.Columns.Add(new DataGridTextColumn { Header = col.Item1, Binding = new Binding(col.Item2) { StringFormat = col.Item2 is "Total" or "Cash" or "Electronic" ? "N2" : col.Item2 == "ClosedAt" ? "dd.MM.yyyy HH:mm:ss" : null }, Width = col.Item3 });
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "Смены и подтверждения", Content = grid });
        var operations = new DataGrid { ItemsSource = _db.DayOperations(org, location, day), IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false };
        foreach (var col in new[] { ("Дата", "Date", 160), ("Источник", "Source", 145), ("ККТ", "Register", 170), ("ФН", "Fn", 145), ("Зав. №", "Serial", 135), ("РНМ", "Rnm", 135), ("Смена", "Shift", 60), ("Операция", "Kind", 85), ("Оплата", "Payment", 85), ("Сумма", "Amount", 110), ("Учёт", "Status", 280), ("Документ", "Document", 210) })
            operations.Columns.Add(new DataGridTextColumn { Header = col.Item1, Binding = new Binding(col.Item2) { StringFormat = col.Item2 == "Amount" ? "N2" : col.Item2 == "Date" ? "dd.MM.yyyy HH:mm:ss" : null }, Width = col.Item3 });
        tabs.Items.Add(new TabItem { Header = "Исходные операции (касса и банк)", Content = operations }); root.Children.Add(tabs);
        new Window { Owner = this, Title = title, Content = root, Width = 1200, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }
}

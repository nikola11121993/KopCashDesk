using KopCashDesk.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KopCashDesk.Desktop;

public sealed class ManualCashEditWindow : Window
{
    private readonly TextBox _amountBox;
    private readonly CultureInfo _ru = CultureInfo.GetCultureInfo("ru-RU");

    public decimal? Value { get; private set; }
    public bool ClearRequested { get; private set; }

    public ManualCashEditWindow(string point, DateOnly date, decimal? currentValue)
    {
        Title = "Касса — ручная сумма";
        Width = 430;
        Height = 245;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = point,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        root.Children.Add(title);

        var dateText = new TextBlock
        {
            Text = date.ToString("dd.MM.yyyy", _ru),
            Margin = new Thickness(0, 5, 0, 18),
            Foreground = System.Windows.Media.Brushes.DimGray
        };
        Grid.SetRow(dateText, 1);
        root.Children.Add(dateText);

        var amountPanel = new Grid();
        amountPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        amountPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        amountPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        amountPanel.Children.Add(new TextBlock
        {
            Text = "Касса безнал:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        });

        _amountBox = new TextBox
        {
            FontSize = 18,
            MinWidth = 180,
            Text = currentValue?.ToString("N2", _ru) ?? string.Empty,
            ToolTip = "Введите сумму безнала на кассе. Можно использовать запятую или точку."
        };
        _amountBox.KeyDown += AmountBox_KeyDown;
        Grid.SetColumn(_amountBox, 1);
        amountPanel.Children.Add(_amountBox);

        var rub = new TextBlock
        {
            Text = "₽",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 18
        };
        Grid.SetColumn(rub, 2);
        amountPanel.Children.Add(rub);
        Grid.SetRow(amountPanel, 2);
        root.Children.Add(amountPanel);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var clear = new Button { Content = "Очистить", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        clear.Click += (_, _) =>
        {
            ClearRequested = true;
            Value = null;
            DialogResult = true;
        };
        buttons.Children.Add(clear);

        var cancel = new Button { Content = "Отмена", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(cancel);

        var save = new Button { Content = "Сохранить", MinWidth = 100, IsDefault = true };
        save.Click += (_, _) => Save();
        buttons.Children.Add(save);

        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
            _amountBox.Focus();
            _amountBox.SelectAll();
        };
    }

    private void AmountBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Save();
            e.Handled = true;
        }
    }

    private void Save()
    {
        var text = (_amountBox.Text ?? string.Empty)
            .Replace("\u00A0", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "Введите сумму или нажмите «Очистить».", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, _ru, out var value) &&
            !decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
        {
            MessageBox.Show(this, "Сумма введена неверно.", "КОП Кассы", MessageBoxButton.OK, MessageBoxImage.Warning);
            _amountBox.Focus();
            _amountBox.SelectAll();
            return;
        }

        Value = Money.Normalize(value);
        ClearRequested = false;
        DialogResult = true;
    }
}

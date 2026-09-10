using KopCashDesk.Data;
using System.Windows;
using System.Windows.Controls;

namespace KopCashDesk.Desktop;

public partial class MainWindow
{
    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _db.EnsureManualCashPostings();

        var merged = 0;
        merged += _db.MergeLocationsByName("Вороний Брод", "Мира 4", "касса перемещалась: Вороний Брод -> Ленинградская 1 -> Мира 4");
        merged += _db.MergeLocationsByName("Ленинградская 1", "Мира 4", "касса перемещалась: Вороний Брод -> Ленинградская 1 -> Мира 4");
        merged += _db.MergeLocationsByName("Буфет", "Чапаева 28", "одна торговая точка по адресу Чапаева, 28");

        if (merged > 0)
        {
            RefreshAll();
            StatusText.Text = $"Объединено торговых точек: {merged}";
        }
    }

    private void Summary_Click(object sender, RoutedEventArgs e)
    {
        _page = "summary";
        SearchBox.Text = "";
        SearchBox.IsEnabled = false;
        PrimaryButton.Visibility = Visibility.Collapsed;
        PageContent.Content = RenderSummary();
    }

    private void OrganizationFilter_SelectionChangedV04(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (_page == "summary")
        {
            SearchBox.IsEnabled = false;
            PrimaryButton.Visibility = Visibility.Collapsed;
            PageContent.Content = RenderSummary();
            return;
        }
        if (_page == "terminals")
        {
            SearchBox.IsEnabled = false;
            PrimaryButton.Visibility = Visibility.Collapsed;
            PageContent.Content = RenderTerminals();
            return;
        }
        Render();
    }

    private void RefreshV04_Click(object sender, RoutedEventArgs e)
    {
        RefreshAll();
        if (_page == "summary")
        {
            SearchBox.IsEnabled = false;
            PrimaryButton.Visibility = Visibility.Collapsed;
            PageContent.Content = RenderSummary();
        }
        else if (_page == "terminals")
        {
            SearchBox.IsEnabled = false;
            PrimaryButton.Visibility = Visibility.Collapsed;
            PageContent.Content = RenderTerminals();
        }
    }
}

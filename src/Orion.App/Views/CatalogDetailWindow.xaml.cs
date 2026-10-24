using System.Windows;
using Orion.App.ViewModels;

namespace Orion.App.Views;

public partial class CatalogDetailWindow : Window
{
    public CatalogDetailWindow(CatalogDetailViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

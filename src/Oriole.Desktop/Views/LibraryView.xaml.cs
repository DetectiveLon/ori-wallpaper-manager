using System.Windows;
using System.Windows.Controls;
using Oriole.Desktop.ViewModels;

namespace Oriole.Desktop.Views;

public sealed partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        DataContext = new LibraryViewModel();
        Loaded += OnLoaded;
    }

    private LibraryViewModel ViewModel => (LibraryViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.RefreshCommand.Execute(null);
}

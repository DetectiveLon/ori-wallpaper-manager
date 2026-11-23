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

    // Thumbnail decoding is lazy: a card is decoded only when its row is realized by the
    // virtualizing panel. Both handlers are idempotent (the VM guards duplicate requests),
    // and Loaded / DataContextChanged together cover initial realization and row recycling.
    private void OnCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        // DataContextChanged is a CLR event, so it cannot be an XAML EventSetter.
        // Attach it once per realized row from Loaded instead.
        if (element.Tag is not true)
        {
            element.DataContextChanged += OnCardDataContextChanged;
            element.Tag = true;
        }

        if (element.DataContext is LibraryEntryViewModel card)
            ViewModel.RequestThumbnail(card);
    }

    private void OnCardDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is LibraryEntryViewModel card)
            ViewModel.RequestThumbnail(card);
    }
}

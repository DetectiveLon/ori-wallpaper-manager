using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Oriole.Desktop.ViewModels;

namespace Oriole.Desktop.Views;

public sealed partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        DataContext = new LibraryViewModel();
        Loaded += OnLoaded;
        // NavigationService caches views: Unloaded fires when the user leaves this page.
        // Cancel debounces, imports and any open confirmation so no work outlives the view.
        Unloaded += OnUnloaded;
    }

    private LibraryViewModel ViewModel => (LibraryViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.RefreshCommand.Execute(null);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Views are cached by NavigationService: Loaded fires again on every return
        // visit. Keep the handler attached so cleanup also runs on later departures.
        ViewModel.CancelOperations();
    }

    private void OnImportFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = "Select a folder of wallpapers" };
            if (dialog.ShowDialog() == true)
                _ = ViewModel.ImportFolderAsync(dialog.FolderName);
        }
        catch (Exception ex)
        {
            ViewModel.Status = $"Could not open folder picker: {ex.Message}";
        }
    }

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

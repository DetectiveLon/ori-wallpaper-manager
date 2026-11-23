using System.Windows;
using System.Windows.Controls;
using Oriole.Desktop.ViewModels;

namespace Oriole.Desktop.Views;

/// <summary>XAML host for the Wallhaven discovery experience.</summary>
public partial class DiscoverView : UserControl
{
    public DiscoverView()
    {
        InitializeComponent();
        DataContext = new DiscoverViewModel();
        var vm = (DiscoverViewModel)DataContext;

        // PasswordBox does not support binding; bridge it to the view model explicitly.
        ApiKeyBox.PasswordChanged += (_, _) => vm.ApiKey = ApiKeyBox.Password;

        Loaded += (_, _) =>
        {
            ApiKeyBox.Password = vm.ApiKey;
            vm.OwnerWindow = Window.GetWindow(this);
            vm.LoadDisplays();
        };
        Unloaded += (_, _) => vm.CancelSearch();
    }
}

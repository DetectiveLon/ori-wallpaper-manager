using System.Windows.Controls;

namespace Oriole.Desktop.Views;

/// <summary>
/// Calm landing page with a hero card and six navigation cards. Buttons bind to
/// the shell's NavigateCommand; MainViewModel supplies itself as DataContext
/// when it creates this view.
/// </summary>
public sealed partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
    }
}

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ori.Desktop.Mvvm;

/// <summary>
/// Minimal INotifyPropertyChanged base with no external dependencies.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Assigns a new value and raises PropertyChanged only when the value actually changed.
    /// </summary>
    /// <returns>True when the value changed, false when it was already equal.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
}

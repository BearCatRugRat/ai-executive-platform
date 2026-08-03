using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Aep.Core;

/// <summary>
/// Minimal INotifyPropertyChanged base for view models. Deliberately small:
/// per ADR 0003's extraction rule (collector-intelligence-engine, Section 10),
/// this stays in the shared Aep.Core library only because more than one
/// module's view models are expected to need it, not because "MVVM base
/// class" sounds generically reusable. No MVVM toolkit dependency is taken
/// on for something this small.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

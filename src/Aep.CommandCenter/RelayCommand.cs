using System.Windows.Input;

namespace Aep.CommandCenter;

/// <summary>Minimal fire-and-forget async ICommand - just enough for a Reload button.</summary>
internal sealed class RelayCommand(Func<Task> execute) : ICommand
{
    // Required by ICommand, but this command is always enabled and never
    // needs to signal a CanExecute change, so it's intentionally unused.
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter) => await execute();
}

/// <summary>
/// Same as <see cref="RelayCommand"/> but takes a typed parameter - used by
/// the per-project Open/Develop buttons, where the parameter is the
/// GovernanceProjectDto the button was clicked on (bound via
/// CommandParameter="{Binding}" inside the card's DataTemplate).
/// </summary>
internal sealed class RelayCommand<T>(Func<T, Task> execute) : ICommand
{
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

    public bool CanExecute(object? parameter) => parameter is T;

    public async void Execute(object? parameter)
    {
        if (parameter is T typed)
        {
            await execute(typed);
        }
    }
}

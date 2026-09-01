using System.Windows;

namespace Aep.CommandCenter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Last-resort safety net. Every command already reports its own
        // failures in the status bar; this is here so a case we didn't
        // anticipate (a hand-broken registry edit, an unexpected null on a
        // navigation path) shows a dismissable message and lets the console
        // keep running, instead of taking the whole process down mid-session.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "The Command Center hit an unexpected error but is still running:\n\n" +
                args.Exception.Message,
                "AI Executive Platform",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
        };

        // A faulted fire-and-forget Task (e.g. the best-effort briefing-cache
        // write in MainViewModel.LoadAsync) must never be able to escalate to
        // a process-killing exception when it's finalized.
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        base.OnStartup(e);
    }
}

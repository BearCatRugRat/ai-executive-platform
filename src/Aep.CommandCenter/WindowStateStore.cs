using System.IO;
using System.Text.Json;
using System.Windows;

namespace Aep.CommandCenter;

/// <summary>
/// Persists the main window's size/position (and maximized state) across
/// sessions, so a console meant to be opened daily doesn't reset to the same
/// default rectangle every launch. Same "best-effort, one file, never block
/// or crash the app" posture as Aep.PlatformServices.Governance.BriefingCache
/// - a failure to read or write this file just means the window falls back
/// to its XAML defaults, never an error the user has to deal with.
/// </summary>
internal static class WindowStateStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AepCommandCenter",
        "window-state.json");

    private sealed record Placement(double Left, double Top, double Width, double Height, bool Maximized);

    /// <summary>Restores the last saved placement onto <paramref name="window"/>,
    /// if one exists and still looks usable. Call before the window is shown
    /// (e.g. in its constructor) so there's no visible jump. Deliberately
    /// conservative: a placement that's absurdly small or sits entirely off
    /// every currently-connected monitor (e.g. saved while an external
    /// monitor was attached, then opened without it) is ignored rather than
    /// applied - WPF doesn't clamp off-screen windows on its own, so
    /// restoring a bad position could put the whole console somewhere the
    /// user can't reach it.</summary>
    public static void Apply(Window window)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var placement = JsonSerializer.Deserialize<Placement>(File.ReadAllText(FilePath));
            if (placement is null || placement.Width < 400 || placement.Height < 300)
            {
                return;
            }

            var virtualScreen = new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var saved = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
            if (!virtualScreen.IntersectsWith(saved))
            {
                return;
            }

            window.Left = placement.Left;
            window.Top = placement.Top;
            window.Width = placement.Width;
            window.Height = placement.Height;
            if (placement.Maximized)
            {
                window.WindowState = WindowState.Maximized;
            }
        }
        catch
        {
            // Corrupt or unreadable file - fall through to the XAML defaults.
        }
    }

    /// <summary>Saves <paramref name="window"/>'s current placement. Call from
    /// Closing. Uses RestoreBounds while maximized so the *un-maximized*
    /// rectangle is what comes back if the window later starts un-maximized.</summary>
    public static void Save(Window window)
    {
        try
        {
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;

            var placement = new Placement(
                bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                window.WindowState == WindowState.Maximized);

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(placement));
        }
        catch
        {
            // Best-effort - never block shutdown over a state-save failure.
        }
    }
}

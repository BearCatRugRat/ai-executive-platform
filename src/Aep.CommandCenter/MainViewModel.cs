using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using Aep.Core;
using Aep.PlatformServices.Governance;

namespace Aep.CommandCenter;

/// <summary>
/// Backs MainWindow. Per ADR 0003 addendum A3/B2 (collector-intelligence-engine),
/// the command center renders one entry per active row in governance's
/// SoftwareProject / GovernanceProject tables - grouped by tier for
/// GovernanceProject - rather than a hard-coded list of domains. Adding a
/// new domain or command-center area is then a data change (a new row via
/// the governance API), not a rebuild of this window.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    // How often to retry on its own while the governance API is unreachable -
    // e.g. Docker Desktop restarting after an outage (see 2026-08-18) - so the
    // console recovers by itself instead of requiring Brian to remember to
    // click Reload once the API's actually back.
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly GovernanceClient _governanceClient;
    private readonly DispatcherTimer _retryTimer;
    private bool _isLoading;
    private string _statusMessage = "Loading governance data...";
    private DateTimeOffset? _lastSuccessfulLoadAt;

    // Statuses that mean "don't render this card" - e.g. a GovernanceProject
    // row consolidated into another one by a governance cleanup (see
    // scripts/sync_governance.py in collector-intelligence-engine) rather
    // than deleted outright, so the history stays queryable via the API
    // without cluttering the daily-use console with a duplicate/dead card.
    private static readonly string[] HiddenStatuses = ["Superseded", "Archived", "Retired"];

    public MainViewModel(GovernanceClient governanceClient)
    {
        _governanceClient = governanceClient;
        ReloadCommand = new RelayCommand(LoadAsync);
        DevCatchUpCommand = new RelayCommand(DevCatchUpAsync);
        OpenProjectCommand = new RelayCommand<GovernanceProjectDto>(p => LaunchAsync(p, "launch-open.ps1", "Open"));
        DevelopProjectCommand = new RelayCommand<GovernanceProjectDto>(p => LaunchAsync(p, "launch-dev.ps1", "Develop"));

        _retryTimer = new DispatcherTimer { Interval = RetryInterval };
        _retryTimer.Tick += async (_, _) =>
        {
            if (!IsLoading)
            {
                await LoadAsync();
            }
        };
    }

    public ObservableCollection<GovernanceProjectDto> DomainApplications { get; } = [];
    public ObservableCollection<GovernanceProjectDto> CommandCenterAreas { get; } = [];
    public ObservableCollection<SoftwareProjectDto> SoftwareProjects { get; } = [];
    public ObservableCollection<ActionDto> OpenActions { get; } = [];

    public bool IsLoading
    {
        get => _isLoading;
        set => SetField(ref _isLoading, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public ICommand ReloadCommand { get; }
    public ICommand DevCatchUpCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand DevelopProjectCommand { get; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading governance data...";
        try
        {
            var briefing = await _governanceClient.GetBriefingAsync();
            Populate(briefing);
            _lastSuccessfulLoadAt = DateTimeOffset.Now;
            _retryTimer.Stop();

            StatusMessage = $"Updated {DateTime.Now:t} - {DomainApplications.Count} domain application(s), " +
                             $"{CommandCenterAreas.Count} command center area(s), {SoftwareProjects.Count} software " +
                             $"project(s), {OpenActions.Count} open action(s).";

            // Fire-and-forget: caching is best-effort and must never delay or
            // fail a load that already succeeded (see BriefingCache).
            _ = BriefingCache.SaveAsync(briefing);
        }
        catch (Exception ex)
        {
            var apiError = $"Could not reach the governance API at {GovernanceClient.DefaultBaseAddress} - " +
                            $"is the collector-intelligence-engine Docker stack running? ({ex.Message})";

            if (_lastSuccessfulLoadAt is { } lastLoad)
            {
                // Already showing real data from earlier this session (Populate
                // is never called on failure, so it's still on screen) - just
                // make clear it's aging, don't manufacture a scarier "nothing
                // works" message than what's actually true.
                StatusMessage = $"{apiError} Still showing data as of {lastLoad:t}.";
            }
            else if (BriefingCache.TryLoad() is { } cached)
            {
                // Cold start during an outage (e.g. Docker Desktop itself failing
                // to start, 2026-08-18) - show the last known-good briefing
                // instead of a blank console, clearly labeled as stale.
                Populate(cached.Briefing);
                StatusMessage = $"{apiError} Showing cached data from {cached.FetchedAt:g} - may be out of date.";
            }
            else
            {
                StatusMessage = apiError;
            }

            _retryTimer.Start();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Populate(ExecutiveBriefingDto briefing)
    {
        DomainApplications.Clear();
        CommandCenterAreas.Clear();
        SoftwareProjects.Clear();
        OpenActions.Clear();

        foreach (var project in briefing.GovernanceProjects
                     .Where(p => !HiddenStatuses.Contains(p.Status, StringComparer.OrdinalIgnoreCase))
                     .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (project.Tier == GovernanceTiers.DomainApplication)
            {
                DomainApplications.Add(project);
            }
            else
            {
                // Anything not explicitly tagged "Domain Application" - including
                // an untagged/unclassified row - renders as a Command Center Area.
                // That's the safer default: an unclassified row should still show
                // up somewhere rather than silently vanish from the console.
                CommandCenterAreas.Add(project);
            }
        }

        foreach (var project in briefing.SoftwareProjects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            SoftwareProjects.Add(project);
        }

        foreach (var action in briefing.OpenActions)
        {
            OpenActions.Add(action);
        }
    }

    /// <summary>
    /// Opens a new Claude Code session (not Cowork - this button is specifically
    /// for dev-context catch-up on active software work, e.g. Myers Wolin AI,
    /// collector-intelligence-engine, ai-executive-platform) via Claude Desktop's
    /// claude:// deep link (see support.claude.com's "Open Claude Desktop with a
    /// link"). Code sessions get real local shell/file access rather than a
    /// sandbox, so Claude can actually check git/docker/build state instead of
    /// just reading files. The prompt points at DEV_CATCHUP.md rather than
    /// embedding the full procedure in the URL, so the checklist stays editable
    /// without touching this code.
    /// </summary>
    public Task DevCatchUpAsync()
    {
        const string url = "claude://code/new?q=Read%20C%3A%5CDevelopment%5CDEV_CATCHUP.md%20and%20run%20" +
                            "the%20Dev%20Catch-Up%20procedure%20it%20describes.&folder=C%3A%5CDevelopment";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            StatusMessage = "Opening a Claude Code session on C:\\Development - the catch-up prompt will be " +
                             "pre-filled in the new window, but you need to press Enter there to send it " +
                             "(Claude Desktop deep links never auto-send, by design).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open Claude Desktop ({ex.Message}). Is it installed?";
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Backs both OpenProjectCommand and DevelopProjectCommand. Each
    /// project's own repo owns its launch logic (launch-open.ps1 /
    /// launch-dev.ps1 at the repo root) rather than this shell duplicating
    /// docker/npm/claude-deep-link details per project - this method just
    /// finds and runs the right script. A project with no RepoPath (most
    /// Command Center Areas - button + status only, no dedicated repo) has
    /// nothing to launch, which is expected, not an error.
    /// </summary>
    private Task LaunchAsync(GovernanceProjectDto project, string scriptName, string actionLabel)
    {
        if (!project.HasRepoPath)
        {
            StatusMessage = $"{project.Name} has no repo linked - nothing to {actionLabel.ToLowerInvariant()}.";
            return Task.CompletedTask;
        }

        var scriptPath = Path.Combine(project.RepoPath!, scriptName);
        if (!File.Exists(scriptPath))
        {
            StatusMessage = $"{actionLabel} script not found for {project.Name}: {scriptPath}";
            return Task.CompletedTask;
        }

        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            Process.Start(psi);

            StatusMessage = $"{actionLabel}ing {project.Name}... " +
                             "(the launch script may pop its own window - e.g. a dev server terminal " +
                             "or a Claude Code session - give it a few seconds)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not {actionLabel.ToLowerInvariant()} {project.Name}: {ex.Message}";
        }

        return Task.CompletedTask;
    }
}

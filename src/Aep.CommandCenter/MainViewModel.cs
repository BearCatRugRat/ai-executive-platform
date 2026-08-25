using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Aep.Core;
using Aep.ModuleContracts;
using Aep.PlatformServices.Governance;
using Aep.PlatformServices.ModuleStatus;

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

    // One ModuleStatusClient per known domain app (Aep.PlatformServices.
    // ModuleStatus.KnownModules) - built once here rather than passed in via
    // the constructor, since there's no DI container in this app yet and
    // every module currently uses the same no-arg construction. Keyed by
    // base address so a per-module failure can be labeled with something
    // meaningful even before that module has ever successfully responded
    // (see LoadModuleStatusAsync).
    private static readonly Uri[] ModuleBaseAddresses = [KnownModules.CollectorIntelligence, KnownModules.MyersWolinIp];
    private readonly Dictionary<Uri, ModuleStatusClient> _moduleStatusClients =
        ModuleBaseAddresses.ToDictionary(address => address, address => new ModuleStatusClient(address));

    private static readonly Dictionary<string, (string Title, string Body)> HelpTopics = new()
    {
        ["overview"] = (
            "AI Executive Platform - Command Center",
            "This console is the daily starting point across all AI Executive Platform work.\n\n" +
            "- Domain Applications: the real, database-backed products (Collector Intelligence, " +
            "Myers Wolin IP). 'Open' launches the app for real work; 'Develop' opens a Claude Code " +
            "session scoped to that app's own repo.\n\n" +
            "- Command Center Areas: everything else being tracked (finance, health, home, travel, " +
            "etc.) that doesn't have its own dedicated app yet - a status card, with the same " +
            "Open/Develop options if a repo exists.\n\n" +
            "- Module Status: live data pulled directly from each Domain Application's own API right " +
            "now (not the governance log) - e.g. today's market outlook, open deadlines. 'Go deeper' " +
            "on any card jumps into a Claude session scoped to that specific topic.\n\n" +
            "- Software Projects / Open Actions: the engineering backlog and open to-dos from the " +
            "governance database.\n\n" +
            "- Dev Catch-Up opens a Claude Code session on all of C:\\Development, pre-filled to read " +
            "the catch-up doc. Cowork Catch-Up opens a Claude Cowork session with both governance " +
            "repos attached. Reload refreshes everything on this screen from source.\n\n" +
            "Click the '?' next to any section below for more detail on that section specifically."),
        ["domain-applications"] = (
            "Domain Applications",
            "A Domain Application is a real product with its own database, API, and module-status " +
            "contract - right now that's Collector Intelligence (coins/bullion) and Myers Wolin IP " +
            "(patent practice).\n\n" +
            "'Open' launches the app for real work. 'Develop' opens a Claude Code session scoped to " +
            "that repo's code, for building or fixing it."),
        ["command-center-areas"] = (
            "Command Center Areas",
            "Everything being tracked that isn't (yet) its own Domain Application - Personal Finance " +
            "& Tax, Myers Family, Health, Home Technology, Travel, Research Platform, Development/" +
            "Platform status. Each gets a status card and, if it has a repo, the same Open/Develop " +
            "buttons.\n\n" +
            "An area can graduate to a full Domain Application later, but only as a deliberate decision."),
        ["software-projects"] = (
            "Software Projects",
            "The engineering backlog - what's being actively built or fixed across the platform's " +
            "own code, independent of the Domain Application / Command Center Area business view above."),
        ["open-actions"] = (
            "Open Actions",
            "Open to-dos logged in the governance database, sorted by priority - things waiting on " +
            "you or on someone else, tracked so they don't get lost between sessions."),
        ["module-status"] = (
            "Module Status",
            "Live data pulled straight from each Domain Application's own GET /module-status API " +
            "right now - not from governance data entry, so it's current as of the moment this " +
            "screen was loaded.\n\n" +
            "Each card's color means: green = all clear, amber = worth a look, red = needs attention " +
            "soon or something's overdue.\n\n" +
            "'Go deeper' opens a Claude Code session pre-scoped to that exact topic, in that app's " +
            "own repo."),
    };

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
        CoworkCatchUpCommand = new RelayCommand(CoworkCatchUpAsync);
        OpenProjectCommand = new RelayCommand<GovernanceProjectDto>(p => LaunchAsync(p, "launch-open.ps1", "Open"));
        DevelopProjectCommand = new RelayCommand<GovernanceProjectDto>(p => LaunchAsync(p, "launch-dev.ps1", "Develop"));
        LaunchModuleCardCommand = new RelayCommand<ModuleStatusCardDto>(LaunchModuleCard);
        ShowHelpCommand = new RelayCommand<string>(ShowHelp);

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
    public ObservableCollection<ModuleStatusDto> ModuleStatuses { get; } = [];

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
    public ICommand CoworkCatchUpCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand DevelopProjectCommand { get; }
    public ICommand LaunchModuleCardCommand { get; }
    public ICommand ShowHelpCommand { get; }

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
            // Runs whether the governance fetch above succeeded or failed -
            // module status is a separate feed with its own per-module
            // failure handling (see LoadModuleStatusAsync), so a governance
            // outage (e.g. collector-intelligence-engine's Docker stack down)
            // should never block Myers Wolin's module status from loading,
            // and vice versa.
            await LoadModuleStatusAsync();
            IsLoading = false;
        }
    }

    /// <summary>
    /// Fetches every known module's GET /module-status independently - one
    /// module being unreachable never blocks another's cards from rendering,
    /// and shows up as its own "unreachable" card (severity alert) rather
    /// than a silent gap or a thrown exception.
    /// </summary>
    private async Task LoadModuleStatusAsync()
    {
        var results = new List<ModuleStatusDto>();

        foreach (var (address, client) in _moduleStatusClients)
        {
            try
            {
                results.Add(await client.GetStatusAsync());
            }
            catch (Exception ex)
            {
                results.Add(new ModuleStatusDto(
                    Module: address.Authority,
                    DisplayName: $"{address.Authority} (unreachable)",
                    GeneratedAt: DateTimeOffset.Now,
                    Cards:
                    [
                        new ModuleStatusCardDto(
                            Id: "unreachable",
                            Title: "Unavailable",
                            Headline: $"Could not reach {address} - is its Docker stack running?",
                            Detail: ex.Message,
                            Severity: ModuleStatusSeverity.Alert,
                            UpdatedAt: null,
                            WebUrl: null,
                            ClaudeDeepLink: null),
                    ]));
            }
        }

        ModuleStatuses.Clear();
        foreach (var status in results)
        {
            ModuleStatuses.Add(status);
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
    /// Reciprocal to DevCatchUpAsync: opens a new Claude Cowork session (not
    /// Code) via the same claude:// deep-link mechanism, pre-filled to run the
    /// brian-catchup skill with both governance-doc repos attached. Code
    /// sessions have real shell access for git/docker/build state but no path
    /// back into Cowork's skill layer - this button closes that asymmetry.
    /// </summary>
    public Task CoworkCatchUpAsync()
    {
        const string url = "claude://cowork/new?q=Run%20the%20brian-catchup%20skill%20and%20catch%20me%20up." +
                            "&folder=C%3A%5CDevelopment%5Ccollector-intelligence-engine" +
                            "&folder=C%3A%5CDevelopment%5Cai-executive-platform";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            StatusMessage = "Opening a Claude Cowork session with both repos attached - the catch-up prompt " +
                             "will be pre-filled in the new window, but you need to press Enter there to send " +
                             "it (Claude Desktop deep links never auto-send, by design).";
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

    /// <summary>
    /// Backs LaunchModuleCardCommand, the "Go deeper" button on every module
    /// status card. Prefers WebUrl when a card offers one (the Market Outlook
    /// page and similar are deferred but this keeps the door open); falls
    /// back to ClaudeDeepLink; a card with neither just reports it has
    /// nothing to open rather than the button silently doing nothing.
    /// </summary>
    private Task LaunchModuleCard(ModuleStatusCardDto card)
    {
        var url = !string.IsNullOrWhiteSpace(card.WebUrl) ? card.WebUrl : card.ClaudeDeepLink;
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusMessage = $"'{card.Title}' has no link to open.";
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            StatusMessage = url.StartsWith("claude://", StringComparison.Ordinal)
                ? $"Opening a Claude session for '{card.Title}' - press Enter in the new window to send " +
                  "the pre-filled prompt (Claude Desktop deep links never auto-send, by design)."
                : $"Opening '{card.Title}' in your browser.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the link for '{card.Title}' ({ex.Message}).";
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Backs ShowHelpCommand - the '?' buttons next to the header and each
    /// section. Deliberately a plain MessageBox rather than a custom dialog:
    /// this app has no dialog infrastructure yet and the help content is
    /// static text, so a MessageBox is the simplest thing that actually
    /// works. See HelpTopics for the content itself.
    /// </summary>
    private Task ShowHelp(string topic)
    {
        if (HelpTopics.TryGetValue(topic, out var help))
        {
            MessageBox.Show(help.Body, help.Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        return Task.CompletedTask;
    }
}

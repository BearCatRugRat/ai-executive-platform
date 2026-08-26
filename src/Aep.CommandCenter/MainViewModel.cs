using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Aep.Core;
using Aep.ModuleContracts;
using Aep.PlatformServices.CommandCenterRegistry;
using Aep.PlatformServices.Governance;
using Aep.PlatformServices.ModuleStatus;

namespace Aep.CommandCenter;

/// <summary>
/// One node's resolved content for the current level of the tree - built
/// fresh from the registry plus whatever live data is currently cached
/// (governance briefing, software projects, module-status responses).
/// Deliberately a plain class, not a record: content is computed once per
/// Reload/navigation and never mutated in place, so there's no need for
/// property-change notification on individual fields - the containing
/// ObservableCollection is just cleared and rebuilt wholesale instead.
/// </summary>
public sealed class CommandCenterNodeView
{
    public required CommandCenterNode Node { get; init; }
    public required bool HasChildren { get; init; }
    public GovernanceProjectDto? GovernanceProject { get; init; }
    public SoftwareProjectDto? SoftwareProject { get; init; }
    public ModuleStatusCardDto? ModuleCard { get; init; }
    public bool ModuleUnreachable { get; init; }
    public List<ActionDto> RelatedActions { get; init; } = [];

    public string Title => Node.Title;

    public bool HasContent => GovernanceProject is not null || SoftwareProject is not null
                              || ModuleCard is not null || ModuleUnreachable;

    /// <summary>Nothing to show at all - no live content and no children to
    /// drill into. Renders Node.StubText instead of an empty card.</summary>
    public bool IsStub => !HasContent && !HasChildren;
}

/// <summary>
/// Backs MainWindow. Renders the Command Center's tree - see
/// Aep.ModuleContracts.CommandCenterNode for the registry shape and
/// command-center-registry.json for the actual tree data - confirmed
/// 2026-08-25 (the "flatten the top, layer buttons as you move down"
/// restructure). Only one level is ever visible at a time (the current
/// node's children, or the root nodes); navigating in/out just changes
/// which slice of the same registry is shown. Adding, renaming, or
/// reshuffling a card is a registry edit, not a code change to this class.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    // How often to retry on its own while the governance API is unreachable -
    // e.g. Docker Desktop restarting after an outage (see 2026-08-18) - so the
    // console recovers by itself instead of requiring Brian to remember to
    // click Reload once the API's actually back.
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private static readonly string RegistryPath =
        Path.Combine(AppContext.BaseDirectory, "command-center-registry.json");

    private readonly GovernanceClient _governanceClient;

    // One ModuleStatusClient per known domain app (Aep.PlatformServices.
    // ModuleStatus.KnownModules) - built once here rather than passed in via
    // the constructor, since there's no DI container in this app yet and
    // every module currently uses the same no-arg construction.
    private static readonly Uri[] ModuleBaseAddresses = [KnownModules.CollectorIntelligence, KnownModules.MyersWolinIp];
    private readonly Dictionary<Uri, ModuleStatusClient> _moduleStatusClients =
        ModuleBaseAddresses.ToDictionary(address => address, address => new ModuleStatusClient(address));

    // Free-text Action.Area values don't always match a GovernanceProject's
    // name exactly (e.g. "Myers Wolin AI" vs. "Myers Wolin IP Intelligence
    // Platform") - this is a small, hand-maintained alias table for the ones
    // that don't line up, same "small static registry, easy to edit" spirit
    // as KnownModules. Exact (case-insensitive) matches never need an entry.
    private static readonly Dictionary<string, string> AreaAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Myers Wolin AI"] = "Myers Wolin IP Intelligence Platform",
        ["Lottery Intelligence"] = "Odds Market Intelligence Platform",
    };

    private static readonly Dictionary<string, (string Title, string Body)> HelpTopics = new()
    {
        ["overview"] = (
            "AI Executive Platform - Command Center",
            "This console is the daily starting point across all AI Executive Platform work.\n\n" +
            "Dev Catch-Up opens a Claude Code session on all of C:\\Development, pre-filled to read " +
            "the catch-up doc - use this for actual software work. Cowork Catch-Up opens a Claude " +
            "Cowork session with both governance repos attached - use this for broader task/research " +
            "work that doesn't need a shell. Reload refreshes everything below from source.\n\n" +
            "Below that is the tree: Personal Finance, Myers Wolin, Odds Market Intelligence, " +
            "General AI, and Other Areas are the top-level domains. Press any card with a 'View " +
            "contents' button to drill into it - a breadcrumb and Back button appear once you're " +
            "inside one. Cards show live status where it exists (green/amber/red = ok/attention/" +
            "alert), and a plain 'not built yet' note where it doesn't. 'Go deeper' on a live card " +
            "opens a Claude session pre-scoped to that exact topic."),
    };

    private readonly DispatcherTimer _retryTimer;
    private bool _isLoading;
    private string _statusMessage = "Loading governance data...";
    private DateTimeOffset? _lastSuccessfulLoadAt;

    // Statuses that mean "don't render this card's live content" - e.g. a
    // GovernanceProject row consolidated into another one by a governance
    // cleanup (see scripts/sync_governance.py in collector-intelligence-
    // engine) rather than deleted outright, so the history stays queryable
    // via the API without cluttering the daily-use console with a dead card.
    private static readonly string[] HiddenStatuses = ["Superseded", "Archived", "Retired"];

    private List<CommandCenterNode> _registryNodes = [];
    private ExecutiveBriefingDto? _latestBriefing;
    private readonly Dictionary<string, ModuleStatusDto> _moduleStatusByBaseAddress = new();
    private readonly HashSet<string> _unreachableModuleAddresses = new();

    // Node ids from root to the currently open node - empty means "at the
    // root", showing the top-level domains. Only single-step Back is wired
    // up for now (not jump-to-any-breadcrumb-segment); BreadcrumbText still
    // shows the full path for orientation.
    private readonly List<string> _currentPath = [];

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
        SelectNodeCommand = new RelayCommand<CommandCenterNodeView>(SelectNode);
        GoBackCommand = new RelayCommand(GoBack);

        _retryTimer = new DispatcherTimer { Interval = RetryInterval };
        _retryTimer.Tick += async (_, _) =>
        {
            if (!IsLoading)
            {
                await LoadAsync();
            }
        };
    }

    public ObservableCollection<CommandCenterNodeView> VisibleNodes { get; } = [];

    /// <summary>The node currently drilled into, if any - its own content (if
    /// it has any) renders above its children. Null at the root.</summary>
    public CommandCenterNodeView? CurrentNode
    {
        get => _currentNode;
        private set => SetField(ref _currentNode, value);
    }
    private CommandCenterNodeView? _currentNode;

    public string BreadcrumbText
    {
        get => _breadcrumbText;
        private set => SetField(ref _breadcrumbText, value);
    }
    private string _breadcrumbText = "Home";

    public bool CanGoBack
    {
        get => _canGoBack;
        private set => SetField(ref _canGoBack, value);
    }
    private bool _canGoBack;

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
    public ICommand SelectNodeCommand { get; }
    public ICommand GoBackCommand { get; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading governance data...";
        ReloadRegistry();

        try
        {
            var briefing = await _governanceClient.GetBriefingAsync();
            _latestBriefing = briefing;
            _lastSuccessfulLoadAt = DateTimeOffset.Now;
            _retryTimer.Stop();

            StatusMessage = $"Updated {DateTime.Now:t} - {briefing.GovernanceProjects.Count} governance " +
                             $"project(s), {briefing.SoftwareProjects.Count} software project(s), " +
                             $"{briefing.OpenActions.Count} open action(s).";

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
                // Already showing real data from earlier this session
                // (_latestBriefing is only ever overwritten on success) -
                // just make clear it's aging, don't manufacture a scarier
                // "nothing works" message than what's actually true.
                StatusMessage = $"{apiError} Still showing data as of {lastLoad:t}.";
            }
            else if (BriefingCache.TryLoad() is { } cached)
            {
                // Cold start during an outage (e.g. Docker Desktop itself failing
                // to start, 2026-08-18) - show the last known-good briefing
                // instead of a blank console, clearly labeled as stale.
                _latestBriefing = cached.Briefing;
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
            // failure handling, so a governance outage (e.g. collector-
            // intelligence-engine's Docker stack down) never blocks Myers
            // Wolin's module status from loading, and vice versa.
            await LoadModuleStatusAsync();
            RebuildVisibleNodes();
            IsLoading = false;
        }
    }

    /// <summary>
    /// Re-reads command-center-registry.json from disk on every load, not
    /// just at startup - so a hand-edit to the tree shape takes effect on
    /// the next Reload press, no rebuild or restart needed. A broken or
    /// missing file keeps whatever registry was already loaded (empty on
    /// first run) rather than crashing the whole load.
    /// </summary>
    private void ReloadRegistry()
    {
        try
        {
            _registryNodes = CommandCenterRegistryLoader.LoadFromFile(RegistryPath);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load the Command Center registry ({ex.Message}).";
        }
    }

    /// <summary>
    /// Fetches every known module's GET /module-status independently - one
    /// module being unreachable never blocks another's cards, and every leaf
    /// pointed at that module renders as unreachable (see RebuildVisibleNodes)
    /// rather than silently showing nothing.
    /// </summary>
    private async Task LoadModuleStatusAsync()
    {
        _unreachableModuleAddresses.Clear();

        foreach (var (address, client) in _moduleStatusClients)
        {
            try
            {
                _moduleStatusByBaseAddress[address.ToString()] = await client.GetStatusAsync();
            }
            catch
            {
                _unreachableModuleAddresses.Add(address.ToString());
            }
        }
    }

    /// <summary>Rebuilds VisibleNodes (and CurrentNode/BreadcrumbText) for
    /// whatever level of the tree _currentPath currently points at, using
    /// whatever governance/module data is currently cached. Called after
    /// every load and after every navigation.</summary>
    private void RebuildVisibleNodes()
    {
        var parentId = _currentPath.Count > 0 ? _currentPath[^1] : null;

        CurrentNode = parentId is null
            ? null
            : BuildNodeView(_registryNodes.First(n => n.Id == parentId));

        BreadcrumbText = _currentPath.Count == 0
            ? "Home"
            : "Home > " + string.Join(" > ", _currentPath.Select(id => _registryNodes.First(n => n.Id == id).Title));

        CanGoBack = _currentPath.Count > 0;

        VisibleNodes.Clear();
        foreach (var node in _registryNodes.Where(n => n.ParentId == parentId))
        {
            VisibleNodes.Add(BuildNodeView(node));
        }
    }

    private CommandCenterNodeView BuildNodeView(CommandCenterNode node)
    {
        var hasChildren = _registryNodes.Any(n => n.ParentId == node.Id);

        GovernanceProjectDto? governanceProject = null;
        if (node.GovernanceProjectName is not null)
        {
            governanceProject = _latestBriefing?.GovernanceProjects.FirstOrDefault(p =>
                p.Name == node.GovernanceProjectName
                && !HiddenStatuses.Contains(p.Status, StringComparer.OrdinalIgnoreCase));
        }

        SoftwareProjectDto? softwareProject = null;
        if (node.SoftwareProjectName is not null)
        {
            softwareProject = _latestBriefing?.SoftwareProjects
                .FirstOrDefault(p => p.Name == node.SoftwareProjectName);
        }

        ModuleStatusCardDto? moduleCard = null;
        var moduleUnreachable = false;
        if (node.ModuleBaseAddressUri is not null)
        {
            var key = node.ModuleBaseAddressUri.ToString();
            if (_unreachableModuleAddresses.Contains(key))
            {
                moduleUnreachable = true;
            }
            else if (node.ModuleCardId is not null
                     && _moduleStatusByBaseAddress.TryGetValue(key, out var status))
            {
                moduleCard = status.Cards.FirstOrDefault(c => c.Id == node.ModuleCardId);
            }
        }

        var relatedActions = _latestBriefing?.OpenActions
            .Where(a => AreaMatchesGovernanceProject(a.Area, node.GovernanceProjectName))
            .ToList() ?? [];

        return new CommandCenterNodeView
        {
            Node = node,
            HasChildren = hasChildren,
            GovernanceProject = governanceProject,
            SoftwareProject = softwareProject,
            ModuleCard = moduleCard,
            ModuleUnreachable = moduleUnreachable,
            RelatedActions = relatedActions,
        };
    }

    private static bool AreaMatchesGovernanceProject(string? area, string? governanceProjectName)
    {
        if (area is null || governanceProjectName is null)
        {
            return false;
        }

        if (string.Equals(area, governanceProjectName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return AreaAliases.TryGetValue(area, out var aliased)
               && string.Equals(aliased, governanceProjectName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Backs SelectNodeCommand - drills into a node's children when
    /// its "View contents" button is pressed. No-op for a node with nothing
    /// underneath it (the button isn't shown in that case anyway).</summary>
    private Task SelectNode(CommandCenterNodeView nodeView)
    {
        if (nodeView.HasChildren)
        {
            _currentPath.Add(nodeView.Node.Id);
            RebuildVisibleNodes();
        }

        return Task.CompletedTask;
    }

    /// <summary>Backs GoBackCommand - steps back up one level. A no-op at the
    /// root (CanGoBack is false there, so the button is disabled anyway).</summary>
    private Task GoBack()
    {
        if (_currentPath.Count > 0)
        {
            _currentPath.RemoveAt(_currentPath.Count - 1);
            RebuildVisibleNodes();
        }

        return Task.CompletedTask;
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
    /// areas - button + status only, no dedicated repo) has nothing to
    /// launch, which is expected, not an error.
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
    /// Backs ShowHelpCommand - the '?' button next to the header. Deliberately
    /// a plain MessageBox rather than a custom dialog: this app has no dialog
    /// infrastructure yet and the help content is static text, so a MessageBox
    /// is the simplest thing that actually works. See HelpTopics for content.
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

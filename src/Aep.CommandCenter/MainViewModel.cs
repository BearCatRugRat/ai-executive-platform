using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
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
    private readonly GovernanceClient _governanceClient;
    private bool _isLoading;
    private string _statusMessage = "Loading governance data...";

    public MainViewModel(GovernanceClient governanceClient)
    {
        _governanceClient = governanceClient;
        ReloadCommand = new RelayCommand(LoadAsync);
        DevCatchUpCommand = new RelayCommand(DevCatchUpAsync);
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

    public async Task LoadAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading governance data...";
        try
        {
            var briefing = await _governanceClient.GetBriefingAsync();

            DomainApplications.Clear();
            CommandCenterAreas.Clear();
            SoftwareProjects.Clear();
            OpenActions.Clear();

            foreach (var project in briefing.GovernanceProjects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
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

            StatusMessage = $"Updated {DateTime.Now:t} - {DomainApplications.Count} domain application(s), " +
                             $"{CommandCenterAreas.Count} command center area(s), {SoftwareProjects.Count} software " +
                             $"project(s), {OpenActions.Count} open action(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not reach the governance API at {GovernanceClient.DefaultBaseAddress} - " +
                             $"is the collector-intelligence-engine Docker stack running? ({ex.Message})";
        }
        finally
        {
            IsLoading = false;
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
            StatusMessage = "Opening a Claude Code session on C:\\Development for a dev catch-up. Claude " +
                             "Desktop will ask you to confirm the folder attachment.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open Claude Desktop ({ex.Message}). Is it installed?";
        }

        return Task.CompletedTask;
    }
}

using System.Windows;
using Aep.PlatformServices.Governance;

namespace Aep.CommandCenter;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel(new GovernanceClient());
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }
}

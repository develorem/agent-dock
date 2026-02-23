using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace AgentDock.Windows;

public partial class GettingStartedWindow : Window
{
    public GettingStartedWindow()
    {
        InitializeComponent();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = e.Uri.AbsoluteUri,
            UseShellExecute = true
        });
        e.Handled = true;
    }
}

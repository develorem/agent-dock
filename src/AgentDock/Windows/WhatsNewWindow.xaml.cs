using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using AgentDock.Services;

using AgentDock.Services.Abstractions;

namespace AgentDock.Windows;

public partial class WhatsNewWindow : Window
{
    public WhatsNewWindow(Window owner, string version, string markdown, IMarkdownRenderer renderer)
    {
        InitializeComponent();
        Owner = owner;

        HeadingText.Text = $"What's new in v{version}";
        var notes = MarkdownRenderer.StripLeadingVersionHeading(markdown);
        renderer.RenderTo(NotesViewer, notes);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => DialogResult = true;

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

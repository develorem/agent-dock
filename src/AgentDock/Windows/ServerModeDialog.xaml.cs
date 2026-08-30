using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using AgentDock.Services.Abstractions;
using AgentDock.Services.Remote;

namespace AgentDock.Windows;

/// <summary>
/// Collects the port and bind scope for server mode, and makes the user acknowledge what hosting
/// allows the first time they do it.
///
/// The port is validated by attempting the bind, not by consulting a table of listeners: anything
/// else races with whatever else on the machine is starting up. When the chosen port is taken the
/// dialog offers the next free one rather than just refusing.
/// </summary>
public partial class ServerModeDialog : Window
{
    private const string AcknowledgedKey = "RemoteServerWarningAcknowledged";

    public sealed record Result(int Port, RemoteBindScope Scope);

    public Result? Outcome { get; private set; }

    private readonly IAppSettingsStore _appSettings;

    private ServerModeDialog(IAppSettingsStore appSettings, int defaultPort)
    {
        _appSettings = appSettings;

        InitializeComponent();

        PortBox.Text = defaultPort.ToString();

        // The warning is a one-time gate, not a nag: once acknowledged it stays acknowledged.
        var alreadyAcknowledged = _appSettings.GetString(AcknowledgedKey) == "true";
        AcknowledgeCheck.IsChecked = alreadyAcknowledged;

        UpdateStartEnabled();
    }

    public static Result? Show(Window owner, IAppSettingsStore appSettings, int defaultPort)
    {
        var dialog = new ServerModeDialog(appSettings, defaultPort) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Outcome : null;
    }

    private static readonly Regex NonDigit = new("[^0-9]", RegexOptions.Compiled);

    private void PortBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var cleaned = NonDigit.Replace(PortBox.Text, "");
        if (cleaned != PortBox.Text)
        {
            var caret = PortBox.CaretIndex;
            PortBox.Text = cleaned;
            PortBox.CaretIndex = Math.Min(caret, cleaned.Length);
        }

        UpdateStartEnabled();
    }

    private void Acknowledge_Changed(object sender, RoutedEventArgs e) => UpdateStartEnabled();

    private void UpdateStartEnabled()
    {
        // StartButton is null while InitializeComponent is still running.
        if (StartButton == null) return;

        StartButton.IsEnabled = AcknowledgeCheck.IsChecked == true && TryReadPort(out _);
    }

    private bool TryReadPort(out int port)
        => int.TryParse(PortBox.Text, out port) && port is > 0 and <= 65535;

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadPort(out var port))
        {
            ShowError("Enter a port between 1 and 65535.");
            return;
        }

        var scope = AllInterfacesCheck.IsChecked == true
            ? RemoteBindScope.AllInterfaces
            : RemoteBindScope.LoopbackOnly;

        var address = scope == RemoteBindScope.AllInterfaces ? IPAddress.Any : IPAddress.Loopback;

        // Bind-test here so the user is told before the dialog closes, and is offered a way out
        // rather than just an error.
        if (!RemoteServerService.IsPortAvailable(address, port))
        {
            var free = RemoteServerService.FindFreePort(address, port + 1);
            if (free.HasValue)
            {
                ShowError($"Port {port} is in use. Port {free.Value} is free — press Start Server again to use it.");
                PortBox.Text = free.Value.ToString();
            }
            else
            {
                ShowError($"Port {port} is in use, and no free port was found nearby.");
            }

            return;
        }

        if (AcknowledgeCheck.IsChecked == true)
            _appSettings.SetString(AcknowledgedKey, "true");

        Outcome = new Result(port, scope);
        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}

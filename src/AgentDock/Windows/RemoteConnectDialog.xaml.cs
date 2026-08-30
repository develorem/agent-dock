using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using AgentDock.Services.Abstractions;
using AgentDock.Services.Remote;

namespace AgentDock.Windows;

/// <summary>
/// Connects to a hosting Agent Dock, in the two phases the feature was described in: find the
/// machine first, then prove who you are.
///
/// Splitting it matters for more than tidiness. Asking for the pairing code up front would mean
/// typing a secret before knowing whether anything answered, and it would leave no place to show
/// which machine replied or whether its identity has changed since last time.
/// </summary>
public partial class RemoteConnectDialog : Window
{
    private const string LastHostKey = "RemoteLastHost";
    private const string LastPortKey = "RemoteLastPort";

    private readonly RemoteClientService _client;
    private readonly IAppSettingsStore _appSettings;

    private bool _awaitingCode;
    private bool _busy;

    private RemoteConnectDialog(RemoteClientService client, IAppSettingsStore appSettings)
    {
        _client = client;
        _appSettings = appSettings;

        InitializeComponent();

        // Reconnecting to the same machine is the common case, so remember where you were.
        HostBox.Text = _appSettings.GetString(LastHostKey);
        var savedPort = _appSettings.GetString(LastPortKey);
        PortBox.Text = string.IsNullOrEmpty(savedPort)
            ? RemoteProtocol.DefaultPort.ToString()
            : savedPort;

        Loaded += (_, _) => HostBox.Focus();
        UpdatePrimaryEnabled();
    }

    /// <summary>Returns true when the client ended up connected and paired.</summary>
    public static bool Show(Window owner, RemoteClientService client, IAppSettingsStore appSettings)
    {
        var dialog = new RemoteConnectDialog(client, appSettings) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private static readonly Regex NonDigit = new("[^0-9]", RegexOptions.Compiled);

    private void Address_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ReferenceEquals(sender, PortBox))
        {
            var cleaned = NonDigit.Replace(PortBox.Text, "");
            if (cleaned != PortBox.Text)
            {
                var caret = PortBox.CaretIndex;
                PortBox.Text = cleaned;
                PortBox.CaretIndex = Math.Min(caret, cleaned.Length);
            }
        }

        UpdatePrimaryEnabled();
    }

    private void Code_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdatePrimaryEnabled();

    private void UpdatePrimaryEnabled()
    {
        if (PrimaryButton == null) return;

        if (_busy)
        {
            PrimaryButton.IsEnabled = false;
            return;
        }

        PrimaryButton.IsEnabled = _awaitingCode
            ? PairingCode.Normalize(CodeBox.Text).Length == PairingCode.Length
            : !string.IsNullOrWhiteSpace(HostBox.Text)
              && int.TryParse(PortBox.Text, out var port)
              && port is > 0 and <= 65535;
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!_awaitingCode)
            await ConnectAsync();
        else
            await PairAsync();
    }

    private async Task ConnectAsync()
    {
        var host = HostBox.Text.Trim();
        if (!int.TryParse(PortBox.Text, out var port)) return;

        SetBusy(true, $"Connecting to {host}:{port}…");

        var outcome = await _client.ConnectAsync(host, port, Dispatcher);

        SetBusy(false, null);

        if (!outcome.NeedsCode)
        {
            ShowStatus(outcome.Message ?? DisconnectReasonText.Describe(outcome.Reason), isError: true);
            return;
        }

        _appSettings.SetString(LastHostKey, host);
        _appSettings.SetString(LastPortKey, port.ToString());

        _awaitingCode = true;
        AddressPanel.Visibility = Visibility.Collapsed;
        CodePanel.Visibility = Visibility.Visible;
        PrimaryButton.Content = "Pair";

        FoundServerText.Text = $"Enter the pairing code for {outcome.ServerName}";
        IdentityText.Text = $"Connected to '{outcome.ServerName}' at {host}:{port}.";
        FingerprintText.Text = $"Identity: {outcome.Fingerprint}";
        FingerprintWarning.Visibility = outcome.FingerprintChanged ? Visibility.Visible : Visibility.Collapsed;

        ShowStatus(null, isError: false);
        CodeBox.Focus();
        UpdatePrimaryEnabled();
    }

    private async Task PairAsync()
    {
        SetBusy(true, "Pairing…");

        var outcome = await _client.SubmitCodeAsync(CodeBox.Text);

        SetBusy(false, null);

        if (!outcome.Success)
        {
            ShowStatus(outcome.Message ?? DisconnectReasonText.Describe(outcome.Reason), isError: true);

            // A rejected code means starting over: the server has closed the connection, so the
            // address phase has to run again rather than letting the user retype into a dead socket.
            _awaitingCode = false;
            AddressPanel.Visibility = Visibility.Visible;
            CodePanel.Visibility = Visibility.Collapsed;
            PrimaryButton.Content = "Connect";
            CodeBox.Text = "";
            UpdatePrimaryEnabled();
            return;
        }

        DialogResult = true;
        Close();
    }

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        Cursor = busy ? Cursors.Wait : null;
        if (message != null) ShowStatus(message, isError: false);
        UpdatePrimaryEnabled();
    }

    private void ShowStatus(string? message, bool isError)
    {
        StatusText.Text = message ?? "";
        StatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("GitUnstagedForeground")
            : (System.Windows.Media.Brush)FindResource("HelpMutedForeground");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // Cancelling mid-handshake must not leave a half-open connection behind.
        if (_awaitingCode) _client.Disconnect();

        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}

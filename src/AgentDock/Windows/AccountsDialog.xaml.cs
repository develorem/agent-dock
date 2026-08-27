using System.Windows;
using System.Windows.Input;
using AgentDock.Services;

namespace AgentDock.Windows;

public partial class AccountsDialog : Window
{
    /// <summary>Row shown in the accounts list.</summary>
    private sealed class AccountRow
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string StatusText { get; init; } = "";
        public string ConfigDir { get; init; } = "";
    }

    // Cancelled when the dialog closes, so a sign-in we're still waiting on doesn't keep
    // a watcher (and a reference to dead visuals) alive. The login process itself is left
    // running — the user may still be mid-flow in the browser.
    private readonly CancellationTokenSource _closing = new();

    private AccountsDialog()
    {
        InitializeComponent();
        Populate();
        Closed += (_, _) => _closing.Cancel();
    }

    /// <summary>Opens the accounts manager. Changes are persisted by AccountManager directly.</summary>
    public static void Show(Window owner)
    {
        var dlg = new AccountsDialog { Owner = owner };
        dlg.ShowDialog();
    }

    private void Populate()
    {
        var rows = AccountManager.Load().Select(a =>
        {
            // Login state decides the wording; the email is only a label. A revoked
            // account keeps its email in .claude.json, so reading the email first (as
            // this used to) rendered dead accounts as perfectly signed in — leaving no
            // hint that Log In was the thing to click.
            var email = AccountManager.ReadEmail(a.Id);
            var status = AccountManager.IsLoggedIn(a.Id)
                ? email != null ? $"— {email}" : "— signed in"
                : email != null
                    ? $"— {email} · signed out (click Log In)"
                    : "— not signed in (click Log In)";

            return new AccountRow
            {
                Id = a.Id,
                Name = a.Name,
                StatusText = status,
                ConfigDir = AccountManager.ConfigDirFor(a.Id)
            };
        }).ToList();

        AccountsList.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NewAccountName.Text.Trim();
        if (name.Length == 0)
        {
            NewAccountName.Focus();
            return;
        }

        var account = AccountManager.Add(name);
        NewAccountName.Text = "";
        Populate();
        await RunLoginAsync(account.Id, account.Name);
    }

    /// <summary>
    /// Runs a sign-in and refreshes the list when it finishes, so the user never has to
    /// click Refresh to find out whether it worked. Status is reported inline rather than
    /// in a message box: a modal here would sit on top of the very list it's telling the
    /// user to go and look at.
    /// </summary>
    private async Task RunLoginAsync(string accountId, string accountName)
    {
        LogInButton.IsEnabled = false;
        RemoveButton.IsEnabled = false;
        AddButton.IsEnabled = false;
        LoginStatus.Text = $"Signing in to \"{accountName}\" — complete it in the terminal window…";
        LoginStatus.Visibility = Visibility.Visible;

        bool signedIn;
        try
        {
            signedIn = await AccountManager.RunLoginAsync(accountId, _closing.Token);
        }
        finally
        {
            // The dialog may already be gone; touching dead visuals is harmless but
            // pointless, and re-enabling buttons on a closed window is not worth guarding
            // separately from the status text.
            LogInButton.IsEnabled = true;
            RemoveButton.IsEnabled = true;
            AddButton.IsEnabled = true;
        }

        Populate();
        LoginStatus.Text = signedIn
            ? $"\"{accountName}\" is signed in."
            : $"\"{accountName}\" is still not signed in — click Log In to try again.";
    }

    private void NewAccountName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            AddButton_Click(sender, e);
    }

    private async void LogInButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountRow row)
        {
            ThemedMessageBox.Show(this, "Select an account first.", "Log in",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunLoginAsync(row.Id, row.Name);
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountRow row)
            return;

        var result = ThemedMessageBox.Show(
            this,
            $"Remove account \"{row.Name}\"?\n\nThis deletes its saved login and config folder for Agent Dock. Your Claude subscription itself is unaffected.",
            "Remove account",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        AccountManager.Remove(row.Id, deleteFiles: true);
        Populate();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Populate();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

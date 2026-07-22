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

    private AccountsDialog()
    {
        InitializeComponent();
        Populate();
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
            var email = AccountManager.ReadEmail(a.Id);
            var status = email != null
                ? $"— {email}"
                : AccountManager.IsLoggedIn(a.Id)
                    ? "— signed in"
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

    private void AddButton_Click(object sender, RoutedEventArgs e)
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
        AccountManager.LaunchLogin(account.Id);

        ThemedMessageBox.Show(
            this,
            $"A terminal window has opened to sign in to \"{account.Name}\".\n\n" +
            "Complete the Claude login there, then close that window and click Refresh to confirm the account is signed in.",
            "Sign in",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void NewAccountName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            AddButton_Click(sender, e);
    }

    private void LogInButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountRow row)
        {
            ThemedMessageBox.Show(this, "Select an account first.", "Log in",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AccountManager.LaunchLogin(row.Id);
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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AgentDock.Services;

using AgentDock.Services.Abstractions;

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

    // Same palette ProjectSettingsDialog/GroupSettingsDialog use for icon colours, so a
    // colour swatch looks the same wherever one appears in the app. null = "Default" —
    // falls back to the palette colour MainWindow assigns by the account's position.
    private static readonly string?[] ColorPresets =
    [
        null,
        "#E74C3C", "#E67E22", "#F1C40F", "#27AE60", "#16A085",
        "#2980B9", "#5B3CC4", "#8E44AD", "#E91E63", "#7F8C8D", "#BDC3C7",
    ];

    // Cancelled when the dialog closes, so a sign-in we're still waiting on doesn't keep
    // a watcher (and a reference to dead visuals) alive. The login process itself is left
    // running — the user may still be mid-flow in the browser.
    private readonly CancellationTokenSource _closing = new();

    private readonly IAccountManager _accounts;
    private readonly Action? _onAccountsChanged;

    private string? _colorsAccountId;
    private string? _selectedLightColor;
    private string? _selectedDarkColor;
    private Border? _selectedLightSwatch;
    private Border? _selectedDarkSwatch;

    private AccountsDialog(IAccountManager accountManager, Action? onAccountsChanged)
    {
        _accounts = accountManager;
        _onAccountsChanged = onAccountsChanged;
        InitializeComponent();
        Populate();
        Closed += (_, _) => _closing.Cancel();
    }

    /// <summary>
    /// Opens the accounts manager. Changes are persisted by AccountManager directly;
    /// <paramref name="onAccountsChanged"/> (if given) is invoked after each change so the
    /// owner can refresh anything already showing an account's name/colour (the usage
    /// popup, chat panel header tints) without waiting for its own next periodic refresh.
    /// </summary>
    public static void Show(Window owner, IAccountManager accounts, Action? onAccountsChanged = null)
    {
        var dlg = new AccountsDialog(accounts, onAccountsChanged) { Owner = owner };
        dlg.ShowDialog();
    }

    private void Populate()
    {
        var rows = _accounts.Load().Select(a =>
        {
            // Login state decides the wording; the email is only a label. A revoked
            // account keeps its email in .claude.json, so reading the email first (as
            // this used to) rendered dead accounts as perfectly signed in — leaving no
            // hint that Log In was the thing to click.
            var email = _accounts.ReadEmail(a.Id);
            var status = _accounts.IsLoggedIn(a.Id)
                ? email != null ? $"— {email}" : "— signed in"
                : email != null
                    ? $"— {email} · signed out (click Log In)"
                    : "— not signed in (click Log In)";

            return new AccountRow
            {
                Id = a.Id,
                Name = a.Name,
                StatusText = status,
                ConfigDir = _accounts.ConfigDirFor(a.Id)
            };
        }).ToList();

        AccountsList.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // The colour section targets one account by id; if that account no longer exists
        // (just removed) there's nothing left to edit.
        if (_colorsAccountId != null && rows.All(r => r.Id != _colorsAccountId))
            CloseColorSection();
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NewAccountName.Text.Trim();
        if (name.Length == 0)
        {
            NewAccountName.Focus();
            return;
        }

        var account = _accounts.Add(name);
        NewAccountName.Text = "";
        Populate();
        _onAccountsChanged?.Invoke();
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
            signedIn = await _accounts.RunLoginAsync(accountId, _closing.Token);
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
        _onAccountsChanged?.Invoke();
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

        _accounts.Remove(row.Id, deleteFiles: true);
        Populate();
        _onAccountsChanged?.Invoke();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Populate();

    // Keeps an open colour section pointed at whichever row is selected, rather than
    // silently going on editing an account that's no longer highlighted.
    private void AccountsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ColorSection.Visibility != Visibility.Visible)
            return;

        if (AccountsList.SelectedItem is AccountRow row)
            OpenColorSection(row);
        else
            CloseColorSection();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // --- Colour picker ---

    private void ColorsButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountRow row)
        {
            ThemedMessageBox.Show(this, "Select an account first.", "Colors",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenColorSection(row);
    }

    private void OpenColorSection(AccountRow row)
    {
        var account = _accounts.Load().FirstOrDefault(a => a.Id == row.Id);
        if (account == null)
            return;

        _colorsAccountId = row.Id;
        _selectedLightColor = account.AccentColorLight;
        _selectedDarkColor = account.AccentColorDark;
        _selectedLightSwatch = null;
        _selectedDarkSwatch = null;

        ColorSectionTitle.Text = $"Colors for \"{row.Name}\"";

        LightSwatchPanel.Children.Clear();
        DarkSwatchPanel.Children.Clear();
        foreach (var color in ColorPresets)
        {
            var lightSwatch = CreateSwatch(color, isDark: false);
            if (ColorsEqual(color, _selectedLightColor))
                SelectSwatch(lightSwatch, color, isDark: false, persist: false);
            LightSwatchPanel.Children.Add(lightSwatch);

            var darkSwatch = CreateSwatch(color, isDark: true);
            if (ColorsEqual(color, _selectedDarkColor))
                SelectSwatch(darkSwatch, color, isDark: true, persist: false);
            DarkSwatchPanel.Children.Add(darkSwatch);
        }

        ColorSection.Visibility = Visibility.Visible;
    }

    private void CloseColorSection()
    {
        _colorsAccountId = null;
        _selectedLightSwatch = null;
        _selectedDarkSwatch = null;
        ColorSection.Visibility = Visibility.Collapsed;
    }

    private static bool ColorsEqual(string? a, string? b) =>
        a == null ? b == null : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private Border CreateSwatch(string? color, bool isDark)
    {
        const double size = 22.0;
        UIElement content;

        if (color == null)
        {
            // "Default" swatch — dotted border circle with no fill, matches
            // ProjectSettingsDialog's convention for "use the app's default".
            content = new Ellipse
            {
                Width = size - 4,
                Height = size - 4,
                Stroke = (Brush)FindResource("HelpForeground"),
                StrokeThickness = 1.2,
                StrokeDashArray = [2, 2],
                Fill = Brushes.Transparent
            };
        }
        else
        {
            content = new Ellipse
            {
                Width = size - 4,
                Height = size - 4,
                Fill = ParseHexBrush(color) ?? Brushes.Gray,
                Stroke = Brushes.Transparent,
                StrokeThickness = 0
            };
        }

        var swatch = new Border
        {
            Width = size + 4,
            Height = size + 4,
            CornerRadius = new CornerRadius((size + 4) / 2),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Margin = new Thickness(2),
            Cursor = Cursors.Hand,
            Child = content,
            Tag = color,
            ToolTip = color ?? "Default"
        };

        swatch.MouseLeftButtonDown += (_, _) => SelectSwatch(swatch, color, isDark, persist: true);

        Func<Border?> selectedRef = isDark ? () => _selectedDarkSwatch : () => _selectedLightSwatch;
        swatch.MouseEnter += (_, _) =>
        {
            if (swatch != selectedRef())
                swatch.Background = (Brush)FindResource("MenuHighlightBackground");
        };
        swatch.MouseLeave += (_, _) =>
        {
            if (swatch != selectedRef())
                swatch.Background = Brushes.Transparent;
        };

        return swatch;
    }

    private void SelectSwatch(Border swatch, string? color, bool isDark, bool persist)
    {
        var previous = isDark ? _selectedDarkSwatch : _selectedLightSwatch;
        if (previous != null)
        {
            previous.BorderBrush = Brushes.Transparent;
            previous.Background = Brushes.Transparent;
        }

        swatch.BorderBrush = (Brush)FindResource("TabButtonActiveBorderBrush");
        swatch.Background = (Brush)FindResource("MenuHighlightBackground");

        if (isDark)
        {
            _selectedDarkSwatch = swatch;
            _selectedDarkColor = color;
        }
        else
        {
            _selectedLightSwatch = swatch;
            _selectedLightColor = color;
        }

        if (persist)
            SaveColors();
    }

    private void SaveColors()
    {
        if (_colorsAccountId == null)
            return;

        var accounts = _accounts.Load();
        var account = accounts.FirstOrDefault(a => a.Id == _colorsAccountId);
        if (account == null)
            return;

        account.AccentColorLight = _selectedLightColor;
        account.AccentColorDark = _selectedDarkColor;
        _accounts.Save(accounts);
        _onAccountsChanged?.Invoke();
    }

    private void DoneColors_Click(object sender, RoutedEventArgs e) => CloseColorSection();

    private static SolidColorBrush? ParseHexBrush(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(color);
        }
        catch
        {
            return null;
        }
    }
}

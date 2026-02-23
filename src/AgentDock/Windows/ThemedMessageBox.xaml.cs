using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentDock.Windows;

public partial class ThemedMessageBox : Window
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    private ThemedMessageBox()
    {
        InitializeComponent();
    }

    public static MessageBoxResult Show(
        Window owner,
        string message,
        string title = "Agent Dock",
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None)
    {
        var dialog = new ThemedMessageBox
        {
            Owner = owner
        };

        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.SetIcon(icon);
        dialog.SetButtons(buttons);

        dialog.ShowDialog();
        return dialog.Result;
    }

    private void SetIcon(MessageBoxImage icon)
    {
        var (glyph, color) = icon switch
        {
            MessageBoxImage.Error => ("\uEA39", "#D03030"),       // ErrorBadge
            MessageBoxImage.Warning => ("\uE7BA", "#D08B20"),     // Warning
            MessageBoxImage.Information => ("\uE946", "#2060B0"), // Info
            MessageBoxImage.Question => ("\uE9CE", "#7B68AE"),   // Unknown/help
            _ => (null, (string?)null)
        };

        if (glyph != null)
        {
            IconText.Text = glyph;
            IconText.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color!));
        }
        else
        {
            IconText.Visibility = Visibility.Collapsed;
        }
    }

    private void SetButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OK:
                OkButton.Visibility = Visibility.Visible;
                OkButton.IsDefault = true;
                break;
            case MessageBoxButton.OKCancel:
                OkButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Visible;
                OkButton.IsDefault = true;
                CancelButton.IsCancel = true;
                break;
            case MessageBoxButton.YesNo:
                YesButton.Visibility = Visibility.Visible;
                NoButton.Visibility = Visibility.Visible;
                YesButton.IsDefault = true;
                break;
            case MessageBoxButton.YesNoCancel:
                YesButton.Visibility = Visibility.Visible;
                NoButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Visible;
                YesButton.IsDefault = true;
                CancelButton.IsCancel = true;
                break;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.Yes;
        DialogResult = true;
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.No;
        DialogResult = false;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.OK;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.Cancel;
        DialogResult = false;
    }
}

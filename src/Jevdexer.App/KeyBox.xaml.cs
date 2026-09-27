using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Jevdexer.Core.Remote;

namespace Jevdexer.App;

/// <summary>
/// The user's Vercel AI Gateway key: paste and save (checked first, free, against the
/// Gateway's credit balance), or, once saved, masked with its status and a way to remove it.
/// Saving a working key turns Smart search on. Used on the Settings Smart search page and in the tour.
/// </summary>
public partial class KeyBox : UserControl
{
    public KeyBox()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = Refresh();
    }

    /// <summary>Shows the saved key's state, or the entry when there is none.</summary>
    public async Task Refresh()
    {
        var key = App.Instance.Settings.GatewayKey;
        EntryPanel.Visibility = key.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedPanel.Visibility = key.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (key.Length == 0) return;

        Masked.Text = key.Length > 10 ? $"{key[..4]}••••{key[^4..]}" : "••••";
        SetStatus("Checking...", "MutedForeground");
        var (state, balance) = await App.Instance.CheckKeyAsync();
        switch (state)
        {
            case JevClient.KeyState.Ok:
                SetStatus(balance is { } b ? $"Works · ${b:0.00} left" : "Works", "Success");
                break;
            case JevClient.KeyState.NoCredits:
                SetStatus("No credits left", "Destructive");
                break;
            case JevClient.KeyState.Rejected:
                SetStatus("Not accepted", "Destructive");
                break;
            default:
                SetStatus("Can't check (offline?)", "MutedForeground");
                break;
        }
    }

    private void SetStatus(string text, string brush)
    {
        StatusText.Text = text;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var key = Entry.Password.Trim();
        if (key.Length == 0) return;
        SaveButton.IsEnabled = false;
        SaveText.Text = "Checking...";
        EntryError.Visibility = Visibility.Collapsed;

        var (state, _) = await JevClient.CheckKeyAsync(key);
        SaveText.Text = "Save";
        SaveButton.IsEnabled = true;
        if (state == JevClient.KeyState.Rejected)
        {
            EntryError.Text = "That key was not accepted. Copy it again from Vercel: AI Gateway, API keys.";
            EntryError.Visibility = Visibility.Visible;
            return;
        }

        // Works, has no credits yet, or cannot be checked right now: keep it, the status says which.
        Entry.Clear();
        var app = App.Instance;
        if (!app.Settings.SmartSearch) app.Settings.SmartSearch = true; // a key means the user wants it
        app.SetGatewayKey(key);
        await Refresh();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.SetGatewayKey("");
        await Refresh();
    }

    private void Entry_PasswordChanged(object sender, RoutedEventArgs e)
    {
        EntryHint.Visibility = Entry.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = Entry.Password.Trim().Length > 0;
    }

    private void Entry_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SaveButton.IsEnabled) Save_Click(sender, e);
    }

    private void Entry_Focus(object sender, KeyboardFocusChangedEventArgs e) =>
        EntryBox.SetResourceReference(Border.BorderBrushProperty, Entry.IsKeyboardFocused ? "Ring" : "Input");

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}

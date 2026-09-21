using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NfaLoader.Controls;
using NfaLoader.Localization;
using NfaLoader.Services;

namespace NfaLoader.Pages;

/// <summary>
/// One window for a replacement: it opens with a spinner while nfa.pub checks the account and turns into the result.
/// The user can hide it while the check runs; the result then goes to the status bar instead.
/// </summary>
internal sealed class NfaReplaceDialog
{
    private readonly ContentDialog _dialog;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _open;
    private bool _finished;
    private bool _showFailed;

    public NfaReplaceDialog(XamlRoot xamlRoot, string steamId)
    {
        _dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            CloseButtonText = Loc.T("Nfa_Replace_Hide"),
            Content = Checking(steamId),
        };
        NfaDialogParts.StretchLoneButton(_dialog);
        _dialog.Closed += (_, _) =>
        {
            _open = false;
            _closed.TrySetResult();
            if (!_finished)
            {
                AppState.ShowStatus(Loc.T("Nfa_Replace_Working"), InfoBarSeverity.Informational);
            }
        };
    }

    public async void Show()
    {
        _open = true;
        try
        {
            await _dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // Another dialog was already open. The check still runs, and the result goes to the status bar.
            AppLog.Warn($"Could not show the replacement window: {ex.Message}");
            _open = false;
            _showFailed = true;
            _closed.TrySetResult();
            AppState.ShowStatus(Loc.T("Nfa_Replace_Working"), InfoBarSeverity.Informational);
        }
    }

    /// <summary>
    /// Shows the result in the window. A window the user hid comes back with it, because the status bar closes on its
    /// own and the check can take minutes. Returns false only when the window cannot be shown, so the caller reports
    /// it elsewhere.
    /// </summary>
    public bool TryShowResult(InfoBarSeverity severity, string title, string message, UIElement? extra = null)
    {
        _finished = true;
        if (_showFailed)
        {
            return false;
        }

        var panel = new StackPanel { Spacing = 14, Width = NfaDialogParts.Width, Padding = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(new FontIcon
        {
            Glyph = severity switch
            {
                InfoBarSeverity.Success => "",
                InfoBarSeverity.Warning => "",
                InfoBarSeverity.Error => "",
                _ => "",
            },
            FontSize = 44,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = NfaDialogParts.Resource(severity switch
            {
                InfoBarSeverity.Success => "SystemFillColorSuccessBrush",
                InfoBarSeverity.Warning => "SystemFillColorCautionBrush",
                InfoBarSeverity.Error => "SystemFillColorCriticalBrush",
                _ => "AccentTextFillColorPrimaryBrush",
            }),
        });
        panel.Children.Add(Heading(title, message));
        if (extra is not null)
        {
            panel.Children.Add(extra);
        }

        _dialog.Content = panel;
        _dialog.CloseButtonText = Loc.T("Nfa_Replace_Close");
        if (!_open)
        {
            _ = ReopenAsync(severity, message);
        }

        return true;
    }

    private async Task ReopenAsync(InfoBarSeverity severity, string message)
    {
        _open = true;
        try
        {
            await _dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // Another dialog is open by now, so the status bar is the only place left.
            AppLog.Warn($"Could not bring back the replacement window: {ex.Message}");
            _open = false;
            AppState.ShowStatus(message, severity);
        }
    }

    /// <summary>The new account in a tile: the Steam logo, its Steam ID, and what the order has left.</summary>
    public static UIElement NewAccount(string steamId, int remaining)
    {
        var id = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        id.Children.Add(NfaDialogParts.SteamLogo(16));
        id.Children.Add(new TextBlock
        {
            Text = steamId,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            IsTextSelectionEnabled = true,
        });

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("Nfa_Replace_NewAccount"),
            Foreground = NfaDialogParts.Resource("TextFillColorSecondaryBrush"),
        });
        content.Children.Add(id);
        content.Children.Add(NfaDialogParts.Divider());
        content.Children.Add(NfaDialogParts.Row(
            Loc.T("Nfa_Replace_LeftLabel"),
            Loc.Tf("Nfa_Replace_Left_Format", remaining, NfaWarranty.ReplacementsPerOrder)));

        return new Border
        {
            Padding = new Thickness(16, 12, 16, 14),
            CornerRadius = new CornerRadius(6),
            Background = NfaDialogParts.Resource("SubtleFillColorSecondaryBrush"),
            Child = content,
        };
    }

    public async Task CloseAsync()
    {
        _finished = true;
        if (_open)
        {
            _dialog.Hide();
            await _closed.Task;
        }
    }

    private static StackPanel Checking(string steamId)
    {
        var panel = new StackPanel { Spacing = 18, Width = NfaDialogParts.Width, Padding = new Thickness(0, 12, 0, 4) };
        panel.Children.Add(new ProgressRing { IsActive = true, Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(Heading(
            Loc.T("Nfa_Replace_Checking_Title"),
            Loc.Tf("Nfa_Replace_Checking_Message_Format", steamId)));
        return panel;
    }

    private static StackPanel Heading(string title, string message)
    {
        var heading = new StackPanel { Spacing = 6 };
        heading.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        heading.Children.Add(new TextBlock
        {
            Text = message,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = NfaDialogParts.Resource("TextFillColorSecondaryBrush"),
        });
        return heading;
    }
}

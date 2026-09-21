using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NfaLoader.Localization;
using Windows.ApplicationModel.DataTransfer;

namespace NfaLoader.Services;

/// <summary>
/// Keeps an account nfa.pub has already delivered when saving it to History fails. The account is paid for or the
/// replacement is used up, so it must survive: it goes to the clipboard and is appended to a file in the data folder.
/// The message states only what actually worked, and if nothing did, the line is shown in a dialog to copy by hand.
/// </summary>
internal static class NfaAccountRescue
{
    public const string FileName = "recovered-accounts.txt";

    /// <param name="kind">"Buy" or "Replace": picks the message wording.</param>
    public static async Task PreserveAsync(XamlRoot? xamlRoot, string kind, string steamId, string token, string context)
    {
        var line = $"{steamId}----{token}";
        var copied = TryCopy(line, steamId, context);
        var path = TryWriteFile(line, steamId, context);

        if (path is not null)
        {
            AppState.ShowStatus(Loc.Tf($"Nfa_{kind}_SaveFailed_File_Format", steamId, path), InfoBarSeverity.Error);
            return;
        }

        if (copied)
        {
            AppState.ShowStatus(Loc.Tf($"Nfa_{kind}_SaveFailed_Clipboard_Format", steamId), InfoBarSeverity.Error);
            return;
        }

        // Nowhere else holds this line, and the status bar closes on its own, so it goes in a dialog that stays open.
        AppState.ShowStatus(Loc.Tf($"Nfa_{kind}_SaveLost_Format", steamId), InfoBarSeverity.Error);
        if (xamlRoot is null)
        {
            return;
        }

        try
        {
            var box = new TextBox { Text = line, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinWidth = 420 };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = Loc.Tf($"Nfa_{kind}_SaveLost_Format", steamId), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(box);

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = Loc.T("Nfa_Rescue_Title"),
                Content = panel,
                CloseButtonText = Loc.T("Nfa_Rescue_Close"),
            };
            Controls.NfaDialogParts.StretchLoneButton(dialog);
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not show the unsaved account {steamId} ({context})", ex);
        }
    }

    private static bool TryCopy(string line, string steamId, string context)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(line);
            Clipboard.SetContent(package);
            try
            {
                // Without Flush the clipboard empties when the app exits.
                Clipboard.Flush();
            }
            catch (COMException)
            {
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not copy {steamId} ({context}) to the clipboard: {ex.Message}");
            return false;
        }
    }

    private static string? TryWriteFile(string line, string steamId, string context)
    {
        try
        {
            var folder = AppState.SettingsService.AppFolderPath;
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FileName);
            File.AppendAllText(path, line + Environment.NewLine);
            AppLog.Warn($"Wrote {steamId} ({context}) to {FileName} because it could not be saved to History");
            return path;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not write {steamId} ({context}) to {FileName}", ex);
            return null;
        }
    }
}

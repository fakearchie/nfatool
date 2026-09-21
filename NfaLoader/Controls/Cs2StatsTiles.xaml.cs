using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NfaLoader.Localization;
using NfaLoader.Models;
using NfaLoader.Services;

namespace NfaLoader.Controls;

/// <summary>The Premier rating and CS2 level tiles, shared by the Login card and the History details.</summary>
public sealed partial class Cs2StatsTiles : UserControl
{
    private int _shownLevelImage = -1;

    public Cs2StatsTiles()
    {
        InitializeComponent();
        ShowPlaceholder(Loc.T("Login_Value_NotQueried"));
    }

    /// <summary>Dashes in both tiles, with the same text under each ("Not yet fetched", "Fetching").</summary>
    public void ShowPlaceholder(string text)
    {
        SetLabels();
        PremierBadge.Rating = null;
        PremierDetail.Text = text;
        ShowLevel(null, text);
    }

    /// <summary>What a saved account knows, or the placeholder when it was never fetched.</summary>
    internal void Show(SteamAccountHistoryItem account)
    {
        if (account.CsStatusUpdatedAt is null && account.PremierScoreUpdatedAt is null && string.IsNullOrWhiteSpace(account.CompetitiveScore))
        {
            ShowPlaceholder(Loc.T("Login_Value_NotQueried"));
            return;
        }

        Show(
            account.PremierScore is > 0 ? account.PremierScore : null,
            account.PremierScore is > 0 ? account.PremierWins : null,
            account.CsPlayerLevel);
    }

    internal void Show(CsPremierScoreResult score) => Show(
        score.HasPremierScore ? checked((int)score.PremierRanking!.RankId) : null,
        score.HasPremierScore ? checked((int)score.PremierRanking!.Wins) : null,
        score.PlayerLevel);

    private void Show(int? rating, int? wins, int? level)
    {
        SetLabels();
        PremierBadge.Rating = rating;
        PremierDetail.Text = rating is null
            ? Loc.T("Login_Card_NoRating")
            : wins == 1 ? Loc.T("Login_Card_OneWin") : Loc.Tf("Login_Card_Wins_Format", wins ?? 0);
        ShowLevel(level, Loc.T("Cs_PlayerLevel_NotRead"));
    }

    private void SetLabels()
    {
        PremierLabel.Text = Loc.T("Login_Card_Premier");
        LevelLabel.Text = Loc.T("Login_Label_CsLevel");
    }

    private void ShowLevel(int? level, string unknownText)
    {
        // Unknown shows dashes, like the Premier badge does, rather than an insignia the account may not have.
        LevelImage.Visibility = level is null ? Visibility.Collapsed : Visibility.Visible;
        LevelText.Visibility = level is null ? Visibility.Collapsed : Visibility.Visible;
        LevelUnknown.Visibility = level is null ? Visibility.Visible : Visibility.Collapsed;

        var image = Math.Clamp(level ?? 0, 0, 40);
        if (level is not null && image != _shownLevelImage)
        {
            _shownLevelImage = image;
            LevelImage.Source = new BitmapImage(new Uri($"ms-appx:///Assets/cs2/levels/level{image}.png"));
        }

        LevelText.Text = level?.ToString() ?? "";
        LevelDetail.Text = level switch
        {
            null => unknownText,
            >= FormatHelper.MinimumPremierLevel => Loc.T("Login_Card_Level_PremierOpen"),
            _ => Loc.Tf("Login_Card_Level_PremierAt_Format", FormatHelper.MinimumPremierLevel),
        };
    }
}

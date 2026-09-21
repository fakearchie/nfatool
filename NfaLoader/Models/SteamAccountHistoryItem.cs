using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using NfaLoader.Localization;
using NfaLoader.Services;

namespace NfaLoader.Models;

// partial: instances cross the WinRT ABI as a ListView ItemsSource, so CsWinRT needs to source-generate the vtable (AOT).
public sealed partial class SteamAccountHistoryItem : INotifyPropertyChanged
{
    public string AccountName { get; set; } = "";

    public string SteamId { get; set; } = "";

    public string EyaToken { get; set; } = "";

    /// <summary>The nfa.pub order this account came from. Needed to replace it.</summary>
    public string? NfaOrder { get; set; }

    /// <summary>When the order was delivered. The 6 hour replacement window starts here.</summary>
    public DateTimeOffset? NfaOrderDeliveredAt { get; set; }

    /// <summary>Replacements the order has left, from nfa.pub's last answer. Null until one is known.</summary>
    public int? NfaReplacementsLeft { get; set; }

    /// <summary>The Steam ID nfa.pub swapped this account for. A swapped account cannot be replaced again.</summary>
    public string? NfaReplacedBy { get; set; }

    public string? PersonaName { get; set; }

    private string? _avatarUrl;

    public string? AvatarUrl
    {
        get => _avatarUrl;
        set
        {
            if (_avatarUrl == value)
            {
                return;
            }

            _avatarUrl = value;
            InvalidateAvatar();
        }
    }

    private string? _avatarPath;

    public string? AvatarPath
    {
        get => _avatarPath;
        set
        {
            if (_avatarPath == value)
            {
                return;
            }

            _avatarPath = value;
            InvalidateAvatar();
        }
    }

    public DateTimeOffset LastLoginAt { get; set; }

    public DateTimeOffset? TokenExpiresAt { get; set; }

    public string? CompetitiveScore { get; set; }

    public string? AccountStatus { get; set; }

    public bool? JwtAvailable { get; set; }

    public string? JwtStatus { get; set; }

    public DateTimeOffset? JwtValidatedAt { get; set; }

    public int? PremierScore { get; set; }

    public int? PremierWins { get; set; }

    public DateTimeOffset? PremierScoreUpdatedAt { get; set; }

    public uint? CooldownSeconds { get; set; }

    public uint? CooldownReason { get; set; }

    public bool? GcVacBanned { get; set; }

    public int? CsPlayerLevel { get; set; }

    public bool? InCsMatch { get; set; }


    public DateTimeOffset? CsStatusUpdatedAt { get; set; }

    private string? _note;

    /// <summary>User note (may be empty). Persisted with the account, editable in the details panel, and included in search.</summary>
    public string? Note
    {
        get => _note;
        set
        {
            if (_note == value)
            {
                return;
            }

            _note = value;
            RaiseNoteVisuals();
        }
    }

    /// <summary>Stable IDs of the groups this account belongs to (group definitions live in settings, so renaming a group does not affect this). Never null.</summary>
    public List<string> GroupIds { get; set; } = [];

    [JsonIgnore]
    public string AccountTitle => string.IsNullOrWhiteSpace(AccountName) ? Loc.T("Account_Title_Unnamed") : AccountName;

    [JsonIgnore]
    public string PersonaDisplayName => string.IsNullOrWhiteSpace(PersonaName) ? Loc.T("Account_Persona_NotSynced") : PersonaName;

    [JsonIgnore]
    public string SteamIdDisplay => string.IsNullOrWhiteSpace(SteamId) ? Loc.T("Account_Steam64_Unresolved") : SteamId;

    [JsonIgnore]
    public string LastLoginText => FormatHelper.FormatDateTime(LastLoginAt);

    [JsonIgnore]
    public string LastLoginShortText => LastLoginAt == default
        ? Loc.T("Account_LastLogin_Unknown")
        : LastLoginAt.LocalDateTime.ToString("MM-dd HH:mm");

    [JsonIgnore]
    public string LastLoginCaptionText => Loc.Tf("Account_LastLogin_Caption_Format", LastLoginShortText);

    [JsonIgnore]
    public string TokenExpiresText => TokenExpiresAt.HasValue
        ? FormatHelper.FormatDateTime(TokenExpiresAt.Value)
        : Loc.T("Account_TokenExpires_Unresolved");

    [JsonIgnore]
    public string CompetitiveScoreText
    {
        get
        {
            if (PremierScore.HasValue)
            {
                return PremierWins.HasValue
                    ? Loc.Tf("Account_Score_WithWins_Format", PremierScore.Value, PremierWins.Value)
                    : string.Format("{0:N0}", PremierScore.Value);
            }

            return string.IsNullOrWhiteSpace(CompetitiveScore) ? Loc.T("Account_Score_Pending") : CompetitiveScore;
        }
    }

    // GcVacBanned is bool?, converted to FormatHelper's int? convention (null unknown / 0 none / non-zero flagged).
    private int? GcVacBannedAsInt => GcVacBanned.HasValue ? (GcVacBanned.Value ? 1 : 0) : null;

    [JsonIgnore]
    public string CooldownText => FormatHelper.FormatCooldownText(CooldownSeconds, CooldownReason, Loc.T("Account_Pending"));

    [JsonIgnore]
    public string CooldownSummaryText => Loc.Tf("Account_Cooldown_Summary_Format", CooldownText);

    [JsonIgnore]
    public string GcVacText => FormatHelper.FormatGcVacText(GcVacBannedAsInt, Loc.T("Account_Pending"));

    [JsonIgnore]
    public string CooldownStatusText =>
        FormatHelper.FormatCooldownStatusText(CooldownSeconds, CooldownReason, GcVacBannedAsInt, Loc.T("Account_Pending"), Loc.T("Account_Pending"));

    // ---------- Cooldown countdown (snapshot seconds left minus elapsed time, counts down live; the page's one-second timer refreshes the bindings) ----------

    /// <summary>Cooldown anchor: the time the query wrote the cooldown snapshot, used to subtract elapsed time.</summary>
    private DateTimeOffset? CooldownAnchor => CsStatusUpdatedAt ?? PremierScoreUpdatedAt;

    /// <summary>
    /// Live cooldown seconds left. Same meaning as <see cref="CooldownSeconds"/>: null = unknown (GC did not answer) / 0 = no cooldown / positive = seconds left.
    /// Elapsed time is subtracted only when the snapshot is in (0, int.MaxValue] and there is an anchor; otherwise the value passes through as is and formatting treats it as unknown/no cooldown.
    /// </summary>
    [JsonIgnore]
    public uint? RemainingCooldownSeconds
    {
        get
        {
            var snapshot = CooldownSeconds;
            if (snapshot is null or 0 || snapshot > int.MaxValue)
            {
                return snapshot;
            }

            var anchor = CooldownAnchor;
            if (anchor is null)
            {
                return snapshot;
            }

            var remaining = snapshot.Value - (DateTimeOffset.Now - anchor.Value).TotalSeconds;
            return remaining <= 0 ? 0u : (uint)remaining;
        }
    }

    /// <summary>Whether the countdown is still running (the page uses this to decide whether to keep refreshing the card every second, and stops the timer once all have expired).</summary>
    [JsonIgnore]
    public bool HasLiveCooldown => CooldownAnchor is not null && RemainingCooldownSeconds is > 0 and <= int.MaxValue;

    [JsonIgnore]
    public string RemainingCooldownText =>
        FormatHelper.FormatCooldownCountdownText(RemainingCooldownSeconds, CooldownReason, Loc.T("Account_Pending"));

    [JsonIgnore]
    public string RemainingCooldownSummaryText => Loc.Tf("Account_Cooldown_Summary_Format", RemainingCooldownText);

    [JsonIgnore]
    public string RemainingCooldownStatusText => Loc.Tf(
        "Format_CooldownStatus_Format",
        RemainingCooldownText,
        FormatHelper.FormatGcVacText(GcVacBannedAsInt, Loc.T("Account_Pending")));

    [JsonIgnore]
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    [JsonIgnore]
    public Visibility NoteIndicatorVisibility => HasNote ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public string CsPlayerLevelText => FormatHelper.FormatPlayerLevelText(CsPlayerLevel, Loc.T("Account_Pending"));

    [JsonIgnore]
    public string InCsMatchText => InCsMatch.HasValue
        ? (InCsMatch.Value ? Loc.T("Account_InMatch_Maybe") : Loc.T("Account_InMatch_None"))
        : Loc.T("Account_Pending");


    [JsonIgnore]
    public string AccountStatusText
    {
        get
        {
            var status = string.IsNullOrWhiteSpace(AccountStatus) ? Loc.T("Account_Pending") : AccountStatus;
            var updatedAt = CsStatusUpdatedAt ?? PremierScoreUpdatedAt;
            if (!updatedAt.HasValue)
            {
                return status;
            }

            return Loc.Tf("Account_Status_WithTime_Format", status, FormatHelper.FormatDateTime(updatedAt.Value));
        }
    }

    [JsonIgnore]
    public string JwtAvailabilityText
    {
        get
        {
            var status = JwtAvailable.HasValue
                ? (JwtAvailable.Value ? Loc.T("Account_Jwt_Valid") : Loc.T("Account_Jwt_Invalid"))
                : JwtStatus;

            if (string.IsNullOrWhiteSpace(status))
            {
                return Loc.T("Account_Pending");
            }

            return JwtValidatedAt.HasValue
                ? Loc.Tf("Account_Status_WithTime_Format", status, FormatHelper.FormatDateTime(JwtValidatedAt.Value))
                : status;
        }
    }

    // Process-wide avatar cache: rebuilding the list creates new instances, so an instance field cannot be reused across rebuilds. Only a static dictionary actually stops the leak.
    // Key = full path, value carries the last write time: if the mtime differs, decode again and "replace" the entry under the same key. Every refresh rewrites the avatar file
    // (so the mtime always changes); if the mtime were part of the key, old decoded bitmaps would stay in the dictionary forever (one leaked per account per refresh).
    // Only accessed on the UI thread (BitmapImage can only be used there too), so a plain Dictionary is enough.
    private static readonly Dictionary<string, (DateTime Mtime, BitmapImage Image)> AvatarCache =
        new(StringComparer.OrdinalIgnoreCase);

    // PersonPicture is shown at most 92px (history details); decode at 2x to leave room for DPI scaling.
    private const int AvatarDecodePixelWidth = 184;

    private BitmapImage? _avatarImage;

    [JsonIgnore]
    public BitmapImage? AvatarImage
    {
        get
        {
            if (_avatarImage is not null)
            {
                return _avatarImage;
            }

            _avatarImage = LoadAvatarImage();
            return _avatarImage;
        }
    }

    // When the avatar source (path/URL) changes, drop the decoded image and tell bindings to fetch again, so the avatar shows up as soon as an async download finishes (no full list rebuild needed).
    private void InvalidateAvatar()
    {
        _avatarImage = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarImage)));
    }

    private BitmapImage? LoadAvatarImage()
    {
        var localPath = AvatarPath;
        if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
        {
            try
            {
                var mtime = File.GetLastWriteTimeUtc(localPath);
                if (AvatarCache.TryGetValue(localPath, out var cached) && cached.Mtime == mtime)
                {
                    return cached.Image;
                }

                // Decode from bytes instead of new BitmapImage(Uri): the latter keeps the file handle open, so the avatar cannot be deleted when the account is removed.
                // Read with FileShare.ReadWrite: even if another thread is replacing the avatar file, no sharing violation is thrown and the avatar does not flicker away.
                byte[] bytes;
                using (var fileStream = new FileStream(
                    localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bytes = new byte[fileStream.Length];
                    fileStream.ReadExactly(bytes);
                }
                var bitmap = new BitmapImage { DecodePixelWidth = AvatarDecodePixelWidth };
                using (var stream = new MemoryStream(bytes))
                {
                    // SetSource reads the whole stream synchronously before returning, so disposing it with using is safe.
                    bitmap.SetSource(stream.AsRandomAccessStream());
                }

                AvatarCache[localPath] = (mtime, bitmap);
                return bitmap;
            }
            catch (Exception ex)
            {
                // Catch-all: a corrupt or non-image file makes SetSource throw COMException, and this getter is called by x:Bind
                // directly on the UI thread, so an escaping exception would crash the whole app. Log it and fall back to the URL/default avatar.
                AppLog.Warn($"Failed to load avatar: {localPath}, {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(AvatarUrl) &&
            Uri.TryCreate(AvatarUrl, UriKind.Absolute, out var avatarUri))
        {
            return new BitmapImage(avatarUri);
        }

        return null;
    }

    // ---------- Transient UI state for list multi-select / hover (not persisted, only drives the card visuals; the page reapplies it after a list rebuild) ----------

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isSelected;

    /// <summary>Whether the account is checked into the bulk selection.</summary>
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            RaiseSelectionVisuals();
        }
    }

    private bool _isPointerOver;

    /// <summary>Whether the pointer is over the card (so the checkbox only shows on hover).</summary>
    [JsonIgnore]
    public bool IsPointerOver
    {
        get => _isPointerOver;
        set
        {
            if (_isPointerOver == value)
            {
                return;
            }

            _isPointerOver = value;
            RaiseSelectionVisuals();
        }
    }

    /// <summary>Shown when selected: a black border around the card + a filled check in the top-left corner.</summary>
    [JsonIgnore]
    public Visibility SelectionRingVisibility => _isSelected ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Top-left check indicator: appears on hover or when selected.</summary>
    [JsonIgnore]
    public Visibility CheckIndicatorVisibility =>
        _isSelected || _isPointerOver ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows an empty circle when not selected (the indicator is then only visible on hover).</summary>
    [JsonIgnore]
    public Visibility EmptyCheckCircleVisibility => _isSelected ? Visibility.Collapsed : Visibility.Visible;

    private void RaiseSelectionVisuals()
    {
        var handler = PropertyChanged;
        if (handler is null)
        {
            return;
        }

        handler(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        handler(this, new PropertyChangedEventArgs(nameof(SelectionRingVisibility)));
        handler(this, new PropertyChangedEventArgs(nameof(CheckIndicatorVisibility)));
        handler(this, new PropertyChangedEventArgs(nameof(EmptyCheckCircleVisibility)));
    }

    /// <summary>Called by the page's one-second timer: tells the countdown bindings to fetch again so the card/details count down live.</summary>
    public void NotifyCooldownTick()
    {
        var handler = PropertyChanged;
        if (handler is null)
        {
            return;
        }

        handler(this, new PropertyChangedEventArgs(nameof(RemainingCooldownText)));
        handler(this, new PropertyChangedEventArgs(nameof(RemainingCooldownSummaryText)));
        handler(this, new PropertyChangedEventArgs(nameof(RemainingCooldownStatusText)));
    }

    private void RaiseNoteVisuals()
    {
        var handler = PropertyChanged;
        if (handler is null)
        {
            return;
        }

        handler(this, new PropertyChangedEventArgs(nameof(Note)));
        handler(this, new PropertyChangedEventArgs(nameof(HasNote)));
        handler(this, new PropertyChangedEventArgs(nameof(NoteIndicatorVisibility)));
    }
}

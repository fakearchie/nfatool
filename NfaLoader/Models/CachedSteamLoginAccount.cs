using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using NfaLoader.Localization;
using NfaLoader.Services;

namespace NfaLoader.Models;

public sealed partial class CachedSteamLoginAccount : INotifyPropertyChanged
{
    public string AccountName { get; set; } = "";

    public string SteamId { get; set; } = "";

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

    public DateTimeOffset CachedAt { get; set; }

    // The ConnectCache token from local.vdf (crc32(account name)+"1" → DPAPI-encrypted refresh token hex).
    // Our sign-in overwrites local.vdf whole, so it is grabbed before that and saved with the account, then written back as is on restore so Steam can sign in automatically without a password.
    // Steam has already encrypted the blob with DPAPI (current user), so only this user on this machine can decrypt it; it is as sensitive as local.vdf itself.
    public string? ConnectCacheToken { get; set; }

    [JsonIgnore]
    public string CacheKey => string.IsNullOrWhiteSpace(SteamId) ? $"name:{AccountName}" : $"id:{SteamId}";

    [JsonIgnore]
    public string AccountTitle => string.IsNullOrWhiteSpace(AccountName) ? Loc.T("Cached_Title_Unknown") : AccountName;

    [JsonIgnore]
    public string PersonaDisplayName => string.IsNullOrWhiteSpace(PersonaName) ? Loc.T("Cached_Persona_NotSynced") : PersonaName;

    [JsonIgnore]
    public string SteamIdDisplay => string.IsNullOrWhiteSpace(SteamId) ? Loc.T("Cached_Steam64_NotRecorded") : SteamId;

    [JsonIgnore]
    public string CachedAtText => CachedAt == default
        ? Loc.T("Cached_CachedAt_UnknownTime")
        : CachedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string CachedAtShortText => CachedAt == default
        ? Loc.T("Cached_CachedAt_Unknown")
        : CachedAt.LocalDateTime.ToString("MM-dd HH:mm");

    [JsonIgnore]
    public string CachedAtCaptionText => Loc.Tf("Cached_Card_CachedAt_Caption_Format", CachedAtShortText);

    // Process-wide avatar cache: rebuilding the list creates new instances, so an instance field can't be reused across rebuilds; only a static dictionary stops the leak.
    // Key = full path, value carries the last write time: if the mtime differs, decode again and "replace" the entry under the same key. Every refresh rewrites the avatar file
    // (so the mtime always changes); if the mtime were part of the key, old decoded bitmaps would stay in the dictionary forever (one leaked per refresh × per account).
    // Only the UI thread touches it (BitmapImage is UI-thread only anyway), so a plain Dictionary is enough.
    private static readonly Dictionary<string, (DateTime Mtime, BitmapImage Image)> AvatarCache =
        new(StringComparer.OrdinalIgnoreCase);

    // PersonPicture shows at most 92px (account details); decode at 2x to leave headroom for DPI.
    private const int AvatarDecodePixelWidth = 184;

    private BitmapImage? _avatarImage;

    [JsonIgnore]
    public BitmapImage? AvatarImage => _avatarImage ??= LoadAvatarImage();

    // When the avatar source (path/URL) changes, drop the decoded cache and tell bindings to fetch again, so the avatar shows as soon as the async download finishes (no full list rebuild).
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

                // Decode from bytes rather than new BitmapImage(Uri): the latter holds the file handle for a long time, so the avatar can't be deleted when the account is removed.
                // Read with FileShare.ReadWrite: even if another thread is replacing the avatar file, no sharing violation is thrown and the avatar doesn't flicker out.
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
                    // SetSource reads the whole stream synchronously before it returns, so disposing it in using is safe.
                    bitmap.SetSource(stream.AsRandomAccessStream());
                }

                AvatarCache[localPath] = (mtime, bitmap);
                return bitmap;
            }
            catch (Exception ex)
            {
                // Catch-all: a corrupt or non-image file makes SetSource throw COMException, and this getter is called by x:Bind directly on
                // the UI thread, so an escaping exception would crash the whole app; log it and fall through to the URL fallback or default avatar.
                AppLog.Warn($"Failed to load cached account avatar: {localPath}, {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(AvatarUrl) &&
            Uri.TryCreate(AvatarUrl, UriKind.Absolute, out var avatarUri))
        {
            return new BitmapImage(avatarUri);
        }

        return null;
    }

    // ---------- Transient UI state for list multi-select / hover (not saved, only drives card visuals, the page reapplies it after a list rebuild) ----------

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

    /// <summary>Whether the mouse is over the card (so the checkbox only shows on hover).</summary>
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

    /// <summary>Shown when selected: a black border around the card + a filled check mark at the top left.</summary>
    [JsonIgnore]
    public Visibility SelectionRingVisibility => _isSelected ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Check indicator at the top left: shows on hover or when selected.</summary>
    [JsonIgnore]
    public Visibility CheckIndicatorVisibility =>
        _isSelected || _isPointerOver ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows an empty circle when not selected (with the indicator visible, that means hover only).</summary>
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
}

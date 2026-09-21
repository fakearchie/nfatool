using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NfaLoader.Models;

namespace NfaLoader.Services;

/// <summary>
/// Persists app-level settings (language, theme). Stored in %AppData%\nfa.pub Loader\settings.json, next to the account history.
/// It is a small file read and written synchronously by few callers (read once at startup, written on settings page changes), so a plain lock is used instead of the account history's file gate.
/// </summary>
internal sealed class SettingsService
{
    private const string AppFolderName = "nfa.pub Loader";
    private const string SettingsFileName = "settings.json";

    private readonly string _settingsFilePath;
    private readonly object _gate = new();

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        AppFolderPath = Path.Combine(appData, AppFolderName);
        _settingsFilePath = Path.Combine(AppFolderPath, SettingsFileName);
    }

    /// <summary>Data root directory (%AppData%\nfa.pub Loader), used by "Open data folder".</summary>
    public string AppFolderPath { get; }

    /// <summary>Fixed on-disk path of the "Personalization" avatar (the cropped 512² JPEG).</summary>
    public string PersonalizationAvatarPath => Path.Combine(AppFolderPath, "personalization", "avatar.jpg");

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                    if (settings is not null)
                    {
                        // An explicit "groups": null in the JSON overrides the property initializer, and callers (LoadGroups etc.) that read .Groups directly would NRE.
                        settings.Groups ??= [];
                        // Same guard for the loadout preset: an explicit null would NRE on loadout page navigation and one-click loadout.
                        settings.Loadout ??= CsLoadoutPreset.Default();
                        settings.Loadout.T ??= new Dictionary<uint, uint>();
                        settings.Loadout.Ct ??= new Dictionary<uint, uint>();
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to read app settings, using defaults.", ex);
            }

            return new AppSettings();
        }
    }

    public bool Save(AppSettings settings)
    {
        lock (_gate)
        {
            // Write a temp file first, then replace atomically, so an interrupted write cannot leave a half-written settings.json.
            var tempPath = _settingsFilePath + "." + Path.GetRandomFileName() + ".tmp";
            try
            {
                Directory.CreateDirectory(AppFolderPath);
                var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);

                File.WriteAllText(tempPath, json);
                if (File.Exists(_settingsFilePath))
                {
                    File.Replace(tempPath, _settingsFilePath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, _settingsFilePath);
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to save app settings.", ex);
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // No need to report a failure to clean up the leftover temp file.
                }

                return false;
            }
        }
    }
}

internal sealed class AppSettings
{
    /// <summary>UI language code: zh-Hans / en / zh-Hant. null means not chosen yet (first start picks it from the system language).</summary>
    public string? Language { get; set; }

    /// <summary>Theme: Default (follow system) / Light / Dark.</summary>
    public string Theme { get; set; } = "Default";

    /// <summary>
    /// The resolved and saved Steam install directory (the root that contains steam.exe). Written after detection on first start and reused after that,
    /// so it is not detected again on every sign-in. null/empty means not resolved yet; if it goes stale (no steam.exe in the directory) it is detected again,
    /// and if that still fails a dialog asks the user to pick it. See <see cref="SteamPathCoordinator"/>.
    /// </summary>
    public string? SteamInstallPath { get; set; }

    /// <summary>The single CS2 loadout preset, edited on the loadout page and applied in one click from the sign-in page. New users get the built-in default loadout.</summary>
    public CsLoadoutPreset Loadout { get; set; } = CsLoadoutPreset.Default();

    /// <summary>Nickname set in the "Personalization" panel, which the sign-in page applies to the account profile in one click. null/empty means leave the nickname unchanged.</summary>
    public string? PersonaName { get; set; }

    /// <summary>Real name set in the "Personalization" panel (the profile's real_name field). null/empty means leave it unchanged.</summary>
    public string? ProfileRealName { get; set; }

    /// <summary>Summary set in the "Personalization" panel (the profile's summary field, can be multi-line). null/empty means leave it unchanged.</summary>
    public string? ProfileSummary { get; set; }

    /// <summary>Whether to clear the account's previous names after "one-click personalization" finishes.</summary>
    public bool ClearAliasHistoryOnPersonalize { get; set; }

    /// <summary>User-defined account group definitions (name, order). Membership is stored in each account's GroupIds; only the definitions live here.</summary>
    public List<AccountGroup> Groups { get; set; } = [];

    /// <summary>Whether to copy the local CS2 settings of the "source account" to the account being signed in.</summary>
    public bool Cs2SyncOnLogin { get; set; }

    /// <summary>SteamID64 of the "source account" for CS2 settings sync; empty means none chosen. See <see cref="Cs2CloudService"/>.</summary>
    public string? Cs2SyncSourceSteamId { get; set; }

    /// <summary>GitHub site code used for update checks (direct / gh-proxy.org / v4.gh-proxy.org / v6.gh-proxy.org / cdn.gh-proxy.org), kept in sync with GitHubUpdateService.ProxySites; unknown values fall back to direct.</summary>
    public string UpdateProxySite { get; set; } = "direct";

    /// <summary>The nfa.pub API key, DPAPI-protected (see SecretProtector). Never stored in plain text.</summary>
    public string? NfaApiKeyProtected { get; set; }

    /// <summary>
    /// A purchase sent but not yet confirmed, saved before the request goes out. Replaying the same idempotency key
    /// makes nfa.pub return that order instead of charging again, even after the app was closed mid-purchase.
    /// </summary>
    public string? NfaPendingProductId { get; set; }

    public string? NfaPendingType { get; set; }

    public string? NfaPendingName { get; set; }

    public string? NfaPendingKey { get; set; }

    public DateTimeOffset? NfaPendingStartedAt { get; set; }

    /// <summary>The API key the pending purchase was sent with, DPAPI-protected, so a replay never uses a different account.</summary>
    public string? NfaPendingApiKeyProtected { get; set; }
}

// Uses a source generator like the account history: JsonSerializerDefaults.Web (camelCase, case-insensitive), so it reads and writes under AOT.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(CsLoadoutPreset))]
[JsonSerializable(typeof(AccountGroup))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;

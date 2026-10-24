using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NfaLoader.Services;

[Flags]
internal enum Cs2SettingsCategory
{
    None = 0, Controls = 1, Crosshair = 2, HudAudio = 4, Video = 8, CustomConfigs = 16,
    All = Controls | Crosshair | HudAudio | Video | CustomConfigs
}

internal sealed record Cs2SettingsPreviewFile(string RelativePath, Cs2SettingsCategory Categories, bool Existed, bool Changed);
internal sealed record Cs2SettingsPreview(string TargetSteamId64, IReadOnlyList<Cs2SettingsPreviewFile> Files);
internal sealed record Cs2SettingsTransferResult(string TargetSteamId64, bool Ok, int FilesWritten, string? BackupPath, string? Error);
internal sealed record Cs2SettingsBackup(string ManifestPath, string TargetSteamId64, DateTime CreatedAt, int FileCount);
internal sealed class Cs2SettingsSnapshot
{
    public required string UserdataRoot { get; init; }
    internal required IReadOnlyList<Cs2SettingsPayload> Payloads { get; init; }
    public required string SourceSteamId64 { get; init; }
    public required Cs2SettingsCategory Categories { get; init; }
    public required Cs2SettingsCategory MissingCategories { get; init; }
    public int FileCount => Payloads.Count;
}
internal sealed record Cs2SettingsPayload(string RelativePath, byte[] Data, Cs2SettingsCategory Categories, bool Merge);
internal sealed class Cs2SettingsBackupManifest
{
    public int Version { get; set; } = 1;
    public string UserdataRoot { get; set; } = "";
    public string SourceSteamId64 { get; set; } = "";
    public string TargetSteamId64 { get; set; } = "";
    public List<Cs2SettingsBackupFile> Files { get; set; } = [];
}
internal sealed class Cs2SettingsBackupFile
{
    public string RelativePath { get; set; } = "";
    public bool Existed { get; set; }
    public string StoredName { get; set; } = "";
    public string Sha256 { get; set; } = "";
}
[JsonSerializable(typeof(Cs2SettingsBackupManifest))]
internal sealed partial class Cs2SettingsBackupJsonContext : JsonSerializerContext;

internal sealed class Cs2SettingsTransferService
{
    private readonly Func<bool> _isRunning;
    private readonly string _backupRoot;
    private const ulong SteamBase = 76561197960265728UL;
    private static readonly string[] ConfigRoots = ["730/local/cfg", "730/remote/cfg"];
    private static readonly HashSet<string> Controls = new(StringComparer.OrdinalIgnoreCase)
    {
        "sensitivity", "zoom_sensitivity_ratio", "zoom_sensitivity_ratio_mouse", "m_yaw", "m_pitch",
        "m_rawinput", "m_customaccel", "m_mouseaccel1", "m_mouseaccel2", "m_mousespeed",
        "cl_mouseenable", "cl_input_enable_raw_keyboard", "cl_input_enable_raw_mouse", "option_duck_method", "option_speed_method",
        "m_forward", "m_side", "cl_autowepswitch", "cl_silencer_mode", "cl_debounce_zoom", "cl_sniper_delay_unscope", "cl_sniper_auto_rezoom",
        "sensitivity_y_scale", "input_button_code_is_scan_code_scd", "cl_joystick_enabled"
    };
    private static readonly HashSet<string> HudAudio = new(StringComparer.OrdinalIgnoreCase)
    {
        "hud_scaling", "hud_showtargetid", "hud_fastswitch", "cl_hud_color", "cl_hud_radar_scale",
        "cl_hud_background_alpha", "cl_hud_playercount_showcount", "cl_hud_playercount_pos", "cl_hud_bomb_under_radar",
        "cl_hud_healthammo_style", "cl_showloadout", "cl_show_clan_in_death_notice", "cl_use_opens_buy_menu",
        "cl_righthand", "cl_prefer_lefthanded", "safezonex", "safezoney", "volume", "voice_modenable",
        "voice_scale", "voice_always_sample_mic", "voice_threshold", "voice_loopback", "voice_vox",
        "cl_mute_enemy_team", "cl_mute_all_but_friends_and_party", "cl_teamid_overhead_mode"
    };

    public Cs2SettingsTransferService(Func<bool>? isSteamOrCs2Running = null, string? backupRoot = null)
    {
        _isRunning = isSteamOrCs2Running ?? ProcessesRunning;
        _backupRoot = Path.GetFullPath(backupRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nfa.pub Loader", "cs2-settings-backups"));
    }

    public Cs2SettingsSnapshot Capture(string userdataRoot, string sourceSteamId64, Cs2SettingsCategory categories)
    {
        EnsureStopped();
        if (categories == Cs2SettingsCategory.None || (categories & ~Cs2SettingsCategory.All) != 0)
            throw new ArgumentException("Select settings to copy.");
        var root = Path.GetFullPath(userdataRoot);
        var account = AccountRoot(root, sourceSteamId64);
        var payloads = new List<Cs2SettingsPayload>();
        var found = Cs2SettingsCategory.None;
        var files = EnumerateSettings(account).ToList();
        var userGroups = files.Where(p => UserFile(p) != null).GroupBy(p => UserFile(p)!.Value.LocalName, StringComparer.OrdinalIgnoreCase);
        foreach (var group in userGroups)
        {
            if ((categories & (Cs2SettingsCategory.Controls | Cs2SettingsCategory.Crosshair | Cs2SettingsCategory.HudAudio)) == 0) continue;
            var relative = group.OrderByDescending(p => p.StartsWith("730/local/cfg/", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(p => File.GetLastWriteTimeUtc(SafePath(account, p))).First();
            var mapping = UserFile(relative)!.Value;
            if (mapping.Keys && (categories & Cs2SettingsCategory.Controls) == 0) continue;
            var selected = Select(ReadVdf(SafePath(account, relative)), categories, mapping.Keys);
            if (selected.Document.Count == 0) continue;
            var data = Encoding.UTF8.GetBytes(VdfDocument.ToText(selected.Document));
            payloads.Add(new("730/local/cfg/" + mapping.LocalName, data, selected.Categories, true));
            payloads.Add(new("730/remote/" + mapping.CloudName, data, selected.Categories, true));
            if (group.Any(p => p.StartsWith("730/remote/cfg/", StringComparison.OrdinalIgnoreCase)))
                payloads.Add(new("730/remote/cfg/" + mapping.LocalName, data, selected.Categories, true));
            found |= selected.Categories;
        }
        foreach (var relative in files.Where(p => UserFile(p) == null))
        {
            var path = SafePath(account, relative);
            var name = Path.GetFileName(path);
            if (name.Equals("cs2_machine_convars.vcfg", StringComparison.OrdinalIgnoreCase))
            {
                if ((categories & (Cs2SettingsCategory.Controls | Cs2SettingsCategory.Crosshair | Cs2SettingsCategory.HudAudio)) == 0) continue;
                var document = ReadVdf(path);
                var selected = Select(document, categories, false);
                if (selected.Document.Count == 0) continue;
                payloads.Add(new(relative, Encoding.UTF8.GetBytes(VdfDocument.ToText(selected.Document)), selected.Categories, true));
                found |= selected.Categories;
            }
            else
            {
                var category = name.Equals("cs2_video.txt", StringComparison.OrdinalIgnoreCase) ? Cs2SettingsCategory.Video : Cs2SettingsCategory.CustomConfigs;
                if ((categories & category) == 0) continue;
                if (category == Cs2SettingsCategory.Video) _ = ReadVdf(path);
                payloads.Add(new(relative, File.ReadAllBytes(path), category, false));
                found |= category;
            }
        }
        return new() { UserdataRoot = root, SourceSteamId64 = sourceSteamId64, Categories = categories,
            MissingCategories = categories & ~found, Payloads = payloads.AsReadOnly() };
    }

    public Cs2SettingsPreview Preview(Cs2SettingsSnapshot snapshot, string targetSteamId64)
    {
        var plans = Plan(snapshot, targetSteamId64);
        return new(targetSteamId64, plans.Select(p => new Cs2SettingsPreviewFile(p.RelativePath, p.Category,
            p.Original != null, p.Original == null || !p.Original.SequenceEqual(p.Data!))).ToList());
    }

    public IReadOnlyList<Cs2SettingsBackup> ListBackups()
    {
        ValidateAncestors(_backupRoot);
        if (!Directory.Exists(_backupRoot)) return [];
        var result = new List<Cs2SettingsBackup>();
        foreach (var folder in Directory.EnumerateDirectories(_backupRoot))
        {
            try
            {
                var path = Path.Combine(folder, "manifest.json");
                ValidateAncestors(path);
                if (!File.Exists(path)) continue;
                var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), Cs2SettingsBackupJsonContext.Default.Cs2SettingsBackupManifest);
                ValidateManifest(manifest);
                _ = AccountRoot(manifest!.UserdataRoot, manifest.SourceSteamId64);
                _ = AccountRoot(manifest.UserdataRoot, manifest.TargetSteamId64);
                result.Add(new(path, manifest.TargetSteamId64, Directory.GetCreationTimeUtc(folder), manifest.Files.Count));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { }
        }
        return result.OrderByDescending(b => b.CreatedAt).ToList();
    }

    public IReadOnlyList<Cs2SettingsTransferResult> Apply(Cs2SettingsSnapshot snapshot, IEnumerable<string> targets)
    {
        var results = new List<Cs2SettingsTransferResult>();
        foreach (var target in targets.Distinct(StringComparer.Ordinal))
        {
            string? backup = null;
            try
            {
                using var gate = AcquireLock();
                EnsureStopped();
                var account = AccountRoot(snapshot.UserdataRoot, target);
                var plans = Plan(snapshot, target).Where(p => p.Original == null || !p.Original.SequenceEqual(p.Data!)).ToList();
                if (plans.Count == 0) { results.Add(new(target, true, 0, null, null)); continue; }
                backup = Backup(snapshot.UserdataRoot, snapshot.SourceSteamId64, target, plans);
                Mutate(account, plans);
                results.Add(new(target, true, plans.Count, backup, null));
            }
            catch (Exception ex) { results.Add(new(target, false, 0, backup, ex.Message)); }
        }
        return results;
    }

    public Cs2SettingsTransferResult Restore(string manifestPath, string? expectedUserdataRoot = null)
    {
        string target = "";
        string? backup = null;
        try
        {
            using var gate = AcquireLock();
            EnsureStopped();
            var manifestFull = Path.GetFullPath(manifestPath);
            ValidateAncestors(manifestFull);
            if (!manifestFull.StartsWith(_backupRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(manifestFull) != "manifest.json") throw new InvalidOperationException("Invalid backup path.");
            var manifest = JsonSerializer.Deserialize(File.ReadAllText(manifestFull), Cs2SettingsBackupJsonContext.Default.Cs2SettingsBackupManifest)
                ?? throw new InvalidOperationException("Invalid backup manifest.");
            ValidateManifest(manifest);
            if (expectedUserdataRoot != null && !Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.UserdataRoot))
                .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedUserdataRoot)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Backup belongs to a different Steam userdata folder.");
            target = manifest.TargetSteamId64;
            _ = AccountRoot(manifest.UserdataRoot, manifest.SourceSteamId64);
            var account = AccountRoot(manifest.UserdataRoot, target);
            var plans = new List<WritePlan>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Files)
            {
                if (!paths.Add(entry.RelativePath)) throw new InvalidOperationException("Duplicate backup path.");
                var path = SafePath(account, entry.RelativePath);
                byte[]? data = null;
                if (entry.Existed)
                {
                    if (entry.StoredName != $"{plans.Count:D4}.bin") throw new InvalidOperationException("Invalid original path.");
                    var stored = Path.Combine(Path.GetDirectoryName(manifestFull)!, entry.StoredName);
                    ValidateAncestors(stored);
                    data = File.ReadAllBytes(stored);
                    if (!Hash(data).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Backup hash mismatch.");
                }
                else if (entry.StoredName.Length != 0 || entry.Sha256.Length != 0) throw new InvalidOperationException("Invalid absent original.");
                plans.Add(new(entry.RelativePath, data, File.Exists(path) ? File.ReadAllBytes(path) : null, Cs2SettingsCategory.None));
            }
            backup = Backup(manifest.UserdataRoot, manifest.SourceSteamId64, target, plans);
            Mutate(account, plans);
            return new(target, true, plans.Count, backup, null);
        }
        catch (Exception ex) { return new(target, false, 0, backup, ex.Message); }
    }

    private sealed record WritePlan(string RelativePath, byte[]? Data, byte[]? Original, Cs2SettingsCategory Category);

    private static void ValidateManifest(Cs2SettingsBackupManifest? manifest)
    {
        if (manifest?.Version != 1 || string.IsNullOrWhiteSpace(manifest.UserdataRoot) ||
            string.IsNullOrWhiteSpace(manifest.SourceSteamId64) || string.IsNullOrWhiteSpace(manifest.TargetSteamId64) ||
            manifest.Files == null || manifest.Files.Count == 0)
            throw new InvalidOperationException("Invalid backup manifest.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < manifest.Files.Count; index++)
        {
            var file = manifest.Files[index];
            if (file == null || string.IsNullOrWhiteSpace(file.RelativePath) || !Allowed(file.RelativePath) ||
                !paths.Add(file.RelativePath) || file.StoredName == null || file.Sha256 == null)
                throw new InvalidOperationException("Invalid backup manifest entry.");
            if (file.Existed ? file.StoredName != $"{index:D4}.bin" || file.Sha256.Length != 64 || !file.Sha256.All(char.IsAsciiHexDigit) :
                file.StoredName.Length != 0 || file.Sha256.Length != 0)
                throw new InvalidOperationException("Invalid backup original metadata.");
        }
    }

    private static List<WritePlan> Plan(Cs2SettingsSnapshot snapshot, string target)
    {
        if (snapshot.Payloads.Count == 0) throw new InvalidOperationException("Snapshot contains no settings for the selected categories.");
        if (target == snapshot.SourceSteamId64) throw new InvalidOperationException("Source and target must differ.");
        var account = AccountRoot(snapshot.UserdataRoot, target);
        var plans = new List<WritePlan>();
        var payloads = snapshot.Payloads.ToList();
        foreach (var payload in snapshot.Payloads.Where(p => p.RelativePath.StartsWith("730/local/cfg/", StringComparison.OrdinalIgnoreCase) && UserFile(p.RelativePath) != null))
        {
            var mapping = UserFile(payload.RelativePath)!.Value;
            foreach (var name in new[] { mapping.LocalName, mapping.CloudName }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var mirror = "730/remote/cfg/" + name;
                if (File.Exists(SafePath(account, mirror)) && !payloads.Any(p => p.RelativePath.Equals(mirror, StringComparison.OrdinalIgnoreCase)))
                    payloads.Add(payload with { RelativePath = mirror });
            }
        }
        foreach (var payload in payloads)
        {
            var path = SafePath(account, payload.RelativePath);
            var original = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var data = payload.Data;
            if (payload.Merge)
            {
                var document = original == null ? ReadTargetSeed(account, payload.RelativePath) : ReadVdf(path);
                var selected = ParseBytes(payload.Data);
                Merge(document, selected);
                data = Encoding.UTF8.GetBytes(VdfDocument.ToText(document));
            }
            plans.Add(new(payload.RelativePath, data, original, payload.Categories));
        }
        return plans;
    }

    private static Dictionary<string, object> ReadTargetSeed(string account, string relativePath)
    {
        var mapping = UserFile(relativePath);
        if (mapping == null) return new(StringComparer.Ordinal);
        var names = new[] { mapping.Value.LocalName, mapping.Value.CloudName }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = names.Select(n => "730/local/cfg/" + n)
            .Concat(names.Reverse().Select(n => "730/remote/" + n))
            .Concat(names.Select(n => "730/remote/cfg/" + n));
        foreach (var candidate in candidates)
        {
            var path = SafePath(account, candidate);
            if (File.Exists(path)) return ReadVdf(path);
        }
        return new(StringComparer.Ordinal);
    }

    private static Dictionary<string, object> ParseBytes(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2-settings-" + Guid.NewGuid().ToString("N") + ".vcfg");
        try { File.WriteAllBytes(path, bytes); return ReadVdf(path); }
        finally { File.Delete(path); }
    }

    private static Dictionary<string, object> ReadVdf(string path)
    {
        if (VdfDocument.LoadForUpdate(path, out var document, out _, out var error, attempts: 1) != VdfReadState.Loaded)
            throw new InvalidOperationException($"Cannot read settings {Path.GetFileName(path)}: {error ?? "empty file"}");
        return document;
    }

    private static (Dictionary<string, object> Document, Cs2SettingsCategory Categories) Select(
        Dictionary<string, object> source, Cs2SettingsCategory requested, bool keys, bool bindings = false)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        var categories = Cs2SettingsCategory.None;
        foreach (var (key, value) in source)
        {
            if (value is Dictionary<string, object> child)
            {
                var selected = Select(child, requested, keys, bindings || (keys && key.Equals("bindings", StringComparison.OrdinalIgnoreCase)));
                if (selected.Document.Count > 0) result[key] = selected.Document;
                categories |= selected.Categories;
            }
            else
            {
                var category = Classify(key, bindings);
                if (category != Cs2SettingsCategory.None && (requested & category) != 0) { result[key] = value; categories |= category; }
            }
        }
        return (result, categories);
    }

    private static void Merge(Dictionary<string, object> target, Dictionary<string, object> source)
    {
        foreach (var (key, value) in source)
        {
            var actual = target.Keys.FirstOrDefault(k => NormalizeKey(k).Equals(NormalizeKey(key), StringComparison.OrdinalIgnoreCase)) ?? key;
            if (value is Dictionary<string, object> child)
            {
                if (target.TryGetValue(actual, out var existing) && existing is not Dictionary<string, object>)
                    throw new InvalidOperationException("Target settings have conflicting structure.");
                var destination = target.TryGetValue(actual, out existing) ? (Dictionary<string, object>)existing : new(StringComparer.Ordinal);
                Merge(destination, child);
                target[actual] = destination;
            }
            else
            {
                if (actual != key) target.Remove(actual);
                target[key] = value;
            }
        }
    }

    private static string NormalizeKey(string key)
    {
        var dollar = key.LastIndexOf('$');
        return dollar >= 0 && dollar < key.Length - 1 && key[(dollar + 1)..].All(char.IsAsciiDigit) ? key[..dollar] : key;
    }

    private static Cs2SettingsCategory Classify(string serializedKey, bool bindings)
    {
        var key = NormalizeKey(serializedKey);
        bool Starts(string prefix) => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        bool Is(string value) => key.Equals(value, StringComparison.OrdinalIgnoreCase);
        if (bindings || Controls.Contains(key) || Starts("joy_")) return Cs2SettingsCategory.Controls;
        if (Starts("cl_crosshair") || Starts("cl_grenadecrosshair") || Is("crosshair") || Is("cl_fixedcrosshairgap") ||
            Is("cl_ironsight_dot_scale") || Is("cl_ironsight_usecrosshaircolor") || Is("cl_observed_bot_crosshair")) return Cs2SettingsCategory.Crosshair;
        if (HudAudio.Contains(key) || Starts("viewmodel_") || Starts("cl_radar_") || Starts("snd_") || Starts("cl_hud_") ||
            Starts("hud_") || Starts("safezone") || Starts("voice_") || Starts("cc_") || Is("closecaption") || Is("speaker_config")) return Cs2SettingsCategory.HudAudio;
        return Cs2SettingsCategory.None;
    }

    private static (string LocalName, string CloudName, bool Keys)? UserFile(string path)
    {
        var name = Path.GetFileName(path);
        var match = Regex.Match(name, "^cs2_user_(convars|keys)(_[0-9]+_slot[0-9]+)?\\.vcfg$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var kind = match.Groups[1].Value.ToLowerInvariant();
        var suffix = match.Groups[2].Success ? match.Groups[2].Value.ToLowerInvariant() : "_0_slot0";
        var stem = "cs2_user_" + kind;
        return (stem + suffix + ".vcfg", stem + (suffix == "_0_slot0" ? "" : suffix) + ".vcfg", kind == "keys");
    }

    private static IEnumerable<string> EnumerateSettings(string account)
    {
        var directories = ConfigRoots.Append("730/remote");
        foreach (var relative in directories)
        {
            var dir = Path.Combine(account, relative.Replace('/', Path.DirectorySeparatorChar));
            ValidateAncestors(dir);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var candidate = Path.GetRelativePath(account, file).Replace('\\', '/');
                if (Allowed(candidate)) { _ = SafePath(account, candidate); yield return candidate; }
            }
        }
    }

    private static bool Allowed(string relative)
    {
        if (relative.Contains('\\') || relative.Split('/').Any(p => p is "" or "." or ".." || p.Contains(':'))) return false;
        var directory = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
        var name = relative[(relative.LastIndexOf('/') + 1)..];
        var user = UserFile(relative) != null;
        if (directory.Equals("730/remote", StringComparison.OrdinalIgnoreCase)) return user;
        return ConfigRoots.Contains(directory, StringComparer.OrdinalIgnoreCase) && (user ||
            name.Equals("cs2_machine_convars.vcfg", StringComparison.OrdinalIgnoreCase) || name.Equals("cs2_video.txt", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) && !IsInternalConfig(name));
    }

    private static bool IsInternalConfig(string name) => name.Equals("trustedlaunch.cfg", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("machine_convars.cfg", StringComparison.OrdinalIgnoreCase) || name.Equals("config.cfg", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("cs2_user_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("cs2_machine_", StringComparison.OrdinalIgnoreCase);

    private static string AccountRoot(string root, string steamId)
    {
        if (!ulong.TryParse(steamId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
            id.ToString(CultureInfo.InvariantCulture) != steamId || id <= SteamBase || id - SteamBase > uint.MaxValue)
            throw new ArgumentException("Invalid personal SteamID64.");
        var path = Path.Combine(Path.GetFullPath(root), (id - SteamBase).ToString(CultureInfo.InvariantCulture));
        ValidateAncestors(path);
        return path;
    }

    private static string SafePath(string account, string relative)
    {
        if (!Allowed(relative)) throw new InvalidOperationException("Settings path is not allowed.");
        var path = Path.GetFullPath(Path.Combine(account, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(account + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Settings path escapes account.");
        ValidateAncestors(path);
        return path;
    }

    private static void ValidateAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked settings paths are not allowed.");
        }
    }

    private FileStream AcquireLock()
    {
        ValidateAncestors(_backupRoot);
        Directory.CreateDirectory(_backupRoot);
        var path = Path.Combine(_backupRoot, "transfer.lock");
        ValidateAncestors(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void EnsureStopped()
    {
        if (_isRunning()) throw new InvalidOperationException("Close Steam and CS2 before changing settings.");
    }

    private static bool ProcessesRunning()
    {
        var processes = Process.GetProcesses();
        try
        {
            return processes.Any(p => p.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase) ||
                p.ProcessName.Equals("cs2", StringComparison.OrdinalIgnoreCase));
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private string Backup(string root, string source, string target, List<WritePlan> plans)
    {
        var folder = Path.Combine(_backupRoot, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        ValidateAncestors(folder);
        Directory.CreateDirectory(folder);
        var manifest = new Cs2SettingsBackupManifest { UserdataRoot = root, SourceSteamId64 = source, TargetSteamId64 = target };
        for (var index = 0; index < plans.Count; index++)
        {
            var plan = plans[index];
            var entry = new Cs2SettingsBackupFile { RelativePath = plan.RelativePath, Existed = plan.Original != null };
            if (plan.Original != null)
            {
                entry.StoredName = $"{index:D4}.bin";
                entry.Sha256 = Hash(plan.Original);
                using var original = new FileStream(Path.Combine(folder, entry.StoredName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                original.Write(plan.Original);
                original.Flush(true);
            }
            manifest.Files.Add(entry);
        }
        var path = Path.Combine(folder, "manifest.json");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, manifest, Cs2SettingsBackupJsonContext.Default.Cs2SettingsBackupManifest);
            stream.Flush(true);
        }
        return path;
    }

    private void Mutate(string account, List<WritePlan> plans)
    {
        var changed = new List<WritePlan>();
        try
        {
            foreach (var plan in plans)
            {
                EnsureStopped();
                var path = SafePath(account, plan.RelativePath);
                var current = File.Exists(path) ? File.ReadAllBytes(path) : null;
                if ((current == null) != (plan.Original == null) || current != null && !current.SequenceEqual(plan.Original!))
                    throw new InvalidOperationException("Target settings changed after preview.");
                AtomicWrite(path, plan.Data);
                changed.Add(plan);
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var plan in changed.AsEnumerable().Reverse())
            {
                try { AtomicWrite(SafePath(account, plan.RelativePath), plan.Original); }
                catch (Exception rollback) { errors.Add(rollback); }
            }
            throw errors.Count == 1 ? failure : new AggregateException("Transfer failed and rollback needs backup restoration.", errors);
        }
    }

    private static void AtomicWrite(string path, byte[]? data)
    {
        ValidateAncestors(path);
        if (data == null) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(data); stream.Flush(true); }
            ValidateAncestors(path);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}

using System.IO;
using System.Reflection;
using System.Text.Json;
using NfaLoader.Models;
using NfaLoader.Services;

namespace NfaLoader.Localization;

/// <summary>
/// Finds and loads every UI language pack. Sources (a later one overrides an earlier one with the same language code, so packs can be edited locally):
///  1. Embedded resources (Languages\*.json packed into the assembly, as a fallback: they still work when a release misses copying the files);
///  2. Languages\*.json in the program folder (copied with the release; contributors edit these);
///  3. %AppData%\nfa.pub Loader\Languages\*.json (users drop in new languages, no rebuild needed).
/// Adding a language = adding a &lt;code&gt;.json, which shows up in Settings as soon as the app starts.
/// </summary>
internal static class LanguageCatalog
{
    private const string AppFolderName = "nfa.pub Loader";
    private const string LanguagesFolderName = "Languages";

    // Preferred order for display and fallback; other languages follow, sorted by name.
    private static readonly string[] PreferredOrder = { "zh-Hans", "en", "zh-Hant" };

    /// <summary>Folder where users can drop in custom language packs (%AppData%\nfa.pub Loader\Languages).</summary>
    public static string UserLanguagesFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName,
        LanguagesFolderName);

    /// <summary>Loads every language pack and returns them in display order. Should include at least the embedded zh-Hans.</summary>
    public static IReadOnlyList<LanguagePack> Load()
    {
        var byCode = new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase);

        LoadEmbedded(byCode);
        LoadDirectory(Path.Combine(AppContext.BaseDirectory, LanguagesFolderName), byCode);
        LoadDirectory(UserLanguagesFolder, byCode);

        return byCode.Values
            .OrderBy(pack =>
            {
                var index = Array.IndexOf(PreferredOrder, pack.Code);
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(pack => pack.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void LoadEmbedded(Dictionary<string, LanguagePack> byCode)
    {
        var assembly = typeof(LanguageCatalog).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            // Resource names look like NfaLoader.Languages.en.json.
            if (!name.Contains(".Languages.", StringComparison.Ordinal) ||
                !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null)
                {
                    continue;
                }

                var pack = JsonSerializer.Deserialize(stream, LanguagePackJsonContext.Default.LanguagePack);
                AddPack(byCode, pack);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to parse embedded language resource {name}, skipped: {ex.Message}");
            }
        }
    }

    private static void LoadDirectory(string directory, Dictionary<string, LanguagePack> byCode)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var pack = JsonSerializer.Deserialize(json, LanguagePackJsonContext.Default.LanguagePack);
                AddPack(byCode, pack);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to parse language file {file}, skipped: {ex.Message}");
            }
        }
    }

    private static void AddPack(Dictionary<string, LanguagePack> byCode, LanguagePack? pack)
    {
        if (pack is null || string.IsNullOrWhiteSpace(pack.Code) || pack.Strings.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(pack.Name))
        {
            pack.Name = pack.Code;
        }

        // Sources loaded later override earlier packs with the same code (user folder > program folder > embedded).
        byCode[pack.Code] = pack;
    }
}

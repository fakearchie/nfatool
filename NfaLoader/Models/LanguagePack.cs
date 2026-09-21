using System.Text.Json;
using System.Text.Json.Serialization;

namespace NfaLoader.Models;

/// <summary>
/// One UI language: maps to one Languages\&lt;code&gt;.json file. A community-contributed language only needs one new JSON file like this.
/// </summary>
internal sealed class LanguagePack
{
    /// <summary>Language code (e.g. zh-Hans / en / zh-Hant / ja). Also serves as the file name and the dedup key.</summary>
    public string Code { get; set; } = "";

    /// <summary>The language's name for itself (e.g. English / Français), shown in the language list on the settings page and always written in that language.</summary>
    public string Name { get; set; } = "";

    /// <summary>Language code to fall back to when a key is missing (the global default fallback is zh-Hans).</summary>
    public string? Fallback { get; set; }

    /// <summary>Key → translated text.</summary>
    public Dictionary<string, string> Strings { get; set; } = new(StringComparer.Ordinal);
}

// Reflection-based System.Text.Json is disabled globally, so language files are parsed by the source generator (AOT-safe).
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LanguagePack))]
internal sealed partial class LanguagePackJsonContext : JsonSerializerContext;

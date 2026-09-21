using System.Globalization;
using NfaLoader.Models;
using NfaLoader.Services;

namespace NfaLoader.Localization;

/// <summary>
/// Code-side localization facade. It avoids .resw / PRI on purpose: the PRI resource index is fragile for unpackaged WinUI + Native AOT
/// (the csproj already carries the IncludeWinUIPriInPublish patch). UI strings come from the Languages\*.json language packs
/// (see <see cref="LanguageCatalog"/>), so the community can contribute translations: adding one &lt;code&gt;.json adds a language.
///
/// Three ways to use it:
///  · Static XAML text: {x:Bind Strings.Get('Key'), Mode=OneWay}, which goes through <see cref="LocalizedStrings"/>; the host page
///    raises PropertyChanged on Strings on <see cref="LanguageChanged"/> to switch live.
///  · Text inside a DataTemplate: {loc:Localize Key=...}, see <see cref="LocalizeExtension"/> (read at load time, refreshed when the list is rebuilt).
///  · Text set from code: <see cref="T"/> / <see cref="Tf"/>.
/// </summary>
internal static class Loc
{
    private const string DefaultFallbackCode = "zh-Hans";

    private static SettingsService? _settings;
    private static IReadOnlyList<LanguagePack> _packs = Array.Empty<LanguagePack>();
    private static LanguagePack _current = EmptyPack();
    private static LanguagePack _fallback = EmptyPack();

    /// <summary>Current language code (such as zh-Hans / en / zh-Hant).</summary>
    public static string CurrentCode => _current.Code;

    /// <summary>Available languages (in display order) for the language list on the settings page.</summary>
    public static IReadOnlyList<LanguagePack> AvailablePacks => _packs;

    /// <summary>Singleton entry point for XAML bindings: {x:Bind Strings.Get('Key'), Mode=OneWay}.</summary>
    public static LocalizedStrings Strings { get; } = new();

    /// <summary>Raised after the language changes (UI thread): pages rerun their code-side rendering and raise PropertyChanged on Strings to switch live.</summary>
    public static event Action? LanguageChanged;

    /// <summary>Called once at startup: loads the language packs and applies the language from settings (or guesses from the system language if none is set).</summary>
    public static void Initialize(SettingsService settings)
    {
        _settings = settings;
        _packs = LanguageCatalog.Load();

        var saved = settings.Load().Language;
        var code = !string.IsNullOrWhiteSpace(saved) && HasPack(saved)
            ? saved!
            : DetectSystemLanguage();
        ApplyLanguage(code);
    }

    /// <summary>Switches language: refreshes all x:Bind text, raises <see cref="LanguageChanged"/> and saves the choice.</summary>
    public static void SetLanguage(string code, bool persist = true)
    {
        if (string.IsNullOrWhiteSpace(code) || code == CurrentCode || !HasPack(code))
        {
            return;
        }

        ApplyLanguage(code);

        if (persist && _settings is not null)
        {
            var settings = _settings.Load();
            settings.Language = code;
            _settings.Save(settings);
        }
    }

    public static string T(string key)
    {
        if (_current.Strings.TryGetValue(key, out var value))
        {
            return value;
        }

        return _fallback.Strings.TryGetValue(key, out var fallback) ? fallback : key;
    }

    public static string Tf(string key, params object?[] args) => string.Format(T(key), args);

    private static void ApplyLanguage(string code)
    {
        _current = FindPack(code) ?? FindPack(DefaultFallbackCode) ?? (_packs.Count > 0 ? _packs[0] : EmptyPack());

        // Fallback pack: the fallback the current language declares, else the global zh-Hans, else the current pack itself.
        _fallback = FindPack(_current.Fallback ?? DefaultFallbackCode)
            ?? FindPack(DefaultFallbackCode)
            ?? _current;

        Strings.RaiseAllChanged();
        LanguageChanged?.Invoke();
    }

    private static bool HasPack(string code) => FindPack(code) is not null;

    private static LanguagePack? FindPack(string? code) => code is null
        ? null
        : _packs.FirstOrDefault(pack => string.Equals(pack.Code, code, StringComparison.OrdinalIgnoreCase));

    private static LanguagePack EmptyPack() => new() { Code = DefaultFallbackCode, Name = DefaultFallbackCode };

    /// <summary>Picks a loaded language code from the system UI language; falls back to the first available pack if none matches.</summary>
    private static string DetectSystemLanguage()
    {
        try
        {
            var name = CultureInfo.CurrentUICulture.Name; // such as zh-CN / zh-TW / en-US

            if (name.StartsWith("en", StringComparison.OrdinalIgnoreCase) && HasPack("en"))
            {
                return "en";
            }

            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                var isTraditional =
                    name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("TW", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("HK", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("MO", StringComparison.OrdinalIgnoreCase);
                if (isTraditional && HasPack("zh-Hant"))
                {
                    return "zh-Hant";
                }

                if (HasPack("zh-Hans"))
                {
                    return "zh-Hans";
                }
            }

            // The full culture name or its two-letter prefix matches a pack directly (such as ja-JP → ja).
            if (HasPack(name))
            {
                return name;
            }

            var twoLetter = name.Length >= 2 ? name[..2] : name;
            if (HasPack(twoLetter))
            {
                return twoLetter;
            }

            // Neither Chinese nor English system: prefer English if we have it.
            if (HasPack("en"))
            {
                return "en";
            }
        }
        catch
        {
            // If detection fails, use the fallback below.
        }

        return HasPack(DefaultFallbackCode) ? DefaultFallbackCode : (_packs.Count > 0 ? _packs[0].Code : DefaultFallbackCode);
    }
}

namespace NfaLoader.Localization
{
    internal static class Loc
    {
        public static string T(string key) => key;
        public static string Tf(string key, params object[] values) => key + ": " + string.Join(", ", values);
    }
}
namespace NfaLoader.Services
{
    internal static class AppLog
    {
        public static void Warn(string text) { }
    }
}

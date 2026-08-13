using System.Runtime.CompilerServices;

namespace SoulBuddy.Services;

internal static class DefaultLanguageInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "data", "language.txt");
            if (File.Exists(path))
            {
                var saved = File.ReadAllText(path).Trim().ToLowerInvariant();
                if (saved is "de" or "en" or "fr" or "es" or "it" or "ja")
                    return;
            }

            LocalizationService.SetLanguage(AppLanguage.English);
        }
        catch
        {
            // Language selection must never prevent SoulBuddy from starting.
        }
    }
}

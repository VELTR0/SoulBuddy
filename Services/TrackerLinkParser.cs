using SoulBuddy.Models;

namespace SoulBuddy.Services;

public sealed record TrackerLinkInfo(
    TrackerProvider Provider,
    string RunId,
    bool RequiresPassword,
    string DisplayName);

public static class TrackerLinkParser
{
    public static TrackerLinkInfo Parse(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
            throw new ArgumentException("Bitte gib einen SoulLocke-Link ein.", nameof(link));

        var value = link.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            // Backwards compatibility for profiles that stored only the old
            // soullocke.com session id instead of the complete URL.
            if (!value.Contains(' ') && value.Length >= 3)
            {
                return new TrackerLinkInfo(
                    TrackerProvider.SoullockeDotCom,
                    value,
                    true,
                    "soullocke.com");
            }

            throw new ArgumentException(
                "Der SoulLocke-Link ist keine gültige URL.",
                nameof(link));
        }

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (host is "soullocke.com" or "www.soullocke.com")
        {
            return new TrackerLinkInfo(
                TrackerProvider.SoullockeDotCom,
                ExtractLegacySessionId(uri),
                true,
                "soullocke.com");
        }

        if (host is "soullocke.vercel.app" or "www.soullocke.vercel.app")
        {
            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();

            if (segments.Length != 2 ||
                !string.Equals(segments[0], "run", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(segments[1]))
            {
                throw new ArgumentException(
                    "Der soullocke.vercel.app-Link muss die Form https://soullocke.vercel.app/run/<run-id> haben.",
                    nameof(link));
            }

            return new TrackerLinkInfo(
                TrackerProvider.SoullockeVercel,
                segments[1].Trim(),
                false,
                "soullocke.vercel.app");
        }

        throw new ArgumentException(
            $"Der Tracker '{uri.Host}' wird von SoulBuddy noch nicht unterstützt. " +
            "Unterstützt werden aktuell soullocke.com und soullocke.vercel.app.",
            nameof(link));
    }

    public static bool TryParse(string? link, out TrackerLinkInfo? info)
    {
        try
        {
            info = Parse(link ?? string.Empty);
            return true;
        }
        catch (ArgumentException)
        {
            info = null;
            return false;
        }
    }

    private static string ExtractLegacySessionId(Uri uri)
    {
        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .FirstOrDefault(parts =>
                parts.Length == 2 &&
                (string.Equals(parts[0], "sessionId", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(parts[0], "session", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(parts[0], "id", StringComparison.OrdinalIgnoreCase)));

        if (query is not null)
        {
            var decoded = Uri.UnescapeDataString(query[1]).Trim();
            if (!string.IsNullOrWhiteSpace(decoded))
                return decoded;
        }

        var lastSegment = uri.Segments
            .Select(segment => segment.Trim('/'))
            .LastOrDefault(segment => !string.IsNullOrWhiteSpace(segment));

        if (!string.IsNullOrWhiteSpace(lastSegment))
            return Uri.UnescapeDataString(lastSegment);

        throw new ArgumentException(
            "Der SoulLocke-Link enthält keine erkennbare Session-ID.",
            nameof(uri));
    }
}

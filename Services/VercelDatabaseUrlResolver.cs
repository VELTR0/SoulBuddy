using System.Net;
using System.Text.RegularExpressions;

namespace SoulBuddy.Services;

internal static partial class VercelDatabaseUrlResolver
{
    private static readonly Uri TrackerRoot = new("https://soullocke.vercel.app/");
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(10);

    public static async Task<string?> TryResolveAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResolveTimeout);

        try
        {
            var html = await httpClient.GetStringAsync(TrackerRoot, timeout.Token);

            var direct = FindDatabaseUrl(html);
            if (direct is not null)
                return direct;

            var scriptUrls = ScriptSourceRegex()
                .Matches(html)
                .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
                .Where(source => source.Contains("/_next/", StringComparison.OrdinalIgnoreCase) &&
                                 source.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                .Select(source => Uri.TryCreate(source, UriKind.Absolute, out var absolute)
                    ? absolute
                    : new Uri(TrackerRoot, source))
                .Distinct()
                .Take(40)
                .ToArray();

            foreach (var scriptUrl in scriptUrls)
            {
                try
                {
                    var javascript = await httpClient.GetStringAsync(scriptUrl, timeout.Token);
                    var resolved = FindDatabaseUrl(javascript);
                    if (resolved is not null)
                        return resolved;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // A single optional Next.js chunk must not abort endpoint discovery.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Discovery is best-effort. VercelSoullockeClient has explicit fallbacks.
        }

        return null;
    }

    private static string? FindDatabaseUrl(string source)
    {
        foreach (Match match in FirebaseUrlRegex().Matches(source))
        {
            var candidate = match.Value
                .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
                .Replace("\\/", "/", StringComparison.Ordinal)
                .TrimEnd('/');

            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            }
        }

        return null;
    }

    [GeneratedRegex("<script[^>]+src=[\\\"']([^\\\"']+)[\\\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptSourceRegex();

    [GeneratedRegex("https:\\?(?:\\\\u002F|\\\\/|/){2}[A-Za-z0-9.-]+(?:firebaseio\\.com|firebasedatabase\\.app)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FirebaseUrlRegex();
}

using System.Net;
using System.Text.RegularExpressions;
using NfaLoader.Localization;

namespace NfaLoader.Services;

internal sealed partial class SteamWorkshopService
{
    // Games whose Workshop subscriptions get cleared: 730 = CS2, 431960 = Wallpaper Engine.
    private static readonly uint[] AppIds = [730, 431960];

    // Unsubscribing uses concurrent HTTP (the steamcommunity unsubscribe endpoint), which is much faster than going one by one over the CM protocol.
    // Concurrency is capped: fast, but not so fast that steamcommunity rate limits us.
    private const int MaxConcurrentUnsubscribes = 5;

    private readonly JwtTokenService _jwtTokenService = new();

    // Automatic redirects are off: steamLoginSecure (which holds the access token) is added to the Cookie header by hand,
    // and .NET's automatic redirects strip Authorization but not a manual Cookie header, so a 3xx to any domain would leak the token as is.
    // Redirects are followed by hand instead, and only https redirects within steamcommunity.com are allowed (see SendFollowingRedirectsAsync).
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private const int MaxRedirects = 3;

    public async Task<int> ClearSubscriptionsAsync(
        string eyaToken,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(Loc.T("Workshop_Progress_ValidatingToken"));
        var token = _jwtTokenService.Validate(eyaToken);

        await using var cmClient = new SteamCmClient(HttpClient);

        progress?.Report(Loc.T("Workshop_Progress_Connecting"));
        await cmClient.ConnectAndLogOnAsync(eyaToken, token.SteamId, cancellationToken);

        progress?.Report(Loc.Tf("Workshop_Progress_LoggedIn_Format", token.SteamId));
        progress?.Report(Loc.T("Workshop_Progress_GettingWebSession"));
        var session = await SteamWebSession.BuildAsync(cmClient, eyaToken, token.SteamId, cancellationToken);

        progress?.Report(Loc.T("Workshop_Progress_GettingSubscriptions"));
        var items = new List<(uint AppId, string Id, string Title)>();
        foreach (var appId in AppIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (ids, titles) = await EnumerateSubscriptionsAsync(
                appId, token.SteamId, session.CookieHeader, cancellationToken);
            foreach (var id in ids)
            {
                items.Add((appId, id, titles.GetValueOrDefault(id, "")));
            }
        }

        if (items.Count == 0)
        {
            progress?.Report(Loc.T("Workshop_Progress_NoSubscriptions"));
            return 0;
        }

        progress?.Report(Loc.Tf("Workshop_Progress_FoundSubscriptions_Format", items.Count));

        var unsubscribed = 0;
        var processed = 0;
        using var throttle = new SemaphoreSlim(MaxConcurrentUnsubscribes);

        async Task UnsubscribeOneAsync(uint appId, string id, string title)
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (ok, status) = await UnsubscribeViaWebAsync(
                    appId, id, session.CookieHeader, session.SessionId, cancellationToken);
                var current = Interlocked.Increment(ref processed);

                if (ok)
                {
                    Interlocked.Increment(ref unsubscribed);
                    progress?.Report(Loc.Tf("Workshop_Progress_ItemUnsubscribed_Format",
                        current, items.Count, id, title));
                }
                else
                {
                    progress?.Report(Loc.Tf("Workshop_Progress_ItemFailed_Format",
                        current, items.Count, id, title, status));
                }
            }
            finally
            {
                throttle.Release();
            }
        }

        await Task.WhenAll(items.Select(item => UnsubscribeOneAsync(item.AppId, item.Id, item.Title)));

        progress?.Report(Loc.Tf("Workshop_Progress_Done_Format", unsubscribed, items.Count - unsubscribed));
        return unsubscribed;
    }

    // Removes a single subscription through the steamcommunity unsubscribe endpoint. The sessionid in the POST body must match the one in the cookie.
    // The handler has automatic redirects off: an expired session gets a 3xx, which IsSuccessStatusCode(2xx) treats as a failure, so it never reports a false success.
    private static async Task<(bool Ok, int Status)> UnsubscribeViaWebAsync(
        uint appId,
        string fileId,
        string cookieHeader,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://steamcommunity.com/sharedfiles/unsubscribe");
        request.Headers.Add("Cookie", cookieHeader);
        request.Headers.Add("User-Agent", "Mozilla/5.0");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["appid"] = appId.ToString(),
            ["id"] = fileId,
            ["sessionid"] = sessionId
        });

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        return (response.IsSuccessStatusCode, (int)response.StatusCode);
    }

    private static async Task<(List<string> ids, Dictionary<string, string> titles)> EnumerateSubscriptionsAsync(
        uint appId,
        string steamId,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        var titles = new Dictionary<string, string>();
        var page = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = $"https://steamcommunity.com/profiles/{steamId}/myworkshopfiles/"
                + $"?appid={appId}&browsefilter=mysubscriptions&numperpage=30&p={page}&l=english";

            using var response = await SendFollowingRedirectsAsync(
                HttpMethod.Get,
                new Uri(url),
                cookieHeader,
                cancellationToken);

            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            foreach (Match match in SubscriptionIdRegex().Matches(html))
            {
                var id = match.Groups[1].Value;
                if (!ids.Contains(id))
                {
                    ids.Add(id);
                }
            }

            foreach (Match match in SubscriptionTitleRegex().Matches(html))
            {
                titles[match.Groups[1].Value] = WebUtility.HtmlDecode(match.Groups[2].Value.Trim());
            }

            var totalMatch = TotalEntriesRegex().Match(html);
            var total = totalMatch.Success
                ? int.Parse(totalMatch.Groups[1].Value.Replace(",", "", StringComparison.Ordinal))
                : ids.Count;

            if (ids.Count >= total || !html.Contains("workshopItemPreviewHolder", StringComparison.Ordinal))
            {
                break;
            }

            page++;
            await Task.Delay(400, cancellationToken);
        }

        return (ids, titles);
    }

    /// <summary>
    /// Sends a request and follows redirects by hand. Only https redirects to steamcommunity.com (or its subdomains) are followed,
    /// keeping the Cookie header; any other redirect target is treated as an expired session so the access token never leaks to an outside domain.
    /// Accounts with a custom URL get a 302 from /profiles/{id}/... to /id/{vanity}/... when listing Workshop items, which is a valid redirect.
    /// </summary>
    private static async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        HttpMethod method,
        Uri requestUri,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var current = requestUri;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(method, current);
            request.Headers.Add("Cookie", cookieHeader);
            request.Headers.Add("User-Agent", "Mozilla/5.0");

            var response = await HttpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode is not (>= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest))
            {
                return response;
            }

            // 3xx: decide ourselves whether to follow, and dispose of this hop's response before deciding.
            var location = response.Headers.Location;
            response.Dispose();

            if (location is null)
            {
                throw new InvalidOperationException(Loc.T("Workshop_Error_RedirectNoLocation"));
            }

            // Location may be relative (such as /id/{vanity}/...), so resolve it to an absolute address against the current Uri.
            var target = new Uri(current, location);

            if (hop >= MaxRedirects)
            {
                throw new InvalidOperationException(Loc.T("Workshop_Error_TooManyRedirects"));
            }

            if (!IsTrustedSteamCommunityUri(target))
            {
                // Leaving steamcommunity.com usually means the access token was not accepted and we were sent to the login page or similar.
                throw new InvalidOperationException(Loc.T("Workshop_Error_RedirectedExternal"));
            }

            current = target;
        }
    }

    /// <summary>Checks whether the target is an https + steamcommunity.com (or subdomain) address that is safe to follow with the Cookie.</summary>
    private static bool IsTrustedSteamCommunityUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host;
        return string.Equals(host, "steamcommunity.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".steamcommunity.com", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"filedetails/\?id=(\d+)""[^>]*><div class=""workshopItemPreviewHolder", RegexOptions.CultureInvariant)]
    private static partial Regex SubscriptionIdRegex();

    [GeneratedRegex(@"filedetails/\?id=(\d+)""[^>]*><div class=""workshopItemTitle"">([^<]*)<", RegexOptions.CultureInvariant)]
    private static partial Regex SubscriptionTitleRegex();

    [GeneratedRegex(@"of ([\d,]+) entries", RegexOptions.CultureInvariant)]
    private static partial Regex TotalEntriesRegex();
}

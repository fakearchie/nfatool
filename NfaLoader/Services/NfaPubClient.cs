using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NfaLoader.Localization;

namespace NfaLoader.Services;

internal sealed record NfaStockItem(string Type, string ProductId, string Name, decimal PriceEur, int Available);

internal sealed record NfaPurchase(string OrderId, string SteamId, string Token, decimal ChargedEur);

internal sealed record NfaPendingPurchase(
    string ProductId,
    string Type,
    string Name,
    string Key,
    DateTimeOffset StartedAt,
    string? ApiKey);

internal sealed record NfaReplacement(
    string SteamId,
    string Token,
    string ReplacedSteamId,
    int Used,
    int Remaining,
    string? CheckerReason);

/// <summary>A failure the API answered with. Code is the stable reference, such as E1401.</summary>
internal sealed class NfaApiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Client for https://www.nfa.pub/api/v1 (documented at nfa.pub/docs).
/// JSON is read with JsonDocument because reflection-based serialization is disabled for AOT.
/// </summary>
internal sealed class NfaPubClient
{
    private const string BaseUrl = "https://www.nfa.pub/api/v1/";
    private const string AccountLineSeparator = "----";

    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    // Buying and replacing wait on nfa.pub sourcing an account from a supplier, which can take well over 30 seconds.
    // A purchase that times out can be replayed with its idempotency key, so a few minutes is enough.
    private static readonly TimeSpan OrderTimeout = TimeSpan.FromMinutes(3);

    // A replacement has no key to replay and cannot be requested twice for the same account, so it waits much longer
    // rather than give up on a swap nfa.pub may still complete.
    private static readonly TimeSpan ReplaceTimeout = TimeSpan.FromMinutes(10);

    public async Task<IReadOnlyList<NfaStockItem>> GetCs2StockAsync(
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        // /products carries name, game, price and stock in one call. The live /stock response has no price.
        using var request = new HttpRequestMessage(HttpMethod.Get, "products?result=json");
        AddKey(request, apiKey);
        using var document = await SendForJsonAsync(request, cancellationToken);

        var items = new List<NfaStockItem>();
        if (!document.RootElement.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var entry in products.EnumerateArray())
        {
            // The loader only signs Steam into CS2 accounts, so Rust and the other games are not offered.
            var id = ReadString(entry, "id");
            if (ReadString(entry, "game") != "CS2" || string.IsNullOrEmpty(id))
            {
                continue;
            }

            items.Add(new NfaStockItem(
                BuyTypeFor(id),
                id,
                ReadString(entry, "name") ?? id,
                ReadInt(entry, "priceCents") / 100m,
                ReadInt(entry, "available")));
        }

        return items;
    }

    // Product ids from /products mapped to the type values /cs2 documents. An id with no documented type is
    // sent as-is; if the API does not accept it, it refuses the request before charging anything.
    private static string BuyTypeFor(string productId) => productId switch
    {
        "prime-ready" => "prime",
        "premier-ready" => "premier",
        "premier-ready-4-medals" => "premier-4-medals",
        "premier-ready-10-medals" => "premier-10-medals",
        "premier-ready-10k-rating" => "premier-10k",
        "premier-ready-15k-rating" => "premier-15k",
        "premier-ready-20k-rating" => "premier-20k",
        _ => productId,
    };


    /// <summary>Reads the balance the key spends. Doubles as the check that a key is valid.</summary>
    public async Task<decimal> GetBalanceAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "balance");
        AddKey(request, apiKey);
        using var response = await SendAsync(request, cancellationToken);
        var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();

        return decimal.TryParse(body, NumberStyles.Number, CultureInfo.InvariantCulture, out var balance)
            ? balance
            : throw new NfaApiException("E1000", Loc.T("Nfa_Error_UnexpectedResponse"));
    }

    /// <summary>
    /// Buys one CS2 account and charges the key's balance.
    /// The idempotency key must stay the same for retries of one purchase: the API then returns the
    /// original order instead of charging again, so a timeout never turns into a second account.
    /// </summary>
    public async Task<NfaPurchase> BuyCs2Async(
        string apiKey,
        string type,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var path = $"cs2?type={Uri.EscapeDataString(type)}&quantity=1&result=json";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        AddKey(request, apiKey);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var document = await SendForJsonAsync(request, cancellationToken, OrderTimeout);

        var root = document.RootElement;
        var orderId = ReadString(root, "order_id") ?? "";
        if (!root.TryGetProperty("accounts", out var accounts) ||
            accounts.ValueKind != JsonValueKind.Array ||
            accounts.GetArrayLength() == 0 ||
            accounts[0].ValueKind != JsonValueKind.String ||
            accounts[0].GetString() is not { } line)
        {
            // The order may already be charged at this point, so record everything that identifies it.
            AppLog.Error($"nfa.pub purchase of {type}: unreadable response. order={orderId} " +
                $"fields=[{string.Join(",", root.EnumerateObject().Select(p => p.Name))}]");
            throw new NfaApiException("E1000", Loc.T("Nfa_Error_UnexpectedResponse"));
        }

        (string SteamId, string Token) parsed;
        try
        {
            parsed = ParseAccountLine(line);
        }
        catch (NfaApiException)
        {
            AppLog.Error($"nfa.pub purchase of {type}: account line not understood. order={orderId} " +
                $"segments={line.Split(AccountLineSeparator).Length}");
            throw;
        }

        var charged = ReadDecimal(root, "charged_eur");
        AppLog.Info($"nfa.pub bought {type}: order={orderId} steamid={parsed.SteamId} charged=EUR {FormatEur(charged)} " +
            $"replayed={(root.TryGetProperty("cached", out var cached) && cached.ValueKind == JsonValueKind.True)}");
        return new NfaPurchase(orderId, parsed.SteamId, parsed.Token, charged);
    }

    /// <summary>Swaps a faulty account on one of the key owner's orders, inside the warranty window.</summary>
    public async Task<NfaReplacement> ReplaceAsync(
        string apiKey,
        string order,
        string steamId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "replace?result=json");
        AddKey(request, apiKey);
        request.Content = new StringContent(BuildReplaceBody(order, steamId, reason), Encoding.UTF8, "application/json");

        using var response = await SendAsync(request, cancellationToken, ReplaceTimeout);
        var checkerReason = response.Headers.TryGetValues("X-Replace-Reason", out var values)
            ? values.FirstOrDefault()
            : null;

        // A 2xx that cannot be read may still be a completed swap, so it is reported as unconfirmed, not refused.
        JsonDocument document;
        try
        {
            document = await ParseJsonAsync(response, cancellationToken);
        }
        catch (NfaApiException)
        {
            AppLog.Error($"nfa.pub replace of {steamId} on order {order}: HTTP {(int)response.StatusCode} body is not JSON");
            throw new NfaApiException("UNREADABLE", Loc.T("Nfa_Error_UnexpectedResponse"));
        }

        using var owned = document;
        var root = document.RootElement;
        (string SteamId, string Token) parsed;
        try
        {
            parsed = ReadString(root, "account") is { } line
                ? ParseAccountLine(line)
                : throw new NfaApiException("E1000", Loc.T("Nfa_Error_UnexpectedResponse"));
        }
        catch (NfaApiException)
        {
            AppLog.Error($"nfa.pub replace of {steamId} on order {order}: unreadable response. " +
                $"fields=[{string.Join(",", root.EnumerateObject().Select(p => p.Name))}] replaced={ReadString(root, "replaced")}");
            throw new NfaApiException("UNREADABLE", Loc.T("Nfa_Error_UnexpectedResponse"));
        }

        var (newSteamId, token) = parsed;
        AppLog.Info($"nfa.pub replaced {steamId} with {newSteamId} on order {order}: " +
            $"used={ReadInt(root, "used")} remaining={ReadInt(root, "remaining")} reason={checkerReason ?? "none"}");
        return new NfaReplacement(
            newSteamId,
            token,
            ReadString(root, "replaced") ?? steamId,
            ReadInt(root, "used"),
            ReadInt(root, "remaining"),
            checkerReason);
    }

    /// <summary>Euro amounts always print with two decimals and a dot, whatever the Windows locale.</summary>
    public static string FormatEur(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>A readable name for an API account type such as "premier-10k", falling back to the raw type.</summary>
    public static string TypeDisplayName(string type)
    {
        var key = $"Nfa_Type_{type}";
        var text = Loc.T(key);
        return text == key ? type : text;
    }

    /// <summary>
    /// Reads an nfa.pub account line. Real lines are "steamid----token----key:value----key:value...", with
    /// account details after the token, so the Steam ID and token are picked out by shape, not position.
    /// </summary>
    public static (string SteamId, string Token) ParseAccountLine(string line)
    {
        var segments = line.Split(AccountLineSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var steamId = segments.FirstOrDefault(segment => segment.Length == 17 && segment.All(char.IsAsciiDigit));
        var token = FormatHelper.NormalizeToken(line);
        if (steamId is null || !FormatHelper.LooksLikeToken(token))
        {
            throw new NfaApiException("E1000", Loc.T("Nfa_Error_UnexpectedResponse"));
        }

        return (steamId, token);
    }

    /// <summary>
    /// Maps an error reference to plain wording. The API promises the codes are stable and says to
    /// branch on them rather than parse its generic error text.
    /// </summary>
    public static string DescribeError(string code)
    {
        var key = $"Nfa_Error_{code}";
        var text = Loc.T(key);
        return text == key ? Loc.Tf("Nfa_Error_Unknown_Format", code) : text;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            // Each request sets its own limit instead, so a purchase can wait longer than a stock read.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"nfa-pub-loader/{GitHubUpdateService.CurrentVersion}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    // A header rather than ?key=, so the key never lands in a URL that a proxy or log might keep.
    private static void AddKey(HttpRequestMessage request, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Add("X-Api-Key", apiKey.Trim());
        }
    }

    private static async Task<JsonDocument> SendForJsonAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var response = await SendAsync(request, cancellationToken, timeout);
        return await ParseJsonAsync(response, cancellationToken);
    }

    // Every request is logged with its outcome and duration. The key travels in a header and tokens only in
    // bodies, so the method and path written here never contain a secret.
    private static async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var what = $"{request.Method} /api/v1/{request.RequestUri}";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? ReadTimeout);
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, limit.Token);
        }
        // The last attempt could not connect. That does not prove nfa.pub never saw the request: the handler silently
        // resends a GET whose pooled connection dropped, so a purchase treats this like any other unconfirmed failure.
        // A POST is never resent, so for a replacement it does mean nothing was sent.
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError)
        {
            AppLog.Warn($"nfa.pub {what} not reached after {clock.ElapsedMilliseconds} ms: {ex.HttpRequestError} ({ex.Message})");
            throw new NfaApiException("OFFLINE", Loc.T("Nfa_Error_Network"));
        }
        catch (HttpRequestException ex)
        {
            AppLog.Warn($"nfa.pub {what} failed after {clock.ElapsedMilliseconds} ms: network error ({ex.Message})");
            throw new NfaApiException("NETWORK", Loc.T("Nfa_Error_Network"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn($"nfa.pub {what} timed out after {clock.ElapsedMilliseconds} ms");
            throw new NfaApiException("TIMEOUT", Loc.T("Nfa_Error_Timeout"));
        }
        catch (OperationCanceledException)
        {
            AppLog.Info($"nfa.pub {what} cancelled by the user after {clock.ElapsedMilliseconds} ms");
            throw;
        }

        if (response.IsSuccessStatusCode)
        {
            AppLog.Info($"nfa.pub {what} -> HTTP {(int)response.StatusCode} in {clock.ElapsedMilliseconds} ms");
            return response;
        }

        using (response)
        {
            var code = await ReadErrorCodeAsync(response, cancellationToken);
            AppLog.Warn($"nfa.pub {what} -> HTTP {(int)response.StatusCode} {code} in {clock.ElapsedMilliseconds} ms");
            throw new NfaApiException(code, DescribeError(code));
        }
    }

    // The header is sent on every failure, including plain-text ones, so it is read before the body.
    private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Headers.TryGetValues("X-Error-Code", out var values) && values.FirstOrDefault() is { Length: > 0 } header)
        {
            return header.Trim();
        }

        try
        {
            var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            if (body.StartsWith('{'))
            {
                using var document = JsonDocument.Parse(body);
                if (ReadString(document.RootElement, "code") is { Length: > 0 } code)
                {
                    return code;
                }
            }
            else if (body.Length is > 1 and < 16 && body[0] == 'E')
            {
                return body;
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
        }

        // A server error without a code may come from a proxy in front of nfa.pub after the request was handled, so
        // it proves nothing either way.
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "E1001",
            HttpStatusCode.TooManyRequests => "E1002",
            >= HttpStatusCode.InternalServerError => "UNCONFIRMED",
            _ => "E1000",
        };
    }

    private static async Task<JsonDocument> ParseJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw new NfaApiException("E1000", Loc.T("Nfa_Error_UnexpectedResponse"));
        }
    }

    private static string BuildReplaceBody(string order, string steamId, string? reason)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("order", order.Trim());
            writer.WriteString("account", steamId.Trim());
            if (!string.IsNullOrWhiteSpace(reason))
            {
                writer.WriteString("reason", reason.Trim());
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal ReadDecimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : 0m;

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;
}

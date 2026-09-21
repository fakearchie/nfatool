using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using NfaLoader.Localization;

namespace NfaLoader.Services;

/// <summary>
/// Applies the nickname / real name / summary / avatar set in the "Personalization" panel to the target Steam account, and can clear the name history at the end.
/// Everything goes through the Steam Web API / steamcommunity, not the CM WebSocket: the EYA refresh token is exchanged for an access token over the web
/// (<see cref="SteamWebSession.BuildViaWebApiAsync"/>), nickname / real name / summary go through the profile form (/edit/),
/// the avatar through FileUploader, and name history through ajaxclearaliashistory.
/// Note: saving on the web submits the whole profile form, so the current profile is read first and fields the user left blank are filled with their current values instead of being cleared.
/// </summary>
internal sealed partial class SteamProfileService
{
    private const string FileUploaderUrl = "https://steamcommunity.com/actions/FileUploader";

    private readonly JwtTokenService _jwtTokenService = new();

    // Same locked-down client as SteamWorkshopService: steamLoginSecure (which holds the access token) is set by hand in the Cookie header,
    // and auto redirects are off so a 3xx to an external domain cannot leak the token in the Cookie header.
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    public async Task<SteamProfileApplyResult> ApplyAsync(
        string eyaToken,
        SteamProfileApplyRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = NullIfBlank(request.PersonaName);
        var trimmedRealName = NullIfBlank(request.RealName);
        // TextBox uses \r for line breaks internally; Steam stores the summary with \n, so normalize before submitting.
        var trimmedSummary = NullIfBlank(request.Summary)?.Replace("\r\n", "\n").Replace('\r', '\n');
        var hasProfileFields = trimmedName is not null || trimmedRealName is not null || trimmedSummary is not null;
        var hasAvatar = !string.IsNullOrWhiteSpace(request.AvatarImagePath) && File.Exists(request.AvatarImagePath);

        if (!hasProfileFields && !hasAvatar && !request.ClearAliasHistory)
        {
            throw new InvalidOperationException(Loc.T("Profile_Error_NothingToApply"));
        }

        progress?.Report(Loc.T("Profile_Progress_ValidatingToken"));
        var token = _jwtTokenService.Validate(eyaToken);

        // The web access token must come from a signed-in CM session: GenerateAccessTokenForApp / finalizelogin on the anonymous web
        // always return AccessDenied(15); these tokens need an authenticated session to activate them. Same mechanism as "Clear Workshop subscriptions".
        progress?.Report(Loc.T("Profile_Progress_Connecting"));
        await using var cmClient = new SteamCmClient(HttpClient);
        await cmClient.ConnectAndLogOnAsync(eyaToken, token.SteamId, cancellationToken);

        progress?.Report(Loc.T("Profile_Progress_GettingWebSession"));
        var session = await SteamWebSession.BuildAsync(cmClient, eyaToken, token.SteamId, cancellationToken);

        var profileApplied = false;
        string? profileError = null;
        if (hasProfileFields)
        {
            progress?.Report(Loc.T("Profile_Progress_SavingProfile"));
            (profileApplied, profileError) = await RunStepAsync(
                () => SaveProfileAsync(token.SteamId, trimmedName, trimmedRealName, trimmedSummary, session, cancellationToken),
                cancellationToken);
        }

        var avatarApplied = false;
        string? avatarError = null;
        if (hasAvatar)
        {
            progress?.Report(Loc.T("Profile_Progress_UploadingAvatar"));
            (avatarApplied, avatarError) = await RunStepAsync(
                () => UploadAvatarAsync(token.SteamId, request.AvatarImagePath!, session, cancellationToken),
                cancellationToken);
        }

        // Clear name history last: after a successful rename the old nickname has just been added to the history, so clearing now removes it too.
        var aliasesCleared = false;
        string? aliasClearError = null;
        if (request.ClearAliasHistory)
        {
            progress?.Report(Loc.T("Profile_Progress_ClearingAliases"));
            (aliasesCleared, aliasClearError) = await RunStepAsync(
                () => ClearAliasHistoryAsync(token.SteamId, session, cancellationToken),
                cancellationToken);
        }

        return new SteamProfileApplyResult(
            hasProfileFields, profileApplied, profileError,
            trimmedName is not null,
            hasAvatar, avatarApplied, avatarError,
            request.ClearAliasHistory, aliasesCleared, aliasClearError);
    }

    private static string? NullIfBlank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    // Per-step exception isolation: network-level exceptions (HttpRequestException / timeouts) become that step's failure reason, so the caller gets
    // partial results and saves the steps that succeeded. Otherwise a later step that throws would keep an earlier rename/avatar change out of the local record.
    // Real user cancellation still propagates (a timeout's TaskCanceledException does not carry the request's cancellation flag, so it is folded in, not mistaken for a cancel).
    private static async Task<(bool Success, string? Error)> RunStepAsync(
        Func<Task<(bool Success, string? Error)>> step,
        CancellationToken cancellationToken)
    {
        try
        {
            return await step();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("Personalization step failed.", ex);
            return (false, ex.Message);
        }
    }

    // ---- Nickname / real name / summary (profile form) ----

    private static async Task<(bool Success, string? Error)> SaveProfileAsync(
        string steamId,
        string? personaName,
        string? realName,
        string? summary,
        SteamWebSession session,
        CancellationToken cancellationToken)
    {
        // Read the current profile first: saving on the web submits the whole profile form, so blank fields are filled with current values instead of being cleared.
        var current = await TryGetCurrentProfileAsync(steamId, session, cancellationToken);

        // Abort when the user did not change the nickname and the current one cannot be read: the nickname is required, and an empty personaName makes Steam reject the whole form
        // (and if it were accepted, it would clear the nickname).
        var effectivePersonaName = personaName ?? current.PersonaName;
        if (string.IsNullOrEmpty(effectivePersonaName))
        {
            return (false, Loc.T("Profile_Error_CurrentProfileUnavailable"));
        }

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("sessionID", session.SessionId),
            new KeyValuePair<string, string>("type", "profileSave"),
            new KeyValuePair<string, string>("personaName", effectivePersonaName),
            new KeyValuePair<string, string>("real_name", realName ?? current.RealName),
            new KeyValuePair<string, string>("customURL", current.CustomUrl),
            new KeyValuePair<string, string>("summary", summary ?? current.Summary),
            new KeyValuePair<string, string>("hide_profile_awards", "0"),
            new KeyValuePair<string, string>("json", "1")
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://steamcommunity.com/profiles/{steamId}/edit/") { Content = form };
        request.Headers.Add("Cookie", session.CookieHeader);
        request.Headers.Add("User-Agent", "Mozilla/5.0");

        using var response = await HttpClient.SendAsync(request, cancellationToken);

        // 3xx: a rejected session is sent to the login page or similar; treat it as an expired session and never follow to an external domain.
        if (response.StatusCode is >= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest)
        {
            return (false, Loc.T("Profile_Error_ProfileSessionRejected"));
        }

        if (!response.IsSuccessStatusCode)
        {
            return (false, Loc.Tf("Profile_Error_ProfileHttp_Format", (int)response.StatusCode));
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseProfileSaveResponse(responseText);
    }

    private static async Task<ProfileFields> TryGetCurrentProfileAsync(
        string steamId, SteamWebSession session, CancellationToken cancellationToken)
    {
        try
        {
            var html = await GetFollowingRedirectsAsync(
                new Uri($"https://steamcommunity.com/profiles/{steamId}/edit/info"),
                session.CookieHeader,
                cancellationToken);
            var fields = ParseProfileFields(html);
            if (fields == ProfileFields.Empty)
            {
                // A normal account always has a nickname: all empty means the page layout changed or the edit page was not returned, so log it for diagnosis.
                AppLog.Warn("No current values parsed from the Steam profile edit page (the page layout may have changed). Saving will not keep fields left blank.");
            }

            return fields;
        }
        catch (Exception ex)
        {
            // Failing to read the current profile does not block saving, but log it: fields the user left blank may get cleared.
            AppLog.Error("Failed to read the current Steam profile. Saving will not keep fields left blank.", ex);
            return ProfileFields.Empty;
        }
    }

    private static ProfileFields ParseProfileFields(string html)
    {
        // The current profile edit page is a React app: current values are in the HTML-escaped JSON of the data-profile-edit-config attribute
        // (strPersonaName / strRealName / strSummary / strCustomURL). The legacy form fields are only a fallback.
        var fromConfig = TryParseProfileEditConfig(html);
        if (fromConfig is not null && fromConfig != ProfileFields.Empty)
        {
            return fromConfig;
        }

        string Extract(Regex regex)
        {
            var match = regex.Match(html);
            return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : string.Empty;
        }

        return new ProfileFields(
            PersonaName: Extract(LegacyPersonaNameRegex()),
            RealName: Extract(RealNameRegex()),
            Summary: Extract(SummaryRegex()),
            CustomUrl: Extract(CustomUrlRegex()));
    }

    private static ProfileFields? TryParseProfileEditConfig(string html)
    {
        var match = ProfileEditConfigRegex().Match(html);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            var json = WebUtility.HtmlDecode(match.Groups[1].Value);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            string Get(string name) =>
                root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : string.Empty;

            return new ProfileFields(
                PersonaName: Get("strPersonaName"),
                RealName: Get("strRealName"),
                Summary: Get("strSummary"),
                CustomUrl: Get("strCustomURL"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (bool Success, string? Error) ParseProfileSaveResponse(string responseText)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;

            var success = root.TryGetProperty("success", out var successElement) &&
                (successElement.ValueKind == JsonValueKind.True ||
                    (successElement.ValueKind == JsonValueKind.Number &&
                        successElement.TryGetInt32(out var flag) && flag == 1));

            if (success)
            {
                return (true, null);
            }

            var message = root.TryGetProperty("errmsg", out var messageElement)
                ? messageElement.GetString()
                : null;

            return (false, string.IsNullOrWhiteSpace(message) ? Loc.T("Profile_Error_ProfileRejected") : message);
        }
        catch (JsonException)
        {
            // Not JSON (usually HTML from a redirect to the login page) → treat as failure.
            return (false, Loc.T("Profile_Error_ProfileRejected"));
        }
    }

    // ---- Avatar (FileUploader) ----

    private static async Task<(bool Success, string? Error)> UploadAvatarAsync(
        string steamId,
        string imagePath,
        SteamWebSession session,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(fileContent, "avatar", "avatar.jpg");
        form.Add(new StringContent("player_avatar_image"), "type");
        form.Add(new StringContent(steamId), "sId");
        form.Add(new StringContent(session.SessionId), "sessionid");
        form.Add(new StringContent("1"), "doSub");
        form.Add(new StringContent("1"), "json");

        using var request = new HttpRequestMessage(HttpMethod.Post, FileUploaderUrl) { Content = form };
        request.Headers.Add("Cookie", session.CookieHeader);
        request.Headers.Add("User-Agent", "Mozilla/5.0");
        request.Headers.Referrer = new Uri($"https://steamcommunity.com/profiles/{steamId}/edit/avatar");

        using var response = await HttpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode is >= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest)
        {
            return (false, Loc.T("Profile_Error_AvatarSessionRejected"));
        }

        if (!response.IsSuccessStatusCode)
        {
            return (false, Loc.Tf("Profile_Error_AvatarHttp_Format", (int)response.StatusCode));
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseUploadResponse(responseText);
    }

    private static (bool Success, string? Error) ParseUploadResponse(string responseText)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;

            var success = root.TryGetProperty("success", out var successElement) &&
                (successElement.ValueKind == JsonValueKind.True ||
                    (successElement.ValueKind == JsonValueKind.Number &&
                        successElement.TryGetInt32(out var flag) && flag != 0));

            if (success)
            {
                return (true, null);
            }

            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;

            return (false, string.IsNullOrWhiteSpace(message) ? Loc.T("Profile_Error_AvatarRejected") : message);
        }
        catch (JsonException)
        {
            return (false, Loc.T("Profile_Error_AvatarBadResponse"));
        }
    }

    // ---- Name history (ajaxclearaliashistory) ----

    private static async Task<(bool Success, string? Error)> ClearAliasHistoryAsync(
        string steamId,
        SteamWebSession session,
        CancellationToken cancellationToken)
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("sessionid", session.SessionId)
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://steamcommunity.com/profiles/{steamId}/ajaxclearaliashistory/") { Content = form };
        request.Headers.Add("Cookie", session.CookieHeader);
        request.Headers.Add("User-Agent", "Mozilla/5.0");

        using var response = await HttpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode is >= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest)
        {
            return (false, Loc.T("Profile_Error_AliasSessionRejected"));
        }

        if (!response.IsSuccessStatusCode)
        {
            return (false, Loc.Tf("Profile_Error_AliasHttp_Format", (int)response.StatusCode));
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        // The response is {"success": <EResult>}, 1 = OK; non-JSON (usually login page HTML) counts as failure.
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;

            var success = root.TryGetProperty("success", out var successElement) &&
                (successElement.ValueKind == JsonValueKind.True ||
                    (successElement.ValueKind == JsonValueKind.Number &&
                        successElement.TryGetInt32(out var result) && result == 1));

            return success ? (true, null) : (false, Loc.T("Profile_Error_AliasRejected"));
        }
        catch (JsonException)
        {
            return (false, Loc.T("Profile_Error_AliasRejected"));
        }
    }

    // ---- Shared: follow steamcommunity internal redirects by hand (GET on the profile page of an account with a vanity URL returns 302) ----

    private static async Task<string> GetFollowingRedirectsAsync(
        Uri uri, string cookieHeader, CancellationToken cancellationToken)
    {
        var current = uri;
        for (var hop = 0; hop < 4; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Add("Cookie", cookieHeader);
            request.Headers.Add("User-Agent", "Mozilla/5.0");

            var response = await HttpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode is not (>= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest))
            {
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Dispose();
                return body;
            }

            var location = response.Headers.Location;
            response.Dispose();

            if (location is null)
            {
                throw new InvalidOperationException("redirect without location");
            }

            var target = new Uri(current, location);
            if (!IsTrustedSteamCommunityUri(target))
            {
                throw new InvalidOperationException("redirected to external host");
            }

            current = target;
        }

        throw new InvalidOperationException("too many redirects");
    }

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

    // Current-values JSON on the React edit page (HTML attribute escaped).
    [GeneratedRegex(@"data-profile-edit-config=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileEditConfigRegex();

    // Current values of these fields on the legacy form edit page (fallback, so saving does not clear them).
    [GeneratedRegex(@"id=""personaName""[^>]*?\bvalue=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyPersonaNameRegex();

    [GeneratedRegex(@"id=""real_name""[^>]*?\bvalue=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex RealNameRegex();

    [GeneratedRegex(@"id=""customURL""[^>]*?\bvalue=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex CustomUrlRegex();

    [GeneratedRegex(@"id=""summary""[^>]*?>(.*?)</textarea>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SummaryRegex();

    private sealed record ProfileFields(string PersonaName, string RealName, string Summary, string CustomUrl)
    {
        public static ProfileFields Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);
    }
}

/// <summary>What one-click personalization applies. A null/blank field means leave it unchanged; ClearAliasHistory runs after the other steps.</summary>
internal sealed record SteamProfileApplyRequest(
    string? PersonaName,
    string? RealName,
    string? Summary,
    string? AvatarImagePath,
    bool ClearAliasHistory);

/// <summary>
/// Personalization result: for the profile form (nickname / real name / summary), the avatar and name history clearing, records whether each was requested, whether it succeeded, and why it failed.
/// NameRequested separately records whether the nickname was changed in the form, so the caller can decide whether to update the local account record.
/// </summary>
internal sealed record SteamProfileApplyResult(
    bool ProfileRequested,
    bool ProfileApplied,
    string? ProfileError,
    bool NameRequested,
    bool AvatarRequested,
    bool AvatarApplied,
    string? AvatarError,
    bool AliasClearRequested,
    bool AliasesCleared,
    string? AliasClearError)
{
    /// <summary>A nickname change was requested and the profile form saved successfully.</summary>
    public bool NameApplied => NameRequested && ProfileApplied;

    public bool IsFullSuccess =>
        (!ProfileRequested || ProfileApplied) &&
        (!AvatarRequested || AvatarApplied) &&
        (!AliasClearRequested || AliasesCleared);
}

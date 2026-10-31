using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 官方多人登录：用 Steam 票据校验本作所有权，签发短期入场令牌。
///
/// 票据只在这里兑换一次，不落库、不写日志；令牌由世界进程用同一份密钥校验。
/// Web API 密钥和令牌密钥都只从服务端环境变量读取。
/// </summary>
public sealed class SteamLogin
{
    public const string WebApiPath = "/ISteamUserAuth/AuthenticateUserTicket/v1/";
    public const string OwnershipApiPath = "/ISteamUser/GetPublisherAppOwnership/v4/";
    public const string TicketIdentity = "wheatstead-multiplayer";
    public const uint PlaytestAppId = 5494990;
    public const uint ReleaseAppId = 5485050;
    public const uint DemoAppId = 5489270;

    /// <summary>可接受的发行 AppID。官方部署用默认三项；开源版用
    /// WHEATSTEAD_STEAM_APP_IDS 改成自己 App 的 ID（逗号分隔）。</summary>
    public static readonly IReadOnlySet<uint> AllowedAppIds = ReadAllowedAppIds();

    private static HashSet<uint> ReadAllowedAppIds()
    {
        var ids = new HashSet<uint>();
        string raw = Environment.GetEnvironmentVariable("WHEATSTEAD_STEAM_APP_IDS")?.Trim() ?? "";
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (uint.TryParse(part, out uint id) && id > 0) ids.Add(id);
        if (ids.Count == 0) { ids.Add(ReleaseAppId); ids.Add(PlaytestAppId); ids.Add(DemoAppId); }
        return ids;
    }

    public static bool IsOfficialAppId(uint appId) => AllowedAppIds.Contains(appId);

    private readonly SteamLoginConfig _config;
    private readonly HttpClient _http;
    private readonly Func<string, long?> _openGeneration;

    public SteamLogin(SteamLoginConfig config, Func<string, long?> openGeneration, HttpClient? http = null)
    {
        _config = config;
        _openGeneration = openGeneration;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public bool Enabled => _config.Enabled;

    public async Task<SteamLoginOutcome> LoginAsync(SteamLoginRequest request, CancellationToken ct)
    {
        if (!Enabled) return new(503, "steam_login_disabled");
        if (request?.AppId is not uint appId || !IsOfficialAppId(appId))
            return new(400, "unsupported_appid");

        string ticket = request.Ticket?.Trim() ?? "";
        if (ticket.Length is < 16 or > 4096 || ticket.Length % 2 != 0 || !ticket.All(char.IsAsciiHexDigit))
            return new(400, "invalid_ticket");

        string world = request.World?.Trim() ?? "";
        if (world.Length is < 1 or > 64 || world.Any(char.IsControl))
            return new(400, "invalid_world");

        var verified = _config.RelayConfigured
            ? await VerifyViaRelayAsync(appId, ticket, ct)
            : await VerifyTicketAsync(appId, ticket, ct);
        if (verified.Status != 200) return new(verified.Status, verified.Error);
        long? generation = _openGeneration(world);
        if (generation is null or <= 0) return new(409, "world_unavailable");

        long expiresAt = DateTimeOffset.UtcNow.AddSeconds(_config.TokenTtlSeconds).ToUnixTimeSeconds();
        var claims = new SteamTokenClaims(verified.SteamId, appId, world, expiresAt,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), generation.Value);
        string token = IssueToken(claims, _config.TokenSecret);
        return new(200, "", token, expiresAt, verified.FamilyShared, verified.VacBanned, verified.PublisherBanned);
    }

    /// <summary>只验票据和所有权，不签发令牌；代验证服务用。</summary>
    internal async Task<SteamVerifyResult> VerifyOnlyAsync(uint appId, string ticket, CancellationToken ct)
    {
        string trimmed = ticket?.Trim() ?? "";
        if (trimmed.Length is < 16 or > 4096 || trimmed.Length % 2 != 0 || !trimmed.All(char.IsAsciiHexDigit))
            return new(400, "invalid_ticket");
        return _config.RelayConfigured
            ? await VerifyViaRelayAsync(appId, trimmed, ct)
            : await VerifyTicketAsync(appId, trimmed, ct);
    }

    /// <summary>代验证模式：把票据转发给中心服务，Key 留在中心服务上。</summary>
    private async Task<SteamVerifyResult> VerifyViaRelayAsync(uint appId, string ticket, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _config.RelayUrl + "/v1/license/verify");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.RelayToken);
            request.Content = new StringContent(JsonSerializer.Serialize(new { app_id = appId, ticket }),
                System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, ct);
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                string code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() ?? "steam_unavailable" : "steam_unavailable";
                return new((int)response.StatusCode, code);
            }
            string steamId = root.TryGetProperty("steamid", out var sid) && sid.ValueKind == JsonValueKind.String
                ? sid.GetString() ?? "" : "";
            if (!MultiplayerStore.ValidSteamId(steamId)) return new(502, "steam_unavailable");
            bool family = root.TryGetProperty("family_shared", out var f) && f.ValueKind == JsonValueKind.True;
            bool vac = root.TryGetProperty("vac_banned", out var v) && v.ValueKind == JsonValueKind.True;
            bool pub = root.TryGetProperty("publisher_banned", out var p) && p.ValueKind == JsonValueKind.True;
            if (family && !_config.AllowFamilySharing) return new(403, "steam_ownership_required");
            return new(200, "", steamId, family, vac, pub);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(502, "steam_unavailable"); }
        catch (HttpRequestException) { return new(502, "steam_unavailable"); }
        catch (JsonException) { return new(502, "steam_unavailable"); }
    }

    private async Task<SteamVerifyResult> VerifyTicketAsync(uint appId, string ticket, CancellationToken ct)
    {
        string url = $"{_config.ApiBase}{WebApiPath}?key={Uri.EscapeDataString(_config.WebApiKey)}"
                   + $"&appid={appId}&ticket={Uri.EscapeDataString(ticket)}"
                   + $"&identity={Uri.EscapeDataString(TicketIdentity)}";
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return new(502, "steam_unavailable");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("response", out var apiResponse)
                || apiResponse.ValueKind != JsonValueKind.Object
                || !apiResponse.TryGetProperty("params", out var parameters)
                || parameters.ValueKind != JsonValueKind.Object)
                return new(502, "steam_unavailable");

            string result = parameters.TryGetProperty("result", out var resultValue)
                && resultValue.ValueKind == JsonValueKind.String ? resultValue.GetString() ?? "" : "";
            if (!string.Equals(result, "OK", StringComparison.Ordinal))
                return new(401, "steam_ticket_rejected");

            string steamId = parameters.TryGetProperty("steamid", out var sid) && sid.ValueKind == JsonValueKind.String
                ? sid.GetString() ?? "" : "";
            if (!MultiplayerStore.ValidSteamId(steamId))
                return new(502, "steam_unavailable");

            // Demo 是免费发行：票据已证明玩家在运行 Demo，而免费 Demo 在出版商
            // 所有权接口里可能还没有记录，因此只校验票据。
            if (appId == DemoAppId)
            {
                bool demoVac = parameters.TryGetProperty("vacbanned", out var dv) && dv.ValueKind == JsonValueKind.True;
                bool demoPub = parameters.TryGetProperty("publisherbanned", out var dp) && dp.ValueKind == JsonValueKind.True;
                return new(200, "", steamId, false, demoVac, demoPub);
            }

            // AuthenticateUserTicket proves identity; it does not establish an active license.
            string ownershipUrl = $"{_config.ApiBase}{OwnershipApiPath}?key={Uri.EscapeDataString(_config.WebApiKey)}"
                                  + $"&steamid={Uri.EscapeDataString(steamId)}";
            using var ownershipResponse = await _http.GetAsync(ownershipUrl, ct);
            if (!ownershipResponse.IsSuccessStatusCode) return new(502, "steam_unavailable");
            await using var ownershipStream = await ownershipResponse.Content.ReadAsStreamAsync(ct);
            using var ownershipDoc = await JsonDocument.ParseAsync(ownershipStream, cancellationToken: ct);
            if (ownershipDoc.RootElement.ValueKind != JsonValueKind.Object
                || !ownershipDoc.RootElement.TryGetProperty("appownership", out var ownership)
                || ownership.ValueKind != JsonValueKind.Object
                || !ownership.TryGetProperty("apps", out var apps)
                || apps.ValueKind != JsonValueKind.Array)
                return new(502, "steam_unavailable");

            JsonElement matched = default;
            int matches = 0;
            foreach (var app in apps.EnumerateArray())
            {
                if (app.ValueKind != JsonValueKind.Object
                    || !app.TryGetProperty("appid", out var id)
                    || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt32(out uint candidate))
                    return new(502, "steam_unavailable");
                if (candidate == appId) { matched = app; matches++; }
            }
            if (matches == 0) return new(403, "steam_ownership_required");
            if (matches != 1 || !matched.TryGetProperty("ownsapp", out var owns)
                || (owns.ValueKind != JsonValueKind.True && owns.ValueKind != JsonValueKind.False))
                return new(502, "steam_unavailable");
            if (!owns.GetBoolean()) return new(403, "steam_ownership_required");

            string ownerId = matched.TryGetProperty("ownersteamid", out var owner) && owner.ValueKind == JsonValueKind.String
                ? owner.GetString() ?? "" : "";
            // Without the owner identity we cannot enforce the family-sharing policy.
            if (!MultiplayerStore.ValidSteamId(ownerId)) return new(502, "steam_unavailable");
            bool familyShared = !string.Equals(steamId, ownerId, StringComparison.Ordinal);
            if (familyShared && !_config.AllowFamilySharing)
                return new(403, "steam_ownership_required");

            bool vacBanned = parameters.TryGetProperty("vacbanned", out var vac) && vac.ValueKind == JsonValueKind.True;
            bool publisherBanned = parameters.TryGetProperty("publisherbanned", out var pub)
                && pub.ValueKind == JsonValueKind.True;
            return new(200, "", steamId, familyShared, vacBanned, publisherBanned);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(502, "steam_unavailable");
        }
        catch (HttpRequestException)
        {
            return new(502, "steam_unavailable");
        }
        catch (JsonException)
        {
            return new(502, "steam_unavailable");
        }
    }

    public static string IssueToken(SteamTokenClaims claims, string secret)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new SteamTokenPayload(
            claims.SteamId, claims.AppId, claims.World, claims.ExpiresAt, claims.Nonce, claims.Generation));
        string body = Base64Url(payload);
        byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(body));
        return body + "." + Base64Url(mac);
    }

    /// <summary>世界进程用同一密钥校验令牌；expectedWorld 为空表示不校验世界。</summary>
    public static bool TryValidateToken(string token, string secret, string expectedWorld,
        out SteamTokenClaims? claims, out string error)
    {
        claims = null;
        error = "invalid_token";
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048 || string.IsNullOrEmpty(secret)) return false;
        string[] parts = token.Split('.');
        if (parts.Length != 2) return false;

        byte[] provided;
        byte[] payload;
        try
        {
            provided = FromBase64Url(parts[1]);
            payload = FromBase64Url(parts[0]);
        }
        catch (FormatException) { return false; }

        byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(parts[0]));
        if (!CryptographicOperations.FixedTimeEquals(provided, expected)) return false;

        SteamTokenPayload? data;
        try { data = JsonSerializer.Deserialize<SteamTokenPayload>(payload); }
        catch (JsonException) { return false; }
        if (data == null || !IsOfficialAppId(data.AppId) || string.IsNullOrEmpty(data.SteamId) || data.ExpiresAt <= 0
            || data.Generation is <= 0 or > 9007199254740991L) return false;
        if (data.ExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            error = "token_expired";
            return false;
        }
        if (!string.IsNullOrEmpty(expectedWorld) && !string.Equals(data.World, expectedWorld, StringComparison.Ordinal))
        {
            error = "wrong_world";
            return false;
        }

        claims = new SteamTokenClaims(data.SteamId, data.AppId, data.World, data.ExpiresAt, data.Nonce, data.Generation);
        error = "";
        return true;
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        string padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}

public sealed record SteamLoginConfig(
    string WebApiKey, string ApiBase, HashSet<uint> AppIds, string TokenSecret,
    int TokenTtlSeconds, bool AllowFamilySharing, string RelayUrl = "", string RelayToken = "")
{
    public const int MinSecretLength = 32;

    /// <summary>Steam Web API 地址只允许 HTTPS；回环 HTTP 只给本机回归用。</summary>
    public bool ApiBaseAllowed => Uri.TryCreate(ApiBase, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>代验证模式：不持有 Web API Key，把票据转发给中心服务验证。</summary>
    public bool RelayConfigured => RelayUrl.Length > 0 && RelayToken.Length >= MinSecretLength;

    public bool RelayAllowed => Uri.TryCreate(RelayUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    public bool Enabled => TokenSecret.Length >= MinSecretLength
        && (RelayConfigured ? RelayAllowed : WebApiKey.Length > 0 && ApiBaseAllowed);

    public static SteamLoginConfig FromEnvironment()
    {
        static string Read(string name, string fallback) => Environment.GetEnvironmentVariable(name)?.Trim() ?? fallback;

        var appIds = new HashSet<uint>(SteamLogin.AllowedAppIds);

        int ttl = int.TryParse(Read("WHEATSTEAD_MULTIPLAYER_TOKEN_TTL_SECONDS", "120"), out int parsed)
            ? parsed : 120;
        return new(
            Read("WHEATSTEAD_STEAM_WEB_API_KEY", ""),
            Read("WHEATSTEAD_STEAM_API_URL", "https://partner.steam-api.com").TrimEnd('/'),
            appIds,
            Read("WHEATSTEAD_MULTIPLAYER_TOKEN_SECRET", ""),
            Math.Clamp(ttl, 30, 600),
            Read("WHEATSTEAD_STEAM_ALLOW_FAMILY_SHARING", "0") == "1",
            Read("WHEATSTEAD_LICENSE_RELAY_URL", "").TrimEnd('/'),
            Read("WHEATSTEAD_LICENSE_RELAY_TOKEN", ""));
    }
}

public sealed record SteamLoginRequest(uint? AppId, string Ticket, string World);

public sealed record RelayVerifyRequest(uint? AppId, string? Ticket);

public sealed record SteamLoginOutcome(
    int Status, string Error, string Token = "", long ExpiresAt = 0,
    bool FamilyShared = false, bool VacBanned = false, bool PublisherBanned = false);

public sealed record SteamTokenClaims(string SteamId, uint AppId, string World, long ExpiresAt, string Nonce, long Generation);

public sealed record SteamTokenPayload(
    [property: JsonPropertyName("sid")] string SteamId,
    [property: JsonPropertyName("app")] uint AppId,
    [property: JsonPropertyName("world")] string World,
    [property: JsonPropertyName("exp")] long ExpiresAt,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("generation")] long Generation);

internal sealed record SteamVerifyResult(
    int Status, string Error, string SteamId = "", bool FamilyShared = false,
    bool VacBanned = false, bool PublisherBanned = false);

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var cfg = LeaderboardConfig.FromEnvironment();
var feedbackCfg = FeedbackConfig.FromEnvironment(cfg.DatabasePath + ".feedback");
var builder = WebApplication.CreateBuilder(args);
string controlSocket = Environment.GetEnvironmentVariable("WHEATSTEAD_CONTROL_UNIX_SOCKET")?.Trim() ?? "";
uint oldUmask = 0;
if (controlSocket.Length > 0)
{
    if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(controlSocket)
        || Path.GetFullPath(controlSocket) != controlSocket || controlSocket.Length > 100
        || controlSocket.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('/' or '_' or '-' or '.'))
        || !Directory.Exists(Path.GetDirectoryName(controlSocket))
        || new DirectoryInfo(Path.GetDirectoryName(controlSocket)!).LinkTarget != null
        || File.Exists(controlSocket) || Directory.Exists(controlSocket))
        throw new InvalidOperationException("控制套接字路径无效或已被占用。");
    // Do not briefly expose the socket while Kestrel binds it. Restore the
    // previous process mask after its group-only permissions are installed.
    oldUmask = UnixPermissions.Umask(0x3f); // 0077
}
builder.WebHost.UseUrls(controlSocket.Length == 0 ? cfg.Urls : cfg.Urls + ";http://unix:" + controlSocket);
builder.Services.ConfigureHttpJsonOptions(o => {
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = 8192);
builder.Services.AddRateLimiter(o => {
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (context, _) => {
        if (context.HttpContext.Request.Path.Value is "/v1/feedback" or "/v1/multiplayer/steam-login")
            context.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
    o.AddPolicy("public", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Up to eight players behind one NAT may poll readiness while their
    // initial houses are created. Keep that traffic away from selection and
    // assignment requests while retaining a separate per-address ceiling.
    o.AddPolicy("world-map", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("feedback", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("license-relay", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("admin-login", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("node-heartbeat", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("node-admission", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("node-lease", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("node-readiness", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("steam", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
if (controlSocket.Length > 0)
    app.Lifetime.ApplicationStarted.Register(() => {
        try {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            File.SetUnixFileMode(controlSocket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        }
        catch (Exception ex) { Console.Error.WriteLine("[多人] 控制套接字权限设置失败：" + ex.GetType().Name); app.Lifetime.StopApplication(); }
        finally { UnixPermissions.Umask(oldUmask); }
    });
// 在代理头处理前记录连接来源，供内部入场接口检查。
app.Use(async (context, next) => {
    context.Items["node_admission_unix"] = controlSocket.Length > 0
        && context.Connection.RemoteIpAddress == null && context.Connection.LocalIpAddress == null
        && !context.Request.Headers.ContainsKey("X-Forwarded-For")
        && !context.Request.Headers.ContainsKey("X-Forwarded-Proto")
        && !context.Request.Headers.ContainsKey("Forwarded");
    context.Items["node_admission_local"] = (context.Connection.RemoteIpAddress != null
        && System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress)
        || controlSocket.Length > 0 && context.Connection.RemoteIpAddress == null && context.Connection.LocalIpAddress == null)
        && !context.Request.Headers.ContainsKey("X-Forwarded-For")
        && !context.Request.Headers.ContainsKey("X-Forwarded-Proto")
        && !context.Request.Headers.ContainsKey("Forwarded");
    await next();
});
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
app.UseRateLimiter();
app.Use(async (context, next) => {
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; form-action 'self'";
    try { await next(); }
    catch (Microsoft.Data.Sqlite.SqliteException) {
        Console.Error.WriteLine("[排行榜] 数据库操作失败，请检查权限、空间与服务日志。");
        if (!context.Response.HasStarted) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { code = "storage_unavailable" }); }
    }
});
using var store = new LeaderboardStore(cfg.DatabasePath);
using var feedback = new FeedbackDelivery(feedbackCfg);
var steamConfig = SteamLoginConfig.FromEnvironment();
var multiplayerDb = Environment.GetEnvironmentVariable("WHEATSTEAD_MULTIPLAYER_DB")?.Trim();
using var multiplayer = new MultiplayerStore(string.IsNullOrEmpty(multiplayerDb)
    ? cfg.DatabasePath + ".multiplayer" : multiplayerDb);
var steamLogin = new SteamLogin(steamConfig, multiplayer.OpenGeneration);
static bool SameSecret(string a, string b)
    => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(a)),
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(b)));

var relayToken = Environment.GetEnvironmentVariable("WHEATSTEAD_LICENSE_RELAY_TOKEN")?.Trim() ?? "";
bool relayEnabled = relayToken.Length >= SteamLoginConfig.MinSecretLength;

var monitor = new ServerMonitor(string.IsNullOrEmpty(multiplayerDb) ? cfg.DatabasePath + ".multiplayer" : multiplayerDb);
var nodeHeartbeatKey = Environment.GetEnvironmentVariable("WHEATSTEAD_NODE_HEARTBEAT_KEY")?.Trim() ?? "";
var admins = new ConcurrentDictionary<string, AdminSession>();
app.MapGet("/v1/health", () => Results.Ok(new { ok = true, api = "leaderboard-v1", speedruns_version = 2,
    feedback_enabled = feedbackCfg.Enabled, steam_login_enabled = steamLogin.Enabled,
    license_relay_enabled = relayEnabled }));
app.MapPost("/v1/feedback", feedback.ReceiveAsync).RequireRateLimiting("feedback");
app.MapPost("/v1/multiplayer/steam-login", async (HttpRequest request, CancellationToken ct) => {
    SteamLoginRequest? data;
    try { data = await request.ReadFromJsonAsync<SteamLoginRequest>(ct); }
    catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
    if (data == null) return Results.BadRequest(new { code = "invalid_login_request" });
    var outcome = await steamLogin.LoginAsync(data, ct);
    IResult result = outcome.Status == 200
        ? Results.Ok(new { ok = true, token = outcome.Token, expires_at = outcome.ExpiresAt })
        : Results.Json(new { code = outcome.Error }, statusCode: outcome.Status);
    return result;
}).RequireRateLimiting("steam");

if (relayEnabled)
{
    // 代验证：自建服把票据转发到这里，Web API Key 只留在本机。
    app.MapPost("/v1/license/verify", async (HttpRequest request, CancellationToken ct) => {
        string presented = request.Headers.Authorization.ToString() is var header && header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? header["Bearer ".Length..].Trim() : "";
        if (!SameSecret(presented, relayToken))
            return Results.Json(new { code = "relay_unauthorized" }, statusCode: 401);
        RelayVerifyRequest? data;
        try { data = await request.ReadFromJsonAsync<RelayVerifyRequest>(ct); }
        catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
        if (data?.AppId is not uint appId || !SteamLogin.IsOfficialAppId(appId))
            return Results.Json(new { code = "unsupported_appid" }, statusCode: 400);
        var verified = await steamLogin.VerifyOnlyAsync(appId, data.Ticket ?? "", ct);
        return verified.Status == 200
            ? Results.Ok(new { ok = true, steamid = verified.SteamId, family_shared = verified.FamilyShared,
                vac_banned = verified.VacBanned, publisher_banned = verified.PublisherBanned })
            : Results.Json(new { code = verified.Error }, statusCode: verified.Status);
    }).RequireRateLimiting("license-relay");
}

app.MapGet("/v1/multiplayer/worlds", () => Results.Ok(new { worlds = multiplayer.ListWorlds() }))
    .RequireRateLimiting("public");
app.MapGet("/v1/multiplayer/worlds/{id}/map", (string id) => {
    var result = multiplayer.PublicMap(id);
    return result.Status == 200
        ? Results.Ok(new { world = result.World, map = result.Map, occupied = result.Occupied,
            generation = result.Generation, map_source = result.Source,
            entry_ready = result.Source == "official" && multiplayer.WorldEntryReady(id, result.Generation), entry_generation = result.Generation })
        : Results.Json(new { code = result.Error }, statusCode: result.Status);
}).RequireRateLimiting("world-map");
app.MapGet("/v1/multiplayer/worlds/{id}/assignment", (HttpContext c, string id) => {
    if (!steamConfig.Enabled) return Results.Json(new { code = "steam_login_disabled" }, statusCode: 503);
    string auth = c.Request.Headers.Authorization.ToString();
    string token = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : "";
    if (!SteamLogin.TryValidateToken(token, steamConfig.TokenSecret, id, out var claims, out var tokenError) || claims == null)
        return Results.Json(new { code = tokenError }, statusCode: 401);
    long? requestedGeneration = null;
    if (c.Request.Query.TryGetValue("generation", out var value)) {
        if (value.Count != 1 || !long.TryParse(value[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long parsed) || parsed <= 0)
            return Results.BadRequest(new { code = "invalid_generation" });
        requestedGeneration = parsed;
    }
    var admission = multiplayer.CheckAdmission(id, claims.SteamId, claims.Generation, requestedGeneration);
    return admission.Status == 200
        ? Results.Ok(new { ok = true, assignment = admission.Assignment, generation = claims.Generation,
            map_source = admission.Assignment!.MapGeneration == 0 ? "legacy" : "official" })
        : Results.Json(new { code = admission.Error }, statusCode: admission.Status);
}).RequireRateLimiting("public");
app.MapPost("/v1/multiplayer/worlds/{id}/select", async (HttpContext c, string id) => {
    if (!steamConfig.Enabled) return Results.Json(new { code = "steam_login_disabled" }, statusCode: 503);
    string auth = c.Request.Headers.Authorization.ToString();
    string token = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : "";
    if (!SteamLogin.TryValidateToken(token, steamConfig.TokenSecret, id, out var claims, out var tokenError) || claims == null)
        return Results.Json(new { code = tokenError }, statusCode: 401);
    MultiplayerSelectRequest? data;
    try { data = await c.Request.ReadFromJsonAsync<MultiplayerSelectRequest>(); }
    catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
    if (data == null || string.IsNullOrWhiteSpace(data.PlotId) || string.IsNullOrWhiteSpace(data.SpawnId))
        return Results.BadRequest(new { code = "invalid_selection" });
    var assigned = multiplayer.Assign(id, claims.SteamId, data.PlotId, data.SpawnId, claims.Generation, data.Generation, requireReady: true);
    IResult result = assigned.Status == 200
        ? Results.Ok(new { ok = true, assignment = assigned.Assignment, generation = claims.Generation,
            map_source = assigned.Assignment!.MapGeneration == 0 ? "legacy" : "official" })
        : Results.Json(new { code = assigned.Error }, statusCode: assigned.Status);
    return result;
}).RequireRateLimiting("public");
app.MapPost("/v1/multiplayer/nodes/heartbeat", async (HttpContext c) => {
    if (nodeHeartbeatKey.Length == 0) return Results.Json(new { code = "heartbeat_disabled" }, statusCode: 503);
    if (!FixedEquals(c.Request.Headers["X-Node-Key"].ToString(), nodeHeartbeatKey))
        return Results.Json(new { code = "invalid_node_key" }, statusCode: 401);
    NodeHeartbeatRequest? data;
    try { data = await c.Request.ReadFromJsonAsync<NodeHeartbeatRequest>(); }
    catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
    if (data == null || string.IsNullOrWhiteSpace(data.Id) || data.Id.Length > 64
        || data.Name is not { Length: <= 64 } || data.Region is not { Length: <= 32 }
        || data.Url is not { Length: <= 256 } || data.Version is not { Length: <= 32 })
        return Results.BadRequest(new { code = "invalid_heartbeat" });
    bool hasMonitor = data.WorldId != null || data.SteamIds != null || data.Events != null
        || data.Generation != null || data.BootNonce != null;
    if (!hasMonitor) {
        return multiplayer.Heartbeat(data.Id, data.Name, data.Region, data.Url, data.Version, data.Players)
            ? Results.Ok(new { ok = true })
            : Results.Conflict(new { code = "legacy_heartbeat_disabled" });
    }
    if (Encoding.UTF8.GetByteCount(nodeHeartbeatKey) < 32)
        return Results.Json(new { code = "monitor_disabled" }, statusCode: 503);
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (string.IsNullOrWhiteSpace(data.WorldId) || data.WorldId.Length > 64 || data.SteamIds == null || data.Events == null
        || (data.Generation == null) != (data.BootNonce == null)
        || (data.Generation != null && (data.Generation is <= 0 or > 9007199254740991L
            || data.BootNonce is not { Length: 32 }
            || !data.BootNonce.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
        || data.SteamIds.Length > 32 || data.Events.Length > 32 || data.SteamIds.Any(s => !MultiplayerStore.ValidSteamId(s))
        || data.SteamIds.Distinct(StringComparer.Ordinal).Count() != data.SteamIds.Length
        || data.Events.Any(e => e == null || e.Id is not { Length: 32 } || !e.Id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            || e.Type is not ("connect" or "disconnect") || !MultiplayerStore.ValidSteamId(e.SteamId) || e.At <= 0 || e.At > now + 300)
        || data.Events.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != data.Events.Length)
        return Results.BadRequest(new { code = "invalid_monitor" });
    if (!multiplayer.MonitorHeartbeat(data.Id, data.Name, data.Region, data.Url, data.Version, data.WorldId, data.SteamIds, data.Events,
        data.Generation ?? 0, data.BootNonce ?? ""))
        return Results.Conflict(new { code = "invalid_monitor_world_or_event" });
    return Results.Ok(new { ok = true, monitor_version = 1 });
}).RequireRateLimiting("node-heartbeat");
app.MapPost("/v1/multiplayer/nodes/admission", async (HttpContext c) => {
    if (c.Items["node_admission_local"] is not true) return Results.NotFound();
    if (Encoding.UTF8.GetByteCount(nodeHeartbeatKey) < 32)
        return Results.Json(new { code = "admission_disabled" }, statusCode: 503);
    if (!FixedEquals(c.Request.Headers["X-Node-Key"].ToString(), nodeHeartbeatKey))
        return Results.Json(new { code = "invalid_node_key" }, statusCode: 401);
    NodeAdmissionRequest? data;
    try { data = await c.Request.ReadFromJsonAsync<NodeAdmissionRequest>(); }
    catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
    if (data == null || string.IsNullOrWhiteSpace(data.World) || data.World.Length > 64
        || !MultiplayerStore.ValidSteamId(data.SteamId) || data.Generation <= 0)
        return Results.BadRequest(new { code = "invalid_admission" });
    var admission = multiplayer.CheckAdmission(data.World, data.SteamId, data.Generation,
        requireNode: true, nodeId: data.NodeId, bootNonce: data.BootNonce);
    return admission.Status == 200
        ? Results.Ok(new { ok = true, assignment = admission.Assignment, spawn = admission.Spawn,
            generation = data.Generation, map_source = admission.Assignment!.MapGeneration == 0 ? "legacy" : "official" })
        : Results.Json(new { code = admission.Error }, statusCode: admission.Status);
}).RequireRateLimiting("node-admission");
app.MapPost("/v1/multiplayer/nodes/start", async (HttpContext c) => {
    if (c.Items["node_admission_local"] is not true) return Results.NotFound();
    if (Encoding.UTF8.GetByteCount(nodeHeartbeatKey) < 32)
        return Results.Json(new { code = "start_validation_disabled" }, statusCode: 503);
    if (!FixedEquals(c.Request.Headers["X-Node-Key"].ToString(), nodeHeartbeatKey))
        return Results.Json(new { code = "invalid_node_key" }, statusCode: 401);
    NodeStartRequest? data;
    try { data = await c.Request.ReadFromJsonAsync<NodeStartRequest>(); }
    catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
    if (data == null || data.WorldId == null || data.NodeId == null || data.BootNonce == null || data.JobId == null)
        return Results.BadRequest(new { code = "invalid_start" });
    if (multiplayer.CheckFreshStart(data.WorldId, data.Generation, data.NodeId, data.BootNonce, data.JobId))
        return Results.Ok(new { ok = true, mode = "fresh" });
    if (multiplayer.CheckRestoreStart(data.WorldId, data.Generation, data.NodeId, data.BootNonce, data.JobId))
        return Results.Ok(new { ok = true, mode = "restore" });
    if (multiplayer.CheckResumeStart(data.WorldId, data.Generation, data.NodeId, data.BootNonce, data.JobId))
        return Results.Ok(new { ok = true, mode = "resume" });
    return Results.Json(new { code = "start_identity_changed" }, statusCode: 409);
}).RequireRateLimiting("node-admission");
app.MapPost("/v1/multiplayer/nodes/map", async (HttpContext c) => {
    if (c.Items["node_admission_local"] is not true) return Results.NotFound();
    if (Encoding.UTF8.GetByteCount(nodeHeartbeatKey) < 32)
        return Results.Json(new { code = "map_registration_disabled" }, statusCode: 503);
    if (!FixedEquals(c.Request.Headers["X-Node-Key"].ToString(), nodeHeartbeatKey))
        return Results.Json(new { code = "invalid_node_key" }, statusCode: 401);
    NodeMapRegistrationRequest? data;
    try { data = await c.Request.ReadFromJsonAsync<NodeMapRegistrationRequest>(); }
    catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
    if (data == null || data.NodeId == null || data.BootNonce == null)
        return Results.BadRequest(new { code = "invalid_official_map" });
    var result = multiplayer.RegisterOfficialWorldMap(data.NodeId, data.BootNonce, data.JobId ?? "", data.Map);
    return result.Status == 200 ? Results.Ok(new { ok = true })
        : Results.Json(new { code = result.Error }, statusCode: result.Status);
}).RequireRateLimiting("node-admission");
app.MapPost("/v1/multiplayer/nodes/map/read", async (HttpContext c) => {
    if (c.Items["node_admission_local"] is not true) return Results.NotFound();
    if (Encoding.UTF8.GetByteCount(nodeHeartbeatKey) < 32)
        return Results.Json(new { code = "map_registration_disabled" }, statusCode: 503);
    if (!FixedEquals(c.Request.Headers["X-Node-Key"].ToString(), nodeHeartbeatKey))
        return Results.Json(new { code = "invalid_node_key" }, statusCode: 401);
    NodeMapReadRequest? data;
    try { data = await c.Request.ReadFromJsonAsync<NodeMapReadRequest>(); }
    catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
    if (data == null || string.IsNullOrWhiteSpace(data.WorldId) || data.NodeId == null || data.BootNonce == null)
        return Results.BadRequest(new { code = "invalid_official_map" });
    var result = multiplayer.ReadOfficialWorldMap(data.WorldId, data.Generation, data.NodeId, data.BootNonce);
    return result.Status == 200 ? Results.Ok(new { ok = true, map = result.Map })
        : Results.Json(new { code = result.Error }, statusCode: result.Status);
}).RequireRateLimiting("node-admission");

app.MapPost("/v1/multiplayer/nodes/lease", async (HttpContext c) => {
    if (c.Items["node_admission_local"] is not true) return Results.NotFound();
    if (Encoding.UTF8.GetByteCount(nodeHeartbeatKey) < 32)
        return Results.Json(new { code = "lease_disabled" }, statusCode: 503);
    if (!FixedEquals(c.Request.Headers["X-Node-Key"].ToString(), nodeHeartbeatKey))
        return Results.Json(new { code = "invalid_node_key" }, statusCode: 401);
    NodeMapReadRequest? data;
    try { data = await c.Request.ReadFromJsonAsync<NodeMapReadRequest>(); }
    catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
    if (data == null || string.IsNullOrWhiteSpace(data.WorldId) || data.WorldId.Length > 64
        || data.NodeId == null || data.BootNonce == null || data.Generation is <= 0 or > 9007199254740991L)
        return Results.BadRequest(new { code = "invalid_lease" });
    string state = multiplayer.CheckWorldLease(data.WorldId, data.Generation, data.NodeId, data.BootNonce);
    return state.Length > 0
        ? Results.Ok(new { ok = true, world_id = data.WorldId, generation = data.Generation,
            node_id = data.NodeId, boot_nonce = data.BootNonce, state })
        : Results.Json(new { code = "lease_invalid" }, statusCode: 409);
}).RequireRateLimiting("node-lease");

app.MapPost("/v1/multiplayer/nodes/ready", async (HttpContext c) => {
    if (c.Items["node_admission_unix"] is not true) return Results.NotFound();
    if (Encoding.UTF8.GetByteCount(nodeHeartbeatKey) < 32)
        return Results.Json(new { code = "ready_disabled" }, statusCode: 503);
    if (!FixedEquals(c.Request.Headers["X-Node-Key"].ToString(), nodeHeartbeatKey))
        return Results.Json(new { code = "invalid_node_key" }, statusCode: 401);
    NodeReadyRequest? data;
    try { data = await c.Request.ReadFromJsonAsync<NodeReadyRequest>(); }
    catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
    if (data == null || string.IsNullOrWhiteSpace(data.WorldId) || data.WorldId.Length > 64
        || data.NodeId == null || data.BootNonce == null || data.Generation is <= 0 or > 9007199254740991L
        || data.Revision is <= 0 or > 9007199254740991L)
        return Results.BadRequest(new { code = "invalid_ready" });
    return multiplayer.RenewWorldEntryReady(data.WorldId, data.Generation, data.NodeId, data.BootNonce, data.Revision)
        ? Results.Ok(new { ok = true }) : Results.Json(new { code = "ready_invalid" }, statusCode: 409);
}).RequireRateLimiting("node-readiness");

app.MapPost("/v1/multiplayer/nodes/not-ready", async (HttpContext c) => {
    if (c.Items["node_admission_unix"] is not true) return Results.NotFound();
    if (Encoding.UTF8.GetByteCount(nodeHeartbeatKey) < 32)
        return Results.Json(new { code = "ready_disabled" }, statusCode: 503);
    if (!FixedEquals(c.Request.Headers["X-Node-Key"].ToString(), nodeHeartbeatKey))
        return Results.Json(new { code = "invalid_node_key" }, statusCode: 401);
    NodeMapReadRequest? data;
    try { data = await c.Request.ReadFromJsonAsync<NodeMapReadRequest>(); }
    catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
    if (data == null || string.IsNullOrWhiteSpace(data.WorldId) || data.WorldId.Length > 64
        || data.NodeId == null || data.BootNonce == null || data.Generation is <= 0 or > 9007199254740991L)
        return Results.BadRequest(new { code = "invalid_ready" });
    return multiplayer.RevokeWorldEntryReady(data.WorldId, data.Generation, data.NodeId, data.BootNonce)
        ? Results.Ok(new { ok = true }) : Results.Json(new { code = "ready_invalid" }, statusCode: 409);
}).RequireRateLimiting("node-readiness");

app.MapPost("/v1/multiplayer/nodes/houses/read", async (HttpContext c) => {
    if (c.Items["node_admission_local"] is not true) return Results.NotFound();
    if (Encoding.UTF8.GetByteCount(nodeHeartbeatKey) < 32)
        return Results.Json(new { code = "house_snapshot_disabled" }, statusCode: 503);
    if (!FixedEquals(c.Request.Headers["X-Node-Key"].ToString(), nodeHeartbeatKey))
        return Results.Json(new { code = "invalid_node_key" }, statusCode: 401);
    NodeMapReadRequest? data;
    try { data = await c.Request.ReadFromJsonAsync<NodeMapReadRequest>(); }
    catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
    if (data == null || string.IsNullOrWhiteSpace(data.WorldId) || data.WorldId.Length > 64
        || data.NodeId == null || data.BootNonce == null || data.Generation is <= 0 or > 9007199254740991L)
        return Results.BadRequest(new { code = "invalid_house_snapshot_request" });
    var result = multiplayer.ReadPlotHouseSnapshot(data.WorldId, data.Generation, data.NodeId, data.BootNonce);
    return result.Status == 200
        ? Results.Ok(new { ok = true, world_id = data.WorldId, generation = data.Generation,
            revision = result.Revision, blueprints_available = true, plots = result.Plots })
        : Results.Json(new { code = result.Error }, statusCode: result.Status);
}).RequireRateLimiting("node-admission");

foreach (string houseAction in new[] { "register", "destroy", "capture" })
{
    app.MapPost("/v1/multiplayer/nodes/houses/" + houseAction, async (HttpContext c) => {
        if (c.Items["node_admission_local"] is not true) return Results.NotFound();
        if (Encoding.UTF8.GetByteCount(nodeHeartbeatKey) < 32)
            return Results.Json(new { code = "house_action_disabled" }, statusCode: 503);
        if (!FixedEquals(c.Request.Headers["X-Node-Key"].ToString(), nodeHeartbeatKey))
            return Results.Json(new { code = "invalid_node_key" }, statusCode: 401);
        var limit = c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 4096;
        if (c.Request.ContentLength > 4096) return Results.StatusCode(413);
        NodeHouseActionRequest? data;
        try { data = await c.Request.ReadFromJsonAsync<NodeHouseActionRequest>(); }
        catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
        catch (BadHttpRequestException) { return Results.StatusCode(413); }
        if (data == null || string.IsNullOrWhiteSpace(data.WorldId) || data.WorldId.Length > 64
            || data.NodeId == null || data.BootNonce == null || data.Generation is <= 0 or > 9007199254740991L
            || string.IsNullOrWhiteSpace(data.PlotId) || data.PlotId.Length > 64 || data.OwnerSteamId == null)
            return Results.BadRequest(new { code = "invalid_house_action" });
        var result = houseAction switch {
            "register" => multiplayer.RegisterPlotHouse(data.WorldId, data.PlotId, data.OwnerSteamId,
                data.Generation, data.HouseId ?? "", data.PreviousHouseId ?? "", data.PreviousVersion, data.NodeId, data.BootNonce,
                data.CellX, data.CellY),
            "destroy" => multiplayer.MarkPlotHouseDestroyed(data.WorldId, data.PlotId, data.OwnerSteamId,
                data.Generation, data.HouseId ?? "", data.HouseVersion, data.BlueprintId ?? "", data.NodeId, data.BootNonce),
            _ => multiplayer.CaptureDestroyedPlot(data.WorldId, data.PlotId, data.OwnerSteamId, data.NewOwnerSteamId ?? "",
                data.Generation, data.HouseId ?? "", data.HouseVersion, data.BlueprintId ?? "", data.EventId ?? "", data.NodeId, data.BootNonce)
        };
        return result.Ok ? Results.Ok(new { ok = true }) : Results.Json(new { code = result.Error }, statusCode: 409);
    }).RequireRateLimiting("node-admission");
}

foreach (string action in new[] { "register", "login", "rebind" })
{
    app.MapPost("/v1/accounts/" + action, async (HttpRequest request) => {
        AccountRequest? data;
        try { data = await request.ReadFromJsonAsync<AccountRequest>(); }
        catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
        if (data == null || !LeaderboardStore.ValidName(data.Name) || !LeaderboardStore.ValidToken(data.DeviceToken))
            return Results.BadRequest(new { code = "invalid_account" });
        Account? account;
        if (action == "register") {
            account = store.Register(data.Name, data.DeviceToken);
            // A lost registration response can be retried safely with the same local credential.
            account ??= store.Authenticate(data.DeviceToken, data.Name);
            if (account == null) return Results.Conflict(new { code = "name_taken" });
        }
        else if (action == "rebind") {
            if (data.Code is not { Length: 32 } || !data.Code.All(char.IsAsciiHexDigit)) return Results.BadRequest(new { code = "invalid_rebind_code" });
            account = store.Rebind(data.Name, data.Code, data.DeviceToken);
            account ??= store.Authenticate(data.DeviceToken, data.Name);
            if (account == null) return Results.Unauthorized();
        }
        else {
            account = store.Authenticate(data.DeviceToken, data.Name);
            if (account == null) return Results.Unauthorized();
        }
        return Results.Ok(new { ok = true, account });
    }).RequireRateLimiting("public");
}
app.MapGet("/v1/speedruns", (bool? hard, bool? multiplayer) => Results.Ok(new { entries = store.Top(hard ?? false, multiplayer ?? false) }))
    .RequireRateLimiting("public");
app.MapPost("/v1/speedruns", async (HttpRequest request) => {
    string auth = request.Headers.Authorization.ToString();
    var account = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? store.Authenticate(auth[7..]) : null;
    if (account == null) return Results.Unauthorized();
    SpeedrunSubmission? score;
    try { score = await request.ReadFromJsonAsync<SpeedrunSubmission>(); }
    catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
    if (!SpeedrunSubmission.IsValid(score)) return Results.BadRequest(new { code = "invalid_score" });
    return store.Record(auth[7..], score!) switch {
        ScoreRecordResult.Recorded => Results.Ok(new { ok = true }),
        ScoreRecordResult.Unauthorized => Results.Unauthorized(),
        _ => Results.Conflict(new { code = "score_not_faster_or_owned_by_another_account" }),
    };
}).RequireRateLimiting("public");

app.MapGet("/admin", (HttpContext c) => {
    if (!cfg.AdminEnabled) return Results.NotFound();
    return Admin(c) != null ? Results.File(WebFile("dashboard.html"), "text/html; charset=utf-8")
        : Results.Content(LoginPage(""), "text/html; charset=utf-8", statusCode: 401);
});
app.MapPost("/admin/login", async (HttpContext c) => {
    if (!cfg.AdminEnabled) return Results.NotFound();
    if (!c.Request.HasFormContentType) return Results.BadRequest();
    var form = await c.Request.ReadFormAsync();
    if (!FixedEquals(form["password"].ToString(), cfg.AdminPassword))
        return Results.Content(LoginPage("管理口令不正确。"), "text/html; charset=utf-8", statusCode: 401);
    foreach (var pair in admins) if (pair.Value.Expires <= DateTimeOffset.UtcNow) admins.TryRemove(pair.Key, out _);
    string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    admins[token] = new AdminSession(DateTimeOffset.UtcNow.AddHours(8), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    c.Response.Cookies.Append("wheatstead_admin", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict,
        Secure = c.Request.IsHttps, Path = "/admin", MaxAge = TimeSpan.FromHours(8) });
    return Results.Redirect("/admin");
}).RequireRateLimiting("admin-login");
app.MapGet("/admin/data", (HttpContext c) => {
    var session = Admin(c); if (session == null) return Results.Unauthorized();
    return Results.Ok(new { overview = store.Overview(), accounts = store.Accounts(), scores = store.Scores(), audit = store.Audit(), csrf = session.Csrf });
});
app.MapPost("/admin/manage", async (HttpContext c) => {
    var session = Admin(c); if (session == null) return Results.Unauthorized();
    if (!FixedEquals(c.Request.Headers["X-CSRF-Token"].ToString(), session.Csrf)) return Results.StatusCode(403);
    ManageRequest? data;
    try { data = await c.Request.ReadFromJsonAsync<ManageRequest>(); }
    catch (JsonException) { return Results.BadRequest(); }
    if (data == null || data.Action is not ("disable" or "enable" or "delete_account" or "delete_score" or "rebind")
        || data.Id is not { Length: 32 or 64 } || !data.Id.All(char.IsAsciiHexDigit)) return Results.BadRequest();
    string? code = store.Manage(data.Action, data.Id);
    return code == null ? Results.NotFound() : Results.Ok(new { ok = true, rebind_code = code, expires_minutes = data.Action == "rebind" ? 15 : 0 });
});
app.MapGet("/admin/assets/{asset}", (string asset) => {
    if (asset is not ("dashboard.css" or "dashboard.js" or "multiplayer.js")) return Results.NotFound();
    return Results.File(WebFile(asset), asset.EndsWith(".css") ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8");
});
app.MapGet("/admin/multiplayer", (HttpContext c) => {
    if (!cfg.AdminEnabled) return Results.NotFound();
    IResult page = Admin(c) != null
        ? Results.File(WebFile("multiplayer.html"), "text/html; charset=utf-8")
        : Results.Redirect("/admin");
    return page;
});
app.MapGet("/admin/multiplayer/data", (HttpContext c) => {
    var session = Admin(c); if (session == null) return Results.Unauthorized();
    bool resetEnabled = Environment.GetEnvironmentVariable("WHEATSTEAD_RESET_WORKER_ENABLED") == "1";
    var worlds = multiplayer.ListWorlds().Select(world => {
        var status = multiplayer.WorldResetStatus(world.Id);
        bool available = multiplayer.CanQueueWorldReset(world.Id);
        bool registered = multiplayer.HasOfficialWorldMap(world.Id);
        long? generation = multiplayer.OpenGeneration(world.Id);
        return new {
            world, assignments = multiplayer.Assignments(world.Id),
            map_registered = registered, plot_owners = multiplayer.PlotOwners(world.Id),
            entry_ready = generation is > 0 && multiplayer.WorldEntryReady(world.Id, generation.Value),
            reset = new { available, status.JobId, status.Phase, status.Error, status.Created, status.Updated,
                status.WorkerLastSeen, enabled = resetEnabled && world.Id == "official-1" }
        };
    }).ToArray();
    return Results.Ok(new { worlds, nodes = multiplayer.Nodes(), audit = multiplayer.Audit(), metrics = monitor.Snapshot(),
        player_monitor = multiplayer.MonitorData(), csrf = session.Csrf });
});
app.MapPost("/admin/multiplayer/manage", async (HttpContext c) => {
    var session = Admin(c); if (session == null) return Results.Unauthorized();
    if (!FixedEquals(c.Request.Headers["X-CSRF-Token"].ToString(), session.Csrf)) return Results.StatusCode(403);
    ManageMultiplayerRequest? data;
    try { data = await c.Request.ReadFromJsonAsync<ManageMultiplayerRequest>(); }
    catch (JsonException) { return Results.BadRequest(); }
    if (data == null || string.IsNullOrWhiteSpace(data.World) || data.World.Length > 64) return Results.BadRequest();
    bool changed = data.Action switch {
        "open" => multiplayer.SetStatus(data.World, "open"),
        "close" => multiplayer.SetStatus(data.World, "closed"),
        "release" => MultiplayerStore.ValidSteamId(data.Steam) && multiplayer.Release(data.World, data.Steam),
        _ => false,
    };
    return changed ? Results.Ok(new { ok = true }) : Results.NotFound();
});
app.MapPost("/admin/multiplayer/reset", async (HttpContext c) => {
    var session = Admin(c); if (session == null) return Results.Unauthorized();
    if (!FixedEquals(c.Request.Headers["X-CSRF-Token"].ToString(), session.Csrf)) return Results.StatusCode(403);
    ResetWorldRequest? data;
    try { data = await c.Request.ReadFromJsonAsync<ResetWorldRequest>(); }
    catch (JsonException) { return Results.BadRequest(new { code = "invalid_json" }); }
    if (data == null || data.World != "official-1" || data.Confirmation != data.World)
        return Results.BadRequest(new { code = "invalid_confirmation" });
    var result = multiplayer.QueueWorldReset(data.World);
    return result.Ok
        ? Results.Ok(new { ok = true, job_id = result.JobId })
        : Results.Json(new { code = result.Error }, statusCode: result.Error == "reset_in_progress" ? 409 : 503);
});
app.MapPost("/admin/logout", (HttpContext c) => {
    var session = Admin(c); if (session == null) return Results.Unauthorized();
    if (!FixedEquals(c.Request.Headers["X-CSRF-Token"].ToString(), session.Csrf)) return Results.StatusCode(403);
    if (c.Request.Cookies.TryGetValue("wheatstead_admin", out var token)) admins.TryRemove(token, out _);
    c.Response.Cookies.Delete("wheatstead_admin", new CookieOptions { Path = "/admin" });
    return Results.Ok(new { ok = true });
});
Console.WriteLine($"[排行榜] 监听 {cfg.Urls}；旧监控与心跳接口已移除。");
Console.WriteLine(cfg.AdminEnabled ? "[排行榜] 管理后台 /admin 已启用。" : "[排行榜] 未设置管理口令，后台关闭。");
Console.WriteLine(feedbackCfg.Enabled ? "[反馈] 服务器邮件投递已启用。" : "[反馈] SMTP 未配置或配置无效，反馈提交暂不可用。");
Console.WriteLine(steamLogin.Enabled
    ? $"[多人] Steam 登录已启用，接受 AppID：{string.Join(", ", steamConfig.AppIds)}。"
    : "[多人] Steam 登录未配置，/v1/multiplayer/steam-login 返回 503。");
Console.WriteLine($"[多人] 官方世界管理已启用：{multiplayer.ListWorlds().Count} 个世界，"
    + (nodeHeartbeatKey.Length > 0 ? "节点心跳已开启。" : "节点心跳未配置。"));
await app.RunAsync();

AdminSession? Admin(HttpContext c) => cfg.AdminEnabled && c.Request.Cookies.TryGetValue("wheatstead_admin", out var token)
    && admins.TryGetValue(token, out var session) && session.Expires > DateTimeOffset.UtcNow ? session : null;
static bool FixedEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
static string WebFile(string name) => Path.Combine(AppContext.BaseDirectory, "web", name);
static string LoginPage(string message) => """
    <!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>Wheatstead 排行榜管理</title><link rel="stylesheet" href="/admin/assets/dashboard.css"><main class="login">
    <p class="eyebrow">WHEATSTEAD / 管理后台</p><h1>排行榜管理</h1><p>使用服务器的独立管理员口令登录。</p>
    <form method="post" action="/admin/login"><label>管理口令<input autofocus required name="password" type="password" autocomplete="current-password"></label><button>登录后台</button></form>
    <p role="alert">
    """ + HtmlEncoder.Default.Encode(message) + "</p></main></html>";
sealed record AdminSession(DateTimeOffset Expires, string Csrf);
static class UnixPermissions
{
    [DllImport("libc", EntryPoint = "umask")]
    internal static extern uint Umask(uint mask);
}
sealed record ManageRequest(string Action, string Id);
sealed record MultiplayerSelectRequest(string PlotId, string SpawnId, long? Generation);
sealed record NodeHeartbeatRequest(string Id, string Name, string Region, string Url, string Version, int Players,
    string? WorldId, string[]? SteamIds, NodeConnectionEvent[]? Events, long? Generation, string? BootNonce);
sealed record NodeAdmissionRequest(string World, string SteamId, long Generation, string? NodeId, string? BootNonce);
sealed record NodeStartRequest(string? WorldId, string? NodeId, long Generation, string? BootNonce, string? JobId);
sealed record NodeMapRegistrationRequest(string? NodeId, string? BootNonce, OfficialWorldMap? Map, string? JobId);
sealed record NodeMapReadRequest(string? WorldId, long Generation, string? NodeId, string? BootNonce);
sealed record NodeReadyRequest(string? WorldId, long Generation, string? NodeId, string? BootNonce, long Revision);
sealed record NodeHouseActionRequest(string? WorldId, long Generation, string? NodeId, string? BootNonce,
    string? PlotId, string? OwnerSteamId, string? NewOwnerSteamId, string? HouseId, long HouseVersion,
    string? BlueprintId, string? EventId, string? PreviousHouseId, long PreviousVersion, int? CellX, int? CellY);
sealed record ManageMultiplayerRequest(string Action, string World, string Steam);
sealed record ResetWorldRequest(string? World, string? Confirmation);
sealed record LeaderboardConfig(string Urls, string DatabasePath, string AdminPassword)
{
    public bool AdminEnabled => AdminPassword.Length > 0;
    public static LeaderboardConfig FromEnvironment()
    {
        static string Read(string name, string fallback) => Environment.GetEnvironmentVariable(name)?.Trim() ?? fallback;
        return new(Read("WHEATSTEAD_LEADERBOARD_URLS", Read("WHEATSTEAD_PLAYTEST_URLS", "http://127.0.0.1:8081")),
            Read("WHEATSTEAD_LEADERBOARD_DB", Read("WHEATSTEAD_PLAYTEST_DB", "data/playtest-monitor.db") + ".leaderboard"),
            Read("WHEATSTEAD_LEADERBOARD_ADMIN_PASSWORD", Read("WHEATSTEAD_PLAYTEST_ADMIN_PASSWORD", "")));
    }
}
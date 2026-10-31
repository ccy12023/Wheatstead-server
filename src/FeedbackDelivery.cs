using System.IO.Compression;
using System.Net;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;

/// <summary>Explicit player reports, delivered to the fixed support mailbox before acknowledging.</summary>
sealed class FeedbackDelivery : IDisposable
{
    public const int MaxArchiveBytes = 1024 * 1024;
    /// <summary>反馈收件人；不配就不收反馈，代码里不写死邮箱。</summary>
    private static readonly string Recipient =
        Environment.GetEnvironmentVariable("WHEATSTEAD_FEEDBACK_RECIPIENT")?.Trim() ?? "";
    private readonly FeedbackConfig _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SqliteConnection _db;

    public FeedbackDelivery(FeedbackConfig config)
    {
        _config = config;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(config.DatabasePath))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = config.DatabasePath, DefaultTimeout = 5 }.ToString());
        _db.Open();
        using var c = _db.CreateCommand();
        c.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS feedback_receipts(id TEXT PRIMARY KEY,archive_hash TEXT NOT NULL,accepted INTEGER NOT NULL);
            """;
        c.ExecuteNonQuery();
    }

    public async Task<IResult> ReceiveAsync(HttpRequest request)
    {
        IResult Retry(string code)
        {
            request.HttpContext.Response.Headers.RetryAfter = "30";
            return Failure(503, code);
        }
        var size = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false }) size.MaxRequestBodySize = MaxArchiveBytes;
        if (request.ContentLength > MaxArchiveBytes) return Failure(413, "report_too_large");
        if (!string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/zip", StringComparison.OrdinalIgnoreCase))
            return Failure(415, "zip_required");
        var ids = request.Headers["X-Feedback-Id"];
        string id = ids.ToString();
        if (ids.Count != 1 || !ValidId(id)) return Failure(400, "invalid_report_id");
        byte[] archive;
        string text;
        try
        {
            archive = await ReadBoundedAsync(request.Body, MaxArchiveBytes, request.HttpContext.RequestAborted);
            text = ValidateArchive(archive, id);
        }
        catch (ReportTooLargeException) { return Failure(413, "report_too_large"); }
        catch (BadHttpRequestException e) when (e.StatusCode == 413) { return Failure(413, "report_too_large"); }
        catch (Exception e) when (e is InvalidDataException or JsonException or DecoderFallbackException or ArgumentException or OverflowException)
        { return Failure(400, "invalid_report"); }
        // Do not queue many ZIPs while SMTP is slow. A manual retry keeps the same report ID.
        if (!await _gate.WaitAsync(0)) return Retry("delivery_busy");
        try
        {
            string hash = Convert.ToHexString(SHA256.HashData(archive));
            // Reserve the writer before SMTP, including when two service instances share this database.
            using var transaction = _db.BeginTransaction();
            using (var existing = _db.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT archive_hash FROM feedback_receipts WHERE id=$id";
                existing.Parameters.AddWithValue("$id", id);
                if (existing.ExecuteScalar() is string previous)
                    return previous == hash ? Results.Ok(new { ok = true, id }) : Failure(409, "report_id_conflict");
            }
            if (!_config.Enabled || Recipient.Length == 0) return Retry("delivery_unavailable");
            try { await SendAsync(id, text, archive); }
            catch (Exception e) when (e is CommandException or ProtocolException or SslHandshakeException or MailKit.Security.AuthenticationException
                or System.Security.Authentication.AuthenticationException or OperationCanceledException or IOException
                or System.Net.Sockets.SocketException or TimeoutException or InvalidOperationException or NotSupportedException)
            {
                // Report text, contact, SMTP credentials and attachments never enter service logs.
                Console.Error.WriteLine($"[反馈] 邮件投递失败，报告 {id} 保留在客户端等待重试。");
                return Retry("delivery_unavailable");
            }
            using var confirmed = _db.CreateCommand();
            confirmed.Transaction = transaction;
            confirmed.CommandText = "INSERT INTO feedback_receipts(id,archive_hash,accepted) VALUES($id,$hash,$at)";
            confirmed.Parameters.AddWithValue("$id", id);
            confirmed.Parameters.AddWithValue("$hash", hash);
            confirmed.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            confirmed.ExecuteNonQuery();
            transaction.Commit();
            return Results.Ok(new { ok = true, id });
        }
        finally { _gate.Release(); }
    }

    private async Task SendAsync(string id, string text, byte[] archive)
    {
        using var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_config.From));
        message.To.Add(MailboxAddress.Parse(Recipient));
        message.Subject = "[Wheatstead 问题反馈] " + id;
        var body = new BodyBuilder { TextBody = "报告编号: " + id + "\n\n" + text };
        body.Attachments.Add(id + ".zip", archive, new ContentType("application", "zip"));
        message.Body = body.ToMessageBody();
        using var smtp = new SmtpClient { Timeout = _config.TimeoutSeconds * 1000 };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        // Finish delivery and persist its receipt even if the HTTP peer disconnects meanwhile.
        await smtp.ConnectAsync(_config.Host, _config.Port, _config.Security, deadline.Token);
        if (_config.Username.Length > 0) await smtp.AuthenticateAsync(_config.Username, _config.Password, deadline.Token);
        await smtp.SendAsync(message, deadline.Token);
        // A failed QUIT after the final DATA acceptance must not turn an accepted report into a retry.
        try { await smtp.DisconnectAsync(true, deadline.Token); }
        catch (Exception e) when (e is IOException or CommandException or ProtocolException or OperationCanceledException or System.Net.Sockets.SocketException) { }
    }

    public static bool ValidId(string value) => value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static IResult Failure(int status, string code) => Results.Json(new { code }, statusCode: status);

    private static string ValidateArchive(byte[] bytes, string id)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
        if (zip.Entries.Count is < 2 or > 5) throw new InvalidDataException();
        var names = new HashSet<string>(StringComparer.Ordinal);
        string? text = null;
        int metadata = 0;
        foreach (var entry in zip.Entries)
        {
            int limit = entry.FullName switch
            {
                "report.txt" => 64 * 1024,
                "report.json" or "sentry.json" => 128 * 1024,
                "game.log" => 64 * 1024,
                "screenshot.png" => 256 * 1024,
                "save.json.gz" => 512 * 1024,
                _ => throw new InvalidDataException()
            };
            if (!names.Add(entry.FullName) || entry.Length <= 0 || entry.Length > limit) throw new InvalidDataException();
            using var source = entry.Open();
            byte[] data;
            try { data = ReadBounded(source, limit); }
            catch (ReportTooLargeException) { throw new InvalidDataException(); }
            if (data.Length != entry.Length) throw new InvalidDataException();
            if (entry.FullName == "report.txt")
            {
                text = new UTF8Encoding(false, true).GetString(data);
                if (string.IsNullOrWhiteSpace(text) || text.Contains('\0')) throw new InvalidDataException();
            }
            else if (entry.FullName is "report.json" or "sentry.json")
            {
                metadata++;
                using var json = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 16 });
                string key = entry.FullName == "report.json" ? "id" : "event_id";
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty(key, out var value)
                    || value.ValueKind != JsonValueKind.String || value.GetString() != id) throw new InvalidDataException();
            }
            else if (entry.FullName == "screenshot.png")
            {
                if (data.Length < 24 || !data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                    || !data.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidDataException();
                uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4));
                uint height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4));
                if (width is 0 or > 640 || height is 0 or > 360) throw new InvalidDataException();
            }
            else if (entry.FullName == "save.json.gz")
            {
                using var saved = new GZipStream(new MemoryStream(data, false), CompressionMode.Decompress);
                // Match the client's 16 MiB uncompressed save bound without allocating the expanded save.
                int total = 0;
                var buffer = new byte[8192];
                int read;
                while ((read = saved.Read(buffer)) > 0)
                    if ((total += read) > 16 * 1024 * 1024) throw new InvalidDataException();
                if (total == 0) throw new InvalidDataException();
            }
        }
        if (text == null || metadata != 1) throw new InvalidDataException();
        return text;
    }

    private static byte[] ReadBounded(Stream stream, int limit)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (output.Length + read > limit) throw new ReportTooLargeException();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + read > limit) throw new ReportTooLargeException();
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        return output.ToArray();
    }

    public void Dispose() { _db.Dispose(); _gate.Dispose(); }
    private sealed class ReportTooLargeException : Exception { }
}

sealed record FeedbackConfig(string Host, int Port, string Username, string Password, string From,
    SecureSocketOptions Security, int TimeoutSeconds, string DatabasePath, bool Enabled)
{
    public static FeedbackConfig FromEnvironment(string databasePath)
    {
        static string Read(string key, string fallback = "") => Environment.GetEnvironmentVariable("WHEATSTEAD_FEEDBACK_" + key)?.Trim() ?? fallback;
        string host = Read("SMTP_HOST"), username = Read("SMTP_USERNAME"), from = Read("SMTP_FROM", username);
        string password = Environment.GetEnvironmentVariable("WHEATSTEAD_FEEDBACK_SMTP_PASSWORD") ?? "";
        string security = Read("SMTP_SECURITY", "ssl");
        bool portOk = int.TryParse(Read("SMTP_PORT", security == "starttls" ? "587" : "465"), out int port) && port is > 0 and <= 65535;
        bool timeoutOk = int.TryParse(Read("SMTP_TIMEOUT_SECONDS", "15"), out int timeout) && timeout is >= 1 and <= 20;
        bool insecure = security == "none" && Read("ALLOW_INSECURE_LOOPBACK") == "1";
        bool loopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
        bool fromOk = MailboxAddress.TryParse(from, out var address) && address.Address == from && !from.Any(char.IsControl);
        bool enabled = host.Length > 0 && !host.Any(char.IsWhiteSpace) && portOk && timeoutOk && fromOk
            && (username.Length == 0) == (password.Length == 0) && (security is "ssl" or "starttls" || insecure && loopback);
        var options = security == "ssl" ? SecureSocketOptions.SslOnConnect : security == "starttls" ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        return new(host, port, username, password, from, options, timeout, Read("DB", databasePath), enabled);
    }
}

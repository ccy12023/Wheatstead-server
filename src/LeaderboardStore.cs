using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

/// <summary>Device credentials for leaderboard accounts; no installation tracking or heartbeats.</summary>
sealed class LeaderboardStore : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _db;
    public LeaderboardStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString());
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS accounts (
              id TEXT PRIMARY KEY,name TEXT NOT NULL,name_key TEXT NOT NULL UNIQUE,token_hash TEXT NOT NULL UNIQUE,
              disabled INTEGER NOT NULL DEFAULT 0,created INTEGER NOT NULL,rebind_hash TEXT,rebind_expires INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS scores (
              run_hash TEXT PRIMARY KEY,account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
              days INTEGER NOT NULL,seconds REAL NOT NULL,hard INTEGER NOT NULL,multiplayer INTEGER NOT NULL,version TEXT NOT NULL,submitted INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS scores_rank ON scores(hard,multiplayer,days,seconds,submitted,run_hash);
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,action TEXT NOT NULL,target TEXT NOT NULL,at INTEGER NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }
    private SqliteCommand Command(string sql, params (string Key, object Value)[] values)
    {
        var c = _db.CreateCommand(); c.CommandText = sql;
        foreach (var pair in values) c.Parameters.AddWithValue(pair.Key, pair.Value);
        return c;
    }
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string NameKey(string text) => text.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    public static bool ValidName(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 32 && NameKey(s).Length <= 32
        && !s.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);
    public static bool ValidToken(string? s) => s is { Length: 64 } && s.All(char.IsAsciiHexDigit);
    public Account? Register(string name, string token)
    {
        lock (_gate)
        {
            var a = new Account(Guid.NewGuid().ToString("N"), name.Trim());
            using var c = Command("INSERT OR IGNORE INTO accounts(id,name,name_key,token_hash,created) VALUES($id,$name,$key,$token,$at)",
                ("$id",a.Id),("$name",a.Name),("$key",NameKey(name)),("$token",Hash(token)),("$at",Now));
            return c.ExecuteNonQuery() == 1 ? a : null;
        }
    }
    public Account? Authenticate(string token, string? name = null)
    {
        if (!ValidToken(token)) return null;
        lock (_gate)
        {
            using var c = Command("SELECT id,name FROM accounts WHERE token_hash=$token AND disabled=0 AND ($key='' OR name_key=$key)",
                ("$token",Hash(token)),("$key",name == null ? "" : NameKey(name)));
            using var r = c.ExecuteReader(); return r.Read() ? new Account(r.GetString(0),r.GetString(1)) : null;
        }
    }
    public Account? Rebind(string name, string code, string token)
    {
        lock (_gate)
        {
            using var c = Command("""
                UPDATE accounts SET token_hash=$token,rebind_hash=NULL,rebind_expires=0
                WHERE name_key=$name AND rebind_hash=$code AND rebind_expires>$at AND disabled=0
                  AND NOT EXISTS (SELECT 1 FROM accounts other WHERE other.token_hash=$token AND other.id<>accounts.id)
                """,
                ("$token",Hash(token)),("$name",NameKey(name)),("$code",Hash(code)),("$at",Now));
            if (c.ExecuteNonQuery() != 1) return null;
            return Authenticate(token,name);
        }
    }
    public ScoreRecordResult Record(string token, SpeedrunSubmission s)
    {
        lock (_gate)
        {
            // Authentication and the write must share the lock with disable, deletion and rebind.
            var a = Authenticate(token);
            if (a == null) return ScoreRecordResult.Unauthorized;
            string canonical = s.RunKey.ToLowerInvariant();
            string key = Hash(canonical);
            string upper = Hash(canonical.ToUpperInvariant());
            string original = Hash(s.RunKey);
            using (var existing = Command("SELECT run_hash FROM scores WHERE run_hash IN ($key,$upper,$original)",
                ("$key",key),("$upper",upper),("$original",original)))
            using (var reader = existing.ExecuteReader())
            {
                if (reader.Read())
                {
                    key = reader.GetString(0);
                    // Never pick an arbitrary owner if old records already conflict.
                    if (reader.Read()) return ScoreRecordResult.Conflict;
                }
            }
            using var c = Command("""
                INSERT INTO scores VALUES($key,$account,$days,$seconds,$hard,$multi,$version,$at)
                ON CONFLICT(run_hash) DO UPDATE SET days=excluded.days,seconds=excluded.seconds,version=excluded.version
                WHERE scores.account_id=excluded.account_id AND excluded.hard=scores.hard AND excluded.multiplayer=scores.multiplayer
                  AND (excluded.days<scores.days OR (excluded.days=scores.days AND excluded.seconds<=scores.seconds));
                """,("$key",key),("$account",a.Id),("$days",s.Days),("$seconds",s.Seconds),
                ("$hard",s.Hard),("$multi",s.Multiplayer),("$version",s.Version),("$at",Now));
            return c.ExecuteNonQuery() == 1 ? ScoreRecordResult.Recorded : ScoreRecordResult.Conflict;
        }
    }
    public object[] Top(bool hard, bool multiplayer)
    {
        lock (_gate)
        {
            using var c = Command("SELECT a.name,s.days,s.seconds,s.version FROM scores s JOIN accounts a ON a.id=s.account_id WHERE a.disabled=0 AND hard=$hard AND multiplayer=$multi ORDER BY days,seconds,submitted,run_hash LIMIT 100",
                ("$hard",hard),("$multi",multiplayer));
            using var r = c.ExecuteReader(); var rows = new List<object>();
            while (r.Read()) rows.Add(new { rank=rows.Count+1,name=r.GetString(0),days=r.GetInt32(1),seconds=r.GetDouble(2),version=r.GetString(3) });
            return rows.ToArray();
        }
    }
    public object Overview()
    {
        lock (_gate)
        {
            using var c = Command("SELECT (SELECT COUNT(*) FROM accounts),(SELECT COUNT(*) FROM scores),(SELECT COUNT(*) FROM accounts WHERE disabled=1)");
            using var r = c.ExecuteReader();r.Read();return new { accounts=r.GetInt64(0),scores=r.GetInt64(1),disabled=r.GetInt64(2) };
        }
    }
    public object[] Accounts()
    {
        lock (_gate)
        {
            using var c = Command("SELECT id,name,disabled,created,(SELECT COUNT(*) FROM scores WHERE account_id=a.id) FROM accounts a ORDER BY created DESC,id LIMIT 500");
            using var r = c.ExecuteReader(); var rows=new List<object>();
            while(r.Read()) rows.Add(new { id=r.GetString(0),name=r.GetString(1),disabled=r.GetBoolean(2),created=r.GetInt64(3),scores=r.GetInt64(4) });
            return rows.ToArray();
        }
    }
    public object[] Scores()
    {
        lock (_gate)
        {
            using var c = Command("SELECT s.run_hash,a.name,s.days,s.seconds,s.hard,s.multiplayer,s.version,s.submitted FROM scores s JOIN accounts a ON a.id=s.account_id ORDER BY s.submitted DESC,s.run_hash LIMIT 500");
            using var r = c.ExecuteReader(); var rows=new List<object>();
            while(r.Read()) rows.Add(new { id=r.GetString(0),name=r.GetString(1),days=r.GetInt32(2),seconds=r.GetDouble(3),hard=r.GetBoolean(4),multiplayer=r.GetBoolean(5),version=r.GetString(6),submitted=r.GetInt64(7) });
            return rows.ToArray();
        }
    }
    public object[] Audit()
    {
        lock (_gate)
        {
            using var c=Command("SELECT action,target,at FROM audit ORDER BY id DESC LIMIT 100");
            using var r=c.ExecuteReader();var rows=new List<object>();
            while(r.Read())rows.Add(new { action=r.GetString(0),target=r.GetString(1),at=r.GetInt64(2) });
            return rows.ToArray();
        }
    }
    public string? Manage(string action,string id)
    {
        lock (_gate)
        {
            string code=action=="rebind" ? Guid.NewGuid().ToString("N") : "";
            using var tx=_db.BeginTransaction();
            using var c=action switch {
                "disable"=>Command("UPDATE accounts SET disabled=1 WHERE id=$id",("$id",id)),
                "enable"=>Command("UPDATE accounts SET disabled=0 WHERE id=$id",("$id",id)),
                "delete_account"=>Command("DELETE FROM accounts WHERE id=$id",("$id",id)),
                "delete_score"=>Command("DELETE FROM scores WHERE run_hash=$id",("$id",id)),
                "rebind"=>Command("UPDATE accounts SET rebind_hash=$code,rebind_expires=$expires WHERE id=$id AND disabled=0",("$id",id),("$code",Hash(code)),("$expires",Now+900)),
                _=>throw new ArgumentException("Unknown action") };
            c.Transaction=tx;if(c.ExecuteNonQuery()!=1)return null;
            using var audit=Command("INSERT INTO audit(action,target,at) VALUES($action,$id,$at)",("$action",action),("$id",id),("$at",Now));
            audit.Transaction=tx;audit.ExecuteNonQuery();tx.Commit();return code;
        }
    }
    public void Dispose()=>_db.Dispose();
}
sealed record Account(string Id,string Name);
enum ScoreRecordResult { Recorded, Unauthorized, Conflict }
sealed record AccountRequest(string Name,string DeviceToken,string? Code);
sealed record SpeedrunSubmission(string RunKey,int Days,double Seconds,bool Hard,bool Multiplayer,string Version)
{
    public static bool IsValid(SpeedrunSubmission? s)=>s!=null && Guid.TryParseExact(s.RunKey,"N",out _)
        && s.Days is >=0 and <=1000000 && double.IsFinite(s.Seconds) && s.Seconds is >0 and <=315576000
        && s.Version is { Length: >0 and <=64 } && !s.Version.Any(char.IsControl);
}

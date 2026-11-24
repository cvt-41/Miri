using Microsoft.Data.Sqlite;

namespace Miri.Brain;

internal sealed record ChatTurn(string User, string Assistant);

/// <summary>Durable user facts in SQLite (memory.db) + the last few chat turns in RAM.</summary>
internal sealed class MemoryStore
{
    public const int MaxFactsInPrompt = 6;   // keep the chat prompt small: CPU prompt evaluation dominates first-token time
    public const int MaxTurns = 6;

    private readonly string _connectionString;
    private readonly object _turnLock = new();
    private readonly LinkedList<ChatTurn> _turns = new();
    private bool _dbOk;

    public MemoryStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS facts (" +
                " id INTEGER PRIMARY KEY AUTOINCREMENT," +
                " text TEXT NOT NULL," +
                " created TEXT NOT NULL);";
            cmd.ExecuteNonQuery();
            _dbOk = true;
        }
        catch (Exception)
        {
            _dbOk = false;   // memory is a nice-to-have; keep working without it
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public IReadOnlyList<string> RecentFacts(int max = MaxFactsInPrompt)
    {
        if (!_dbOk) return Array.Empty<string>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT text FROM facts ORDER BY id DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", max);
            var list = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            list.Reverse();   // oldest first reads more naturally
            return list;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Adds a fact unless an identical (case-insensitive) one exists. Returns true if stored.</summary>
    public bool AddFact(string text)
    {
        text = text.Trim();
        if (!_dbOk || text.Length == 0) return false;
        try
        {
            using var conn = Open();
            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM facts WHERE lower(text) = lower($t);";
                check.Parameters.AddWithValue("$t", text);
                if (Convert.ToInt64(check.ExecuteScalar()) > 0) return false;
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO facts(text, created) VALUES ($t, $c);";
            cmd.Parameters.AddWithValue("$t", text);
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void AddTurn(string user, string assistant)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(assistant)) return;
        lock (_turnLock)
        {
            _turns.AddLast(new ChatTurn(TextUtil.Truncate(user.Trim(), 600), TextUtil.Truncate(assistant.Trim(), 700)));
            while (_turns.Count > MaxTurns) _turns.RemoveFirst();
        }
    }

    public IReadOnlyList<ChatTurn> RecentTurns()
    {
        lock (_turnLock) return _turns.ToList();
    }
}

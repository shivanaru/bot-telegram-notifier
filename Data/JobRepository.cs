using Microsoft.Data.Sqlite;

/// <summary>
/// Persists notified job URLs in a local SQLite database so that the same
/// job is never sent to Telegram more than once.
/// </summary>
class JobRepository : IDisposable
{
    private readonly SqliteConnection _connection;

    public JobRepository(string dbPath)
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS notified_jobs (
                url         TEXT PRIMARY KEY,
                title       TEXT NOT NULL,
                source      TEXT NOT NULL,
                notified_at TEXT NOT NULL   -- ISO-8601 UTC
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns true if this job URL has already been notified.</summary>
    public bool IsAlreadyNotified(string url)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM notified_jobs WHERE url = $url LIMIT 1;";
        cmd.Parameters.AddWithValue("$url", url);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Marks a job as notified. Silently ignores duplicates.</summary>
    public void MarkAsNotified(string url, string title, string source)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO notified_jobs (url, title, source, notified_at)
            VALUES ($url, $title, $source, $notifiedAt);
            """;
        cmd.Parameters.AddWithValue("$url", url);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$notifiedAt", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}

using Microsoft.Data.Sqlite;

namespace DiskMonitor.Core.Database;

public sealed class DatabaseManager : IDisposable
{
    private readonly string _connectionString;
    private bool _disposed;

    public DatabaseManager(string dbPath)
    {
        _connectionString = $"Data Source={dbPath};Pooling=True;";
        Initialize(dbPath);
    }

    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Initialize(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using var conn = OpenConnection();

        // WAL 模式：读写并发互不阻塞
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = Schema.CreateTables;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SqliteConnection.ClearAllPools();
    }
}

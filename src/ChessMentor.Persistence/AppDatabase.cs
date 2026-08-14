using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace ChessMentor.Persistence;

/// <summary>
/// Owns the SQLite file and serializes all synchronous provider work on a worker thread.
/// Microsoft.Data.Sqlite exposes async members, but its I/O is synchronous; callers never run it on the UI thread.
/// </summary>
public sealed class AppDatabase : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;
    private bool _disposed;

    public AppDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await ExecuteAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
                command.ExecuteNonQuery();
                DatabaseMigrator.Migrate(connection);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> ExecuteAsync<T>(Func<SqliteConnection, T> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var connection = new SqliteConnection(_connectionString);
                    connection.Open();
                    using var pragma = connection.CreateCommand();
                    pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
                    pragma.ExecuteNonQuery();
                    return operation(connection);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(T Result, double ElapsedMilliseconds)> MeasureAsync<T>(
        Func<SqliteConnection, T> operation,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        return (result, stopwatch.Elapsed.TotalMilliseconds);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _gate.Dispose();
            SqliteConnection.ClearAllPools();
        }

        return ValueTask.CompletedTask;
    }
}

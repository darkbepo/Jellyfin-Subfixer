using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.State;

/// <summary>
/// SQLite state store (ARCHITECTURE.md, state store). Uses the Microsoft.Data.Sqlite the server already loads (D17).
/// </summary>
public sealed class SqliteStateStore : IStateStore, IDisposable
{
    /// <summary>Schema version this code writes.</summary>
    public const int CurrentSchemaVersion = 1;

    private const int MaxIdsPerQuery = 500;

    // Index = target version. Each step runs inside one transaction.
    private static readonly string[] Migrations =
    [
        string.Empty,
        """
        CREATE TABLE synced_items (
          item_id            TEXT PRIMARY KEY,
          video_path         TEXT NOT NULL,
          video_size         INTEGER NOT NULL,
          video_mtime_utc    TEXT NOT NULL,
          subtitle_path      TEXT NOT NULL,
          status             TEXT NOT NULL,
          message            TEXT,
          audio_language     TEXT,
          subsync_version    TEXT,
          seconds            REAL,
          updated_utc        TEXT NOT NULL
        );
        CREATE INDEX ix_synced_status ON synced_items(status);
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """,
    ];

    private readonly string _connectionString;
    private readonly ILogger<SqliteStateStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteStateStore"/> class.
    /// </summary>
    /// <param name="databasePath">Full path of the database file.</param>
    /// <param name="logger">Logger.</param>
    public SqliteStateStore(string databasePath, ILogger<SqliteStateStore> logger)
    {
        Location = databasePath;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    /// <inheritdoc />
    public string Location { get; }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(Location);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);", cancellationToken).ConfigureAwait(false);

            var version = await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"State database {Location} has schema version {version}, newer than this plugin understands ({CurrentSchemaVersion}).");
            }

            for (var target = version + 1; target <= CurrentSchemaVersion; target++)
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, Migrations[target], cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, "DELETE FROM schema_version;", cancellationToken).ConfigureAwait(false);
                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO schema_version (version) VALUES ($v);";
                    insert.Parameters.AddWithValue("$v", target);
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Subtitle Fixer state database migrated to version {Version}", target);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, SyncRecord>> GetAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, SyncRecord>();
        if (itemIds.Count == 0)
        {
            return result;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var chunk in itemIds.Distinct().Chunk(MaxIdsPerQuery))
        {
            await using var command = connection.CreateCommand();
            var sql = new StringBuilder("SELECT item_id, video_path, video_size, video_mtime_utc, subtitle_path, status, message, audio_language, subsync_version, seconds, updated_utc FROM synced_items WHERE item_id IN (");
            for (var i = 0; i < chunk.Length; i++)
            {
                var name = "$id" + i.ToString(CultureInfo.InvariantCulture);
                sql.Append(i == 0 ? name : "," + name);
                command.Parameters.AddWithValue(name, FormatId(chunk[i]));
            }

            sql.Append(");");
            command.CommandText = sql.ToString();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var record = ReadRecord(reader);
                result[record.ItemId] = record;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task UpsertAsync(SyncRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO synced_items (item_id, video_path, video_size, video_mtime_utc, subtitle_path, status, message, audio_language, subsync_version, seconds, updated_utc)
                VALUES ($id, $vp, $vs, $vm, $sp, $st, $msg, $al, $ver, $sec, $up)
                ON CONFLICT(item_id) DO UPDATE SET
                  video_path = excluded.video_path, video_size = excluded.video_size, video_mtime_utc = excluded.video_mtime_utc,
                  subtitle_path = excluded.subtitle_path, status = excluded.status, message = excluded.message,
                  audio_language = excluded.audio_language, subsync_version = excluded.subsync_version,
                  seconds = excluded.seconds, updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$id", FormatId(record.ItemId));
            command.Parameters.AddWithValue("$vp", record.Video.Path);
            command.Parameters.AddWithValue("$vs", record.Video.Size);
            command.Parameters.AddWithValue("$vm", FormatDate(record.Video.MtimeUtc));
            command.Parameters.AddWithValue("$sp", record.SubtitlePath);
            command.Parameters.AddWithValue("$st", FormatStatus(record.Status));
            command.Parameters.AddWithValue("$msg", (object?)record.Message ?? DBNull.Value);
            command.Parameters.AddWithValue("$al", (object?)record.AudioLanguage ?? DBNull.Value);
            command.Parameters.AddWithValue("$ver", (object?)record.SubSyncVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("$sec", (object?)record.Seconds ?? DBNull.Value);
            command.Parameters.AddWithValue("$up", FormatDate(record.UpdatedUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM synced_items WHERE item_id = $id;";
            command.Parameters.AddWithValue("$id", FormatId(itemId));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $k;";
        command.Parameters.AddWithValue("$k", key);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    /// <inheritdoc />
    public async Task SetMetaAsync(string key, string value, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$v", value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _writeLock.Dispose();
    }

    internal static string FormatStatus(SyncStatus status) => status switch
    {
        SyncStatus.Done => "done",
        SyncStatus.Check => "check",
        SyncStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    internal static SyncStatus ParseStatus(string value) => value switch
    {
        "done" => SyncStatus.Done,
        "check" => SyncStatus.Check,
        "failed" => SyncStatus.Failed,
        _ => throw new FormatException($"Unknown sync status '{value}'."),
    };

    private static string FormatId(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private static string FormatDate(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseDate(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static SyncRecord ReadRecord(SqliteDataReader reader)
    {
        return new SyncRecord
        {
            ItemId = Guid.ParseExact(reader.GetString(0), "N"),
            Video = new VideoFingerprint(reader.GetString(1), reader.GetInt64(2), ParseDate(reader.GetString(3))),
            SubtitlePath = reader.GetString(4),
            Status = ParseStatus(reader.GetString(5)),
            Message = reader.IsDBNull(6) ? null : reader.GetString(6),
            AudioLanguage = reader.IsDBNull(7) ? null : reader.GetString(7),
            SubSyncVersion = reader.IsDBNull(8) ? null : reader.GetString(8),
            Seconds = reader.IsDBNull(9) ? null : reader.GetDouble(9),
            UpdatedUtc = ParseDate(reader.GetString(10)),
        };
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> GetSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}

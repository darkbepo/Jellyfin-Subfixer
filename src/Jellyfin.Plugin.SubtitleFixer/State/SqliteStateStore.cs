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
    public const int CurrentSchemaVersion = 5;

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
        """
        CREATE TABLE in_flight (
          item_id        TEXT PRIMARY KEY,
          subtitle_path  TEXT NOT NULL,
          backup_path    TEXT NOT NULL,
          started_utc    TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE failed_syncs (
          id             INTEGER PRIMARY KEY AUTOINCREMENT,
          item_id        TEXT NOT NULL,
          name           TEXT NOT NULL,
          series_name    TEXT NOT NULL,
          user_id        TEXT NOT NULL,
          status         TEXT NOT NULL,
          message        TEXT,
          source         TEXT NOT NULL,
          note           TEXT,
          finished_utc   TEXT NOT NULL
        );
        CREATE INDEX ix_failed_user ON failed_syncs(user_id, id);
        """,
        """
        CREATE TABLE audio_checks (
          item_id          TEXT PRIMARY KEY,
          video_path       TEXT NOT NULL,
          video_size       INTEGER NOT NULL,
          video_mtime_utc  TEXT NOT NULL,
          language         TEXT,
          probability      REAL,
          speech_seconds   REAL NOT NULL,
          checked_utc      TEXT NOT NULL
        );
        """,
        // v5 (D50): records, journal entries and the failed-syncs log per item and subtitle language; existing rows are English
        """
        CREATE TABLE synced_items_v5 (
          item_id            TEXT NOT NULL,
          language           TEXT NOT NULL DEFAULT 'en',
          video_path         TEXT NOT NULL,
          video_size         INTEGER NOT NULL,
          video_mtime_utc    TEXT NOT NULL,
          subtitle_path      TEXT NOT NULL,
          status             TEXT NOT NULL,
          message            TEXT,
          audio_language     TEXT,
          subsync_version    TEXT,
          seconds            REAL,
          updated_utc        TEXT NOT NULL,
          PRIMARY KEY (item_id, language)
        );
        INSERT INTO synced_items_v5 (item_id, language, video_path, video_size, video_mtime_utc, subtitle_path, status, message, audio_language, subsync_version, seconds, updated_utc)
          SELECT item_id, 'en', video_path, video_size, video_mtime_utc, subtitle_path, status, message, audio_language, subsync_version, seconds, updated_utc FROM synced_items;
        DROP TABLE synced_items;
        ALTER TABLE synced_items_v5 RENAME TO synced_items;
        CREATE INDEX ix_synced_status ON synced_items(status);
        CREATE TABLE in_flight_v5 (
          item_id        TEXT NOT NULL,
          language       TEXT NOT NULL DEFAULT 'en',
          subtitle_path  TEXT NOT NULL,
          backup_path    TEXT NOT NULL,
          started_utc    TEXT NOT NULL,
          PRIMARY KEY (item_id, language)
        );
        INSERT INTO in_flight_v5 (item_id, language, subtitle_path, backup_path, started_utc)
          SELECT item_id, 'en', subtitle_path, backup_path, started_utc FROM in_flight;
        DROP TABLE in_flight;
        ALTER TABLE in_flight_v5 RENAME TO in_flight;
        ALTER TABLE failed_syncs ADD COLUMN language TEXT NOT NULL DEFAULT 'en';
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

    /// <summary>English records (as before D50).</summary>
    /// <param name="itemIds">Item ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Records by item id.</returns>
    public Task<IReadOnlyDictionary<Guid, SyncRecord>> GetAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken) =>
        GetAsync(itemIds, Domain.SubtitleLanguage.English, cancellationToken);

    /// <summary>Removes the English record of an item.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task DeleteAsync(Guid itemId, CancellationToken cancellationToken) => DeleteAsync(itemId, Domain.SubtitleLanguage.English, cancellationToken);

    /// <summary>Removes the English journal entry of an item.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task RemoveInFlightAsync(Guid itemId, CancellationToken cancellationToken) =>
        RemoveInFlightAsync(itemId, Domain.SubtitleLanguage.English, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, SyncRecord>> GetAsync(IReadOnlyCollection<Guid> itemIds, string language, CancellationToken cancellationToken)
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
            var sql = new StringBuilder("SELECT item_id, video_path, video_size, video_mtime_utc, subtitle_path, status, message, audio_language, subsync_version, seconds, updated_utc, language FROM synced_items WHERE language = $lang AND item_id IN (");
            command.Parameters.AddWithValue("$lang", Domain.SubtitleLanguage.Normalize(language));
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
                INSERT INTO synced_items (item_id, language, video_path, video_size, video_mtime_utc, subtitle_path, status, message, audio_language, subsync_version, seconds, updated_utc)
                VALUES ($id, $lang, $vp, $vs, $vm, $sp, $st, $msg, $al, $ver, $sec, $up)
                ON CONFLICT(item_id, language) DO UPDATE SET
                  video_path = excluded.video_path, video_size = excluded.video_size, video_mtime_utc = excluded.video_mtime_utc,
                  subtitle_path = excluded.subtitle_path, status = excluded.status, message = excluded.message,
                  audio_language = excluded.audio_language, subsync_version = excluded.subsync_version,
                  seconds = excluded.seconds, updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$id", FormatId(record.ItemId));
            command.Parameters.AddWithValue("$lang", Domain.SubtitleLanguage.Normalize(record.Language));
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
    public async Task<IReadOnlyList<AudioCheck>> GetAudioChecksAsync(CancellationToken cancellationToken)
    {
        var result = new List<AudioCheck>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id, video_path, video_size, video_mtime_utc, language, probability, speech_seconds, checked_utc FROM audio_checks;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new AudioCheck(
                Guid.ParseExact(reader.GetString(0), "N"),
                new VideoFingerprint(reader.GetString(1), reader.GetInt64(2), ParseDate(reader.GetString(3))),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.GetDouble(6),
                ParseDate(reader.GetString(7))));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task UpsertAudioCheckAsync(AudioCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audio_checks (item_id, video_path, video_size, video_mtime_utc, language, probability, speech_seconds, checked_utc)
                VALUES ($id, $vp, $vs, $vm, $lang, $prob, $sp, $at)
                ON CONFLICT(item_id) DO UPDATE SET
                  video_path = excluded.video_path, video_size = excluded.video_size, video_mtime_utc = excluded.video_mtime_utc,
                  language = excluded.language, probability = excluded.probability, speech_seconds = excluded.speech_seconds,
                  checked_utc = excluded.checked_utc;
                """;
            command.Parameters.AddWithValue("$id", FormatId(check.ItemId));
            command.Parameters.AddWithValue("$vp", check.Video.Path);
            command.Parameters.AddWithValue("$vs", check.Video.Size);
            command.Parameters.AddWithValue("$vm", FormatDate(check.Video.MtimeUtc));
            command.Parameters.AddWithValue("$lang", (object?)check.Language ?? DBNull.Value);
            command.Parameters.AddWithValue("$prob", (object?)check.Probability ?? DBNull.Value);
            command.Parameters.AddWithValue("$sp", check.SpeechSeconds);
            command.Parameters.AddWithValue("$at", FormatDate(check.CheckedUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid itemId, string language, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM synced_items WHERE item_id = $id AND language = $lang;";
            command.Parameters.AddWithValue("$id", FormatId(itemId));
            command.Parameters.AddWithValue("$lang", Domain.SubtitleLanguage.Normalize(language));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task AddInFlightAsync(InFlightBackup entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO in_flight (item_id, language, subtitle_path, backup_path, started_utc) VALUES ($id, $lang, $sp, $bp, $st)
                ON CONFLICT(item_id, language) DO UPDATE SET subtitle_path = excluded.subtitle_path, backup_path = excluded.backup_path, started_utc = excluded.started_utc;
                """;
            command.Parameters.AddWithValue("$id", FormatId(entry.ItemId));
            command.Parameters.AddWithValue("$lang", Domain.SubtitleLanguage.Normalize(entry.Language));
            command.Parameters.AddWithValue("$sp", entry.SubtitlePath);
            command.Parameters.AddWithValue("$bp", entry.BackupPath);
            command.Parameters.AddWithValue("$st", FormatDate(entry.StartedUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RemoveInFlightAsync(Guid itemId, string language, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM in_flight WHERE item_id = $id AND language = $lang;";
            command.Parameters.AddWithValue("$id", FormatId(itemId));
            command.Parameters.AddWithValue("$lang", Domain.SubtitleLanguage.Normalize(language));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InFlightBackup>> GetInFlightAsync(CancellationToken cancellationToken)
    {
        var result = new List<InFlightBackup>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id, subtitle_path, backup_path, started_utc, language FROM in_flight;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new InFlightBackup(Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1), reader.GetString(2), ParseDate(reader.GetString(3)), reader.GetString(4)));
        }

        return result;
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
    public async Task AddFailedSyncAsync(FailedSync entry, int keep, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO failed_syncs (item_id, name, series_name, user_id, status, message, source, note, finished_utc, language)
                    VALUES ($item, $name, $series, $user, $st, $msg, $src, $note, $fin, $lang);
                    """;
                insert.Parameters.AddWithValue("$item", FormatId(entry.ItemId));
                insert.Parameters.AddWithValue("$name", entry.Name);
                insert.Parameters.AddWithValue("$series", entry.SeriesName);
                insert.Parameters.AddWithValue("$user", FormatId(entry.UserId));
                insert.Parameters.AddWithValue("$st", FormatStatus(entry.Status));
                insert.Parameters.AddWithValue("$msg", (object?)entry.Message ?? DBNull.Value);
                insert.Parameters.AddWithValue("$src", entry.Source);
                insert.Parameters.AddWithValue("$note", (object?)entry.Note ?? DBNull.Value);
                insert.Parameters.AddWithValue("$fin", FormatDate(entry.FinishedUtc));
                insert.Parameters.AddWithValue("$lang", Domain.SubtitleLanguage.Normalize(entry.Language));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var trim = connection.CreateCommand())
            {
                trim.Transaction = transaction;
                trim.CommandText = "DELETE FROM failed_syncs WHERE id NOT IN (SELECT id FROM failed_syncs ORDER BY id DESC LIMIT $keep);";
                trim.Parameters.AddWithValue("$keep", Math.Max(1, keep));
                await trim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<FailedSync> Items, int TotalCount)> GetFailedSyncsAsync(Guid? userId, int startIndex, int limit, CancellationToken cancellationToken)
    {
        var where = userId is null ? string.Empty : " WHERE user_id = $user";
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        int total;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM failed_syncs" + where + ";";
            if (userId is { } u)
            {
                count.Parameters.AddWithValue("$user", FormatId(u));
            }

            total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        var items = new List<FailedSync>();
        await using var command = connection.CreateCommand();
        command.CommandText = FailedSyncColumns + where + " ORDER BY id DESC LIMIT $limit OFFSET $start;";
        if (userId is { } user)
        {
            command.Parameters.AddWithValue("$user", FormatId(user));
        }

        command.Parameters.AddWithValue("$limit", Math.Max(0, limit));
        command.Parameters.AddWithValue("$start", Math.Max(0, startIndex));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadFailedSync(reader));
        }

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<FailedSync?> GetFailedSyncAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = FailedSyncColumns + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadFailedSync(reader) : null;
    }

    /// <inheritdoc />
    public async Task<int> RemoveFailedSyncsAsync(Guid? userId, long? id, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            var conditions = new List<string>(2);
            if (userId is { } user)
            {
                conditions.Add("user_id = $user");
                command.Parameters.AddWithValue("$user", FormatId(user));
            }

            if (id is { } one)
            {
                conditions.Add("id = $id");
                command.Parameters.AddWithValue("$id", one);
            }

            command.CommandText = "DELETE FROM failed_syncs" + (conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions)) + ";";
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private const string FailedSyncColumns = "SELECT id, item_id, name, series_name, user_id, status, message, source, note, finished_utc, language FROM failed_syncs";

    private static FailedSync ReadFailedSync(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        Guid.ParseExact(reader.GetString(1), "N"),
        reader.GetString(2),
        reader.GetString(3),
        Guid.ParseExact(reader.GetString(4), "N"),
        ParseStatus(reader.GetString(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        ParseDate(reader.GetString(9)),
        reader.GetString(10));

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
            Language = reader.GetString(11),
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

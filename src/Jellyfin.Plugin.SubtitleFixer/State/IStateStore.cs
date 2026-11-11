namespace Jellyfin.Plugin.SubtitleFixer.State;

/// <summary>
/// Durable per-episode sync state. One writer; reads are batched per page of ids.
/// Hides SQLite so a JSONL store can replace it (D17).
/// </summary>
public interface IStateStore
{
    /// <summary>Gets the file the store writes to.</summary>
    string Location { get; }

    /// <summary>Creates the store and applies migrations.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Gets the records for the given items; missing items are absent from the result.</summary>
    /// <param name="itemIds">Item ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Records by item id.</returns>
    Task<IReadOnlyDictionary<Guid, SyncRecord>> GetAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    /// <summary>Inserts or replaces a record.</summary>
    /// <param name="record">Record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task UpsertAsync(SyncRecord record, CancellationToken cancellationToken);

    /// <summary>Removes the record of an item (used by startup recovery: "mark not synced").</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task DeleteAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Journals a backup before the <c>.bak</c> is created, so startup recovery knows exactly which files to restore
    /// without scanning the library (D23).
    /// </summary>
    /// <param name="entry">Journal entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task AddInFlightAsync(InFlightBackup entry, CancellationToken cancellationToken);

    /// <summary>Removes the journal entry once the backup is gone (deleted after success, or restored).</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task RemoveInFlightAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>Gets every journal entry (normally zero or one).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Entries.</returns>
    Task<IReadOnlyList<InFlightBackup>> GetInFlightAsync(CancellationToken cancellationToken);

    /// <summary>Reads a value from the key/value meta table.</summary>
    /// <param name="key">Key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The value, or null.</returns>
    Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken);

    /// <summary>Writes a value to the key/value meta table.</summary>
    /// <param name="key">Key.</param>
    /// <param name="value">Value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetMetaAsync(string key, string value, CancellationToken cancellationToken);
}

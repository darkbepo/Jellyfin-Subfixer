using System.Collections.Concurrent;
using System.Security.Cryptography;
using Jellyfin.Plugin.SubtitleFixer.Configuration;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Bazarr;

/// <summary>Error codes of the Bazarr endpoints (API.md, "Bazarr").</summary>
public static class BazarrErrorCodes
{
    /// <summary>Bazarr is off, not set up, unreachable, or rejected the key.</summary>
    public const string Unavailable = "bazarr_unavailable";

    /// <summary>Bazarr doesn't know the item.</summary>
    public const string NotFound = "bazarr_not_found";

    /// <summary>The item already has an accepted English sidecar (D28).</summary>
    public const string HasSubtitle = "has_subtitle";

    /// <summary>The item has no English audio, so a downloaded subtitle could never be re-timed.</summary>
    public const string NoEnglishAudio = "no_english_audio";

    /// <summary>Jellyfin has no video file for the item.</summary>
    public const string NoVideoFile = "no_video_file";

    /// <summary>Too many searches; try again in a few seconds.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>Bazarr's search answered with an error of its own.</summary>
    public const string SearchFailed = "search_failed";

    /// <summary>The search result handle has expired or belongs to someone else.</summary>
    public const string ResultExpired = "result_expired";

    /// <summary>Another download for the same item is running.</summary>
    public const string DownloadInProgress = "download_in_progress";

    /// <summary>Bazarr refused the download, or produced no usable subtitle.</summary>
    public const string DownloadFailed = "download_failed";

    /// <summary>Unknown or inaccessible item.</summary>
    public const string ItemNotFound = "not_found";
}

/// <summary>One row of the Find subtitles dialog. Never carries Bazarr's opaque handle.</summary>
/// <param name="Id">Our short-lived handle for this result (phase 2 sends it back to download).</param>
/// <param name="Score">Match score in percent.</param>
/// <param name="Language"><c>EN</c>, or <c>EN:HI</c> for hearing-impaired.</param>
/// <param name="HearingImpaired">Hearing-impaired subtitle.</param>
/// <param name="Provider">Provider.</param>
/// <param name="Releases">Release names.</param>
/// <param name="Uploader">Uploader, or null.</param>
/// <param name="Matches">Properties that match the file.</param>
/// <param name="DontMatches">Properties that don't.</param>
public sealed record BazarrResultDto(
    string Id,
    int Score,
    string Language,
    bool HearingImpaired,
    string Provider,
    IReadOnlyList<string> Releases,
    string? Uploader,
    IReadOnlyList<string> Matches,
    IReadOnlyList<string> DontMatches);

/// <summary><c>GET /Bazarr/Search</c> response.</summary>
/// <param name="ItemId">Jellyfin item.</param>
/// <param name="Kind"><c>movie</c> or <c>episode</c>.</param>
/// <param name="Resource">Video path Bazarr matched (the dialog's Resource line).</param>
/// <param name="MatchedBy"><c>path</c> or <c>ids</c>.</param>
/// <param name="Results">English, non-forced results, best score first.</param>
/// <param name="Hidden">Results left out because they're another language or forced.</param>
public sealed record BazarrSearchDto(Guid ItemId, string Kind, string? Resource, string MatchedBy, IReadOnlyList<BazarrResultDto> Results, int Hidden);

/// <summary>A search error for the controller: HTTP status, code and message.</summary>
/// <param name="Status">HTTP status.</param>
/// <param name="Code">Error code.</param>
/// <param name="Message">Message for people.</param>
/// <param name="RetryAfterSeconds">For <c>rate_limited</c>: seconds to wait.</param>
public sealed record BazarrError(int Status, string Code, string Message, int? RetryAfterSeconds = null);

/// <summary>A search result kept server-side behind <see cref="BazarrResultDto.Id"/> (phase 2 downloads it).</summary>
/// <param name="UserId">Who searched.</param>
/// <param name="ItemId">Jellyfin item.</param>
/// <param name="Target">Bazarr's ids for the item.</param>
/// <param name="Result">Bazarr's result, including its opaque handle.</param>
/// <param name="Expires">When the handle stops working.</param>
public sealed record StoredBazarrResult(Guid UserId, Guid ItemId, BazarrTarget Target, BazarrSearchResult Result, DateTimeOffset Expires);

/// <summary>Keeps search results behind short random handles for about ten minutes (BAZARR-PLAN.md, API).</summary>
public sealed class BazarrResultStore
{
    /// <summary>How long a handle works.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Most handles kept; the oldest go first.</summary>
    public const int MaxEntries = 5000;

    private readonly ConcurrentDictionary<string, StoredBazarrResult> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="BazarrResultStore"/> class.
    /// </summary>
    /// <param name="time">Clock; the system clock when null.</param>
    public BazarrResultStore(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Stores a result and returns its new handle.</summary>
    /// <param name="userId">Who searched.</param>
    /// <param name="itemId">Item.</param>
    /// <param name="target">Bazarr's ids.</param>
    /// <param name="result">Bazarr's result.</param>
    /// <returns>The handle.</returns>
    public string Add(Guid userId, Guid itemId, BazarrTarget target, BazarrSearchResult result)
    {
        Prune();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        _entries[id] = new StoredBazarrResult(userId, itemId, target, result, _time.GetUtcNow() + Lifetime);
        return id;
    }

    /// <summary>Gets a stored result if it hasn't expired and belongs to this user and item.</summary>
    /// <param name="id">Handle.</param>
    /// <param name="userId">User.</param>
    /// <param name="itemId">Item.</param>
    /// <returns>The result, or null.</returns>
    public StoredBazarrResult? Get(string? id, Guid userId, Guid itemId)
    {
        if (id is null || !_entries.TryGetValue(id, out var entry))
        {
            return null;
        }

        if (entry.Expires <= _time.GetUtcNow())
        {
            _entries.TryRemove(id, out _);
            return null;
        }

        return entry.UserId == userId && entry.ItemId == itemId ? entry : null;
    }

    private void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var pair in _entries)
        {
            if (pair.Value.Expires <= now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }

        if (_entries.Count >= MaxEntries)
        {
            foreach (var pair in _entries.OrderBy(p => p.Value.Expires).Take(_entries.Count - MaxEntries + 1).ToList())
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }
}

/// <summary>At most one search per user every few seconds, and one at a time per user (BAZARR-PLAN.md rule 6).</summary>
public sealed class BazarrRateLimiter
{
    /// <summary>Shortest gap between two searches by one user.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _last = new();
    private readonly ConcurrentDictionary<Guid, byte> _running = new();
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="BazarrRateLimiter"/> class.
    /// </summary>
    /// <param name="time">Clock; the system clock when null.</param>
    public BazarrRateLimiter(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Takes a slot for a search, or says how long to wait.</summary>
    /// <param name="userId">User.</param>
    /// <param name="retryAfter">Wait when refused.</param>
    /// <returns>A slot to dispose when the search ends, or null when refused.</returns>
    public IDisposable? TryStart(Guid userId, out TimeSpan retryAfter)
    {
        var now = _time.GetUtcNow();
        retryAfter = TimeSpan.Zero;
        if (!_running.TryAdd(userId, 0))
        {
            retryAfter = MinInterval;
            return null;
        }

        if (_last.TryGetValue(userId, out var last) && now - last < MinInterval)
        {
            _running.TryRemove(userId, out _);
            retryAfter = MinInterval - (now - last);
            return null;
        }

        _last[userId] = now;
        return new Slot(() => _running.TryRemove(userId, out _));
    }

    private sealed class Slot(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

/// <summary>
/// Find subtitles (BAZARR-PLAN.md phase 1): checks the item, the D28 rule and the rate limit, maps the item to Bazarr,
/// runs Bazarr's manual search and keeps the results behind our own handles.
/// </summary>
public sealed class BazarrSearchService
{
    private readonly ILibraryFacade _library;
    private readonly IBazarrLookupSource _lookups;
    private readonly BazarrItemResolver _resolver;
    private readonly IBazarrClient _client;
    private readonly BazarrResultStore _store;
    private readonly BazarrRateLimiter _limiter;
    private readonly ILogger<BazarrSearchService> _logger;
    private readonly Func<PluginConfiguration?> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="BazarrSearchService"/> class.
    /// </summary>
    /// <param name="library">Library facade.</param>
    /// <param name="lookups">Lookup source.</param>
    /// <param name="resolver">Item resolver.</param>
    /// <param name="client">Bazarr client.</param>
    /// <param name="store">Result store.</param>
    /// <param name="limiter">Rate limiter.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="config">Settings; the plugin's when null.</param>
    public BazarrSearchService(
        ILibraryFacade library,
        IBazarrLookupSource lookups,
        BazarrItemResolver resolver,
        IBazarrClient client,
        BazarrResultStore store,
        BazarrRateLimiter limiter,
        ILogger<BazarrSearchService> logger,
        Func<PluginConfiguration?>? config = null)
    {
        _library = library;
        _lookups = lookups;
        _resolver = resolver;
        _client = client;
        _store = store;
        _limiter = limiter;
        _logger = logger;
        _config = config ?? (() => Plugin.Instance?.Configuration);
    }

    /// <summary>Gets a value indicating whether Bazarr search is switched on and has an address and key.</summary>
    public bool Enabled => Connection(out _) is not null;

    /// <summary>Searches Bazarr for English subtitles for one episode or movie.</summary>
    /// <param name="userId">Caller; <c>AllowedUsers</c> was already checked.</param>
    /// <param name="itemId">Episode or movie.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The results, or an error.</returns>
    public async Task<(BazarrSearchDto? Result, BazarrError? Error)> SearchAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        if (Connection(out var mapper) is not { } connection)
        {
            return (null, new BazarrError(StatusCodes.Status503ServiceUnavailable, BazarrErrorCodes.Unavailable, "Bazarr search isn't set up. Ask an administrator to turn it on in the plugin settings."));
        }

        // Access first: an item the caller can't see is "not found", before anything else is said about it.
        var video = _library.GetVideo(userId, itemId);
        if (video is null)
        {
            return (null, new BazarrError(StatusCodes.Status404NotFound, BazarrErrorCodes.ItemNotFound, "That item was not found or you cannot access it."));
        }

        // Record-free evaluation: the rule is about the files, not about earlier syncs.
        var check = EligibilityService.Evaluate(video, null);
        var refusal = check.Reason switch
        {
            IneligibleReason.None => new BazarrError(StatusCodes.Status409Conflict, BazarrErrorCodes.HasSubtitle, "This item already has an English subtitle. Re-time it instead."),
            IneligibleReason.NoVideoFile => new BazarrError(StatusCodes.Status409Conflict, BazarrErrorCodes.NoVideoFile, "Jellyfin has no video file for this item."),
            IneligibleReason.NoEnglishAudio => new BazarrError(StatusCodes.Status409Conflict, BazarrErrorCodes.NoEnglishAudio, "This item has no English audio, so an English subtitle can't be re-timed to it."),
            _ => null,
        };
        if (refusal is not null)
        {
            return (null, refusal);
        }

        var lookup = _lookups.GetLookup(userId, video);
        if (lookup is null)
        {
            return (null, new BazarrError(StatusCodes.Status409Conflict, BazarrErrorCodes.NoVideoFile, "Jellyfin has no video file for this item."));
        }

        using var slot = _limiter.TryStart(userId, out var wait);
        if (slot is null)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            return (null, new BazarrError(StatusCodes.Status429TooManyRequests, BazarrErrorCodes.RateLimited, $"Wait {seconds} s before the next search.", seconds));
        }

        var resolved = await _resolver.ResolveAsync(connection, mapper, lookup, cancellationToken).ConfigureAwait(false);
        if (!resolved.Ok)
        {
            return (null, FromFailure(resolved.Failure!.Value, resolved.Message!));
        }

        if (resolved.Value is not { } target)
        {
            var what = lookup.IsMovie ? "movie" : "episode";
            return (null, new BazarrError(StatusCodes.Status404NotFound, BazarrErrorCodes.NotFound, $"Bazarr doesn't know this {what}. Check that it's in {(lookup.IsMovie ? "Radarr" : "Sonarr")} and that the path mappings are right."));
        }

        var search = target.IsMovie
            ? await _client.SearchMovieAsync(connection, target.RadarrId!.Value, cancellationToken).ConfigureAwait(false)
            : await _client.SearchEpisodeAsync(connection, target.SonarrEpisodeId!.Value, cancellationToken).ConfigureAwait(false);
        if (!search.Ok)
        {
            return (null, FromFailure(search.Failure!.Value, search.Message!));
        }

        var all = search.Value!;
        var shown = all
            .Where(r => IsEnglish(r.Language) && !r.Forced)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.HearingImpaired)
            .Select(r => new BazarrResultDto(
                _store.Add(userId, itemId, target, r),
                r.Score,
                r.HearingImpaired ? "EN:HI" : "EN",
                r.HearingImpaired,
                r.Provider,
                r.Releases,
                string.IsNullOrWhiteSpace(r.Uploader) ? null : r.Uploader,
                r.Matches,
                r.DontMatches))
            .ToList();

        _logger.LogInformation("Bazarr search for {ItemId}: {Shown} of {Total} results shown (matched by {MatchedBy})", itemId, shown.Count, all.Count, target.MatchedBy);
        return (new BazarrSearchDto(itemId, target.IsMovie ? "movie" : "episode", target.Path, target.MatchedBy, shown, all.Count - shown.Count), null);
    }

    /// <summary>Whether a Bazarr language code is English (<c>en</c>, <c>eng</c>, or a regional <c>en-…</c>).</summary>
    /// <param name="code">Language code.</param>
    /// <returns>True for English.</returns>
    internal static bool IsEnglish(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var c = code.Trim();
        return c.Equals("en", StringComparison.OrdinalIgnoreCase)
            || c.Equals("eng", StringComparison.OrdinalIgnoreCase)
            || c.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
            || c.StartsWith("en:", StringComparison.OrdinalIgnoreCase);
    }

    private static BazarrError FromFailure(BazarrFailure failure, string message) => failure switch
    {
        BazarrFailure.NotFound => new BazarrError(StatusCodes.Status404NotFound, BazarrErrorCodes.NotFound, "Bazarr doesn't know this item."),
        BazarrFailure.Failed => new BazarrError(StatusCodes.Status502BadGateway, BazarrErrorCodes.SearchFailed, message),
        _ => new BazarrError(StatusCodes.Status503ServiceUnavailable, BazarrErrorCodes.Unavailable, message),
    };

    private BazarrConnection? Connection(out BazarrPathMapper mapper)
    {
        var config = _config();
        mapper = BazarrPathMapper.FromSetting(config?.BazarrPathMappings);
        return config is { BazarrEnabled: true } && BazarrConnection.TryCreate(config.BazarrUrl, config.BazarrApiKey, out var c, out _) ? c : null;
    }
}

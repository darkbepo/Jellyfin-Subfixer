using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Bazarr;

/// <summary>What Jellyfin knows about an item that Bazarr can be matched on.</summary>
/// <param name="IsMovie">Movie (true) or episode (false).</param>
/// <param name="VideoPath">Video path as Jellyfin sees it.</param>
/// <param name="ImdbId">Movie's IMDb id, or the series' for an episode.</param>
/// <param name="TvdbId">Series' TVDB id, for episodes.</param>
/// <param name="SeriesPath">Series folder as Jellyfin sees it, for episodes.</param>
/// <param name="Season">Season number, for episodes.</param>
/// <param name="Episode">Episode number, for episodes.</param>
public sealed record BazarrLookup(bool IsMovie, string VideoPath, string? ImdbId, int? TvdbId, string? SeriesPath, int? Season, int? Episode);

/// <summary>An item as Bazarr knows it.</summary>
/// <param name="IsMovie">Movie (true) or episode (false).</param>
/// <param name="RadarrId">Movie id.</param>
/// <param name="SonarrSeriesId">Series id, for episodes.</param>
/// <param name="SonarrEpisodeId">Episode id.</param>
/// <param name="Path">Video path as Bazarr reports it: the dialog's Resource line.</param>
/// <param name="MatchedBy"><c>path</c> or <c>ids</c>.</param>
public sealed record BazarrTarget(bool IsMovie, int? RadarrId, int? SonarrSeriesId, int? SonarrEpisodeId, string? Path, string MatchedBy);

/// <summary>
/// Maps a Jellyfin episode or movie to Bazarr's Radarr or Sonarr ids (BAZARR-PLAN.md, "Mapping a Jellyfin item to
/// Bazarr"): by path first (after the admin's path mappings), then by IMDb/TVDB ids plus season and episode numbers.
/// Bazarr's movie and series lists are read in pages and kept for a few minutes; episodes are read per series.
/// A miss on a cached list reads it again once, so an item added to Bazarr a minute ago is still found.
/// </summary>
public sealed class BazarrItemResolver
{
    /// <summary>How long a cached Bazarr list is used before it's read again.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Rows per list request.</summary>
    public const int PageSize = 250;

    /// <summary>Most pages read for one list, so a broken <c>total</c> can't loop forever (250,000 rows).</summary>
    public const int MaxPages = 1000;

    /// <summary>Most series whose episode lists are kept at once.</summary>
    public const int MaxCachedSeries = 200;

    private readonly IBazarrClient _client;
    private readonly ILogger<BazarrItemResolver> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, Cached<IReadOnlyList<BazarrEpisode>>> _episodes = new();
    private Uri? _cacheFor;
    private Cached<IReadOnlyList<BazarrMovie>>? _movies;
    private Cached<IReadOnlyList<BazarrSeries>>? _series;

    /// <summary>
    /// Initializes a new instance of the <see cref="BazarrItemResolver"/> class.
    /// </summary>
    /// <param name="client">Bazarr client.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="time">Clock; the system clock when null.</param>
    public BazarrItemResolver(IBazarrClient client, ILogger<BazarrItemResolver> logger, TimeProvider? time = null)
    {
        _client = client;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Finds the item in Bazarr.</summary>
    /// <param name="connection">Where to reach Bazarr.</param>
    /// <param name="mapper">Path mappings.</param>
    /// <param name="lookup">What Jellyfin knows about the item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The target, null when Bazarr doesn't know the item, or a failure when Bazarr couldn't be asked.</returns>
    public async Task<BazarrResult<BazarrTarget?>> ResolveAsync(BazarrConnection connection, BazarrPathMapper mapper, BazarrLookup lookup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(lookup);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A different Bazarr (the admin changed the address) has different ids.
            if (_cacheFor != connection.BaseUri)
            {
                _movies = null;
                _series = null;
                _episodes.Clear();
                _cacheFor = connection.BaseUri;
            }

            return lookup.IsMovie
                ? await ResolveMovieAsync(connection, mapper, lookup, cancellationToken).ConfigureAwait(false)
                : await ResolveEpisodeAsync(connection, mapper, lookup, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool SameImdb(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static async Task<BazarrResult<IReadOnlyList<T>>> ReadAllAsync<T>(
        Func<int, int, CancellationToken, Task<BazarrResult<BazarrPage<T>>>> page,
        CancellationToken cancellationToken)
    {
        var all = new List<T>();
        for (var i = 0; i < MaxPages; i++)
        {
            var result = await page(all.Count, PageSize, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                return BazarrResult<IReadOnlyList<T>>.Fail(result.Failure!.Value, result.Message!);
            }

            all.AddRange(result.Value!.Items);
            if (result.Value.Items.Count == 0 || all.Count >= result.Value.Total)
            {
                break;
            }
        }

        return BazarrResult<IReadOnlyList<T>>.Success(all);
    }

    private async Task<BazarrResult<BazarrTarget?>> ResolveMovieAsync(BazarrConnection connection, BazarrPathMapper mapper, BazarrLookup lookup, CancellationToken cancellationToken)
    {
        var bazarrPath = mapper.ToBazarr(lookup.VideoPath);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (movies, fromCache) = await GetMoviesAsync(connection, refresh: attempt > 0, cancellationToken).ConfigureAwait(false);
            if (!movies.Ok)
            {
                return BazarrResult<BazarrTarget?>.Fail(movies.Failure!.Value, movies.Message!);
            }

            var byPath = movies.Value!.FirstOrDefault(m => BazarrPathMapper.SamePath(m.Path, bazarrPath));
            if (byPath is not null)
            {
                return BazarrResult<BazarrTarget?>.Success(new BazarrTarget(true, byPath.RadarrId, null, null, byPath.Path, "path"));
            }

            var byId = movies.Value!.Where(m => SameImdb(lookup.ImdbId, m.ImdbId)).ToList();
            if (byId.Count == 1)
            {
                return BazarrResult<BazarrTarget?>.Success(new BazarrTarget(true, byId[0].RadarrId, null, null, byId[0].Path, "ids"));
            }

            if (!fromCache)
            {
                break;
            }
        }

        return BazarrResult<BazarrTarget?>.Success(null);
    }

    private async Task<BazarrResult<BazarrTarget?>> ResolveEpisodeAsync(BazarrConnection connection, BazarrPathMapper mapper, BazarrLookup lookup, CancellationToken cancellationToken)
    {
        var bazarrPath = mapper.ToBazarr(lookup.VideoPath);
        var bazarrSeriesPath = lookup.SeriesPath is null ? null : mapper.ToBazarr(lookup.SeriesPath);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var refresh = attempt > 0;
            var (all, anyFromCache) = await GetSeriesAsync(connection, refresh, cancellationToken).ConfigureAwait(false);
            if (!all.Ok)
            {
                return BazarrResult<BazarrTarget?>.Fail(all.Failure!.Value, all.Message!);
            }

            // Path match first; a few id matches at most (one show can exist twice in Sonarr).
            var candidates = all.Value!
                .Where(s => BazarrPathMapper.SamePath(s.Path, bazarrSeriesPath)
                    || (lookup.TvdbId is { } tvdb && s.TvdbId == tvdb)
                    || SameImdb(lookup.ImdbId, s.ImdbId))
                .OrderByDescending(s => BazarrPathMapper.SamePath(s.Path, bazarrSeriesPath))
                .Take(3)
                .ToList();

            foreach (var series in candidates)
            {
                var (episodes, episodesFromCache) = await GetEpisodesAsync(connection, series.SonarrSeriesId, refresh, cancellationToken).ConfigureAwait(false);
                anyFromCache |= episodesFromCache;
                if (!episodes.Ok)
                {
                    if (episodes.Failure == BazarrFailure.NotFound)
                    {
                        continue;
                    }

                    return BazarrResult<BazarrTarget?>.Fail(episodes.Failure!.Value, episodes.Message!);
                }

                var byPath = episodes.Value!.FirstOrDefault(e => BazarrPathMapper.SamePath(e.Path, bazarrPath));
                if (byPath is not null)
                {
                    return BazarrResult<BazarrTarget?>.Success(new BazarrTarget(false, null, series.SonarrSeriesId, byPath.SonarrEpisodeId, byPath.Path, "path"));
                }

                if (lookup.Season is { } season && lookup.Episode is { } number)
                {
                    var byNumber = episodes.Value!.Where(e => e.Season == season && e.Episode == number).ToList();
                    if (byNumber.Count == 1)
                    {
                        var e = byNumber[0];
                        return BazarrResult<BazarrTarget?>.Success(new BazarrTarget(false, null, series.SonarrSeriesId, e.SonarrEpisodeId, e.Path, "ids"));
                    }
                }
            }

            if (!anyFromCache)
            {
                break;
            }
        }

        return BazarrResult<BazarrTarget?>.Success(null);
    }

    private async Task<(BazarrResult<IReadOnlyList<BazarrMovie>> Result, bool FromCache)> GetMoviesAsync(BazarrConnection connection, bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && _movies is { } cached && !cached.Expired(_time))
        {
            return (BazarrResult<IReadOnlyList<BazarrMovie>>.Success(cached.Value), true);
        }

        var read = await ReadAllAsync((start, length, ct) => _client.GetMoviesAsync(connection, start, length, ct), cancellationToken).ConfigureAwait(false);
        if (read.Ok)
        {
            _movies = new Cached<IReadOnlyList<BazarrMovie>>(read.Value!, _time.GetUtcNow());
            _logger.LogDebug("Read {Count} movies from Bazarr", read.Value!.Count);
        }

        return (read, false);
    }

    private async Task<(BazarrResult<IReadOnlyList<BazarrSeries>> Result, bool FromCache)> GetSeriesAsync(BazarrConnection connection, bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && _series is { } cached && !cached.Expired(_time))
        {
            return (BazarrResult<IReadOnlyList<BazarrSeries>>.Success(cached.Value), true);
        }

        var read = await ReadAllAsync((start, length, ct) => _client.GetSeriesAsync(connection, start, length, ct), cancellationToken).ConfigureAwait(false);
        if (read.Ok)
        {
            _series = new Cached<IReadOnlyList<BazarrSeries>>(read.Value!, _time.GetUtcNow());
            _logger.LogDebug("Read {Count} series from Bazarr", read.Value!.Count);
        }

        return (read, false);
    }

    private async Task<(BazarrResult<IReadOnlyList<BazarrEpisode>> Result, bool FromCache)> GetEpisodesAsync(BazarrConnection connection, int seriesId, bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && _episodes.TryGetValue(seriesId, out var cached) && !cached.Expired(_time))
        {
            return (BazarrResult<IReadOnlyList<BazarrEpisode>>.Success(cached.Value), true);
        }

        var read = await _client.GetEpisodesAsync(connection, seriesId, cancellationToken).ConfigureAwait(false);
        if (read.Ok)
        {
            if (_episodes.Count >= MaxCachedSeries)
            {
                _episodes.Clear();
            }

            _episodes[seriesId] = new Cached<IReadOnlyList<BazarrEpisode>>(read.Value!, _time.GetUtcNow());
        }

        return (read, false);
    }

    private sealed record Cached<T>(T Value, DateTimeOffset ReadAt)
    {
        public bool Expired(TimeProvider time) => time.GetUtcNow() - ReadAt > CacheLifetime;
    }
}

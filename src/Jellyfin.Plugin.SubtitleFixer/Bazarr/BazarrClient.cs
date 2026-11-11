using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Bazarr;

/// <summary>Where to reach Bazarr. Built from plugin settings, or from the settings form for the Test button.</summary>
/// <param name="BaseUri">Bazarr's base address, ending in <c>/</c>.</param>
/// <param name="ApiKey">API key. Never logged or returned.</param>
public sealed record BazarrConnection(Uri BaseUri, string ApiKey)
{
    /// <summary>Builds a connection, or explains what's wrong with the inputs.</summary>
    /// <param name="url">Base address as typed.</param>
    /// <param name="apiKey">API key.</param>
    /// <param name="connection">The connection when valid.</param>
    /// <param name="error">Why it's invalid.</param>
    /// <returns>Whether the inputs are valid.</returns>
    public static bool TryCreate(string? url, string? apiKey, out BazarrConnection? connection, out string? error)
    {
        connection = null;
        error = null;
        var trimmed = url?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            error = "Set the Bazarr address.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "The Bazarr address must be a plain http:// or https:// address, for example http://localhost:6767.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            error = "Set the Bazarr API key.";
            return false;
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        connection = new BazarrConnection(builder.Uri, apiKey.Trim());
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => BaseUri.ToString();
}

/// <summary>Result of <c>GET /api/system/status</c>.</summary>
/// <param name="Ok">Whether Bazarr answered and accepted the key.</param>
/// <param name="Version">Bazarr version, when known.</param>
/// <param name="Message">What to tell the admin. Never contains the key.</param>
public sealed record BazarrStatusResult(bool Ok, string? Version, string Message);

/// <summary>Talks to the owner's Bazarr. Every call is server-side; the browser never sees the key (BAZARR-PLAN.md rule 1).</summary>
public interface IBazarrClient
{
    /// <summary>Asks Bazarr for its status: the cheap call behind the Test button.</summary>
    /// <param name="connection">Where to reach Bazarr.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result. Never throws for network or HTTP failures.</returns>
    Task<BazarrStatusResult> GetStatusAsync(BazarrConnection connection, CancellationToken cancellationToken);

    /// <summary>
    /// Reads Bazarr's own "synchronize subtitles" setting (<c>subsync.use_subsync</c>). If it's on, downloads get re-timed
    /// twice (BAZARR-PLAN.md, owner decision 3). The settings response also holds every provider password, so only that one value is read and
    /// nothing else is kept, logged or returned.
    /// </summary>
    /// <param name="connection">Where to reach Bazarr.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The setting, or null when it couldn't be read. Never throws for network or HTTP failures.</returns>
    Task<bool?> GetUseSubsyncAsync(BazarrConnection connection, CancellationToken cancellationToken);

    /// <summary>Gets a page of Bazarr's movies (<c>GET /api/movies?start=&amp;length=</c>).</summary>
    /// <param name="connection">Where to reach Bazarr.</param>
    /// <param name="start">First row.</param>
    /// <param name="length">Rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page.</returns>
    Task<BazarrResult<BazarrPage<BazarrMovie>>> GetMoviesAsync(BazarrConnection connection, int start, int length, CancellationToken cancellationToken);

    /// <summary>Gets a page of Bazarr's series (<c>GET /api/series?start=&amp;length=</c>).</summary>
    /// <param name="connection">Where to reach Bazarr.</param>
    /// <param name="start">First row.</param>
    /// <param name="length">Rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page.</returns>
    Task<BazarrResult<BazarrPage<BazarrSeries>>> GetSeriesAsync(BazarrConnection connection, int start, int length, CancellationToken cancellationToken);

    /// <summary>Gets all episodes of one series (<c>GET /api/episodes?seriesid[]=</c>).</summary>
    /// <param name="connection">Where to reach Bazarr.</param>
    /// <param name="sonarrSeriesId">Sonarr series id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The episodes.</returns>
    Task<BazarrResult<IReadOnlyList<BazarrEpisode>>> GetEpisodesAsync(BazarrConnection connection, int sonarrSeriesId, CancellationToken cancellationToken);

    /// <summary>Runs Bazarr's manual search for a movie (<c>GET /api/providers/movies?radarrid=</c>).</summary>
    /// <param name="connection">Where to reach Bazarr.</param>
    /// <param name="radarrId">Radarr id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The results.</returns>
    Task<BazarrResult<IReadOnlyList<BazarrSearchResult>>> SearchMovieAsync(BazarrConnection connection, int radarrId, CancellationToken cancellationToken);

    /// <summary>Runs Bazarr's manual search for an episode (<c>GET /api/providers/episodes?episodeid=</c>).</summary>
    /// <param name="connection">Where to reach Bazarr.</param>
    /// <param name="sonarrEpisodeId">Sonarr episode id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The results.</returns>
    Task<BazarrResult<IReadOnlyList<BazarrSearchResult>>> SearchEpisodeAsync(BazarrConnection connection, int sonarrEpisodeId, CancellationToken cancellationToken);

    /// <summary>
    /// Asks Bazarr to download one search result (<c>POST /api/providers/movies</c> or <c>/episodes</c>). Bazarr 1.6.2
    /// only queues the download as one of its own jobs and answers <c>204</c> at once, so success here means
    /// "accepted"; the caller watches for the file (BAZARR-API.md).
    /// </summary>
    /// <param name="connection">Where to reach Bazarr.</param>
    /// <param name="target">Bazarr's ids for the item.</param>
    /// <param name="result">The search result, including Bazarr's opaque handle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when Bazarr accepted the request.</returns>
    Task<BazarrResult<bool>> DownloadAsync(BazarrConnection connection, BazarrTarget target, BazarrSearchResult result, CancellationToken cancellationToken);
}

/// <summary>
/// Bazarr HTTP client. Calls and field names are recorded in <c>docs/BAZARR-API.md</c>; only verified calls go here.
/// </summary>
public sealed partial class BazarrClient : IBazarrClient
{
    /// <summary>Name of the client taken from <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientName = "SubtitleFixer.Bazarr";

    /// <summary>Header Bazarr reads the key from (<c>bazarr/api/utils.py</c>, <c>authenticate</c>).</summary>
    public const string ApiKeyHeader = "X-API-KEY";

    /// <summary>Per-call timeout (BAZARR-PLAN.md).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for a manual search, which asks every enabled provider in turn (5.6 s for three providers on the test
    /// Bazarr; slow providers take longer). See D30.
    /// </summary>
    public static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(90);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BazarrClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BazarrClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory (registered by the Jellyfin server).</param>
    /// <param name="logger">Logger.</param>
    public BazarrClient(IHttpClientFactory httpClientFactory, ILogger<BazarrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BazarrStatusResult> GetStatusAsync(BazarrConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            using var request = CreateRequest(HttpMethod.Get, connection, "api/system/status");
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new BazarrStatusResult(false, null, "Bazarr rejected the API key.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var problem = $"{request.RequestUri} answered HTTP {(int)response.StatusCode}.";
                return new BazarrStatusResult(false, null, await ExplainAsync(connection, problem, timeout.Token).ConfigureAwait(false));
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("bazarr_version", out var v)
                    && v.ValueKind == JsonValueKind.String)
                {
                    var version = v.GetString();
                    return new BazarrStatusResult(true, version, $"Connected to Bazarr {version}.");
                }

                var problem = $"{request.RequestUri} answered, but not like Bazarr's API.";
                return new BazarrStatusResult(false, null, await ExplainAsync(connection, problem, timeout.Token).ConfigureAwait(false));
            }
        }
        catch (JsonException)
        {
            var problem = $"{connection.BaseUri}api/system/status answered, but not like Bazarr's API.";
            return new BazarrStatusResult(false, null, await ExplainAsync(connection, problem, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BazarrStatusResult(false, null, $"Bazarr did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Could not reach Bazarr at {BaseUri}: {Error}", connection.BaseUri, ex.Message);
            return new BazarrStatusResult(false, null, $"Could not reach Bazarr at {connection.BaseUri}.");
        }
    }

    /// <inheritdoc />
    public async Task<bool?> GetUseSubsyncAsync(BazarrConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            using var request = CreateRequest(HttpMethod.Get, connection, "api/system/settings");
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("subsync", out var subsync)
                    && subsync.ValueKind == JsonValueKind.Object
                    && subsync.TryGetProperty("use_subsync", out var use)
                    && use.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? use.GetBoolean()
                    : null;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Could not read Bazarr settings at {BaseUri}: {Error}", connection.BaseUri, ex.Message);
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task<BazarrResult<BazarrPage<BazarrMovie>>> GetMoviesAsync(BazarrConnection connection, int start, int length, CancellationToken cancellationToken) =>
        GetJsonAsync(connection, Query("api/movies", ("start", start), ("length", length)), Timeout, root => ReadPage(root, row => new BazarrMovie(
            Int(row, "radarrId") ?? throw new JsonException("radarrId"),
            Str(row, "path"),
            Str(row, "imdbId"),
            Str(row, "title"))), cancellationToken);

    /// <inheritdoc />
    public Task<BazarrResult<BazarrPage<BazarrSeries>>> GetSeriesAsync(BazarrConnection connection, int start, int length, CancellationToken cancellationToken) =>
        GetJsonAsync(connection, Query("api/series", ("start", start), ("length", length)), Timeout, root => ReadPage(root, row => new BazarrSeries(
            Int(row, "sonarrSeriesId") ?? throw new JsonException("sonarrSeriesId"),
            Str(row, "path"),
            Int(row, "tvdbId"),
            Str(row, "imdbId"),
            Str(row, "title"))), cancellationToken);

    /// <inheritdoc />
    public Task<BazarrResult<IReadOnlyList<BazarrEpisode>>> GetEpisodesAsync(BazarrConnection connection, int sonarrSeriesId, CancellationToken cancellationToken) =>
        GetJsonAsync<IReadOnlyList<BazarrEpisode>>(connection, Query("api/episodes", ("seriesid[]", sonarrSeriesId)), Timeout, root => ReadData(root, row => new BazarrEpisode(
            Int(row, "sonarrEpisodeId") ?? throw new JsonException("sonarrEpisodeId"),
            Int(row, "sonarrSeriesId") ?? sonarrSeriesId,
            Int(row, "season"),
            Int(row, "episode"),
            Str(row, "path"))), cancellationToken);

    /// <inheritdoc />
    public Task<BazarrResult<IReadOnlyList<BazarrSearchResult>>> SearchMovieAsync(BazarrConnection connection, int radarrId, CancellationToken cancellationToken) =>
        GetJsonAsync<IReadOnlyList<BazarrSearchResult>>(connection, Query("api/providers/movies", ("radarrid", radarrId)), SearchTimeout, root => ReadData(root, ReadSearchResult), cancellationToken);

    /// <inheritdoc />
    public Task<BazarrResult<IReadOnlyList<BazarrSearchResult>>> SearchEpisodeAsync(BazarrConnection connection, int sonarrEpisodeId, CancellationToken cancellationToken) =>
        GetJsonAsync<IReadOnlyList<BazarrSearchResult>>(connection, Query("api/providers/episodes", ("episodeid", sonarrEpisodeId)), SearchTimeout, root => ReadData(root, ReadSearchResult), cancellationToken);

    /// <inheritdoc />
    public async Task<BazarrResult<bool>> DownloadAsync(BazarrConnection connection, BazarrTarget target, BazarrSearchResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(result);

        // Parameters are query parameters (Swagger, 1.6.2). original_format is always False so Bazarr saves .srt,
        // the only format the fixer re-times (D6); booleans use Bazarr's "True"/"False" strings.
        var args = new List<(string Name, string Value)>();
        if (target.IsMovie)
        {
            args.Add(("radarrid", target.RadarrId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        else
        {
            args.Add(("seriesid", target.SonarrSeriesId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            args.Add(("episodeid", target.SonarrEpisodeId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        args.Add(("hi", result.HearingImpaired ? "True" : "False"));
        args.Add(("forced", result.Forced ? "True" : "False"));
        args.Add(("original_format", "False"));
        args.Add(("provider", result.Provider));
        args.Add(("subtitle", result.Subtitle));
        var path = (target.IsMovie ? "api/providers/movies" : "api/providers/episodes") + "?"
            + string.Join('&', args.Select(a => Uri.EscapeDataString(a.Name) + "=" + Uri.EscapeDataString(a.Value)));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = CreateRequest(HttpMethod.Post, connection, path);
            request.Content = new ByteArrayContent([]);
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return BazarrResult<bool>.Fail(BazarrFailure.Unauthorized, "Bazarr rejected the API key.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return BazarrResult<bool>.Fail(BazarrFailure.NotFound, "Bazarr doesn't know this item.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                return BazarrResult<bool>.Fail(BazarrFailure.Failed, BazarrMessage(text) ?? $"Bazarr answered HTTP {(int)response.StatusCode}.");
            }

            return BazarrResult<bool>.Success(true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BazarrResult<bool>.Fail(BazarrFailure.Unavailable, $"Bazarr did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Could not reach Bazarr at {BaseUri}: {Error}", connection.BaseUri, ex.Message);
            return BazarrResult<bool>.Fail(BazarrFailure.Unavailable, $"Could not reach Bazarr at {connection.BaseUri}.");
        }
    }

    /// <summary>Reads one search row. Booleans arrive as the strings <c>"True"</c>/<c>"False"</c> (verified on 1.6.2).</summary>
    /// <param name="row">Row.</param>
    /// <returns>The result, or null when the row lacks a provider or handle.</returns>
    internal static BazarrSearchResult? ReadSearchResult(JsonElement row)
    {
        var provider = Str(row, "provider");
        var subtitle = Str(row, "subtitle");
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(subtitle))
        {
            return null;
        }

        return new BazarrSearchResult(
            provider,
            subtitle,
            Str(row, "language") ?? string.Empty,
            Bool(row, "hearing_impaired"),
            Bool(row, "forced"),
            Bool(row, "original_format"),
            Int(row, "score") ?? 0,
            Strings(row, "release_info"),
            Str(row, "uploader"),
            Strings(row, "matches"),
            Strings(row, "dont_matches"));
    }

    private static string Query(string path, params (string Name, int Value)[] args) =>
        path + "?" + string.Join('&', args.Select(a => Uri.EscapeDataString(a.Name) + "=" + a.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static BazarrPage<T> ReadPage<T>(JsonElement root, Func<JsonElement, T?> read)
        where T : class
    {
        var items = ReadData(root, read);
        var total = root.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : items.Count;
        return new BazarrPage<T>(items, total);
    }

    private static IReadOnlyList<T> ReadData<T>(JsonElement root, Func<JsonElement, T?> read)
        where T : class
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Expected { data: [...] }.");
        }

        var list = new List<T>(data.GetArrayLength());
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object && read(row) is { } item)
            {
                list.Add(item);
            }
        }

        return list;
    }

    private static string? Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool Bool(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var v))
        {
            return false;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(v.GetString(), "True", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static IReadOnlyList<string> Strings(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
    }

    private async Task<BazarrResult<T>> GetJsonAsync<T>(BazarrConnection connection, string relativePath, TimeSpan timeoutAfter, Func<JsonElement, T> parse, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutAfter);

        try
        {
            using var request = CreateRequest(HttpMethod.Get, connection, relativePath);
            var client = _httpClientFactory.CreateClient(HttpClientName);
            client.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // Our own token enforces the limit.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return BazarrResult<T>.Fail(BazarrFailure.Unauthorized, "Bazarr rejected the API key.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return BazarrResult<T>.Fail(BazarrFailure.NotFound, "Bazarr doesn't know this item.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                return BazarrResult<T>.Fail(BazarrFailure.Failed, BazarrMessage(text) ?? $"Bazarr answered HTTP {(int)response.StatusCode}.");
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
                return BazarrResult<T>.Success(parse(doc.RootElement));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BazarrResult<T>.Fail(BazarrFailure.Unavailable, $"Bazarr did not answer within {timeoutAfter.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Could not reach Bazarr at {BaseUri}: {Error}", connection.BaseUri, ex.Message);
            return BazarrResult<T>.Fail(BazarrFailure.Unavailable, $"Could not reach Bazarr at {connection.BaseUri}.");
        }
        catch (JsonException)
        {
            return BazarrResult<T>.Fail(BazarrFailure.Unavailable, "Bazarr answered with something unexpected. Check the Bazarr address.");
        }
    }

    /// <summary>
    /// Bazarr's error bodies are a JSON string, for example <c>"Movie file not found. Path mapping issue?"</c>
    /// (<c>providers_movies.py</c>). Returns it, trimmed and capped, or null when the body is something else.
    /// </summary>
    /// <param name="body">Response body.</param>
    /// <returns>The message.</returns>
    internal static string? BazarrMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => doc.RootElement.GetString(),
                JsonValueKind.Object when doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String => m.GetString(),
                _ => null,
            };
            text = text?.Trim();
            return string.IsNullOrEmpty(text) ? null : "Bazarr: " + (text.Length > 200 ? text[..200] + "..." : text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Bazarr's web page says where Bazarr lives: it embeds <c>"baseUrl": "/bazarr"</c> (its URL Base setting), and with a
    /// URL Base set, Bazarr 1.6.2 redirects every other path, the API included, to that page (verified locally). So when
    /// the status call fails, look at the page at the typed address and then at the server root, and suggest the address
    /// Bazarr reports. Never logs or returns the page, which also carries Bazarr's API key.
    /// </summary>
    internal static string? SuggestAddress(Uri pageUri, string html)
    {
        var match = BaseUrlPattern().Match(html);
        if (!match.Success)
        {
            return null;
        }

        var baseUrl = match.Groups[1].Value.Trim().Trim('/');
        var root = pageUri.GetLeftPart(UriPartial.Authority);
        return baseUrl.Length == 0 ? root + "/" : root + "/" + baseUrl + "/";
    }

    [GeneratedRegex("window\\.Bazarr\\s*=[^;]*?\"baseUrl\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BaseUrlPattern();

    private async Task<string> ExplainAsync(BazarrConnection connection, string problem, CancellationToken cancellationToken)
    {
        var typed = connection.BaseUri;
        var root = new Uri(typed.GetLeftPart(UriPartial.Authority) + "/");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        foreach (var page in typed == root ? new[] { typed } : new[] { typed, root })
        {
            var (finalUri, html) = await TryGetPageAsync(page, limit.Token).ConfigureAwait(false);
            if (finalUri is null || html is null || SuggestAddress(finalUri, html) is not { } suggestion)
            {
                continue;
            }

            if (!string.Equals(suggestion, typed.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return $"{problem} Bazarr's own page says it is at {suggestion}. Use that address.";
            }

            return $"{problem} The address is Bazarr's, but its API isn't reachable there. If a proxy sits in front of Bazarr, make it pass /api through, or use Bazarr's own address and port.";
        }

        return $"{problem} Nothing at {typed} looks like Bazarr. Check the address and port (Bazarr's default is http://<server>:6767/), and add Bazarr's URL Base if it has one.";
    }

    private async Task<(Uri? FinalUri, string? Html)> TryGetPageAsync(Uri page, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, page);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, null);
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                // The page is small; never read more than 256 KB of whatever answers.
                var buffer = new char[256 * 1024];
                using var reader = new StreamReader(stream);
                var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                return (response.RequestMessage?.RequestUri ?? page, new string(buffer, 0, read));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return (null, null);
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, BazarrConnection connection, string relativePath)
    {
        var request = new HttpRequestMessage(method, new Uri(connection.BaseUri, relativePath));
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, connection.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}

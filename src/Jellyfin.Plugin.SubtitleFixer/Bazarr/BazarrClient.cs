using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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
}

/// <summary>
/// Bazarr HTTP client. Calls and field names are recorded in <c>docs/BAZARR-API.md</c>; only verified calls go here.
/// </summary>
public sealed class BazarrClient : IBazarrClient
{
    /// <summary>Name of the client taken from <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientName = "SubtitleFixer.Bazarr";

    /// <summary>Header Bazarr reads the key from (<c>bazarr/api/utils.py</c>, <c>authenticate</c>).</summary>
    public const string ApiKeyHeader = "X-API-KEY";

    /// <summary>Per-call timeout (BAZARR-PLAN.md).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

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
                return new BazarrStatusResult(false, null, $"Bazarr answered HTTP {(int)response.StatusCode}. Check the address, including any base path.");
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

                return new BazarrStatusResult(false, null, "That address answered, but not like Bazarr. Check the address, including any base path.");
            }
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
        catch (JsonException)
        {
            return new BazarrStatusResult(false, null, "That address answered, but not like Bazarr. Check the address, including any base path.");
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

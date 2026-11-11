using System.Net.Mime;
using System.Text.Json;
using Jellyfin.Plugin.SubtitleFixer.Bazarr;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleFixer.Api;

/// <summary><c>POST /Admin/TestBazarr</c> request. Empty fields fall back to the saved settings.</summary>
/// <param name="Url">Bazarr address from the settings form, or empty.</param>
/// <param name="ApiKey">API key from the settings form, or empty to use the saved one.</param>
/// <param name="PathMappings">Path mappings from the settings form, or null to check the saved ones.</param>
public sealed record TestBazarrRequest(string? Url, string? ApiKey, string? PathMappings);

/// <summary><c>POST /Admin/TestBazarr</c> response. Never carries the API key.</summary>
/// <param name="Ok">Whether Bazarr answered and accepted the key.</param>
/// <param name="Version">Bazarr version.</param>
/// <param name="Message">What to tell the admin.</param>
/// <param name="PathMappingErrors">One message per bad path-mapping line.</param>
public sealed record TestBazarrDto(bool Ok, string? Version, string Message, IReadOnlyList<string> PathMappingErrors);

/// <summary>Bazarr endpoints (BAZARR-PLAN.md, API.md "Bazarr").</summary>
[ApiController]
[Authorize]
[Route("SubtitleFixer")]
public sealed class BazarrController : ControllerBase
{
    private readonly IBazarrClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="BazarrController"/> class.
    /// </summary>
    /// <param name="client">Bazarr client.</param>
    public BazarrController(IBazarrClient client)
    {
        _client = client;
    }

    /// <summary>Checks the Bazarr address and key with a status call. Works whether or not Bazarr is enabled. Admin only.</summary>
    /// <param name="request">Unsaved values from the settings form; empty fields use the saved settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    [HttpPost("Admin/TestBazarr")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> TestBazarr([FromBody] TestBazarrRequest? request, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var mappingErrors = BazarrPathMapper.Parse(request?.PathMappings ?? config?.BazarrPathMappings).Errors;
        if (ResolveTestConnection(request, config?.BazarrUrl, config?.BazarrApiKey, out var connection) is { } error)
        {
            return Json(new TestBazarrDto(false, null, error, mappingErrors));
        }

        var status = await _client.GetStatusAsync(connection!, cancellationToken).ConfigureAwait(false);
        return Json(new TestBazarrDto(status.Ok, status.Version, status.Message, mappingErrors));
    }

    /// <summary>
    /// Picks the address and key to test: form values first, saved settings for empty fields. The saved key is only
    /// ever sent to the saved address, so a typed address cannot be used to send it somewhere else.
    /// </summary>
    /// <param name="request">Form values.</param>
    /// <param name="savedUrl">Saved address.</param>
    /// <param name="savedKey">Saved key.</param>
    /// <param name="connection">The connection to test.</param>
    /// <returns>Null when ready, otherwise what to tell the admin.</returns>
    internal static string? ResolveTestConnection(TestBazarrRequest? request, string? savedUrl, string? savedKey, out BazarrConnection? connection)
    {
        connection = null;
        var url = string.IsNullOrWhiteSpace(request?.Url) ? savedUrl : request.Url;
        var typedKey = !string.IsNullOrWhiteSpace(request?.ApiKey);
        if (!typedKey && !string.IsNullOrWhiteSpace(savedKey) && !SameAddress(url, savedUrl))
        {
            return "Enter the API key to test an address other than the saved one.";
        }

        return BazarrConnection.TryCreate(url, typedKey ? request!.ApiKey : savedKey, out connection, out var error) ? null : error;
    }

    /// <summary>Whether two typed addresses name the same Bazarr, after the same normalization the client applies.</summary>
    /// <param name="a">First address.</param>
    /// <param name="b">Second address.</param>
    /// <returns>True when both are valid and equal.</returns>
    internal static bool SameAddress(string? a, string? b) =>
        BazarrConnection.TryCreate(a, "x", out var ca, out _)
        && BazarrConnection.TryCreate(b, "x", out var cb, out _)
        && Uri.Compare(ca!.BaseUri, cb!.BaseUri, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static ContentResult Json(object value) => new()
    {
        Content = JsonSerializer.Serialize(value, value.GetType(), ApiJson.Options),
        ContentType = MediaTypeNames.Application.Json,
        StatusCode = StatusCodes.Status200OK,
    };
}

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
        var url = string.IsNullOrWhiteSpace(request?.Url) ? config?.BazarrUrl : request.Url;
        var key = string.IsNullOrWhiteSpace(request?.ApiKey) ? config?.BazarrApiKey : request.ApiKey;
        var mappingErrors = BazarrPathMapper.Parse(request?.PathMappings ?? config?.BazarrPathMappings).Errors;

        if (!BazarrConnection.TryCreate(url, key, out var connection, out var error))
        {
            return Json(new TestBazarrDto(false, null, error!, mappingErrors));
        }

        var status = await _client.GetStatusAsync(connection!, cancellationToken).ConfigureAwait(false);
        return Json(new TestBazarrDto(status.Ok, status.Version, status.Message, mappingErrors));
    }

    private static ContentResult Json(object value) => new()
    {
        Content = JsonSerializer.Serialize(value, value.GetType(), ApiJson.Options),
        ContentType = MediaTypeNames.Application.Json,
        StatusCode = StatusCodes.Status200OK,
    };
}

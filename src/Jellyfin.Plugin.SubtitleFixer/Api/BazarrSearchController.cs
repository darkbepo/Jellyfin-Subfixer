using System.Globalization;
using System.Net.Mime;
using Jellyfin.Plugin.SubtitleFixer.Bazarr;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleFixer.Api;

/// <summary><c>GET /Bazarr/Info</c>: whether the page should offer Find subtitles at all.</summary>
/// <param name="Enabled">Bazarr search is on and set up.</param>
public sealed record BazarrInfoDto(bool Enabled);

/// <summary>
/// Find subtitles for any allowed user (BAZARR-PLAN.md owner decision 4: no separate Bazarr permission). The API key
/// never leaves the server; responses carry our own short-lived handles instead of Bazarr's.
/// </summary>
[ApiController]
[Authorize]
[Route("SubtitleFixer/Bazarr")]
public sealed class BazarrSearchController : ControllerBase
{
    private readonly AccessService _access;
    private readonly BazarrSearchService _search;

    /// <summary>
    /// Initializes a new instance of the <see cref="BazarrSearchController"/> class.
    /// </summary>
    /// <param name="access">Access service.</param>
    /// <param name="search">Search service.</param>
    public BazarrSearchController(AccessService access, BazarrSearchService search)
    {
        _access = access;
        _search = search;
    }

    /// <summary>Whether Bazarr search is available.</summary>
    /// <returns>The info.</returns>
    [HttpGet("Info")]
    [Produces(MediaTypeNames.Application.Json)]
    public IActionResult Info()
    {
        if (Deny(out _) is { } denied)
        {
            return denied;
        }

        return ApiResults.Json(new BazarrInfoDto(_search.Enabled));
    }

    /// <summary>Bazarr's manual-search results for one episode or movie with no English subtitle (D28).</summary>
    /// <param name="itemId">Episode or movie.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The results.</returns>
    [HttpGet("Search")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> Search([FromQuery] Guid? itemId, CancellationToken cancellationToken)
    {
        if (Deny(out var caller) is { } denied)
        {
            return denied;
        }

        if (itemId is not { } id || id == Guid.Empty)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "bad_request", "itemId is required.");
        }

        var (result, error) = await _search.SearchAsync(caller.User.Id, id, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            if (error.RetryAfterSeconds is { } seconds)
            {
                Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            }

            return ApiResults.Error(error.Status, error.Code, error.Message);
        }

        return ApiResults.Json(result!);
    }

    private ContentResult? Deny(out Caller caller)
    {
        caller = _access.GetCaller(User)!;
        if (caller is null)
        {
            return ApiResults.Error(StatusCodes.Status401Unauthorized, "unauthorized", "Sign in to Jellyfin with a user account.");
        }

        return caller.Allowed
            ? null
            : ApiResults.Error(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, AccessService.DenialMessage(caller.Denial));
    }
}

using System.Net.Mime;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleFixer.Api;

/// <summary><c>POST /AudioCheck</c> request.</summary>
/// <param name="ItemId">Episode or movie whose audio isn't tagged.</param>
public sealed record AudioCheckRequest(Guid? ItemId);

/// <summary>The Check button (D49): any allowed user can check an item's untagged audio.</summary>
[ApiController]
[Authorize]
[Route("SubtitleFixer")]
public sealed class AudioCheckController : ControllerBase
{
    private readonly AccessService _access;
    private readonly AudioCheckService _checks;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioCheckController"/> class.
    /// </summary>
    /// <param name="access">Access service.</param>
    /// <param name="checks">Audio checks.</param>
    public AudioCheckController(AccessService access, AudioCheckService checks)
    {
        _access = access;
        _checks = checks;
    }

    /// <summary>Checks which language an item's untagged audio is, and remembers it.</summary>
    /// <param name="request">The item.</param>
    /// <param name="cancellationToken">Cancelled when the page goes away; SubSync is stopped.</param>
    /// <returns>What SubSync heard.</returns>
    [HttpPost("AudioCheck")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> Check([FromBody] AudioCheckRequest? request, CancellationToken cancellationToken)
    {
        var caller = _access.GetCaller(User);
        if (caller is null)
        {
            return ApiResults.Error(StatusCodes.Status401Unauthorized, "unauthorized", "Sign in to Jellyfin with a user account.");
        }

        if (!caller.Allowed)
        {
            return ApiResults.Error(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, AccessService.DenialMessage(caller.Denial));
        }

        if (request?.ItemId is not { } id || id == Guid.Empty)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "bad_request", "itemId is required.");
        }

        var (result, error) = await _checks.CheckAsync(caller.User.Id, id, cancellationToken).ConfigureAwait(false);
        return error is not null ? ApiResults.Error(error.Status, error.Code, error.Message) : ApiResults.Json(result!);
    }
}

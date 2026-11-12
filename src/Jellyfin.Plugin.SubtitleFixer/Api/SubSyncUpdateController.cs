using System.Net.Mime;
using Jellyfin.Plugin.SubtitleFixer.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleFixer.Api;

/// <summary>SubSync updates from the settings page (D46, API.md). Admin only.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("SubtitleFixer/Admin/SubSyncUpdate")]
public sealed class SubSyncUpdateController : ControllerBase
{
    private readonly SubSyncUpdateService _updates;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSyncUpdateController"/> class.
    /// </summary>
    /// <param name="updates">Update service.</param>
    public SubSyncUpdateController(SubSyncUpdateService updates)
    {
        _updates = updates;
    }

    /// <summary>Installed and latest SubSync versions, and the state of an update.</summary>
    /// <param name="refresh">Ask GitHub again instead of using the cached answer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status.</returns>
    [HttpGet]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> Get([FromQuery] bool refresh, CancellationToken cancellationToken) =>
        ApiResults.Json(await _updates.GetStatusAsync(refresh, cancellationToken).ConfigureAwait(false));

    /// <summary>Starts installing the latest SubSync release; the page then polls <see cref="Get"/>.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>202 with the status; 200 with the status and why not in <c>message</c> when it didn't start.</returns>
    [HttpPost]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> Start(CancellationToken cancellationToken)
    {
        var refused = await _updates.StartAsync(cancellationToken).ConfigureAwait(false);
        var status = await _updates.GetStatusAsync(false, cancellationToken).ConfigureAwait(false);
        return refused is null
            ? ApiResults.Json(status, StatusCodes.Status202Accepted)
            : ApiResults.Json(status with { Message = refused });
    }
}

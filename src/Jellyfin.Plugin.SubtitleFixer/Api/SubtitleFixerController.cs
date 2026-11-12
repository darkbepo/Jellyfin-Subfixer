using System.Globalization;
using System.Net.Mime;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services;
using Jellyfin.Plugin.SubtitleFixer.State;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleFixer.Api;

/// <summary>
/// The REST API (API.md). Jellyfin rejects unauthenticated calls with 401 before these run; <c>AllowedUsers</c>
/// is checked here. Responses are camelCase JSON.
/// </summary>
[ApiController]
[Authorize]
[Route("SubtitleFixer")]
public sealed class SubtitleFixerController : ControllerBase
{
    private const int MaxPageSize = 200;
    private const string DiagnosticKey = "diagnostic_last_write";

    private readonly AccessService _access;
    private readonly BrowseService _browse;
    private readonly SelectionValidator _validator;
    private readonly IStateStore _state;
    private readonly StartupStatus _startup;
    private readonly SubSyncProbe _probe;
    private readonly JellyfinLibraryFacade _allLibraries;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleFixerController"/> class.
    /// </summary>
    /// <param name="access">Access service.</param>
    /// <param name="browse">Browse service.</param>
    /// <param name="validator">Selection validator.</param>
    /// <param name="state">State store.</param>
    /// <param name="startup">Startup status.</param>
    /// <param name="probe">SubSync probe.</param>
    /// <param name="allLibraries">The library without the "Libraries" setting applied (for the settings page).</param>
    public SubtitleFixerController(
        AccessService access,
        BrowseService browse,
        SelectionValidator validator,
        IStateStore state,
        StartupStatus startup,
        SubSyncProbe probe,
        JellyfinLibraryFacade allLibraries)
    {
        _allLibraries = allLibraries;
        _access = access;
        _browse = browse;
        _validator = validator;
        _state = state;
        _startup = startup;
        _probe = probe;
    }

    /// <summary>Who is calling and whether they may use the plugin.</summary>
    /// <returns>The caller.</returns>
    [HttpGet("Me")]
    [Produces(MediaTypeNames.Application.Json)]
    public IActionResult GetMe()
    {
        var caller = _access.GetCaller(User);
        return caller is null
            ? Error(StatusCodes.Status401Unauthorized, "unauthorized", "Sign in to Jellyfin with a user account.")
            : Json(new MeDto(
                caller.User.Id,
                caller.User.Username,
                caller.IsAdmin,
                caller.Allowed,
                AccessService.DenialCode(caller.Denial),
                caller.Allowed ? null : AccessService.DenialMessage(caller.Denial)));
    }

    /// <summary>TV libraries, or one parent's children, paged.</summary>
    /// <param name="parentId">Parent; empty for libraries.</param>
    /// <param name="startIndex">First item.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="startsWith">Library pages only: <c>A</c>–<c>Z</c>, or <c>#</c> for names before A (D47).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A page.</returns>
    [HttpGet("Browse")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> Browse([FromQuery] Guid? parentId, [FromQuery] int startIndex = 0, [FromQuery] int limit = 50, [FromQuery] string? startsWith = null, CancellationToken cancellationToken = default)
    {
        if (Deny(out var caller) is { } denied)
        {
            return denied;
        }

        if (!LetterFilter.TryParse(startsWith, out var letter))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request", "startsWith must be a letter A to Z, or # for names that start with a digit or symbol.");
        }

        var page = await _browse.BrowseAsync(caller.User.Id, parentId, Math.Max(0, startIndex), Clamp(limit), cancellationToken, letter).ConfigureAwait(false);
        return page is null
            ? Error(StatusCodes.Status404NotFound, "not_found", "That item was not found or you cannot access it.")
            : Json(page);
    }

    /// <summary>Search series and episodes, paged.</summary>
    /// <param name="q">Search term.</param>
    /// <param name="startIndex">First item.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A page.</returns>
    [HttpGet("Search")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int startIndex = 0, [FromQuery] int limit = 30, CancellationToken cancellationToken = default)
    {
        if (Deny(out var caller) is { } denied)
        {
            return denied;
        }

        var page = await _browse.SearchAsync(caller.User.Id, q ?? string.Empty, Math.Max(0, startIndex), Clamp(limit), cancellationToken).ConfigureAwait(false);
        return Json(page);
    }

    /// <summary>Validates a selection and reports what would be queued. Nothing is queued.</summary>
    /// <param name="request">Selected ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The preview.</returns>
    [HttpPost("Jobs/Preview")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> Preview([FromBody] SelectionRequest request, CancellationToken cancellationToken)
    {
        if (Deny(out var caller) is { } denied)
        {
            return denied;
        }

        var max = Plugin.Instance?.Configuration.MaxEpisodesPerRequest ?? 150;
        var result = await _validator.ValidateAsync(caller.User.Id, request?.ItemIds, max, cancellationToken, request?.Languages, request?.Again ?? false).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            return Error(StatusCodes.Status400BadRequest, error.Code, error.Message);
        }

        var s = result.Skipped;
        return Json(new PreviewDto(
            result.Kind == SelectionKind.Movies ? "movies" : "episodes",
            result.SeriesId,
            result.Kind == SelectionKind.Movies ? null : result.SeriesName,
            result.Selected,
            result.Eligible.Count,
            new SkippedDto(s.AlreadySynced, s.NoEnglishAudio, s.NoSidecar, s.NoVideoFile),
            result.AttemptedBefore));
    }

    /// <summary>Runs <c>subsync-cli check</c> and <c>version</c> as the Jellyfin account. Admin only.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    [HttpPost("Admin/TestSubSync")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> TestSubSync(CancellationToken cancellationToken)
    {
        var r = await _probe.RunAsync(Plugin.Instance?.Configuration.SubSyncFolder, cancellationToken).ConfigureAwait(false);
        return Json(new TestSubSyncDto(r.Ok, r.Version, r.Mode, r.Message, r.CheckExitCode, r.CheckOutput));
    }

    /// <summary>
    /// Every TV and movie library the admin can see, ignoring the "Libraries" setting, for the settings page to offer
    /// (D43). Admin only.
    /// </summary>
    /// <returns>Libraries.</returns>
    [HttpGet("Admin/Libraries")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces(MediaTypeNames.Application.Json)]
    public IActionResult AllLibraries()
    {
        var caller = _access.GetCaller(User);
        if (caller is null)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "Sign in to Jellyfin with a user account.");
        }

        var allowed = LibraryScopeFacade.Parse(Plugin.Instance?.Configuration.AllowedLibraryIds);
        return Json(_allLibraries.GetLibraries(caller.User.Id)
            .Select(l => new LibraryOptionDto(l.Id, l.Name, l.LibraryType ?? "tvshows", allowed is null || allowed.Contains(l.Id)))
            .ToList());
    }

    /// <summary>Startup and state health for the diagnostics page. Admin only.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Diagnostics.</returns>
    [HttpGet("Admin/Diagnostics")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> Diagnostics(CancellationToken cancellationToken)
    {
        string? last = null;
        if (_startup.StateReady)
        {
            last = await _state.GetMetaAsync(DiagnosticKey, cancellationToken).ConfigureAwait(false);
        }

        return Json(BuildDiagnostics(last));
    }

    /// <summary>Writes the diagnostic row (spike S7: survives a restart). Admin only.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Diagnostics after the write.</returns>
    [HttpPost("Admin/Diagnostics/State")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> WriteDiagnosticRow(CancellationToken cancellationToken)
    {
        if (!_startup.StateReady)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "state_unavailable", _startup.StateError ?? "The state database is not ready.");
        }

        var value = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await _state.SetMetaAsync(DiagnosticKey, value, cancellationToken).ConfigureAwait(false);
        var readBack = await _state.GetMetaAsync(DiagnosticKey, cancellationToken).ConfigureAwait(false);
        return Json(BuildDiagnostics(readBack));
    }

    private static int Clamp(int limit) => Math.Clamp(limit, 1, MaxPageSize);

    private static ContentResult Json(object value) => new()
    {
        Content = JsonSerializer.Serialize(value, value.GetType(), ApiJson.Options),
        ContentType = MediaTypeNames.Application.Json,
        StatusCode = StatusCodes.Status200OK,
    };

    private static ContentResult Error(int status, string code, string message) => new()
    {
        Content = JsonSerializer.Serialize(new ErrorDto(code, message), ApiJson.Options),
        ContentType = MediaTypeNames.Application.Json,
        StatusCode = status,
    };

    private DiagnosticsDto BuildDiagnostics(string? lastWrite) => new(
        typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
        _state.Location,
        _startup.StateReady,
        _startup.StateError,
        _startup.MenuRegistered,
        _startup.MenuError,
        lastWrite);

    private ContentResult? Deny(out Caller caller)
    {
        caller = _access.GetCaller(User)!;
        if (caller is null)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "Sign in to Jellyfin with a user account.");
        }

        return caller.Allowed
            ? null
            : Error(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, AccessService.DenialMessage(caller.Denial));
    }
}

/// <summary>Serves the page and its scripts. Anonymous: these files contain no data (API.md, static).</summary>
[ApiController]
[AllowAnonymous]
[Route("SubtitleFixer")]
public sealed class SubtitleFixerPageController : ControllerBase
{
    private static readonly Assembly Self = typeof(SubtitleFixerPageController).Assembly;

    /// <summary>The page.</summary>
    /// <returns>HTML.</returns>
    [HttpGet("Page")]
    public IActionResult GetPage() => Resource("index.html", MediaTypeNames.Text.Html);

    /// <summary>Page script.</summary>
    /// <returns>JavaScript.</returns>
    [HttpGet("Page/app.js")]
    public IActionResult GetAppJs() => Resource("app.js", "application/javascript");

    /// <summary>Page styles.</summary>
    /// <returns>CSS.</returns>
    [HttpGet("Page/styles.css")]
    public IActionResult GetStyles() => Resource("styles.css", "text/css");

    /// <summary>Fake data for <c>?mock=1</c> (UI.md, mock mode). Loaded only in mock mode; contains no real data.</summary>
    /// <returns>JavaScript.</returns>
    [HttpGet("Page/mock-data.js")]
    public IActionResult GetMockData() => Resource("mock-data.js", "application/javascript");

    /// <summary>Avatar-menu script injected into jellyfin-web.</summary>
    /// <returns>JavaScript.</returns>
    [HttpGet("menu.js")]
    public IActionResult GetMenuJs() => Resource("menu.js", "application/javascript");

    private FileStreamResult Resource(string name, string contentType)
    {
        var stream = Self.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Web.{name}")
            ?? throw new FileNotFoundException("Embedded resource missing", name);
        Response.Headers.CacheControl = "no-cache";
        return File(stream, contentType + "; charset=utf-8");
    }
}

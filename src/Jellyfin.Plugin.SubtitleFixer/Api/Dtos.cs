using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleFixer.Api;

/// <summary>JSON settings for our responses: camelCase as in API.md (Jellyfin's own default is PascalCase).</summary>
public static class ApiJson
{
    /// <summary>Gets the serializer options.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>Error body.</summary>
/// <param name="Error">Code.</param>
/// <param name="Message">Message for people.</param>
public sealed record ErrorDto(string Error, string Message);

/// <summary><c>GET /Me</c>.</summary>
/// <param name="UserId">User id.</param>
/// <param name="Name">User name.</param>
/// <param name="IsAdmin">Administrator.</param>
/// <param name="Allowed">Allowed by <c>AllowedUsers</c>.</param>
/// <param name="Reason">Why not: <c>not_configured</c> (SubSync not set up yet; admins only until then) or <c>admins_only</c>; null when allowed.</param>
/// <param name="Message">Message for people when not allowed.</param>
public sealed record MeDto(Guid UserId, string Name, bool IsAdmin, bool Allowed, string? Reason, string? Message);

/// <summary>A breadcrumb entry.</summary>
/// <param name="Id">Item id.</param>
/// <param name="Name">Name.</param>
/// <param name="Type">Library, Series, Season or Episode.</param>
public sealed record CrumbDto(Guid Id, string Name, string Type);

/// <summary>The parent of a browse page.</summary>
/// <param name="Id">Item id.</param>
/// <param name="Name">Name.</param>
/// <param name="Type">Type.</param>
/// <param name="Breadcrumb">Library first, ending with this item.</param>
public sealed record ParentDto(Guid Id, string Name, string Type, IReadOnlyList<CrumbDto> Breadcrumb);

/// <summary>One row in Browse or Search (API.md).</summary>
public sealed record ItemDto
{
    /// <summary>Gets the item id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the type: Library, Series, Season or Episode.</summary>
    public required string Type { get; init; }

    /// <summary>Gets the series id.</summary>
    public Guid? SeriesId { get; init; }

    /// <summary>Gets the series name.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets the season id of an episode (search results open their season).</summary>
    public Guid? SeasonId { get; init; }

    /// <summary>Gets the library type for library rows: <c>tvshows</c> or <c>movies</c>.</summary>
    public string? LibraryType { get; init; }

    /// <summary>Gets the year, for movies.</summary>
    public int? ProductionYear { get; init; }

    /// <summary>Gets the season or episode number.</summary>
    public int? IndexNumber { get; init; }

    /// <summary>Gets the season number of an episode.</summary>
    public int? ParentIndexNumber { get; init; }

    /// <summary>Gets the episode count (seasons only).</summary>
    public int? EpisodeCount { get; init; }

    /// <summary>Gets the eligible episode count (seasons only).</summary>
    public int? EligibleCount { get; init; }

    /// <summary>Gets the synced episode count (seasons only).</summary>
    public int? SyncedCount { get; init; }

    /// <summary>Gets the status: none, done or attempted (episodes; seasons report none).</summary>
    public string Status { get; init; } = "none";

    /// <summary>Gets whether the episode can be queued (episodes only).</summary>
    public bool? Eligible { get; init; }

    /// <summary>Gets why not: no_video_file, already_synced, no_english_audio, no_sidecar.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets the message of the last attempt.</summary>
    public string? Message { get; init; }
}

/// <summary><c>GET /Browse</c> and <c>GET /Search</c>.</summary>
/// <param name="Parent">Parent, null for the library list and search.</param>
/// <param name="TotalCount">Total items.</param>
/// <param name="Items">This page.</param>
public sealed record PageDto(ParentDto? Parent, int TotalCount, IReadOnlyList<ItemDto> Items);

/// <summary>Body of <c>POST /Jobs/Preview</c> and <c>POST /Jobs</c>.</summary>
public sealed class SelectionRequest
{
    /// <summary>Gets or sets the selected season and episode ids.</summary>
    public IReadOnlyList<Guid>? ItemIds { get; set; }
}

/// <summary>Skipped counts.</summary>
/// <param name="AlreadySynced">Already synced.</param>
/// <param name="NoEnglishAudio">No English audio.</param>
/// <param name="NoSidecar">No sidecar.</param>
/// <param name="NoVideoFile">Video missing.</param>
public sealed record SkippedDto(int AlreadySynced, int NoEnglishAudio, int NoSidecar, int NoVideoFile);

/// <summary><c>POST /Jobs/Preview</c>.</summary>
/// <param name="Kind"><c>episodes</c> or <c>movies</c>.</param>
/// <param name="SeriesId">Series; null for movies.</param>
/// <param name="SeriesName">Series name.</param>
/// <param name="Selected">Distinct episodes after expanding seasons.</param>
/// <param name="Eligible">Episodes that would be queued.</param>
/// <param name="Skipped">Skipped counts.</param>
/// <param name="AttemptedBefore">Eligible episodes attempted before.</param>
public sealed record PreviewDto(string Kind, Guid? SeriesId, string? SeriesName, int Selected, int Eligible, SkippedDto Skipped, int AttemptedBefore);

/// <summary><c>POST /Admin/TestSubSync</c>.</summary>
/// <param name="Ok">Ready.</param>
/// <param name="Version">SubSync version.</param>
/// <param name="Mode">GPU, CPU or unknown.</param>
/// <param name="Message">Message.</param>
/// <param name="CheckExitCode">Exit code of check.</param>
/// <param name="CheckOutput">Raw output of check.</param>
public sealed record TestSubSyncDto(bool Ok, string? Version, string Mode, string Message, int? CheckExitCode, string? CheckOutput);

/// <summary><c>GET /Admin/Diagnostics</c>.</summary>
/// <param name="PluginVersion">Plugin version.</param>
/// <param name="StateLocation">Database path.</param>
/// <param name="StateReady">Database opened.</param>
/// <param name="StateError">Database error.</param>
/// <param name="MenuRegistered">Menu transformation registered.</param>
/// <param name="MenuError">Registration error.</param>
/// <param name="LastDiagnosticWrite">Value of the diagnostic row, if written before.</param>
public sealed record DiagnosticsDto(
    string PluginVersion,
    string StateLocation,
    bool StateReady,
    string? StateError,
    bool MenuRegistered,
    string? MenuError,
    string? LastDiagnosticWrite);

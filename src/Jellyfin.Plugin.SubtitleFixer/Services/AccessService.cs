using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SubtitleFixer.Configuration;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SubtitleFixer.Services;

/// <summary>Why a caller may not use the plugin (wire names in <see cref="AccessService.DenialCode"/>).</summary>
public enum AccessDenial
{
    /// <summary>Allowed.</summary>
    None,

    /// <summary>Non-admins wait until an admin has set up SubSync (D10).</summary>
    NotConfigured,

    /// <summary><c>AllowedUsers</c> is <c>AdminOnly</c>.</summary>
    AdminsOnly,
}

/// <summary>The signed-in caller and what they may do.</summary>
/// <param name="User">Jellyfin user.</param>
/// <param name="IsAdmin">Whether the user is an administrator.</param>
/// <param name="Denial">Why not allowed, or <see cref="AccessDenial.None"/>.</param>
public sealed record Caller(User User, bool IsAdmin, AccessDenial Denial)
{
    /// <summary>Gets a value indicating whether the caller may use the plugin.</summary>
    public bool Allowed => Denial == AccessDenial.None;
}

/// <summary>
/// Resolves the caller from Jellyfin's claims and applies <c>AllowedUsers</c> (SPEC.md hard rule 8, D10).
/// Admins are always allowed. Everyone else is allowed when <c>AllowedUsers</c> is <c>AllAuthenticated</c> (the
/// default) and SubSync is configured, so a fresh install stays admin-only until an admin has set it up.
/// </summary>
public sealed class AccessService
{
    /// <summary>Claim Jellyfin puts the user id in (<c>Jellyfin.Api.Constants.InternalClaimTypes.UserId</c>, not on NuGet).</summary>
    public const string UserIdClaim = "Jellyfin-UserId";

    private readonly IUserManager _userManager;
    private readonly ISubSyncWorker _worker;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessService"/> class.
    /// </summary>
    /// <param name="userManager">User manager.</param>
    /// <param name="worker">Worker (only "is SubSync configured").</param>
    public AccessService(IUserManager userManager, ISubSyncWorker worker)
    {
        _userManager = userManager;
        _worker = worker;
    }

    /// <summary>Gets the wire name of a denial (API.md, <c>GET /Me</c>).</summary>
    /// <param name="denial">Denial.</param>
    /// <returns>Wire name, or null when allowed.</returns>
    public static string? DenialCode(AccessDenial denial) => denial switch
    {
        AccessDenial.NotConfigured => "not_configured",
        AccessDenial.AdminsOnly => "admins_only",
        _ => null,
    };

    /// <summary>Gets the message for a denial.</summary>
    /// <param name="denial">Denial.</param>
    /// <returns>Message for people.</returns>
    public static string DenialMessage(AccessDenial denial) => denial switch
    {
        AccessDenial.NotConfigured => "Subtitle Fixer isn't set up yet. Ask an administrator to finish its settings.",
        _ => "An administrator has not allowed your account to use Subtitle Fixer.",
    };

    /// <summary>Decides access. Pure, for tests.</summary>
    /// <param name="isAdmin">Administrator.</param>
    /// <param name="setting"><c>AllowedUsers</c>.</param>
    /// <param name="subSyncConfigured">SubSync folder set and the CLI present.</param>
    /// <returns>The denial, or none.</returns>
    public static AccessDenial Decide(bool isAdmin, AllowedUsers setting, bool subSyncConfigured)
    {
        if (isAdmin)
        {
            return AccessDenial.None;
        }

        if (setting != AllowedUsers.AllAuthenticated)
        {
            return AccessDenial.AdminsOnly;
        }

        return subSyncConfigured ? AccessDenial.None : AccessDenial.NotConfigured;
    }

    /// <summary>Gets the caller, or null when the request carries no known user (for example an API key).</summary>
    /// <param name="principal">Request principal.</param>
    /// <returns>The caller.</returns>
    public Caller? GetCaller(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst(UserIdClaim)?.Value;
        if (!Guid.TryParse(raw, out var userId) || userId == Guid.Empty)
        {
            return null;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        var isAdmin = user.HasPermission(PermissionKind.IsAdministrator);
        var setting = Plugin.Instance?.Configuration.AllowedUsers ?? AllowedUsers.AllAuthenticated;
        return new Caller(user, isAdmin, Decide(isAdmin, setting, !isAdmin && _worker.IsConfigured));
    }
}

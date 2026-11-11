using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SubtitleFixer.Configuration;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SubtitleFixer.Services;

/// <summary>The signed-in caller and what they may do.</summary>
/// <param name="User">Jellyfin user.</param>
/// <param name="IsAdmin">Whether the user is an administrator.</param>
/// <param name="Allowed">Whether <c>AllowedUsers</c> lets them use the plugin.</param>
public sealed record Caller(User User, bool IsAdmin, bool Allowed);

/// <summary>
/// Resolves the caller from Jellyfin's claims and applies <c>AllowedUsers</c> (SPEC.md hard rule 8).
/// </summary>
public sealed class AccessService
{
    /// <summary>Claim Jellyfin puts the user id in (<c>Jellyfin.Api.Constants.InternalClaimTypes.UserId</c>, not on NuGet).</summary>
    public const string UserIdClaim = "Jellyfin-UserId";

    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessService"/> class.
    /// </summary>
    /// <param name="userManager">User manager.</param>
    public AccessService(IUserManager userManager)
    {
        _userManager = userManager;
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
        var setting = Plugin.Instance?.Configuration.AllowedUsers ?? AllowedUsers.AdminOnly;
        return new Caller(user, isAdmin, isAdmin || setting == AllowedUsers.AllAuthenticated);
    }
}

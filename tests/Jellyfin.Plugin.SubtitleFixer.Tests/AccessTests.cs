using Jellyfin.Plugin.SubtitleFixer.Configuration;
using Jellyfin.Plugin.SubtitleFixer.Services;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

/// <summary>D10: everyone by default, but only admins until SubSync is set up.</summary>
public sealed class AccessTests
{
    [Theory]
    [InlineData(true, AllowedUsers.AllAuthenticated, false, AccessDenial.None)]
    [InlineData(true, AllowedUsers.AdminOnly, false, AccessDenial.None)]
    [InlineData(false, AllowedUsers.AllAuthenticated, true, AccessDenial.None)]
    [InlineData(false, AllowedUsers.AllAuthenticated, false, AccessDenial.NotConfigured)]
    [InlineData(false, AllowedUsers.AdminOnly, true, AccessDenial.AdminsOnly)]
    [InlineData(false, AllowedUsers.AdminOnly, false, AccessDenial.AdminsOnly)]
    public void Decide(bool isAdmin, AllowedUsers setting, bool configured, AccessDenial expected)
    {
        Assert.Equal(expected, AccessService.Decide(isAdmin, setting, configured));
    }

    [Fact]
    public void Default_is_everyone()
    {
        Assert.Equal(AllowedUsers.AllAuthenticated, new PluginConfiguration().AllowedUsers);
    }

    [Fact]
    public void Denials_have_codes_and_messages()
    {
        Assert.Null(AccessService.DenialCode(AccessDenial.None));
        Assert.Equal("not_configured", AccessService.DenialCode(AccessDenial.NotConfigured));
        Assert.Equal("admins_only", AccessService.DenialCode(AccessDenial.AdminsOnly));
        Assert.Contains("set up", AccessService.DenialMessage(AccessDenial.NotConfigured), StringComparison.Ordinal);
    }
}

using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Platform.Tests.Identity;

/// <summary>
/// An ArcGIS token's scope stops at the native administration API and nowhere else — ADR-015 §4, Q-154.
/// </summary>
public sealed class SessionScopesTests
{
    [Theory]
    [InlineData("/admin", true)]
    [InlineData("/admin/layers", true)]
    [InlineData("/ADMIN/members/x", true)]
    [InlineData("/admin/generateToken", false)]
    [InlineData("/rest/admin/services/hosted/x/FeatureServer/0/truncate", false)]
    [InlineData("/administration", false)]
    [InlineData("/rest/services", false)]
    [InlineData("/sharing/rest/search", false)]
    [InlineData(null, false)]
    public void Only_the_native_administration_API_is_outside_the_scope(string? path, bool native)
    {
        Assert.Equal(native, SessionScopes.IsNativeAdministration(path));
    }
}

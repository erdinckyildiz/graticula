using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// A service's page says who may read it.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-141](../../docs/architecture-debt.md): it was a pill on the services list and nothing
/// at all on the service's own four pages.</b> An operator who arrived from a bookmark or a
/// shared link — the path that was broken for a different reason on the same day — read a whole
/// settings page with no sign of whether the thing was private. *You can see it on the other
/// screen* is not an answer when the other screen is not the one they are on.
/// </para>
/// <para>
/// <b>Both kinds, because the lookup needs two catalogues.</b> An image service is a row in
/// `coverage` and not in the feature-service listing, so a version of this that asked only
/// `ListServicesAsync` would report every image service as unknown and draw nothing — which is
/// the shape of omission [D-136](../../docs/architecture-debt.md) records, arriving by a
/// different door. One test per kind is what makes that visible.
/// </para>
/// </remarks>
public sealed class ServiceScopeTests : ConsoleTest
{
    /// <summary>The three scopes a service can carry.</summary>
    private static readonly string[] Scopes = ["public", "private", "organization"];

    [Fact]
    public async Task A_private_image_service_says_so_on_its_own_page()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/service/hosted/look_imagery", token);

        await WaitForAsync(
            "!!document.getElementById('serviceScope')"
            + " && document.getElementById('serviceScope').hidden === false",
            "A service's page shows no sharing scope, so an operator reading its settings "
            + "cannot tell whether it is private.");

        string scope = await Browser.EvaluateAsync<string>(
            "document.getElementById('serviceScope')?.textContent || ''") ?? string.Empty;

        Assert.Equal("private", scope.Trim());

        // <b>Visible, not merely present.</b> This console has shipped a control that existed
        // in the DOM and could not be seen three separate times.
        bool seen = await Browser.EvaluateAsync<bool>(
            """
            (() => {
              const e = document.getElementById('serviceScope');
              if (!e || e.offsetParent === null) return false;
              const box = e.getBoundingClientRect();
              return box.width > 0 && box.height > 0;
            })()
            """);

        Assert.True(seen, "The sharing scope is in the markup and not on the screen.");
    }

    [Fact]
    public async Task A_feature_service_says_it_too()
    {
        // <b>The other catalogue.</b> A feature service is found by `ListServicesAsync`; an
        // image service is not, and vice versa. Asserting one kind would have passed with the
        // other broken.
        (string token, _) = await SignInAsync();

        string layerService = await AnyLayerAsync();

        Assert.False(
            string.IsNullOrWhiteSpace(layerService),
            "No feature service in the catalogue, so there is no page to open. This fails "
            + "rather than skips: a green run with its subject absent is worse than no test.");

        await OpenAsync("/server/#/service/hosted/look_buildings", token);

        await WaitForAsync(
            "!!document.getElementById('serviceScope')"
            + " && document.getElementById('serviceScope').hidden === false",
            "A feature service's page shows no sharing scope.");

        string scope = await Browser.EvaluateAsync<string>(
            "document.getElementById('serviceScope')?.textContent || ''") ?? string.Empty;

        // <b>Whatever it is, not a particular value.</b> Which scope a development service
        // happens to carry is not this test's subject; that the page states it is.
        Assert.Contains(scope.Trim(), Scopes);
    }
}

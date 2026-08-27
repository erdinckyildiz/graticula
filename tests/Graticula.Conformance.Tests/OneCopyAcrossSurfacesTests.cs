using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Server and Studio are one application served twice, not two that have to be kept in step.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-034](../../docs/adr/ADR-034-server-and-studio.md) condition 2.</b> *"One stylesheet
/// and one map module across both surfaces, asserted by a test that fails if a second copy of
/// either appears. D-46 has six recorded instances and this decision doubles the opportunity."*
/// </para>
/// <para>
/// <b>Asserted from outside, because the thing being asserted is what a browser gets.</b> The
/// arrangement that makes it true is one <c>PhysicalFileProvider</c> mounted at
/// <c>/server</c> and <c>/studio</c>, and a source test could read that — but a second copy
/// would most likely arrive as a second file served at one of the two paths, which only a
/// request can see. So this fetches each asset from both surfaces and compares the bytes.
/// </para>
/// <para>
/// <b>The assets are read from the page rather than listed here.</b> A hard-coded list is a
/// second place to keep in step, which is the shape of the defect this condition is about.
/// </para>
/// </remarks>
public sealed class OneCopyAcrossSurfacesTests : ArcGisClient
{
    /// <summary>The two mounts ADR-034 §5 created over one directory.</summary>
    private static readonly string[] Surfaces = ["/server", "/studio"];

    /// <summary>
    /// Every asset the application loads is the same bytes on both surfaces.
    /// </summary>
    [Fact]
    public async Task Both_surfaces_serve_the_same_bytes_for_every_asset_the_page_names()
    {
        string root = await RequireServerAsync();

        string page = await TextAsync($"{root}/server/index.html");

        string[] assets =
        [
            .. Regex.Matches(page, "(?:src|href)=\"([^\"]+)\"")
                .Select(m => m.Groups[1].Value)
                .Where(a => !a.Contains("://", StringComparison.Ordinal))
                .Where(a => a.EndsWith(".js", StringComparison.Ordinal)
                            || a.EndsWith(".css", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];

        Assert.True(
            assets.Length >= 3,
            $"index.html names {assets.Length} same-origin scripts or stylesheets, which is fewer "
            + "than the application has. The asset list is read from the page, so this failing "
            + "means the page changed shape rather than that the surfaces disagree.");

        foreach (string asset in assets)
        {
            List<string> digests = [];

            foreach (string surface in Surfaces)
            {
                using HttpResponseMessage response =
                    await Http.GetAsync(new Uri($"{root}{surface}/{asset}"));

                Assert.True(
                    response.StatusCode == HttpStatusCode.OK,
                    $"{surface}/{asset} answered {(int)response.StatusCode}. Both surfaces serve "
                    + "one directory, so an asset present on one and absent on the other is the "
                    + "second copy this test exists to catch.");

                digests.Add(Convert.ToHexString(
                    SHA256.HashData(await response.Content.ReadAsByteArrayAsync())));
            }

            Assert.True(
                digests.Distinct(StringComparer.Ordinal).Count() == 1,
                $"'{asset}' is not the same file on Server and Studio: "
                + string.Join(" vs ", digests.Select(d => d[..12]))
                + ". ADR-034 condition 2 — one stylesheet and one map module across both "
                + "surfaces. D-46 is six recorded instances of exactly this.");
        }
    }

    /// <summary>
    /// The page names one stylesheet of its own, and one place the map SDK is loaded from.
    /// </summary>
    /// <remarks>
    /// <b>The count, not the equality.</b> The test above would pass against two stylesheets as
    /// long as both surfaces served both of them — the copies would be identical and the
    /// condition still broken. This is the half that says *one*.
    /// </remarks>
    [Fact]
    public async Task The_application_carries_one_stylesheet_and_one_map_loader()
    {
        string root = await RequireServerAsync();

        string page = await TextAsync($"{root}/server/index.html");

        string[] stylesheets =
        [
            .. Regex.Matches(page, "<link[^>]+rel=\"stylesheet\"[^>]*>")
                .Select(m => m.Value)
                .Where(link => !link.Contains("://", StringComparison.Ordinal)),
        ];

        Assert.True(
            stylesheets.Length == 1,
            $"The application links {stylesheets.Length} same-origin stylesheets: "
            + string.Join(" | ", stylesheets)
            + ". ADR-034 condition 2 asks for one across both surfaces.");

        // The SDK's address appears once in the code that loads it. A second literal is how a
        // pinned version drifts between the two surfaces without either being wrong on its own.
        string console = await TextAsync($"{root}/server/console.js");

        int loaders = Regex.Count(console, "https://js\\.arcgis\\.com");

        Assert.True(
            loaders == 1,
            $"console.js names the map SDK's address {loaders} times. One is the map module; a "
            + "second is the copy ADR-034 condition 2 forbids.");
    }

    /// <summary>Fetches a text asset, failing with the status when it is not there.</summary>
    private async Task<string> TextAsync(string url)
    {
        using HttpResponseMessage response = await Http.GetAsync(new Uri(url));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{url} answered {(int)response.StatusCode}.");

        return await response.Content.ReadAsStringAsync();
    }
}

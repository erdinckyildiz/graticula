using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Api.ArcGis;
using Graticula.Platform.Admin;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A service with several styles is checked across all of them, and a style naming an origin taken off the
/// list is not served — ADR-094.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sprite check read one style until there were several.</b> ADR-092's refusal compared a sheet with
/// the stored style; with named styles, a sheet that loses an icon the <c>dark</c> style names would pass that
/// check and leave <c>dark</c> drawing nothing. The pure halves of the check are pinned here; the routes are in
/// <c>NamedStyleConformanceTests</c>.
/// </para>
/// <para>
/// <b>The serving check with the list in it</b>, and the one relaxation it keeps: an unreadable sheet excuses a
/// style's icons, and nothing else.
/// </para>
/// </remarks>
public sealed class EveryStyleOfAServiceIsCheckedTests
{
    private static string Drawing(params string[] icons) =>
        "{ \"version\": 8, \"layers\": ["
        + string.Join(", ", icons.Select((icon, i) =>
            $"{{ \"id\": \"l{i}\", \"type\": \"symbol\", \"source-layer\": \"parcels\", \"layout\": {{ \"icon-image\": \"{icon}\" }} }}"))
        + "] }";

    private static readonly StoredStyle Light = new("light", Drawing("marker"), IsDefault: true, UpdatedAt: null);

    private static readonly StoredStyle Dark = new("dark", Drawing("marker", "school"), IsDefault: false, UpdatedAt: null);

    [Fact]
    public void Every_style_contributes_its_icons_with_its_name()
    {
        IReadOnlyList<(string Icon, IReadOnlyList<string> Styles)> uses = AdminEndpoints.IconsTheStylesDraw([Light, Dark]);

        Assert.Equal(["marker", "school"], uses.Select(u => u.Icon));
        Assert.Equal(["light", "dark"], uses.Single(u => u.Icon == "marker").Styles);
        Assert.Equal(["dark"], uses.Single(u => u.Icon == "school").Styles);
    }

    [Fact]
    public void A_sheet_that_only_the_default_would_accept_is_refused_for_the_other_style()
    {
        string[] markerOnly = ["marker"];

        IReadOnlyList<(string Icon, IReadOnlyList<string> Styles)> missing =
            AdminEndpoints.IconsTheSheetLacks(AdminEndpoints.IconsTheStylesDraw([Light, Dark]), markerOnly);

        (string icon, IReadOnlyList<string> styles) = Assert.Single(missing);
        Assert.Equal("school", icon);
        Assert.Equal(["dark"], styles);
    }

    [Fact]
    public void No_styles_hold_nothing() =>
        Assert.Empty(AdminEndpoints.IconsTheStylesDraw([]));

    // ---------- serving ----------

    private const string Vendor = """
        {
          "version": 8,
          "sources": {
            "esri": { "type": "vector", "url": "../../" },
            "vendor": { "type": "raster", "tiles": ["https://tiles.example.com/{z}/{x}/{y}.png"] }
          },
          "layers": [ { "id": "p", "type": "fill", "source": "esri", "source-layer": "parcels" } ]
        }
        """;

    private static StyleOrigin Origin(string text)
    {
        Assert.True(StyleOrigins.TryParse(text, out StyleOrigin? origin, out string? error), error);
        return origin!;
    }

    [Fact]
    public void A_style_on_an_allowed_origin_is_served_and_one_taken_off_the_list_is_not()
    {
        string[] parcels = ["parcels"];

        Assert.True(
            VectorTileEndpoints.StoredStyleFits(Vendor, parcels, null, [Origin("https://tiles.example.com")], out string? stale),
            stale);

        Assert.False(VectorTileEndpoints.StoredStyleFits(Vendor, parcels, null, [], out stale));
        Assert.Contains("https://tiles.example.com", stale!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_sheet_excuses_the_icons_and_not_the_origins()
    {
        string[] parcels = ["parcels"];
        string pins = Drawing("marker");

        // No sheet could be read: the icon is excused rather than refused for a sheet nobody could see.
        Assert.True(VectorTileEndpoints.StoredStyleFits(pins, parcels, null, [], out string? stale, iconsCheckable: false), stale);

        // The same excuse does not reach the origin: a removed origin still takes the style down.
        Assert.False(VectorTileEndpoints.StoredStyleFits(Vendor, parcels, null, [], out _, iconsCheckable: false));
    }

    // ---------- the console's policy ----------

    [Fact]
    public void The_console_policy_allows_the_style_origins_to_be_fetched_and_nothing_else()
    {
        string policy = SecurityHeaders.ConsolePolicyFor(
            "https://js.arcgis.com", ["https://tiles.example.com", "https://*.cdn.example.net"]);

        string Directive(string name) =>
            policy.Split(';').Select(d => d.Trim()).Single(d => d.StartsWith(name + " ", StringComparison.Ordinal));

        foreach (string directive in (string[])["connect-src", "img-src"])
        {
            Assert.Contains("https://tiles.example.com", Directive(directive), StringComparison.Ordinal);
            Assert.Contains("https://*.cdn.example.net", Directive(directive), StringComparison.Ordinal);
        }

        foreach (string directive in (string[])["script-src", "style-src", "font-src"])
        {
            Assert.DoesNotContain("tiles.example.com", Directive(directive), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void With_no_style_origins_the_console_policy_is_what_it_was() =>
        Assert.Equal(
            SecurityHeaders.ConsolePolicyFor("https://js.arcgis.com"),
            SecurityHeaders.ConsolePolicyFor("https://js.arcgis.com", []));
}

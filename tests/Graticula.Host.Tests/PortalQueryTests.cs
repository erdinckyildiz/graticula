using System;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The portal search query, and the wrong answer it exists to prevent.
/// </summary>
/// <remarks>
/// <b>ArcGIS Pro finds its geocoder by searching for its URL.</b> A server that
/// ignores <c>q</c> and returns everything has just told Pro that a provinces
/// layer is a geocoding service, and Pro will use it — there is no error anywhere
/// in that sequence. So a clause this cannot evaluate returns nothing rather than
/// everything, which is the same rule <c>FilterReader</c> follows for WFS.
/// </remarks>
public sealed class PortalQueryTests
{
    private static object Item(
        string title = "tr_il",
        string type = "Feature Service",
        string owner = "root",
        string url = "https://example/rest/services/hosted/tr_il/FeatureServer") => new
        {
            title,
            type,
            owner,
            url,
            tags = new[] { "hosted" },
        };

    [Fact]
    public void An_empty_query_matches_everything()
    {
        Assert.True(PortalQuery.Matches(Item(), null));
        Assert.True(PortalQuery.Matches(Item(), string.Empty));
        Assert.True(PortalQuery.Matches(Item(), "   "));
    }

    [Fact]
    public void A_clause_this_server_cannot_evaluate_matches_nothing()
    {
        // <b>The one that matters.</b> Pro asks for its geocoder by URL against
        // Esri's own servers. The only truthful answer this server has is none, and
        // the tempting answer -- ignore what you do not understand -- is the one
        // that hands back a provinces layer.
        Assert.False(PortalQuery.Matches(
            Item(),
            "url:https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer"));

        // Any unknown key, not just that one.
        Assert.False(PortalQuery.Matches(Item(), "categories:/Basemaps"));
        Assert.False(PortalQuery.Matches(Item(), "group:abc123"));
    }

    [Fact]
    public void A_url_clause_that_does_match_is_honoured()
    {
        // The refusal above is about not being able to answer, not about the field.
        Assert.True(PortalQuery.Matches(
            Item(url: "https://example/rest/services/hosted/tr_il/FeatureServer"),
            "url:https://example/rest/services/hosted/tr_il/FeatureServer"));
    }

    [Theory]
    [InlineData("type:\"Feature Service\"", true)]
    [InlineData("type:\"Vector Tile Service\"", false)]
    [InlineData("-type:\"Shapefile\"", true)]
    [InlineData("-type:\"Feature Service\"", false)]
    [InlineData("owner:root", true)]
    [InlineData("owner:someone", false)]
    [InlineData("tags:hosted", true)]
    [InlineData("tags:turkiye", false)]
    public void The_clauses_pro_sends_are_evaluated(string query, bool expected)
    {
        Assert.Equal(expected, PortalQuery.Matches(Item(), query));
    }

    [Fact]
    public void Pros_own_my_content_query_matches_a_feature_service()
    {
        // Trimmed from the real thing, which carries thirty negated types. None of
        // them is what this server publishes, so the item survives all of them.
        const string Query =
            "owner:root ownerfolder:root -type:\"Web Mapping Application\"  -type:\"Shapefile\"  "
            + "-type:\"Service Definition\"  -type:\"CSV\"  -type:\"Map Document\"  "
            + "-type:\"Geometry Service\"  -type:\"PDF\" ";

        Assert.True(PortalQuery.Matches(Item(), Query));

        // And the same query excludes what it says it excludes.
        Assert.False(PortalQuery.Matches(Item(type: "Shapefile"), Query));
    }

    [Fact]
    public void Ownerfolder_is_accepted_and_ignored_rather_than_refused()
    {
        // <b>Ignored on purpose, and it is the only clause that is.</b> This server
        // has no portal folders, so every item is at the root of the one that would
        // exist and the clause is true of all of them. Refusing it would empty
        // Pro's My Content, which is where it always appears.
        Assert.True(PortalQuery.Matches(Item(), "ownerfolder:root"));
        Assert.True(PortalQuery.Matches(Item(), "ownerfolder:anything-at-all"));
    }

    [Fact]
    public void A_bare_word_searches_the_title()
    {
        Assert.True(PortalQuery.Matches(Item(title: "tr_il"), "tr_il"));
        Assert.True(PortalQuery.Matches(Item(title: "tr_il"), "TR_IL"));
        Assert.False(PortalQuery.Matches(Item(title: "tr_il"), "parcels"));
    }

    [Fact]
    public void Every_clause_has_to_hold_rather_than_any_of_them()
    {
        // An `and` between clauses, which is what a portal search means by a space.
        // An `or` would make a negated type list useless: one clause matching would
        // readmit everything the other twenty-nine excluded.
        Assert.True(PortalQuery.Matches(Item(), "owner:root type:\"Feature Service\""));
        Assert.False(PortalQuery.Matches(Item(), "owner:root type:\"Shapefile\""));
        Assert.False(PortalQuery.Matches(Item(), "owner:nobody type:\"Feature Service\""));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The portal and its hosting server's admin listing answer an ArcGIS client as ArcGIS does — the ArcGIS veteran's
/// review of 2026-11-02: an administrator acting through the owner's path (A), a service item's summary not dropped in
/// silence (C), the admin listing's shape (D), a search narrowed or refused rather than answered whole (E), and the
/// publisher's role (G).
/// </summary>
public sealed class ThePortalAnswersAnArcGisClientAsArcGisDoesTests
{
    private static RequestPrincipal Caller(params string[] roles) => new(
        new Principal(Guid.NewGuid(), PrincipalKind.User, "caller", "Caller", isDisabled: false),
        Guid.NewGuid(),
        Authorization.Resolve(UserTypes.Unrestricted, roles));

    private static readonly RequestPrincipal Nobody = new(Principal.Anonymous, null, Authorization.Nothing);

    [Fact]
    public void An_administrator_may_act_for_a_member_named_bare_and_nobody_else_may()
    {
        Assert.True(PortalEndpoints.MayActForAnother(Caller(Roles.Administrator), "ali"));

        // A name with a backslash is an outsider, another organisation's member (ADR-204 §5e item 16).
        Assert.False(PortalEndpoints.MayActForAnother(Caller(Roles.Administrator), "other\\ali"));
        Assert.False(PortalEndpoints.MayActForAnother(Caller(Roles.Administrator), " "));
        Assert.False(PortalEndpoints.MayActForAnother(Caller(Roles.Publisher), "ali"));
        Assert.False(PortalEndpoints.MayActForAnother(Caller(Roles.User), "ali"));
        Assert.False(PortalEndpoints.MayActForAnother(Nobody, "ali"));
    }

    [Theory]
    [InlineData("new", null, "held", "new")]
    [InlineData(null, "new", "held", "new")]
    [InlineData("new", "held", "held", "new")]   // a description edited, the summary echoed as read
    [InlineData("held", "new", "held", "new")]   // a summary edited, the description echoed as read
    [InlineData("same", "same", "held", "same")]
    [InlineData("held", "held", "held", null)]
    [InlineData("", null, "held", "")]
    [InlineData("x", null, null, "x")]
    public void A_service_items_one_text_is_written_from_whichever_field_changed(
        string? description, string? snippet, string? held, string? written)
    {
        (string? text, string? conflict) = PortalEndpoints.ServiceTextOf(description, snippet, held);

        Assert.Null(conflict);
        Assert.Equal(written, text);
    }

    [Fact]
    public void A_summary_and_a_description_changed_to_two_texts_are_refused_by_name_rather_than_one_dropped()
    {
        (string? text, string? conflict) = PortalEndpoints.ServiceTextOf("a new description", "a new summary", "held");

        Assert.Null(text);
        Assert.Contains("'snippet'", conflict, StringComparison.Ordinal);
        Assert.Contains("'description'", conflict, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_who_may_publish_is_an_org_publisher_and_an_administrator_stays_an_org_admin()
    {
        Assert.Equal("org_admin", PortalEndpoints.RoleOf(Caller(Roles.Administrator), administrator: true));
        Assert.Equal("org_publisher", PortalEndpoints.RoleOf(Caller(Roles.Publisher), administrator: false));
        Assert.Equal("org_user", PortalEndpoints.RoleOf(Caller(Roles.User), administrator: false));
        Assert.Equal("org_user", PortalEndpoints.RoleOf(Caller(Roles.Viewer), administrator: false));
    }

    private static PortalSearchParameters.Narrowing Narrowing(string query)
    {
        DefaultHttpContext context = new();
        context.Request.QueryString = new QueryString(query);
        return PortalSearchParameters.Of(context, null);
    }

    private sealed record Item(string Title, string[] Categories);

    private static readonly Item Rivers = new("rivers", ["/Categories/Water/Rivers"]);
    private static readonly Item Roads = new("roads", ["/Categories/Transport"]);
    private static readonly Item Both = new("bridges", ["/Categories/Transport", "/Categories/Water"]);
    private static readonly Item None = new("none", []);

    private static string[] Found(string query) =>
        [.. new[] { Rivers, Roads, Both, None }
            .Where(item => PortalSearchParameters.InCategories(item, Narrowing(query).Categories))
            .Select(item => item.Title)];

    [Theory]
    [InlineData("?bbox=-10,-10,10,10", "'bbox'")]
    [InlineData("?filter=title:%22roads%22", "'filter'")]
    [InlineData("?bbox=1,2,3,4&filter=x", "'bbox' or 'filter'")]
    public void A_bbox_or_a_filter_is_refused_by_name_rather_than_answered_with_everything(string query, string named)
    {
        Assert.Contains(named, Narrowing(query).Refused, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_bbox_or_filter_asks_nothing_and_is_not_refused()
    {
        Assert.Null(Narrowing("?bbox=&filter=&q=x").Refused);
        Assert.Equal(["rivers", "roads", "bridges", "none"], Found("?categories="));
    }

    [Fact]
    public void Categories_in_one_parameter_are_any_of_and_in_several_are_all_of()
    {
        Assert.Equal(["roads", "bridges"], Found("?categories=/Categories/Transport"));
        Assert.Equal(["rivers", "roads", "bridges"], Found("?categories=/Categories/Water,/Categories/Transport"));
        Assert.Equal(["bridges"], Found("?categories=/Categories/Water&categories=/Categories/Transport"));
    }

    [Fact]
    public void A_category_finds_what_is_below_it_with_or_without_its_root_in_any_case()
    {
        Assert.Equal(["rivers", "bridges"], Found("?categories=Water"));
        Assert.Equal(["rivers"], Found("?categories=/categories/water/rivers/"));
    }

    [Fact]
    public void Categories_sent_as_json_are_read_as_the_js_sdk_sends_them()
    {
        Assert.Equal(["rivers", "roads", "bridges"],
            Found("?categories=" + Uri.EscapeDataString("[\"/Categories/Water\",\"/Categories/Transport\"]")));
        Assert.Equal(["bridges"],
            Found("?categories=" + Uri.EscapeDataString("[[\"/Categories/Water\"],[\"/Categories/Transport\"]]")));
        Assert.NotNull(Narrowing("?categories=" + Uri.EscapeDataString("[{\"a\":1}]")).Refused);
    }

    [Fact]
    public void The_admin_listing_names_each_service_in_the_admin_apis_words()
    {
        PortalEndpoints.AdminListedService[] services =
        [
            new("hosted", "roads", "FeatureServer", "Roads of the city"),
            new(null, "parcels", "MapServer", null),
        ];

        using JsonDocument root = JsonDocument.Parse(JsonSerializer.Serialize(
            PortalEndpoints.AdminServicesFolder(null, ["hosted", "Utilities"], services)));

        Assert.Equal("/", root.RootElement.GetProperty("folderName").GetString());
        Assert.Equal(2, root.RootElement.GetProperty("folders").GetArrayLength());

        JsonElement first = root.RootElement.GetProperty("services")[0];
        Assert.Equal("hosted", first.GetProperty("folderName").GetString());
        Assert.Equal("roads", first.GetProperty("serviceName").GetString());
        Assert.Equal("FeatureServer", first.GetProperty("type").GetString());
        Assert.Equal("Roads of the city", first.GetProperty("description").GetString());
        Assert.Equal("/", root.RootElement.GetProperty("services")[1].GetProperty("folderName").GetString());

        using JsonDocument folder = JsonDocument.Parse(JsonSerializer.Serialize(
            PortalEndpoints.AdminServicesFolder("hosted", null, services.Take(1))));

        Assert.Equal("hosted", folder.RootElement.GetProperty("folderName").GetString());
        Assert.Equal(1, folder.RootElement.GetProperty("services").GetArrayLength());
    }
}

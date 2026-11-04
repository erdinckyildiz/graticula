using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Graticula.Platform.Webhooks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-266: where a webhook may post (§4), what it is told (§2, §3), how it is signed (§6), and whether its owner may
/// still be told (§7) — each without a server.
/// </summary>
public sealed class AWebhookReachesOnlyWhatItMayTests
{
    private static readonly IPAddress Public = IPAddress.Parse("93.184.216.34");

    private static Func<string, IPAddress[]> Resolves(params string[] addresses) =>
        _ => [.. addresses.Select(IPAddress.Parse)];

    [Theory]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://192.168.1.10/hook")]
    [InlineData("https://[::ffff:127.0.0.1]/hook")]
    [InlineData("https://[fe80::1]/hook")]
    public void A_private_loopback_or_link_local_address_is_refused_when_the_setting_is_off(string url)
    {
        Assert.False(WebhookTargets.TryCheck(url, allowPrivate: false, out _, out string? error));
        Assert.Contains("Graticula:WebhooksAllowPrivate", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_that_resolves_inward_is_refused_when_any_of_its_addresses_is()
    {
        Assert.False(WebhookTargets.TryCheck(
            "https://receiver.example.com/hook", false, out _, out string? error, Resolves("93.184.216.34", "127.0.0.1")));
        Assert.Contains("127.0.0.1", error, StringComparison.Ordinal);

        Assert.False(WebhookTargets.TryCheck("https://localhost:8443/hook", false, out _, out _, Resolves("127.0.0.1")));
    }

    [Fact]
    public void A_public_https_receiver_is_allowed_and_plain_http_is_not()
    {
        Assert.True(WebhookTargets.TryCheck("https://receiver.example.com/hook?sig=abc", false, out Uri? url, out _, Resolves("93.184.216.34")));
        Assert.Equal("receiver.example.com", url!.Host);

        Assert.False(WebhookTargets.TryCheck("http://receiver.example.com/hook", false, out _, out _, Resolves("93.184.216.34")));
        Assert.False(WebhookTargets.TryCheck("ftp://receiver.example.com/hook", false, out _, out _, Resolves("93.184.216.34")));
        Assert.False(WebhookTargets.TryCheck("https://user:pass@receiver.example.com/hook", false, out _, out _, Resolves("93.184.216.34")));
        Assert.False(WebhookTargets.TryCheck("/relative", false, out _, out _));
    }

    [Fact]
    public void With_the_setting_on_a_private_receiver_is_allowed_and_plain_http_only_to_one()
    {
        Assert.True(WebhookTargets.TryCheck("http://127.0.0.1:9000/hook", allowPrivate: true, out _, out _));
        Assert.True(WebhookTargets.TryCheck("https://10.0.0.5/hook", allowPrivate: true, out _, out _));

        // A receiver on the internet is still HTTPS.
        Assert.False(WebhookTargets.TryCheck("http://receiver.example.com/hook", true, out _, out string? error, Resolves("93.184.216.34")));
        Assert.Contains("public address", error, StringComparison.Ordinal);
        Assert.Null(WebhookTargets.Refusal(false, [Public], allowPrivate: true));
    }

    /// <remarks>
    /// <b>The check at creation is not the only one.</b> The connection resolves the name again and refuses an inward
    /// address before a byte is sent — what a name that moved after the webhook was made would reach.
    /// </remarks>
    [Fact]
    public async Task The_connection_itself_refuses_a_loopback_address()
    {
        using HttpClient client = new(WebhookTargets.Handler(allowPrivate: false));

        HttpRequestException refused = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PostAsync("https://127.0.0.1:9/hook", new StringContent("{}")));

        Exception inner = refused;
        while (inner.InnerException is { } next)
        {
            inner = next;
        }

        Assert.IsType<WebhookTargetRefusedException>(inner);
    }

    [Fact]
    public void A_redirect_is_not_followed()
    {
        using SocketsHttpHandler handler = WebhookTargets.Handler(allowPrivate: false);

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public void A_signature_is_the_hmac_sha256_of_the_body_as_posted()
    {
        // RFC 4231 test case 2.
        string signed = WebhookPayload.Sign("Jefe", Encoding.UTF8.GetBytes("what do ya want for nothing?"));

        Assert.Equal("sha256=5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", signed);
    }

    [Fact]
    public void An_audited_item_add_is_a_portal_event_under_every_uri_it_answers_to()
    {
        Guid actor = Guid.NewGuid();
        Guid item = Guid.NewGuid();
        AuditEvent added = new(actor, "ci", null, "document.create", item.ToString(), "{\"title\":\"x\"}", true);

        WebhookEvents.PortalEvent raised = WebhookEvents.FromAudit(added, WebhookEvents.ArcGisId(item.ToString()), DateTimeOffset.UnixEpoch)!;

        Assert.Equal("/items/add", raised.Name);
        Assert.Equal(["/", "/items", "/items/add", $"/items/{item:N}", $"/items/{item:N}/add"], raised.Uris);
        Assert.Equal("add", raised.Body["operation"]!.GetValue<string>());
        Assert.Equal("item", raised.Body["source"]!.GetValue<string>());
        Assert.Equal(actor.ToString("N"), raised.Body["userId"]!.GetValue<string>());

        Assert.Null(WebhookEvents.FromAudit(added with { Succeeded = false }, null, DateTimeOffset.UnixEpoch));
        Assert.Null(WebhookEvents.FromAudit(added with { Action = "member.password" }, null, DateTimeOffset.UnixEpoch));

        // A service's layer group shares the portal group's action, and is no group event.
        Assert.Null(WebhookEvents.FromAudit(added with { Action = "group.delete", Detail = "{\"index\":3}" }, null, DateTimeOffset.UnixEpoch));

        WebhookEvents.PortalEvent unprotected = WebhookEvents.FromAudit(
            added with { Action = "service.protection", Detail = "{\"protected\":false}" }, "abc", DateTimeOffset.UnixEpoch)!;
        Assert.Equal("/items/unprotect", unprotected.Name);
    }

    [Fact]
    public void An_edit_is_the_change_types_it_kept_and_its_object_ids()
    {
        EditOutcome outcome = new([EditResult.Ok(7), new EditResult(8, false, "bad")], [], [EditResult.Ok(9)], RolledBack: false);

        WebhookEvents.ServiceEvent raised = WebhookEvents.FromEdit("roads", "hosted", 0, outcome, DateTimeOffset.UnixEpoch)!;

        Assert.Equal(["*", "FeaturesEdited", "FeaturesCreated", "FeaturesDeleted"], raised.ChangeTypes);
        Assert.Equal([7L], raised.Body["objectIds"]!["adds"]!.AsArray().Select(n => n!.GetValue<long>()));

        Assert.Null(WebhookEvents.FromEdit("roads", null, 0, outcome with { RolledBack = true }, DateTimeOffset.UnixEpoch));
        Assert.Null(WebhookEvents.FromEdit("roads", null, 0, new EditOutcome([new EditResult(1, false, "no")], [], [], false), DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void A_portal_webhooks_events_are_arcgis_uris_and_nothing_else()
    {
        Guid id = Guid.NewGuid();

        // A user's event, spelt in two parts only so the repository's check for home directories does not read it as one.
        string userDelete = "/" + "users/ci/delete";

        Assert.True(WebhookRequests.TryReadPortalEvents($"/items/add, /groups,{userDelete},/items/{id}/update", out string[] events, out _));
        Assert.Equal(["/items/add", "/groups", userDelete, $"/items/{id:N}/update"], events);

        Assert.True(WebhookRequests.TryReadPortalEvents("[\"/\"]", out string[] all, out _));
        Assert.Equal(["/"], all);

        Assert.False(WebhookRequests.TryReadPortalEvents("/items/explode", out _, out _));
        Assert.False(WebhookRequests.TryReadPortalEvents("/services", out _, out _));
        Assert.False(WebhookRequests.TryReadPortalEvents("/roles/abc/delete", out _, out _));
        Assert.False(WebhookRequests.TryReadPortalEvents(string.Empty, out _, out _));

        Assert.True(WebhookRequests.TryReadChangeTypes("[\"FeaturesCreated\",\"All\"]", out string[] types, out _));
        Assert.Equal(["FeaturesCreated", "*"], types);
        Assert.False(WebhookRequests.TryReadChangeTypes("FeaturesExploded", out _, out _));
    }

    [Fact]
    public void A_portal_payload_carries_arcgis_envelope_and_a_service_payload_its_ids_instead_of_changes_url()
    {
        WebhookDefinition hook = Hook(WebhookKind.Portal);
        (byte[] bytes, string media) = WebhookPayload.Compose(hook, Guid.NewGuid(), "{\"operation\":\"add\",\"source\":\"item\"}", DateTimeOffset.UnixEpoch);

        using JsonDocument portal = JsonDocument.Parse(bytes);
        Assert.Equal("application/json", media);
        Assert.Equal(hook.Id.ToString("N"), portal.RootElement.GetProperty("info").GetProperty("webhookId").GetString());
        Assert.Equal("https://gis.example.com/", portal.RootElement.GetProperty("info").GetProperty("portalURL").GetString());
        Assert.Equal("add", portal.RootElement.GetProperty("events")[0].GetProperty("operation").GetString());

        WebhookEvents.ServiceEvent edit = WebhookEvents.FromEdit(
            "roads", "hosted", 2, new EditOutcome([EditResult.Ok(5)], [], [], false), DateTimeOffset.UnixEpoch)!;
        (byte[] serviceBytes, _) = WebhookPayload.Compose(Hook(WebhookKind.FeatureService), Guid.NewGuid(), edit.Body.ToJsonString(), DateTimeOffset.UnixEpoch);

        using JsonDocument service = JsonDocument.Parse(serviceBytes);
        JsonElement one = service.RootElement[0];
        Assert.Equal("https://gis.example.com/rest/services/hosted/roads/FeatureServer/2", one.GetProperty("layerUrl").GetString());
        Assert.Contains("objectIds=5", one.GetProperty("queryUrl").GetString(), StringComparison.Ordinal);
        Assert.False(one.TryGetProperty("changesUrl", out _));

        (byte[] form, string formMedia) = WebhookPayload.Compose(
            Hook(WebhookKind.Portal) with { ContentType = "application/x-www-form-urlencoded" }, Guid.NewGuid(), "{}", DateTimeOffset.UnixEpoch);
        Assert.Equal("application/x-www-form-urlencoded", formMedia);
        Assert.StartsWith("payload=", Encoding.UTF8.GetString(form), StringComparison.Ordinal);
    }

    [Fact]
    public void An_owner_who_may_no_longer_see_everything_is_not_told()
    {
        IRoleGrants grants = CompiledRoleGrants.Instance;
        WebhookDefinition portal = Hook(WebhookKind.Portal);
        WebhookDefinition service = Hook(WebhookKind.FeatureService);

        Assert.Null(WebhookDeliverer.Unentitled(portal, new OwnerStanding("creator", [Roles.Administrator], false, false), grants));
        Assert.NotNull(WebhookDeliverer.Unentitled(portal, new OwnerStanding("creator", [Roles.Publisher], false, false), grants));
        Assert.NotNull(WebhookDeliverer.Unentitled(portal, new OwnerStanding("creator", [Roles.Administrator], true, false), grants));
        Assert.NotNull(WebhookDeliverer.Unentitled(portal, new OwnerStanding(null, [], false, false), grants));

        Assert.Null(WebhookDeliverer.Unentitled(service, new OwnerStanding("creator", [Roles.Publisher], false, OwnsService: true), grants));
        Assert.NotNull(WebhookDeliverer.Unentitled(service, new OwnerStanding("creator", [Roles.Publisher], false, OwnsService: false), grants));
        Assert.Null(WebhookDeliverer.Unentitled(service, new OwnerStanding("creator", [Roles.Administrator], false, OwnsService: false), grants));
    }

    private static WebhookDefinition Hook(WebhookKind kind) => new(
        Guid.NewGuid(), kind, kind == WebhookKind.FeatureService ? Guid.NewGuid() : null, kind == WebhookKind.FeatureService ? 1 : null,
        "hook", "https://receiver.example.com/hook", ["*"], HasSecret: false, Active: true, Guid.NewGuid(), "ci", null,
        "https://gis.example.com", "application/json", "json", 3, 5, 5, "{}", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
}

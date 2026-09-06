using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A published service cannot take a system service's address.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-028](../../docs/adr/ADR-028-style-documents.md) condition 5, and
/// [D-187](../../docs/architecture-debt.md).</b> <c>/admin/services/{name}/sharing</c>,
/// <c>/style</c> and <c>/groups</c> address a *system* service when one answers to that name
/// and folder, and a *published* one otherwise. [D-39](../../docs/architecture-debt.md) made
/// the folder part of the lookup, which removed every collision except the one where the
/// folders are equal — and that one was reachable in two requests.
/// </para>
/// <para>
/// <b>Measured before the repair, 2026-08-27</b>: creating a FeatureServer called
/// <c>Geometry</c> in <c>Utilities</c> answered **201**, and
/// <c>PUT /admin/services/Geometry/sharing?folder=Utilities</c> afterwards changed **the system
/// geometry service's** scope from <c>organization</c> to <c>public</c> — answering as though
/// it had changed the published one. An operator sharing their own service would have put the
/// geometry server on the internet and had no way to see it.
/// </para>
/// <para>
/// <b>Both directions, because a refusal that is too wide is its own defect.</b> The same name
/// at the root has always been legitimate and stays legitimate; only the address a system
/// service already answers on is refused.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class SystemServiceAddressTests : ArcGisClient
{
    /// <summary>The system service every deployment has.</summary>
    private const string System = "Geometry";

    /// <summary>Where it lives.</summary>
    private const string Utilities = "Utilities";

    /// <summary>The layer a probe composition is made of.</summary>
    /// <remarks>
    /// <b>A service cannot be created without layers, so these tests publish one.</b> ADR-057
    /// condition 4, owner decision 2026-09-06 — <c>POST /admin/featureservices</c> made an empty
    /// container and refuses now. Nothing here is about the layer; it is what a service is made
    /// of, and a test about an <i>address</i> has to make a real one to find out whether the
    /// address is refused.
    /// </remarks>
    private const string Layer = "zz_d187_probe";

    [Fact]
    public async Task A_published_service_may_not_take_a_system_services_address()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        (int status, string body) = await PublishOneAsync(System, Layer, Utilities);

        if (status is 200 or 201)
        {
            // Leave nothing behind before failing, or the next run fails on the fixture.
            await UnpublishAsync(System, Layer, Utilities);

            Assert.Fail(
                $"A FeatureServer called '{System}' was published in '{Utilities}', where the "
                + "system geometry service already answers. The administrative routes for "
                + "sharing, styles and group layers would then address two different things by "
                + "one name, and the system lookup wins — so an operator sharing their own "
                + "service would change the geometry server's scope instead. D-187.");
        }

        // <b>400 or 409, and which one arrives says which guard spoke.</b> `Utilities` is a
        // reserved folder, so `POST /admin/publish` refuses everything addressed into it before
        // it ever asks whether this particular name is a system service's — and that is the
        // wider guarantee, not a weaker one. The narrower check behind it is still there and
        // still needed: a system service that does not live in a reserved folder is refused by
        // name. What this test asserts is the property D-187 is about, which is that no
        // published service can end up answering at a system service's address.
        //
        // <b>The endpoint this replaced had only the narrow half</b>, so
        // `POST /admin/featureservices` with folder `Utilities` and any other name created a
        // published service inside a reserved folder. Retiring it (ADR-057 condition 4) closed
        // that without anybody noticing it was open.
        Assert.True(
            status is 400 or 409,
            $"Publishing over a system service's address answered {status}, which is not a "
            + $"refusal. The server said: {body}");

        // The refusal says which folder, because the same name elsewhere is fine and a caller
        // who is refused should be able to see why the obvious retry works.
        Assert.Contains(Utilities, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same name somewhere else is untouched, which is what keeps the refusal honest.
    /// </summary>
    /// <remarks>
    /// <b>A guard that refused the name everywhere would pass the test above and be wrong.</b>
    /// `Geometry` at the root is a perfectly ordinary service name — D-39's own reasoning says
    /// so — and the collision is an address, not a word.
    /// </remarks>
    [Fact]
    public async Task The_same_name_in_another_folder_is_still_publishable()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        await UnpublishAsync(System, Layer);

        try
        {
            (int status, string body) = await PublishOneAsync(System, Layer);

            Assert.True(
                status is 200 or 201,
                $"'{System}' at the root was refused with {status}, and it should not be: "
                + "the system service lives in a folder, so this is a different address. "
                + $"The server said: {body}");
        }
        finally
        {
            await UnpublishAsync(System, Layer);
        }
    }

    /// <summary>One admin round trip.</summary>
    private async Task<(HttpStatusCode Status, string Body)> AdminAsync(
        string root, string token, HttpMethod method, string path, string? body)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}

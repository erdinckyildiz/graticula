using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Content is listed by how it reached you, and the four ways are distinguishable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked for by the owner, 2026-08-18:</b> *"content can be my own, from my groups, or shared in
/// organization. I think we need a public section as well to get publicly shared items."* Four sections,
/// and the point of testing them end to end rather than in the store is that each one is a *different
/// caller's* view of the same eleven services — a unit test would have to fake the thing being tested.
/// </para>
/// <para>
/// <b>And it exists because the first implementation got the sections wrong in a way that read as
/// correct.</b> <c>LayerAccess.Evaluate</c> answers *why are you allowed* and checks <c>Public</c>
/// before <c>Owner</c>, because for that question the cheapest sufficient reason is the right one. Used
/// as a *content scope* it put ten of this server's eleven services under **public** for the person who
/// owns all of them — so *My content* would have shown one item to an operator who published every one.
/// Ownership decides the section; the reason is reported beside it as <c>because</c>, which is the fact
/// that says who *else* can see it.
/// </para>
/// </remarks>
/// <summary>
/// Serialises the classes that walk the whole services catalogue against the ones that publish into it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A defect in the tests, and the second time this shape of one has cost a diagnosis.</b>
/// <c>ContentScopeConformanceTests</c> publishes `zz_scope_org` and `zz_scope_reach`, shares them, reads
/// them back and removes them. <c>ArcGisDiscoveryTests</c>, <c>ArcGisConsistencyTests</c> and
/// <c>MultiLayerServiceConformanceTests</c> each begin by asking the catalogue what services exist and
/// then opening every one. xUnit runs test classes in parallel, so the walkers see a fixture that is
/// being created or removed and report *404 — an ArcGIS client stops here* about a service that was
/// never meant to outlive one test.
/// </para>
/// <para>
/// Measured 2026-08-19: five failures in one run, two in the next, one in the third, always naming a
/// `zz_scope_*` service. A failure set that changes between runs of unchanged code is the signature,
/// and the same signature sent a commit message to blame the wrong debt earlier the same day.
/// </para>
/// <para>
/// <b>The rule, so that the next class does not have to be bitten first: a conformance class that
/// publishes anything into the catalogue belongs in this collection.</b> Four were added when the
/// failures were traced; `HostedDeleteTests` and `ArchiveFormatRefusalTests` were added an hour later
/// after a run failed on `zz_drop_…`, which is the same fault with a different fixture name. The
/// membership is the whole mechanism — there is nothing to configure and nothing that detects a class
/// which forgot.
/// </para>
/// <para>
/// <b>A collection rather than a filter on the walk.</b> Skipping names that look like fixtures would
/// hide the one thing these tests exist to check — that *every* service in the catalogue can actually
/// be opened — and would keep hiding it after somebody published a real service called `zz_anything`.
/// The cost is that four classes no longer run in parallel with each other; they are 11 seconds
/// together.
/// </para>
/// </remarks>
[CollectionDefinition("catalogue walk")]
public sealed class CatalogueWalkState
{
}

[Collection("catalogue walk")]
public sealed class ContentScopeConformanceTests : ArcGisClient
{
    private const string Stranger = "zz_scope_stranger";
    private const string Password = "Scope!2026xyz";

    /// <summary>
    /// An owner's own public service is theirs, not filed under public.
    /// </summary>
    /// <remarks>
    /// <b>The regression this whole class is for.</b> The assertion is on <c>scope</c> and
    /// <c>because</c> disagreeing: a service somebody owns and has published to the world is *both*
    /// theirs and public, and a listing that can only say one of those has to say the one that decides
    /// which section it belongs in.
    /// </remarks>
    [Fact]
    public async Task An_owners_public_service_is_listed_as_theirs_and_says_it_is_public()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        JsonElement listing = await ContentAsync(root, token!);

        JsonElement[] items = listing.GetProperty("items").EnumerateArray().ToArray();

        Assert.False(items.Length == 0, "The administrator can see no content on this server.");

        JsonElement[] ownedAndPublic = items
            .Where(i => i.GetProperty("scope").GetString() == "mine"
                        && i.GetProperty("because").GetString() == "public")
            .ToArray();

        Assert.False(
            ownedAndPublic.Length == 0,
            "This server has no service that its administrator both owns and has made public, which "
            + "is the case this test is about.");

        // Nothing owned may be filed anywhere else, whatever its sharing scope is.
        Assert.DoesNotContain(
            items.Where(i => i.GetProperty("scope").GetString() != "mine"),
            i => i.GetProperty("because").GetString() == "owner");

        // And the counts agree with the items, because a section header that disagrees with its own
        // list is worse than either being wrong alone.
        JsonElement counts = listing.GetProperty("counts");

        foreach (string scope in new[] { "mine", "group", "organization", "public", "administrative" })
        {
            int said = counts.GetProperty(scope).GetInt32();
            int found = items.Count(i => i.GetProperty("scope").GetString() == scope);

            Assert.Equal(found, said);
        }
    }

    /// <summary>
    /// Each of the four scopes is reachable, and a stranger sees only what reached them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One caller, four states, measured in order.</b> A newly created member owns nothing, so their
    /// listing is public-only; putting them in a group with a <c>group</c>-scoped service adds the group
    /// section; setting a service to <c>organization</c> adds that one.
    /// </para>
    /// <para>
    /// <b>It makes its own services, and the first version borrowed the demo ones.</b> That version
    /// changed two demo services' sharing scopes and put them back — correctly, from values it had
    /// read — and still broke two unrelated conformance tests, because the suites run against one
    /// shared server and <c>QueryCapabilityConformanceTests</c> read <c>look_buildings</c> during the
    /// window when it was <c>group</c>-scoped and got a 404. That is
    /// <see href="../../docs/architecture-debt.md">D-75</see> exactly: the HTTP-backed suites are
    /// serialised against each other only by luck. **A test that mutates shared state is a test that
    /// fails somebody else**, and restoring the value afterwards does not help — the window is the
    /// defect. So this one publishes two empty services of its own and mutates only those.
    /// </remarks>
    [Fact]
    public async Task A_stranger_sees_public_then_group_then_organization_as_each_arrives()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        // ------------------------------------------------------------------ a member who owns nothing
        // <b>Removed first, because this test leaked one the first time it failed.</b> A falsification
        // run tripped an assertion that sits *outside* the try, so `finally` never ran and the next run
        // failed with *"could not create the probe member"* — a test whose second failure hides its
        // first. Deleting before creating makes the run independent of how the last one ended.
        await AdminAsync(
            root, token!, HttpMethod.Delete, $"/admin/members/{Stranger}?deleteOwned=true", null);

        string? generated = await CreateMemberAsync(root, token!, Stranger);

        Assert.False(generated is null, "Could not create the probe member.");

        const string group = "zz_scope_group";

        // Declared before the try so the cleanup can see them, and null until read.
        string? forGroup = null;
        string? forOrganization = null;

        try
        {
            await SetPasswordAsync(root, generated!, Password);

            (string? theirs, string why) = await SignInAsync(root, Stranger, Password);

            Assert.False(theirs is null, $"The probe member could not sign in. {why}");

            // <b>Two services of its own, so nothing another suite reads can move.</b> Empty ones:
            // this test is about how a service *reaches* somebody, and an empty service reaches people
            // the same way a full one does. `POST /admin/featureservices` makes a container with no
            // layers, which is exactly enough.
            forGroup = "zz_scope_reach";
            forOrganization = "zz_scope_org";

            foreach (string one in new[] { forGroup, forOrganization })
            {
                await AdminAsync(
                    root, token!, HttpMethod.Delete,
                    $"/admin/featureservices/{Uri.EscapeDataString(one)}", null);

                (System.Net.HttpStatusCode put, string putWhy) = await AdminAsync(
                    root, token!, HttpMethod.Post, "/admin/featureservices",
                    JsonSerializer.Serialize(new { name = one, sharing = "private" }));

                Assert.True(
                    put is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.Created,
                    $"{(int)put} {putWhy}");
            }

            JsonElement before = await ContentAsync(root, theirs!);

            Assert.Equal(0, before.GetProperty("counts").GetProperty("mine").GetInt32());
            Assert.Equal(0, before.GetProperty("counts").GetProperty("group").GetInt32());

            // ------------------------------------------------------------------ the group section
            await AdminAsync(root, token!, HttpMethod.Post, "/admin/groups",
                JsonSerializer.Serialize(new { name = group, title = "Scope probe" }));

            await AdminAsync(root, token!, HttpMethod.Put,
                $"/admin/groups/{group}/members/{Stranger}",
                JsonSerializer.Serialize(new { manager = false }));

            await ShareWithGroupAsync(root, token!, group, forGroup);
            await SetSharingAsync(root, token!, forGroup, "group");

            JsonElement withGroup = await ContentAsync(root, theirs!);

            Assert.Equal(1, withGroup.GetProperty("counts").GetProperty("group").GetInt32());
            Assert.Equal(0, withGroup.GetProperty("counts").GetProperty("mine").GetInt32());

            // <b>And the group it came through is named.</b> A scope that says *one of your groups*
            // without saying which is a label rather than an answer.
            Assert.True(
                withGroup.GetProperty("groups").EnumerateObject().Any(g => g.Name == group),
                "The listing does not name the group the item arrived through.");

            // ------------------------------------------------------------------ the organization one
            await SetSharingAsync(root, token!, forOrganization, "organization");

            JsonElement withOrganization = await ContentAsync(root, theirs!);

            Assert.Equal(
                1, withOrganization.GetProperty("counts").GetProperty("organization").GetInt32());

            // <b>And its own service is named, not merely counted.</b> A count of one over the wrong
            // service would pass a bare assertion.
            Assert.Contains(
                forOrganization!,
                withOrganization.GetProperty("items").EnumerateArray()
                    .Where(i => i.GetProperty("scope").GetString() == "organization")
                    .Select(i => i.GetProperty("name").GetString()));

            // Still nothing of their own, which is the section that must not fill up by accident.
            Assert.Equal(0, withOrganization.GetProperty("counts").GetProperty("mine").GetInt32());

            // ------------------------------------------------------------------ and each item can be drawn
            // The cover is the only thing standing in for a thumbnail here — a service holds no
            // geometry, so a picture of one has to come from a layer. An item without one is a service
            // with no layers, and the listing must say so rather than offering an address that 404s.
            foreach (JsonElement item in withOrganization.GetProperty("items").EnumerateArray())
            {
                int layers = item.GetProperty("layers").GetInt32();
                bool drawable = item.GetProperty("cover").ValueKind != JsonValueKind.Null;

                Assert.Equal(layers > 0, drawable);
            }
        }
        finally
        {
            // <b>Removed rather than restored, because they are this test's own.</b> Nothing here
            // belonged to the server before the run, so there is no earlier value to put back — which
            // is the property that makes the test safe to run beside the others.
            foreach (string? one in new[] { forGroup, forOrganization })
            {
                if (one is not null)
                {
                    await AdminAsync(
                        root, token!, HttpMethod.Delete,
                        $"/admin/featureservices/{Uri.EscapeDataString(one)}", null);
                }
            }

            await AdminAsync(root, token!, HttpMethod.Delete, $"/admin/groups/{group}", null);
            await AdminAsync(root, token!, HttpMethod.Delete,
                $"/admin/members/{Stranger}?deleteOwned=true", null);
        }
    }

    /// <summary>
    /// An anonymous caller is refused, and told where the public things are.
    /// </summary>
    /// <remarks>
    /// <b>The sibling listing shipped this defect once.</b> <c>/content/layers</c> checked
    /// <c>Principal.Id == Guid.Empty</c> for anonymity, which never matches — ADR-015 §2a made
    /// anonymous a real principal — so an unauthenticated caller got 200 and a list of public layers
    /// under a heading that says *mine*. Measured here rather than reasoned about, for the same reason.
    /// </remarks>
    [Fact]
    public async Task An_anonymous_caller_is_refused_and_pointed_at_the_directory()
    {
        string root = await RequireServerAsync();

        (HttpStatusCode status, string body) = await AnonymousAsync("/content/items");

        Assert.Equal(HttpStatusCode.Unauthorized, status);

        Assert.Contains("/rest/services", body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------ helpers

    private async Task<JsonElement> ContentAsync(string root, string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{root}/content/items");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

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

    private async Task<string?> CreateMemberAsync(string root, string token, string name)
    {
        (HttpStatusCode status, string body) = await AdminAsync(
            root, token, HttpMethod.Post, "/admin/members",
            JsonSerializer.Serialize(new { name, role = "user", userType = "creator" }));

        if (status is not (HttpStatusCode.OK or HttpStatusCode.Created))
        {
            return null;
        }

        return JsonDocument.Parse(body).RootElement.TryGetProperty("password", out JsonElement p)
            ? p.GetString()
            : null;
    }

    /// <summary>Signs the probe member in and changes its password to a known one.</summary>
    /// <remarks>
    /// <b>Every step reports now — [D-177](../../docs/architecture-debt.md).</b> This method
    /// used to sign in, return silently if that failed, post the password change, and then
    /// write `_ = response.StatusCode;` — reading the outcome of the one operation it exists
    /// to perform and discarding it. Both silent exits land on the caller's *The probe member
    /// could not sign in*, which names the step **after** the one that failed. A CI run on
    /// 2026-08-26 failed exactly there and the log could say nothing more.
    /// </remarks>
    private async Task SetPasswordAsync(string root, string generated, string wanted)
    {
        (string? first, string why) = await SignInAsync(root, Stranger, generated);

        Assert.True(
            first is not null,
            "The probe member could not sign in with the password its creation returned, so its "
            + $"password was never changed. {why}");

        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/rest/auth/password");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", first);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { currentPassword = generated, newPassword = wanted }),
            Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await Http.SendAsync(request);
        string said = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"Changing the probe member's password returned {(int)response.StatusCode}. "
            + Explain(said));
    }

    /// <summary>Signs in, and says why when it cannot.</summary>
    /// <param name="root">The server.</param>
    /// <param name="name">Who.</param>
    /// <param name="password">Their password.</param>
    /// <returns>The token, or null with the reason beside it.</returns>
    private async Task<(string? Token, string Why)> SignInAsync(
        string root, string name, string password)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/rest/auth/login");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { name, password }), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            // <b>401 and 429 are different problems.</b> The first is the credential; the
            // second is this server's per-address throttle, which a suite signing in from one
            // address can reach on its own. Reporting only *could not sign in* makes them one
            // symptom with two repairs.
            return (null, $"Signing in as '{name}' returned {(int)response.StatusCode}. "
                + Explain(body));
        }

        JsonDocument document = JsonDocument.Parse(body);

        return document.RootElement.TryGetProperty("token", out JsonElement token)
            ? (token.GetString(), string.Empty)
            : (null, $"Signing in as '{name}' returned 200 with no token in it. "
                + Explain(body));
    }

    /// <summary>The server's own words, trimmed for a failure message.</summary>
    private static string Explain(string body) =>
        string.IsNullOrWhiteSpace(body)
            ? "The response had no body to explain it."
            : "The server said: " + (body.Length <= 300 ? body : body[..300] + "…");

    private async Task ShareWithGroupAsync(string root, string token, string group, string qualified)
    {
        (string folder, string bare) = Split(qualified);

        await AdminAsync(
            root, token, HttpMethod.Put,
            $"/admin/groups/{group}/items/{Uri.EscapeDataString(bare)}"
            + $"?folder={Uri.EscapeDataString(folder)}",
            null);
    }

    private async Task SetSharingAsync(string root, string token, string qualified, string scope)
    {
        (string folder, string bare) = Split(qualified);

        await AdminAsync(
            root, token, HttpMethod.Put,
            $"/admin/services/{Uri.EscapeDataString(bare)}/sharing"
            + $"?folder={Uri.EscapeDataString(folder)}",
            JsonSerializer.Serialize(new { sharing = scope }));
    }

    private static (string Folder, string Bare) Split(string qualified)
    {
        int cut = qualified.LastIndexOf('/');

        return cut < 0 ? (string.Empty, qualified) : (qualified[..cut], qualified[(cut + 1)..]);
    }
}

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
    /// <b>One caller, four states, measured in order.</b> A newly created member owns nothing, so their
    /// listing is public-only; putting them in a group with a <c>group</c>-scoped service adds the group
    /// section; setting a service to <c>organization</c> adds that one. Every change is undone at the
    /// end, and the two services' original scopes are **read before they are changed** rather than
    /// assumed — restoring a value you never recorded is how this repository once set a service to
    /// public by accident and failed three conformance tests.
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
        string? wasGroupScope = null;
        string? wasOrgScope = null;

        try
        {
            await SetPasswordAsync(root, generated!, Password);

            string? theirs = await SignInAsync(root, Stranger, Password);

            Assert.False(theirs is null, "The probe member could not sign in.");

            // A group and a service to share into it, both taken from what is already here.
            JsonElement mine = await ContentAsync(root, token!);

            JsonElement[] candidates = mine.GetProperty("items").EnumerateArray()
                .Where(i => i.GetProperty("scope").GetString() == "mine")
                .ToArray();

            Assert.False(
                candidates.Length < 2,
                "Fewer than two services belong to the administrator, so this test cannot make one "
                + "group-scoped and another organization-scoped.");

            forGroup = candidates[0].GetProperty("name").GetString()!;
            forOrganization = candidates[1].GetProperty("name").GetString()!;

            // <b>Read before written.</b> Both are put back exactly as they were found.
            wasGroupScope = candidates[0].GetProperty("sharing").GetString()!;
            wasOrgScope = candidates[1].GetProperty("sharing").GetString()!;

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
            // <b>Only what was actually read is put back.</b> Restoring a value never recorded is how
            // this repository once set a service to public by accident and failed three conformance
            // tests; a null here means the test did not get far enough to change it.
            if (forGroup is not null && wasGroupScope is not null)
            {
                await SetSharingAsync(root, token!, forGroup, wasGroupScope);
            }

            if (forOrganization is not null && wasOrgScope is not null)
            {
                await SetSharingAsync(root, token!, forOrganization, wasOrgScope);
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

    private async Task SetPasswordAsync(string root, string generated, string wanted)
    {
        string? first = await SignInAsync(root, Stranger, generated);

        if (first is null)
        {
            return;
        }

        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/rest/auth/password");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", first);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { currentPassword = generated, newPassword = wanted }),
            Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await Http.SendAsync(request);

        _ = response.StatusCode;
    }

    private async Task<string?> SignInAsync(string root, string name, string password)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/rest/auth/login");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { name, password }), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await Http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("token", out JsonElement token)
            ? token.GetString()
            : null;
    }

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

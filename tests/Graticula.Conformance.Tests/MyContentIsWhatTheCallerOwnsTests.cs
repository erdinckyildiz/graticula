using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A portal's per-owner content listing, against a server with two owners in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-127](../../docs/open-questions.md)'s ownership half, closed 2026-09-09.</b>
/// `/sharing/rest/content/users/{name}` answered with every item the caller could
/// <em>see</em> rather than the ones they <em>own</em>, so Pro's <em>My Content</em>
/// showed a colleague's services beside the caller's own. The register named the trigger
/// exactly: <em>the moment two people publish on one server, My Content is wrong for both
/// of them.</em>
/// </para>
/// <para>
/// <b>Which is why this test makes a second member rather than asserting against the
/// fixture.</b> On a single-operator deployment the two sets coincide and every
/// assertion about them passes against the defect — the old behaviour and the new one
/// are indistinguishable until somebody else publishes something. So the second owner is
/// the test, and the fixture is put back in a `finally`.
/// </para>
/// <para>
/// <b>It asserts the other half too, because that one is a decision rather than a
/// bug.</b> An item owned by somebody else reports the product's name in `owner` and not
/// theirs: this surface has no member directory and is reachable anonymously for public
/// items, so naming an owner would publish usernames to whoever can see the service.
/// A test that only checked the listing would let that quietly become a real name.
/// </para>
/// </remarks>
[Trait("Needs", "RunningHost")]
public sealed class MyContentIsWhatTheCallerOwnsTests : ArcGisClient
{
    /// <summary>The second owner, made and removed by this test.</summary>
    private const string Member = "q127_second_owner";

    /// <summary>What they publish, named so a leftover is obviously this test's.</summary>
    private const string Service = "q127_second_owner_service";

    /// <summary>Two owners, two listings, and neither one is the catalogue.</summary>
    /// <returns>The task.</returns>
    [Fact]
    public async Task My_content_is_what_the_caller_owns_and_not_what_they_can_see()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string administrator = await AdministratorNameAsync(root, token!);

        // <b>The control, before there is a second owner.</b> Whatever the administrator
        // owns now is what they must still own at the end — a listing that grows by the
        // other member's service is the defect this closes.
        int mine = await OwnedCountAsync(root, token!, administrator);

        (HttpStatusCode made, string said) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/members",
            token!,
            JsonSerializer.Serialize(
                new { name = Member, displayName = "Q-127", role = "publisher", userType = "creator" }));

        if (made == HttpStatusCode.Conflict)
        {
            // <b>A leftover from a run that was interrupted between creating the member and
            // reaching the `finally`.</b> Clearing it and trying once is right where
            // refusing would be wrong: the name is this test's own, nothing else uses it,
            // and a suite that can only run once is a suite nobody runs.
            await RequestAsync(
                HttpMethod.Delete,
                $"{root}/admin/members/{Member}?deleteOwned=true",
                token!,
                json: null);

            (made, said) = await RequestAsync(
                HttpMethod.Post,
                $"{root}/admin/members",
                token!,
                JsonSerializer.Serialize(new
                {
                    name = Member,
                    displayName = "Q-127",
                    role = "publisher",
                    userType = "creator",
                }));
        }

        Assert.True(
            made == HttpStatusCode.Created,
            $"Creating the second member answered {(int)made}: {said}. Without one this test "
            + "cannot tell the fixed behaviour from the defect, so it fails rather than skips.");

        try
        {
            string second = await SignInAsync(root, said);

            await PublishAsAsync(root, token!, second);

            // ---- the listing each of them gets ----
            int administratorOwns = await OwnedCountAsync(root, token!, administrator);
            JsonElement theirs = await ContentAsync(root, second, Member);

            Assert.True(
                administratorOwns == mine,
                $"`{administrator}` owned {mine} items before the second member published and "
                + $"{administratorOwns} after. My Content is per-owner; a listing that grows "
                + "when somebody else publishes is the catalogue wearing another name.");

            Assert.True(
                theirs.GetProperty("total").GetInt32() == 1,
                $"`{Member}` published one service and their content listing says "
                + $"{theirs.GetProperty("total").GetInt32()}.");

            foreach (JsonElement item in theirs.GetProperty("items").EnumerateArray())
            {
                Assert.Equal(Member, item.GetProperty("owner").GetString());
            }

            // ---- and the search, which is deliberately not per-owner ----
            JsonElement found = await SearchAsync(root, token!);

            Assert.Contains(
                found.GetProperty("results").EnumerateArray(),
                i => i.GetProperty("title").GetString() == Service);

            JsonElement other = found.GetProperty("results").EnumerateArray()
                .First(i => i.GetProperty("title").GetString() == Service);

            Assert.True(
                other.GetProperty("owner").GetString() == "graticula",
                "A service owned by another member reports "
                + $"`{other.GetProperty("owner").GetString()}` to the administrator. That field "
                + "is the product's name by decision, not by omission: this surface has no "
                + "member directory and answers anonymously for public items, so a real name "
                + "here publishes the user list through a door nobody reviewed (Q-127).");
        }
        finally
        {
            // <b>`?deleteOwned=true`, which the server itself named.</b> A member who owns
            // content cannot simply be removed — ADR-015 §6c — and the refusal says what to
            // do instead: transfer the content or take it along. The first version of this
            // cleanup deleted a `/admin/services/{name}` route that does not exist and then
            // a member who could not go, so it left both behind and every later run failed
            // with a 409 before asserting anything. The service is `hosted/...` rather than
            // the bare name, which is the other half of why addressing the layer directly
            // was the wrong move.
            await RequestAsync(
                HttpMethod.Delete,
                $"{root}/admin/members/{Member}?deleteOwned=true",
                token!,
                json: null);
        }
    }

    /// <summary>Whoever the administrator credential belongs to.</summary>
    private async Task<string> AdministratorNameAsync(string root, string token)
    {
        (HttpStatusCode status, string body) = await RequestAsync(
            HttpMethod.Get, $"{root}/sharing/rest/community/self?f=json", token, json: null);

        Assert.True(status == HttpStatusCode.OK, $"community/self answered {(int)status}: {body}");

        using JsonDocument document = JsonDocument.Parse(body);

        return document.RootElement.GetProperty("username").GetString()!;
    }

    /// <summary>Signs the new member in, replacing the password they must replace.</summary>
    private async Task<string> SignInAsync(string root, string created)
    {
        using JsonDocument made = JsonDocument.Parse(created);

        string issued = made.RootElement.GetProperty("password").GetString()!;

        string first = await LoginAsync(root, issued);

        (HttpStatusCode changed, string said) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/rest/auth/password",
            first,
            JsonSerializer.Serialize(new { currentPassword = issued, newPassword = issued + "X1" }));

        Assert.True(
            changed == HttpStatusCode.OK,
            $"The new member could not replace their issued password: {(int)changed} {said}. "
            + "Nothing else answers until they do, so the rest of this test cannot run.");

        return await LoginAsync(root, issued + "X1");
    }

    /// <summary>A token for the second member.</summary>
    private async Task<string> LoginAsync(string root, string password)
    {
        (HttpStatusCode status, string body) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/rest/auth/login",
            token: string.Empty,
            JsonSerializer.Serialize(new { name = Member, password }));

        Assert.True(status == HttpStatusCode.OK, $"Signing in as `{Member}` answered {(int)status}: {body}");

        using JsonDocument document = JsonDocument.Parse(body);

        return document.RootElement.GetProperty("token").GetString()!;
    }

    /// <summary>The second member publishes something of their own.</summary>
    /// <remarks>
    /// <b>The administrator reads the data source and the member publishes into it</b>, which
    /// is not a convenience: `publisher` does not carry `content:registerDataStore`, so the
    /// member cannot list data sources at all. Publishing into one they were handed is exactly
    /// the workflow the role is for, and doing it with the administrator's token throughout
    /// would have made every item the administrator's and tested nothing.
    /// </remarks>
    /// <param name="root">The server.</param>
    /// <param name="administrator">A token that may read the data sources.</param>
    /// <param name="token">The second member's token, which publishes.</param>
    /// <returns>The task.</returns>
    private async Task PublishAsAsync(string root, string administrator, string token)
    {
        (HttpStatusCode listed, string sources) = await RequestAsync(
            HttpMethod.Get, $"{root}/admin/datasources", administrator, json: null);

        Assert.True(
            listed == HttpStatusCode.OK,
            $"The administrator cannot read the data sources: {(int)listed} {sources}");

        using JsonDocument document = JsonDocument.Parse(sources);

        JsonElement first = document.RootElement.GetProperty("dataSources").EnumerateArray().First();

        (HttpStatusCode published, string said) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/layers",
            token,
            JsonSerializer.Serialize(new
            {
                name = Service,
                dataSourceId = first.GetProperty("id").GetString(),
                schemaName = Environment.GetEnvironmentVariable("GRATICULA_TEST_SCHEMA") ?? "hosted",
                tableName = Environment.GetEnvironmentVariable("GRATICULA_TEST_TABLE"),
                geometryColumn = Environment.GetEnvironmentVariable("GRATICULA_TEST_GEOMETRY") ?? "geom",
                identityColumn = Environment.GetEnvironmentVariable("GRATICULA_TEST_IDENTITY") ?? "objectid",
                objectIdColumn = Environment.GetEnvironmentVariable("GRATICULA_TEST_IDENTITY") ?? "objectid",
                geometryType = Environment.GetEnvironmentVariable("GRATICULA_TEST_GEOMETRYTYPE")
                    ?? "Polygon",
                srid = 3857,
                sharing = "public",
                serviceName = Service,
            }));

        Assert.True(
            published == HttpStatusCode.Created,
            $"The second member could not publish: {(int)published} {said}. Set "
            + "GRATICULA_TEST_TABLE (and GRATICULA_TEST_SCHEMA) to a publishable table, because "
            + "this test needs a second owner and cannot invent one.");
    }

    /// <summary>How many items a caller's own content listing holds.</summary>
    private async Task<int> OwnedCountAsync(string root, string token, string username) =>
        (await ContentAsync(root, token, username)).GetProperty("total").GetInt32();

    /// <summary>One caller's content listing.</summary>
    private async Task<JsonElement> ContentAsync(string root, string token, string username)
    {
        (HttpStatusCode status, string body) = await RequestAsync(
            HttpMethod.Get,
            $"{root}/sharing/rest/content/users/{Uri.EscapeDataString(username)}?f=json",
            token,
            json: null);

        Assert.True(status == HttpStatusCode.OK, $"content/users answered {(int)status}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>The catalogue as the portal search reports it.</summary>
    private async Task<JsonElement> SearchAsync(string root, string token)
    {
        (HttpStatusCode status, string body) = await RequestAsync(
            HttpMethod.Get, $"{root}/sharing/rest/search?q=&f=json", token, json: null);

        Assert.True(status == HttpStatusCode.OK, $"search answered {(int)status}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}

using System;
using System.IO;
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
/// A removed member's content either changes hands or goes for good, and nothing is left owned by nobody — owner decision
/// 2026-10-19 (ADR-203 condition 1, D-306).
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's words:</b> *"sahipsiz nesne olamaz. Ya birisine move edilmeli, ya da silinmeli, başka çare yok."* The
/// catalogue already asked and already moved; what this pins is what was left behind on the way. Measured before the
/// change, a delete left the hosted table in the datastore, and the member's recycle bin entries stayed in the bin with
/// nobody who could restore them; a member whose only content was in the bin was removed without being asked.
/// </para>
/// <para>
/// <b>Each thing is made by the administrator and handed to the member</b>, through Change owner, so the test needs no
/// second sign-in to publish, and the leaver signs in only to prove nothing.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class ARemovedMemberLeavesNothingOwnerlessTests : ArcGisClient
{
    private const string Leaver = "zz_leaver";
    private const string Heir = "zz_heir";
    private const string HeirPassword = "a-long-enough-test-password-306";

    [Fact]
    public async Task Removing_with_its_content_takes_the_tables_and_the_recycle_bin_with_it()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string kept = $"zz_lva_{Guid.NewGuid():N}"[..16];
        string binned = $"zz_lvc_{Guid.NewGuid():N}"[..16];

        try
        {
            await MakeMemberAsync(Leaver);

            string table = await ImportCorpusAsync(root, token!, kept);
            Assert.True(await DatastoreHasAsync(root, token!, table), $"'{table}' is not in the datastore after its import.");
            await GiveAsync(kept, Leaver);

            // Something of theirs already in the bin: handed over, then deleted by the administrator.
            await ImportCorpusAsync(root, token!, binned);
            await GiveAsync(binned, Leaver);
            (int deleted, string deletedBody) = await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{binned}?folder=hosted&drop=true");
            Assert.True(deleted == 200, $"deleting into the bin: {deleted} {deletedBody}");
            string entry = JsonDocument.Parse(deletedBody).RootElement.GetProperty("recycled").GetString()!;

            // Asked, as before.
            (int asked, string why) = await AdminAsync(HttpMethod.Delete, $"/admin/members/{Leaver}");
            Assert.True(asked == 409, $"removing without saying what to do: {asked} {why}");
            Assert.Contains("recycle bin", why, StringComparison.OrdinalIgnoreCase);

            (int removed, string removedBody) = await AdminAsync(HttpMethod.Delete, $"/admin/members/{Leaver}?deleteOwned=true");
            Assert.True(removed == 200, $"removing with the content: {removed} {removedBody}");

            // The table went with the service, rather than staying in the datastore published by nothing.
            Assert.False(await DatastoreHasAsync(root, token!, table),
                $"'{table}' is still in the datastore after its owner was removed with their content. {removedBody}");

            // And their bin went too: nobody is left who could restore it, and nobody may own it.
            (int listed, string bin) = await AdminAsync(HttpMethod.Get, "/admin/recycle-bin");
            Assert.True(listed == 200, $"listing the bin: {listed} {bin}");
            Assert.DoesNotContain(entry, bin, StringComparison.Ordinal);
            Assert.DoesNotContain(kept, bin, StringComparison.Ordinal);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{Leaver}?deleteOwned=true");
            await ClearAsync(kept);
            await ClearAsync(binned);
        }
    }

    [Fact]
    public async Task Transferring_hands_the_recycle_bin_to_the_receiver_who_can_restore_it()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string binned = $"zz_lvd_{Guid.NewGuid():N}"[..16];
        string leaver = Leaver + "2";

        try
        {
            await MakeMemberAsync(leaver);
            string heir = await MakeMemberAsync(Heir, HeirPassword);

            await ImportCorpusAsync(root, token!, binned);
            string item = (await GetJsonAsync($"/rest/services/hosted/{binned}/FeatureServer")).GetProperty("serviceItemId").GetString()!;
            await GiveAsync(binned, leaver);
            (int deleted, string deletedBody) = await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{binned}?folder=hosted&drop=true");
            Assert.True(deleted == 200, $"deleting into the bin: {deleted} {deletedBody}");
            string entry = JsonDocument.Parse(deletedBody).RootElement.GetProperty("recycled").GetString()!;

            // The bin is all they own, and that is still something to ask about.
            (int asked, string why) = await AdminAsync(HttpMethod.Delete, $"/admin/members/{leaver}");
            Assert.True(asked == 409, $"a member whose only content is in the bin was removed without being asked: {asked} {why}");

            (int moved, string movedBody) = await AdminAsync(HttpMethod.Delete, $"/admin/members/{leaver}?transferTo={Heir}");
            Assert.True(moved == 200, $"removing with a transfer: {moved} {movedBody}");

            // The receiver sees it as theirs, and brings it back as theirs.
            (HttpStatusCode listed, string bin) = await RequestAsync(HttpMethod.Get, $"{root}/admin/recycle-bin", heir, null);
            Assert.True(listed == HttpStatusCode.OK, $"the receiver listing the bin: {listed} {bin}");
            Assert.Contains(entry, bin, StringComparison.Ordinal);

            (HttpStatusCode restored, string restoredBody) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/recycle-bin/{entry}/restore", heir, null);
            Assert.True(restored == HttpStatusCode.OK, $"the receiver restoring: {restored} {restoredBody}");

            // Read as the receiver: the portal names an owner only to the owner (MyContentIsWhatTheCallerOwnsTests).
            (HttpStatusCode owned, string ownedBody) = await RequestAsync(
                HttpMethod.Get, $"{root}/sharing/rest/content/items/{item}?f=json", heir, null);
            Assert.True(owned == HttpStatusCode.OK, $"reading the restored item: {owned} {ownedBody}");
            Assert.Equal(Heir, JsonDocument.Parse(ownedBody).RootElement.GetProperty("owner").GetString());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{leaver}?deleteOwned=true");
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{Heir}?deleteOwned=true");
            await ClearAsync(binned);
        }
    }

    /// <summary>Makes a member, removing a leftover of the same name first; their token when a password is given.</summary>
    private async Task<string> MakeMemberAsync(string name, string? password = null)
    {
        await AdminAsync(HttpMethod.Delete, $"/admin/members/{name}?deleteOwned=true");
        (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/admin/members",
            JsonSerializer.Serialize(new { name, role = "publisher", userType = "creator" }));
        Assert.True(made is 200 or 201, $"making '{name}': {made} {madeBody}");

        if (password is null)
        {
            return "";
        }

        string root = await RequireServerAsync();
        string issued = JsonDocument.Parse(madeBody).RootElement.GetProperty("password").GetString()!;

        using (StringContent first = new(JsonSerializer.Serialize(new { name, password = issued }), Encoding.UTF8, "application/json"))
        using (HttpResponseMessage signedIn = await Http.PostAsync(new Uri($"{root}/rest/auth/login"), first))
        {
            string once = JsonDocument.Parse(await signedIn.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
            (HttpStatusCode changed, string why) = await RequestAsync(HttpMethod.Post, $"{root}/rest/auth/password", once,
                JsonSerializer.Serialize(new { currentPassword = issued, newPassword = password }));
            Assert.True(changed is HttpStatusCode.OK or HttpStatusCode.NoContent, $"changing the password: {changed} {why}");
        }

        using StringContent again = new(JsonSerializer.Serialize(new { name, password }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.PostAsync(new Uri($"{root}/rest/auth/login"), again);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    }

    private async Task GiveAsync(string service, string to)
    {
        (int given, string body) = await AdminAsync(HttpMethod.Put, $"/admin/services/{service}/owner?folder=hosted",
            JsonSerializer.Serialize(new { to }));
        Assert.True(given == 200, $"giving '{service}' to '{to}': {given} {body}");
    }

    /// <summary>Deletes a service this test made, wherever it is, and empties what that put in the bin.</summary>
    private async Task ClearAsync(string service)
    {
        await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{service}?folder=hosted&drop=true");
        (int listed, string bin) = await AdminAsync(HttpMethod.Get, "/admin/recycle-bin");

        if (listed != 200)
        {
            return;
        }

        foreach (JsonElement entry in JsonDocument.Parse(bin).RootElement.GetProperty("entries").EnumerateArray()
            .Where(e => (e.GetProperty("title").GetString() ?? "").Contains(service, StringComparison.Ordinal)))
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/recycle-bin/{entry.GetProperty("id").GetString()}");
        }
    }

    /// <summary>Imports a corpus shapefile as the administrator and returns the table it landed in.</summary>
    private async Task<string> ImportCorpusAsync(string root, string token, string name)
    {
        string path = Path.Combine(RepositoryRoot(), "tests", "Graticula.Core.Tests", "corpus", "shapefile", "points.zip");
        Assert.True(File.Exists(path), $"The corpus archive is not at {path}.");

        using MultipartFormDataContent form = new();
        using ByteArrayContent bytes = new(await File.ReadAllBytesAsync(path));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(bytes, "file", "points.zip");
        form.Add(new StringContent(name), "name");

        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/admin/hosted/import") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"The import of '{name}' failed with {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.TryGetProperty("table", out JsonElement had) ? had.GetString() ?? "" : "";
    }

    /// <summary>Whether any registered datasource still offers that table — the datastore's own answer.</summary>
    private async Task<bool> DatastoreHasAsync(string root, string token, string table)
    {
        string bare = table.Contains('.', StringComparison.Ordinal) ? table[(table.IndexOf('.', StringComparison.Ordinal) + 1)..] : table;

        using HttpRequestMessage list = new(HttpMethod.Get, $"{root}/admin/datasources");
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage listed = await Http.SendAsync(list);

        foreach (JsonElement source in JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("dataSources").EnumerateArray())
        {
            using HttpRequestMessage ask = new(HttpMethod.Get, $"{root}/admin/datasources/{source.GetProperty("id").GetString()}/capability");
            ask.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage got = await Http.SendAsync(ask);

            if (got.IsSuccessStatusCode && (await got.Content.ReadAsStringAsync()).Contains(bare, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? at = new(AppContext.BaseDirectory);

        while (at is not null && !File.Exists(Path.Combine(at.FullName, "CLAUDE.md")))
        {
            at = at.Parent;
        }

        Assert.NotNull(at);
        return at!.FullName;
    }
}

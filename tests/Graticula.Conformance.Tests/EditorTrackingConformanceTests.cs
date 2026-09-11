using System;
using System.Collections.Generic;
using System.Globalization;
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
/// <c>features:edit</c> means what it means in Portal on a layer that records its creators —
/// ADR-064, and D-20 closing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two accounts, because one proves nothing.</b> An administrator holds
/// <c>features:fullEdit</c> and can change every feature whatever this server records, so a test
/// with one account would pass against a server that never looked at a creator. The second
/// account is an <c>editor</c> user type, whose ceiling is <c>features:edit</c> and nothing wider.
/// </para>
/// <para>
/// <b>Both writing faces, because ADR-064 condition 1 names both.</b> ArcGIS <c>applyEdits</c>
/// reports a refusal inside a result; OGC API Features has HTTP statuses for it, and a feature
/// that is there and not yours is <c>403</c>, not the 400 of a malformed body.
/// </para>
/// <para>
/// <b>Its own layer, defined and dropped here</b>, because turning tracking on adds four columns
/// to a table and every other test reads the fixture's layers by their shape.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class EditorTrackingConformanceTests : ArcGisClient
{
    private const string Member = "zz_adr064_editor";
    private const string MergePatch = "application/merge-patch+json";

    [Fact]
    public async Task An_editor_changes_its_own_features_and_nobody_else_s()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string layer = "zz_adr064_" + Guid.NewGuid().ToString("N")[..8];

        (HttpStatusCode defined, string design) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/hosted/define",
            token!,
            JsonSerializer.Serialize(new
            {
                name = layer,
                geometryType = "Point",
                fields = new[] { new { name = "label", type = "text", nullable = true } },
                sharing = "public",
            }));

        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}: {design}");

        // `/rest/services/{folder}/{service}/FeatureServer/{index}`, as the definition says it.
        string feature = JsonDocument.Parse(design).RootElement
            .GetProperty("services").GetProperty("feature").GetString()!;

        string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string folder = parts[2];
        string service = parts[3];

        try
        {
            // ---- a feature from before tracking, which has no creator and so is nobody's ----
            long nobodys = Assert.Single(Ids(await EditAsync(
                root, token!, feature, "addFeatures", ("features", Point("from before tracking", impostor: false)))));

            (HttpStatusCode tracked, string tracking) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/hosted/{layer}/editor-tracking", token!, "{}");

            Assert.True(
                tracked == HttpStatusCode.OK,
                $"Turning editor tracking on answered {(int)tracked}: {tracking}");

            // ---- the document says so, in the two objects a client reads ----
            JsonElement document = await GetJsonAsync(feature);

            Assert.Equal(
                "created_user",
                document.GetProperty("editFieldsInfo").GetProperty("creatorField").GetString());

            JsonElement ownership = document.GetProperty("ownershipBasedAccessControlForFeatures");

            Assert.False(ownership.GetProperty("allowOthersToUpdate").GetBoolean());
            Assert.False(ownership.GetProperty("allowOthersToDelete").GetBoolean());

            JsonElement creatorField = document.GetProperty("fields").EnumerateArray()
                .Single(f => f.GetProperty("name").GetString() == "created_user");

            Assert.False(
                creatorField.GetProperty("editable").GetBoolean(),
                "created_user is advertised editable, and this server replaces whatever a client "
                + "writes to it.");

            // ---- a second account that may edit and may not full-edit ----
            string editor = await EditorAsync(root, token!);

            long theirs = Assert.Single(Ids(await EditAsync(
                root, token!, feature, "addFeatures", ("features", Point("the administrator's")))));

            long mine = Assert.Single(Ids(await EditAsync(
                root, editor, feature, "addFeatures", ("features", Point("the editor's")))));

            // ---- its own: changed, and the creator it sends is not the creator written ----
            JsonElement own = Result(await EditAsync(
                root, editor, feature, "updateFeatures",
                ("features", Change(mine, "changed by its owner", impostor: true))));

            Assert.True(own.GetProperty("success").GetBoolean(), $"An editor could not change its own feature: {own}");

            // ---- somebody else's: refused ----
            JsonElement other = Result(await EditAsync(
                root, editor, feature, "updateFeatures", ("features", Change(theirs, "not mine to change"))));

            Assert.False(
                other.GetProperty("success").GetBoolean(),
                "An account with features:edit changed a feature somebody else created. That is "
                + "D-20's opposite defect: the narrowing removed, and nothing put in its place.");

            Assert.Contains("somebody else", other.ToString(), StringComparison.Ordinal);

            // ---- nobody's: refused too, because *probably yours* is the guess D-20 refused ----
            JsonElement unowned = Result(await EditAsync(
                root, editor, feature, "updateFeatures", ("features", Change(nobodys, "claimed"))));

            Assert.False(
                unowned.GetProperty("success").GetBoolean(),
                "An editor changed a feature with no creator recorded. A row nobody created is "
                + "nobody's, and changing it needs features:fullEdit.");

            Assert.Contains("no creator", unowned.ToString(), StringComparison.Ordinal);

            // ---- and the administrator is refused nothing ----
            JsonElement full = Result(await EditAsync(
                root, token!, feature, "updateFeatures", ("features", Change(mine, "changed by the administrator"))));

            Assert.True(full.GetProperty("success").GetBoolean(), $"features:fullEdit was refused: {full}");

            // ---- the server wrote who did it, whatever the client sent ----
            JsonElement attributes = await AttributesAsync(feature, mine);

            Assert.Equal(Member, attributes.GetProperty("created_user").GetString());
            Assert.NotEqual(Member, attributes.GetProperty("last_edited_user").GetString());

            // ---- deletes follow the same rule ----
            JsonElement refused = Result(await EditAsync(
                root, editor, feature, "deleteFeatures", ("objectIds", Id(theirs))));

            Assert.False(refused.GetProperty("success").GetBoolean(), $"An editor deleted somebody else's feature: {refused}");

            JsonElement deleted = Result(await EditAsync(
                root, editor, feature, "deleteFeatures", ("objectIds", Id(mine))));

            Assert.True(deleted.GetProperty("success").GetBoolean(), $"An editor could not delete its own feature: {deleted}");

            // ---- the OGC API Features face: the same rule, in HTTP's words ----
            string items = $"{root}/ogc/features/v1/collections/{Uri.EscapeDataString(layer)}/items";

            (HttpStatusCode created, string createdBody, string? location) = await OgcAsync(
                HttpMethod.Post,
                items,
                editor,
                "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[32.87,39.96]},"
                + "\"properties\":{\"label\":\"the editor's, over OGC\",\"created_user\":\"an impostor\"}}",
                "application/geo+json");

            Assert.True(created == HttpStatusCode.Created, $"An editor's OGC create answered {(int)created}: {createdBody}");

            string ogcMine = new Uri(location!).Segments[^1];

            Assert.Equal(
                Member,
                (await AttributesAsync(feature, long.Parse(ogcMine, CultureInfo.InvariantCulture)))
                    .GetProperty("created_user").GetString());

            const string Relabel = "{\"type\":\"Feature\",\"properties\":{\"label\":\"over OGC\"}}";

            await ExpectAsync(HttpStatusCode.NoContent, "an editor patching its own feature",
                HttpMethod.Patch, $"{items}/{ogcMine}", editor, Relabel);

            string why = await ExpectAsync(HttpStatusCode.Forbidden, "an editor patching somebody else's feature",
                HttpMethod.Patch, $"{items}/{Id(theirs)}", editor, Relabel);

            Assert.Contains("somebody else", why, StringComparison.Ordinal);

            await ExpectAsync(HttpStatusCode.Forbidden, "an editor patching a feature with no creator",
                HttpMethod.Patch, $"{items}/{Id(nobodys)}", editor, Relabel);

            await ExpectAsync(HttpStatusCode.Forbidden, "an editor deleting somebody else's feature",
                HttpMethod.Delete, $"{items}/{Id(theirs)}", editor, json: null);

            // The refusals changed nothing: 403 that had written anyway would be worse than none.
            Assert.Equal(
                "the administrator's",
                (await AttributesAsync(feature, theirs)).GetProperty("label").GetString());

            await ExpectAsync(HttpStatusCode.NoContent, "an editor deleting its own feature",
                HttpMethod.Delete, $"{items}/{ogcMine}", editor, json: null);

            await ExpectAsync(HttpStatusCode.NoContent, "the administrator deleting anybody's feature",
                HttpMethod.Delete, $"{items}/{Id(theirs)}", token!, json: null);

            // ---- condition 3: a role is refused where it is set, not at the next edit ----
            foreach ((string overrides, string expected) in ((string, string)[])
            [
                ("[{\"column\":\"label\",\"tracks\":\"created\"}]", "not a date column"),
                ("[{\"column\":\"created_date\",\"tracks\":\"creator\"}]", "not a text column"),
                ("[{\"column\":\"label\",\"tracks\":\"owner\"}]", "not something a column can record"),
                ("[{\"column\":\"created_user\",\"tracks\":\"creator\",\"hidden\":true}]", "cannot be hidden"),
                ("[{\"column\":\"created_user\",\"tracks\":\"creator\"},"
                    + "{\"column\":\"last_edited_user\",\"tracks\":\"creator\"}]", "Two columns"),
            ])
            {
                (HttpStatusCode status, string body) = await RequestAsync(
                    HttpMethod.Put, $"{root}/admin/layers/{layer}/fields", token!, $"{{\"overrides\":{overrides}}}");

                Assert.True(
                    status == HttpStatusCode.BadRequest,
                    $"Setting {overrides} answered {(int)status}, and should have been refused: {body}");

                Assert.Contains(expected, body, StringComparison.Ordinal);
            }

            // And none of the refusals touched the roles that were there.
            Assert.Equal(
                "created_user",
                (await GetJsonAsync(feature)).GetProperty("editFieldsInfo").GetProperty("creatorField").GetString());
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/members/{Member}?deleteOwned=true", token!, json: null);

            await RequestAsync(
                HttpMethod.Delete,
                $"{root}/admin/featureservices/{service}?folder={folder}&drop=true",
                token!,
                json: null);
        }
    }

    /// <summary>Creates the editor account and signs it in.</summary>
    private async Task<string> EditorAsync(string root, string administrator)
    {
        await RequestAsync(HttpMethod.Delete, $"{root}/admin/members/{Member}?deleteOwned=true", administrator, json: null);

        (HttpStatusCode made, string said) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/members",
            administrator,
            JsonSerializer.Serialize(new { name = Member, displayName = "ADR-064", role = "publisher", userType = "editor" }));

        Assert.True(
            made == HttpStatusCode.Created,
            $"Creating an editor answered {(int)made}: {said}. Without an account that holds "
            + "features:edit and not features:fullEdit this test cannot tell the rule from its absence.");

        string issued = JsonDocument.Parse(said).RootElement.GetProperty("password").GetString()!;
        string first = await LoginAsync(root, issued);

        (HttpStatusCode changed, string why) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/rest/auth/password",
            first,
            JsonSerializer.Serialize(new { currentPassword = issued, newPassword = issued + "X1" }));

        Assert.True(changed == HttpStatusCode.OK, $"The editor could not replace its issued password: {(int)changed} {why}");

        return await LoginAsync(root, issued + "X1");
    }

    private async Task<string> LoginAsync(string root, string password)
    {
        (HttpStatusCode status, string body) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/rest/auth/login",
            token: string.Empty,
            JsonSerializer.Serialize(new { name = Member, password }));

        Assert.True(status == HttpStatusCode.OK, $"Signing in as `{Member}` answered {(int)status}: {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }

    /// <summary>One feature's attributes, read through the ArcGIS query.</summary>
    private async Task<JsonElement> AttributesAsync(string feature, long id)
    {
        JsonElement written = await GetJsonAsync(
            $"{feature}/query?where=objectid%3D{Id(id)}&outFields=label,created_user,last_edited_user&f=json");

        return written.GetProperty("features")[0].GetProperty("attributes");
    }

    /// <summary>Sends an OGC API Features write as a given account.</summary>
    private async Task<(HttpStatusCode Status, string Body, string? Location)> OgcAsync(
        HttpMethod method, string url, string token, string? json, string contentType = MergePatch)
    {
        using HttpRequestMessage request = new(method, new Uri(url));

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (
            response.StatusCode,
            await response.Content.ReadAsStringAsync(),
            response.Headers.Location?.ToString());
    }

    /// <summary>Sends an OGC write and asserts its status, returning the body.</summary>
    private async Task<string> ExpectAsync(
        HttpStatusCode expected, string what, HttpMethod method, string url, string token, string? json)
    {
        (HttpStatusCode status, string body, _) = await OgcAsync(method, url, token, json);

        Assert.True(status == expected, $"Over OGC, {what} answered {(int)status}, expected {(int)expected}: {body}");

        return body;
    }

    /// <summary>Posts one of the single-operation edit endpoints as a given account.</summary>
    private static async Task<JsonElement> EditAsync(
        string root, string token, string feature, string operation, params (string Key, string Value)[] fields)
    {
        using HttpClient http = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        using FormUrlEncodedContent content = new(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
                .Append(new KeyValuePair<string, string>("rollbackOnFailure", "false"))
                .Append(new KeyValuePair<string, string>("f", "json")));

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{feature}/{operation}"))
        {
            Content = content,
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await http.SendAsync(request);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>The object ids an addFeatures answer reports, having asserted each succeeded.</summary>
    private static List<long> Ids(JsonElement answer) =>
        answer.GetProperty("addResults").EnumerateArray()
            .Select(r =>
            {
                Assert.True(r.GetProperty("success").GetBoolean(), $"An add failed: {answer}");
                return r.GetProperty("objectId").GetInt64();
            })
            .ToList();

    /// <summary>The one result of a single-operation answer, whichever it is.</summary>
    private static JsonElement Result(JsonElement answer)
    {
        foreach (string name in (string[])["updateResults", "deleteResults", "addResults"])
        {
            if (answer.TryGetProperty(name, out JsonElement results))
            {
                return Assert.Single(results.EnumerateArray());
            }
        }

        throw new Xunit.Sdk.XunitException($"No results array in the answer: {answer}");
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A point to add, sending a creator of its own unless told not to — before tracking is on
    /// there is no such column to send it to.
    /// </summary>
    private static string Point(string label, bool impostor = true) =>
        $"[{{\"geometry\":{{\"x\":1000,\"y\":2000,\"spatialReference\":{{\"wkid\":3857}}}},"
        + $"\"attributes\":{{\"label\":\"{label}\"{(impostor ? ",\"created_user\":\"an impostor\"" : "")}}}}}]";

    private static string Change(long id, string label, bool impostor = false) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"[{{\"attributes\":{{\"objectid\":{id},\"label\":\"{label}\""
            + $"{(impostor ? ",\"created_user\":\"an impostor\"" : "")}}}}}]");
}

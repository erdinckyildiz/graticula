using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-300: a form's Inbox is a view each member reads their own rows of. Condition 1 — every face of the per-reader view
/// asked as two members — and the update and conflict rules of §5.1 and §5.5.
/// </summary>
/// <remarks>
/// Two members fill a form shared with the organisation, each a record with a repeat row and a photograph, at points far
/// apart and under names that say whose they are. Through the Inbox each must find their own on every face, and the other's
/// on none; an anonymous caller finds nothing; the faces that would keep one reader's rows for the next are refused.
/// </remarks>
[Collection("catalogue walk")]
public sealed class AFormsInboxShowsEachMemberTheirOwnTests : ArcGisClient
{
    private const string Password = "Zz-inbox-member-9!";

    // Far apart and unmistakable, so a document that leaks a point is caught by its digits.
    private const double AX = 29.123457, AY = 41.234567, BX = 32.876543, BY = 39.987654;

    private static JsonObject Model(string formId) => new()
    {
        ["format"] = "xlsform/1",
        ["sheets"] = new JsonArray(
            Sheet("survey", ["type", "name", "label"],
                ["geopoint", "konum", "Konum"],
                ["text", "ad", "Ad"],
                ["image", "foto", "Foto"],
                ["begin repeat", "katlar", "Katlar"],
                ["integer", "kat_no", "Kat"],
                ["end repeat"]),
            Sheet("settings", ["form_title", "form_id"], ["Inbox " + formId, formId])),
    };

    private static JsonObject Sheet(string name, string[] columns, params string[][] rows) => new()
    {
        ["name"] = name,
        ["columns"] = new JsonArray([.. columns.Select(c => (JsonNode?)c)]),
        ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode?)new JsonArray([.. r.Select(c => (JsonNode?)c)]))]),
    };

    /// <summary>One member's record: its tokens, names and ids as the test made them.</summary>
    private sealed record Record(string Token, string Name, Guid GlobalId, int ObjectId, int RepeatObjectId, double X, double Y);

    /// <summary>What a set-up form is.</summary>
    private sealed record InboxForm(
        string Root, string Admin, string FormId, string Item, string Run, JsonElement Publication, Record A, Record B)
    {
        public JsonElement Top => Publication.GetProperty("layers").EnumerateArray().First(l => l.GetProperty("repeat").GetString() == "");

        public JsonElement Repeat => Publication.GetProperty("layers").EnumerateArray().First(l => l.GetProperty("repeat").GetString() == "katlar");

        public string Inbox => Top.GetProperty("inboxUrl").GetString()!;

        public string InboxRepeat => Repeat.GetProperty("inboxUrl").GetString()!;

        public string InboxService => Inbox[..Inbox.LastIndexOf("/FeatureServer/", StringComparison.Ordinal)];

        public string ViewName => Publication.GetProperty("inboxService").GetString()!;
    }

    [Fact]
    public async Task Each_member_reads_their_own_rows_on_every_face_of_the_Inbox_and_nobody_else_does()
    {
        InboxForm? f = null;
        string root = await RequireServerAsync();
        string run = Guid.NewGuid().ToString("N")[..6];

        try
        {
            f = await SetUpAsync(root, run, "organization");

            // The settings read back as they were set, with the view they made.
            (HttpStatusCode read, string readBody) = await RequestAsync(HttpMethod.Get, $"{root}/admin/forms/{f.Item}/inbox", f.Admin, null);
            Assert.True(read == HttpStatusCode.OK, $"GET …/inbox answered {(int)read}: {readBody}");
            JsonElement settings = JsonDocument.Parse(readBody).RootElement;
            Assert.True(settings.GetProperty("enabled").GetBoolean());
            Assert.Equal("created_user = ${username}", settings.GetProperty("filter").GetString());
            Assert.Equal(40, settings.GetProperty("limit").GetInt32());
            Assert.Equal(f.ViewName, settings.GetProperty("service").GetString());
            Assert.Equal(40, f.Publication.GetProperty("inbox").GetProperty("limit").GetInt32());

            foreach ((Record me, Record other) in new[] { (f.A, f.B), (f.B, f.A) })
            {
                await OwnOnlyAsync(f, me, other);
            }

            // Anonymous: no row on any face, and the form's own document tells nobody the Inbox is there.
            foreach (string face in (string[])[
                $"{f.Inbox}/query?where=1%3D1&outFields=*&f=json",
                $"{f.Inbox}/query?where=1%3D1&returnCountOnly=true&f=json",
                $"{f.InboxRepeat}/query?where=1%3D1&outFields=*&f=json",
                $"{f.Inbox}/{f.A.ObjectId}/attachments?f=json"])
            {
                using HttpRequestMessage anonymous = new(HttpMethod.Get, $"{root}{face}");
                using HttpResponseMessage answered = await Http.SendAsync(anonymous);
                string seen = await answered.Content.ReadAsStringAsync();
                Assert.False(seen.Contains(f.A.Name, StringComparison.Ordinal) || seen.Contains(f.B.Name, StringComparison.Ordinal)
                    || seen.Contains("\"count\":1", StringComparison.Ordinal) || seen.Contains("\"count\":2", StringComparison.Ordinal),
                    $"An anonymous {face} answered {(int)answered.StatusCode}: {Head(seen)}");
            }

            // An administrator reads the view as a member too: their own rows, which here are none.
            (HttpStatusCode adminRead, string adminRows) = await RequestAsync(HttpMethod.Get,
                $"{root}{f.Inbox}/query?where=1%3D1&returnCountOnly=true&f=json", f.Admin, null);
            Assert.True(adminRead == HttpStatusCode.OK, $"The administrator's count answered {(int)adminRead}: {adminRows}");
            Assert.Equal(0, JsonDocument.Parse(adminRows).RootElement.GetProperty("count").GetInt32());

            // The faces that keep one reader's rows for the next are refused (§5.4).
            foreach (string face in (string[])[
                $"{f.InboxService}/VectorTileServer/tile/0/0/0.pbf",
                $"{f.InboxService}/VectorTileServer?f=json"])
            {
                (HttpStatusCode tile, string tileBody) = await RequestAsync(HttpMethod.Get, $"{root}{face}", f.A.Token, null);
                Assert.True(tile != HttpStatusCode.OK, $"The Inbox's {face} answered 200: {Head(tileBody)}");
            }

            using (HttpRequestMessage thumbnail = new(HttpMethod.Get, $"{root}/admin/thumbnail?service=hosted/{f.ViewName}&layer=0"))
            {
                thumbnail.Headers.Authorization = new AuthenticationHeaderValue("Bearer", f.A.Token);
                using HttpResponseMessage drawn = await Http.SendAsync(thumbnail);
                Assert.True(drawn.StatusCode != HttpStatusCode.OK || drawn.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) != true,
                    $"The Inbox drew a thumbnail: {(int)drawn.StatusCode}");
            }

            (HttpStatusCode exported, string exportBody) = await RequestAsync(HttpMethod.Post,
                $"{root}/admin/services/{f.ViewName}/data-exports?folder=hosted", f.Admin, """{"format":"geojson"}""");
            Assert.True(exported == HttpStatusCode.Forbidden && exportBody.Contains("each reader", StringComparison.Ordinal),
                $"An export of the Inbox answered {(int)exported}: {exportBody}");

            (HttpStatusCode analysed, string analysis) = await PostFormAsync($"{root}/rest/services/System/SpatialAnalysisTools/GPServer/CreateBuffers/submitJob", f.Admin,
                new() { ["f"] = "json", ["inputLayer"] = JsonSerializer.Serialize(new { url = root + f.Inbox }), ["distances"] = "[1]", ["units"] = "Meters" });
            Assert.True(analysis.Contains("each reader", StringComparison.Ordinal),
                $"An analysis job naming the Inbox answered {(int)analysed}: {Head(analysis)}");
        }
        finally
        {
            await TearDownAsync(root, run, f);
        }
    }

    /// <summary>Every face of ADR-300 condition 1, as <paramref name="me"/>.</summary>
    private async Task OwnOnlyAsync(InboxForm f, Record me, Record other)
    {
        string root = f.Root;
        string who = me.Name;

        void OwnNotOther(string face, HttpStatusCode status, string body)
        {
            Assert.True(status == HttpStatusCode.OK, $"{who}: {face} answered {(int)status}: {Head(body)}");
            Assert.True(body.Contains(me.Name, StringComparison.Ordinal), $"{who}: {face} did not show their own record: {Head(body)}");
            Assert.False(body.Contains(other.Name, StringComparison.Ordinal), $"{who}: {face} showed the other member's record: {Head(body)}");
        }

        // query, and its HTTP caching
        using (HttpRequestMessage query = new(HttpMethod.Get, $"{root}{f.Inbox}/query?where=1%3D1&outFields=*&f=json"))
        {
            query.Headers.Authorization = new AuthenticationHeaderValue("Bearer", me.Token);
            using HttpResponseMessage answered = await Http.SendAsync(query);
            OwnNotOther("query", answered.StatusCode, await answered.Content.ReadAsStringAsync());
            string caching = answered.Headers.CacheControl?.ToString() ?? string.Empty;
            Assert.True(caching.Contains("private", StringComparison.Ordinal) && caching.Contains("no-store", StringComparison.Ordinal),
                $"{who}: the Inbox's query answered Cache-Control '{caching}'.");
        }

        // count, ids, statistics
        JsonElement count = await JsonAsync(me.Token, $"{root}{f.Inbox}/query?where=1%3D1&returnCountOnly=true&f=json");
        Assert.Equal(1, count.GetProperty("count").GetInt32());
        JsonElement ids = await JsonAsync(me.Token, $"{root}{f.Inbox}/query?where=1%3D1&returnIdsOnly=true&f=json");
        Assert.Equal([me.ObjectId], ids.GetProperty("objectIds").EnumerateArray().Select(i => i.GetInt32()).ToArray());
        string statistics = Uri.EscapeDataString("""[{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"n"}]""");
        JsonElement counted = await JsonAsync(me.Token, $"{root}{f.Inbox}/query?where=1%3D1&outStatistics={statistics}&f=json");
        Assert.Equal(1, counted.GetProperty("features")[0].GetProperty("attributes").GetProperty("n").GetInt32());

        // an object id that is the other's is not there
        JsonElement asked = await JsonAsync(me.Token, $"{root}{f.Inbox}/query?objectIds={other.ObjectId}&outFields=*&f=json");
        Assert.Equal(0, asked.GetProperty("features").GetArrayLength());

        // renderer
        string definition = Uri.EscapeDataString("""{"type":"uniqueValueDef","uniqueValueFields":["ad"]}""");
        (HttpStatusCode rendered, string renderer) = await RequestAsync(HttpMethod.Get,
            $"{root}{f.Inbox}/generateRenderer?classificationDef={definition}&f=json", me.Token, null);
        OwnNotOther("generateRenderer", rendered, renderer);

        // related records: the repeat's row of their own record, and nothing for the other's
        int relationship = f.Repeat.GetProperty("inboxRelationshipId").GetInt32();
        (HttpStatusCode relatedStatus, string related) = await RequestAsync(HttpMethod.Get,
            $"{root}{f.Inbox}/queryRelatedRecords?objectIds={me.ObjectId},{other.ObjectId}&relationshipId={relationship}&outFields=*&f=json",
            me.Token, null);
        Assert.True(relatedStatus == HttpStatusCode.OK, $"{who}: queryRelatedRecords answered {(int)relatedStatus}: {Head(related)}");
        JsonElement groups = JsonDocument.Parse(related).RootElement.GetProperty("relatedRecordGroups");
        Assert.True(groups.GetArrayLength() == 1 && groups[0].GetProperty("objectId").GetInt32() == me.ObjectId,
            $"{who}: queryRelatedRecords answered other groups than their own: {Head(related)}");
        JsonElement repeats = await JsonAsync(me.Token, $"{root}{f.InboxRepeat}/query?where=1%3D1&outFields=*&f=json");
        Assert.Equal([me.RepeatObjectId], repeats.GetProperty("features").EnumerateArray()
            .Select(r => r.GetProperty("attributes").GetProperty("objectid").GetInt32()).ToArray());

        // attachments: list, query, download — their own photograph and not the other's
        (HttpStatusCode listed, string list) = await RequestAsync(HttpMethod.Get, $"{root}{f.Inbox}/{me.ObjectId}/attachments?f=json", me.Token, null);
        Assert.True(listed == HttpStatusCode.OK, $"{who}: attachments answered {(int)listed}: {Head(list)}");
        JsonElement infos = JsonDocument.Parse(list).RootElement.GetProperty("attachmentInfos");
        Assert.True(infos.GetArrayLength() >= 1, $"{who}: their own photograph was not listed: {Head(list)}");
        int attachment = infos[0].GetProperty("id").GetInt32();

        (HttpStatusCode theirs, string theirList) = await RequestAsync(HttpMethod.Get, $"{root}{f.Inbox}/{other.ObjectId}/attachments?f=json", me.Token, null);
        Assert.False(theirs == HttpStatusCode.OK && theirList.Contains("\"attachmentInfos\":[{", StringComparison.Ordinal),
            $"{who}: the other member's photographs were listed: {Head(theirList)}");

        JsonElement queried = await JsonAsync(me.Token, $"{root}{f.Inbox}/queryAttachments?definitionExpression=1%3D1&f=json");
        Assert.Equal([me.ObjectId], queried.GetProperty("attachmentGroups").EnumerateArray().Select(g => g.GetProperty("parentObjectId").GetInt32()).ToArray());

        using (HttpRequestMessage download = new(HttpMethod.Get, $"{root}{f.Inbox}/{me.ObjectId}/attachments/{attachment}"))
        {
            download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", me.Token);
            using HttpResponseMessage bytes = await Http.SendAsync(download);
            Assert.True(bytes.StatusCode == HttpStatusCode.OK, $"{who}: downloading their own photograph answered {(int)bytes.StatusCode}.");
        }

        using (HttpRequestMessage download = new(HttpMethod.Get, $"{root}{f.Inbox}/{other.ObjectId}/attachments/{attachment}"))
        {
            download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", me.Token);
            using HttpResponseMessage bytes = await Http.SendAsync(download);
            Assert.NotEqual(HttpStatusCode.OK, bytes.StatusCode);
        }

        (HttpStatusCode addedOwn, string addedOwnBody) = await AttachAsync(root, me.Token, $"{f.Inbox}/{me.ObjectId}/addAttachment");
        Assert.True(addedOwn == HttpStatusCode.OK && addedOwnBody.Contains("\"success\":true", StringComparison.Ordinal),
            $"{who}: adding a photograph to their own record through the Inbox answered {(int)addedOwn}: {Head(addedOwnBody)}");
        (HttpStatusCode addedOther, string addedOtherBody) = await AttachAsync(root, me.Token, $"{f.Inbox}/{other.ObjectId}/addAttachment");
        Assert.False(addedOther == HttpStatusCode.OK && addedOtherBody.Contains("\"success\":true", StringComparison.Ordinal),
            $"{who}: a photograph was added to the other member's record through the Inbox: {Head(addedOtherBody)}");

        // OGC API Features
        JsonElement collections = await JsonAsync(me.Token, $"{root}/ogc/features/v1/collections?f=json");
        string? collection = collections.GetProperty("collections").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString())
            .FirstOrDefault(id => id is not null && id.StartsWith(f.ViewName, StringComparison.Ordinal) && !id.Contains("katlar", StringComparison.Ordinal));
        Assert.False(collection is null, $"{who}: the Inbox is not among the OGC API collections.");
        (HttpStatusCode items, string itemsBody) = await RequestAsync(HttpMethod.Get,
            $"{root}/ogc/features/v1/collections/{collection}/items?f=json", me.Token, null);
        OwnNotOther("OGC API Features items", items, itemsBody);
        JsonElement described = await JsonAsync(me.Token, $"{root}/ogc/features/v1/collections/{collection}?f=json");
        Assert.False(described.GetRawText().Contains(Digits(other.X), StringComparison.Ordinal),
            $"{who}: the OGC collection document gave the other member's point away: {Head(described.GetRawText())}");

        // WFS
        (HttpStatusCode capabilitiesStatus, string capabilities) = await RequestAsync(HttpMethod.Get,
            $"{root}/wfs?service=WFS&version=2.0.0&request=GetCapabilities", me.Token, null);
        Assert.True(capabilitiesStatus == HttpStatusCode.OK, $"{who}: WFS GetCapabilities answered {(int)capabilitiesStatus}.");
        string? type = XDocument.Parse(capabilities).Descendants().Where(e => e.Name.LocalName == "FeatureType")
            .Select(e => e.Elements().FirstOrDefault(n => n.Name.LocalName == "Name")?.Value)
            .FirstOrDefault(n => n is not null && n.Contains(f.ViewName, StringComparison.Ordinal) && !n.Contains("katlar", StringComparison.Ordinal));
        Assert.False(type is null, $"{who}: the Inbox is not among WFS's feature types.");
        (HttpStatusCode wfs, string features) = await RequestAsync(HttpMethod.Get,
            $"{root}/wfs?service=WFS&version=2.0.0&request=GetFeature&typeNames={Uri.EscapeDataString(type!)}", me.Token, null);
        OwnNotOther("WFS GetFeature", wfs, features);

        // MapServer: its query and identify read the view as the caller too
        (HttpStatusCode mapQuery, string mapRows) = await RequestAsync(HttpMethod.Get,
            $"{root}{f.InboxService}/MapServer/0/query?where=1%3D1&outFields=*&f=json", me.Token, null);
        if (mapQuery == HttpStatusCode.OK)
        {
            OwnNotOther("MapServer query", mapQuery, mapRows);
        }

        foreach ((double x, double y) in new[] { (me.X, me.Y), (other.X, other.Y) })
        {
            (HttpStatusCode identified, string identify) = await RequestAsync(HttpMethod.Get,
                $"{root}{f.InboxService}/MapServer/identify?geometry={x},{y}&geometryType=esriGeometryPoint&sr=4326&tolerance=10"
                + "&mapExtent=20,30,40,50&imageDisplay=400,400,96&layers=all&returnGeometry=false&f=json", me.Token, null);
            Assert.False(identify.Contains(other.Name, StringComparison.Ordinal),
                $"{who}: MapServer identify answered {(int)identified} with the other member's record: {Head(identify)}");
        }

        // The documents state no extent, so neither point is in them.
        foreach (string document in (string[])[$"{f.Inbox}?f=json", $"{f.InboxService}/FeatureServer?f=json", $"{f.InboxService}/MapServer?f=json"])
        {
            (_, string body) = await RequestAsync(HttpMethod.Get, $"{root}{document}", me.Token, null);
            Assert.False(body.Contains(Digits(other.X), StringComparison.Ordinal) || body.Contains(Digits(other.Y), StringComparison.Ordinal)
                || body.Contains(Digits(me.X), StringComparison.Ordinal),
                $"{who}: {document} states a point: {Head(body)}");
        }

        JsonElement layerDocument = await JsonAsync(me.Token, $"{root}{f.Inbox}?f=json");
        Assert.True(!layerDocument.TryGetProperty("extent", out JsonElement extent) || extent.ValueKind == JsonValueKind.Null,
            $"{who}: the Inbox layer states an extent: {extent}");
    }

    [Fact]
    public async Task A_member_changes_their_own_record_through_the_Inbox_and_a_stale_or_missing_one_is_refused()
    {
        InboxForm? f = null;
        string root = await RequireServerAsync();
        string run = Guid.NewGuid().ToString("N")[..6];

        try
        {
            f = await SetUpAsync(root, run, "organization");
            long downloaded = await EditDateAsync(f, f.A);

            // The other's record is not reachable, with the dates or without them.
            (_, string onOther) = await UpdateAsync(f, f.A.Token, f.B.GlobalId, "A wrote this", downloaded);
            JsonElement refusedOther = JsonDocument.Parse(onOther).RootElement.GetProperty("updateResults")[0];
            Assert.False(refusedOther.GetProperty("success").GetBoolean(), $"A changed B's record through the Inbox: {onOther}");
            Assert.Equal(404, refusedOther.GetProperty("error").GetProperty("code").GetInt32());
            (_, string onOtherPlain) = await UpdateAsync(f, f.A.Token, f.B.GlobalId, "A wrote this", null);
            Assert.False(onOtherPlain.Contains("\"success\":true", StringComparison.Ordinal), $"A changed B's record without dates: {onOtherPlain}");

            // Stale: the date before the one downloaded. Refused as 412, the row unchanged.
            (_, string stale) = await UpdateAsync(f, f.A.Token, f.A.GlobalId, "stale", downloaded - 1);
            JsonElement staleResult = JsonDocument.Parse(stale).RootElement.GetProperty("updateResults")[0];
            Assert.False(staleResult.GetProperty("success").GetBoolean(), $"A stale update was applied: {stale}");
            Assert.Equal(412, staleResult.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(f.A.GlobalId, Guid.Parse(staleResult.GetProperty("globalId").GetString()!));
            Assert.Equal(f.A.Name, await NameInSourceAsync(f, f.A.ObjectId));

            // The right date: applied.
            (_, string applied) = await UpdateAsync(f, f.A.Token, f.A.GlobalId, f.A.Name + " changed", downloaded);
            Assert.True(JsonDocument.Parse(applied).RootElement.GetProperty("updateResults")[0].GetProperty("success").GetBoolean(),
                $"An update with the downloaded EditDate was refused: {applied}");
            Assert.Equal(f.A.Name + " changed", await NameInSourceAsync(f, f.A.ObjectId));
            Assert.Equal(f.B.Name, await NameInSourceAsync(f, f.B.ObjectId));

            // The same date again is now stale: the record moved when it was changed.
            (_, string again) = await UpdateAsync(f, f.A.Token, f.A.GlobalId, "again", downloaded);
            Assert.Equal(412, JsonDocument.Parse(again).RootElement.GetProperty("updateResults")[0].GetProperty("error").GetProperty("code").GetInt32());

            // By object id, as a client without GlobalIDs sends it.
            long now = await EditDateAsync(f, f.A);
            (HttpStatusCode byId, string byIdBody) = await PostFormAsync($"{root}{f.Inbox}/applyEdits", f.A.Token, new()
            {
                ["f"] = "json",
                ["updates"] = JsonSerializer.Serialize(new[] { new { attributes = new Dictionary<string, object> { ["objectid"] = f.A.ObjectId, ["ad"] = f.A.Name + " by id" } } }),
                ["expectedEditDates"] = JsonSerializer.Serialize(new Dictionary<string, long> { [f.A.ObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = now }),
            });
            Assert.True(byId == HttpStatusCode.OK && byIdBody.Contains("\"success\":true", StringComparison.Ordinal), $"An update by object id answered {(int)byId}: {byIdBody}");

            // §5.7: a new repeat row is added through the Inbox under the member's own record, and refused under the other's.
            foreach ((Guid parent, bool expected) in new[] { (f.A.GlobalId, true), (f.B.GlobalId, false) })
            {
                (HttpStatusCode addedRow, string addedRowBody) = await PostFormAsync($"{root}{f.InboxRepeat}/applyEdits", f.A.Token, new()
                {
                    ["f"] = "json",
                    ["useGlobalIds"] = "true",
                    ["adds"] = $$$"""[{"attributes":{"globalid":"{{{Guid.NewGuid():B}}}","parentglobalid":"{{{parent:B}}}","kat_no":7}}]""",
                });
                Assert.True(addedRow == HttpStatusCode.OK && addedRowBody.Contains("\"success\":true", StringComparison.Ordinal) == expected,
                    $"A repeat row added through the Inbox under {(expected ? "A's own" : "B's")} record answered {(int)addedRow}: {addedRowBody}");
            }

            JsonElement aRows = await JsonAsync(f.A.Token, $"{root}{f.InboxRepeat}/query?where=1%3D1&outFields=*&f=json");
            Assert.Equal(2, aRows.GetProperty("features").GetArrayLength());
            JsonElement bRows = await JsonAsync(f.B.Token, $"{root}{f.InboxRepeat}/query?where=1%3D1&outFields=*&f=json");
            Assert.Equal(1, bRows.GetProperty("features").GetArrayLength());

            // The field names the form client reads, as the Inbox answers them.
            JsonElement fieldNames = await JsonAsync(f.A.Token, $"{root}{f.InboxRepeat}/query?where=1%3D1&outFields=*&f=json");
            string[] names = [.. fieldNames.GetProperty("fields").EnumerateArray().Select(x => x.GetProperty("name").GetString()!)];
            foreach (string name in (string[])["objectid", "globalid", "parentglobalid", "last_edited_date", "last_edited_user", "created_user"])
            {
                Assert.Contains(name, names);
            }

            // A record deleted on the server while the device held it: 404, by GlobalID.
            (HttpStatusCode deleted, string deleteBody) = await PostFormAsync($"{root}{f.Top.GetProperty("url").GetString()}/deleteFeatures", f.Admin,
                new() { ["f"] = "json", ["objectIds"] = f.A.ObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            Assert.True(deleted == HttpStatusCode.OK && deleteBody.Contains("\"success\":true", StringComparison.Ordinal), $"Deleting A's record answered {(int)deleted}: {deleteBody}");
            (_, string gone) = await UpdateAsync(f, f.A.Token, f.A.GlobalId, "gone", now);
            JsonElement goneResult = JsonDocument.Parse(gone).RootElement.GetProperty("updateResults")[0];
            Assert.False(goneResult.GetProperty("success").GetBoolean());
            Assert.Equal(404, goneResult.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(f.A.GlobalId, Guid.Parse(goneResult.GetProperty("globalId").GetString()!));

            // Nobody anonymous changes anything through it.
            using HttpRequestMessage anonymous = new(HttpMethod.Post, $"{root}{f.Inbox}/applyEdits")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["f"] = "json",
                    ["updates"] = JsonSerializer.Serialize(new[] { new { attributes = new Dictionary<string, object> { ["objectid"] = f.B.ObjectId, ["ad"] = "anonymous" } } }),
                }),
            };
            using HttpResponseMessage refused = await Http.SendAsync(anonymous);
            Assert.False((await refused.Content.ReadAsStringAsync()).Contains("\"success\":true", StringComparison.Ordinal));
            Assert.Equal(f.B.Name, await NameInSourceAsync(f, f.B.ObjectId));

            // expectedEditDates is refused where it is not compared, not ignored.
            (HttpStatusCode single, string singleBody) = await PostFormAsync($"{root}{f.Inbox}/updateFeatures", f.B.Token, new()
            {
                ["f"] = "json",
                ["features"] = JsonSerializer.Serialize(new[] { new { attributes = new Dictionary<string, object> { ["objectid"] = f.B.ObjectId, ["ad"] = "x" } } }),
                ["expectedEditDates"] = "{}",
            });
            Assert.True(single == HttpStatusCode.BadRequest, $"updateFeatures with expectedEditDates answered {(int)single}: {singleBody}");
        }
        finally
        {
            await TearDownAsync(root, run, f);
        }
    }

    [Fact]
    public async Task A_public_forms_Inbox_is_the_organisations_and_is_kept_when_published_again_and_dropped_when_turned_off()
    {
        InboxForm? f = null;
        string root = await RequireServerAsync();
        string run = Guid.NewGuid().ToString("N")[..6];

        try
        {
            f = await SetUpAsync(root, run, "public");

            using (HttpRequestMessage open = new(HttpMethod.Get, $"{root}/forms/{f.Item}/form.json"))
            {
                using HttpResponseMessage opened = await Http.SendAsync(open);
                string client = await opened.Content.ReadAsStringAsync();
                Assert.True(opened.StatusCode == HttpStatusCode.OK, $"An anonymous read of a public form answered {(int)opened.StatusCode}: {Head(client)}");
                Assert.False(client.Contains("inboxUrl", StringComparison.Ordinal) || client.Contains("inboxService", StringComparison.Ordinal) || client.Contains("\"inbox\":", StringComparison.Ordinal), $"An anonymous caller was told of the Inbox: {Head(client)}");
            }

            (HttpStatusCode memberOpen, string memberClient) = await RequestAsync(HttpMethod.Get, $"{root}/forms/{f.Item}/form.json", f.A.Token, null);
            Assert.True(memberOpen == HttpStatusCode.OK && memberClient.Contains("inboxUrl", StringComparison.Ordinal),
                $"A member was not told of the Inbox: {(int)memberOpen} {Head(memberClient)}");

            // §5.9 (b): shared with the organisation, not with everyone.
            using (HttpRequestMessage anonymous = new(HttpMethod.Get, $"{root}{f.Inbox}/query?where=1%3D1&outFields=*&f=json"))
            {
                using HttpResponseMessage answered = await Http.SendAsync(anonymous);
                string seen = await answered.Content.ReadAsStringAsync();
                Assert.False(answered.StatusCode == HttpStatusCode.OK && seen.Contains("\"features\"", StringComparison.Ordinal),
                    $"A public form's Inbox answered an anonymous query: {Head(seen)}");
            }

            JsonElement listed = await JsonAsync(f.Admin, $"{root}/admin/services/{f.FormId}/views?folder=hosted");
            Assert.Equal("organization", listed.GetProperty("views").EnumerateArray()
                .First(v => v.GetProperty("name").GetString() == f.ViewName).GetProperty("sharing").GetString());

            // Published again with a new question, the Inbox is made again and still each member's own.
            JsonObject changed = Model(f.FormId);
            ((JsonArray)changed["sheets"]![0]!["rows"]!).Insert(2, new JsonArray("text", "not", "Not"));
            (HttpStatusCode draft, string draftBody) = await RequestAsync(HttpMethod.Put, $"{root}/admin/forms/{f.Item}/model", f.Admin, changed.ToJsonString());
            Assert.True(draft == HttpStatusCode.OK, $"Saving the draft answered {(int)draft}: {draftBody}");
            (HttpStatusCode again, string againBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms/{f.Item}/publish", f.Admin, "{}");
            Assert.True(again == HttpStatusCode.OK, $"Publishing again answered {(int)again}: {againBody}");
            string inboxAgain = JsonDocument.Parse(againBody).RootElement.GetProperty("layers")[0].GetProperty("inboxUrl").GetString()!;
            JsonElement mine = await JsonAsync(f.B.Token, $"{root}{inboxAgain}/query?where=1%3D1&outFields=*&f=json");
            Assert.Equal([f.B.Name], mine.GetProperty("features").EnumerateArray().Select(r => r.GetProperty("attributes").GetProperty("ad").GetString() ?? string.Empty).ToArray());
            Assert.True(mine.GetProperty("fields").EnumerateArray().Any(x => x.GetProperty("name").GetString() == "not"),
                "The Inbox made again does not carry the new question's field.");

            // A filter the layer cannot read is refused with the parser's words, and nothing changes.
            (HttpStatusCode bad, string badBody) = await RequestAsync(HttpMethod.Put, $"{root}/admin/forms/{f.Item}/inbox", f.Admin,
                """{"enabled":true,"filter":"nosuch = ${username}"}""");
            Assert.True(bad == HttpStatusCode.BadRequest && badBody.Contains("nosuch", StringComparison.Ordinal), $"A bad filter answered {(int)bad}: {badBody}");
            (HttpStatusCode limit, _) = await RequestAsync(HttpMethod.Put, $"{root}/admin/forms/{f.Item}/inbox", f.Admin, """{"limit":5001}""");
            Assert.Equal(HttpStatusCode.BadRequest, limit);

            // A member who does not manage the form cannot change its Inbox.
            (HttpStatusCode stranger, _) = await RequestAsync(HttpMethod.Put, $"{root}/admin/forms/{f.Item}/inbox", f.A.Token, """{"enabled":false}""");
            Assert.NotEqual(HttpStatusCode.OK, stranger);

            // Turned off: the view is dropped and the publication says nothing of it.
            (HttpStatusCode off, string offBody) = await RequestAsync(HttpMethod.Put, $"{root}/admin/forms/{f.Item}/inbox", f.Admin, """{"enabled":false}""");
            Assert.True(off == HttpStatusCode.OK, $"Turning the Inbox off answered {(int)off}: {offBody}");
            Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(offBody).RootElement.GetProperty("service").ValueKind);
            (HttpStatusCode afterOff, _) = await RequestAsync(HttpMethod.Get, $"{root}{f.Inbox}?f=json", f.A.Token, null);
            Assert.NotEqual(HttpStatusCode.OK, afterOff);
            (_, string memberAfter) = await RequestAsync(HttpMethod.Get, $"{root}/forms/{f.Item}/form.json", f.A.Token, null);
            Assert.DoesNotContain("inboxUrl", memberAfter, StringComparison.Ordinal);
        }
        finally
        {
            await TearDownAsync(root, run, f);
        }
    }

    // ------------------------------------------------------------------------------------------------------- set-up

    private async Task<InboxForm> SetUpAsync(string root, string run, string sharing)
    {
        string? admin = await TokenAsync(root);
        Assert.False(admin is null, "No administrator credential.");
        string formId = "zz_inbox_" + run;

        string aToken = await NewMemberAsync(root, admin!, "zz_inbox_a_" + run);
        string bToken = await NewMemberAsync(root, admin!, "zz_inbox_b_" + run);

        (HttpStatusCode added, string addBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms", admin!,
            new JsonObject { ["title"] = "Inbox " + formId, ["model"] = Model(formId) }.ToJsonString());
        Assert.True(added == HttpStatusCode.Created, $"Adding the form answered {(int)added}: {addBody}");
        string item = JsonDocument.Parse(addBody).RootElement.GetProperty("id").GetString()!;

        // Turned on before publishing, as Studio lets a publisher do; the view is made when the form is published.
        (HttpStatusCode on, string onBody) = await RequestAsync(HttpMethod.Put, $"{root}/admin/forms/{item}/inbox", admin!, """{"enabled":true,"limit":40}""");
        Assert.True(on == HttpStatusCode.OK, $"Turning the Inbox on answered {(int)on}: {onBody}");
        (HttpStatusCode published, string publishBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms/{item}/publish", admin!, "{}");
        Assert.True(published == HttpStatusCode.OK, $"Publishing answered {(int)published}: {publishBody}");
        (HttpStatusCode shared, string sharedBody) = await RequestAsync(HttpMethod.Put, $"{root}/admin/forms/{item}/sharing", admin!,
            JsonSerializer.Serialize(new { sharing }));
        Assert.True(shared == HttpStatusCode.OK, $"Sharing answered {(int)shared}: {sharedBody}");
        JsonElement publication = JsonDocument.Parse(sharedBody).RootElement.GetProperty("publication").Clone();
        Assert.True(publication.TryGetProperty("inboxService", out _), $"The publication names no Inbox: {sharedBody}");

        JsonElement top = publication.GetProperty("layers").EnumerateArray().First(l => l.GetProperty("repeat").GetString() == "");
        JsonElement repeat = publication.GetProperty("layers").EnumerateArray().First(l => l.GetProperty("repeat").GetString() == "katlar");

        async Task<Record> FillAsync(string token, string name, double x, double y)
        {
            Guid global = Guid.NewGuid();
            (HttpStatusCode sent, string sentBody) = await PostFormAsync($"{root}{top.GetProperty("submitUrl").GetString()}/applyEdits", token, new()
            {
                ["f"] = "json",
                ["useGlobalIds"] = "true",
                ["adds"] = $$$"""[{"geometry":{"x":{{{x}}},"y":{{{y}}},"spatialReference":{"wkid":4326}},"attributes":{"globalid":"{{{global:B}}}","ad":"{{{name}}}"}}]""",
            });
            Assert.True(sent == HttpStatusCode.OK && sentBody.Contains("\"success\":true", StringComparison.Ordinal), $"{name} could not send: {sentBody}");
            int objectId = JsonDocument.Parse(sentBody).RootElement.GetProperty("addResults")[0].GetProperty("objectId").GetInt32();
            (HttpStatusCode sentRepeat, string repeatBody) = await PostFormAsync($"{root}{repeat.GetProperty("submitUrl").GetString()}/applyEdits", token, new()
            {
                ["f"] = "json",
                ["useGlobalIds"] = "true",
                ["adds"] = $$$"""[{"attributes":{"globalid":"{{{Guid.NewGuid():B}}}","parentglobalid":"{{{global:B}}}","kat_no":3}}]""",
            });
            Assert.True(sentRepeat == HttpStatusCode.OK && repeatBody.Contains("\"success\":true", StringComparison.Ordinal), $"{name}'s repeat could not be sent: {repeatBody}");
            int repeatId = JsonDocument.Parse(repeatBody).RootElement.GetProperty("addResults")[0].GetProperty("objectId").GetInt32();
            (HttpStatusCode photo, string photoBody) = await AttachAsync(root, token,
                $"{top.GetProperty("submitUrl").GetString()}/{objectId}/addAttachment?parentGlobalId={Uri.EscapeDataString(global.ToString("B"))}");
            Assert.True(photo == HttpStatusCode.OK && photoBody.Contains("\"success\":true", StringComparison.Ordinal), $"{name}'s photograph could not be sent: {photoBody}");
            return new Record(token, name, global, objectId, repeatId, x, y);
        }

        Record a = await FillAsync(aToken, $"Alfa-{run}", AX, AY);
        Record b = await FillAsync(bToken, $"Bravo-{run}", BX, BY);
        return new InboxForm(root, admin!, formId, item, run, publication, a, b);
    }

    private async Task TearDownAsync(string root, string run, InboxForm? f)
    {
        string? admin = f?.Admin ?? await TokenAsync(root);

        if (admin is null)
        {
            return;
        }

        string formId = "zz_inbox_" + run;
        await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{formId}_inbox?folder=hosted&drop=true", admin, null);
        await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{formId}_submit?folder=hosted&drop=true", admin, null);
        await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{formId}?folder=hosted&drop=true", admin, null);

        if (f is not null)
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/content/documents/{f.Item}", admin, null);
        }

        await RequestAsync(HttpMethod.Delete, $"{root}/admin/members/zz_inbox_a_{run}?deleteOwned=true", admin, null);
        await RequestAsync(HttpMethod.Delete, $"{root}/admin/members/zz_inbox_b_{run}?deleteOwned=true", admin, null);
    }

    // ------------------------------------------------------------------------------------------------------ helpers

    private async Task<long> EditDateAsync(InboxForm f, Record r)
    {
        JsonElement rows = await JsonAsync(r.Token, $"{f.Root}{f.Inbox}/query?objectIds={r.ObjectId}&outFields=last_edited_date&f=json");
        return rows.GetProperty("features")[0].GetProperty("attributes").GetProperty("last_edited_date").GetInt64();
    }

    private async Task<string?> NameInSourceAsync(InboxForm f, int objectId)
    {
        JsonElement rows = await JsonAsync(f.Admin, $"{f.Root}{f.Top.GetProperty("url").GetString()}/query?objectIds={objectId}&outFields=ad&f=json");
        return rows.GetProperty("features").GetArrayLength() == 0 ? null : rows.GetProperty("features")[0].GetProperty("attributes").GetProperty("ad").GetString();
    }

    private Task<(HttpStatusCode Status, string Body)> UpdateAsync(InboxForm f, string token, Guid global, string name, long? expected)
    {
        Dictionary<string, string> form = new()
        {
            ["f"] = "json",
            ["useGlobalIds"] = "true",
            ["updates"] = $$$"""[{"attributes":{"globalid":"{{{global:B}}}","ad":"{{{name}}}"}}]""",
        };

        if (expected is { } moment)
        {
            form["expectedEditDates"] = $$$"""{"{{{global:B}}}":{{{moment}}}}""";
        }

        return PostFormAsync($"{f.Root}{f.Inbox}/applyEdits", token, form);
    }

    private async Task<JsonElement> JsonAsync(string token, string url)
    {
        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Get, url, token, null);
        Assert.True(status == HttpStatusCode.OK, $"{url} answered {(int)status}: {Head(body)}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<(HttpStatusCode Status, string Body)> PostFormAsync(string url, string token, Dictionary<string, string> form)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(HttpStatusCode Status, string Body)> AttachAsync(string root, string token, string path)
    {
        using MultipartFormDataContent photo = new();
        photo.Add(new StringContent("json"), "f");
        photo.Add(new StringContent("foto"), "keywords");
        ByteArrayContent png = new([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
        png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        photo.Add(png, "attachment", "foto.png");
        using HttpRequestMessage attach = new(HttpMethod.Post, $"{root}{path}") { Content = photo };
        attach.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage attached = await Http.SendAsync(attach);
        return (attached.StatusCode, await attached.Content.ReadAsStringAsync());
    }

    private async Task<string> NewMemberAsync(string root, string token, string name)
    {
        await RequestAsync(HttpMethod.Delete, $"{root}/admin/members/{name}?deleteOwned=true", token, null);
        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Post, $"{root}/admin/members", token,
            JsonSerializer.Serialize(new { name, role = "user", userType = "creator" }));
        Assert.True((int)status is >= 200 and < 300, $"Making the member '{name}' answered {(int)status}.");
        string generated = JsonDocument.Parse(body).RootElement.GetProperty("password").GetString()!;
        string first = await LoginAsync(root, name, generated);
        using HttpRequestMessage change = new(HttpMethod.Post, $"{root}/rest/auth/password");
        change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", first);
        change.Content = new StringContent(JsonSerializer.Serialize(new { currentPassword = generated, newPassword = Password }), Encoding.UTF8, "application/json");
        using HttpResponseMessage changed = await Http.SendAsync(change);
        Assert.True(changed.IsSuccessStatusCode, $"Setting '{name}''s password answered {(int)changed.StatusCode}.");
        return await LoginAsync(root, name, Password);
    }

    private async Task<string> LoginAsync(string root, string name, string password)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/rest/auth/login");
        request.Content = new StringContent(JsonSerializer.Serialize(new { name, password }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Signing in as '{name}' answered {(int)response.StatusCode}.");
        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }

    private static string Digits(double value) => value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);

    private static string Head(string text) => text.Length > 500 ? text[..500] : text;
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The 12.2 band-1 query gaps of ADR-309: <c>queryAttachments</c> counts and filters, <c>gdbVersion</c> refused when it
/// names another version, <c>outDistance</c> refused by name, and <c>queryBins</c>.
/// </summary>
/// <remarks>
/// Written 2026-11-21. <c>returnCountOnly</c> on <c>queryAttachments</c> was ignored and the answer was the list, which is
/// the silent degradation ADR-008 §2 forbids; <c>gdbVersion</c> sat on the ignored list while a comment said refused.
/// Each test makes a layer of its own and drops it, so none depends on what another leaves.
/// </remarks>
[Collection("catalogue walk")]
public sealed class QueryAttachmentsBinsAndVersionsTests : ArcGisClient
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    /// <summary>A hosted layer of two features, three attachments: feature one has two, feature two has one.</summary>
    private async Task<(string Root, string Token, string Feature, string[] Parts, long First, long Second)> AttachedLayerAsync(string prefix)
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        (string feature, string[] parts) = await DefineAsync(root, token!, prefix, "[{\"name\":\"label\",\"type\":\"text\",\"nullable\":true}]");

        using HttpClient http = Client();
        JsonElement added = await PostFormAsync(http, root, token!, $"{feature}/addFeatures",
            "[{\"geometry\":{\"x\":1000,\"y\":2000,\"spatialReference\":{\"wkid\":3857}},\"attributes\":{\"label\":\"a\"}},"
            + "{\"geometry\":{\"x\":1100,\"y\":2000,\"spatialReference\":{\"wkid\":3857}},\"attributes\":{\"label\":\"b\"}}]");
        long first = added.GetProperty("addResults")[0].GetProperty("objectId").GetInt64();
        long second = added.GetProperty("addResults")[1].GetProperty("objectId").GetInt64();

        await AttachAsync(http, root, token!, $"{feature}/{first}/addAttachment", "photo-one.png", 11);
        await AttachAsync(http, root, token!, $"{feature}/{first}/addAttachment", "sketch.png", 40);
        await AttachAsync(http, root, token!, $"{feature}/{second}/addAttachment", "photo-two.png", 200);

        return (root, token!, feature, parts, first, second);
    }

    private async Task<(string Feature, string[] Parts)> DefineAsync(string root, string token, string prefix, string fieldsJson)
    {
        string layer = prefix + Guid.NewGuid().ToString("N")[..8];

        (HttpStatusCode defined, string design) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/hosted/define",
            token,
            "{\"name\":\"" + layer + "\",\"geometryType\":\"Point\",\"fields\":" + fieldsJson + "}");

        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}: {design}");

        string feature = JsonDocument.Parse(design).RootElement
            .GetProperty("services").GetProperty("feature").GetString()!;

        return (feature, feature.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task DropAsync(string root, string token, string[] parts) =>
        await RequestAsync(
            HttpMethod.Delete,
            $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true",
            token,
            json: null);

    private static HttpClient Client() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private async Task<(HttpStatusCode Status, JsonElement Body)> AskAsync(string root, string token, string path)
    {
        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Get, $"{root}{path}", token, json: null);
        return (status, JsonDocument.Parse(body).RootElement.Clone());
    }

    // ---------- queryAttachments ----------

    [Fact]
    public async Task Query_attachments_with_returnCountOnly_answers_a_count_per_feature_and_no_list()
    {
        (string root, string token, string feature, string[] parts, long first, long second) = await AttachedLayerAsync("zz_qa_count_");

        try
        {
            (HttpStatusCode status, JsonElement body) = await AskAsync(root, token,
                $"{feature}/queryAttachments?objectIds={first},{second}&returnCountOnly=true&f=json");

            Assert.Equal(HttpStatusCode.OK, status);

            JsonElement[] groups = [.. body.GetProperty("attachmentGroups").EnumerateArray()];
            Assert.Equal(2, groups.Length);
            Assert.Equal(2, groups.Single(g => g.GetProperty("parentObjectId").GetInt64() == first).GetProperty("count").GetInt32());
            Assert.Equal(1, groups.Single(g => g.GetProperty("parentObjectId").GetInt64() == second).GetProperty("count").GetInt32());
            Assert.All(groups, g => Assert.False(g.TryGetProperty("attachmentInfos", out _), "a count answer carries no list"));

            // Without it the answer is the list, as before.
            (_, JsonElement list) = await AskAsync(root, token, $"{feature}/queryAttachments?objectIds={first}&f=json");
            Assert.Equal(2, list.GetProperty("attachmentGroups")[0].GetProperty("attachmentInfos").GetArrayLength());

            // A count follows the same filters as the list: only a feature with a matching attachment is named.
            (_, JsonElement filtered) = await AskAsync(root, token,
                $"{feature}/queryAttachments?objectIds={first},{second}&returnCountOnly=true&size=100,300&f=json");
            JsonElement only = filtered.GetProperty("attachmentGroups").EnumerateArray().Single();
            Assert.Equal(second, only.GetProperty("parentObjectId").GetInt64());
            Assert.Equal(1, only.GetProperty("count").GetInt32());
        }
        finally
        {
            await DropAsync(root, token, parts);
        }
    }

    [Fact]
    public async Task Query_attachments_filters_by_size_expression_and_pages_and_refuses_what_it_does_not_do()
    {
        (string root, string token, string feature, string[] parts, long first, long second) = await AttachedLayerAsync("zz_qa_filter_");

        try
        {
            string ids = $"objectIds={first},{second}";

            JsonElement[] Infos(JsonElement body) =>
            [
                .. body.GetProperty("attachmentGroups").EnumerateArray()
                    .SelectMany(g => g.GetProperty("attachmentInfos").EnumerateArray()),
            ];

            // size=min,max in bytes.
            (_, JsonElement bySize) = await AskAsync(root, token, $"{feature}/queryAttachments?{ids}&size=30,100&f=json");
            Assert.Equal(["sketch.png"], Infos(bySize).Select(i => i.GetProperty("name").GetString()!));

            // attachmentsDefinitionExpression, by the field names the reference lists.
            (_, JsonElement byName) = await AskAsync(root, token,
                $"{feature}/queryAttachments?{ids}&attachmentsDefinitionExpression={Uri.EscapeDataString("ATT_NAME LIKE 'photo%'")}&f=json");
            Assert.Equal(["photo-one.png", "photo-two.png"], Infos(byName).Select(i => i.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));

            (_, JsonElement byBoth) = await AskAsync(root, token,
                $"{feature}/queryAttachments?{ids}&attachmentsDefinitionExpression={Uri.EscapeDataString("ATT_NAME LIKE 'photo%' AND DATA_SIZE > 100")}&f=json");
            Assert.Equal(["photo-two.png"], Infos(byBoth).Select(i => i.GetProperty("name").GetString()!));

            // A page of attachments, cut in a stable order (feature, then attachment id).
            (_, JsonElement page1) = await AskAsync(root, token, $"{feature}/queryAttachments?{ids}&resultRecordCount=2&f=json");
            (_, JsonElement page2) = await AskAsync(root, token, $"{feature}/queryAttachments?{ids}&resultOffset=2&resultRecordCount=2&f=json");
            Assert.Equal(2, Infos(page1).Length);
            Assert.Equal(["photo-two.png"], Infos(page2).Select(i => i.GetProperty("name").GetString()!));

            // What it does not do is refused with a reason, never ignored.
            foreach (string refused in (string[])[
                "orderByFields=size DESC",
                "attachmentsWhere=1%3D1",
                "size=abc",
                "size=100,10",
                "attachmentsDefinitionExpression=" + Uri.EscapeDataString("nosuchfield = 1"),
                "attachmentsDefinitionExpression=" + Uri.EscapeDataString("ATT_NAME = 'x'; drop table y"),
                "resultRecordCount=0",
                "resultOffset=-1",
            ])
            {
                (HttpStatusCode status, JsonElement body) = await AskAsync(root, token, $"{feature}/queryAttachments?{ids}&{refused}&f=json");

                Assert.True(status == HttpStatusCode.BadRequest, $"{refused} answered {(int)status}: {body}");
                Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetProperty("message").GetString()), refused);
            }
        }
        finally
        {
            await DropAsync(root, token, parts);
        }
    }

    [Fact]
    public async Task The_layer_document_advertises_what_query_attachments_does()
    {
        (string root, string token, string feature, string[] parts, _, _) = await AttachedLayerAsync("zz_qa_doc_");

        try
        {
            (_, JsonElement layer) = await AskAsync(root, token, $"{feature}?f=json");
            JsonElement advanced = layer.GetProperty("advancedQueryCapabilities");

            Assert.True(advanced.GetProperty("supportsQueryAttachments").GetBoolean());
            Assert.True(advanced.GetProperty("supportsQueryAttachmentsCountOnly").GetBoolean());
            Assert.True(advanced.GetProperty("supportsQueryAttachmentsWithReturnUrl").GetBoolean());

            // Ordering is refused, so it is not advertised.
            Assert.False(advanced.GetProperty("supportsQueryAttachmentOrderByFields").GetBoolean());

            Dictionary<string, bool> properties = layer.GetProperty("attachmentProperties").EnumerateArray()
                .ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("isEnabled").GetBoolean());
            Assert.True(properties["keywords"]);
            Assert.True(properties["size"]);
            Assert.False(properties["exifInfo"], "no EXIF is read, so it must not say it is");
        }
        finally
        {
            await DropAsync(root, token, parts);
        }
    }

    // ---------- gdbVersion, outDistance ----------

    [Fact]
    public async Task A_version_other_than_the_default_is_refused_where_it_would_change_the_answer()
    {
        (string root, string token, string feature, string[] parts, long first, _) = await AttachedLayerAsync("zz_qa_gdb_");

        try
        {
            // The default version, in every spelling, is the one there is.
            foreach (string version in (string[])["", "sde.DEFAULT", "SDE.default", "DEFAULT"])
            {
                (HttpStatusCode ok, _) = await AskAsync(root, token,
                    $"{feature}/query?where=1%3D1&returnCountOnly=true&gdbVersion={Uri.EscapeDataString(version)}&f=json");
                Assert.Equal(HttpStatusCode.OK, ok);
            }

            (HttpStatusCode read, JsonElement refusal) = await AskAsync(root, token, $"{feature}/query?where=1%3D1&gdbVersion=Edits&f=json");
            Assert.Equal(HttpStatusCode.BadRequest, read);
            Assert.Contains("gdbVersion=Edits", refusal.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

            (HttpStatusCode related, _) = await AskAsync(root, token,
                $"{feature}/queryRelatedRecords?objectIds={first}&relationshipId=1&gdbVersion=Edits&f=json");
            Assert.Equal(HttpStatusCode.BadRequest, related);

            // An edit aimed at another version is not applied to the default one.
            using HttpClient http = Client();
            using FormUrlEncodedContent form = new([
                new("adds", "[{\"geometry\":{\"x\":5,\"y\":5,\"spatialReference\":{\"wkid\":3857}},\"attributes\":{\"label\":\"z\"}}]"),
                new("gdbVersion", "Edits"),
                new("f", "json"),
            ]);
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{feature}/applyEdits")) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage edit = await http.SendAsync(request);
            string answer = await edit.Content.ReadAsStringAsync();
            Assert.True(edit.StatusCode == HttpStatusCode.BadRequest, $"applyEdits with another version answered {(int)edit.StatusCode}: {answer}");

            (_, JsonElement count) = await AskAsync(root, token, $"{feature}/query?where=1%3D1&returnCountOnly=true&f=json");
            Assert.Equal(2, count.GetProperty("count").GetInt32());
        }
        finally
        {
            await DropAsync(root, token, parts);
        }
    }

    [Fact]
    public async Task OutDistance_is_refused_by_name_when_it_has_a_value()
    {
        (string root, string token, string feature, string[] parts, _, _) = await AttachedLayerAsync("zz_qa_dist_");

        try
        {
            (HttpStatusCode empty, _) = await AskAsync(root, token, $"{feature}/query?where=1%3D1&returnCountOnly=true&outDistance=&f=json");
            Assert.Equal(HttpStatusCode.OK, empty);

            (HttpStatusCode given, JsonElement body) = await AskAsync(root, token, $"{feature}/query?where=1%3D1&outDistance=5&f=json");
            Assert.Equal(HttpStatusCode.BadRequest, given);
            Assert.Contains("outDistance", body.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            await DropAsync(root, token, parts);
        }
    }

    // ---------- queryBins ----------

    /// <summary>Ten points: <c>v</c> is 0, 10, 20 … 90, and <c>d</c> is one day apart from 2025-01-01 00:00 UTC.</summary>
    private async Task<(string Root, string Token, string Feature, string[] Parts)> BinnedLayerAsync()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        (string feature, string[] parts) = await DefineAsync(root, token!, "zz_qb_",
            "[{\"name\":\"v\",\"type\":\"integer\",\"nullable\":true},{\"name\":\"d\",\"type\":\"date\",\"nullable\":true},{\"name\":\"g\",\"type\":\"text\",\"nullable\":true}]");

        long day = 86_400_000L;
        long origin = 1_735_689_600_000L; // 2025-01-01T00:00:00Z

        List<string> rows = [];

        for (int i = 0; i < 10; i++)
        {
            rows.Add(string.Create(CultureInfo.InvariantCulture,
                $"{{\"geometry\":{{\"x\":{1000 + i},\"y\":2000,\"spatialReference\":{{\"wkid\":3857}}}},\"attributes\":{{\"v\":{i * 10},\"d\":{origin + (i * day)},\"g\":\"{(i < 5 ? "lo" : "hi")}\"}}}}"));
        }

        using HttpClient http = Client();
        JsonElement added = await PostFormAsync(http, root, token!, $"{feature}/addFeatures", "[" + string.Join(",", rows) + "]");
        Assert.True(added.GetProperty("addResults").EnumerateArray().All(r => r.GetProperty("success").GetBoolean()), added.ToString());

        return (root, token!, feature, parts);
    }

    private static string Bin(string json) => "bin=" + Uri.EscapeDataString(json);

    [Fact]
    public async Task Query_bins_divides_a_numeric_field_by_boundaries_interval_and_count_with_empty_bins_filled()
    {
        (string root, string token, string feature, string[] parts) = await BinnedLayerAsync();

        try
        {
            // Boundaries: lower inclusive, upper exclusive, the last upper inclusive.
            (HttpStatusCode status, JsonElement bounded) = await AskAsync(root, token,
                $"{feature}/queryBins?{Bin("{\"type\":\"fixedBoundariesBin\",\"onField\":\"v\",\"parameters\":{\"boundaries\":[0,30,60,90]}}")}"
                + "&outStatistics=" + Uri.EscapeDataString("[{\"statisticType\":\"count\",\"onStatisticField\":\"objectid\",\"outStatisticFieldName\":\"n\"},"
                    + "{\"statisticType\":\"sum\",\"onStatisticField\":\"v\",\"outStatisticFieldName\":\"total\"}]")
                + "&f=json");

            Assert.True(status == HttpStatusCode.OK, bounded.ToString());

            (double Lower, double Upper, long N, double Total)[] got =
            [
                .. bounded.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("attributes")).Select(a =>
                    (a.GetProperty("lowerBoundary").GetDouble(), a.GetProperty("upperBoundary").GetDouble(),
                        a.GetProperty("n").GetInt64(), a.GetProperty("total").GetDouble())),
            ];

            Assert.Equal([(0, 30, 3, 30), (30, 60, 3, 120), (60, 90, 4, 300)], got);
            Assert.Equal(
                ["lowerBoundary", "upperBoundary", "n", "total"],
                bounded.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!));

            // A fixed interval with a start and an end holds the empty bins too, and the default statistic is `frequency`.
            (_, JsonElement interval) = await AskAsync(root, token,
                $"{feature}/queryBins?{Bin("{\"type\":\"fixedIntervalBin\",\"onField\":\"v\",\"parameters\":{\"interval\":20,\"start\":-40,\"end\":100}}")}&f=json");
            long[] frequency = [.. interval.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("attributes").GetProperty("frequency").GetInt64())];
            Assert.Equal([0, 0, 2, 2, 2, 2, 2], frequency);
            Assert.Equal(-40, interval.GetProperty("features")[0].GetProperty("attributes").GetProperty("lowerBoundary").GetDouble());

            // The where clause filters what is binned, and a count of nothing is 0 rather than a missing bin.
            (_, JsonElement filtered) = await AskAsync(root, token,
                $"{feature}/queryBins?{Bin("{\"type\":\"autoIntervalBin\",\"onField\":\"v\",\"parameters\":{\"numberOfBins\":3,\"start\":0,\"end\":90}}")}"
                + $"&where={Uri.EscapeDataString("g = 'lo'")}&f=json");
            Assert.Equal([3, 2, 0], filtered.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("attributes").GetProperty("frequency").GetInt64()));

            // binOrder, hideUpperBound and the aliases.
            (_, JsonElement shaped) = await AskAsync(root, token,
                $"{feature}/queryBins?{Bin("{\"type\":\"fixedBoundariesBin\",\"onField\":\"v\",\"parameters\":{\"boundaries\":[0,50,100]},\"hideUpperBound\":true}")}"
                + "&binOrder=DESC&lowerBoundaryAlias=lo&f=json");
            JsonElement[] features = [.. shaped.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("attributes"))];
            Assert.Equal([50, 0], features.Select(a => a.GetProperty("lo").GetDouble()));
            Assert.All(features, a => Assert.False(a.TryGetProperty("upperBoundary", out _)));
        }
        finally
        {
            await DropAsync(root, token, parts);
        }
    }

    [Fact]
    public async Task Query_bins_divides_a_date_field_by_a_unit_and_an_offset()
    {
        (string root, string token, string feature, string[] parts) = await BinnedLayerAsync();

        try
        {
            long origin = 1_735_689_600_000L;
            long day = 86_400_000L;

            // Days, three at a time, from the stated start; the last bin stops at the data unless the full interval is asked for.
            (HttpStatusCode status, JsonElement bins) = await AskAsync(root, token,
                $"{feature}/queryBins?{Bin($"{{\"type\":\"dateBin\",\"onField\":\"d\",\"parameters\":{{\"unit\":\"day\",\"number\":3,\"start\":{origin}}}}}")}&f=json");

            Assert.True(status == HttpStatusCode.OK, bins.ToString());

            JsonElement[] rows = [.. bins.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("attributes"))];
            Assert.Equal([3, 3, 3, 1], rows.Select(a => a.GetProperty("frequency").GetInt64()));
            Assert.Equal(origin, rows[0].GetProperty("lowerBoundary").GetInt64());
            Assert.Equal(origin + (3 * day), rows[0].GetProperty("upperBoundary").GetInt64());
            Assert.Equal(origin + (9 * day), rows[3].GetProperty("upperBoundary").GetInt64());
            Assert.Equal("esriFieldTypeDate", bins.GetProperty("fields")[0].GetProperty("type").GetString());

            (_, JsonElement full) = await AskAsync(root, token,
                $"{feature}/queryBins?{Bin($"{{\"type\":\"dateBin\",\"onField\":\"d\",\"parameters\":{{\"unit\":\"day\",\"number\":3,\"start\":{origin},\"returnFullIntervalBin\":true}}}}")}&f=json");
            Assert.Equal(origin + (12 * day), full.GetProperty("features")[3].GetProperty("attributes").GetProperty("upperBoundary").GetInt64());

            // An offset moves every boundary: with 12 hours the bins begin at noon, and the first holds the first day's noon-to-noon.
            (_, JsonElement shifted) = await AskAsync(root, token,
                $"{feature}/queryBins?{Bin($"{{\"type\":\"dateBin\",\"onField\":\"d\",\"parameters\":{{\"unit\":\"day\",\"start\":{origin - (12 * 3_600_000L)},\"offset\":{{\"number\":12,\"unit\":\"hour\"}}}}}}")}&f=json");
            Assert.Equal(10, shifted.GetProperty("features").GetArrayLength());

            // Calendar months: all ten rows are in January 2025, and the bin is the data's extent.
            (_, JsonElement months) = await AskAsync(root, token,
                $"{feature}/queryBins?{Bin("{\"type\":\"dateBin\",\"onField\":\"d\",\"parameters\":{\"unit\":\"month\"}}")}&f=json");
            JsonElement month = months.GetProperty("features").EnumerateArray().Single().GetProperty("attributes");
            Assert.Equal(10, month.GetProperty("frequency").GetInt64());
            Assert.Equal(origin, month.GetProperty("lowerBoundary").GetInt64());

            // Weeks follow firstDayOfWeek (1 Monday .. 7 Sunday): 2025-01-01 is a Wednesday.
            (_, JsonElement weeks) = await AskAsync(root, token,
                $"{feature}/queryBins?{Bin("{\"type\":\"dateBin\",\"onField\":\"d\",\"parameters\":{\"unit\":\"week\"},\"firstDayOfWeek\":1}")}&f=json");
            Assert.Equal([5, 5], weeks.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("attributes").GetProperty("frequency").GetInt64()));
        }
        finally
        {
            await DropAsync(root, token, parts);
        }
    }

    [Fact]
    public async Task Query_bins_refuses_by_name_what_it_does_not_do()
    {
        (string root, string token, string feature, string[] parts) = await BinnedLayerAsync();

        try
        {
            foreach (string refused in (string[])[
                Bin("{\"type\":\"lodBin\",\"onField\":\"v\",\"parameters\":{}}"),
                Bin("{\"type\":\"classificationBin\",\"onField\":\"v\",\"parameters\":{}}"),
                Bin("{\"type\":\"fixedIntervalBin\",\"onField\":\"v\",\"parameters\":{\"interval\":5},\"stackBy\":[\"g\"]}"),
                Bin("{\"type\":\"fixedIntervalBin\",\"onField\":\"v\",\"parameters\":{\"interval\":5},\"splitBy\":\"g\"}"),
                Bin("{\"type\":\"fixedIntervalBin\",\"onField\":\"v\",\"parameters\":{\"interval\":5},\"transformation\":\"naturalLog\"}"),
                Bin("{\"type\":\"dateBin\",\"onField\":\"d\",\"parameters\":{\"unit\":\"day\",\"snapToData\":\"last\"}}"),
                Bin("{\"type\":\"fixedIntervalBin\",\"onField\":\"d\",\"parameters\":{\"interval\":5}}"),
                Bin("{\"type\":\"dateBin\",\"onField\":\"v\",\"parameters\":{\"unit\":\"day\"}}"),
                Bin("{\"type\":\"fixedIntervalBin\",\"onField\":\"nosuch\",\"parameters\":{\"interval\":5}}"),
                Bin("{\"type\":\"fixedIntervalBin\",\"onField\":\"v\",\"parameters\":{\"interval\":0}}"),
                Bin("{\"type\":\"fixedBoundariesBin\",\"onField\":\"v\",\"parameters\":{\"boundaries\":[5,1]}}"),
                Bin("{\"type\":\"fixedIntervalBin\",\"onField\":\"v\",\"parameters\":{\"interval\":0.0001}}"),
                Bin("{\"type\":\"fixedIntervalBin\",\"onField\":\"v\",\"parameters\":{\"interval\":5}}") + "&async=true",
                Bin("{\"type\":\"fixedIntervalBin\",\"onField\":\"v\",\"parameters\":{\"interval\":5}}") + "&returnCentroid=true",
                Bin("{\"type\":\"fixedIntervalBin\",\"onField\":\"v\",\"parameters\":{\"interval\":5}}") + "&outTimeReference=" + Uri.EscapeDataString("{\"timeZone\":\"Pacific Standard Time\"}"),
                Bin("{\"type\":\"fixedIntervalBin\",\"onField\":\"v\",\"parameters\":{\"interval\":5}}") + "&orderByFields=v",
                "where=1%3D1",
            ])
            {
                (HttpStatusCode status, JsonElement body) = await AskAsync(root, token, $"{feature}/queryBins?{refused}&f=json");

                Assert.True(status == HttpStatusCode.BadRequest, $"{refused} answered {(int)status}: {body}");
                Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetProperty("message").GetString()), refused);
            }
        }
        finally
        {
            await DropAsync(root, token, parts);
        }
    }

    [Fact]
    public async Task The_layer_document_advertises_query_bins_as_it_is_answered()
    {
        (string root, string token, string feature, string[] parts) = await BinnedLayerAsync();

        try
        {
            (_, JsonElement layer) = await AskAsync(root, token, $"{feature}?f=json");
            JsonElement caps = layer.GetProperty("queryBinsCapabilities");

            foreach (string yes in (string[])["supportsAutoIntervalBin", "supportsFixedIntervalBin", "supportsFixedBoundariesBin", "supportsDateBin", "supportsFirstDayOfWeek", "supportsReturnFullIntervalBin"])
            {
                Assert.True(caps.GetProperty(yes).GetBoolean(), yes);
            }

            foreach (string no in (string[])["supportsAsync", "supportsStackBy", "supportsSplitBy", "supportsNormalization", "supportsClassificationBin"])
            {
                Assert.False(caps.GetProperty(no).GetBoolean(), no);
            }
        }
        finally
        {
            await DropAsync(root, token, parts);
        }
    }

    // ---------- helpers ----------

    private static async Task<JsonElement> PostFormAsync(HttpClient http, string root, string token, string path, string features)
    {
        using FormUrlEncodedContent form = new([new("features", features), new("f", "json")]);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{path}")) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await http.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task AttachAsync(HttpClient http, string root, string token, string path, string name, int bytes)
    {
        using MultipartFormDataContent form = new();
        byte[] content = [.. Png, .. new byte[bytes - Png.Length]];
        ByteArrayContent file = new(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "attachment", name);
        form.Add(new StringContent("json"), "f");

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{path}")) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"{path} answered {(int)response.StatusCode}: {body}");
    }
}

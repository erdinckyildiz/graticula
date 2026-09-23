using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// That a relationship is named by an integer, and says which layer it relates to — V-46.
/// </summary>
/// <remarks>
/// <para>
/// <b>The third ArcGIS review, decided by the owner 2026-09-23.</b> A layer document's
/// <c>relationships[].id</c> was the row's uuid and <c>relatedTableId</c> was missing, so a client that parses
/// both as integers — which is every ArcGIS client — could not follow a relationship at all.
/// </para>
/// <para>
/// <b>Declared here and removed again</b>, between two layers of the multi-layer fixture joined on their object
/// ids: a relationship nobody would declare, and the smallest one this suite can declare anywhere, because both
/// columns exist on every layer and have the same type. Until this test nothing declared a relationship end to
/// end; the only tests here were about refusing one.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class ARelationshipIsNumberedAsArcGisNumbersItTests : ArcGisClient
{
    [Fact]
    public async Task The_document_numbers_it_and_both_ids_answer_related_records()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string? service = Environment.GetEnvironmentVariable(MultiLayerServiceConformanceTests.ServiceVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(service),
            $"{MultiLayerServiceConformanceTests.ServiceVariable} is not set, so this test FAILS rather than skips.");

        string face = $"{root}/rest/services/{service}/FeatureServer";

        (_, string listed) = await RequestAsync(HttpMethod.Get, $"{face}?f=json", token, null);

        JsonElement[] layers =
        [
            .. JsonDocument.Parse(listed).RootElement.GetProperty("layers").EnumerateArray()
                .Where(l => !l.TryGetProperty("subLayerIds", out JsonElement sub) || sub.ValueKind != JsonValueKind.Array),
        ];

        Assert.True(layers.Length >= 2, $"{service} lists fewer than two feature layers: {listed}");

        int originId = layers[0].GetProperty("id").GetInt32();
        int relatedId = layers[1].GetProperty("id").GetInt32();

        JsonElement origin = await LayerAsync(face, originId, token);
        JsonElement related = await LayerAsync(face, relatedId, token);

        string name = $"zz_v46_{Guid.NewGuid():N}"[..20];

        (HttpStatusCode declared, string body) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/relationships",
            token,
            JsonSerializer.Serialize(new
            {
                name,
                originLayer = origin.GetProperty("name").GetString(),
                originKey = origin.GetProperty("objectIdField").GetString(),
                relatedLayer = related.GetProperty("name").GetString(),
                relatedKey = related.GetProperty("objectIdField").GetString(),
                cardinality = "OneToMany",
            }));

        Assert.True(declared == HttpStatusCode.Created, $"declaring the relationship answered {(int)declared}: {body}");

        JsonElement created = JsonDocument.Parse(body).RootElement;
        string key = created.GetProperty("id").GetString()!;
        int number = created.GetProperty("number").GetInt32();

        try
        {
            JsonElement reported = (await LayerAsync(face, originId, token)).GetProperty("relationships")
                .EnumerateArray().Single(r => r.GetProperty("name").GetString() == name);

            Assert.Equal(JsonValueKind.Number, reported.GetProperty("id").ValueKind);
            Assert.Equal(number, reported.GetProperty("id").GetInt32());
            Assert.Equal(relatedId, reported.GetProperty("relatedTableId").GetInt32());

            (_, string sample) = await RequestAsync(
                HttpMethod.Get, $"{face}/{originId}/query?where=1%3D1&returnIdsOnly=true&f=json", token, null);

            long objectId = JsonDocument.Parse(sample).RootElement.GetProperty("objectIds")
                .EnumerateArray().First().GetInt64();

            // The integer ArcGIS clients send, and the uuid a client may have stored before.
            foreach (string asked in (string[])[number.ToString(System.Globalization.CultureInfo.InvariantCulture), key])
            {
                (HttpStatusCode status, string answer) = await RequestAsync(
                    HttpMethod.Get,
                    $"{face}/{originId}/queryRelatedRecords?relationshipId={asked}&objectIds={objectId}&outFields=*&f=json",
                    token,
                    null);

                Assert.True(status == HttpStatusCode.OK, $"relationshipId={asked} answered {(int)status}: {answer}");
                Assert.Equal(number, JsonDocument.Parse(answer).RootElement.GetProperty("relationshipId").GetInt32());
            }
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/relationships/{key}", token, null);
        }
    }

    private async Task<JsonElement> LayerAsync(string face, int id, string token)
    {
        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Get, $"{face}/{id}?f=json", token, null);

        Assert.True(status == HttpStatusCode.OK, $"layer {id} answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }
}

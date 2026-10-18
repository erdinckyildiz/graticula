using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// <c>queryRelatedRecords</c> writes a related row as the query writes it — a date as epoch milliseconds — and sends
/// only the fields <c>outFields</c> names, with the object id. V-102, found 2026-10-19 while building the showcase's
/// example service: the date came back as ISO text and every field came back whatever was asked.
/// </summary>
[Collection("catalogue walk")]
public sealed class RelatedRecordsAnswerAsAQueryDoesTests : ArcGisClient
{
    [Fact]
    public async Task A_related_date_is_epoch_milliseconds_and_out_fields_are_honoured()
    {
        string root = await RequireServerAsync();
        string token = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");
        string tag = Guid.NewGuid().ToString("N")[..8];
        string service = $"zz_related_{tag}";
        string points = $"zz_rel_points_{tag}";
        string readings = $"zz_rel_readings_{tag}";
        long when = new DateTimeOffset(2024, 3, 2, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        async Task<string> Ok(HttpMethod method, string path, string? json, HttpStatusCode expected = HttpStatusCode.OK)
        {
            (HttpStatusCode status, string body) = await RequestAsync(method, $"{root}{path}", token, json);
            Assert.True(status == expected || (expected == HttpStatusCode.OK && status == HttpStatusCode.Created),
                $"{method} {path} answered {(int)status}: {body}");
            return body;
        }

        await Ok(HttpMethod.Post, "/admin/hosted/define", JsonSerializer.Serialize(new
        {
            name = points, geometryType = "Point", srid = 4326, serviceName = service,
            fields = new[] { new { name = "code", type = "Text", nullable = false } },
        }));
        await Ok(HttpMethod.Post, "/admin/hosted/define", JsonSerializer.Serialize(new
        {
            name = readings, geometryType = "Point", srid = 4326, serviceName = service,
            fields = new object[]
            {
                new { name = "code", type = "Text", nullable = false },
                new { name = "kind", type = "Text", nullable = true },
                new { name = "measured", type = "Date", nullable = true },
            },
        }));

        try
        {
            string face = $"/rest/services/hosted/{service}/FeatureServer";
            JsonElement layers = JsonDocument.Parse(await Ok(HttpMethod.Get, $"{face}?f=json", null)).RootElement.GetProperty("layers");
            int pointsId = layers.EnumerateArray().Single(l => l.GetProperty("name").GetString() == points).GetProperty("id").GetInt32();
            int readingsId = layers.EnumerateArray().Single(l => l.GetProperty("name").GetString() == readings).GetProperty("id").GetInt32();

            string point = JsonSerializer.Serialize(new[] { new { geometry = new { x = 29, y = 41 }, attributes = new { code = "A" } } });
            string reading = JsonSerializer.Serialize(new[]
            {
                new { geometry = new { x = 29, y = 41 }, attributes = new { code = "A", kind = "temperature", measured = when } },
            });

            using (FormUrlEncodedContent form = new([new("adds", point), new("f", "json")]))
            {
                await PostFormAsync(root, token, $"{face}/{pointsId}/applyEdits", form);
            }

            using (FormUrlEncodedContent form = new([new("adds", reading), new("f", "json")]))
            {
                await PostFormAsync(root, token, $"{face}/{readingsId}/applyEdits", form);
            }

            JsonElement declared = JsonDocument.Parse(await Ok(HttpMethod.Post, "/admin/relationships", JsonSerializer.Serialize(new
            {
                name = $"zz_rel_{tag}", originLayer = points, originKey = "code", relatedLayer = readings, relatedKey = "code",
                cardinality = "OneToMany",
            }), HttpStatusCode.Created)).RootElement;
            int relationship = declared.GetProperty("number").GetInt32();

            JsonElement oneId = JsonDocument.Parse(await Ok(HttpMethod.Get,
                $"{face}/{pointsId}/query?where=1%3D1&returnIdsOnly=true&f=json", null)).RootElement;
            long objectId = oneId.GetProperty("objectIds")[0].GetInt64();

            JsonElement all = JsonDocument.Parse(await Ok(HttpMethod.Get,
                $"{face}/{pointsId}/queryRelatedRecords?relationshipId={relationship}&objectIds={objectId}&outFields=*&f=json", null)).RootElement;
            JsonElement row = all.GetProperty("relatedRecordGroups")[0].GetProperty("relatedRecords")[0].GetProperty("attributes");

            Assert.Equal(JsonValueKind.Number, row.GetProperty("measured").ValueKind);
            Assert.Equal(when, row.GetProperty("measured").GetInt64());

            JsonElement some = JsonDocument.Parse(await Ok(HttpMethod.Get,
                $"{face}/{pointsId}/queryRelatedRecords?relationshipId={relationship}&objectIds={objectId}&outFields=kind&f=json", null)).RootElement;
            string[] names = [.. some.GetProperty("relatedRecordGroups")[0].GetProperty("relatedRecords")[0]
                .GetProperty("attributes").EnumerateObject().Select(p => p.Name)];

            Assert.Contains("kind", names);
            Assert.DoesNotContain("measured", names);
            Assert.DoesNotContain("code", names);
            Assert.Contains(names, n => n.Equals("objectid", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(names.Length, some.GetProperty("fields").GetArrayLength());

            (HttpStatusCode refused, string why) = await RequestAsync(HttpMethod.Get,
                $"{root}{face}/{pointsId}/queryRelatedRecords?relationshipId={relationship}&objectIds={objectId}&outFields=nothere&f=json", token, null);
            Assert.True(refused == HttpStatusCode.BadRequest, $"An unknown outFields name answered {(int)refused}: {why}");
            Assert.Contains("nothere", why, StringComparison.Ordinal);
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{service}?folder=hosted&drop=true", token, null);
        }
    }

    private async Task PostFormAsync(string root, string token, string path, FormUrlEncodedContent form)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}{path}") { Content = form };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode && !body.Contains("\"success\":false", StringComparison.Ordinal), $"{path} answered {(int)response.StatusCode}: {body}");
    }
}

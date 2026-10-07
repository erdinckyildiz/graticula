using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// How long a layer's history is kept, and what a restore says when today's domains disagree with the version —
/// ADR-078 conditions 3 and 5, owner decisions 2026-10-07.
/// </summary>
/// <remarks>
/// <b>Its own layer and its own domain, defined and dropped here</b>, as <see cref="DomainConformanceTests"/> does:
/// a history and a domain change what every write to a layer does, and the fixture's layers are everybody's.
/// </remarks>
[Collection("catalogue walk")]
public sealed class HistoryKeepingConformanceTests : ArcGisClient
{
    [Fact]
    public async Task A_kept_period_moves_the_first_moment_and_a_restore_names_values_today_s_domain_refuses()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string layer = "zz_adr078_" + suffix;
        string domainName = "ZzHistMaterial" + suffix;

        (HttpStatusCode defined, string design) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/hosted/define",
            token!,
            JsonSerializer.Serialize(new
            {
                name = layer,
                geometryType = "Point",
                fields = new object[] { new { name = "material", type = "text", nullable = true } },
                sharing = "public",
            }));

        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}: {design}");

        string feature = JsonDocument.Parse(design).RootElement.GetProperty("services").GetProperty("feature").GetString()!;
        string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);

        try
        {
            await SetMaterialsAsync(root, token!, layer, domainName, "CU", "PVC", "DI");

            JsonElement added = await PostAsync(root, token!, $"{feature}/addFeatures",
                ("features", "[{\"geometry\":{\"x\":1000,\"y\":2000,\"spatialReference\":{\"wkid\":3857}},\"attributes\":{\"material\":\"PVC\"}}]"));
            long id = added.GetProperty("addResults")[0].GetProperty("objectId").GetInt64();

            (HttpStatusCode on, string onSaid) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/layers/{layer}/history", token!, "{\"enabled\":true}");
            Assert.True(on == HttpStatusCode.OK, $"Turning history on answered {(int)on}: {onSaid}");

            await PostAsync(root, token!, $"{feature}/updateFeatures",
                ("features", $"[{{\"attributes\":{{\"objectid\":{id.ToString(CultureInfo.InvariantCulture)},\"material\":\"CU\"}}}}]"));

            // ---- condition 5: the list is narrowed, and the version from before it is put back anyway, said ----
            // The field save made the list a shared domain (ADR-087), so it is narrowed where it lives.
            (_, string domains) = await RequestAsync(HttpMethod.Get, $"{root}/admin/domains", token!, json: null);
            string domainId = JsonDocument.Parse(domains).RootElement.GetProperty("domains").EnumerateArray()
                .Single(d => d.GetProperty("name").GetString() == domainName).GetProperty("id").GetString()!;

            (HttpStatusCode narrowed, string narrowSaid) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/domains/{domainId}", token!,
                JsonSerializer.Serialize(new { domain = Materials(domainName, "CU", "DI") }));
            Assert.True(narrowed == HttpStatusCode.OK, $"Narrowing the list answered {(int)narrowed}: {narrowSaid}");

            (_, string versions) = await RequestAsync(
                HttpMethod.Get, $"{root}/admin/layers/{layer}/history/{id}", token!, json: null);
            long pvc = JsonDocument.Parse(versions).RootElement.GetProperty("versions").EnumerateArray()
                .First(v => v.GetProperty("attributes").GetProperty("material").GetString() == "PVC")
                .GetProperty("version").GetInt64();

            (HttpStatusCode restored, string restoreSaid) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/layers/{layer}/history/{id}/restore", token!,
                JsonSerializer.Serialize(new { version = pvc }));

            Assert.True(restored == HttpStatusCode.OK, $"The restore answered {(int)restored}: {restoreSaid}");

            JsonElement outside = JsonDocument.Parse(restoreSaid).RootElement.GetProperty("outsideDomains");
            string named = Assert.Single(outside.EnumerateArray()).GetString()!;
            Assert.Contains("'material' is", named, StringComparison.Ordinal);
            Assert.Contains(domainName, named, StringComparison.Ordinal);

            // ---- condition 3: a period, its first moment, and a moment before it refused, not answered empty ----
            (HttpStatusCode tooLong, _) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{layer}/history/keep", token!, "{\"days\":0}");
            Assert.Equal(HttpStatusCode.BadRequest, tooLong);

            (HttpStatusCode kept, string keptSaid) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{layer}/history/keep", token!, "{\"days\":30}");
            Assert.True(kept == HttpStatusCode.OK, $"Setting 30 days answered {(int)kept}: {keptSaid}");

            (_, string summary) = await RequestAsync(HttpMethod.Get, $"{root}/admin/layers/{layer}/history", token!, json: null);
            Assert.Equal(30, JsonDocument.Parse(summary).RootElement.GetProperty("keepDays").GetInt32());

            // A history younger than its period still begins where it began; the period moves the first moment only
            // once there is more history than it keeps (FeatureHistoryTests holds that half, with dates moved back).
            JsonElement document = await GetJsonAsync($"{feature}?f=json");
            long start = document.GetProperty("archivingInfo").GetProperty("startArchivingMoment").GetInt64();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.InRange(start, now - 600_000, now + 60_000);

            string before = (start - 86_400_000).ToString(CultureInfo.InvariantCulture);
            int refused = await StatusOfAsync($"{feature}/query?where=1%3D1&returnCountOnly=true&historicMoment={before}&f=json");
            Assert.Equal(400, refused);

            string inside = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            Assert.Equal(200, (int)await StatusOfAsync($"{feature}/query?where=1%3D1&returnCountOnly=true&historicMoment={inside}&f=json"));

            // Keeping all of it puts the first moment back where history began.
            await RequestAsync(HttpMethod.Put, $"{root}/admin/layers/{layer}/history/keep", token!, "{\"days\":null}");
            Assert.Equal(200, (int)await StatusOfAsync($"{feature}/query?where=1%3D1&returnCountOnly=true&historicMoment={before}&f=json"));
        }
        finally
        {
            await RequestAsync(
                HttpMethod.Delete, $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true", token!, json: null);

            // The field save made the list a shared domain, which outlives the layer (ADR-087).
            (HttpStatusCode listed, string body) = await RequestAsync(HttpMethod.Get, $"{root}/admin/domains", token!, json: null);

            if (listed == HttpStatusCode.OK)
            {
                foreach (JsonElement domain in JsonDocument.Parse(body).RootElement.GetProperty("domains").EnumerateArray()
                             .Where(d => d.GetProperty("name").GetString() == domainName))
                {
                    await RequestAsync(HttpMethod.Delete, $"{root}/admin/domains/{domain.GetProperty("id").GetString()}", token!, json: null);
                }
            }
        }
    }

    private async Task SetMaterialsAsync(string root, string token, string layer, string name, params string[] codes)
    {
        (HttpStatusCode set, string said) = await RequestAsync(
            HttpMethod.Put,
            $"{root}/admin/layers/{layer}/fields",
            token,
            JsonSerializer.Serialize(new
            {
                overrides = new object[]
                {
                    new
                    {
                        column = "material",
                        domain = Materials(name, codes),
                    },
                },
            }));

        Assert.True(set == HttpStatusCode.OK, $"Giving material the list {string.Join(",", codes)} answered {(int)set}: {said}");
    }

    private static object Materials(string name, params string[] codes) =>
        new { type = "codedValue", name, codedValues = codes.Select(c => new { code = c, name = c }).ToArray() };

    private async Task<JsonElement> PostAsync(string root, string token, string path, params (string Key, string Value)[] fields)
    {
        using FormUrlEncodedContent content = new(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
                .Append(new KeyValuePair<string, string>("f", "json")));

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{path}")) { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"{path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}

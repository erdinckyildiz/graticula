using System;
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
/// A layer's attachments are read in one <c>queryAttachments</c> and replaced in place with
/// <c>updateAttachment</c>.
/// </summary>
/// <remarks>
/// Written 2026-09-15: both were 404. Dashboards, Experience Builder and the Maps SDK read galleries
/// through the first and Survey123 replaces a photograph through the second.
/// </remarks>
[Collection("catalogue walk")]
public sealed class AttachmentsAreQueriedAndReplacedTests : ArcGisClient
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Fact]
    public async Task An_attachment_is_found_by_query_and_replaced_under_the_same_id()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string layer = "zz_attach_query_" + Guid.NewGuid().ToString("N")[..8];

        (HttpStatusCode defined, string design) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/hosted/define",
            token!,
            JsonSerializer.Serialize(new
            {
                name = layer,
                geometryType = "Point",
                fields = new[] { new { name = "label", type = "text", nullable = true } },
            }));

        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}: {design}");

        string feature = JsonDocument.Parse(design).RootElement
            .GetProperty("services").GetProperty("feature").GetString()!;
        string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);

        using HttpClient http = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        try
        {
            JsonElement added = await PostFormAsync(http, root, token!, $"{feature}/addFeatures",
                "[{\"geometry\":{\"x\":1000,\"y\":2000,\"spatialReference\":{\"wkid\":3857}},\"attributes\":{\"label\":\"a\"}}]");
            long objectId = added.GetProperty("addResults")[0].GetProperty("objectId").GetInt64();

            JsonElement attached = await MultipartAsync(http, root, token!, $"{feature}/{objectId}/addAttachment", attachmentId: null);
            int attachmentId = attached.GetProperty("addAttachmentResult").GetProperty("objectId").GetInt32();

            JsonElement queried = await GetJsonAsync($"{feature}/queryAttachments?objectIds={objectId}&returnUrl=true");
            JsonElement group = queried.GetProperty("attachmentGroups").EnumerateArray().Single();

            Assert.Equal(objectId, group.GetProperty("parentObjectId").GetInt64());
            JsonElement info = group.GetProperty("attachmentInfos").EnumerateArray().Single();
            Assert.Equal(attachmentId, info.GetProperty("id").GetInt32());
            Assert.EndsWith($"/{objectId}/attachments/{attachmentId}", info.GetProperty("url").GetString(), StringComparison.Ordinal);

            JsonElement replaced = await MultipartAsync(http, root, token!, $"{feature}/{objectId}/updateAttachment", attachmentId);
            Assert.True(replaced.GetProperty("updateAttachmentResult").GetProperty("success").GetBoolean(), replaced.ToString());

            JsonElement again = await GetJsonAsync($"{feature}/queryAttachments?objectIds={objectId}");
            JsonElement after = again.GetProperty("attachmentGroups")[0].GetProperty("attachmentInfos").EnumerateArray().Single();

            Assert.Equal(attachmentId, after.GetProperty("id").GetInt32());
            Assert.Equal("replaced.png", after.GetProperty("name").GetString());
            // V-78, the fourth ArcGIS review: definitionExpression is what the Maps SDK sends for an
            // AttachmentQuery.where, and it was refused. It is read as the query face reads where.
            JsonElement byWhere = await GetJsonAsync(
                $"{feature}/queryAttachments?definitionExpression={Uri.EscapeDataString("label = 'a'")}");
            Assert.Equal(objectId, byWhere.GetProperty("attachmentGroups").EnumerateArray().Single().GetProperty("parentObjectId").GetInt64());

            JsonElement none = await GetJsonAsync(
                $"{feature}/queryAttachments?definitionExpression={Uri.EscapeDataString("label = 'nobody'")}");
            Assert.Empty(none.GetProperty("attachmentGroups").EnumerateArray());

            JsonElement both = await GetJsonAsync(
                $"{feature}/queryAttachments?objectIds={objectId + 1000}&definitionExpression={Uri.EscapeDataString("label = 'a'")}");
            Assert.Empty(both.GetProperty("attachmentGroups").EnumerateArray());
        }
        finally
        {
            await RequestAsync(
                HttpMethod.Delete,
                $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true",
                token!,
                json: null);
        }
    }

    private static async Task<JsonElement> PostFormAsync(HttpClient http, string root, string token, string path, string features)
    {
        using FormUrlEncodedContent form = new([new("features", features), new("f", "json")]);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{path}")) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await http.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> MultipartAsync(HttpClient http, string root, string token, string path, int? attachmentId)
    {
        using MultipartFormDataContent form = new();

        if (attachmentId is { } id)
        {
            form.Add(new StringContent(id.ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8), "attachmentId");
        }

        ByteArrayContent file = new(Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "attachment", attachmentId is null ? "first.png" : "replaced.png");
        form.Add(new StringContent("json"), "f");

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{path}")) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"{path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}

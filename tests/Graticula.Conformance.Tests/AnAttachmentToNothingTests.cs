using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// An attachment added to a feature that does not exist is refused as that, not as an outage.
/// </summary>
/// <remarks>
/// <para>
/// Written 2026-09-15, against the showcase: <c>POST …/FeatureServer/0/999/addAttachment</c>
/// answered 503, <i>a database this server depends on is unreachable</i>, because the companion
/// table's foreign key refused the insert and nothing classified the refusal. The Logs screen
/// recorded the same request as 200.
/// </para>
/// <para>
/// <b>Its own layer, defined and dropped here</b>, because attachments are built only on a table
/// in the schema this server creates, and the fixture's editable layer need not be one.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class AnAttachmentToNothingTests : ArcGisClient
{
    [Fact]
    public async Task Attaching_to_a_feature_that_is_not_there_is_a_404_that_says_so()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string layer = "zz_attach_nothing_" + Guid.NewGuid().ToString("N")[..8];

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

        try
        {
            using HttpClient http = new(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            });

            using MultipartFormDataContent form = new();
            ByteArrayContent file = new([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "attachment", "nothing.png");
            form.Add(new StringContent("json"), "f");

            using HttpRequestMessage request = new(
                HttpMethod.Post, new Uri($"{root}{feature}/2147480000/addAttachment"))
            {
                Content = form,
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            Assert.True(
                response.StatusCode == HttpStatusCode.NotFound,
                $"Attaching to a feature that does not exist answered {(int)response.StatusCode}: {body}");
            Assert.Contains("2147480000", body, StringComparison.Ordinal);
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
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-222 conditions 1 and 2: the Spatial Analysis Service answers as the ArcGIS REST reference documents it, and
/// <c>CreateBuffers</c> and <c>DissolveBoundaries</c> make a hosted layer from a hosted layer through <c>submitJob</c>.
/// </summary>
/// <remarks>
/// <b>Its own output layers, made and removed here</b>; the input is the suite's queryable layer, which is only read.
/// In the catalogue walk, because publishing a result puts a service in the directory others enumerate.
/// </remarks>
[Collection("catalogue walk")]
public sealed class SpatialAnalysisServiceTests : ArcGisClient
{
    private const string Service = "/rest/services/System/SpatialAnalysisTools/GPServer";

    private static HttpClient Unverified() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private static async Task<(int Status, JsonElement Body)> SendAsync(
        HttpClient http, string token, HttpMethod method, string url, IDictionary<string, string>? form = null)
    {
        using HttpRequestMessage request = new(method, new Uri(url));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        using HttpResponseMessage response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, JsonDocument.Parse(body.Length == 0 ? "null" : body).RootElement.Clone());
    }

    [Fact]
    public async Task The_service_and_its_tasks_answer_as_documented_and_the_portal_names_it()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        using HttpClient http = Unverified();

        (int status, JsonElement service) = await SendAsync(http, token, HttpMethod.Get, $"{root}{Service}?f=json");
        Assert.Equal(200, status);
        Assert.Equal("esriExecutionTypeAsynchronous", service.GetProperty("executionType").GetString());
        Assert.Contains("CreateBuffers", service.GetProperty("tasks").EnumerateArray().Select(t => t.GetString()));
        Assert.Contains("DissolveBoundaries", service.GetProperty("tasks").EnumerateArray().Select(t => t.GetString()));

        (_, JsonElement task) = await SendAsync(http, token, HttpMethod.Get, $"{root}{Service}/CreateBuffers?f=json");
        JsonElement[] parameters = [.. task.GetProperty("parameters").EnumerateArray()];
        Assert.Contains(parameters, p => p.GetProperty("name").GetString() == "inputLayer"
            && p.GetProperty("parameterType").GetString() == "esriGPParameterTypeRequired"
            && p.GetProperty("direction").GetString() == "esriGPParameterDirectionInput");
        Assert.Contains(parameters, p => p.GetProperty("name").GetString() == "bufferLayer"
            && p.GetProperty("direction").GetString() == "esriGPParameterDirectionOutput");

        (_, JsonElement portal) = await SendAsync(http, token, HttpMethod.Get, $"{root}/sharing/rest/portals/self?f=json");
        Assert.EndsWith(Service, portal.GetProperty("helperServices").GetProperty("analysis").GetProperty("url").GetString(), StringComparison.Ordinal);
        Assert.Contains("premium:user:spatialanalysis",
            portal.GetProperty("user").GetProperty("privileges").EnumerateArray().Select(p => p.GetString()));

        // Every task is asynchronous, and a job id that is nobody's is no job.
        (int sync, JsonElement refused) = await SendAsync(http, token, HttpMethod.Get, $"{root}{Service}/CreateBuffers/execute?f=json");
        Assert.Equal(400, sync);
        Assert.Contains("asynchronous", refused.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        (int none, _) = await SendAsync(http, token, HttpMethod.Get, $"{root}{Service}/CreateBuffers/jobs/j{Guid.NewGuid():N}?f=json");
        Assert.Equal(400, none);
    }

    [Fact]
    public async Task CreateBuffers_and_DissolveBoundaries_make_hosted_layers_with_the_documented_fields()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string input = (Environment.GetEnvironmentVariable(QueryCapabilityConformanceTests.LayerVariable) ?? "hosted/ci_buildings").Trim('/');
        string inputUrl = $"{root}/rest/services/{input}/FeatureServer/0";
        string stamp = Guid.NewGuid().ToString("N")[..8];
        string buffers = $"zz_buf_{stamp}";
        string dissolved = $"zz_dis_{stamp}";
        using HttpClient http = Unverified();

        try
        {
            (_, JsonElement counted) = await SendAsync(http, token, HttpMethod.Get, $"{inputUrl}/query?where=1%3D1&returnCountOnly=true&f=json");
            int features = counted.GetProperty("count").GetInt32();
            Assert.True(features > 0, "The queryable layer is empty, so there is nothing to buffer.");

            // Refused before it is queued: a result needs a name.
            (int unnamed, JsonElement why) = await SendAsync(http, token, HttpMethod.Post, $"{root}{Service}/CreateBuffers/submitJob",
                new Dictionary<string, string> { ["inputLayer"] = Json(new { url = inputUrl }), ["distances"] = "[100]", ["f"] = "json" });
            Assert.Equal(400, unnamed);
            Assert.Contains("outputName", why.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

            string bufferUrl = await RunAsync(http, root, token, "CreateBuffers", "bufferLayer", new Dictionary<string, string>
            {
                ["inputLayer"] = Json(new { url = inputUrl }),
                ["distances"] = "[100, 250]",
                ["units"] = "Meters",
                ["outputName"] = Json(new { serviceProperties = new { name = buffers } }),
            });

            // One buffer per feature per distance, with the fields the task documents.
            (_, JsonElement made) = await SendAsync(http, token, HttpMethod.Get, $"{bufferUrl}/query?where=1%3D1&returnCountOnly=true&f=json");
            Assert.Equal(features * 2, made.GetProperty("count").GetInt32());
            (_, JsonElement layer) = await SendAsync(http, token, HttpMethod.Get, $"{bufferUrl}?f=json");
            string[] fields = [.. layer.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!.ToLowerInvariant())];
            Assert.Contains("buff_dist", fields);
            Assert.Contains("orig_fid", fields);
            Assert.Contains("analysisarea", fields);
            Assert.Equal("esriGeometryPolygon", layer.GetProperty("geometryType").GetString());

            // Dissolved by distance, multipart: one feature per distance, each counting its buffers.
            string dissolvedUrl = await RunAsync(http, root, token, "DissolveBoundaries", "dissolvedLayer", new Dictionary<string, string>
            {
                ["inputLayer"] = Json(new { url = bufferUrl }),
                ["dissolveFields"] = "[\"buff_dist\"]",
                ["summaryFields"] = "[\"analysisarea Sum\"]",
                ["multiPartFeatures"] = "true",
                ["outputName"] = Json(new { serviceProperties = new { name = dissolved } }),
            });

            (_, JsonElement groups) = await SendAsync(http, token, HttpMethod.Get,
                $"{dissolvedUrl}/query?where=1%3D1&outFields=*&returnGeometry=false&orderByFields=buff_dist&f=json");
            JsonElement[] rows = [.. groups.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("attributes"))];
            Assert.Equal(2, rows.Length);
            Assert.All(rows, r => Assert.Equal(features, r.GetProperty("count").GetInt32()));
            Assert.All(rows, r => Assert.True(r.GetProperty("sum_analysisarea").GetDouble() > 0));
        }
        finally
        {
            // The service with its table, into the recycle bin and out of it, so nothing of this test is left.
            foreach (string name in new[] { dissolved, buffers })
            {
                (int status, string body) = await AdminAsync(
                    HttpMethod.Delete, $"/admin/featureservices/{Uri.EscapeDataString(name)}?folder=hosted&drop=true");

                if (status == 200 && JsonDocument.Parse(body).RootElement.TryGetProperty("recycled", out JsonElement entry)
                    && entry.ValueKind == JsonValueKind.String)
                {
                    await AdminAsync(HttpMethod.Delete, $"/admin/recycle-bin/{entry.GetString()}");
                }
            }
        }
    }

    private static async Task<string> RunAsync(
        HttpClient http, string root, string token, string task, string result, Dictionary<string, string> form)
    {
        form["f"] = "json";
        (int submitted, JsonElement job) = await SendAsync(http, token, HttpMethod.Post, $"{root}{Service}/{task}/submitJob", form);
        Assert.True(submitted == 200 && job.TryGetProperty("jobId", out _), $"{task} was not submitted: {submitted} {job}");
        Assert.Equal("esriJobSubmitted", job.GetProperty("jobStatus").GetString());
        string id = job.GetProperty("jobId").GetString()!;
        Assert.Matches("^j[0-9a-f]{32}$", id);

        JsonElement status = default;

        for (int i = 0; i < 120; i++)
        {
            (_, status) = await SendAsync(http, token, HttpMethod.Get, $"{root}{Service}/{task}/jobs/{id}?f=json");

            if (status.GetProperty("jobStatus").GetString() is "esriJobSucceeded" or "esriJobFailed" or "esriJobCancelled")
            {
                break;
            }

            await Task.Delay(500);
        }

        Assert.True(status.GetProperty("jobStatus").GetString() == "esriJobSucceeded", $"{task} did not succeed: {status}");
        Assert.Equal($"results/{result}", status.GetProperty("results").GetProperty(result).GetProperty("paramUrl").GetString());

        (_, JsonElement value) = await SendAsync(http, token, HttpMethod.Get, $"{root}{Service}/{task}/jobs/{id}/results/{result}?f=json");
        Assert.Equal("GPString", value.GetProperty("dataType").GetString());
        Assert.Matches("^[0-9a-f]{32}$", value.GetProperty("value").GetProperty("itemId").GetString()!);

        // A job that has ended is not cancelled.
        (int cancel, _) = await SendAsync(http, token, HttpMethod.Post, $"{root}{Service}/{task}/jobs/{id}/cancel", new Dictionary<string, string> { ["f"] = "json" });
        Assert.Equal(400, cancel);

        return value.GetProperty("value").GetProperty("url").GetString()!;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}

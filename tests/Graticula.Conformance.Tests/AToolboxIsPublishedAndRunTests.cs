using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-223 conditions 2 and 3: a Python toolbox written against grapy publishes as an item and a GPServer whose tasks and
/// parameters are read from its tools, and a run reads a hosted layer and writes one.
/// </summary>
/// <remarks>
/// <para>
/// <b>On CI the run is fenced</b> — Linux, Landlock and seccomp — and the job's first message says which fence went up;
/// grapy's own tests are where a hostile tool is shown stopped. A Windows fixture runs it with
/// <c>Graticula:Tools:UnsafeUnfenced</c>, and the test asserts only what both have in common.
/// </para>
/// <para>Written 2026-10-29.</para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class AToolboxIsPublishedAndRunTests : ArcGisClient
{
    private const string Toolbox = """
        import grapy as arcpy

        class Toolbox:
            def __init__(self):
                self.label = "Probe tools"
                self.tools = [Count, Halo]

        class Count:
            def __init__(self):
                self.label = "Count features"
                self.description = "How many features a layer holds."

            def getParameterInfo(self):
                return [arcpy.Parameter(name="in_features", displayName="Features", datatype="GPFeatureLayer"),
                        arcpy.Parameter(name="count", displayName="Count", datatype="GPLong", direction="Output", parameterType="Derived")]

            def execute(self, parameters, messages):
                n = int(arcpy.management.GetCount(parameters[0].valueAsText).getOutput(0))
                messages.addMessage(f"{n} features")
                parameters[1].value = n

        class Halo:
            def __init__(self):
                self.label = "Halo"

            def getParameterInfo(self):
                return [arcpy.Parameter(name="in_features", datatype="GPFeatureLayer"),
                        arcpy.Parameter(name="metres", datatype="GPDouble"),
                        arcpy.Parameter(name="halo", datatype="GPFeatureRecordSetLayer", direction="Output", parameterType="Derived")]

            def execute(self, parameters, messages):
                projected = arcpy.management.Project(parameters[0].valueAsText, "p.gpkg", 3857).getOutput(0)
                parameters[2].value = arcpy.analysis.Buffer(projected, "halo.gpkg", float(parameters[1].value)).getOutput(0)
        """;

    private static HttpClient Unverified() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    [Fact]
    public async Task A_toolbox_publishes_as_a_GPServer_and_its_tools_read_and_write_hosted_layers()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string points = $"{root}/rest/services/{(Environment.GetEnvironmentVariable("GRATICULA_TEST_TEMPORAL") ?? "hosted/ci_observations").Trim('/')}/FeatureServer/0";
        string name = "zz_tools_" + Guid.NewGuid().ToString("N")[..8];
        using HttpClient http = Unverified();
        List<string> made = [];

        int pointCount = (await GetAsync(http, token, $"{points}/query?where=1%3D1&returnCountOnly=true&f=json")).GetProperty("count").GetInt32();

        using MultipartFormDataContent upload = new()
        {
            { new StringContent(name), "name" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(Toolbox)) { Headers = { ContentType = new MediaTypeHeaderValue("text/x-python") } }, "file", "probe.pyt" },
        };
        using HttpRequestMessage publish = new(HttpMethod.Post, new Uri($"{root}/content/toolboxes")) { Content = upload };
        publish.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage published = await http.SendAsync(publish);
        string said = await published.Content.ReadAsStringAsync();
        Assert.True((int)published.StatusCode == 201, $"Publishing answered {(int)published.StatusCode}: {said}");

        string gp = $"{root}/rest/services/Tools/{name}/GPServer";

        try
        {
            JsonElement service = await GetAsync(http, token, $"{gp}?f=json");
            Assert.Equal(["Count", "Halo"], service.GetProperty("tasks").EnumerateArray().Select(t => t.GetString()));

            JsonElement task = await GetAsync(http, token, $"{gp}/Halo?f=json");
            JsonElement[] parameters = [.. task.GetProperty("parameters").EnumerateArray()];
            Assert.Equal("GPFeatureRecordSetLayer", parameters[0].GetProperty("dataType").GetString());
            Assert.Equal("GPDouble", parameters[1].GetProperty("dataType").GetString());
            Assert.Equal("esriGPParameterDirectionOutput", parameters[2].GetProperty("direction").GetString());

            // ---- a number back
            JsonElement count = await RunAsync(http, token, gp, "Count", "count", new() { ["in_features"] = Json(new { url = points }) });
            Assert.Equal(pointCount, count.GetProperty("value").GetInt32());

            // ---- a layer back, published as the caller's
            JsonElement halo = await RunAsync(http, token, gp, "Halo", "halo", new()
            {
                ["in_features"] = Json(new { url = points }),
                ["metres"] = "50",
            });
            string url = halo.GetProperty("value").GetProperty("url").GetString()!;
            made.Add(url.Split('/')[^3]);
            JsonElement drawn = await GetAsync(http, token, $"{url}?f=json");
            Assert.Equal("esriGeometryPolygon", drawn.GetProperty("geometryType").GetString());
            Assert.Equal(pointCount, (await GetAsync(http, token, $"{url}/query?where=1%3D1&returnCountOnly=true&f=json")).GetProperty("count").GetInt32());

            // ---- a required parameter not sent is refused before a run is queued
            using HttpRequestMessage missing = new(HttpMethod.Post, new Uri($"{gp}/Halo/submitJob"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json", ["in_features"] = Json(new { url = points }) }),
            };
            missing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage refused = await http.SendAsync(missing);
            Assert.Equal(400, (int)refused.StatusCode);
            Assert.Contains("metres is required", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // ---- a private toolbox is not there for anybody signed out
            using HttpResponseMessage anonymous = await http.GetAsync(new Uri($"{gp}?f=json"));
            Assert.Equal(404, (int)anonymous.StatusCode);
        }
        finally
        {
            foreach (string layer in made)
            {
                (int status, string body) = await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{layer}?folder=hosted&drop=true");

                if (status == 200 && JsonDocument.Parse(body).RootElement.TryGetProperty("recycled", out JsonElement entry) && entry.ValueKind == JsonValueKind.String)
                {
                    await AdminAsync(HttpMethod.Delete, $"/admin/recycle-bin/{entry.GetString()}");
                }
            }

            await AdminAsync(HttpMethod.Delete, $"/content/toolboxes/{name}");
        }
    }

    [Fact]
    public async Task A_toolbox_with_no_tools_or_that_will_not_load_is_refused_with_what_its_run_said()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        using HttpClient http = Unverified();

        foreach ((string source, string expected) in new[] { ("x = 1\n", "no class Toolbox"), ("import nothing_of_the_kind\n", "nothing_of_the_kind") })
        {
            using MultipartFormDataContent upload = new()
            {
                { new ByteArrayContent(Encoding.UTF8.GetBytes(source)), "file", "broken.pyt" },
            };
            using HttpRequestMessage publish = new(HttpMethod.Post, new Uri($"{root}/content/toolboxes")) { Content = upload };
            publish.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage refused = await http.SendAsync(publish);
            string said = await refused.Content.ReadAsStringAsync();

            Assert.Equal(400, (int)refused.StatusCode);
            Assert.Contains(expected, said, StringComparison.Ordinal);
        }
    }

    private static async Task<JsonElement> RunAsync(
        HttpClient http, string token, string gp, string tool, string result, Dictionary<string, string> form)
    {
        form["f"] = "json";
        using HttpRequestMessage submit = new(HttpMethod.Post, new Uri($"{gp}/{tool}/submitJob")) { Content = new FormUrlEncodedContent(form) };
        submit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage submitted = await http.SendAsync(submit);
        JsonElement job = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync()).RootElement.Clone();
        Assert.True(job.TryGetProperty("jobId", out JsonElement id), $"{tool} was not submitted: {job}");

        JsonElement status = default;

        for (int i = 0; i < 240; i++)
        {
            status = await GetAsync(http, token, $"{gp}/{tool}/jobs/{id.GetString()}?f=json");

            if (status.GetProperty("jobStatus").GetString() is "esriJobSucceeded" or "esriJobFailed" or "esriJobCancelled")
            {
                break;
            }

            await Task.Delay(500);
        }

        Assert.True(status.GetProperty("jobStatus").GetString() == "esriJobSucceeded", $"{tool} did not succeed: {status}");
        Assert.StartsWith("Fence: ", status.GetProperty("messages")[1].GetProperty("description").GetString(), StringComparison.Ordinal);
        return await GetAsync(http, token, $"{gp}/{tool}/jobs/{id.GetString()}/results/{result}?f=json");
    }

    private static async Task<JsonElement> GetAsync(HttpClient http, string token, string url)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(url));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await http.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}

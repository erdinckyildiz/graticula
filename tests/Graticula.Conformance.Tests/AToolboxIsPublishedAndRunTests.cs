using System;
using System.Collections.Generic;
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

    /// <summary>
    /// ADR-223 condition 4: publishing a toolbox takes <c>content:publishTools</c>, which no built-in role but the
    /// administrator holds — so a publisher, who may publish a layer, is refused, and the refusal names the privilege.
    /// </summary>
    [Fact]
    public async Task A_publisher_without_the_privilege_is_refused_publishing_a_toolbox_by_its_name()
    {
        string root = await RequireServerAsync();
        const string member = "zz_tools_publisher";
        const string password = "a-long-enough-test-password-223";

        await AdminAsync(HttpMethod.Delete, $"/admin/members/{member}?deleteOwned=true");
        (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/admin/members",
            JsonSerializer.Serialize(new { name = member, role = "publisher", userType = "creator" }));
        Assert.True(made is 200 or 201, $"making '{member}': {made} {madeBody}");

        try
        {
            string issued = JsonDocument.Parse(madeBody).RootElement.GetProperty("password").GetString()!;
            string once = await SignInAsync(root, member, issued);
            (HttpStatusCode changed, string why) = await RequestAsync(HttpMethod.Post, $"{root}/rest/auth/password", once,
                JsonSerializer.Serialize(new { currentPassword = issued, newPassword = password }));
            Assert.True(changed is HttpStatusCode.OK or HttpStatusCode.NoContent, $"changing the password: {changed} {why}");
            string token = await SignInAsync(root, member, password);

            (HttpStatusCode listed, string answer) = await RequestAsync(HttpMethod.Get, $"{root}/content/toolboxes", token, null);
            Assert.Equal(HttpStatusCode.OK, listed);
            Assert.False(JsonDocument.Parse(answer).RootElement.GetProperty("mayPublish").GetBoolean(), answer);

            using HttpClient http = Unverified();
            using MultipartFormDataContent upload = new()
            {
                { new StringContent("zz_tools_refused"), "name" },
                { new ByteArrayContent(Encoding.UTF8.GetBytes(Toolbox)), "file", "probe.pyt" },
            };
            using HttpRequestMessage publish = new(HttpMethod.Post, new Uri($"{root}/content/toolboxes")) { Content = upload };
            publish.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage refused = await http.SendAsync(publish);
            string said = await refused.Content.ReadAsStringAsync();

            Assert.True((int)refused.StatusCode == 403, $"Publishing as a publisher answered {(int)refused.StatusCode}: {said}");
            Assert.Contains("content:publishTools", said, StringComparison.Ordinal);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, "/content/toolboxes/zz_tools_refused");
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{member}?deleteOwned=true");
        }
    }

    private const string RasterToolbox = """
        import grapy as arcpy

        class Toolbox:
            def __init__(self):
                self.label = "Raster probe"
                self.tools = [Cells, PlusOne]

        class Cells:
            def __init__(self):
                self.label = "Cells"

            def getParameterInfo(self):
                return [arcpy.Parameter(name="in_raster", datatype="GPRasterLayer"),
                        arcpy.Parameter(name="cells", datatype="GPString", direction="Output", parameterType="Derived")]

            def execute(self, parameters, messages):
                raster = arcpy.Raster(parameters[0].valueAsText)
                a = arcpy.RasterToNumPyArray(raster, nodata_to_value=-1)
                parameters[1].value = f"{raster.width} {raster.height} {a[29, 37]} {a[130, 130]} {a[191, 255]} {a[0, 0]}"

        class PlusOne:
            def __init__(self):
                self.label = "Plus one"

            def getParameterInfo(self):
                return [arcpy.Parameter(name="in_raster", datatype="GPRasterLayer"),
                        arcpy.Parameter(name="out_raster", datatype="DERasterDataset", direction="Output")]

            def execute(self, parameters, messages):
                parameters[1].value = arcpy.Raster(parameters[0].valueAsText) + 1
        """;

    /// <summary>
    /// ADR-224 condition 2: an image service reaches a tool as a raster file and <c>arcpy.Raster</c> reads it. The corpus
    /// image's samples are recorded in <c>truth.json</c> beside it — 96 at (37, 29), 141 at (130, 130), 137 at
    /// (255, 191), and NoData (0) at the corner, which the tool writes as -1.
    /// </summary>
    [Fact]
    public async Task An_image_service_reaches_a_tool_as_a_raster_it_reads_cell_for_cell()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string image = $"zz_toolimg_{Guid.NewGuid():N}"[..20];
        string toolbox = "zz_rtools_" + Guid.NewGuid().ToString("N")[..8];
        using HttpClient http = Unverified();
        string corpus = System.IO.Path.Combine(RepositoryRoot(), "tests", "Graticula.Raster.Tiff.Tests", "corpus", "gray-byte-nodata.tif");

        using (MultipartFormDataContent form = new())
        {
            form.Add(new ByteArrayContent(await System.IO.File.ReadAllBytesAsync(corpus)), "file", "gray.tif");
            form.Add(new StringContent(image), "name");
            using HttpRequestMessage upload = new(HttpMethod.Post, new Uri($"{root}/admin/coverages/upload")) { Content = form };
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage made = await http.SendAsync(upload);
            Assert.True(made.IsSuccessStatusCode, $"uploading: {(int)made.StatusCode} {await made.Content.ReadAsStringAsync()}");
        }

        try
        {
            using MultipartFormDataContent publishForm = new()
            {
                { new StringContent(toolbox), "name" },
                { new ByteArrayContent(Encoding.UTF8.GetBytes(RasterToolbox)), "file", "raster.pyt" },
            };
            using HttpRequestMessage publish = new(HttpMethod.Post, new Uri($"{root}/content/toolboxes")) { Content = publishForm };
            publish.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage published = await http.SendAsync(publish);
            Assert.True((int)published.StatusCode == 201, $"Publishing answered {(int)published.StatusCode}: {await published.Content.ReadAsStringAsync()}");

            string gp = $"{root}/rest/services/Tools/{toolbox}/GPServer";
            JsonElement task = await GetAsync(http, token, $"{gp}/Cells?f=json");
            Assert.Equal("GPRasterDataLayer", task.GetProperty("parameters")[0].GetProperty("dataType").GetString());

            JsonElement cells = await RunAsync(http, token, gp, "Cells", "cells", new()
            {
                ["in_raster"] = Json(new { url = $"{root}/rest/services/hosted/{image}/ImageServer" }),
            });
            Assert.Equal("256 192 96 141 137 -1", cells.GetProperty("value").GetString());

            // A raster parameter takes an image service, not a feature layer.
            using HttpRequestMessage wrong = new(HttpMethod.Post, new Uri($"{gp}/Cells/submitJob"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["f"] = "json",
                    ["in_raster"] = Json(new { url = $"{root}/rest/services/hosted/ci_observations/FeatureServer/0" }),
                }),
            };
            wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage refused = await http.SendAsync(wrong);
            Assert.Equal(400, (int)refused.StatusCode);
            Assert.Contains("/ImageServer", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/toolboxes/{toolbox}");
            (int status, string body) = await AdminAsync(HttpMethod.Delete, $"/admin/coverages/{image}?folder=hosted");

            if (status == 200 && JsonDocument.Parse(body).RootElement.TryGetProperty("recycled", out JsonElement entry) && entry.ValueKind == JsonValueKind.String)
            {
                await AdminAsync(HttpMethod.Delete, $"/admin/recycle-bin/{entry.GetString()}");
            }
        }
    }

    /// <summary>
    /// D-324: a raster a tool answers is published as an image service of the caller's, through the upload door's
    /// checks, and named as the caller asked. Read back through the same tool path, every cell is one more than the
    /// corpus image's and NoData stays NoData.
    /// </summary>
    [Fact]
    public async Task A_raster_a_tool_answers_is_published_as_an_image_service_of_the_callers()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string image = $"zz_toolimg_{Guid.NewGuid():N}"[..20];
        string made = $"zz_toolout_{Guid.NewGuid():N}"[..20];
        string toolbox = "zz_rtools_" + Guid.NewGuid().ToString("N")[..8];
        using HttpClient http = Unverified();
        string corpus = System.IO.Path.Combine(RepositoryRoot(), "tests", "Graticula.Raster.Tiff.Tests", "corpus", "gray-byte-nodata.tif");

        using (MultipartFormDataContent form = new())
        {
            form.Add(new ByteArrayContent(await System.IO.File.ReadAllBytesAsync(corpus)), "file", "gray.tif");
            form.Add(new StringContent(image), "name");
            using HttpRequestMessage upload = new(HttpMethod.Post, new Uri($"{root}/admin/coverages/upload")) { Content = form };
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage uploaded = await http.SendAsync(upload);
            Assert.True(uploaded.IsSuccessStatusCode, $"uploading: {(int)uploaded.StatusCode} {await uploaded.Content.ReadAsStringAsync()}");
        }

        try
        {
            using MultipartFormDataContent publishForm = new()
            {
                { new StringContent(toolbox), "name" },
                { new ByteArrayContent(Encoding.UTF8.GetBytes(RasterToolbox)), "file", "raster.pyt" },
            };
            using HttpRequestMessage publish = new(HttpMethod.Post, new Uri($"{root}/content/toolboxes")) { Content = publishForm };
            publish.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage published = await http.SendAsync(publish);
            Assert.True((int)published.StatusCode == 201, $"Publishing answered {(int)published.StatusCode}: {await published.Content.ReadAsStringAsync()}");

            string gp = $"{root}/rest/services/Tools/{toolbox}/GPServer";
            JsonElement output = await RunAsync(http, token, gp, "PlusOne", "out_raster", new()
            {
                ["in_raster"] = Json(new { url = $"{root}/rest/services/hosted/{image}/ImageServer" }),
                ["out_raster"] = made,
            });
            Assert.Equal("GPRasterDataLayer", output.GetProperty("dataType").GetString());
            string url = output.GetProperty("value").GetProperty("url").GetString()!;
            Assert.Equal($"{root}/rest/services/hosted/{made}/ImageServer", url);

            JsonElement service = await GetAsync(http, token, $"{url}?f=json");
            Assert.Equal(1, service.GetProperty("bandCount").GetInt32());

            JsonElement cells = await RunAsync(http, token, gp, "Cells", "cells", new()
            {
                ["in_raster"] = Json(new { url }),
            });
            Assert.Equal("256 192 97 142 138 -1", cells.GetProperty("value").GetString());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/toolboxes/{toolbox}");

            foreach (string coverage in new[] { image, made })
            {
                (int status, string body) = await AdminAsync(HttpMethod.Delete, $"/admin/coverages/{coverage}?folder=hosted");

                if (status == 200 && JsonDocument.Parse(body).RootElement.TryGetProperty("recycled", out JsonElement entry) && entry.ValueKind == JsonValueKind.String)
                {
                    await AdminAsync(HttpMethod.Delete, $"/admin/recycle-bin/{entry.GetString()}");
                }
            }
        }
    }

    private const string PairToolbox = """
        import grapy as arcpy

        class Toolbox:
            def __init__(self):
                self.label = "Pair probe"
                self.tools = [Pair]

        class Pair:
            def __init__(self):
                self.label = "Pair"

            def getParameterInfo(self):
                return [arcpy.Parameter(name="in_features", datatype="GPFeatureLayer"),
                        arcpy.Parameter(name="halo", datatype="GPFeatureRecordSetLayer", direction="Output", parameterType="Derived"),
                        arcpy.Parameter(name="out_raster", datatype="DERasterDataset", direction="Output")]

            def execute(self, parameters, messages):
                projected = arcpy.management.Project(parameters[0].valueAsText, "p.gpkg", 3857).getOutput(0)
                parameters[1].value = arcpy.analysis.Buffer(projected, "halo.gpkg", 10.0).getOutput(0)
                arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
                parameters[2].value = arcpy.NumPyArrayToRaster(__import__("numpy").ones((4, 4)))
        """;

    /// <summary>
    /// D-325 (d), owner decision 2026-10-31: a run whose second output cannot be published leaves its first withdrawn
    /// rather than published and named nowhere. The two outputs are given one name, so the image service cannot take
    /// the name the hosted layer took a moment before.
    /// </summary>
    [Fact]
    public async Task A_run_whose_second_output_fails_leaves_its_first_withdrawn()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string points = $"{root}/rest/services/{(Environment.GetEnvironmentVariable("GRATICULA_TEST_TEMPORAL") ?? "hosted/ci_observations").Trim('/')}/FeatureServer/0";
        string toolbox = "zz_pair_" + Guid.NewGuid().ToString("N")[..8];
        string both = $"zz_pairout_{Guid.NewGuid():N}"[..20];
        using HttpClient http = Unverified();

        using MultipartFormDataContent publishForm = new()
        {
            { new StringContent(toolbox), "name" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(PairToolbox)), "file", "pair.pyt" },
        };
        using HttpRequestMessage publish = new(HttpMethod.Post, new Uri($"{root}/content/toolboxes")) { Content = publishForm };
        publish.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage published = await http.SendAsync(publish);
        Assert.True((int)published.StatusCode == 201, $"Publishing answered {(int)published.StatusCode}: {await published.Content.ReadAsStringAsync()}");

        try
        {
            string gp = $"{root}/rest/services/Tools/{toolbox}/GPServer";
            JsonElement status = await FinishedAsync(http, token, gp, "Pair", new()
            {
                ["in_features"] = Json(new { url = points }),
                ["halo"] = both,
                ["out_raster"] = both,
            });

            Assert.True(status.GetProperty("jobStatus").GetString() == "esriJobFailed", $"Pair should have failed: {status}");

            using HttpRequestMessage look = new(HttpMethod.Get, new Uri($"{root}/rest/services/hosted/{both}/FeatureServer?f=json"));
            look.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage gone = await http.SendAsync(look);
            string said = await gone.Content.ReadAsStringAsync();
            Assert.True((int)gone.StatusCode == 404 || said.Contains("\"error\"", StringComparison.Ordinal), $"The first output is still published: {said}");
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/toolboxes/{toolbox}");
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{both}?folder=hosted&drop=true");
            await AdminAsync(HttpMethod.Delete, $"/admin/coverages/{both}?folder=hosted");
        }
    }

    /// <summary>
    /// D-325 (b), owner decision 2026-10-31: a feature output is a hosted layer, and a run that would publish one asks
    /// <c>content:publishFeatures</c> as the upload door does — so a member of the user role is refused the tool that
    /// answers a layer, and not the one that answers a number.
    /// </summary>
    [Fact]
    public async Task A_member_without_the_publish_privilege_is_refused_a_tool_that_answers_a_layer()
    {
        string root = await RequireServerAsync();
        string admin = (await TokenAsync(root))!;
        string points = $"{root}/rest/services/{(Environment.GetEnvironmentVariable("GRATICULA_TEST_TEMPORAL") ?? "hosted/ci_observations").Trim('/')}/FeatureServer/0";
        string toolbox = "zz_tools_" + Guid.NewGuid().ToString("N")[..8];
        const string member = "zz_tools_user";
        const string password = "a-long-enough-test-password-325";
        using HttpClient http = Unverified();

        using MultipartFormDataContent publishForm = new()
        {
            { new StringContent(toolbox), "name" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(Toolbox)), "file", "probe.pyt" },
        };
        using HttpRequestMessage publish = new(HttpMethod.Post, new Uri($"{root}/content/toolboxes")) { Content = publishForm };
        publish.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using HttpResponseMessage published = await http.SendAsync(publish);
        Assert.True((int)published.StatusCode == 201, $"Publishing answered {(int)published.StatusCode}: {await published.Content.ReadAsStringAsync()}");

        await AdminAsync(HttpMethod.Delete, $"/admin/members/{member}?deleteOwned=true");

        try
        {
            (HttpStatusCode shared, string sharedSaid) = await RequestAsync(
                HttpMethod.Put, $"{root}/content/toolboxes/{toolbox}/sharing", admin, Json(new { sharing = "organization" }));
            Assert.True(shared is HttpStatusCode.OK or HttpStatusCode.NoContent, $"sharing: {shared} {sharedSaid}");

            (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/admin/members",
                JsonSerializer.Serialize(new { name = member, role = "user", userType = "creator" }));
            Assert.True(made is 200 or 201, $"making '{member}': {made} {madeBody}");
            string issued = JsonDocument.Parse(madeBody).RootElement.GetProperty("password").GetString()!;
            string once = await SignInAsync(root, member, issued);
            (HttpStatusCode changed, string why) = await RequestAsync(HttpMethod.Post, $"{root}/rest/auth/password", once,
                JsonSerializer.Serialize(new { currentPassword = issued, newPassword = password }));
            Assert.True(changed is HttpStatusCode.OK or HttpStatusCode.NoContent, $"changing the password: {changed} {why}");
            string token = await SignInAsync(root, member, password);

            string gp = $"{root}/rest/services/Tools/{toolbox}/GPServer";
            using HttpRequestMessage halo = new(HttpMethod.Post, new Uri($"{gp}/Halo/submitJob"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["f"] = "json", ["in_features"] = Json(new { url = points }), ["metres"] = "5",
                }),
            };
            halo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage refused = await http.SendAsync(halo);
            string said = await refused.Content.ReadAsStringAsync();
            Assert.True((int)refused.StatusCode == 403, $"Halo as a user answered {(int)refused.StatusCode}: {said}");
            Assert.Contains("a hosted layer", said, StringComparison.Ordinal);
            Assert.Contains("content:publishFeatures", said, StringComparison.Ordinal);

            JsonElement counted = await FinishedAsync(http, token, gp, "Count", new() { ["in_features"] = Json(new { url = points }) });
            Assert.Equal("esriJobSucceeded", counted.GetProperty("jobStatus").GetString());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/toolboxes/{toolbox}");
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{member}?deleteOwned=true");
        }
    }

    private static async Task<JsonElement> FinishedAsync(HttpClient http, string token, string gp, string tool, Dictionary<string, string> form)
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

        return status;
    }

    private static string RepositoryRoot()
    {
        System.IO.DirectoryInfo? at = new(AppContext.BaseDirectory);

        while (at is not null && !System.IO.File.Exists(System.IO.Path.Combine(at.FullName, "CLAUDE.md")))
        {
            at = at.Parent;
        }

        Assert.NotNull(at);
        return at!.FullName;
    }

    private async Task<string> SignInAsync(string root, string name, string password)
    {
        using StringContent body = new(JsonSerializer.Serialize(new { name, password }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.PostAsync(new Uri($"{root}/rest/auth/login"), body);
        string said = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"signing in as '{name}': {(int)response.StatusCode} {said}");
        return JsonDocument.Parse(said).RootElement.GetProperty("token").GetString()!;
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

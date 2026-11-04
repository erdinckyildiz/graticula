using System;
using System.Buffers.Binary;
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
/// ADR-265: the print service answers as ArcGIS's <c>Utilities/PrintingTools</c> does, draws a web map's layers of this
/// server as the caller may read them, leaves out a layer at any other address, and is named by <c>portals/self</c>.
/// </summary>
/// <remarks>
/// <b>Not drawn is proved by bytes, not by a sentence.</b> A print whose only layer was left out must be the same file as a
/// print of no layers at all, at the same size — the encoder is deterministic — so the test cannot pass on a server that
/// says it left a layer out and drew it anyway. In the catalogue walk, because one test publishes a private service.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ThePrintServiceDrawsAWebMapTests : ArcGisClient
{
    private const string Service = "/rest/services/Utilities/PrintingTools/GPServer";
    private const string Export = Service + "/Export%20Web%20Map%20Task";

    private static HttpClient Unverified() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private static async Task<(int Status, JsonElement Body)> SendAsync(
        HttpClient http, string? token, string url, IDictionary<string, string>? form = null)
    {
        using HttpRequestMessage request = new(form is null ? HttpMethod.Get : HttpMethod.Post, new Uri(url));

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        using HttpResponseMessage response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, JsonDocument.Parse(body.Length == 0 ? "null" : body).RootElement.Clone());
    }

    /// <summary>Prints and fetches the file; the messages come back beside it.</summary>
    private static async Task<(byte[] File, string Type, string[] Messages)> PrintAsync(
        HttpClient http, string? token, string root, string webMap, string format, string template)
    {
        (int status, JsonElement answer) = await SendAsync(http, token, $"{root}{Export}/execute", new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMap,
            ["Format"] = format,
            ["Layout_Template"] = template,
            ["f"] = "json",
        });

        Assert.True(status == 200, $"The print answered {status}: {answer}");
        JsonElement result = answer.GetProperty("results")[0];
        Assert.Equal("Output_File", result.GetProperty("paramName").GetString());
        Assert.Equal("GPDataFile", result.GetProperty("dataType").GetString());
        string url = result.GetProperty("value").GetProperty("url").GetString()!;

        // The output address needs no token, as ArcGIS's output directory does not.
        using HttpResponseMessage file = await http.GetAsync(new Uri(url));
        Assert.True(file.IsSuccessStatusCode, $"The printed file answered {(int)file.StatusCode}.");

        return (
            await file.Content.ReadAsByteArrayAsync(),
            file.Content.Headers.ContentType?.MediaType ?? string.Empty,
            [.. answer.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("description").GetString()!)]);
    }

    private static readonly int[] OutputSize = [320, 240];

    private static string WebMap(JsonElement extent, params object[] layers) => JsonSerializer.Serialize(new
    {
        mapOptions = new { extent, spatialReference = extent.GetProperty("spatialReference") },
        operationalLayers = layers,
        exportOptions = new { outputSize = OutputSize, dpi = 96 },
        layoutOptions = new { titleText = "Conformance print", copyrightText = "(c) the suite" },
    });

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static (int Width, int Height) PngSize(byte[] png)
    {
        Assert.True(png.Length > 24 && png.AsSpan(0, 8).SequenceEqual(PngSignature),
            "The file is not a PNG.");
        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
    }

    private async Task<(string Root, string Token, string LayerUrl, JsonElement Extent)> QueryableAsync()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string service = (Environment.GetEnvironmentVariable(QueryCapabilityConformanceTests.LayerVariable) ?? "hosted/ci_buildings").Trim('/');
        string layerUrl = $"{root}/rest/services/{service}/FeatureServer/0";
        using HttpClient http = Unverified();
        (_, JsonElement layer) = await SendAsync(http, token, $"{layerUrl}?f=json");
        return (root, token, layerUrl, layer.GetProperty("extent"));
    }

    [Fact]
    public async Task The_service_its_tasks_and_its_layouts_answer_and_the_portal_names_it()
    {
        string root = await RequireServerAsync();
        using HttpClient http = Unverified();

        (int status, JsonElement service) = await SendAsync(http, null, $"{root}{Service}?f=json");
        Assert.Equal(200, status);
        Assert.Equal("esriExecutionTypeSynchronous", service.GetProperty("executionType").GetString());
        string[] tasks = [.. service.GetProperty("tasks").EnumerateArray().Select(t => t.GetString()!)];
        Assert.Contains("Export Web Map Task", tasks);
        Assert.Contains("Get Layout Templates Info Task", tasks);

        (_, JsonElement task) = await SendAsync(http, null, $"{root}{Export}?f=json");
        JsonElement[] parameters = [.. task.GetProperty("parameters").EnumerateArray()];
        Assert.Contains(parameters, p => p.GetProperty("name").GetString() == "Web_Map_as_JSON"
            && p.GetProperty("parameterType").GetString() == "esriGPParameterTypeRequired");
        Assert.Contains(parameters, p => p.GetProperty("name").GetString() == "Output_File"
            && p.GetProperty("dataType").GetString() == "GPDataFile"
            && p.GetProperty("direction").GetString() == "esriGPParameterDirectionOutput");
        JsonElement layout = parameters.Single(p => p.GetProperty("name").GetString() == "Layout_Template");
        string[] layouts = [.. layout.GetProperty("choiceList").EnumerateArray().Select(c => c.GetString()!)];
        Assert.Contains("MAP_ONLY", layouts);
        Assert.Contains("A4 Landscape", layouts);
        Assert.Contains("Letter ANSI A Portrait", layouts);

        (int listed, JsonElement info) = await SendAsync(http, null, $"{root}{Service}/Get%20Layout%20Templates%20Info%20Task/execute",
            new Dictionary<string, string> { ["f"] = "json" });
        Assert.Equal(200, listed);
        JsonElement output = info.GetProperty("results")[0];
        Assert.Equal("Output_JSON", output.GetProperty("paramName").GetString());
        JsonElement a4 = output.GetProperty("value").EnumerateArray().Single(t => t.GetProperty("layoutTemplate").GetString() == "A4 Landscape");
        Assert.Equal(11.69, a4.GetProperty("pageSize")[0].GetDouble(), 2);
        Assert.True(a4.GetProperty("layoutOptions").GetProperty("hasLegend").GetBoolean());

        (_, JsonElement portal) = await SendAsync(http, null, $"{root}/sharing/rest/portals/self?f=json");
        Assert.EndsWith("/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task",
            portal.GetProperty("helperServices").GetProperty("printTask").GetProperty("url").GetString(), StringComparison.Ordinal);

        (_, JsonElement utilities) = await SendAsync(http, null, $"{root}/rest/services/Utilities?f=json");
        Assert.Contains(utilities.GetProperty("services").EnumerateArray(),
            s => s.GetProperty("name").GetString() == "Utilities/PrintingTools" && s.GetProperty("type").GetString() == "GPServer");
    }

    [Fact]
    public async Task A_map_only_png_is_a_png_of_the_asked_size_with_the_layer_drawn()
    {
        (string root, string token, string layerUrl, JsonElement extent) = await QueryableAsync();
        using HttpClient http = Unverified();

        (byte[] drawn, string type, string[] messages) = await PrintAsync(
            http, token, root, WebMap(extent, new { id = "q", title = "Queryable", url = layerUrl }), "PNG32", "MAP_ONLY");
        Assert.Equal("image/png", type);
        Assert.Equal((320, 240), PngSize(drawn));
        Assert.Empty(messages);

        (byte[] empty, _, _) = await PrintAsync(http, token, root, WebMap(extent), "PNG32", "MAP_ONLY");
        Assert.Equal((320, 240), PngSize(empty));
        Assert.False(drawn.AsSpan().SequenceEqual(empty), "A print of the layer is the same file as a print of nothing.");

        // A synchronous service still takes a job, and the job's result is the same kind of file.
        (int submitted, JsonElement job) = await SendAsync(http, token, $"{root}{Export}/submitJob", new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = WebMap(extent, new { id = "q", url = layerUrl }),
            ["Format"] = "JPG",
            ["Layout_Template"] = "MAP_ONLY",
            ["f"] = "json",
        });
        Assert.Equal(200, submitted);
        string id = job.GetProperty("jobId").GetString()!;
        (_, JsonElement status) = await SendAsync(http, token, $"{root}{Export}/jobs/{id}?f=json");
        Assert.Equal("esriJobSucceeded", status.GetProperty("jobStatus").GetString());
        (_, JsonElement result) = await SendAsync(http, token, $"{root}{Export}/jobs/{id}/results/Output_File?f=json");
        using HttpResponseMessage jpeg = await http.GetAsync(new Uri(result.GetProperty("value").GetProperty("url").GetString()!));
        byte[] bytes = await jpeg.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8, "The job's file is not a JPEG.");
    }

    [Fact]
    public async Task An_a4_landscape_pdf_is_a_pdf_of_the_page()
    {
        (string root, string token, string layerUrl, JsonElement extent) = await QueryableAsync();
        using HttpClient http = Unverified();

        (byte[] pdf, string type, _) = await PrintAsync(
            http, token, root, WebMap(extent, new { id = "q", title = "Queryable", url = layerUrl }), "PDF", "A4 Landscape");

        Assert.Equal("application/pdf", type);
        string text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);

        // A4 landscape is 11.69 by 8.27 inches: 841.68 by 595.44 points.
        Assert.Contains("/MediaBox [0 0 841.68 595.44]", text, StringComparison.Ordinal);
        Assert.Contains("/Subtype /Image", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_layer_at_another_address_is_not_fetched_and_the_result_says_so()
    {
        (string root, string token, _, JsonElement extent) = await QueryableAsync();
        using HttpClient http = Unverified();

        // Addresses a print service could be talked into fetching: another host, and this machine by another name.
        foreach (string elsewhere in (string[])["https://example.com/arcgis/rest/services/World/MapServer", "http://169.254.169.254/latest/meta-data/"])
        {
            (byte[] printed, _, string[] messages) = await PrintAsync(
                http, token, root, WebMap(extent, new { id = "x", title = "Elsewhere", url = elsewhere }), "PNG32", "MAP_ONLY");

            Assert.Contains(messages, m => m.Contains("'Elsewhere' was not drawn", StringComparison.Ordinal)
                && m.Contains("not one of this server's services", StringComparison.Ordinal));

            (byte[] empty, _, _) = await PrintAsync(http, token, root, WebMap(extent), "PNG32", "MAP_ONLY");
            Assert.True(printed.AsSpan().SequenceEqual(empty), $"A print naming {elsewhere} drew something.");
        }

        (int refused, JsonElement why) = await SendAsync(http, token, $"{root}{Export}/execute", new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = WebMap(extent),
            ["Format"] = "GIF",
            ["f"] = "json",
        });
        Assert.Equal(400, refused);
        Assert.Contains("not one this print service writes", why.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_private_layer_is_not_drawn_for_an_anonymous_caller()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string stamp = Guid.NewGuid().ToString("N")[..8];
        string service = $"zz_print_{stamp}";
        string layerName = $"zz_print_l_{stamp}";

        (int published, string made) = await PublishOneAsync(service, layerName, folder: null, sharing: "private");
        Assert.True(published is 200 or 201, $"Publishing a private service answered {published}: {made}");

        try
        {
            using HttpClient http = Unverified();
            string layerUrl = $"{root}/rest/services/{service}/FeatureServer/0";
            (int read, JsonElement layer) = await SendAsync(http, token, $"{layerUrl}?f=json");
            Assert.Equal(200, read);
            JsonElement extent = layer.GetProperty("extent");
            string webMap = WebMap(extent, new { id = "p", title = "Private", url = layerUrl });

            (byte[] mine, _, string[] mineSaid) = await PrintAsync(http, token, root, webMap, "PNG32", "MAP_ONLY");
            Assert.Empty(mineSaid);

            (byte[] theirs, _, string[] theirsSaid) = await PrintAsync(http, null, root, webMap, "PNG32", "MAP_ONLY");
            Assert.Contains(theirsSaid, m => m.Contains("'Private' was not drawn", StringComparison.Ordinal)
                && m.Contains("that you may read", StringComparison.Ordinal));

            (byte[] empty, _, _) = await PrintAsync(http, null, root, WebMap(extent), "PNG32", "MAP_ONLY");
            Assert.True(theirs.AsSpan().SequenceEqual(empty), "An anonymous print drew a private layer.");
            Assert.False(mine.AsSpan().SequenceEqual(empty), "The owner's print of the private layer drew nothing, so the anonymous one proves nothing.");
        }
        finally
        {
            await UnpublishAsync(service, layerName);
        }
    }
}

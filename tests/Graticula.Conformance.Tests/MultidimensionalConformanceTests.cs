using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-159: a NetCDF of two variables over time and depth is published as a multidimensional image service — an image
/// a slice, each with its variable and depth — and ArcGIS's <c>multidimensionalDefinition</c> chooses among them.
/// <c>ocean.nc</c> in the raster corpus is an 8 × 8 grid at 30° E, 41° N whose every pixel holds its slice's number:
/// 100 for temp, 200 for salt, plus ten for February and one for 100 m.
/// </summary>
[Collection("catalogue walk")]
public sealed class MultidimensionalConformanceTests : ArcGisClient
{
    private static byte[] Corpus(string name)
    {
        DirectoryInfo? at = new(AppContext.BaseDirectory);
        while (at is not null && at.GetFiles("*.sln").Length == 0) at = at.Parent;
        return File.ReadAllBytes(Path.Combine(at!.FullName, "tests", "Graticula.Raster.Tiff.Tests", "corpus", name));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        string root, string token, HttpMethod method, string path, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone());
    }

    private async Task<JsonElement> IdentifyAsync(string root, string token, string service, string extra = "")
    {
        (_, JsonElement said) = await SendAsync(root, token, HttpMethod.Get,
            $"{service}/identify?geometry=30.025,40.975&geometryType=esriGeometryPoint&f=json{extra}");
        return said;
    }

    private async Task<string> ValueAsync(string root, string token, string service, string extra = "")
    {
        JsonElement said = await IdentifyAsync(root, token, service, extra);
        Assert.False(said.TryGetProperty("error", out _), said.ToString());
        return said.GetProperty("value").GetString()!;
    }

    private static string Rule(string definition) =>
        "&mosaicRule=" + Uri.EscapeDataString($$"""{"multidimensionalDefinition":{{definition}}}""");

    [Fact]
    public async Task Variables_and_depths_become_slices_a_definition_chooses_among()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_md_{Guid.NewGuid():N}"[..16];

        try
        {
            using MultipartFormDataContent form = new();
            form.Add(new ByteArrayContent(Corpus("ocean.nc")), "file", "ocean.nc");
            form.Add(new StringContent(name), "name");
            (HttpStatusCode made, JsonElement said) = await SendAsync(root, token!, HttpMethod.Post, "/admin/coverages/upload", form);
            Assert.True(made == HttpStatusCode.Created, said.ToString());
            Assert.Equal(8, said.GetProperty("images").GetInt32());

            string service = $"/rest/services/hosted/{name}/ImageServer";
            JsonElement info = (await SendAsync(root, token!, HttpMethod.Get, $"{service}?f=json")).Body;
            Assert.True(info.GetProperty("hasMultidimensions").GetBoolean(), info.ToString());

            // multidimensionalInfo: both variables, each over StdTime and depth.
            JsonElement md = (await SendAsync(root, token!, HttpMethod.Get, $"{service}/multidimensionalInfo?f=json")).Body;
            JsonElement[] variables = [.. md.GetProperty("multidimensionalInfo").GetProperty("variables").EnumerateArray()];
            Assert.Equal(["temp", "salt"], variables.Select(v => v.GetProperty("name").GetString()));
            JsonElement[] dimensions = [.. variables[0].GetProperty("dimensions").EnumerateArray()];
            Assert.Equal(["StdTime", "depth"], dimensions.Select(d => d.GetProperty("name").GetString()));
            Assert.Equal([0d, 100d], dimensions[1].GetProperty("values").EnumerateArray().Select(v => v.GetDouble()));
            long january = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            Assert.Equal(january, dimensions[0].GetProperty("values")[0].GetInt64());

            // No definition: the first variable at its first depth, the latest month on top.
            Assert.Equal("110", await ValueAsync(root, token!, service));

            // A variable and a depth: both months, and a rule's MT_FIRST puts the lowest id — January — on top, as ArcGIS
            // does; then February asked for, and the depth as a range.
            long february = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            Assert.Equal("201", await ValueAsync(root, token!, service,
                Rule("""[{"variableName":"salt","dimensionName":"depth","values":[100]}]""")));
            Assert.Equal("211", await ValueAsync(root, token!, service,
                Rule($$"""[{"variableName":"salt","dimensionName":"StdTime","values":[{{february}}]},{"variableName":"salt","dimensionName":"depth","values":[[50,150]]}]""")));

            // time, with no definition: the first variable's first depth, in January.
            Assert.Equal("100", await ValueAsync(root, token!, service, $"&time={january + 3_600_000}"));

            // The catalog has a row a slice, with its variable to filter on.
            JsonElement count = (await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/query?where={Uri.EscapeDataString("Variable = 'salt'")}&returnCountOnly=true&f=json")).Body;
            Assert.Equal(4, count.GetProperty("count").GetInt32());

            // A variable it does not have is refused, naming those it has.
            JsonElement refused = await IdentifyAsync(root, token!, service, Rule("""[{"variableName":"wind"}]"""));
            Assert.Contains("temp, salt", refused.GetProperty("error").ToString(), StringComparison.Ordinal);
            JsonElement noDepth = await IdentifyAsync(root, token!, service,
                Rule("""[{"variableName":"temp","dimensionName":"height","values":[2]}]"""));
            Assert.Contains("depth", noDepth.GetProperty("error").ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await SendAsync(root, token!, HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
        }
    }
}

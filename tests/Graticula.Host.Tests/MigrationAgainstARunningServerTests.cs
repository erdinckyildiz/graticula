using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host.Tools;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// <c>graticula tools migrate</c> end to end, with the CI server as both the ArcGIS Server it reads and the server
/// it publishes to — ADR-081.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own source, because it speaks the REST directory the tool reads.</b> What is asserted is the part a unit
/// test cannot reach: that <c>plan</c> signs in, reads the directory and the data source's tables and writes a
/// file; and that <c>apply</c> publishes over a registered table and carries a drawing and an alias through the
/// target's own admin API, where the layer document then says them.
/// </para>
/// <para>
/// <b>Over <c>cifree.zz_free_two</c></b>, the free table <c>tools/ci-free-tables.sql</c> makes, and removed again
/// whatever happens.
/// </para>
/// </remarks>
[Trait("Needs", "RunningHost")]
public sealed class MigrationAgainstARunningServerTests
{
    private const string Layer = "zz_migrated";
    private const string Service = "zz_migrated_svc";

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{name} is not set, so this test FAILS rather than skips. It needs a running server and an account on it.");

    [Fact]
    public async Task Plan_reads_both_servers_and_apply_publishes_with_the_drawing_and_the_alias()
    {
        string url = Required("GRATICULA_TEST_URL");
        Environment.SetEnvironmentVariable("GRATICULA_USER", Required("GRATICULA_TEST_USER"));
        Environment.SetEnvironmentVariable("GRATICULA_PASSWORD", Required("GRATICULA_TEST_PASSWORD"));

        string planPath = Path.Combine(Path.GetTempPath(), $"migration-plan-{Guid.NewGuid():N}.json");
        string applyPath = Path.Combine(Path.GetTempPath(), $"migration-apply-{Guid.NewGuid():N}.json");

        using HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using HttpClient http = new(handler) { BaseAddress = new Uri(url) };

        try
        {
            // ---- plan ----
            using StringWriter planned = new();
            int planExit = await MigrationPlan.RunAsync(
                ["tools", "migrate", "plan", url, "--to", url, "--datasource", "datastore", "--out", planPath, "--insecure"],
                planned,
                CancellationToken.None);

            Assert.True(planExit == 0, planned.ToString());
            Assert.Contains("Nothing has been published", planned.ToString(), StringComparison.Ordinal);

            JsonElement plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath)).RootElement;
            Assert.True(plan.GetProperty("layers").GetArrayLength() > 0, $"The plan read no layers from {url}.");

            // ---- apply: one entry, over a free table, as a person would have filled it in ----
            await File.WriteAllTextAsync(applyPath, $$$"""
                {"source":"{{{url}}}","dataSource":"datastore","layers":[
                  {"service":"{{{Service}}}","folder":"hosted","sourceLayer":0,"name":"{{{Layer}}}",
                   "schema":"cifree","table":"zz_free_two","geometryColumn":"shape","objectIdColumn":"objectid",
                   "srid":3857,"geometryType":"Polygon",
                   "aliases":{"name":"Ad"},
                   "drawingInfo":{"renderer":{"type":"simple","symbol":{"type":"esriSFS","style":"esriSFSSolid",
                     "color":[200,40,40,160],"outline":{"type":"esriSLS","style":"esriSLSSolid","color":[0,0,0,255],"width":1} } } } }
                ]}
                """);

            using StringWriter applied = new();
            int applyExit = await MigrationPlan.RunAsync(
                ["tools", "migrate", "apply", applyPath, "--to", url, "--insecure"], applied, CancellationToken.None);

            string said = applied.ToString();
            Assert.True(applyExit == 0, said);
            Assert.Contains($"published {Layer} over cifree.zz_free_two", said, StringComparison.Ordinal);
            Assert.Contains("its drawing", said, StringComparison.Ordinal);
            Assert.Contains("1 alias(es)", said, StringComparison.Ordinal);

            // ---- and the layer document says both ----
            string token = await TokenAsync(http);
            using HttpRequestMessage read = new(HttpMethod.Get, $"/rest/services/hosted/{Service}/FeatureServer/0?f=json");
            read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage answer = await http.SendAsync(read);
            JsonElement layer = JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement;

            Assert.Equal(
                "Ad",
                layer.GetProperty("fields").EnumerateArray().First(f => f.GetProperty("name").GetString() == "name")
                    .GetProperty("alias").GetString());
            Assert.Equal("simple", layer.GetProperty("drawingInfo").GetProperty("renderer").GetProperty("type").GetString());
        }
        finally
        {
            string token = await TokenAsync(http);

            foreach (string path in (string[])[$"/admin/layers/{Layer}", $"/admin/featureservices/{Service}?folder=hosted"])
            {
                using HttpRequestMessage delete = new(HttpMethod.Delete, path);
                delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage ignored = await http.SendAsync(delete);
            }

            File.Delete(planPath);
            File.Delete(applyPath);
        }
    }

    private static async Task<string> TokenAsync(HttpClient http)
    {
        using StringContent body = new(
            JsonSerializer.Serialize(new { name = Required("GRATICULA_TEST_USER"), password = Required("GRATICULA_TEST_PASSWORD") }),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await http.PostAsync("/rest/auth/login", body);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    }
}

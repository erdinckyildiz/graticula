using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// A registered layer's answer lifetime is set on its service's Parameters page — D-308.
/// </summary>
/// <remarks>
/// <para>
/// <b>The box was in the Tile layer page</b>, which is drawn only for a layer with tiles, and ADR-206 took tiles off
/// every registered layer on 2026-10-20 while its query answers kept that lifetime — five minutes by default for a
/// registered PostGIS table. So a registered layer's figure could be set only through the API.
/// </para>
/// <para>
/// <b>A registered layer is made for the test</b>, from a second source on <c>GRATICULA_TEST_PG</c> and the table
/// <c>GRATICULA_TEST_TILE_SERVICE</c> draws, as <c>ARegisteredPostGisLayerServesNoTilesTests</c> does, and removed
/// afterwards. Set is answered by the harness, which never sends a write; what is asserted is the request.
/// </para>
/// </remarks>
public sealed class AnswerLifetimeTests : ConsoleTest
{
    private const string SourceName = "zz_answer_life";
    private const string LayerName = "zz_answer_life_layer";

    [Fact]
    public async Task A_registered_layers_answer_lifetime_is_set_on_its_Parameters_page()
    {
        string? connection = Environment.GetEnvironmentVariable("GRATICULA_TEST_PG");
        string? tiled = Environment.GetEnvironmentVariable("GRATICULA_TEST_TILE_SERVICE");

        Assert.False(string.IsNullOrWhiteSpace(connection),
            "GRATICULA_TEST_PG is not set, so this FAILS rather than skips: it registers that database as a second source.");
        Assert.False(string.IsNullOrWhiteSpace(tiled),
            "GRATICULA_TEST_TILE_SERVICE is not set, so this FAILS rather than skips: it borrows that service's table.");

        (string token, _) = await SignInAsync();
        (string schema, string table) = await TableOfAsync(tiled!);
        await CleanUpAsync();

        (int made, string source) = await AdminAsync(HttpMethod.Post, "/admin/datasources",
            JsonSerializer.Serialize(new { name = SourceName, connectionString = connection }));
        Assert.True(made is 200 or 201, $"Registering the second source answered {made}: {source}");

        try
        {
            string id = JsonDocument.Parse(source).RootElement.GetProperty("id").GetString()!;
            JsonElement found = await OfferedAsync(id, schema, table);

            (int published, string answer) = await AdminAsync(HttpMethod.Post, "/admin/layers", JsonSerializer.Serialize(new
            {
                name = LayerName,
                dataSourceId = id,
                schemaName = schema,
                tableName = table,
                geometryColumn = found.GetProperty("geometryColumn").GetString(),
                identityColumn = found.GetProperty("objectIdColumn").GetString(),
                objectIdColumn = found.GetProperty("objectIdColumn").GetString(),
                srid = found.GetProperty("srid").GetInt32(),
                geometryType = found.TryGetProperty("geometryType", out JsonElement kind)
                    && kind.ValueKind == JsonValueKind.String ? kind.GetString() : "Polygon",
            }));
            Assert.True(published is 200 or 201, $"Publishing the registered layer answered {published}: {answer}");

            string url = JsonDocument.Parse(answer).RootElement.GetProperty("url").GetString()!;
            string service = url["/rest/services/".Length..url.IndexOf("/FeatureServer", StringComparison.Ordinal)];

            await OpenAsync("/server/#/service/" + string.Join("/", Array.ConvertAll(service.Split('/'), Uri.EscapeDataString)), token);
            await WaitForAsync(Shown("#serviceNav a[data-service-page=limits]"), "The registered service has no Parameters page in Server.");

            // The listing not yet read when the page draws, as CI's slower run had it: the section reads it itself.
            await Browser.EvaluateAsync<bool>("(known = [], true)");
            await ClickAsync("#serviceNav a[data-service-page=limits]");

            await WaitForAsync(Shown("#answerLifeBox input[type=number]"),
                "A registered layer's Parameters page offers no answer lifetime, so it is set only through the API (D-308).");

            // Empty, and the line under it says what applies: the server's five minutes for a registered PostGIS table.
            Assert.Equal(string.Empty, await Browser.EvaluateAsync<string>("document.getElementById('answerLife-0').value"));
            Assert.Contains("Follows the server: 5 minutes", await Browser.EvaluateAsync<string>(
                "document.getElementById('answerLifeSays-0').textContent") ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("another tool", await Browser.EvaluateAsync<string>(
                "document.getElementById('answerLifeAbout').textContent") ?? string.Empty, StringComparison.Ordinal);

            // Not a number: said, marked, and nothing sent.
            await Browser.EvaluateAsync<bool>("(document.getElementById('answerLife-0').value = '1.5', true)");
            await ClickAsync("[data-answer-life='0']");
            await WaitForAsync("(document.getElementById('answerLifeResult')?.textContent || '').includes('whole number')",
                "A fraction of a second was not refused in words.");
            Assert.DoesNotContain(await WritesAsync(), w => w.Contains("/cache", StringComparison.Ordinal));

            // A number, by Enter in the box: sent as seconds, for this layer of this service, and said in the live region
            // that was there before the rows were drawn again — one inserted with its text is often not announced.
            await Browser.EvaluateAsync<bool>("(window.__said = document.getElementById('answerLifeResult'), true)");
            await Browser.EvaluateAsync<bool>("(el = document.getElementById('answerLife-0'), el.value = '900', "
                + "el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })), true)");
            await WaitForAsync("(document.getElementById('answerLifeResult')?.textContent || '').includes('15 minutes')",
                "Enter in the box set nothing, or said nothing of what it did.");
            Assert.True(await Browser.EvaluateAsync<bool>("window.__said === document.getElementById('answerLifeResult')"),
                "The status line was drawn again with its sentence in it, so a screen reader may say nothing.");

            string sent = Array.Find(await WritesAsync(), w => w.Contains("/cache", StringComparison.Ordinal)) ?? string.Empty;
            Assert.Contains($"PUT /admin/layers/{LayerName}/cache?service=", sent, StringComparison.Ordinal);

            // Focus is back on the row's button, which was drawn again.
            Assert.Equal("0", await Browser.EvaluateAsync<string>("document.activeElement?.dataset?.answerLife ?? ''"));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await CleanUpAsync();
        }
    }

    [Fact]
    public async Task A_service_with_tiles_sets_the_figure_on_its_Tile_layer_page_and_not_here()
    {
        string? tiled = Environment.GetEnvironmentVariable("GRATICULA_TEST_TILE_SERVICE");
        Assert.False(string.IsNullOrWhiteSpace(tiled), "GRATICULA_TEST_TILE_SERVICE is not set, so this FAILS rather than skips.");

        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/service/" + string.Join("/", Array.ConvertAll(tiled!.Trim('/').Split('/'), Uri.EscapeDataString)), token);
        await WaitForAsync(Shown("#serviceNav a[data-service-page=limits]"), "The tiled service has no Parameters page in Server.");
        await ClickAsync("#serviceNav a[data-service-page=limits]");
        await WaitForAsync(Shown("#capDeadline"), "The Parameters page did not draw.");

        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('answerLifeBox').hidden"),
            "A service with tiles offers the lifetime on Parameters as well as on its Tile layer page: two boxes for one figure.");
    }

    /// <summary>The schema and table of the tiled service's hosted layer.</summary>
    private async Task<(string Schema, string Table)> TableOfAsync(string tiled)
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/admin/layers");
        Assert.Equal(200, status);

        string wanted = tiled.Trim('/');
        string name = wanted.Contains('/', StringComparison.Ordinal) ? wanted[(wanted.LastIndexOf('/') + 1)..] : wanted;

        foreach (JsonElement layer in JsonDocument.Parse(body).RootElement.GetProperty("layers").EnumerateArray())
        {
            if (string.Equals(layer.GetProperty("service").GetString(), name, StringComparison.OrdinalIgnoreCase)
                && layer.GetProperty("hosted").GetBoolean())
            {
                string qualified = layer.GetProperty("table").GetString()!;
                int dot = qualified.IndexOf('.', StringComparison.Ordinal);
                return (qualified[..dot], qualified[(dot + 1)..]);
            }
        }

        Assert.Fail($"No hosted layer of '{tiled}' is in /admin/layers, so there is no table to borrow.");
        throw new InvalidOperationException();
    }

    /// <summary>The table as the new source's probe offers it.</summary>
    private async Task<JsonElement> OfferedAsync(string source, string schema, string table)
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, $"/admin/datasources/{source}/capability");
        Assert.True(status == 200, $"The new source's capability answered {status}: {body}");

        foreach (JsonElement each in JsonDocument.Parse(body).RootElement.GetProperty("tables").EnumerateArray())
        {
            if (each.GetProperty("schemaName").GetString() == schema && each.GetProperty("tableName").GetString() == table
                && each.TryGetProperty("objectIdColumn", out JsonElement oid) && oid.ValueKind == JsonValueKind.String)
            {
                return each.Clone();
            }
        }

        Assert.Fail($"The registered source does not offer {schema}.{table} with an integer identity.");
        throw new InvalidOperationException();
    }

    private async Task CleanUpAsync()
    {
        await AdminAsync(HttpMethod.Delete, $"/admin/layers/{LayerName}");

        (int status, string body) = await AdminAsync(HttpMethod.Get, "/admin/datasources");

        if (status == 200)
        {
            foreach (JsonElement each in JsonDocument.Parse(body).RootElement.GetProperty("dataSources").EnumerateArray())
            {
                if (each.GetProperty("name").GetString() == SourceName)
                {
                    await AdminAsync(HttpMethod.Delete, $"/admin/datasources/{each.GetProperty("id").GetString()}");
                }
            }
        }

        await AdminAsync(HttpMethod.Post, "/admin/featureservices/sweep", "{}");
    }
}

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A server-wide setting, so what changes it runs alone — every other query on the server answers under it.
/// </summary>
[CollectionDefinition("server settings", DisableParallelization = true)]
public sealed class ServerSettingsRunAlone;

/// <summary>
/// The page size is one number: the document gives it, a query naming none gets it, a query asking for
/// more gets it, and a service's own replaces the server's — V-70, ADR-084.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-09-23 from the third ArcGIS review.</b> A layer document said <c>maxRecordCount</c> 50000
/// over a query answering 1000, and a script that paged by the document skipped rows. The owner chose ArcGIS's
/// single number, a server page size set from the console, and a service's own beside it.
/// </para>
/// <para>
/// <b>In a collection that runs alone, and every change is put back in <c>finally</c></b>, because the
/// server's page size is what every other query on the fixture answers under.
/// </para>
/// </remarks>
[Collection("server settings")]
public sealed class AServerHasOnePageSizeTests : ArcGisClient
{
    private const string LargeVariable = "GRATICULA_TEST_LARGE";

    private static HttpClient Client() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private static string Large()
    {
        string? name = Environment.GetEnvironmentVariable(LargeVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(name),
            $"{LargeVariable} is not set, so this FAILS rather than skips: a page size shows only on a layer "
            + "with more rows than the page.");

        return name!.Trim('/');
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> AdminAsync(
        string root, HttpMethod method, string path, object? body = null)
    {
        using HttpClient http = Client();
        using HttpRequestMessage request = new(method, new Uri($"{root}{path}"));

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<(int Document, int Plain, bool More, int Asked)> MeasureAsync(string service)
    {
        string layer = $"/rest/services/{service}/FeatureServer/0";

        int document = (await GetJsonAsync(layer)).GetProperty("maxRecordCount").GetInt32();

        JsonElement plain = await GetJsonAsync($"{layer}/query?where=1%3D1&returnGeometry=false");
        JsonElement asked = await GetJsonAsync($"{layer}/query?where=1%3D1&returnGeometry=false&resultRecordCount=100000");

        return (
            document,
            plain.GetProperty("features").GetArrayLength(),
            plain.TryGetProperty("exceededTransferLimit", out JsonElement more) && more.GetBoolean(),
            asked.GetProperty("features").GetArrayLength());
    }

    private static string NameOf(string service) => service[(service.LastIndexOf('/') + 1)..];

    private static string? FolderOf(string service) =>
        service.Contains('/', StringComparison.Ordinal) ? service[..service.LastIndexOf('/')] : null;

    [Fact]
    public async Task The_servers_page_size_is_what_the_document_says_and_the_query_does()
    {
        string root = await RequireServerAsync();
        string service = Large();

        (int _, int rows, _, _) = await MeasureAsync(service);
        int page = Math.Max(1, rows / 3);

        Assert.True(rows > 3, $"{service} has {rows} rows; a page size cannot show on so few.");

        try
        {
            (HttpStatusCode set, JsonElement said) = await AdminAsync(root, HttpMethod.Put, "/admin/settings", new { pageSize = page });

            Assert.Equal(HttpStatusCode.OK, set);
            Assert.Equal("stored", said.GetProperty("pageSize").GetProperty("source").GetString());

            (int document, int plain, bool more, int asked) = await MeasureAsync(service);

            Assert.Equal(page, document);
            Assert.Equal(page, plain);
            Assert.True(more, "A page shorter than the layer did not say there was more.");

            // Asking for more than the page gets the page — the rule that makes the document's number true.
            Assert.Equal(page, asked);
        }
        finally
        {
            await AdminAsync(root, HttpMethod.Put, "/admin/settings", new { pageSize = (int?)null });
        }

        (HttpStatusCode status, JsonElement back) = await AdminAsync(root, HttpMethod.Get, "/admin/settings");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("configured", back.GetProperty("pageSize").GetProperty("source").GetString());
    }

    [Fact]
    public async Task A_services_own_page_size_replaces_the_servers_either_way()
    {
        string root = await RequireServerAsync();
        string service = Large();
        string capabilities = $"/admin/services/{NameOf(service)}/capabilities";

        (int _, int rows, _, _) = await MeasureAsync(service);
        int server = Math.Max(1, rows / 4);
        int own = Math.Max(server + 1, rows / 2);

        // What the service was configured with, so `finally` puts that back rather than clearing it.
        string where = $"{capabilities}?folder={Uri.EscapeDataString(FolderOf(service) ?? string.Empty)}";
        (_, JsonElement before) = await AdminAsync(root, HttpMethod.Get, where);

        object restore = new
        {
            folder = FolderOf(service),
            servesFeatures = Nullable<bool>(before, "servesFeatures"),
            servesTiles = Nullable<bool>(before, "servesTiles"),
            capabilities = before.TryGetProperty("capabilities", out JsonElement ceiling) && ceiling.ValueKind == JsonValueKind.Array
                ? ceiling.EnumerateArray().Select(c => c.GetString()).ToArray()
                : null,
            statementTimeoutMilliseconds = Nullable<int>(before, "statementTimeoutMs"),
            maxRecordCount = Nullable<int>(before, "maxRecordCount"),
            maxResponseBytes = Nullable<long>(before, "maxResponseBytes"),
            maxRequestBytes = Nullable<long>(before, "maxRequestBytes"),
            maxEditsPerTransaction = Nullable<int>(before, "maxEditsPerTransaction"),
            requestDeadlineSeconds = Nullable<int>(before, "requestDeadlineSeconds"),
        };

        try
        {
            await AdminAsync(root, HttpMethod.Put, "/admin/settings", new { pageSize = server });

            (HttpStatusCode set, _) = await AdminAsync(
                root, HttpMethod.Put, capabilities, new { folder = FolderOf(service), maxRecordCount = own });

            Assert.Equal(HttpStatusCode.OK, set);

            // Larger than the server's: the server's page size is a default, not a ceiling.
            (int document, int plain, _, int asked) = await MeasureAsync(service);

            Assert.Equal(own, document);
            Assert.Equal(own, plain);
            Assert.Equal(own, asked);

            (HttpStatusCode read, JsonElement limits) = await AdminAsync(root, HttpMethod.Get, where);

            Assert.Equal(HttpStatusCode.OK, read);
            Assert.Equal(server, limits.GetProperty("serverPageSize").GetInt32());
        }
        finally
        {
            await AdminAsync(root, HttpMethod.Put, capabilities, restore);
            await AdminAsync(root, HttpMethod.Put, "/admin/settings", new { pageSize = (int?)null });
        }
    }

    private static T? Nullable<T>(JsonElement document, string name)
        where T : struct =>
        document.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value.Deserialize<T>()
            : null;

    [Fact]
    public async Task A_default_page_beside_the_page_size_is_refused_with_why()
    {
        string root = await RequireServerAsync();
        string service = Large();

        (HttpStatusCode status, JsonElement said) = await AdminAsync(
            root,
            HttpMethod.Put,
            $"/admin/services/{NameOf(service)}/capabilities",
            new { folder = FolderOf(service), defaultRecordCount = 50 });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("maxRecordCount", said.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A service's page size below one is refused in a sentence, and the ceiling it is held to is said.
    /// </summary>
    /// <remarks>
    /// <b>Design review 2026-09-24:</b> 0 came back as the constructor's exception text, with its parameter
    /// name and "Actual value was 0" appended, on an administrator's screen.
    /// </remarks>
    [Fact]
    public async Task A_services_page_size_below_one_is_refused_in_a_sentence()
    {
        string root = await RequireServerAsync();
        string service = Large();
        string capabilities = $"/admin/services/{NameOf(service)}/capabilities";

        (HttpStatusCode status, JsonElement said) = await AdminAsync(
            root, HttpMethod.Put, capabilities, new { folder = FolderOf(service), maxRecordCount = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, status);

        string message = said.GetProperty("error").GetProperty("message").GetString() ?? string.Empty;

        Assert.Contains("page size", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Parameter", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Actual value", message, StringComparison.Ordinal);

        (_, JsonElement limits) = await AdminAsync(
            root, HttpMethod.Get, $"{capabilities}?folder={Uri.EscapeDataString(FolderOf(service) ?? string.Empty)}");

        Assert.True(limits.GetProperty("pageSizeCeiling").GetInt32() >= limits.GetProperty("serverPageSize").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public async Task A_page_size_outside_one_and_the_ceiling_is_refused(int pageSize)
    {
        string root = await RequireServerAsync();

        (HttpStatusCode status, _) = await AdminAsync(root, HttpMethod.Put, "/admin/settings", new { pageSize });

        Assert.Equal(HttpStatusCode.BadRequest, status);

        (_, JsonElement back) = await AdminAsync(root, HttpMethod.Get, "/admin/settings");

        Assert.Equal("configured", back.GetProperty("pageSize").GetProperty("source").GetString());
    }

    [Fact]
    public async Task The_settings_are_not_readable_without_signing_in()
    {
        (HttpStatusCode status, _) = await AnonymousAsync("/admin/settings");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }
}

using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A stand-alone ArcGIS Server's address (<c>/arcgis/rest</c>) and ArcGIS Enterprise's hosting server's
/// (<c>/server/rest</c>) answer what the root does, and the Server screens under <c>/server/</c> are still pages.
/// </summary>
public sealed class ArcGisAddressesConformanceTests : ArcGisClient
{
    [Theory]
    [InlineData("/arcgis")]
    [InlineData("/server")]
    public async Task The_directory_under_either_name_is_the_roots(string prefix)
    {
        JsonElement root = await GetJsonAsync("/rest/services");
        JsonElement aliased = await GetJsonAsync($"{prefix}/rest/services");

        Assert.Equal(root.GetProperty("folders").GetRawText(), aliased.GetProperty("folders").GetRawText());
        Assert.Equal(root.GetProperty("services").GetRawText(), aliased.GetProperty("services").GetRawText());
    }

    [Theory]
    [InlineData("/arcgis")]
    [InlineData("/server")]
    public async Task A_services_document_under_either_name_is_the_roots(string prefix)
    {
        string path = await AnyServicePathAsync();

        JsonElement root = await GetJsonAsync(path);
        JsonElement aliased = await GetJsonAsync(prefix + path);

        Assert.Equal(root.GetRawText(), aliased.GetRawText());
    }

    [Fact]
    public async Task The_Server_screens_stay_pages()
    {
        string page = await GetHtmlAsync("/server/");

        Assert.Contains("<html", page, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((int)HttpStatusCode.OK, await StatusOfAsync("/arcgis"));
    }

    [Theory]
    [InlineData("/home/item.html")]
    [InlineData("/portal/home/item.html")]
    [InlineData("/studio/home/item.html")]
    public async Task ArcGIS_s_item_page_goes_to_Studio_s(string page)
    {
        string root = await RequireServerAsync();
        using System.Net.Http.HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using System.Net.Http.HttpClient client = new(handler);

        using System.Net.Http.HttpResponseMessage response =
            await client.GetAsync(new Uri($"{root}{page}?id=0123456789abcdef0123456789abcdef"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/studio/item/0123456789abcdef0123456789abcdef", response.Headers.Location?.OriginalString);
    }

    /// <summary>The first service a folder of the directory lists, as <c>/rest/services/{name}/{type}</c>.</summary>
    private async Task<string> AnyServicePathAsync()
    {
        JsonElement root = await GetJsonAsync("/rest/services");
        foreach (string? folder in root.GetProperty("folders").EnumerateArray().Select(f => f.GetString()).Prepend(null))
        {
            JsonElement listing = folder is null ? root : await GetJsonAsync($"/rest/services/{folder}");
            foreach (JsonElement service in listing.GetProperty("services").EnumerateArray())
            {
                string name = service.GetProperty("name").GetString()!;
                if (!name.Contains("corpus_", StringComparison.Ordinal) && !name.Contains("zz_", StringComparison.Ordinal))
                {
                    return $"/rest/services/{name}/{service.GetProperty("type").GetString()}";
                }
            }
        }

        throw new InvalidOperationException("The server lists no service, so there is nothing to compare.");
    }
}

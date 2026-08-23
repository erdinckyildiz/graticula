using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Every read operation answers a <c>POST</c>, and honours what was posted.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-139](../../docs/architecture-debt.md), and the row was half wrong in a way worth
/// keeping.</b> It said POST is refused on every ArcGIS rendering and query operation. True of
/// `exportImage`, `identify` and the service documents, which answered a bare 405 with an empty
/// body. **Not true of `query`**: that route had always been mapped for both methods and had
/// always read `context.Request.Query` either way, so a posted query answered a different
/// question in silence — `returnCountOnly=true` in the body returned the full attribute set,
/// because every parameter was absent and every default applied.
/// </para>
/// <para>
/// <b>Which is why every test here asserts the answer and not the status.</b> A 200 proves the
/// route exists; only the content proves the body was read. The failure this suite is built
/// against was a 200.
/// </para>
/// <para>
/// <b>The cookie is still GET-only and that is asserted too.</b>
/// `Authentication.CookieToken` refuses anything but GET and HEAD, argued at length where it
/// lives: a forged cross-site request can only ever read. Accepting a posted parameter must not
/// weaken it, and `A_cookie_still_does_not_authenticate_a_post` is what says so.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class PostConformanceTests : ArcGisClient
{
    private async Task<(HttpStatusCode Status, string Body, string? Type)> PostAsync(
        string path, string form, bool authenticated = true)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(root + path))
        {
            Content = new FormUrlEncodedContent(Pairs(form)),
        };

        if (authenticated)
        {
            await AuthenticateAsync(request, root);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (
            response.StatusCode,
            await response.Content.ReadAsStringAsync(),
            response.Content.Headers.ContentType?.MediaType);
    }

    private static IEnumerable<KeyValuePair<string, string>> Pairs(string form)
    {
        foreach (string part in form.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = part.IndexOf('=', StringComparison.Ordinal);

            yield return at < 0
                ? new KeyValuePair<string, string>(part, string.Empty)
                : new KeyValuePair<string, string>(part[..at], part[(at + 1)..]);
        }
    }

    [Fact]
    public async Task A_posted_query_answers_what_was_posted()
    {
        // <b>The one that was a silent wrong answer rather than a refusal.</b> Count only,
        // asked in the body: if the body is ignored the answer is a feature set, and a feature
        // set has no `count` in it at all.
        string? layer = await AnyQueryableLayerAsync();

        if (layer is null)
        {
            return;
        }

        (HttpStatusCode status, string body, _) = await PostAsync(
            $"/rest/services/{layer}/query", "where=1=1&returnCountOnly=true&f=json");

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement answer = JsonDocument.Parse(body).RootElement;

        Assert.True(
            answer.TryGetProperty("count", out JsonElement count),
            "A posted `returnCountOnly=true` returned something other than a count, so the "
            + "body was ignored and every default applied. That is a wrong answer with a 200 "
            + "on it, which is worse than the 405 it replaced.");

        Assert.True(count.GetInt64() >= 0);
    }

    [Fact]
    public async Task A_posted_where_clause_actually_filters()
    {
        // <b>Not just *a* count — the right one.</b> A body that was read but not applied
        // would still answer a count, of everything.
        string? layer = await AnyQueryableLayerAsync();

        if (layer is null)
        {
            return;
        }

        (_, string all, _) = await PostAsync(
            $"/rest/services/{layer}/query", "where=1=1&returnCountOnly=true&f=json");

        // <b>`1=0`, not `objectid<10`, and the first version taught me why.</b> The
        // development layer has eight rows and every one of them has an id below ten, so the
        // clause was applied, selected everything, and the test reported that it had been
        // ignored. A predicate that is false for every row is false whatever the data is —
        // which is what a test about *was the body read* needs, rather than a fact about this
        // machine's fixtures.
        (_, string none, _) = await PostAsync(
            $"/rest/services/{layer}/query", "where=1=0&returnCountOnly=true&f=json");

        long total = JsonDocument.Parse(all).RootElement.GetProperty("count").GetInt64();
        long matched = JsonDocument.Parse(none).RootElement.GetProperty("count").GetInt64();

        Assert.True(total > 0, "The layer is empty, so nothing here can be distinguished.");

        Assert.Equal(0, matched);
    }

    [Fact]
    public async Task A_posted_export_draws_the_image_the_body_asked_for()
    {
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Post, new Uri($"{root}/rest/services/{service}/ImageServer/exportImage"))
        {
            Content = new FormUrlEncodedContent(
                Pairs("size=96,64&format=png&f=image")),
        };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        byte[] png = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47], png[..4]);

        // <b>The size from the body, read out of the PNG's own header.</b> A defaulted size
        // would be 400 by 400, so this is the assertion that proves the form was read rather
        // than merely accepted.
        int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];

        Assert.Equal(96, width);
        Assert.Equal(64, height);
    }

    [Fact]
    public async Task A_posted_identify_answers_about_the_point_in_the_body()
    {
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (HttpStatusCode status, string document, _) =
            await ReadJsonAsync($"/rest/services/{service}/ImageServer?f=json");

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement extent = JsonDocument.Parse(document).RootElement.GetProperty("extent");

        double x = (extent.GetProperty("xmin").GetDouble()
            + extent.GetProperty("xmax").GetDouble()) / 2;

        double y = (extent.GetProperty("ymin").GetDouble()
            + extent.GetProperty("ymax").GetDouble()) / 2;

        (HttpStatusCode answered, string body, _) = await PostAsync(
            $"/rest/services/{service}/ImageServer/identify",
            $"geometry={x.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
                + $"{y.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + "&geometryType=esriGeometryPoint&f=json");

        Assert.Equal(HttpStatusCode.OK, answered);

        JsonElement root = JsonDocument.Parse(body).RootElement;

        // <b>A location, and it is the one that was posted.</b> An ignored body would have
        // refused for a missing `geometry`, so this fails either way if the form is not read.
        Assert.True(
            root.TryGetProperty("location", out JsonElement location),
            $"A posted identify answered without a location: {body}");

        Assert.Equal(x, location.GetProperty("x").GetDouble(), 6);
    }

    [Fact]
    public async Task A_service_document_can_be_posted_for()
    {
        // The simplest case, and it was a 405 with an empty body — which tells a client
        // nothing at all, not even that the method was the problem.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (HttpStatusCode status, string body, string? type) = await PostAsync(
            $"/rest/services/{service}/ImageServer", "f=json");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("application/json", type);

        Assert.Equal(
            "Image,Tilemap",
            JsonDocument.Parse(body).RootElement.GetProperty("capabilities").GetString());
    }

    [Fact]
    public async Task A_parameter_in_the_url_wins_over_one_in_the_body()
    {
        // <b>Something has to win, and the URL is the half a reader can see.</b> A request
        // whose visible `f=json` was overridden by an invisible `f=image` in the body would be
        // unexplainable from outside — so the rule is written down and asserted rather than
        // left to whichever collection happened to be searched first.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri($"{root}/rest/services/{service}/ImageServer/exportImage?size=32,32"))
        {
            Content = new FormUrlEncodedContent(Pairs("size=256,256&format=png&f=image")),
        };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        byte[] png = await response.Content.ReadAsByteArrayAsync();

        int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];

        Assert.Equal(32, width);
    }

    [Fact]
    public async Task A_cookie_still_does_not_authenticate_a_post()
    {
        /*
          <b>The property this change had to not break.</b> `Authentication.CookieToken`
          refuses anything but GET and HEAD, and the reasoning where it lives is worth more
          than an antiforgery token: there is no token to get wrong, because the credential
          simply does not work for the requests that matter.

          <b>Asserted through a private service, which is the observable consequence.</b> A
          cookie that worked for POST would show a stranger's browser a service shared with
          the organisation; a cookie that does not leaves the request anonymous, and an
          anonymous caller cannot see it. The test does not need to read the cookie's
          implementation to know which happened.
        */
        string root = await RequireServerAsync();

        (HttpStatusCode status, string body, _) =
            await ReadJsonAsync("/admin/coverages");

        if (status != HttpStatusCode.OK)
        {
            return;
        }

        string? priv = null;

        foreach (JsonElement coverage in JsonDocument.Parse(body).RootElement
            .GetProperty("coverages").EnumerateArray())
        {
            if (coverage.GetProperty("sharing").GetString() == "private")
            {
                priv = coverage.GetProperty("name").GetString();
                break;
            }
        }

        if (priv is null)
        {
            return;
        }

        // A browser session, obtained the way the services directory hands one out.
        using HttpRequestMessage login = new(
            HttpMethod.Post, new Uri(root + "/rest/auth/login"))
        {
            Content = new FormUrlEncodedContent(Pairs(
                $"name={Environment.GetEnvironmentVariable("GRATICULA_TEST_USER") ?? "root"}"
                + $"&password={Environment.GetEnvironmentVariable("GRATICULA_TEST_PASSWORD")}"
                + "&f=json")),
        };

        using HttpResponseMessage signedIn = await Http.SendAsync(login);

        if (!signedIn.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            return;
        }

        string jar = string.Join("; ", Cut(cookies));

        // <b>The same private service, twice, on the cookie alone.</b> GET sees it; POST
        // must not.
        using HttpRequestMessage read = new(
            HttpMethod.Get, new Uri($"{root}/rest/services/{priv}/ImageServer?f=json"));

        read.Headers.Add("Cookie", jar);

        using HttpResponseMessage got = await Http.SendAsync(read);

        Assert.True(
            got.IsSuccessStatusCode,
            "A cookie did not authenticate a GET, so this test is measuring the wrong thing.");

        Assert.Contains("extent", await got.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using HttpRequestMessage posted = new(
            HttpMethod.Post, new Uri($"{root}/rest/services/{priv}/ImageServer"))
        {
            Content = new FormUrlEncodedContent(Pairs("f=json")),
        };

        posted.Headers.Add("Cookie", jar);

        using HttpResponseMessage refused = await Http.SendAsync(posted);

        string answer = await refused.Content.ReadAsStringAsync();

        Assert.DoesNotContain(
            "\"extent\"",
            answer,
            StringComparison.Ordinal);
    }

    private static IEnumerable<string> Cut(IEnumerable<string> cookies)
    {
        foreach (string cookie in cookies)
        {
            int at = cookie.IndexOf(';', StringComparison.Ordinal);

            yield return at < 0 ? cookie : cookie[..at];
        }
    }

    private async Task<(HttpStatusCode Status, string Body, string? Type)> ReadJsonAsync(
        string path)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (
            response.StatusCode,
            await response.Content.ReadAsStringAsync(),
            response.Content.Headers.ContentType?.MediaType);
    }

    private async Task<string?> AnyImageServiceAsync()
    {
        (HttpStatusCode status, string body, _) = await ReadJsonAsync("/admin/coverages");

        if (status != HttpStatusCode.OK)
        {
            return null;
        }

        foreach (JsonElement coverage in JsonDocument.Parse(body).RootElement
            .GetProperty("coverages").EnumerateArray())
        {
            if (coverage.GetProperty("sharing").GetString() != "private")
            {
                return coverage.GetProperty("name").GetString();
            }
        }

        return null;
    }

    private static Task<string?> AnyQueryableLayerAsync()
    {
        string? named = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE");

        if (string.IsNullOrWhiteSpace(named))
        {
            return Task.FromResult<string?>(null);
        }

        // The variable names a layer; the query route wants folder, service and layer index.
        string qualified = named.Contains('/', StringComparison.Ordinal)
            ? named
            : "hosted/" + named;

        return Task.FromResult<string?>(qualified + "/FeatureServer/0");
    }
}

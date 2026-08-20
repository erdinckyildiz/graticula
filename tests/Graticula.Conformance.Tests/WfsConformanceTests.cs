using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The WFS surface, driven over HTTP the way a client drives it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-08-20, and the gap it fills is the point.</b> WFS was verified
/// three ways when it was built — 28 documents against the published OGC schemas,
/// GDAL reading and converting, and the OGC CITE suite — and none of the three is
/// in this repository. Every one of them was a person running a tool. What was
/// missing is the thing that runs on every change: a suite that drives
/// <c>/wfs</c> against a live process and fails when the surface moves.
/// </para>
/// <para>
/// <b>Two of these assert agreement between surfaces rather than correctness of
/// one.</b> A directory page that advertises WFS for a layer WFS does not publish
/// is worse than a page that says nothing, and a service whose feature face an
/// operator switched off is switched off on every door or on none.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection, because this class mutates a live
/// service.</b> It switches a service's feature face off and back on, and xUnit runs
/// test classes in parallel — so on 2026-08-20 it ran beside `ArcGisConsistencyTests`
/// and `ServiceFolderConformanceTests` walking the same service and 404'd them.
/// Those were read as [D-75](../../docs/architecture-debt.md) recurrences with no
/// cause found; the cause was this class not being in the collection that exists to
/// prevent exactly it.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class WfsConformanceTests : ArcGisClient
{
    private const string Wfs = "http://www.opengis.net/wfs/2.0";
    private const string Ows = "http://www.opengis.net/ows/1.1";
    private const string Xsd = "http://www.w3.org/2001/XMLSchema";

    /// <summary>The WFS link a directory page offers, or null when it offers none.</summary>
    private static string? FormatLinkIn(string html)
    {
        Match line = Regex.Match(
            html, "<div class=\"fmt\">(.*?)</div>", RegexOptions.Singleline);

        if (!line.Success)
        {
            return null;
        }

        Match link = Regex.Match(
            line.Groups[1].Value, "href=\"([^\"]+)\"[^>]*>WFS</a>", RegexOptions.Singleline);

        return link.Success ? WebUtility.HtmlDecode(link.Groups[1].Value) : null;
    }

    private async Task<XDocument> XmlAsync(string path)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"{path} answered {(int)response.StatusCode}: {body[..Math.Min(400, body.Length)]}");

        return XDocument.Parse(body);
    }

    /// <summary>Every feature type the capabilities document advertises.</summary>
    private async Task<IReadOnlyList<string>> PublishedTypesAsync()
    {
        XDocument capabilities =
            await XmlAsync("/wfs?service=WFS&version=2.0.0&request=GetCapabilities");

        return
        [
            .. capabilities
                .Descendants(XName.Get("FeatureType", Wfs))
                .Select(t => t.Element(XName.Get("Name", Wfs))?.Value)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!),
        ];
    }

    // ---------- discovery ----------

    [Fact]
    public async Task A_service_page_offers_wfs_beside_json()
    {
        // <b>The whole reason this was built.</b> An ArcGIS Server directory
        // prints JSON | SOAP | WMS | WFS on the format line, and that line is how
        // somebody discovers a service speaks a second protocol. This server spoke
        // WFS for a day with the word appearing on no page.
        string? service = await AnyServiceNameAsync();
        Assert.NotNull(service);

        string html = await GetHtmlAsync($"/rest/services/{service}/FeatureServer");
        string? link = FormatLinkIn(html);

        Assert.True(link is not null, "A feature service page offers no WFS link.");

        XDocument capabilities = await XmlAsync(link!);

        Assert.Equal(XName.Get("WFS_Capabilities", Wfs), capabilities.Root!.Name);
    }

    [Fact]
    public async Task Every_layer_page_links_a_type_that_wfs_actually_publishes()
    {
        // <b>Agreement between two surfaces, which is what a link is.</b> The WFS
        // type name is the layer's own name and the ArcGIS layer id is a number;
        // building one from the other is a guess, and this fails the moment the
        // guess is wrong for one layer out of nineteen services.
        IReadOnlyList<string> published = await PublishedTypesAsync();
        Assert.NotEmpty(published);

        List<string> walked = [];

        foreach (string service in await EveryServiceNameAsync())
        {
            JsonElement document;

            try
            {
                document = await GetJsonAsync($"/rest/services/{service}/FeatureServer");
            }
            catch (Exception e) when (e is HttpRequestException or JsonException)
            {
                // Not a feature service, or its feature face is off. Either way
                // there is no page here to carry a link.
                continue;
            }

            if (!document.TryGetProperty("layers", out JsonElement layers))
            {
                continue;
            }

            foreach (JsonElement layer in layers.EnumerateArray())
            {
                int id = layer.GetProperty("id").GetInt32();
                string path = $"/rest/services/{service}/FeatureServer/{id}";
                string html = await GetHtmlAsync(path);
                string? link = FormatLinkIn(html);

                bool isGroup =
                    layer.TryGetProperty("type", out JsonElement kind)
                    && string.Equals(kind.GetString(), "Group Layer", StringComparison.Ordinal);

                if (isGroup)
                {
                    // <b>A group layer has no WFS type and must not claim one.</b>
                    // It holds other layers; there is nothing to describe.
                    Assert.True(link is null, $"{path} is a group layer and offers a WFS link.");
                    continue;
                }

                Assert.True(link is not null, $"{path} offers no WFS link.");

                XDocument schema = await XmlAsync(link!);

                Assert.Equal(XName.Get("schema", Xsd), schema.Root!.Name);

                Assert.True(
                    schema.Descendants(XName.Get("element", Xsd)).Any(),
                    $"{link} described nothing.");

                string type = Uri.UnescapeDataString(
                    link!.Split("typeNames=", StringSplitOptions.None)[^1]);

                Assert.Contains(type, published);
                walked.Add(type);
            }
        }

        // A test that walked nothing passes silently, which is the failure mode
        // this whole suite exists to avoid.
        Assert.NotEmpty(walked);
    }

    // ---------- the two surfaces agree ----------

    [Fact]
    public async Task A_service_whose_feature_face_is_off_is_absent_from_wfs()
    {
        // <b>Found by building the link, not by looking for it.</b> An operator who
        // sets servesFeatures false gets a 404 at the ArcGIS door (ADR-031
        // condition 2) and, until 2026-08-20, a full read at the WFS one. A second
        // protocol quietly reopening a door somebody closed is the failure a new
        // surface is most likely to introduce, so it is asserted rather than
        // remembered. D-123.
        string root = await RequireServerAsync();
        string? service = await AnyServiceNameAsync();
        Assert.NotNull(service);

        string[] parts = service!.Split('/');
        string? folder = parts.Length > 1 ? parts[0] : null;
        string bare = parts[^1];

        JsonElement layers = Require(
            await GetJsonAsync($"/rest/services/{service}/FeatureServer"),
            "layers",
            "A feature service lists no layers.");

        string name = layers.EnumerateArray().First().GetProperty("name").GetString()!;

        Assert.Contains($"graticula:{name}", await PublishedTypesAsync());

        // <b>Read first, and put back exactly what was there.</b> The first version
        // restored by writing nulls, which is not "unchanged" — it is *unconfigured*,
        // and it wiped an explicit empty capability ceiling on the service this
        // happens to pick. A console test failed hours later with checkboxes that had
        // turned themselves on. [D-75](../../docs/architecture-debt.md) exactly: a
        // test that mutates shared state fails somebody else, and restoring a value
        // it never read is not restoring.
        string before = await CapabilitiesAsync(root, bare, folder);

        await SetFeatureFaceAsync(root, bare, folder, serves: false);

        try
        {
            Assert.DoesNotContain($"graticula:{name}", await PublishedTypesAsync());

            // And the ArcGIS door agrees, which is the behaviour being matched
            // rather than a second thing being asserted.
            Assert.Equal(404, await StatusOfAsync($"/rest/services/{service}/FeatureServer"));
        }
        finally
        {
            await RestoreAsync(root, bare, before);
        }

        Assert.Contains($"graticula:{name}", await PublishedTypesAsync());
    }

    /// <summary>A service's capability document, as it stands now.</summary>
    private async Task<string> CapabilitiesAsync(string root, string name, string? folder)
    {
        string path = $"/admin/services/{Uri.EscapeDataString(name)}/capabilities"
            + (folder is { Length: > 0 } ? $"?folder={Uri.EscapeDataString(folder)}" : string.Empty);

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.IsSuccessStatusCode,
            $"Could not read {name}'s capabilities: {(int)response.StatusCode}");

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Puts a service's capabilities back exactly as they were read.
    /// </summary>
    /// <remarks>
    /// <b>The read document and the write document are the same shape on purpose</b>
    /// — the admin surface says so in its own remarks — so restoring is echoing what
    /// was read, with the two members the write shape does not carry removed.
    /// </remarks>
    private async Task RestoreAsync(string root, string name, string document)
    {
        System.Text.Json.JsonElement read =
            System.Text.Json.JsonDocument.Parse(document).RootElement;

        Dictionary<string, object?> body = new(StringComparer.Ordinal);

        foreach (System.Text.Json.JsonProperty property in read.EnumerateObject())
        {
            if (property.Name is "name" or "configured" or "note"
                or "serverRequestDeadlineSeconds")
            {
                continue;
            }

            body[property.Name] = property.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Null => null,
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Number => property.Value.GetInt64(),
                System.Text.Json.JsonValueKind.Array =>
                    property.Value.EnumerateArray().Select(v => v.GetString()).ToArray(),
                _ => property.Value.GetString(),
            };
        }

        // The write shape spells this one differently from the read shape.
        if (body.Remove("statementTimeoutMs", out object? timeout))
        {
            body["statementTimeoutMilliseconds"] = timeout;
        }

        using HttpRequestMessage request =
            new(HttpMethod.Put, new Uri($"{root}/admin/services/{Uri.EscapeDataString(name)}/capabilities"))
            {
                Content = JsonContent.Create(body),
            };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.IsSuccessStatusCode,
            $"Could not restore {name}'s capabilities: {(int)response.StatusCode} "
            + await response.Content.ReadAsStringAsync());
    }

    private async Task SetFeatureFaceAsync(string root, string name, string? folder, bool? serves)
    {
        using HttpRequestMessage request =
            new(HttpMethod.Put, new Uri($"{root}/admin/services/{name}/capabilities"))
            {
                Content = JsonContent.Create(new
                {
                    folder,
                    servesFeatures = serves,
                    servesTiles = (bool?)null,
                    capabilities = (string[]?)null,
                    statementTimeoutMilliseconds = (int?)null,
                }),
            };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.IsSuccessStatusCode,
            $"Could not set servesFeatures={serves} on {name}: "
            + $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    // ---------- the surface refuses what it should ----------

    [Fact]
    public async Task An_older_version_is_refused_rather_than_answered_approximately()
    {
        // <b>ADR-039 §5, and the two halves of it are not the same rule.</b> A
        // client that asks for 1.1.0 and receives a 2.0.0 document cannot tell it
        // apart from a server that is simply wrong — so every operation refuses a
        // `version` it does not speak.
        //
        // <b>GetCapabilities is the exception, and it is the exception on
        // purpose.</b> OWS Common negotiates that one operation through
        // `AcceptVersions`; `version` is not among its parameters, and a server
        // that refused it would be refusing to say what it speaks to a client
        // asking exactly that. This test asserted the opposite when it was written
        // and the server was right.
        XDocument capabilities = await XmlAsync(
            "/wfs?service=WFS&version=1.1.0&request=GetCapabilities");

        Assert.Equal(XName.Get("WFS_Capabilities", Wfs), capabilities.Root!.Name);

        Assert.Equal(
            "VersionNegotiationFailed",
            await RefusalOfAsync("/wfs?service=WFS&acceptversions=1.1.0&request=GetCapabilities"));

        Assert.Equal(
            "VersionNegotiationFailed",
            await RefusalOfAsync(
                "/wfs?service=WFS&version=1.1.0&request=DescribeFeatureType"
                + "&typeNames=graticula:no_such_layer"));
    }

    /// <summary>The exception code a request is refused with.</summary>
    /// <remarks>
    /// <b>The code, not the status.</b> An OWS exception report carries the reason
    /// in <c>exceptionCode</c>, and a test that asserted only "it did not work"
    /// would pass for a request refused for a completely different reason — which
    /// is how a version test ends up quietly asserting that a layer is missing.
    /// </remarks>
    private async Task<string?> RefusalOfAsync(string path)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);
        XDocument document = XDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(XName.Get("ExceptionReport", Ows), document.Root!.Name);

        return document
            .Descendants(XName.Get("Exception", Ows))
            .FirstOrDefault()
            ?.Attribute("exceptionCode")
            ?.Value;
    }

    [Fact]
    public async Task An_unknown_type_name_is_an_exception_report_and_not_an_empty_collection()
    {
        // An empty collection means no features match; this question has no answer
        // at all, and the two must not look alike to a client.
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri($"{root}/wfs?service=WFS&version=2.0.0&request=GetFeature"
                + "&typeNames=graticula:no_such_layer&count=1"));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);
        XDocument document = XDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(XName.Get("ExceptionReport", Ows), document.Root!.Name);
    }
}

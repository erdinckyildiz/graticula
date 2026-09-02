using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// One setting, four read faces, and the same answer on all of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-180](../../docs/architecture-debt.md), and
/// [ADR-049](../../docs/adr/ADR-049-a-face-refuses-in-its-own-vocabulary.md) is the decision
/// this asserts.</b> [ADR-031](../../docs/adr/ADR-031-service-capability-configuration.md) §2a
/// keeps `Query` revocable so an operator can stop a service answering without stopping it.
/// Measured on 2026-08-27 and again on 2026-09-02, two of the four read faces ignored it
/// entirely: ArcGIS and OGC API Features refused with 403 while WFS `GetFeature` returned a
/// `wfs:FeatureCollection` and WMS `GetMap` returned a picture.
/// </para>
/// <para>
/// <b>All four in one test, deliberately.</b> The debt exists because the repair was made on
/// the faces that had a word for it and not on the ones that did not, and four separate tests
/// would let that happen again one face at a time. What this pins is the property — *the
/// setting reaches every face that reads data* — rather than four independent behaviours.
/// </para>
/// <para>
/// <b>And the three doors that must stay open are asserted beside them.</b> §2a's state is
/// *running and refusing*, not absent: `GetCapabilities` on both faces still names the layer
/// and `DescribeFeatureType` still answers. A test that only checked the refusals would pass
/// just as well against a server that had made the service disappear, which is the other way
/// to get this wrong and the one ADR-031 condition 2 forbids.
/// </para>
/// <para>
/// <b>It mutates a real service and puts back what it read</b>, the pattern
/// <c>FaceOffLooksAbsentTests</c> arrived at after restoring nulls wiped an explicit empty
/// ceiling.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class CeilingReachesEveryReadFaceTests : ArcGisClient
{
    private static (string Bare, string? Folder) Service()
    {
        string? named = Environment.GetEnvironmentVariable(
            QueryCapabilityConformanceTests.LayerVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(named),
            $"{QueryCapabilityConformanceTests.LayerVariable} is not set, so this test FAILS "
            + "rather than skips. Name a queryable layer, e.g. hosted/ci_buildings.");

        string[] parts = named!.Trim('/').Split('/');

        return (parts[^1], parts.Length > 1 ? parts[0] : null);
    }

    private async Task<(HttpStatusCode Status, string Body, string? ContentType)> GetAsync(
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

    [Fact]
    public async Task Query_turned_off_is_refused_on_every_face_that_reads_data()
    {
        string root = await RequireServerAsync();
        (string bare, string? folder) = Service();

        string prefix = folder is { Length: > 0 } ? $"/rest/services/{folder}" : "/rest/services";

        const string Wms =
            "/wms?service=WMS&version=1.3.0&request=GetMap&layers={0}&styles=&crs=EPSG:4326"
            + "&bbox=39,32,40,33&width=64&height=64&format=image/png";

        const string Info =
            "/wms?service=WMS&version=1.3.0&request=GetFeatureInfo&layers={0}&query_layers={0}"
            + "&styles=&crs=EPSG:4326&bbox=39,32,40,33&width=64&height=64&format=image/png"
            + "&info_format=application/json&i=32&j=32";

        string wfs =
            $"/wfs?service=WFS&version=2.0.0&request=GetFeature&typenames={bare}&count=1";

        // <b>Before anything is changed: the name this test uses is a name these faces know.</b>
        // A typo would make every refusal below pass for the wrong reason.
        (HttpStatusCode listed, string capabilities, _) =
            await GetAsync("/wfs?service=WFS&version=2.0.0&request=GetCapabilities");

        Assert.Equal(HttpStatusCode.OK, listed);

        Assert.Contains(
            bare,
            capabilities,
            StringComparison.Ordinal);

        string before = await CapabilitiesAsync(root, bare, folder);

        try
        {
            await SetCeilingAsync(root, bare, folder, ["Create"]);

            // ------------------------------------------------ the two faces that always did
            (HttpStatusCode arcgis, string arcgisBody, _) = await GetAsync(
                $"{prefix}/{bare}/FeatureServer/0/query?where=1%3D1&outFields=*&f=json"
                + "&resultRecordCount=1");

            Assert.True(
                arcgis == HttpStatusCode.Forbidden,
                $"ArcGIS query answered {(int)arcgis} rather than 403: {arcgisBody}");

            (HttpStatusCode ogc, string ogcBody, _) = await GetAsync(
                $"/ogc/features/v1/collections/{Uri.EscapeDataString(bare)}/items?limit=1");

            Assert.True(
                ogc == HttpStatusCode.Forbidden,
                $"OGC items answered {(int)ogc} rather than 403: {ogcBody}");

            // ------------------------------------------------------- WFS, which did not
            (HttpStatusCode features, string featuresBody, _) = await GetAsync(wfs);

            Assert.True(
                features == HttpStatusCode.Forbidden,
                $"WFS GetFeature answered {(int)features} rather than 403. Until 2026-09-02 it "
                + $"answered 200 with rows: {Head(featuresBody)}");

            Assert.Contains("OperationNotSupported", featuresBody, StringComparison.Ordinal);
            Assert.Contains("is configured to offer", featuresBody, StringComparison.Ordinal);

            // ------------------------------------------------------- WMS, which did not
            (HttpStatusCode map, string mapBody, string? mapType) =
                await GetAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, Wms, bare));

            Assert.True(
                mapType is not null && mapType.Contains("xml", StringComparison.OrdinalIgnoreCase),
                $"WMS GetMap answered {mapType} rather than a ServiceExceptionReport. Until "
                + "2026-09-02 it drew the picture.");

            Assert.Contains("OperationNotSupported", mapBody, StringComparison.Ordinal);
            Assert.Contains("is configured to offer", mapBody, StringComparison.Ordinal);

            // <b>200, and that is ADR-049's decision rather than an oversight.</b> This face
            // returns exceptions as successful responses because several WMS clients treat a
            // 4xx as a transport failure and never read the body. If that rule is revised,
            // this assertion is the one that says the WMS half of ADR-049 goes with it.
            Assert.Equal(HttpStatusCode.OK, map);

            (HttpStatusCode info, string infoBody, _) =
                await GetAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, Info, bare));

            Assert.True(
                infoBody.Contains("LayerNotQueryable", StringComparison.Ordinal),
                $"WMS GetFeatureInfo answered {(int)info} without LayerNotQueryable, which is "
                + $"the code WMS 1.3.0 wrote for exactly this case: {Head(infoBody)}");

            // ------------------------------------ and the three doors that must stay open
            (HttpStatusCode stillListed, string stillCapabilities, _) =
                await GetAsync("/wfs?service=WFS&version=2.0.0&request=GetCapabilities");

            Assert.Equal(HttpStatusCode.OK, stillListed);

            Assert.True(
                stillCapabilities.Contains(bare, StringComparison.Ordinal),
                "WFS GetCapabilities stopped naming the layer when Query was turned off. "
                + "ADR-031 §2a's state is *running and refusing*, not absent -- and making it "
                + "vanish is how a refusal becomes indistinguishable from a service that was "
                + "never there.");

            (HttpStatusCode described, _, _) = await GetAsync(
                $"/wfs?service=WFS&version=2.0.0&request=DescribeFeatureType&typenames={bare}");

            Assert.True(
                described == HttpStatusCode.OK,
                $"DescribeFeatureType answered {(int)described}. Describing what a service "
                + "offers is not reading its features, and a client that cannot discover the "
                + "type cannot be told why it is refused.");

            (HttpStatusCode wmsListed, string wmsCapabilities, _) =
                await GetAsync("/wms?service=WMS&version=1.3.0&request=GetCapabilities");

            Assert.Equal(HttpStatusCode.OK, wmsListed);

            Assert.Contains(bare, wmsCapabilities, StringComparison.Ordinal);
        }
        finally
        {
            await RestoreAsync(root, bare, before);
        }

        // <b>And it reads again afterwards</b>, which is what proves the restore restored
        // rather than that the test happened to end.
        (HttpStatusCode back, string backBody, _) = await GetAsync(wfs);

        Assert.True(
            back == HttpStatusCode.OK,
            $"After restoring the ceiling, WFS GetFeature answered {(int)back}: {Head(backBody)}");
    }

    private static string Head(string body) =>
        body.Length <= 300 ? body : body[..300] + "…";

    private async Task SetCeilingAsync(
        string root, string name, string? folder, string[] capabilities)
    {
        using HttpRequestMessage request =
            new(HttpMethod.Put, new Uri($"{root}/admin/services/{Uri.EscapeDataString(name)}/capabilities"))
            {
                Content = JsonContent.Create(new
                {
                    folder,
                    servesFeatures = (bool?)null,
                    servesTiles = (bool?)null,
                    capabilities,
                }),
            };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.IsSuccessStatusCode,
            $"Could not set {name}'s ceiling: {(int)response.StatusCode} "
            + await response.Content.ReadAsStringAsync());
    }

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

    /// <summary>Puts a service's capabilities back exactly as they were read.</summary>
    private async Task RestoreAsync(string root, string name, string document)
    {
        JsonElement read = JsonDocument.Parse(document).RootElement;

        Dictionary<string, object?> body = new(StringComparer.Ordinal);

        foreach (JsonProperty property in read.EnumerateObject())
        {
            if (property.Name is "name" or "configured" or "note" or "serverRequestDeadlineSeconds"
                or "kind" or "sharing")
            {
                continue;
            }

            body[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => property.Value.GetInt64(),
                JsonValueKind.Array => property.Value.EnumerateArray().Select(v => v.GetString()).ToArray(),
                _ => property.Value.GetString(),
            };
        }

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
}

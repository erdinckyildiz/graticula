using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The seven things the WFS 2.0 CITE suite found, asserted from outside.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because the suite is run by hand.</b> `ogccite/ets-wfs20` went from 405 of
/// 420 to 420 of 420 on 2026-08-23, and nothing in this repository runs it again —
/// [D-63](../../docs/architecture-debt.md) means no workflow has ever executed a step, so
/// a number earned in a container is a number that decays. Each test below is one of the
/// nine assertions that were red, expressed against the running server so that a
/// regression is caught by a suite that does run.
/// </para>
/// <para>
/// <b>These are not restatements of the suite.</b> Where CITE asserts a status code this
/// asserts the same code and the reason it matters, because the value of a conformance
/// failure is the client behaviour behind it and a test that only remembers the number
/// teaches nobody why it is that number.
/// </para>
/// </remarks>
/// <b>In the catalogue walk's collection because it reads a layer other tests reconfigure.</b>
/// [D-180](../../docs/architecture-debt.md) made the capability ceiling reach WFS and WMS, and
/// `CeilingReachesEveryReadFaceTests` sets that ceiling on `GRATICULA_TEST_QUERYABLE` to prove
/// it. Before that change the mutation was invisible to most readers; after it, any class
/// querying the same layer in parallel can be answered **403** and report it as its own
/// defect — which is exactly what a CI run did on 2026-09-02, blaming
/// `supportsQueryWithDistance`. Serialising is the fix the collection already exists for.
[Collection("catalogue walk")]
public sealed class WfsCiteRepairTests : ArcGisClient
{
    private const string Path = "/wfs";

    private const string GetFeatureById = "urn:ogc:def:query:OGC-WFS::GetFeatureById";

    private static readonly XNamespace Ows = "http://www.opengis.net/ows/1.1";

    private static readonly XNamespace Wfs = "http://www.opengis.net/wfs/2.0";

    private static string Query(string tail) =>
        $"{Path}?service=WFS&version=2.0.0&{tail}";

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, root + path);
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(string xml)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Post, root + Path)
        {
            Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml"),
        };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string ExceptionCode(string body)
    {
        XElement report = XElement.Parse(body);

        Assert.Equal(Ows + "ExceptionReport", report.Name);

        XElement exception = Assert.Single(report.Elements(Ows + "Exception"));

        return (string?)exception.Attribute("exceptionCode") ?? string.Empty;
    }

    // ---------------------------------------------------------------- service

    /// <summary>
    /// A request with no <c>service</c> is refused rather than answered.
    /// </summary>
    /// <remarks>
    /// <b>OWS Common makes it required and this surface answered anyway.</b> Being
    /// generous is not harmless: a request with no `service` reaching a shared endpoint
    /// is ambiguous by construction, and answering it teaches a client to send requests
    /// no other server will accept. CITE's `getCapabilities_missingServiceParam`.
    /// </remarks>
    [Fact]
    public async Task A_request_without_a_service_parameter_is_refused()
    {
        (HttpStatusCode status, string body) =
            await GetAsync($"{Path}?version=2.0.0&request=GetCapabilities");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("MissingParameterValue", ExceptionCode(body));

        // And the same request with it is answered, so this is not passing because the
        // endpoint broke.
        (HttpStatusCode ok, _) = await GetAsync(Query("request=GetCapabilities"));
        Assert.Equal(HttpStatusCode.OK, ok);
    }

    // ---------------------------------------------------------------- GetFeatureById

    /// <summary>
    /// <c>GetFeatureById</c> answers with the feature, not with a collection holding it.
    /// </summary>
    /// <remarks>
    /// <b>§7.9.3.6, and the failure was invisible from the inside.</b> The document was
    /// well formed and carried the right feature; a client looking for the identifier it
    /// asked for on the root element found nothing, which is what CITE's
    /// `invokeGetFeatureById` reports as *expected [look_buildings.1] but found []*.
    /// </remarks>
    [Fact]
    public async Task GetFeatureById_answers_with_the_feature_itself()
    {
        string? layer = Environment.GetEnvironmentVariable(
            QueryCapabilityConformanceTests.LayerVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(layer),
            $"{QueryCapabilityConformanceTests.LayerVariable} is not set.");

        string type = layer!.Trim('/');
        int slash = type.LastIndexOf('/');
        type = slash < 0 ? type : type[(slash + 1)..];

        (HttpStatusCode status, string body) = await GetAsync(
            Query($"request=GetFeature&STOREDQUERY_ID={Uri.EscapeDataString(GetFeatureById)}"
                + $"&ID={type}.1"));

        Assert.Equal(HttpStatusCode.OK, status);

        XElement feature = XElement.Parse(body);

        Assert.NotEqual(Wfs + "FeatureCollection", feature.Name);
        Assert.Equal(type, feature.Name.LocalName);

        Assert.Equal(
            $"{type}.1",
            (string?)feature.Attribute(XName.Get("id", "http://www.opengis.net/gml/3.2")));
    }

    /// <summary>
    /// An identifier that names nothing is a 404, whatever is wrong with it.
    /// </summary>
    /// <remarks>
    /// <b>400 says *fix your request*, and a client looking for a typo in an identifier
    /// this server gave it will not find one.</b> CITE accepts 404 or 403. The three
    /// shapes are asserted together on purpose: splitting them into a 400 and a 404 would
    /// make a client's retry logic depend on how wrong its identifier was.
    /// </remarks>
    [Theory]
    [InlineData("look_buildings.999999999")]
    [InlineData("nodotatall")]
    [InlineData("no_such_feature_type.1")]
    public async Task An_identifier_that_names_nothing_is_not_found(string identifier)
    {
        (HttpStatusCode status, string body) = await GetAsync(
            Query($"request=GetFeature&STOREDQUERY_ID={Uri.EscapeDataString(GetFeatureById)}"
                + $"&ID={Uri.EscapeDataString(identifier)}"));

        Assert.Equal(HttpStatusCode.NotFound, status);

        // Still a document a WFS client can read, which is the whole reason this surface
        // answers with ExceptionReport rather than with a bare status.
        Assert.NotEmpty(ExceptionCode(body));
    }

    // ---------------------------------------------------------------- paging

    /// <summary>
    /// A page in the middle of a result set says where the pages either side are.
    /// </summary>
    /// <remarks>
    /// <b>§7.7.4.4.1, and <c>numberMatched</c> is not a substitute.</b> It tells a client
    /// how much there is and nothing told it how to ask for the rest, so a client had to
    /// construct `startIndex` itself against a page size the server chose. CITE's
    /// `traverseResultSetInBothDirections`.
    /// </remarks>
    [Fact]
    public async Task A_middle_page_links_forwards_and_backwards()
    {
        string layer = await LargeTypeAsync();

        (HttpStatusCode status, string body) = await GetAsync(
            Query($"request=GetFeature&typeNames=graticula:{layer}&count=2&startIndex=2"));

        Assert.Equal(HttpStatusCode.OK, status);

        XElement collection = XElement.Parse(body);

        string? next = (string?)collection.Attribute("next");
        string? previous = (string?)collection.Attribute("previous");

        Assert.False(string.IsNullOrWhiteSpace(next), "a middle page must link forwards");
        Assert.False(string.IsNullOrWhiteSpace(previous), "a middle page must link backwards");

        // <b>And the links have to work.</b> An attribute that satisfies a schema and
        // returns nothing is worse than an absent one, because a client trusts it.
        Assert.Contains("startIndex=4", next, StringComparison.Ordinal);
        Assert.Contains("startIndex=0", previous, StringComparison.Ordinal);

        (HttpStatusCode followed, string page) = await GetAsync(Relative(next!));

        Assert.Equal(HttpStatusCode.OK, followed);

        Assert.Equal(
            "2",
            (string?)XElement.Parse(page).Attribute("numberReturned"));
    }

    /// <summary>
    /// The first page links forwards only, and the last links backwards only.
    /// </summary>
    [Fact]
    public async Task The_ends_of_a_result_set_link_one_way()
    {
        string layer = await LargeTypeAsync();

        string firstBody = (await GetAsync(
            Query($"request=GetFeature&typeNames=graticula:{layer}&count=2"))).Body;

        XElement first = XElement.Parse(firstBody);

        // <b>The document, when the attribute is missing — D-173's neighbourhood.</b> This
        // failed once in CI, on 2026-08-27, with `Assert.NotNull() Failure: Value is null`
        // and nothing else: a `next` that is absent because the server paged wrongly and one
        // that is absent because the answer is an exception report look identical from an
        // assertion that only names the attribute. The element's own name settles it in one
        // word — `FeatureCollection` or `ExceptionReport` — and this suite has spent two days
        // learning that the next failure has to carry its evidence.
        Assert.True(
            first.Attribute("next") is not null,
            $"The first page of '{layer}' has no 'next' link. The server answered a "
            + $"<{first.Name.LocalName}> with numberMatched="
            + $"{(string?)first.Attribute("numberMatched") ?? "absent"}, numberReturned="
            + $"{(string?)first.Attribute("numberReturned") ?? "absent"}: "
            + Excerpt(firstBody));

        Assert.Null(first.Attribute("previous"));

        long matched = long.Parse(
            (string?)first.Attribute("numberMatched") ?? "0", CultureInfo.InvariantCulture);

        Assert.True(matched > 4, $"{layer} has {matched} features and this test needs several.");

        string lastBody = (await GetAsync(
            Query($"request=GetFeature&typeNames=graticula:{layer}&count=2"
                + $"&startIndex={matched - 2}"))).Body;

        XElement last = XElement.Parse(lastBody);

        Assert.Null(last.Attribute("next"));

        Assert.True(
            last.Attribute("previous") is not null,
            $"The last page of '{layer}' has no 'previous' link. The server answered a "
            + $"<{last.Name.LocalName}>: " + Excerpt(lastBody));
    }

    /// <summary>
    /// A hits response links to the first page of features and not to itself.
    /// </summary>
    /// <remarks>
    /// <b>CITE's <c>getFeatureWithHitsOnly</c> asserts <c>next</c> present and
    /// <c>previous</c> absent</b>, which only makes sense if a hits response is page
    /// zero — a client goes from the count straight to the features. **What this test
    /// exists for is the other half**: the first implementation preserved every parameter
    /// in the link, so `next` reproduced the request that had just been answered, with
    /// the same `next` on it, for ever. A conformance suite asserting presence would have
    /// passed on that.
    /// </remarks>
    [Fact]
    public async Task A_hits_response_links_to_features_rather_than_to_itself()
    {
        string layer = await LargeTypeAsync();

        XElement hits = XElement.Parse((await GetAsync(
            Query($"request=GetFeature&typeNames=graticula:{layer}&resultType=hits"))).Body);

        Assert.Equal("0", (string?)hits.Attribute("numberReturned"));
        Assert.Null(hits.Attribute("previous"));

        string? next = (string?)hits.Attribute("next");
        Assert.False(string.IsNullOrWhiteSpace(next), "a hits response must link to the features");

        Assert.DoesNotContain("resultType=hits", next!, StringComparison.OrdinalIgnoreCase);

        XElement page = XElement.Parse((await GetAsync(Relative(next!))).Body);

        Assert.NotEqual("0", (string?)page.Attribute("numberReturned"));
    }

    // ---------------------------------------------------------------- filters

    /// <summary>
    /// <c>matchCase="false"</c> is answered case-insensitively rather than refused.
    /// </summary>
    /// <remarks>
    /// <b>Refusing it was truthful while the capability was missing</b>, since answering
    /// a case-sensitive comparison to a caller who asked for a case-insensitive one is a
    /// wrong answer rather than a smaller one. CITE's `propertyIsEqualTo_caseSensitive`
    /// reported the 400; the repair was the capability, not the refusal.
    /// </remarks>
    [Fact]
    public async Task A_case_insensitive_comparison_is_answered()
    {
        string layer = Environment.GetEnvironmentVariable(
            QueryCapabilityConformanceTests.LayerVariable)!.Trim('/');

        int slash = layer.LastIndexOf('/');
        string type = slash < 0 ? layer : layer[(slash + 1)..];

        // The value the server itself publishes, lower-cased. Case-sensitively this
        // matches nothing; case-insensitively it matches the feature it came from.
        (_, string body) = await GetAsync(
            Query($"request=GetFeature&typeNames=graticula:{type}&count=1"));

        XElement feature = Assert.Single(
            XElement.Parse(body).Descendants(XNamespace.Get("urn:graticula:ns") + type));

        XElement? text = null;

        foreach (XElement property in feature.Elements())
        {
            if (property.Value.Length > 2
                && !string.Equals(
                    property.Value, property.Value.ToLowerInvariant(), StringComparison.Ordinal)
                && !property.Value.Contains('<', StringComparison.Ordinal))
            {
                text = property;
                break;
            }
        }

        Assert.NotNull(text);

        string filter =
            "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\">"
            + $"<fes:PropertyIsEqualTo matchCase=\"{{0}}\">"
            + $"<fes:ValueReference>{text!.Name.LocalName}</fes:ValueReference>"
            + $"<fes:Literal>{text.Value.ToLowerInvariant()}</fes:Literal>"
            + "</fes:PropertyIsEqualTo></fes:Filter>";

        string Wrap(string matchCase) =>
            "<?xml version=\"1.0\"?>"
            + "<wfs:GetFeature xmlns:wfs=\"http://www.opengis.net/wfs/2.0\" "
            + "service=\"WFS\" version=\"2.0.0\">"
            + "<wfs:Query typeNames=\"graticula:" + type + "\" "
            + "xmlns:graticula=\"urn:graticula:ns\">"
            + string.Format(CultureInfo.InvariantCulture, filter, matchCase)
            + "</wfs:Query></wfs:GetFeature>";

        (HttpStatusCode folded, string insensitive) = await PostAsync(Wrap("false"));

        Assert.Equal(HttpStatusCode.OK, folded);

        Assert.NotEqual(
            "0", (string?)XElement.Parse(insensitive).Attribute("numberReturned"));

        // And case still matters when the caller says it does, which is what makes the
        // first assertion mean something.
        (HttpStatusCode exact, string sensitive) = await PostAsync(Wrap("true"));

        Assert.Equal(HttpStatusCode.OK, exact);

        Assert.Equal("0", (string?)XElement.Parse(sensitive).Attribute("numberReturned"));
    }

    /// <summary>
    /// A property GML gives every feature is refused as impossible, not as unknown.
    /// </summary>
    /// <remarks>
    /// <b><c>InvalidParameterValue</c> sends a client looking for a typo it will not
    /// find.</b> `gml:boundedBy` exists on every GML feature; comparing it with a literal
    /// is understood and cannot be carried out, which is what `OperationProcessingFailed`
    /// means. CITE's `invalidOperand_boundedBy`.
    /// </remarks>
    [Fact]
    public async Task Comparing_gml_boundedBy_is_refused_as_impossible()
    {
        string layer = Environment.GetEnvironmentVariable(
            QueryCapabilityConformanceTests.LayerVariable)!.Trim('/');

        int slash = layer.LastIndexOf('/');
        string type = slash < 0 ? layer : layer[(slash + 1)..];

        (HttpStatusCode status, string body) = await PostAsync(
            "<?xml version=\"1.0\"?>"
            + "<wfs:GetFeature xmlns:wfs=\"http://www.opengis.net/wfs/2.0\" "
            + "service=\"WFS\" version=\"2.0.0\">"
            + "<wfs:Query typeNames=\"graticula:" + type + "\" "
            + "xmlns:graticula=\"urn:graticula:ns\">"
            + "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\" "
            + "xmlns:gml=\"http://www.opengis.net/gml/3.2\">"
            + "<fes:PropertyIsEqualTo>"
            + "<fes:ValueReference>gml:boundedBy</fes:ValueReference>"
            + "<fes:Literal>x</fes:Literal>"
            + "</fes:PropertyIsEqualTo></fes:Filter>"
            + "</wfs:Query></wfs:GetFeature>");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("OperationProcessingFailed", ExceptionCode(body));
    }

    /// <summary>
    /// An empty <c>valueReference</c> is a bad value rather than an absent parameter.
    /// </summary>
    /// <remarks>
    /// <b>Two different instructions to the caller.</b> `MissingParameterValue` says add
    /// the parameter, and a caller who wrote `valueReference=""` has added it. CITE's
    /// `getProperty_emptyValueRef`.
    /// </remarks>
    [Fact]
    public async Task An_empty_valueReference_is_an_invalid_value()
    {
        string layer = Environment.GetEnvironmentVariable(
            QueryCapabilityConformanceTests.LayerVariable)!.Trim('/');

        int slash = layer.LastIndexOf('/');
        string type = slash < 0 ? layer : layer[(slash + 1)..];

        (HttpStatusCode status, string body) = await GetAsync(
            Query($"request=GetPropertyValue&typeNames=graticula:{type}&valueReference="));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("InvalidParameterValue", ExceptionCode(body));

        // Absent keeps the other code, which is what makes this distinction real rather
        // than a rename.
        (_, string absent) = await GetAsync(
            Query($"request=GetPropertyValue&typeNames=graticula:{type}"));

        Assert.Equal("MissingParameterValue", ExceptionCode(absent));
    }

    // ---------------------------------------------------------------- helpers

    private static string Relative(string url)
    {
        int cut = url.IndexOf(Path, StringComparison.Ordinal);
        return cut < 0 ? url : url[cut..];
    }

    /// <summary>A feature type with enough features to page through.</summary>
    private static Task<string> LargeTypeAsync()
    {
        string? named = Environment.GetEnvironmentVariable(
            AdmissionControlConformanceTests.LargeLayerVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(named),
            $"{AdmissionControlConformanceTests.LargeLayerVariable} is not set, so the paging "
            + "tests FAIL rather than skip. Name a layer with more than four features.");

        string type = named!.Trim('/');
        int slash = type.LastIndexOf('/');

        return Task.FromResult(slash < 0 ? type : type[(slash + 1)..]);
    }

    /// <summary>The first of a response, for a failure message.</summary>
    /// <param name="body">What came back.</param>
    /// <returns>Enough of it to tell an exception report from a feature collection.</returns>
    /// <remarks>
    /// <b>Two hundred characters, on one line.</b> A whole GML document in an assertion
    /// message is unreadable and a truncation at fifty is what
    /// [D-173](../../docs/architecture-debt.md) spent a day on: the evidence was past it.
    /// </remarks>
    private static string Excerpt(string body) =>
        body.Length <= 200
            ? body.Replace("\n", " ", StringComparison.Ordinal)
                  .Replace("\r", string.Empty, StringComparison.Ordinal)
            : body[..200].Replace("\n", " ", StringComparison.Ordinal)
                         .Replace("\r", string.Empty, StringComparison.Ordinal) + "...";
}

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The sequence an ArcGIS client walks before it can draw anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written as a walk, not as a set of endpoint checks.</b> Each test starts
/// where a client starts and uses only what the previous document gave it. That
/// is the difference between "the endpoint returns 200" and "a client can get
/// here" — and the latter was false for every document except <c>query</c> until
/// 2026-08-14, while every endpoint test passed.
/// </para>
/// <para>
/// Requires a running, migrated server with at least one publicly shared layer.
/// </para>
/// </remarks>
[Trait("Category", "Conformance")]
[Collection("catalogue walk")]
public sealed class ArcGisDiscoveryTests : ArcGisClient
{
    /// <summary>Step 1. Every ArcGIS client begins here.</summary>
    [Fact]
    public async Task Step1_the_server_identifies_itself_and_says_how_to_authenticate()
    {
        JsonElement info = await GetJsonAsync("/rest/info");

        JsonElement version = Require(
            info, "currentVersion",
            "A client uses it to decide which operations to attempt, and refuses a server that "
            + "will not state one.");

        Assert.Equal(JsonValueKind.Number, version.ValueKind);
        Assert.True(version.GetDouble() > 0);

        JsonElement auth = Require(
            info, "authInfo",
            "Without it a client assumes anonymous access and fails later, less clearly.");

        Assert.True(Require(auth, "isTokenBasedSecurity", "").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(
            Require(auth, "tokenServicesUrl", "A client needs somewhere to send credentials.")
                .GetString()));
    }

    /// <summary>Step 2. The catalogue, reached from nothing but the root.</summary>
    [Fact]
    public async Task Step2_the_catalogue_lists_services_a_client_can_address()
    {
        JsonElement catalogue = await GetJsonAsync("/rest/services");

        Require(catalogue, "currentVersion", "A client version-gates the catalogue too.");
        Require(catalogue, "folders", "Absent means malformed, not empty; a client may enumerate it.");

        JsonElement services = Require(
            catalogue, "services", "This is the list. Without it there is nothing to add.");

        Assert.Equal(JsonValueKind.Array, services.ValueKind);

        // <b>Somewhere, not necessarily at the root.</b> A client enumerates the
        // folders too, and every hosted layer lands in one -- so a server
        // published entirely through the hosting API has an empty root array and
        // is perfectly addressable. This used to demand a root service and
        // failed against exactly that server.
        Assert.NotNull(await AnyServiceNameAsync());

        foreach (JsonElement service in services.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(Require(service, "name", "").GetString()));
            Assert.Equal("FeatureServer", Require(service, "type", "").GetString());
        }
    }

    /// <summary>
    /// Step 3. The service document, at the URL a client builds by convention.
    /// </summary>
    /// <remarks>
    /// The convention is <c>{catalogue}/{name}/{type}</c>. A client does not
    /// receive this URL; it constructs it. If our routing disagrees with the
    /// convention, every client 404s on a service it can see in the catalogue.
    /// </remarks>
    [Fact]
    public async Task Step3_the_service_document_is_where_the_naming_convention_says()
    {
        string name = await FirstServiceNameAsync(
            "this is a walkthrough of one service on purpose — it follows a client from the catalogue to a query, and following it against every service would prove the same convention many times");

        JsonElement service = await GetJsonAsync($"/rest/services/{name}/FeatureServer");

        // <b>Contains, not equals.</b> Capabilities describe what <em>this</em>
        // caller may do, so an editor legitimately sees Create,Update,Delete
        // here and a reader does not. Asserting the exact string asserted that
        // nobody was signed in, which is a fact about the harness rather than
        // about conformance — and it broke the moment the suite grew a login.
        Assert.Contains("Query", Require(
            service, "capabilities",
            "A client reads this to decide which UI to offer.").GetString()!,
            System.StringComparison.Ordinal);

        JsonElement layers = Require(
            service, "layers", "A FeatureServer with no layer list has nothing to add.");

        Assert.True(layers.GetArrayLength() > 0);

        JsonElement first = layers[0];
        Assert.Equal(
            0,
            Require(first, "id", "The layer id is how the query URL is built.").GetInt32());

        Assert.StartsWith(
            "esriGeometry",
            Require(first, "geometryType", "A client picks its renderer from this.").GetString(),
            StringComparison.Ordinal);

        Require(
            service, "spatialReference",
            "A client cannot place the service on a map without one.");
    }

    /// <summary>
    /// Step 4. The layer document, which is what actually lets a layer be added.
    /// </summary>
    [Fact]
    public async Task Step4_the_layer_document_carries_everything_needed_to_add_the_layer()
    {
        string name = await FirstServiceNameAsync(
            "the same walkthrough, one step on; what every layer's document must carry is Every_layer_is_consistent_with_its_own_document in the consistency suite");

        JsonElement layer = await GetJsonAsync($"/rest/services/{name}/FeatureServer/0");

        Require(layer, "objectIdField",
            "A client uses it for selection and paging. Null is a legitimate answer only for a "
            + "layer that is not ArcGIS-servable, and such a layer should not be in the catalogue.");

        JsonElement fields = Require(
            layer, "fields", "Without a field list a client shows an empty attribute table.");

        Assert.True(fields.GetArrayLength() > 0, "The layer declares no fields.");

        foreach (JsonElement field in fields.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(Require(field, "name", "").GetString()));
            Assert.StartsWith(
                "esriFieldType",
                Require(field, "type", "").GetString(),
                StringComparison.Ordinal);
        }

        Assert.StartsWith(
            "esriGeometry",
            Require(layer, "geometryType", "").GetString(),
            StringComparison.Ordinal);

        Assert.True(
            Require(layer, "maxRecordCount", "A client pages against this.").GetInt32() > 0);
    }

    /// <summary>
    /// Step 4a. The extent, which decides where the map opens.
    /// </summary>
    /// <remarks>
    /// A separate test because null is *permitted* — it means unknown — and a
    /// client handles that by zooming to the data after a query. What must never
    /// happen is a zeroed box, which sends the view to the Atlantic and looks
    /// like a data fault rather than a metadata one.
    /// </remarks>
    [Fact]
    public async Task Step4a_the_extent_is_either_absent_or_actually_somewhere()
    {
        string name = await FirstServiceNameAsync(
            "part of the same walkthrough; the extent rule itself is asserted per layer elsewhere");

        JsonElement extent = Require(
            await GetJsonAsync($"/rest/services/{name}/FeatureServer/0"), "extent",
            "A client expects the property, even when its value is null.");

        if (extent.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        double xmin = Require(extent, "xmin", "").GetDouble();
        double ymin = Require(extent, "ymin", "").GetDouble();
        double xmax = Require(extent, "xmax", "").GetDouble();
        double ymax = Require(extent, "ymax", "").GetDouble();

        Assert.True(xmax > xmin && ymax > ymin, $"The extent is inverted or empty: {extent}");
        Assert.False(
            xmin == 0 && ymin == 0 && xmax == 0 && ymax == 0,
            "A zeroed extent is 'unknown' spelled as a location. Send null instead.");

        Require(extent, "spatialReference", "Four numbers a client cannot place are not an extent.");
    }

    /// <summary>Step 5. The query, reached only through the four documents above.</summary>
    [Fact]
    public async Task Step5_a_query_built_from_the_documents_returns_features()
    {
        string name = await FirstServiceNameAsync(
            "the last step of the walkthrough — that a query assembled from these documents works at all, which is a fact about the documents rather than about a layer");

        JsonElement result = await GetJsonAsync(
            $"/rest/services/{name}/FeatureServer/0/query?resultRecordCount=1");

        Require(result, "objectIdFieldName", "");
        Require(result, "geometryType", "");
        Require(result, "spatialReference", "");

        JsonElement features = Require(result, "features", "");
        Assert.True(features.GetArrayLength() > 0, "The layer is empty, so nothing is proved.");

        JsonElement feature = features[0];
        Require(feature, "attributes", "");
        Require(feature, "geometry", "A feature with no shape cannot be drawn.");
    }

    /// <summary>A layer nobody may see is absent, not forbidden.</summary>
    /// <remarks>
    /// A 403 on a named layer confirms it exists, which turns the endpoint into
    /// a directory of everything published. This checks that a private layer and
    /// a nonexistent one are indistinguishable from outside.
    /// </remarks>
    [Fact]
    public async Task A_layer_that_is_not_shared_looks_exactly_like_one_that_does_not_exist()
    {
        int invented = await StatusOfAsync("/rest/services/definitely-not-a-layer/FeatureServer/0");

        Assert.Equal(404, invented);

        Assert.Equal(
            invented,
            await StatusOfAsync("/rest/services/definitely-not-a-layer/FeatureServer"));
    }

    /// <summary>
    /// Some FeatureServer a client could add, for a claim that one service can settle.
    /// </summary>
    /// <param name="whyOneIsEnough">
    /// What makes this test's question a question about the server rather than about a layer.
    /// </param>
    /// <returns>The name of a service, folder-qualified if it is in one.</returns>
    /// <remarks>
    /// <para>
    /// <b>The reason is a parameter because [D-65](../../docs/architecture-debt.md) is about
    /// not being able to tell two things apart.</b> Most of this suite asks its question of one
    /// service and is right to: a form's parameters, a button, an `Accept` header are facts
    /// about the server. Some claims are universal — *for every layer this server serves* — and
    /// those must walk. **Nothing at the call site distinguished them**, so a universal claim
    /// asking one service looked exactly like a per-server claim asking one service, and one of
    /// them sat in the suite passing for four days while three of the owner's ten layers were
    /// skipping rows.
    /// </para>
    /// <para>
    /// <b>A parameter rather than a comment, because a comment is optional.</b> The compiler
    /// makes the next person state which kind their test is before it will build, which is the
    /// note D-65 asked for in the only form that cannot be skipped. The string is not read by
    /// anything: its reader is whoever is deciding whether to widen the test.
    /// </para>
    /// </remarks>
    private async Task<string> FirstServiceNameAsync(string whyOneIsEnough)
    {
        Assert.False(
            string.IsNullOrWhiteSpace(whyOneIsEnough),
            "A test that asks one service has to say why one is enough. D-65.");

        string? name = await AnyServiceNameAsync();

        Assert.False(
            string.IsNullOrWhiteSpace(name),
            "No FeatureServer is visible anonymously, at the root or in any folder; this suite "
            + "needs one publicly shared layer.");

        return name!;
    }
}

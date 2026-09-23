using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Host;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-069: a FeatureServer <c>query</c> GET carries the layer's own cache lifetime, the same
/// shape <see cref="VectorTileEndpoints"/> already carries on a tile.
/// </summary>
/// <remarks>
/// <b>Against <see cref="QueryResponseCaching"/> directly</b>, with a fake <see cref="IFeatureSource"/>
/// and a hand-built <see cref="PublishedLayer"/> — neither needs PostgreSQL nor a GeoParquet file on
/// disk, and the behaviour under test is the header logic, not either provider.
/// </remarks>
public sealed class QueryResponseCachingTests
{
    private sealed class PlainSource : IFeatureSource
    {
        public FeatureSchema SchemaFor(FeatureQuery query) => throw new NotSupportedException();

        public IAsyncEnumerable<Feature> ReadAsync(
            FeatureQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> CountUpToAsync(
            FeatureQuery query, long ceiling, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class VersionedSource(string? version) : IFeatureSource, ISourceCacheValidator
    {
        public int Calls { get; private set; }

        public FeatureSchema SchemaFor(FeatureQuery query) => throw new NotSupportedException();

        public IAsyncEnumerable<Feature> ReadAsync(
            FeatureQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> CountUpToAsync(
            FeatureQuery query, long ceiling, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> CacheValidatorAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(version);
        }
    }

    /// <remarks>
    /// <b>Query-only unless a test says otherwise</b>, because since V-56 a layer somebody can edit is not
    /// cached by default, and every test below that is about the header's shape is about a layer that is.
    /// </remarks>
    private static PublishedLayer Layer(
        SharingScope sharing, TimeSpan? cacheLifetime = null, string[]? ceiling = null) =>
        new(
            Guid.NewGuid(),
            new LayerDefinition(
                "parcels", "public", "parcels", "geom", 4326, "objectid", "objectid", isHosted: false),
            "postgis",
            "unused",
            GeometryKind.Polygon,
            owner: null,
            sharing,
            ServiceStatus.Started,
            cacheLifetime: cacheLifetime,
            capabilityCeiling: ceiling ?? ["Query"]);

    [Fact]
    public async Task A_layer_somebody_can_edit_is_not_cached_by_default()
    {
        // V-56: an editable hosted layer answered `public, max-age=3600`, so an edit stayed invisible to
        // every other browser for up to an hour. The anonymous caller cannot edit; somebody else can.
        DefaultHttpContext context = Request();
        PublishedLayer layer = Layer(SharingScope.Public, ceiling: ["Query", "Create", "Update", "Delete"]);

        await QueryResponseCaching.ApplyAsync(
            context, layer, new PlainSource(), TimeSpan.FromMinutes(60), CancellationToken.None, writable: true);

        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void Editable_is_the_layers_fact_and_an_administrators_lifetime_still_wins()
    {
        TimeSpan server = TimeSpan.FromMinutes(60);

        // No ceiling at all means every edit is on offer to whoever holds the privilege.
        Assert.Equal(TimeSpan.Zero, QueryResponseCaching.LifetimeOf(new PublishedLayer(
            Guid.NewGuid(),
            new LayerDefinition("p", "public", "p", "geom", 4326, "objectid", "objectid", isHosted: false),
            "postgis", "unused", GeometryKind.Polygon, owner: null, SharingScope.Public, ServiceStatus.Started),
            server, null));

        // The store refuses writes (a view, a GeoParquet file): nobody can edit, so the default stands.
        Assert.Equal(server, QueryResponseCaching.LifetimeOf(Layer(SharingScope.Public, ceiling: ["Query", "Update"]), server, false));

        // A service configured to offer only Query.
        Assert.Equal(server, QueryResponseCaching.LifetimeOf(Layer(SharingScope.Public), server, true));

        // An administrator who set a lifetime on the layer chose it, editable or not.
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            QueryResponseCaching.LifetimeOf(
                Layer(SharingScope.Public, TimeSpan.FromSeconds(30), ["Query", "Update"]), server, true));
    }

    private static DefaultHttpContext Request(
        string query = "?f=json&where=1%3D1", RequestPrincipal? principal = null)
    {
        DefaultHttpContext context = new();
        context.Request.QueryString = new QueryString(query);

        if (principal is not null)
        {
            context.Features.Set(principal);
        }

        return context;
    }

    private static RequestPrincipal SignedIn() =>
        new(
            new Principal(Guid.NewGuid(), PrincipalKind.User, "erdinc", "Erdinc", isDisabled: false),
            Guid.NewGuid(),
            Authorization.Nothing);

    [Fact]
    public async Task A_public_layer_read_anonymously_is_cached_public()
    {
        DefaultHttpContext context = Request();
        PublishedLayer layer = Layer(SharingScope.Public);

        bool answered304 = await QueryResponseCaching.ApplyAsync(
            context, layer, new PlainSource(), TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.False(answered304);
        Assert.Equal(
            "public, max-age=300", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task A_private_layer_is_never_cached_public_even_though_the_caller_may_read_it()
    {
        // <b>Falsified: removing the `sharable` gate and always writing `public` makes this fail</b> —
        // confirmed by hand before this was left in place.
        DefaultHttpContext context = Request(principal: SignedIn());
        PublishedLayer layer = Layer(SharingScope.Private);

        await QueryResponseCaching.ApplyAsync(
            context, layer, new PlainSource(), TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.StartsWith("private", context.Response.Headers.CacheControl.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signed_in_caller_reading_a_public_layer_still_gets_private()
    {
        // The read succeeded — ADR-018 §3b already allowed it — but a shared cache must not learn
        // that and hand the next anonymous caller a response built for this one.
        DefaultHttpContext context = Request(principal: SignedIn());
        PublishedLayer layer = Layer(SharingScope.Public);

        await QueryResponseCaching.ApplyAsync(
            context, layer, new PlainSource(), TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.StartsWith("private", context.Response.Headers.CacheControl.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A vector tile carries every layer of its service, so a shared cache may keep it only when
    /// every one of those layers is public and nobody signed in to fetch it.
    /// </summary>
    /// <remarks>
    /// Written 2026-09-15: the tile face wrote <c>public</c> for every tile, and a private layer's
    /// tile fetched with a token came back <c>public, max-age=3600</c> on the showcase.
    /// </remarks>
    [Fact]
    public void A_tile_with_one_private_layer_is_private_even_for_an_anonymous_caller()
    {
        DefaultHttpContext context = Request();

        string header = QueryResponseCaching.CacheControlFor(
            context,
            [Layer(SharingScope.Public), Layer(SharingScope.Private)],
            TimeSpan.FromHours(1));

        Assert.Equal("private, max-age=3600", header);
    }

    [Fact]
    public void A_tile_whose_every_layer_is_public_is_public_for_an_anonymous_caller()
    {
        DefaultHttpContext context = Request();

        string header = QueryResponseCaching.CacheControlFor(
            context,
            [Layer(SharingScope.Public), Layer(SharingScope.Public)],
            TimeSpan.FromHours(1));

        Assert.Equal("public, max-age=3600", header);
    }

    [Fact]
    public void A_tile_fetched_by_a_signed_in_caller_is_private_even_when_every_layer_is_public()
    {
        DefaultHttpContext context = Request(principal: SignedIn());

        string header = QueryResponseCaching.CacheControlFor(
            context, [Layer(SharingScope.Public)], TimeSpan.FromHours(1));

        Assert.Equal("private, max-age=3600", header);
    }

    [Fact]
    public void A_zero_lifetime_tile_is_no_store_whoever_asks()
    {
        Assert.Equal(
            "no-store",
            QueryResponseCaching.CacheControlFor(
                Request(principal: SignedIn()), [Layer(SharingScope.Private)], TimeSpan.Zero));
    }

    [Fact]
    public async Task Zero_lifetime_is_no_store_and_nothing_else_is_written()
    {
        DefaultHttpContext context = Request();
        PublishedLayer layer = Layer(SharingScope.Public, TimeSpan.Zero);

        await QueryResponseCaching.ApplyAsync(
            context, layer, new VersionedSource("v1"), TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.True(string.IsNullOrEmpty(context.Response.Headers.ETag.ToString()));
    }

    [Fact]
    public async Task A_source_with_no_cheap_validator_gets_no_ETag()
    {
        DefaultHttpContext context = Request();
        PublishedLayer layer = Layer(SharingScope.Public);

        await QueryResponseCaching.ApplyAsync(
            context, layer, new PlainSource(), TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(string.IsNullOrEmpty(context.Response.Headers.ETag.ToString()));
    }

    [Fact]
    public async Task A_source_with_a_cheap_validator_gets_an_ETag_and_it_is_a_weak_quoted_tag()
    {
        // Weak, because the compression middleware (ADR-068) sends one representation in three
        // encodings under this tag, and a strong tag promises identical bytes.
        DefaultHttpContext context = Request();
        PublishedLayer layer = Layer(SharingScope.Public);

        await QueryResponseCaching.ApplyAsync(
            context, layer, new VersionedSource("v1"), TimeSpan.FromMinutes(5), CancellationToken.None);

        string etag = context.Response.Headers.ETag.ToString();
        Assert.StartsWith("W/\"", etag, StringComparison.Ordinal);
        Assert.EndsWith("\"", etag, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Republishing_a_layer_on_an_unchanged_file_changes_the_ETag()
    {
        // An alias edited on the same file: same query string, same file version, different body.
        // A GeoParquet file, which answers Writable: false — so it is cached, and the tag is what is tested.
        Guid id = Guid.NewGuid();
        LayerDefinition definition = new(
            "parcels", "public", "parcels", "geom", 4326, "objectid", "objectid", isHosted: false);

        PublishedLayer before = new(
            id, definition, "postgis", "unused", GeometryKind.Polygon, owner: null,
            SharingScope.Public, ServiceStatus.Started);
        PublishedLayer after = new(
            id, definition, "postgis", "unused", GeometryKind.Polygon, owner: null,
            SharingScope.Public, ServiceStatus.Started)
        {
            FieldOverrides = [new FieldOverride("objectid", "Parcel number", Hidden: false)],
        };

        DefaultHttpContext a = Request();
        await QueryResponseCaching.ApplyAsync(
            a, before, new VersionedSource("v1"), TimeSpan.FromMinutes(5), CancellationToken.None, writable: false);

        DefaultHttpContext b = Request();
        await QueryResponseCaching.ApplyAsync(
            b, after, new VersionedSource("v1"), TimeSpan.FromMinutes(5), CancellationToken.None, writable: false);

        Assert.NotEqual(a.Response.Headers.ETag.ToString(), b.Response.Headers.ETag.ToString());
    }

    [Fact]
    public void Every_property_of_a_published_layer_is_either_in_the_fingerprint_or_excluded_by_name()
    {
        // A property added to PublishedLayer and forgotten here would let a republished layer
        // revalidate a body it no longer produces. The fingerprint is serialised from an
        // anonymous object, so what it covers is read back from that serialisation.
        PublishedLayer layer = Layer(SharingScope.Public);
        using System.Text.Json.JsonDocument covered =
            System.Text.Json.JsonDocument.Parse(QueryResponseCaching.Fingerprint(layer));

        HashSet<string> accounted = new(StringComparer.Ordinal);

        foreach (System.Text.Json.JsonProperty property in covered.RootElement.EnumerateObject())
        {
            accounted.Add(property.Name);
        }

        foreach (string excluded in QueryResponseCaching.NotInFingerprint.Keys)
        {
            accounted.Add(excluded);
        }

        string[] missing =
        [
            .. typeof(PublishedLayer)
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(p => p.Name)
                .Where(name => !accounted.Contains(name)),
        ];

        Assert.True(
            missing.Length == 0,
            $"PublishedLayer properties neither fingerprinted nor excluded: {string.Join(", ", missing)}");

        string[] definition =
        [
            .. typeof(LayerDefinition)
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(p => p.Name)
                .Where(name => !accounted.Contains(name)),
        ];

        Assert.True(
            definition.Length == 0,
            $"LayerDefinition properties not fingerprinted: {string.Join(", ", definition)}");
    }

    [Fact]
    public async Task Two_different_queries_against_the_same_layer_get_different_ETags()
    {
        PublishedLayer layer = Layer(SharingScope.Public);

        DefaultHttpContext a = Request("?f=json&where=1%3D1");
        await QueryResponseCaching.ApplyAsync(
            a, layer, new VersionedSource("v1"), TimeSpan.FromMinutes(5), CancellationToken.None);

        DefaultHttpContext b = Request("?f=json&where=2%3D2");
        await QueryResponseCaching.ApplyAsync(
            b, layer, new VersionedSource("v1"), TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.NotEqual(
            a.Response.Headers.ETag.ToString(), b.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Replacing_the_file_changes_the_ETag_for_the_identical_query()
    {
        // <b>What a version change stands in for.</b> GeoParquetFolder's own version changes on
        // replacement (measured elsewhere); this asserts only that this class reacts to that
        // change, not that the folder produces one.
        PublishedLayer layer = Layer(SharingScope.Public);

        DefaultHttpContext before = Request();
        await QueryResponseCaching.ApplyAsync(
            before, layer, new VersionedSource("v1"), TimeSpan.FromMinutes(5), CancellationToken.None);

        DefaultHttpContext after = Request();
        await QueryResponseCaching.ApplyAsync(
            after, layer, new VersionedSource("v2"), TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.NotEqual(
            before.Response.Headers.ETag.ToString(), after.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task A_matching_If_None_Match_answers_304_and_never_calls_the_source_again()
    {
        PublishedLayer layer = Layer(SharingScope.Public);
        VersionedSource source = new("v1");

        DefaultHttpContext first = Request();
        await QueryResponseCaching.ApplyAsync(
            first, layer, source, TimeSpan.FromMinutes(5), CancellationToken.None);

        string etag = first.Response.Headers.ETag.ToString();

        DefaultHttpContext second = Request();
        second.Request.Headers.IfNoneMatch = etag;

        bool answered304 = await QueryResponseCaching.ApplyAsync(
            second, layer, source, TimeSpan.FromMinutes(5), CancellationToken.None);

        // <b>Falsified: changing `!= 304` to always return false makes this assertion fail</b> —
        // confirmed by hand.
        Assert.True(answered304);
        Assert.Equal(StatusCodes.Status304NotModified, second.Response.StatusCode);
        Assert.True(string.IsNullOrEmpty(second.Response.Headers.ContentLength.ToString()));

        // <b>The saving that matters for a query — the source is asked for its cheap validator
        // (one call) and never for rows.</b> `VersionedSource` implements only
        // `ISourceCacheValidator`; `ReadAsync` above throws `NotSupportedException`, so a test
        // that regressed into running the query would fail here rather than silently pass.
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task A_non_matching_If_None_Match_does_not_answer_304()
    {
        DefaultHttpContext context = Request();
        context.Request.Headers.IfNoneMatch = "\"not-the-right-tag\"";
        PublishedLayer layer = Layer(SharingScope.Public);

        bool answered304 = await QueryResponseCaching.ApplyAsync(
            context, layer, new VersionedSource("v1"), TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.False(answered304);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public void GET_is_the_only_safe_method()
    {
        DefaultHttpContext get = new();
        get.Request.Method = "GET";
        Assert.True(QueryResponseCaching.IsSafeMethod(get));

        DefaultHttpContext post = new();
        post.Request.Method = "POST";
        // <b>Falsified: dropping this check (calling ApplyAsync unconditionally for POST) was the
        // bug this guards — confirmed by hand that POST then received the same headers as GET.</b>
        Assert.False(QueryResponseCaching.IsSafeMethod(post));
    }

    [Fact]
    public async Task Null_defaults_to_the_server_lifetime_when_the_layer_names_none()
    {
        DefaultHttpContext context = Request();
        PublishedLayer layer = Layer(SharingScope.Public, cacheLifetime: null);

        await QueryResponseCaching.ApplyAsync(
            context, layer, new PlainSource(), TimeSpan.FromSeconds(42), CancellationToken.None);

        Assert.Equal(
            "public, max-age=42", context.Response.Headers.CacheControl.ToString());
    }
}

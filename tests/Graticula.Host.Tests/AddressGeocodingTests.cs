using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Formats;
using Graticula.Geocoding;
using Graticula.Geometries;
using Graticula.Host.Geocoding;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-310: a table of addresses located by the configured geocoder — what is asked, what is written, what is refused
/// and what is reported. The geocoder is a stand-in; the table is built in memory.
/// </summary>
public sealed class AddressGeocodingTests
{
    private sealed class FakeGeocoder(Func<string, IReadOnlyList<GeocodePlace>> answer) : IGeocoder
    {
        public ConcurrentBag<string> Asked { get; } = [];

        public Task<IReadOnlyList<GeocodePlace>> SearchAsync(GeocodeSearch search, CancellationToken cancellationToken)
        {
            Asked.Add(search.Text);
            return Task.FromResult(answer(search.Text));
        }

        public Task<GeocodePlace?> ReverseAsync(
            double longitude, double latitude, double? radiusMeters, string? language, CancellationToken cancellationToken) =>
            Task.FromResult<GeocodePlace?>(null);
    }

    private static readonly GeocodePlace Ankara = new(32.86, 39.93, GeocodePlaceKind.Locality, "R1")
    {
        Name = "Kizilay",
        City = "Ankara",
        Country = "Turkiye",
        CountryCode = "TR",
    };

    private static IReadOnlyList<GeocodePlace> ByWord(string text) =>
        text.Contains("ankara", StringComparison.OrdinalIgnoreCase) ? [Ankara] : [];

    private static GeocoderSettings Settings(SharingScope sharing = SharingScope.Public, int batch = 200) =>
        new(new Uri("http://photon.test/"), TimeSpan.FromSeconds(5), sharing, new HashSet<string>(), batch);

    private static DefaultHttpContext Context(IGeocoder? geocoder, GeocoderSettings? settings)
    {
        ServiceCollection services = new();
        services.AddLogging();

        if (geocoder is not null) services.AddSingleton(geocoder);
        if (settings is not null) services.AddSingleton(settings);

        DefaultHttpContext context = new() { RequestServices = services.BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        context.Features.Set(new RequestPrincipal(Principal.Anonymous, null, Authorization.Nothing));

        return context;
    }

    private static ImportedDataset Table(params (string Name, string Street, string City)[] rows)
    {
        List<ImportedFeature> features = [];

        foreach ((string name, string street, string city) in rows)
        {
            features.Add(new ImportedFeature(null, new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement(name),
                ["street"] = JsonSerializer.SerializeToElement(street),
                ["city"] = city.Length == 0 ? JsonSerializer.SerializeToElement<string?>(null) : JsonSerializer.SerializeToElement(city),
            }));
        }

        return new ImportedDataset(
            features,
            [new InferredColumn { Name = "name" }, new InferredColumn { Name = "street" }, new InferredColumn { Name = "city" }],
            GeometryKind.Point,
            4326);
    }

    private static FormCollection Form(params (string Key, string Value)[] fields) =>
        new(fields.ToDictionary(f => f.Key, f => new StringValues(f.Value)));

    private static string Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    [Fact]
    public async Task Rows_are_located_unmatched_ones_are_dropped_and_reported_by_row_number()
    {
        FakeGeocoder geocoder = new(ByWord);
        DefaultHttpContext context = Context(geocoder, Settings());

        ImportedDataset? located = await AddressGeocoding.ApplyAsync(
            context,
            Form(("locationType", "address"), ("addressTemplate", "{street}, {city}")),
            Table(("a", "Kizilay", "Ankara"), ("b", "Nowhere", "Qqq"), ("c", "Cankaya", "Ankara")),
            CancellationToken.None);

        Assert.NotNull(located);
        Assert.Equal(2, located.Features.Count);
        Assert.All(located.Features, f => Assert.Equal(new Point(32.86, 39.93).X, ((Point)f.Geometry!).X));
        Assert.Equal(["a", "c"], located.Features.Select(f => f.Values["name"].GetString()));
        Assert.Equal(4326, located.Srid);
        Assert.Equal(GeometryKind.Point, located.GeometryType);

        AddressGeocoding.Report report = Assert.IsType<AddressGeocoding.Report>(AddressGeocoding.ReportOf(context));
        Assert.Equal(3, report.Rows);
        Assert.Equal(2, report.Matched);
        Assert.Equal(1, report.UnmatchedCount);
        Assert.Equal([2], report.Unmatched);
        Assert.Equal(["Cankaya, Ankara", "Kizilay, Ankara", "Nowhere, Qqq"], geocoder.Asked.Order());
    }

    [Fact]
    public async Task The_address_columns_are_joined_in_the_order_of_an_address_and_an_empty_part_leaves_no_trace()
    {
        FakeGeocoder geocoder = new(ByWord);

        ImportedDataset? located = await AddressGeocoding.ApplyAsync(
            Context(geocoder, Settings()),
            Form(("addressFields", "{\"City\":\"city\",\"Address\":\"street\"}")),
            Table(("a", "Kizilay 5", "Ankara"), ("b", "Kizilay 6", string.Empty)),
            CancellationToken.None);

        Assert.NotNull(located);
        Assert.Equal(["Kizilay 5, Ankara", "Kizilay 6"], geocoder.Asked.Order());
    }

    [Fact]
    public async Task A_table_with_no_geocoder_is_refused_naming_the_key_to_set()
    {
        DefaultHttpContext context = Context(null, null);

        ImportedDataset? located = await AddressGeocoding.ApplyAsync(
            context, Form(("addressTemplate", "{street}")), Table(("a", "x", "y")), CancellationToken.None);

        Assert.Null(located);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Contains("Graticula:Geocoding:PhotonUrl", Body(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_the_geocoders_sharing_does_not_admit_is_refused_and_nothing_is_asked()
    {
        FakeGeocoder geocoder = new(ByWord);
        DefaultHttpContext context = Context(geocoder, Settings(SharingScope.Organization));

        ImportedDataset? located = await AddressGeocoding.ApplyAsync(
            context, Form(("addressTemplate", "{street}")), Table(("a", "Ankara", "x")), CancellationToken.None);

        Assert.Null(located);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Empty(geocoder.Asked);
    }

    [Fact]
    public async Task More_rows_than_the_batch_are_refused_whole_and_nothing_is_asked()
    {
        FakeGeocoder geocoder = new(ByWord);
        DefaultHttpContext context = Context(geocoder, Settings(batch: 2));

        ImportedDataset? located = await AddressGeocoding.ApplyAsync(
            context, Form(("addressTemplate", "{street}")),
            Table(("a", "Ankara", "x"), ("b", "Ankara", "x"), ("c", "Ankara", "x")), CancellationToken.None);

        Assert.Null(located);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Contains("MaxBatchSize", Body(context), StringComparison.Ordinal);
        Assert.Empty(geocoder.Asked);
    }

    [Theory]
    [InlineData("{\"addressTemplate\":\"{street} {nope}\"}", "nope")]
    [InlineData("{\"addressFields\":\"{\\\"Address\\\":\\\"nope\\\"}\"}", "nope")]
    [InlineData("{\"addressFields\":\"{\\\"Planet\\\":\\\"street\\\"}\"}", "Planet")]
    [InlineData("{}", "addressTemplate")]
    public async Task A_template_or_mapping_that_names_nothing_is_refused_before_the_engine_is_asked(string json, string says)
    {
        FakeGeocoder geocoder = new(ByWord);
        DefaultHttpContext context = Context(geocoder, Settings());

        Dictionary<string, string> asked = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;

        ImportedDataset? located = await AddressGeocoding.ApplyAsync(
            context, new FormCollection(asked.ToDictionary(p => p.Key, p => new StringValues(p.Value))),
            Table(("a", "Ankara", "x")), CancellationToken.None);

        Assert.Null(located);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Contains(says, Body(context), StringComparison.Ordinal);
        Assert.Empty(geocoder.Asked);
    }

    [Fact]
    public async Task An_engine_that_is_down_is_a_503_and_a_table_nothing_matches_is_refused()
    {
        DefaultHttpContext down = Context(
            new FakeGeocoder(_ => throw new GeocoderUnavailableException("Photon did not answer.")), Settings());

        Assert.Null(await AddressGeocoding.ApplyAsync(
            down, Form(("addressTemplate", "{street}")), Table(("a", "Ankara", "x"), ("b", "Ankara", "x")), CancellationToken.None));
        Assert.Equal(503, down.Response.StatusCode);

        DefaultHttpContext none = Context(new FakeGeocoder(_ => []), Settings());

        Assert.Null(await AddressGeocoding.ApplyAsync(
            none, Form(("addressTemplate", "{street}")), Table(("a", "Ankara", "x")), CancellationToken.None));
        Assert.Equal(400, none.Response.StatusCode);
        Assert.Contains("None of the 1 addresses", Body(none), StringComparison.Ordinal);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geocoding;
using Graticula.Geometries;
using Graticula.Host.Geocoding;
using Graticula.Platform.Identity;
using Authorization = Graticula.Platform.Identity.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The GeocodeServer over a Photon — ADR-258 — against a stub Photon that answers recorded-shape GeoJSON.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole chain but the socket.</b> Requests go through the real routes, the real sharing filter, the
/// real <see cref="PhotonGeocoder"/> and its parsing; only the HTTP handler under the adapter is a fake, and it
/// answers with GeoJSON written by hand in the shape Photon 1.3 answered with on the VPS
/// (docs/research/photon-turkiye-2026-11-02.md). The projector is a spherical Web Mercator — the arithmetic,
/// not PostGIS — because what is under test is that outSR is honoured, not PROJ.
/// </para>
/// </remarks>
public sealed class GeocodeServerTests
{
    private const string Kizilay = """
        {"type":"Feature","geometry":{"type":"Point","coordinates":[32.8541,39.9208]},
         "properties":{"osm_type":"N","osm_id":21512345,"osm_key":"place","osm_value":"suburb","type":"district",
           "name":"Kızılay","district":"Çankaya","county":"Çankaya","city":"Ankara","state":"Ankara",
           "country":"Türkiye","countrycode":"TR","postcode":"06420",
           "extent":[32.845,39.927,32.862,39.914]}}
        """;

    private const string AnkaraCity = """
        {"type":"Feature","geometry":{"type":"Point","coordinates":[32.8597,39.9334]},
         "properties":{"osm_type":"R","osm_id":223422,"osm_key":"place","osm_value":"city","type":"city",
           "name":"Ankara","state":"Ankara","country":"Türkiye","countrycode":"TR"}}
        """;

    private const string BusStop = """
        {"type":"Feature","geometry":{"type":"Point","coordinates":[32.8550,39.9150]},
         "properties":{"osm_type":"N","osm_id":555,"osm_key":"highway","osm_value":"bus_stop","type":"house",
           "name":"Atatürk Bulvarı","street":"Atatürk Bulvarı","district":"Çankaya","city":"Ankara",
           "country":"Türkiye","countrycode":"TR"}}
        """;

    private const string Muhurdar = """
        {"type":"Feature","geometry":{"type":"Point","coordinates":[29.0244,40.9903]},
         "properties":{"osm_type":"W","osm_id":777,"osm_key":"building","osm_value":"apartments","type":"house",
           "housenumber":"22","street":"Mühürdar Caddesi","locality":"Caferağa","district":"Kadıköy",
           "city":"İstanbul","state":"İstanbul","country":"Türkiye","countrycode":"TR","postcode":"34710"}}
        """;

    private static string Collection(params string[] features) =>
        "{\"type\":\"FeatureCollection\",\"features\":[" + string.Join(",", features) + "]}";

    /// <summary>A Photon that answers from a function of the request, and remembers what it was asked.</summary>
    private sealed class StubPhoton(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }

        public static StubPhoton Json(string body) =>
            new(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>A Photon that is not there: every request fails to connect.</summary>
    private sealed class DownPhoton : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused (127.0.0.1:2322)");
    }

    /// <summary>A Photon that never answers, so the adapter's own deadline is what ends the request.</summary>
    private sealed class SilentPhoton : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>Spherical Web Mercator both ways, and nothing else.</summary>
    private sealed class MercatorProjector : IProjector
    {
        private const double R = 6378137.0;

        public Task<(IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)> ProjectAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, CancellationToken cancellationToken)
        {
            List<Geometry> moved = [];

            foreach (Point p in geometries.Cast<Point>())
            {
                moved.Add((fromSrid, toSrid) switch
                {
                    (4326, 3857) => new Point(
                        R * p.X * Math.PI / 180, R * Math.Log(Math.Tan(Math.PI / 4 + (p.Y * Math.PI / 360)))),
                    (3857, 4326) => new Point(
                        p.X / R * 180 / Math.PI, (2 * Math.Atan(Math.Exp(p.Y / R)) - (Math.PI / 2)) * 180 / Math.PI),
                    _ => throw new ArgumentException($"no transformation {fromSrid} to {toSrid}"),
                });
            }

            return Task.FromResult<(IReadOnlyList<Geometry>, ProjectionProvenance)>(
                (moved, new ProjectionProvenance("mercator stub", null)));
        }

        public Task<IReadOnlyList<Geometry>?> ProjectToDefinitionAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, string definition, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Geometry>?>(null);

        public Task<IReadOnlyList<Geometry>> GeneralizeAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, double tolerance, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) => Task.FromResult(srid is 4326 or 3857);

        public Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) => Task.FromResult<Envelope?>(null);

        public Task<IReadOnlyList<KnownReference>> ReferencesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnownReference>>([]);

        public Task<ProjectionProvenance> DescribeAsync(int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            Task.FromResult(new ProjectionProvenance("mercator stub", null));
    }

    private static GeocoderSettings Settings(SharingScope sharing = SharingScope.Public, double seconds = 5) =>
        new(new Uri("http://photon.test/"), TimeSpan.FromSeconds(seconds), sharing, new HashSet<string> { "tr", "en" });

    /// <summary>The routes as Program maps them, an anonymous caller, and the given Photon underneath.</summary>
    private static async Task<WebApplication> ServeAsync(HttpMessageHandler photon, GeocoderSettings? settings)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        if (settings is not null)
        {
            builder.Services.AddSingleton(settings);
            builder.Services.AddSingleton<IGeocoder>(new PhotonGeocoder(settings, photon));
        }

        builder.Services.AddSingleton<IProjector, MercatorProjector>();

        WebApplication app = builder.Build();

        app.Use((context, next) =>
        {
            context.Features.Set(new RequestPrincipal(Principal.Anonymous, null, Authorization.Nothing));
            return next(context);
        });

        GeocodeServerEndpoints.Map(app, settings);

        await app.StartAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(WebApplication app, string path)
    {
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));
        string text = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private const string Root = "/rest/services/Utilities/Geocode/GeocodeServer";

    [Fact]
    public async Task The_service_says_what_it_takes_what_it_answers_and_whose_data_it_is()
    {
        await using WebApplication app = await ServeAsync(StubPhoton.Json(Collection()), Settings());

        (HttpStatusCode status, JsonElement body) = await GetAsync(app, $"{Root}?f=json");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(4326, body.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.Equal("SingleLine", body.GetProperty("singleLineAddressField").GetProperty("name").GetString());
        Assert.Equal("Geocode,ReverseGeocode,Suggest", body.GetProperty("capabilities").GetString());
        Assert.Contains("OpenStreetMap contributors", body.GetProperty("copyrightText").GetString(), StringComparison.Ordinal);

        string[] candidateFields =
            [.. body.GetProperty("candidateFields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!)];
        Assert.Contains("Score", candidateFields);
        Assert.Contains("Match_addr", candidateFields);
        Assert.Contains("Addr_type", candidateFields);

        Assert.Contains(body.GetProperty("addressFields").EnumerateArray(), f => f.GetProperty("name").GetString() == "City");
    }

    [Fact]
    public async Task Find_address_candidates_maps_photon_places_to_scored_candidates_best_first()
    {
        StubPhoton photon = StubPhoton.Json(Collection(AnkaraCity, Kizilay));
        await using WebApplication app = await ServeAsync(photon, Settings());

        (HttpStatusCode status, JsonElement body) =
            await GetAsync(app, $"{Root}/findAddressCandidates?SingleLine=Kizilay%20Ankara&outFields=*&f=json");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(4326, body.GetProperty("spatialReference").GetProperty("wkid").GetInt32());

        JsonElement[] candidates = [.. body.GetProperty("candidates").EnumerateArray()];
        Assert.Equal(2, candidates.Length);

        // Kızılay carries both words, folded (Kizilay = Kızılay); the city carries only one — so it is first now,
        // although Photon ranked the city first.
        JsonElement best = candidates[0];
        Assert.Equal(100, best.GetProperty("score").GetDouble());
        Assert.Equal(50, candidates[1].GetProperty("score").GetDouble());
        Assert.Equal("Kızılay, Çankaya, Ankara, 06420", best.GetProperty("address").GetString());
        Assert.Equal(32.8541, best.GetProperty("location").GetProperty("x").GetDouble(), 6);
        Assert.Equal(39.9208, best.GetProperty("location").GetProperty("y").GetDouble(), 6);

        JsonElement attributes = best.GetProperty("attributes");
        Assert.Equal("Locality", attributes.GetProperty("Addr_type").GetString());
        Assert.Equal("Kızılay, Çankaya, Ankara, 06420, Türkiye", attributes.GetProperty("LongLabel").GetString());
        Assert.Equal("Kızılay", attributes.GetProperty("ShortLabel").GetString());
        Assert.Equal("TR", attributes.GetProperty("CountryCode").GetString());
        Assert.Equal("M", attributes.GetProperty("Status").GetString());

        // Photon's extent, west-north-east-south, read as a box.
        JsonElement extent = best.GetProperty("extent");
        Assert.Equal(32.845, extent.GetProperty("xmin").GetDouble(), 6);
        Assert.Equal(39.914, extent.GetProperty("ymin").GetDouble(), 6);
        Assert.Equal(32.862, extent.GetProperty("xmax").GetDouble(), 6);
        Assert.Equal(39.927, extent.GetProperty("ymax").GetDouble(), 6);

        // What Photon was asked: the line, and the default number.
        Uri asked = Assert.Single(photon.Asked);
        Assert.Equal("/api", asked.AbsolutePath);
        Assert.Contains("q=Kizilay%20Ankara", asked.Query, StringComparison.Ordinal);
        Assert.Contains("limit=20", asked.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_out_fields_a_candidate_carries_only_its_score_and_match_address()
    {
        await using WebApplication app = await ServeAsync(StubPhoton.Json(Collection(Kizilay)), Settings());

        (_, JsonElement body) = await GetAsync(app, $"{Root}/findAddressCandidates?SingleLine=Kizilay&f=json");

        string[] names =
            [.. body.GetProperty("candidates")[0].GetProperty("attributes").EnumerateObject().Select(p => p.Name)];

        Assert.Equal(["Score", "Match_addr"], names);
    }

    [Fact]
    public async Task Max_locations_bounds_what_photon_is_asked_and_what_is_answered()
    {
        StubPhoton photon = StubPhoton.Json(Collection(AnkaraCity, Kizilay, BusStop));
        await using WebApplication app = await ServeAsync(photon, Settings());

        (_, JsonElement body) = await GetAsync(app, $"{Root}/findAddressCandidates?SingleLine=Ankara&maxLocations=1&f=json");

        Assert.Single(body.GetProperty("candidates").EnumerateArray());
        Assert.Contains("limit=1", photon.Asked[0].Query, StringComparison.Ordinal);

        // Above ArcGIS's ceiling is answered with the ceiling rather than refused.
        await GetAsync(app, $"{Root}/findAddressCandidates?SingleLine=Ankara&maxLocations=500&f=json");
        Assert.Contains("limit=50", photon.Asked[1].Query, StringComparison.Ordinal);

        (HttpStatusCode refused, JsonElement error) =
            await GetAsync(app, $"{Root}/findAddressCandidates?SingleLine=Ankara&maxLocations=none&f=json");
        Assert.Equal(HttpStatusCode.BadRequest, refused);
        Assert.Equal(JsonValueKind.Array, error.GetProperty("error").GetProperty("details").ValueKind);
    }

    [Fact]
    public async Task Out_sr_projects_locations_and_extents_and_is_echoed()
    {
        await using WebApplication app = await ServeAsync(StubPhoton.Json(Collection(Kizilay)), Settings());

        (HttpStatusCode status, JsonElement body) =
            await GetAsync(app, $"{Root}/findAddressCandidates?SingleLine=Kizilay&outSR=102100&f=json");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(102100, body.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.Equal(3857, body.GetProperty("spatialReference").GetProperty("latestWkid").GetInt32());

        JsonElement location = body.GetProperty("candidates")[0].GetProperty("location");
        Assert.Equal(6378137.0 * 32.8541 * Math.PI / 180, location.GetProperty("x").GetDouble(), 3);
        Assert.True(location.GetProperty("y").GetDouble() > 4_800_000, "y is in metres, not degrees");

        JsonElement extent = body.GetProperty("candidates")[0].GetProperty("extent");
        Assert.True(extent.GetProperty("xmin").GetDouble() < location.GetProperty("x").GetDouble());
        Assert.True(extent.GetProperty("xmax").GetDouble() > location.GetProperty("x").GetDouble());
    }

    [Fact]
    public async Task A_location_in_web_mercator_reaches_photon_as_longitude_and_latitude()
    {
        StubPhoton photon = StubPhoton.Json(Collection(Kizilay));
        await using WebApplication app = await ServeAsync(photon, Settings());

        double x = 6378137.0 * 32.85 * Math.PI / 180;
        string location = Uri.EscapeDataString(
            $"{{\"x\":{x.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"y\":4852000,\"spatialReference\":{{\"wkid\":102100}}}}");

        (HttpStatusCode status, JsonElement body) =
            await GetAsync(app, $"{Root}/findAddressCandidates?SingleLine=Kizilay&location={location}&outFields=Distance&f=json");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("lon=32.85", photon.Asked[0].Query, StringComparison.Ordinal);
        Assert.Contains("lat=39.", photon.Asked[0].Query, StringComparison.Ordinal);
        Assert.True(body.GetProperty("candidates")[0].GetProperty("attributes").GetProperty("Distance").GetDouble() > 0);
    }

    [Fact]
    public async Task Suggest_answers_labels_with_magic_keys_and_find_recognises_the_key()
    {
        StubPhoton photon = StubPhoton.Json(Collection(AnkaraCity, Kizilay));
        await using WebApplication app = await ServeAsync(photon, Settings());

        (HttpStatusCode status, JsonElement body) = await GetAsync(app, $"{Root}/suggest?text=Ank&f=json");

        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement[] suggestions = [.. body.GetProperty("suggestions").EnumerateArray()];
        Assert.Equal(2, suggestions.Length);
        Assert.Equal("Ankara, Türkiye", suggestions[0].GetProperty("text").GetString());
        Assert.Equal("R223422", suggestions[0].GetProperty("magicKey").GetString());
        Assert.False(suggestions[0].GetProperty("isCollection").GetBoolean());
        Assert.Contains("limit=5", photon.Asked[0].Query, StringComparison.Ordinal);

        // The SDK sends the suggestion back with its key; the keyed place is the one answer, wholly matched.
        (_, JsonElement found) = await GetAsync(
            app, $"{Root}/findAddressCandidates?singleLine=Kızılay&magicKey=N21512345&f=json");

        JsonElement only = Assert.Single(found.GetProperty("candidates").EnumerateArray());
        Assert.Equal(100, only.GetProperty("score").GetDouble());
        Assert.StartsWith("Kızılay", only.GetProperty("address").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reverse_geocode_names_the_place_and_answers_in_the_locations_reference()
    {
        StubPhoton photon = StubPhoton.Json(Collection(Muhurdar));
        await using WebApplication app = await ServeAsync(photon, Settings());

        (HttpStatusCode status, JsonElement body) =
            await GetAsync(app, $"{Root}/reverseGeocode?location=29.0244,40.9903&langCode=tr&f=json");

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement address = body.GetProperty("address");
        Assert.Equal("Mühürdar Caddesi 22", address.GetProperty("Address").GetString());
        Assert.Equal("PointAddress", address.GetProperty("Addr_type").GetString());
        Assert.Equal("22", address.GetProperty("AddNum").GetString());
        Assert.Equal("Kadıköy", address.GetProperty("District").GetString());
        Assert.Equal("İstanbul", address.GetProperty("City").GetString());
        Assert.Equal("34710", address.GetProperty("Postal").GetString());
        Assert.Equal(
            "Mühürdar Caddesi 22, Caferağa, Kadıköy, İstanbul, 34710, Türkiye",
            address.GetProperty("LongLabel").GetString());

        Assert.Equal(4326, body.GetProperty("location").GetProperty("spatialReference").GetProperty("wkid").GetInt32());

        Uri asked = Assert.Single(photon.Asked);
        Assert.Equal("/reverse", asked.AbsolutePath);
        Assert.Contains("lon=29.0244", asked.Query, StringComparison.Ordinal);
        Assert.Contains("lat=40.9903", asked.Query, StringComparison.Ordinal);
        Assert.Contains("lang=tr", asked.Query, StringComparison.Ordinal);

        // A Web Mercator location is answered in Web Mercator.
        string mercator = Uri.EscapeDataString("{\"x\":3231000,\"y\":5009000,\"spatialReference\":{\"wkid\":3857}}");
        (_, JsonElement projected) = await GetAsync(app, $"{Root}/reverseGeocode?location={mercator}&f=json");
        Assert.Equal(3857, projected.GetProperty("location").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.True(projected.GetProperty("location").GetProperty("x").GetDouble() > 3_000_000);
    }

    [Fact]
    public async Task A_language_the_index_was_not_built_with_is_not_passed_to_photon()
    {
        StubPhoton photon = StubPhoton.Json(Collection(Muhurdar));
        await using WebApplication app = await ServeAsync(photon, Settings());

        await GetAsync(app, $"{Root}/reverseGeocode?location=29.0244,40.9903&langCode=ja&f=json");

        Assert.DoesNotContain("lang=", photon.Asked[0].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reverse_geocode_with_nothing_near_answers_the_references_refusal()
    {
        await using WebApplication app = await ServeAsync(StubPhoton.Json(Collection()), Settings());

        (HttpStatusCode status, JsonElement body) = await GetAsync(app, $"{Root}/reverseGeocode?location=0,0&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(400, body.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains(
            "Unable to find address for the specified location.",
            body.GetProperty("error").GetProperty("details").EnumerateArray().Select(d => d.GetString()));
    }

    [Fact]
    public async Task A_photon_that_is_down_is_a_503_with_a_sentence_not_a_crash()
    {
        await using WebApplication app = await ServeAsync(new DownPhoton(), Settings());

        foreach (string path in (string[])[
                     $"{Root}/findAddressCandidates?SingleLine=Ankara&f=json",
                     $"{Root}/suggest?text=Ank&f=json",
                     $"{Root}/reverseGeocode?location=32.85,39.92&f=json"])
        {
            (HttpStatusCode status, JsonElement body) = await GetAsync(app, path);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
            JsonElement error = body.GetProperty("error");
            Assert.Equal(503, error.GetProperty("code").GetInt32());
            Assert.Contains("could not be reached", error.GetProperty("details")[0].GetString(), StringComparison.Ordinal);
        }

        // The service document needs no engine and still answers.
        (HttpStatusCode root, _) = await GetAsync(app, $"{Root}?f=json");
        Assert.Equal(HttpStatusCode.OK, root);
    }

    [Fact]
    public async Task A_photon_that_does_not_answer_in_time_is_a_503_naming_the_deadline()
    {
        await using WebApplication app = await ServeAsync(new SilentPhoton(), Settings(seconds: 0.3));

        (HttpStatusCode status, JsonElement body) = await GetAsync(app, $"{Root}/suggest?text=Ank&f=json");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("did not answer within", body.GetProperty("error").GetProperty("details")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_photon_answering_an_error_or_not_json_is_unavailable_and_its_body_is_not_repeated()
    {
        StubPhoton failing = new(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("java.lang.IllegalStateException: secret internals"),
        });

        await using (WebApplication app = await ServeAsync(failing, Settings()))
        {
            (HttpStatusCode status, JsonElement body) = await GetAsync(app, $"{Root}/suggest?text=Ank&f=json");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
            Assert.DoesNotContain("secret", body.GetRawText(), StringComparison.Ordinal);
            Assert.Contains("HTTP 500", body.GetRawText(), StringComparison.Ordinal);
        }

        await using (WebApplication app = await ServeAsync(StubPhoton.Json("<html>not photon</html>"), Settings()))
        {
            (HttpStatusCode status, _) = await GetAsync(app, $"{Root}/suggest?text=Ank&f=json");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        }
    }

    [Fact]
    public async Task An_answer_larger_than_the_bound_is_refused_rather_than_read()
    {
        string huge = Collection(Enumerable.Repeat(Kizilay, 2 + (PhotonGeocoder.MaximumResponseBytes / Kizilay.Length)).ToArray());

        using PhotonGeocoder geocoder = new(Settings(), StubPhoton.Json(huge));

        await Assert.ThrowsAsync<GeocoderUnavailableException>(() =>
            geocoder.SearchAsync(new GeocodeSearch("Kizilay", 5), CancellationToken.None));
    }

    [Fact]
    public async Task Not_configured_means_absent_no_route_and_no_directory_entry()
    {
        await using WebApplication app = await ServeAsync(new DownPhoton(), settings: null);

        (HttpStatusCode status, _) = await GetAsync(app, $"{Root}?f=json");
        Assert.Equal(HttpStatusCode.NotFound, status);

        RequestPrincipal anyone = new(Principal.Anonymous, null, Authorization.Nothing);
        Assert.False(GeocodeServerEndpoints.DirectoryLists(null, "Utilities", anyone));
        Assert.True(GeocodeServerEndpoints.DirectoryLists(Settings(), "Utilities", anyone));
        Assert.False(GeocodeServerEndpoints.DirectoryLists(Settings(), null, anyone));
    }

    [Fact]
    public async Task Shared_with_the_organization_it_is_neither_listed_nor_answered_to_a_stranger()
    {
        await using WebApplication app = await ServeAsync(StubPhoton.Json(Collection(Kizilay)), Settings(SharingScope.Organization));

        (HttpStatusCode status, JsonElement body) = await GetAsync(app, $"{Root}/findAddressCandidates?SingleLine=Kizilay&f=json");

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.False(body.TryGetProperty("candidates", out _));
        Assert.False(GeocodeServerEndpoints.DirectoryLists(
            Settings(SharingScope.Organization), "Utilities", new RequestPrincipal(Principal.Anonymous, null, Authorization.Nothing)));
    }

    [Fact]
    public void Settings_are_absent_without_a_url_and_refuse_an_unusable_one()
    {
        Assert.Null(GeocoderSettings.Read(new ConfigurationBuilder().Build()));

        // Unset, it is the organisation's (owner decision 2026-11-02): not an open proxy onto the operator's Photon.
        Assert.Equal(
            SharingScope.Organization,
            GeocoderSettings.Read(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Graticula:Geocoding:PhotonUrl"] = "http://127.0.0.1:2322" })
                .Build())!.Sharing);

        GeocoderSettings read = GeocoderSettings.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graticula:Geocoding:PhotonUrl"] = "http://127.0.0.1:2322",
                ["Graticula:Geocoding:Languages"] = "tr, EN",
                ["Graticula:Geocoding:Sharing"] = "public",
            })
            .Build())!;

        Assert.Equal("http://127.0.0.1:2322/", read.PhotonUrl.ToString());
        Assert.Equal(TimeSpan.FromSeconds(5), read.Timeout);
        Assert.Equal(SharingScope.Public, read.Sharing);
        Assert.True(read.Languages.SetEquals(["tr", "en"]));

        foreach ((string key, string value) in (ValueTuple<string, string>[])[
                     ("PhotonUrl", "photon:2322"),
                     ("PhotonUrl", "ftp://photon/"),
                     ("TimeoutSeconds", "0"),
                     ("Sharing", "everyone"),
                     ("Sharing", "group"),
                     ("Languages", "turkish")])
        {
            Dictionary<string, string?> values = new() { ["Graticula:Geocoding:PhotonUrl"] = "http://127.0.0.1:2322/" };
            values[$"Graticula:Geocoding:{key}"] = value;

            Assert.Throws<InvalidOperationException>(() =>
                GeocoderSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
        }
    }

    [Fact]
    public async Task A_search_extent_and_a_location_bias_are_passed_to_photon_as_it_spells_them()
    {
        StubPhoton photon = StubPhoton.Json(Collection());
        using PhotonGeocoder geocoder = new(Settings(), photon);

        await geocoder.SearchAsync(
            new GeocodeSearch("çay bahçesi", 7, (32.85, 39.92), new GeocodeBox(32, 39, 33, 40), "TR-tr"),
            CancellationToken.None);

        string query = photon.Asked[0].Query;
        Assert.Contains("q=%C3%A7ay%20bah%C3%A7esi", query, StringComparison.Ordinal);
        Assert.Contains("limit=7", query, StringComparison.Ordinal);
        Assert.Contains("lat=39.92&lon=32.85", query, StringComparison.Ordinal);
        Assert.Contains("bbox=32,39,33,40", query, StringComparison.Ordinal);
        Assert.Contains("lang=tr", query, StringComparison.Ordinal);
    }
}

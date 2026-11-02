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

    // ---------- ADR-258 §5.8: geocodeAddresses, category, sourceCountry, distance, the portal's locator ----------

    private static string Records(params string[] attributes) =>
        Uri.EscapeDataString("{\"records\":[" + string.Join(",", attributes.Select(a => "{\"attributes\":" + a + "}")) + "]}");

    /// <summary>A Photon that answers by the text asked, fails the texts it is told to, and counts how many it holds at once.</summary>
    private sealed class BatchPhoton(Func<string, string?> answer) : HttpMessageHandler
    {
        private int _now;

        public int MostAtOnce { get; private set; }

        public List<Uri> Asked { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int now = Interlocked.Increment(ref _now);

            lock (Asked)
            {
                Asked.Add(request.RequestUri!);
                MostAtOnce = Math.Max(MostAtOnce, now);
            }

            try
            {
                await Task.Delay(20, cancellationToken);

                string q = Uri.UnescapeDataString(
                    request.RequestUri!.Query.Split('&').First(part => part.TrimStart('?').StartsWith("q=", StringComparison.Ordinal))
                        .TrimStart('?')[2..]);

                return answer(q) is { } body
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.BadGateway);
            }
            finally
            {
                Interlocked.Decrement(ref _now);
            }
        }
    }

    [Fact]
    public async Task Geocode_addresses_answers_every_record_by_its_objectid_in_the_order_sent()
    {
        BatchPhoton photon = new(q => q.Contains("Kizilay", StringComparison.Ordinal) ? Collection(AnkaraCity, Kizilay)
            : q.Contains("Ankara", StringComparison.Ordinal) ? Collection(AnkaraCity)
            : Collection(Muhurdar));

        await using WebApplication app = await ServeAsync(photon, Settings());

        // OBJECTID 0 is what the ArcGIS API for Python numbers its first record with.
        string addresses = Records(
            "{\"OBJECTID\":0,\"SingleLine\":\"Kizilay Ankara\"}",
            "{\"OBJECTID\":7,\"City\":\"Ankara\",\"CountryCode\":\"TR\"}",
            "{\"objectid\":9,\"SingleLine\":\"\"}",
            "{\"OBJECTID\":12,\"SingleLine\":\"Zzzz Qqqq\"}");

        (HttpStatusCode status, JsonElement body) =
            await GetAsync(app, $"{Root}/geocodeAddresses?addresses={addresses}&outSR=3857&f=json");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3857, body.GetProperty("spatialReference").GetProperty("wkid").GetInt32());

        JsonElement[] locations = [.. body.GetProperty("locations").EnumerateArray()];
        long[] ids = [.. locations.Select(l => l.GetProperty("attributes").GetProperty("ResultID").GetInt64())];
        Assert.Equal([0L, 7L, 9L, 12L], ids);

        // The best of what Photon answered, scored as findAddressCandidates scores, with every field.
        JsonElement first = locations[0];
        Assert.Equal(100, first.GetProperty("score").GetDouble());
        Assert.Equal("Kızılay, Çankaya, Ankara, 06420", first.GetProperty("address").GetString());
        Assert.True(first.GetProperty("location").GetProperty("x").GetDouble() > 3_000_000, "projected to 3857");
        Assert.Equal("M", first.GetProperty("attributes").GetProperty("Status").GetString());
        Assert.Equal("Locality", first.GetProperty("attributes").GetProperty("Addr_type").GetString());

        // A structured record is joined into one line, as findAddressCandidates joins it.
        Assert.Equal("Ankara, Türkiye", locations[1].GetProperty("attributes").GetProperty("LongLabel").GetString());

        // An empty record is not asked of the engine, and an answer that matches none of the words is no match: both
        // are the reference's unmatched record — no location, empty address, score 0, Status U.
        foreach (JsonElement unmatched in locations[2..])
        {
            Assert.False(unmatched.TryGetProperty("location", out _));
            Assert.Equal(string.Empty, unmatched.GetProperty("address").GetString());
            Assert.Equal(0, unmatched.GetProperty("score").GetDouble());
            Assert.Equal("U", unmatched.GetProperty("attributes").GetProperty("Status").GetString());
            Assert.Equal(0, unmatched.GetProperty("attributes").GetProperty("X").GetDouble());
        }

        Assert.Equal(3, photon.Asked.Count);
    }

    [Fact]
    public async Task Geocode_addresses_reads_a_posted_form_and_honours_out_fields()
    {
        await using WebApplication app = await ServeAsync(StubPhoton.Json(Collection(Kizilay)), Settings());
        using HttpClient client = app.GetTestClient();

        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["addresses"] = "{\"records\":[{\"attributes\":{\"OBJECTID\":1,\"SingleLine\":\"Kizilay\"}}]}",
            ["outFields"] = "none",
            ["f"] = "json",
        });

        using HttpResponseMessage response = await client.PostAsync(new Uri($"{Root}/geocodeAddresses", UriKind.Relative), form);
        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string[] names = [.. body.GetProperty("locations")[0].GetProperty("attributes").EnumerateObject().Select(p => p.Name)];
        Assert.Equal(["ResultID", "Status", "Score", "Match_addr"], names);
    }

    [Fact]
    public async Task One_record_the_engine_fails_is_that_records_unmatched_answer_not_the_batchs()
    {
        BatchPhoton photon = new(q => q.Contains("broken", StringComparison.Ordinal) ? null : Collection(Kizilay));
        await using WebApplication app = await ServeAsync(photon, Settings());

        string addresses = Records(
            "{\"OBJECTID\":1,\"SingleLine\":\"Kizilay\"}",
            "{\"OBJECTID\":2,\"SingleLine\":\"broken\"}",
            "{\"OBJECTID\":3,\"SingleLine\":\"Kizilay\"}");

        (HttpStatusCode status, JsonElement body) = await GetAsync(app, $"{Root}/geocodeAddresses?addresses={addresses}&f=json");

        Assert.Equal(HttpStatusCode.OK, status);
        string[] statuses =
            [.. body.GetProperty("locations").EnumerateArray().Select(l => l.GetProperty("attributes").GetProperty("Status").GetString()!)];
        Assert.Equal(["M", "U", "M"], statuses);
    }

    [Fact]
    public async Task An_engine_that_is_down_fails_the_batch_with_a_503_after_a_bounded_number_of_tries()
    {
        BatchPhoton photon = new(_ => null);
        await using WebApplication app = await ServeAsync(photon, Settings());

        string addresses = Records(
            [.. Enumerable.Range(1, 60).Select(i => $"{{\"OBJECTID\":{i},\"SingleLine\":\"Ankara {i}\"}}")]);

        (HttpStatusCode status, JsonElement body) = await GetAsync(app, $"{Root}/geocodeAddresses?addresses={addresses}&f=json");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(503, body.GetProperty("error").GetProperty("code").GetInt32());

        // It stopped once the first ones all failed, rather than asking sixty times.
        Assert.True(photon.Asked.Count <= 2 * GeocoderSettings.BatchConcurrency, $"asked {photon.Asked.Count} times");
    }

    [Fact]
    public async Task A_batch_asks_the_engine_at_most_four_at_a_time_and_stops_at_max_batch_size()
    {
        BatchPhoton photon = new(_ => Collection(Kizilay));
        GeocoderSettings settings = Settings() with { MaxBatchSize = 30 };
        await using WebApplication app = await ServeAsync(photon, settings);

        string addresses = Records(
            [.. Enumerable.Range(1, 40).Select(i => $"{{\"OBJECTID\":{i},\"SingleLine\":\"Kizilay {i}\"}}")]);

        (HttpStatusCode status, JsonElement body) = await GetAsync(app, $"{Root}/geocodeAddresses?addresses={addresses}&f=json");

        Assert.Equal(HttpStatusCode.OK, status);

        // The reference: records past MaxBatchSize are not geocoded.
        Assert.Equal(30, body.GetProperty("locations").GetArrayLength());
        Assert.Equal(30, photon.Asked.Count);
        Assert.InRange(photon.MostAtOnce, 1, GeocoderSettings.BatchConcurrency);

        (_, JsonElement service) = await GetAsync(app, $"{Root}?f=json");
        JsonElement properties = service.GetProperty("locatorProperties");
        Assert.Equal(30, properties.GetProperty("MaxBatchSize").GetInt32());
        Assert.Equal(30, properties.GetProperty("SuggestedBatchSize").GetInt32());
    }

    [Fact]
    public async Task Geocode_addresses_refuses_a_record_set_it_cannot_read_with_a_sentence()
    {
        await using WebApplication app = await ServeAsync(StubPhoton.Json(Collection(Kizilay)), Settings());

        foreach (string addresses in (string[])[
                     string.Empty,
                     Uri.EscapeDataString("not json"),
                     Uri.EscapeDataString("{\"rows\":[]}"),
                     Records("{\"SingleLine\":\"Kizilay\"}")])
        {
            (HttpStatusCode status, JsonElement body) = await GetAsync(app, $"{Root}/geocodeAddresses?addresses={addresses}&f=json");

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetProperty("message").GetString()));
        }
    }

    [Fact]
    public async Task A_category_narrows_photon_by_layer_and_filters_what_it_answers()
    {
        StubPhoton photon = StubPhoton.Json(Collection(AnkaraCity, BusStop, Muhurdar));
        await using WebApplication app = await ServeAsync(photon, Settings());

        (HttpStatusCode status, JsonElement body) = await GetAsync(
            app, $"{Root}/findAddressCandidates?SingleLine=Ankara&category=POI&outFields=Addr_type&f=json");

        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement only = Assert.Single(body.GetProperty("candidates").EnumerateArray());
        Assert.Equal("POI", only.GetProperty("attributes").GetProperty("Addr_type").GetString());
        Assert.Contains("layer=house&layer=other", photon.Asked[0].Query, StringComparison.Ordinal);

        // A parent and a child together are their union: a locality and a house-numbered address.
        (_, body) = await GetAsync(
            app, $"{Root}/findAddressCandidates?SingleLine=Ankara&category=Populated%20Place,Point%20Address&outFields=Addr_type&f=json");
        string[] types =
            [.. body.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("attributes").GetProperty("Addr_type").GetString()!).Order()];
        Assert.Equal(["Locality", "PointAddress"], types);

        // Postal has no Photon layer, so nothing is narrowed there and the answer is filtered here.
        await GetAsync(app, $"{Root}/suggest?text=06420&category=Postal&f=json");
        Assert.DoesNotContain("layer=", photon.Asked[^1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_category_that_cannot_be_mapped_is_refused_by_name_not_ignored()
    {
        StubPhoton photon = StubPhoton.Json(Collection(Kizilay));
        await using WebApplication app = await ServeAsync(photon, Settings());

        foreach (string path in (string[])[
                     $"{Root}/findAddressCandidates?SingleLine=Kizilay&category=Coffee%20Shop&f=json",
                     $"{Root}/suggest?text=Kiz&category=Intersection&f=json",
                     $"{Root}/geocodeAddresses?addresses={Records("{\"OBJECTID\":1,\"SingleLine\":\"Kizilay\"}")}&category=City&f=json"])
        {
            (HttpStatusCode status, JsonElement body) = await GetAsync(app, path);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("cannot filter by the category", body.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        Assert.Empty(photon.Asked);
    }

    [Fact]
    public async Task Source_country_is_sent_as_photons_countrycode_and_a_three_letter_code_is_refused()
    {
        StubPhoton photon = StubPhoton.Json(Collection(Kizilay));
        await using WebApplication app = await ServeAsync(photon, Settings());

        (HttpStatusCode status, JsonElement body) =
            await GetAsync(app, $"{Root}/findAddressCandidates?SingleLine=Kizilay&sourceCountry=de&f=json");

        // Photon was told; the stub ignored it, so the answer is filtered here too, and a Turkish place is not German.
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("countrycode=de", photon.Asked[0].Query, StringComparison.Ordinal);
        Assert.Empty(body.GetProperty("candidates").EnumerateArray());

        (_, body) = await GetAsync(app, $"{Root}/findAddressCandidates?SingleLine=Kizilay&sourceCountry=TR&f=json");
        Assert.Single(body.GetProperty("candidates").EnumerateArray());

        (status, body) = await GetAsync(app, $"{Root}/findAddressCandidates?SingleLine=Kizilay&sourceCountry=TUR&f=json");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("alpha-2", body.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Distance_lifts_the_candidates_within_it_above_better_scores_outside_it_and_needs_a_location()
    {
        // For "Kizilay Ankara" Kızılay scores 100 and the city 50. A location at the city's centre with a 500 m radius
        // lifts the city above Kızılay, which is about 1.5 km from it.
        StubPhoton photon = StubPhoton.Json(Collection(Kizilay, AnkaraCity));
        await using WebApplication app = await ServeAsync(photon, Settings());

        (_, JsonElement plain) = await GetAsync(app, $"{Root}/findAddressCandidates?SingleLine=Kizilay%20Ankara&location=32.8597,39.9334&f=json");
        Assert.StartsWith("Kızılay", plain.GetProperty("candidates")[0].GetProperty("address").GetString(), StringComparison.Ordinal);

        (HttpStatusCode status, JsonElement boosted) = await GetAsync(
            app, $"{Root}/findAddressCandidates?SingleLine=Kizilay%20Ankara&location=32.8597,39.9334&distance=500&maxLocations=2&f=json");

        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement[] candidates = [.. boosted.GetProperty("candidates").EnumerateArray()];
        Assert.Equal(2, candidates.Length);
        Assert.Equal("Ankara", candidates[0].GetProperty("address").GetString());

        // Boosted, not filtered: the one outside the radius is still answered. And Photon was asked for more than two,
        // so a near place it ranked lower is there to be lifted.
        Assert.StartsWith("Kızılay", candidates[1].GetProperty("address").GetString(), StringComparison.Ordinal);
        Assert.Contains("limit=20", photon.Asked[^1].Query, StringComparison.Ordinal);

        (status, _) = await GetAsync(app, $"{Root}/findAddressCandidates?SingleLine=Ankara&distance=500&f=json");
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public void The_portal_names_the_locator_only_when_configured_and_only_to_a_caller_it_would_answer()
    {
        RequestPrincipal anonymous = new(Principal.Anonymous, null, Authorization.Nothing);

        Assert.Null(GeocodeServerEndpoints.PortalLocator(null, anonymous, "https://gis.example"));
        Assert.Null(GeocodeServerEndpoints.PortalLocator(Settings(SharingScope.Organization), anonymous, "https://gis.example"));

        Dictionary<string, object?> locator =
            GeocodeServerEndpoints.PortalLocator(Settings(), anonymous, "https://gis.example/studio")!;

        Assert.Equal("https://gis.example/studio/rest/services/Utilities/Geocode/GeocodeServer", locator["url"]);
        Assert.Equal("Ymax", locator["northLat"]);
        Assert.Equal("Ymin", locator["southLat"]);
        Assert.Equal("Xmax", locator["eastLon"]);
        Assert.Equal("Xmin", locator["westLon"]);
        Assert.Equal(true, locator["suggest"]);
        Assert.Equal(true, locator["placefinding"]);
        Assert.Equal(true, locator["batch"]);

        // Without it the Maps SDK's Search widget makes no source of a locator that is not Esri's World one.
        Assert.Equal("SingleLine", locator["singleLineFieldName"]);
    }

    [Fact]
    public void Max_batch_size_is_read_bounded_and_refused_when_unusable()
    {
        static GeocoderSettings? Read(string? value) => GeocoderSettings.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graticula:Geocoding:PhotonUrl"] = "http://127.0.0.1:2322",
                ["Graticula:Geocoding:MaxBatchSize"] = value,
            })
            .Build());

        Assert.Equal(GeocoderSettings.DefaultMaxBatchSize, Read(null)!.MaxBatchSize);
        Assert.Equal(100, Read(null)!.SuggestedBatchSize);
        Assert.Equal(1000, Read("1000")!.MaxBatchSize);
        Assert.Equal(40, Read("40")!.SuggestedBatchSize);

        foreach (string bad in (string[])["0", "-3", "2001", "lots"])
        {
            Assert.Throws<InvalidOperationException>(() => Read(bad));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geocoding;

namespace Graticula.Host.Geocoding;

/// <summary>
/// The <see cref="IGeocoder"/> over a Photon the operator runs — ADR-258.
/// </summary>
/// <remarks>
/// <para>
/// <b>Photon is a separate HTTP service, not a library</b> (komoot/photon, Apache-2.0, Java, over an
/// OpenStreetMap index). Nothing of it is linked or shipped; this adapter speaks its two documented
/// endpoints — <c>/api?q=…</c> and <c>/reverse?lon=…&amp;lat=…</c> — and reads the GeoJSON they answer with.
/// </para>
/// <para>
/// <b>Bounded both ways.</b> Every request has a deadline (<see cref="GeocoderSettings.Timeout"/>), and an
/// answer larger than <see cref="MaximumResponseBytes"/> is refused rather than read: a geocoder answering
/// fifty places writes tens of kilobytes, and a configured address that turned out to be something else
/// must not be able to make this process buffer whatever it sends. Redirects are not followed, so the
/// configured address is the only one this ever reaches.
/// </para>
/// <para>
/// <b>Every failure is one exception</b>, <see cref="GeocoderUnavailableException"/>, with a sentence that
/// names the cause and never the engine's body.
/// </para>
/// </remarks>
internal sealed class PhotonGeocoder : IGeocoder, IDisposable
{
    /// <summary>The largest answer read from Photon.</summary>
    public const int MaximumResponseBytes = 2 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly GeocoderSettings _settings;

    /// <summary>Creates the adapter.</summary>
    /// <param name="settings">Where Photon is and how long to wait for it.</param>
    /// <param name="handler">The handler requests go through; a fresh socket handler when null. Tests pass a fake.</param>
    public PhotonGeocoder(GeocoderSettings settings, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = settings.Timeout,
            MaxResponseContentBufferSize = MaximumResponseBytes,
        };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GeocodePlace>> SearchAsync(
        GeocodeSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);

        // <b>A filter Photon is given is also applied to what it answers</b>, because the port promises only places of
        // the kinds and countries asked for, and Photon's layers are coarser than our kinds in one place (a point of
        // interest and an address are both its `house`) and absent in another (it has no postcode layer).
        bool filtered = search.Kinds is { Count: > 0 } || search.Countries is { Count: > 0 };
        int limit = filtered ? Math.Min(MaximumFilteredLimit, Math.Max(search.Limit * 2, 10)) : search.Limit;

        StringBuilder query = new("api?q=");
        query.Append(Uri.EscapeDataString(search.Text));
        query.Append("&limit=").Append(limit.ToString(CultureInfo.InvariantCulture));

        if (search.Near is { } near)
        {
            query.Append("&lat=").Append(Number(near.Latitude));
            query.Append("&lon=").Append(Number(near.Longitude));
        }

        if (search.Within is { } box)
        {
            query.Append("&bbox=")
                .Append(Number(box.West)).Append(',')
                .Append(Number(box.South)).Append(',')
                .Append(Number(box.East)).Append(',')
                .Append(Number(box.North));
        }

        AppendLanguage(query, search.Language);

        if (Layers(search.Kinds) is { } layers)
        {
            foreach (string layer in layers)
            {
                query.Append("&layer=").Append(layer);
            }
        }

        if (search.Countries is { Count: > 0 } countries)
        {
            foreach (string country in countries.Order(StringComparer.Ordinal))
            {
                query.Append("&countrycode=").Append(Uri.EscapeDataString(country.ToLowerInvariant()));
            }
        }

        using JsonDocument answer = await GetAsync(query.ToString(), cancellationToken).ConfigureAwait(false);

        IReadOnlyList<GeocodePlace> places = Places(answer.RootElement);

        if (!filtered)
        {
            return places;
        }

        return
        [
            .. places
                .Where(place => search.Kinds is not { Count: > 0 } kinds || kinds.Contains(place.Kind))
                .Where(place => search.Countries is not { Count: > 0 } codes
                    || (place.CountryCode is { } code && codes.Contains(code)))
                .Take(search.Limit),
        ];
    }

    /// <summary>The most places asked of Photon when its answer will be filtered here as well.</summary>
    public const int MaximumFilteredLimit = 50;

    /// <summary>
    /// Photon's <c>layer</c> values covering <paramref name="kinds"/>, or null when they cannot be narrowed there.
    /// </summary>
    /// <remarks>
    /// Photon 1.x documents the layers <c>house</c>, <c>street</c>, <c>locality</c>, <c>district</c>, <c>city</c>,
    /// <c>county</c>, <c>state</c>, <c>country</c> and <c>other</c>. A point of interest is a <c>house</c> when it has a
    /// number and <c>other</c> when it is a natural or unaddressed feature; a postcode has no layer of its own, so a
    /// search that wants postcodes is not narrowed at Photon at all and is filtered here.
    /// </remarks>
    internal static IReadOnlyList<string>? Layers(IReadOnlySet<GeocodePlaceKind>? kinds)
    {
        if (kinds is not { Count: > 0 } || kinds.Contains(GeocodePlaceKind.Postcode))
        {
            return null;
        }

        SortedSet<string> layers = new(StringComparer.Ordinal);

        foreach (GeocodePlaceKind kind in kinds)
        {
            string[] these = kind switch
            {
                GeocodePlaceKind.House => ["house"],
                GeocodePlaceKind.PointOfInterest => ["house", "other"],
                GeocodePlaceKind.Street => ["street"],
                _ => ["locality", "district", "city", "county", "state", "country"],
            };

            layers.UnionWith(these);
        }

        return [.. layers];
    }

    /// <inheritdoc/>
    public async Task<GeocodePlace?> ReverseAsync(
        double longitude,
        double latitude,
        double? radiusMeters,
        string? language,
        CancellationToken cancellationToken)
    {
        StringBuilder query = new("reverse?lon=");
        query.Append(Number(longitude)).Append("&lat=").Append(Number(latitude)).Append("&limit=1");

        // Photon's radius is in kilometres, ArcGIS's distance in metres.
        if (radiusMeters is > 0 and var meters)
        {
            query.Append("&radius=").Append(Number(meters / 1000.0));
        }

        AppendLanguage(query, language);

        using JsonDocument answer = await GetAsync(query.ToString(), cancellationToken).ConfigureAwait(false);

        IReadOnlyList<GeocodePlace> places = Places(answer.RootElement);

        return places.Count > 0 ? places[0] : null;
    }

    /// <summary>Asks for a language only when Photon's index was built with it — otherwise Photon refuses.</summary>
    private void AppendLanguage(StringBuilder query, string? language)
    {
        if (language is { Length: >= 2 } asked
            && _settings.Languages.Contains(asked[..2].ToLowerInvariant()))
        {
            query.Append("&lang=").Append(asked[..2].ToLowerInvariant());
        }
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private async Task<JsonDocument> GetAsync(string relative, CancellationToken cancellationToken)
    {
        Uri address = new(_settings.PhotonUrl, relative);

        HttpResponseMessage response;

        try
        {
            response = await _http.GetAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GeocoderUnavailableException(
                $"The geocoder did not answer within {_settings.Timeout.TotalSeconds:0.#} seconds.", e);
        }
        catch (HttpRequestException e)
        {
            // Too large is an HttpRequestException too; its message says so and carries no body.
            throw new GeocoderUnavailableException($"The geocoder could not be reached: {e.Message}", e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new GeocoderUnavailableException(
                    $"The geocoder answered HTTP {(int)response.StatusCode}.");
            }

            try
            {
                byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                return JsonDocument.Parse(body);
            }
            catch (JsonException e)
            {
                throw new GeocoderUnavailableException("The geocoder's answer was not JSON.", e);
            }
            catch (HttpRequestException e)
            {
                throw new GeocoderUnavailableException($"The geocoder's answer could not be read: {e.Message}", e);
            }
        }
    }

    /// <summary>The places in a Photon GeoJSON feature collection.</summary>
    /// <param name="root">The answer.</param>
    /// <returns>The places, in Photon's order.</returns>
    /// <exception cref="GeocoderUnavailableException">The answer is not a feature collection.</exception>
    internal static IReadOnlyList<GeocodePlace> Places(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("features", out JsonElement features)
            || features.ValueKind != JsonValueKind.Array)
        {
            throw new GeocoderUnavailableException("The geocoder's answer was not a GeoJSON feature collection.");
        }

        List<GeocodePlace> places = [];

        foreach (JsonElement feature in features.EnumerateArray())
        {
            if (Place(feature) is { } place)
            {
                places.Add(place);
            }
        }

        return places;
    }

    /// <summary>One Photon feature as a place, or null when it has no point.</summary>
    private static GeocodePlace? Place(JsonElement feature)
    {
        if (feature.ValueKind != JsonValueKind.Object
            || !feature.TryGetProperty("geometry", out JsonElement geometry)
            || geometry.ValueKind != JsonValueKind.Object
            || !geometry.TryGetProperty("coordinates", out JsonElement coordinates)
            || coordinates.ValueKind != JsonValueKind.Array
            || coordinates.GetArrayLength() < 2
            || !coordinates[0].TryGetDouble(out double longitude)
            || !coordinates[1].TryGetDouble(out double latitude))
        {
            return null;
        }

        JsonElement properties = feature.TryGetProperty("properties", out JsonElement p)
            && p.ValueKind == JsonValueKind.Object
            ? p
            : default;

        string? Text(string name) =>
            properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty(name, out JsonElement value)
                ? value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() is { Length: > 0 } s ? s : null,
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null,
                }
                : null;

        string? houseNumber = Text("housenumber");
        string? osmKey = Text("osm_key");
        string? osmValue = Text("osm_value");
        string? type = Text("type");

        string? key = (Text("osm_type"), Text("osm_id")) is ({ } t, { } id) ? t + id : null;

        return new GeocodePlace(longitude, latitude, Kind(type, osmKey, osmValue, houseNumber, Text("name")), key)
        {
            Name = Text("name"),
            HouseNumber = houseNumber,
            Street = Text("street"),
            Neighborhood = Text("locality"),
            District = Text("district"),
            City = Text("city"),
            County = Text("county"),
            State = Text("state"),
            Postcode = Text("postcode"),
            Country = Text("country"),
            CountryCode = Text("countrycode")?.ToUpperInvariant(),
            Category = osmKey is null ? null : osmValue is null ? osmKey : $"{osmKey}={osmValue}",
            Extent = Extent(properties),
        };
    }

    /// <summary>What a Photon feature is, from its <c>type</c>, its OpenStreetMap tag and whether it has a number.</summary>
    /// <remarks>
    /// Photon's <c>type</c> is one of <c>house</c>, <c>street</c>, <c>locality</c>, <c>district</c>,
    /// <c>city</c>, <c>county</c>, <c>state</c>, <c>country</c> and <c>other</c>. A named thing that is not a
    /// building or a place — a shop, a stop, a school — is a point of interest even when it has a number, as
    /// ArcGIS's POI is; a building or an unnamed thing with a number is an address.
    /// </remarks>
    internal static GeocodePlaceKind Kind(string? type, string? osmKey, string? osmValue, string? houseNumber, string? name)
    {
        if (string.Equals(osmValue, "postcode", StringComparison.Ordinal)
            || string.Equals(osmKey, "postcode", StringComparison.Ordinal))
        {
            return GeocodePlaceKind.Postcode;
        }

        if (type == "street")
        {
            return GeocodePlaceKind.Street;
        }

        if (osmKey is "place" or "boundary"
            || type is "locality" or "district" or "city" or "county" or "state" or "country")
        {
            return GeocodePlaceKind.Locality;
        }

        bool named = name is not null && osmKey is not "building";

        return houseNumber is not null && !named ? GeocodePlaceKind.House : GeocodePlaceKind.PointOfInterest;
    }

    /// <summary>
    /// Photon's <c>extent</c> — four numbers, west, north, east, south — as a box, or null.
    /// </summary>
    /// <remarks>Read by minimum and maximum rather than by position, so the order the four come in does not matter.</remarks>
    private static GeocodeBox? Extent(JsonElement properties)
    {
        if (properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty("extent", out JsonElement extent)
            || extent.ValueKind != JsonValueKind.Array
            || extent.GetArrayLength() != 4)
        {
            return null;
        }

        double[] v = new double[4];

        for (int i = 0; i < 4; i++)
        {
            if (!extent[i].TryGetDouble(out v[i]))
            {
                return null;
            }
        }

        return new GeocodeBox(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();
}

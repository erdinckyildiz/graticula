using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Graticula.Geocoding;

namespace Graticula.Api.ArcGis;

/// <summary>
/// The documents a GeocodeServer answers with, from places an <see cref="IGeocoder"/> found — ADR-258.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from ArcGIS's published REST API reference for the geocode service</b> — the service resource,
/// <c>findAddressCandidates</c>, <c>suggest</c> and <c>reverseGeocode</c> — which is documentation of a wire
/// format, the thing a compatibility surface is allowed to implement (CLAUDE.md §5). The attribute names are
/// the documented output fields; which of them this server fills, and from what, is the table in ADR-258 §5.
/// </para>
/// <para>
/// <b>Pure, so the mapping is tested without an engine or a socket.</b> The endpoint projects and asks; this
/// only spells.
/// </para>
/// </remarks>
public static class GeocodeServerWriter
{
    /// <summary>The single-line input field, as ArcGIS names it.</summary>
    public const string SingleLineField = "SingleLine";

    /// <summary>The OpenStreetMap attribution the data's licence (ODbL) asks for, carried in <c>copyrightText</c>.</summary>
    public const string Attribution = "© OpenStreetMap contributors";

    /// <summary>The most candidates one request returns, whatever <c>maxLocations</c> asks — ArcGIS's own ceiling.</summary>
    public const int MaximumLocations = 50;

    /// <summary>Candidates returned when <c>maxLocations</c> is not sent.</summary>
    public const int DefaultLocations = 20;

    /// <summary>The most suggestions one request returns — ArcGIS's documented ceiling.</summary>
    public const int MaximumSuggestions = 15;

    /// <summary>Suggestions returned when <c>maxSuggestions</c> is not sent — ArcGIS's documented default.</summary>
    public const int DefaultSuggestions = 5;

    /// <summary>
    /// The structured address fields a request may send instead of <see cref="SingleLineField"/>, in the order
    /// they are joined into one line.
    /// </summary>
    /// <remarks>
    /// <b>Joined, not matched field by field — INFERRED (ADR-258 §5).</b> Photon takes free text; a structured
    /// request is turned into the line a person would have typed.
    /// </remarks>
    public static IReadOnlyList<string> AddressFields { get; } =
        ["Address", "Neighborhood", "City", "Subregion", "Region", "Postal", "CountryCode"];

    /// <summary>Every attribute a candidate can carry, in the order they are written.</summary>
    public static IReadOnlyList<(string Name, string Type, string Alias)> CandidateFields { get; } =
    [
        ("Loc_name", "esriFieldTypeString", "Loc_name"),
        ("Status", "esriFieldTypeString", "Status"),
        ("Score", "esriFieldTypeDouble", "Score"),
        ("Match_addr", "esriFieldTypeString", "Match_addr"),
        ("LongLabel", "esriFieldTypeString", "LongLabel"),
        ("ShortLabel", "esriFieldTypeString", "ShortLabel"),
        ("Addr_type", "esriFieldTypeString", "Addr_type"),
        ("Type", "esriFieldTypeString", "Type"),
        ("PlaceName", "esriFieldTypeString", "PlaceName"),
        ("AddNum", "esriFieldTypeString", "AddNum"),
        ("StName", "esriFieldTypeString", "StName"),
        ("Nbrhd", "esriFieldTypeString", "Nbrhd"),
        ("District", "esriFieldTypeString", "District"),
        ("City", "esriFieldTypeString", "City"),
        ("Subregion", "esriFieldTypeString", "Subregion"),
        ("Region", "esriFieldTypeString", "Region"),
        ("Postal", "esriFieldTypeString", "Postal"),
        ("CntryName", "esriFieldTypeString", "CntryName"),
        ("CountryCode", "esriFieldTypeString", "CountryCode"),
        ("Distance", "esriFieldTypeDouble", "Distance"),
        ("X", "esriFieldTypeDouble", "X"),
        ("Y", "esriFieldTypeDouble", "Y"),
        ("DisplayX", "esriFieldTypeDouble", "DisplayX"),
        ("DisplayY", "esriFieldTypeDouble", "DisplayY"),
        ("Xmin", "esriFieldTypeDouble", "Xmin"),
        ("Xmax", "esriFieldTypeDouble", "Xmax"),
        ("Ymin", "esriFieldTypeDouble", "Ymin"),
        ("Ymax", "esriFieldTypeDouble", "Ymax"),
        ("Osm_key", "esriFieldTypeString", "Osm_key"),
    ];

    /// <summary>The attributes a candidate carries when <c>outFields</c> is not sent.</summary>
    /// <remarks>INFERRED (ADR-258 §5): the reference describes the default as the minimum; these two are it.</remarks>
    public static IReadOnlyList<string> DefaultOutFields { get; } = ["Score", "Match_addr"];

    /// <summary>The service resource.</summary>
    /// <param name="currentVersion">The REST version this server reports.</param>
    /// <param name="engine">What answers, for the description — for instance <c>Photon</c>.</param>
    /// <returns>The document.</returns>
    public static Dictionary<string, object?> ServiceDocument(double currentVersion, string engine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engine);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["currentVersion"] = currentVersion,
            ["serviceDescription"] =
                $"Finds places by name and names places by position, answered by a {engine} geocoder over "
                + "OpenStreetMap data that this server's operator runs. Places, streets and districts are found "
                + "well; house numbers only where OpenStreetMap has them.",
            ["addressFields"] = AddressFields.Select(name => Field(name, "esriFieldTypeString", name, 100)).ToArray(),
            ["singleLineAddressField"] = Field(SingleLineField, "esriFieldTypeString", "Single Line Input", 200),
            ["candidateFields"] = CandidateFields.Select(f => Field(f.Name, f.Type, f.Alias, f.Type == "esriFieldTypeString" ? 200 : null)).ToArray(),
            ["intersectionCandidateFields"] = Array.Empty<object>(),
            ["spatialReference"] = SpatialReference(4326),
            ["locatorProperties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["UICLIDs"] = string.Empty,
                ["SuggestedBatchSize"] = 0,
                ["MaxBatchSize"] = 0,
                ["MaxResultSize"] = MaximumLocations,
                ["WriteXYCoordFields"] = "TRUE",
                ["WriteStandardizedAddressField"] = "FALSE",
                ["WriteReferenceIDField"] = "FALSE",
                ["WritePercentAlongField"] = "FALSE",
            },
            ["capabilities"] = "Geocode,ReverseGeocode,Suggest",
            ["copyrightText"] = Attribution,
        };
    }

    /// <summary>A field description, as the service resource lists them.</summary>
    private static Dictionary<string, object?> Field(string name, string type, string alias, int? length)
    {
        Dictionary<string, object?> field = new(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["type"] = type,
            ["alias"] = alias,
            ["required"] = false,
        };

        if (length is { } l)
        {
            field["length"] = l;
        }

        return field;
    }

    /// <summary>A spatial reference, with <c>latestWkid</c> where the code has a newer spelling.</summary>
    /// <param name="wkid">The code as the request named it.</param>
    /// <returns>The object.</returns>
    public static Dictionary<string, object?> SpatialReference(int wkid) =>
        new(StringComparer.Ordinal)
        {
            ["wkid"] = wkid,
            ["latestWkid"] = wkid is 102100 or 102113 ? 3857 : wkid,
        };

    /// <summary>What ArcGIS's <c>Addr_type</c> calls a place of this kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The match type.</returns>
    public static string MatchType(GeocodePlaceKind kind) => kind switch
    {
        GeocodePlaceKind.House => "PointAddress",
        GeocodePlaceKind.PointOfInterest => "POI",
        GeocodePlaceKind.Street => "StreetName",
        GeocodePlaceKind.Postcode => "Postal",
        _ => "Locality",
    };

    /// <summary>The street and house number as one phrase, or whichever of the two there is.</summary>
    private static string? StreetLine(GeocodePlace place) =>
        (place.Street, place.HouseNumber) switch
        {
            ({ Length: > 0 } street, { Length: > 0 } number) => $"{street} {number}",
            ({ Length: > 0 } street, _) => street,
            _ => null,
        };

    /// <summary>The place's labels: the long one, the one without the country, and the short one.</summary>
    /// <param name="place">The place.</param>
    /// <returns>
    /// <c>LongLabel</c> — everything from the most particular part to the country; <c>Match_addr</c> — the same
    /// without the country; <c>ShortLabel</c> — the first part alone.
    /// </returns>
    /// <remarks>
    /// <b>Most particular first, and a part equal to the one before it is said once</b> — OpenStreetMap often
    /// names a district and its county alike (<i>Çankaya, Çankaya</i>), and a label that repeats itself reads as
    /// a fault. The order follows the reference's examples: name, street and number, neighbourhood, district,
    /// city, province, postal code, country.
    /// </remarks>
    public static (string Long, string Match, string Short) Labels(GeocodePlace place)
    {
        ArgumentNullException.ThrowIfNull(place);

        string? street = StreetLine(place);

        // A street found as itself is named by its street; its own name is the same word.
        string? name = place.Kind == GeocodePlaceKind.Street && street is not null ? null : place.Name;

        List<string> parts = [];

        void Add(string? part)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                return;
            }

            string trimmed = part.Trim();

            if (parts.Any(p => string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            parts.Add(trimmed);
        }

        Add(name);
        Add(street);
        Add(place.Neighborhood);
        Add(place.District);
        Add(place.City);
        Add(place.County);
        Add(place.State);
        Add(place.Postcode);

        string match = string.Join(", ", parts);

        Add(place.Country);

        string longLabel = string.Join(", ", parts);
        string shortLabel = parts.Count > 0 ? parts[0] : string.Empty;

        return (longLabel, match.Length > 0 ? match : longLabel, shortLabel);
    }

    /// <summary>The key a suggestion carries and <c>findAddressCandidates</c> recognises, or null.</summary>
    /// <param name="place">The place.</param>
    /// <returns>The magic key.</returns>
    public static string? MagicKey(GeocodePlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        return place.Key;
    }

    /// <summary>How well a place answers a request, from 0 to 100 — ADR-258 §5, INFERRED.</summary>
    /// <param name="request">What was asked, as one line.</param>
    /// <param name="place">What was found.</param>
    /// <returns>The score, rounded to two places.</returns>
    /// <remarks>
    /// <para>
    /// <b>The share of the request's words that the place's long label carries.</b> Photon ranks and does not
    /// score, and ArcGIS's score is a 0–100 measure of how completely the input was matched. The nearest honest
    /// reading is coverage: each word of the request counts once, matched when a word of the label equals it or
    /// begins with it — so <i>cad</i> finds <i>Caddesi</i> and <i>Ank</i> finds <i>Ankara</i>. Case, and the
    /// letters Turkish and other Latin alphabets add (ç, ğ, ı, ö, ş, ü, accents), are folded first, so
    /// <i>Kizilay</i> matches <i>Kızılay</i> as Photon itself finds it.
    /// </para>
    /// <para>
    /// <b>What it means to a client</b>: a request whose every word is in the answer scores 100, which is what
    /// Pro's automatic match looks for; <i>Atatürk Bulvarı 100 Ankara</i> answered by a bus stop on the street
    /// scores 75, because the house number was not found — which is the truth about that answer.
    /// </para>
    /// </remarks>
    public static double Score(string request, GeocodePlace place)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(place);

        string[] asked = Words(request);

        if (asked.Length == 0)
        {
            return 0;
        }

        HashSet<string> label = [.. Words(Labels(place).Long)];

        if (place.CountryCode is { Length: > 0 } code)
        {
            label.Add(Fold(code));
        }

        int matched = asked.Count(word => label.Any(l => l.StartsWith(word, StringComparison.Ordinal)));

        return Math.Round(100.0 * matched / asked.Length, 2);
    }

    /// <summary>The words of a text, folded for comparison.</summary>
    internal static string[] Words(string text)
    {
        StringBuilder folded = new(text.Length);

        foreach (char c in text)
        {
            folded.Append(char.IsLetterOrDigit(c) ? FoldChar(c) : " ");
        }

        return folded.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>A text folded for comparison.</summary>
    internal static string Fold(string text)
    {
        StringBuilder folded = new(text.Length);

        foreach (char c in text)
        {
            folded.Append(FoldChar(c));
        }

        return folded.ToString();
    }

    /// <summary>
    /// One character, lower-cased and without its diacritic.
    /// </summary>
    /// <remarks>
    /// <b>A table rather than Unicode decomposition</b>: the server runs with invariant globalization, and the
    /// dotless <i>ı</i> has no decomposition at all — it is a letter of its own that Turkish users type as
    /// <i>i</i>. The table covers the Latin letters with diacritics an OpenStreetMap label in Europe and Türkiye
    /// carries; a letter not in it is compared as itself.
    /// </remarks>
    private static string FoldChar(char c)
    {
        char lower = char.ToLowerInvariant(c);

        return lower switch
        {
            'ı' or 'İ' or 'í' or 'ì' or 'î' or 'ï' => "i",
            'ç' or 'ć' or 'č' => "c",
            'ğ' => "g",
            'ö' or 'ó' or 'ò' or 'ô' or 'õ' or 'ø' => "o",
            'ş' or 'ś' or 'š' or 'ș' => "s",
            'ü' or 'ú' or 'ù' or 'û' => "u",
            'â' or 'á' or 'à' or 'ä' or 'ã' or 'å' or 'ā' => "a",
            'é' or 'è' or 'ê' or 'ë' or 'ē' or 'ę' => "e",
            'ñ' or 'ń' => "n",
            'ž' or 'ź' or 'ż' => "z",
            'ł' => "l",
            'ß' => "ss",
            _ => c == 'I' ? "i" : lower.ToString(),
        };
    }

    /// <summary>Every attribute this server can give a candidate.</summary>
    /// <param name="place">The place.</param>
    /// <param name="score">Its score.</param>
    /// <param name="x">Its x in the answer's reference.</param>
    /// <param name="y">Its y in the answer's reference.</param>
    /// <param name="extent">Its extent in the answer's reference.</param>
    /// <param name="distanceMeters">How far it is from the request's <c>location</c>, or null when none was sent.</param>
    /// <returns>The attributes, in <see cref="CandidateFields"/> order.</returns>
    public static Dictionary<string, object?> Attributes(
        GeocodePlace place,
        double score,
        double x,
        double y,
        GeocodeBox extent,
        double? distanceMeters)
    {
        ArgumentNullException.ThrowIfNull(place);

        (string longLabel, string match, string shortLabel) = Labels(place);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Loc_name"] = "Photon",
            ["Status"] = score >= 100 ? "M" : score > 0 ? "T" : "U",
            ["Score"] = score,
            ["Match_addr"] = match,
            ["LongLabel"] = longLabel,
            ["ShortLabel"] = shortLabel,
            ["Addr_type"] = MatchType(place.Kind),
            ["Type"] = place.Category is { } category && category.IndexOf('=', StringComparison.Ordinal) is var at and > 0
                ? category[(at + 1)..]
                : string.Empty,
            ["PlaceName"] = place.Kind is GeocodePlaceKind.PointOfInterest or GeocodePlaceKind.Locality
                ? place.Name ?? string.Empty
                : string.Empty,
            ["AddNum"] = place.HouseNumber ?? string.Empty,
            ["StName"] = place.Street ?? string.Empty,
            ["Nbrhd"] = place.Neighborhood ?? string.Empty,
            ["District"] = place.District ?? string.Empty,
            ["City"] = place.City ?? string.Empty,
            ["Subregion"] = place.County ?? string.Empty,
            ["Region"] = place.State ?? string.Empty,
            ["Postal"] = place.Postcode ?? string.Empty,
            ["CntryName"] = place.Country ?? string.Empty,
            ["CountryCode"] = place.CountryCode ?? string.Empty,
            ["Distance"] = distanceMeters is { } d ? Math.Round(d, 2) : 0.0,
            ["X"] = x,
            ["Y"] = y,
            ["DisplayX"] = x,
            ["DisplayY"] = y,
            ["Xmin"] = extent.West,
            ["Xmax"] = extent.East,
            ["Ymin"] = extent.South,
            ["Ymax"] = extent.North,
            ["Osm_key"] = place.Category ?? string.Empty,
        };
    }

    /// <summary>The attributes <c>outFields</c> asked for.</summary>
    /// <param name="all">Every attribute.</param>
    /// <param name="outFields">The parameter as sent, or null.</param>
    /// <returns>The chosen ones, in the order they are held; names not held are skipped.</returns>
    public static Dictionary<string, object?> Choose(IReadOnlyDictionary<string, object?> all, string? outFields)
    {
        ArgumentNullException.ThrowIfNull(all);

        IEnumerable<string> wanted = string.IsNullOrWhiteSpace(outFields)
            ? DefaultOutFields
            : outFields.Trim() == "*"
                ? all.Keys
                : outFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        HashSet<string> named = new(wanted, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, object?> chosen = new(StringComparer.Ordinal);

        foreach ((string key, object? value) in all)
        {
            if (named.Contains(key))
            {
                chosen[key] = value;
            }
        }

        return chosen;
    }

    /// <summary>The <c>address</c> object <c>reverseGeocode</c> answers with.</summary>
    /// <param name="place">The place found.</param>
    /// <returns>The address.</returns>
    public static Dictionary<string, object?> ReverseAddress(GeocodePlace place)
    {
        ArgumentNullException.ThrowIfNull(place);

        (string longLabel, string match, string shortLabel) = Labels(place);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Match_addr"] = match,
            ["LongLabel"] = longLabel,
            ["ShortLabel"] = shortLabel,
            ["Addr_type"] = MatchType(place.Kind),
            ["Type"] = place.Category is { } category && category.IndexOf('=', StringComparison.Ordinal) is var at and > 0
                ? category[(at + 1)..]
                : string.Empty,
            ["PlaceName"] = place.Kind is GeocodePlaceKind.PointOfInterest or GeocodePlaceKind.Locality
                ? place.Name ?? string.Empty
                : string.Empty,
            ["AddNum"] = place.HouseNumber ?? string.Empty,
            ["Address"] = StreetLine(place) ?? string.Empty,
            ["Neighborhood"] = place.Neighborhood ?? string.Empty,
            ["District"] = place.District ?? string.Empty,
            ["City"] = place.City ?? string.Empty,
            ["Subregion"] = place.County ?? string.Empty,
            ["Region"] = place.State ?? string.Empty,
            ["Postal"] = place.Postcode ?? string.Empty,
            ["CntryName"] = place.Country ?? string.Empty,
            ["CountryCode"] = place.CountryCode ?? string.Empty,
        };
    }

    /// <summary>
    /// The extent a place is given when the engine gave none — ADR-258 §5, INFERRED.
    /// </summary>
    /// <param name="place">The place.</param>
    /// <returns>Its extent in longitude and latitude.</returns>
    /// <remarks>
    /// <b>A box sized by what the place is</b>, because the reference's candidates always carry one and a client
    /// zooms to it: about 200 m across for an address or a point of interest, a kilometre for a street, four for
    /// a locality or a postal code.
    /// </remarks>
    public static GeocodeBox ExtentOf(GeocodePlace place)
    {
        ArgumentNullException.ThrowIfNull(place);

        if (place.Extent is { } given)
        {
            return given;
        }

        double half = place.Kind switch
        {
            GeocodePlaceKind.House or GeocodePlaceKind.PointOfInterest => 0.001,
            GeocodePlaceKind.Street => 0.005,
            _ => 0.02,
        };

        return new GeocodeBox(
            place.Longitude - half, place.Latitude - half, place.Longitude + half, place.Latitude + half);
    }

    /// <summary>The great-circle distance between two positions, in metres.</summary>
    /// <param name="fromLongitude">From, longitude.</param>
    /// <param name="fromLatitude">From, latitude.</param>
    /// <param name="toLongitude">To, longitude.</param>
    /// <param name="toLatitude">To, latitude.</param>
    /// <returns>Metres, on a sphere of the WGS 84 mean radius.</returns>
    public static double DistanceMeters(double fromLongitude, double fromLatitude, double toLongitude, double toLatitude)
    {
        const double Radius = 6_371_008.8;
        double ToRadians(double degrees) => degrees * Math.PI / 180.0;

        double dLat = ToRadians(toLatitude - fromLatitude);
        double dLon = ToRadians(toLongitude - fromLongitude);
        double a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
            + (Math.Cos(ToRadians(fromLatitude)) * Math.Cos(ToRadians(toLatitude))
               * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));

        return 2 * Radius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Geocoding;

/// <summary>
/// Finds places by name and names places by position — the port an external geocoding engine sits behind
/// (ADR-258).
/// </summary>
/// <remarks>
/// <para>
/// <b>A port, because the engine is somebody else's.</b> The first and so far only implementation is an
/// adapter onto a Photon the operator runs (komoot/photon, over OpenStreetMap); build-vs-adopt-policy.md §4
/// keeps an adopted engine behind an interface of ours, so nothing about Photon's wire format reaches the
/// ArcGIS surface. Q-84's own locator over a customer's address table would be a second implementation of
/// this, not a second surface.
/// </para>
/// <para>
/// <b>Coordinates are longitude and latitude in WGS 84</b>, in and out. A caller that holds another
/// reference projects before and after; an engine that answered in anything else would be the adapter's
/// problem to hide.
/// </para>
/// </remarks>
public interface IGeocoder
{
    /// <summary>Places matching a text, best first as the engine ranks them.</summary>
    /// <param name="search">What to look for and how to narrow it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The places, at most <see cref="GeocodeSearch.Limit"/> of them; empty when none match.</returns>
    /// <exception cref="GeocoderUnavailableException">The engine could not be asked or did not answer.</exception>
    Task<IReadOnlyList<GeocodePlace>> SearchAsync(GeocodeSearch search, CancellationToken cancellationToken);

    /// <summary>The place nearest a position, or null when the engine knows nothing near it.</summary>
    /// <param name="longitude">Longitude, degrees.</param>
    /// <param name="latitude">Latitude, degrees.</param>
    /// <param name="radiusMeters">How far to look, or null for the engine's own default.</param>
    /// <param name="language">The language names should be in, or null for the engine's default.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The place, or null.</returns>
    /// <exception cref="GeocoderUnavailableException">The engine could not be asked or did not answer.</exception>
    Task<GeocodePlace?> ReverseAsync(
        double longitude,
        double latitude,
        double? radiusMeters,
        string? language,
        CancellationToken cancellationToken);
}

/// <summary>A text search.</summary>
/// <param name="Text">What to look for, as one line.</param>
/// <param name="Limit">How many places at most.</param>
/// <param name="Near">A position to prefer places near — longitude, latitude — or null.</param>
/// <param name="Within">A box places must fall in, or null.</param>
/// <param name="Language">The language names should be in, or null for the engine's default.</param>
/// <param name="Kinds">
/// The kinds of place wanted, or null for any. An implementation answers only places of these kinds — narrowing
/// at the engine where it can and filtering what it answered where it cannot.
/// </param>
/// <param name="Countries">
/// The countries places must be in, as upper-case ISO 3166-1 alpha-2 codes, or null for anywhere.
/// </param>
public sealed record GeocodeSearch(
    string Text,
    int Limit,
    (double Longitude, double Latitude)? Near = null,
    GeocodeBox? Within = null,
    string? Language = null,
    IReadOnlySet<GeocodePlaceKind>? Kinds = null,
    IReadOnlySet<string>? Countries = null);

/// <summary>A box in longitude and latitude.</summary>
/// <param name="West">Smallest longitude.</param>
/// <param name="South">Smallest latitude.</param>
/// <param name="East">Largest longitude.</param>
/// <param name="North">Largest latitude.</param>
public readonly record struct GeocodeBox(double West, double South, double East, double North);

/// <summary>What a place is, coarsely — the distinction a client acts on when it zooms or labels.</summary>
public enum GeocodePlaceKind
{
    /// <summary>An address with a house number.</summary>
    House,

    /// <summary>A named thing that is not a street or an administrative area: a shop, a stop, a building.</summary>
    PointOfInterest,

    /// <summary>A street, as a whole.</summary>
    Street,

    /// <summary>A neighbourhood, a district, a town, a province or a country.</summary>
    Locality,

    /// <summary>A postal code area.</summary>
    Postcode,
}

/// <summary>One place an engine found.</summary>
/// <param name="Longitude">Where it is.</param>
/// <param name="Latitude">Where it is.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Key">
/// The engine's stable name for it — for Photon, the OpenStreetMap element type and id such as <c>W123</c> —
/// or null when it has none. Lets a later search recognise the same place.
/// </param>
public sealed record GeocodePlace(double Longitude, double Latitude, GeocodePlaceKind Kind, string? Key)
{
    /// <summary>Its own name, when it has one: a building, a shop, a suburb.</summary>
    public string? Name { get; init; }

    /// <summary>The house number.</summary>
    public string? HouseNumber { get; init; }

    /// <summary>The street.</summary>
    public string? Street { get; init; }

    /// <summary>The neighbourhood or locality.</summary>
    public string? Neighborhood { get; init; }

    /// <summary>The district.</summary>
    public string? District { get; init; }

    /// <summary>The city or town.</summary>
    public string? City { get; init; }

    /// <summary>The county, or the second administrative level.</summary>
    public string? County { get; init; }

    /// <summary>The state or province.</summary>
    public string? State { get; init; }

    /// <summary>The postal code.</summary>
    public string? Postcode { get; init; }

    /// <summary>The country's name.</summary>
    public string? Country { get; init; }

    /// <summary>The country's ISO 3166-1 alpha-2 code, upper case.</summary>
    public string? CountryCode { get; init; }

    /// <summary>The engine's own words for what it is — OpenStreetMap's key and value, such as <c>amenity=cafe</c>.</summary>
    public string? Category { get; init; }

    /// <summary>The area it covers, when the engine gives one.</summary>
    public GeocodeBox? Extent { get; init; }
}

/// <summary>The geocoding engine could not be asked, or did not answer usefully.</summary>
/// <remarks>
/// <b>One exception for every way an external engine fails</b> — refused, timed out, answered 5xx, answered
/// something that is not its format — because the caller's response is the same for all of them: the service
/// exists and its engine is not answering. The message says which, and never carries the engine's body.
/// </remarks>
public sealed class GeocoderUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public GeocoderUnavailableException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public GeocoderUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public GeocoderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

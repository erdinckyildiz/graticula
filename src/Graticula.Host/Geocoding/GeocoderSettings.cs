using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Graticula.Platform.Identity;
using Microsoft.Extensions.Configuration;

namespace Graticula.Host.Geocoding;

/// <summary>
/// Whether this server fronts a geocoder, and where it is — ADR-258.
/// </summary>
/// <param name="PhotonUrl">The Photon to ask, ending in <c>/</c>.</param>
/// <param name="Timeout">How long one request to it may take.</param>
/// <param name="Sharing">Who may use the GeocodeServer: public, every organisation's members, this one's, or administrators.</param>
/// <param name="Languages">The languages its index was built with, lower-case two-letter codes; a request's
/// <c>langCode</c> is passed on only when it is one of these, because Photon refuses any other.</param>
/// <remarks>
/// <para>
/// <b>Absent unless <c>Graticula:Geocoding:PhotonUrl</c> is set</b> — §6: everything beyond PostgreSQL is
/// optional, and a deployment that never set this has no GeocodeServer, not a broken one.
/// </para>
/// <para>
/// <b>Configuration rather than the admin API, deliberately</b> — ADR-017 §5c keeps what a server needs to
/// start in configuration, and where an engine the operator runs beside it lives is that kind of fact, the
/// same as the platform store's connection string. Read only under <c>Graticula:</c>: the key is new, so
/// there is no <c>GisServer:</c> spelling any deployment could already be using (ADR-032 §5).
/// </para>
/// </remarks>
internal sealed record GeocoderSettings(
    Uri PhotonUrl,
    TimeSpan Timeout,
    SharingScope Sharing,
    IReadOnlySet<string> Languages)
{
    /// <summary>The configuration section.</summary>
    public const string Section = "Graticula:Geocoding";

    /// <summary>The folder the service is listed in, with the geometry service.</summary>
    public const string Folder = "Utilities";

    /// <summary>The service's name within its folder.</summary>
    public const string Name = "Geocode";

    /// <summary>The service's address.</summary>
    public const string Path = "/rest/services/Utilities/Geocode/GeocodeServer";

    /// <summary>Reads the settings, or null when no geocoder is configured.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The settings, or null.</returns>
    /// <exception cref="InvalidOperationException">A key is set and unusable — refused at startup, D-171.</exception>
    public static GeocoderSettings? Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration[$"{Section}:PhotonUrl"] is not { Length: > 0 } url)
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? address)
            || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(address.Query))
        {
            throw new InvalidOperationException(
                $"{Section}:PhotonUrl is '{url}', which is not an absolute http or https URL without a query. "
                + "It is the address of a Photon geocoder this server's operator runs, such as "
                + "http://127.0.0.1:2322/ — the server asks it /api and /reverse.");
        }

        // Relative paths resolve against the last segment's directory, so a base without its slash would lose it.
        if (!address.AbsolutePath.EndsWith('/'))
        {
            address = new Uri(address.GetLeftPart(UriPartial.Path) + "/");
        }

        double seconds = 5;

        if (configuration[$"{Section}:TimeoutSeconds"] is { Length: > 0 } timeout
            && (!double.TryParse(timeout, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                || seconds <= 0 || seconds > 120))
        {
            throw new InvalidOperationException(
                $"{Section}:TimeoutSeconds is '{timeout}'; it must be a number of seconds above 0 and at most 120.");
        }

        // <b>The organisation by default — owner decision 2026-11-02, ADR-258 §5.7 (2).</b> Public was the first
        // reading, and it made every configured server an open proxy onto the operator's Photon; an operator who wants
        // a web map's anonymous search box sets public.
        SharingScope sharing = SharingScope.Organization;

        if (configuration[$"{Section}:Sharing"] is { Length: > 0 } scope)
        {
            sharing = scope.Trim().ToLowerInvariant() switch
            {
                "public" => SharingScope.Public,
                "organization" or "organisation" => SharingScope.Organization,
                "private" => SharingScope.Private,
                "allorganizations" => SharingScope.AllOrganizations,

                // A group scope names groups an item is shared with; a service that lives in configuration has no
                // item and no list, so it is refused by name rather than read as something narrower.
                "group" => throw new InvalidOperationException(
                    $"{Section}:Sharing cannot be group: the geocode service is configuration, not an item, and "
                    + "has no groups to be shared with. Use organization, or private for administrators only."),
                _ => throw new InvalidOperationException(
                    $"{Section}:Sharing is '{scope}'; it must be public, allorganizations, organization or private."),
            };
        }

        HashSet<string> languages = new(StringComparer.Ordinal);

        if (configuration[$"{Section}:Languages"] is { Length: > 0 } list)
        {
            foreach (string language in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (language.Length != 2 || !language.All(char.IsAsciiLetter))
                {
                    throw new InvalidOperationException(
                        $"{Section}:Languages names '{language}'; write two-letter codes separated by commas, "
                        + "the ones the Photon index was imported with, such as tr,en.");
                }

                languages.Add(language.ToLowerInvariant());
            }
        }

        return new GeocoderSettings(address, TimeSpan.FromSeconds(seconds), sharing, languages);
    }
}

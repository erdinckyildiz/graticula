using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Graticula.Api.Wfs;

/// <summary>The operations this server offers.</summary>
public enum WfsOperation
{
    /// <summary>What this server is and what it holds.</summary>
    GetCapabilities,

    /// <summary>The XML Schema for one or more feature types.</summary>
    DescribeFeatureType,

    /// <summary>Features.</summary>
    GetFeature,

    /// <summary>One property of the features that match, as a value collection.</summary>
    GetPropertyValue,

    /// <summary>Which stored queries exist.</summary>
    ListStoredQueries,

    /// <summary>What one stored query takes.</summary>
    DescribeStoredQueries,
}

/// <summary>How a feature collection is written.</summary>
public enum WfsOutputFormat
{
    /// <summary>GML 3.2, which WFS 2.0 makes the default.</summary>
    Gml,

    /// <summary>GeoJSON, offered beside it.</summary>
    GeoJson,
}

/// <summary>
/// A WFS request, bound from key-value pairs.
/// </summary>
/// <remarks>
/// <para>
/// <b>No framework types.</b> The binder takes a lookup and returns this, so the
/// whole surface is testable without a web server and the adapter carries no
/// dependency on one. The host turns a query string into the lookup and nothing
/// else crosses.
/// </para>
/// <para>
/// <b>Parameter names are matched case-insensitively because WFS says so.</b> The
/// specification defines KVP keys as case-insensitive, and clients differ:
/// <c>typeNames</c>, <c>typenames</c> and <c>TYPENAMES</c> all arrive in practice.
/// Matching one spelling produces a server that works with one client.
/// </para>
/// </remarks>
public sealed record WfsRequest(
    WfsOperation Operation,
    IReadOnlyList<string> TypeNames,
    WfsOutputFormat Format,
    int? Count,
    int StartIndex,
    int? Srid,
    IReadOnlyList<string> ResourceIds,
    string? Filter,
    string? BoundingBox,
    IReadOnlyList<string> SortBy,
    IReadOnlyList<string> PropertyNames,
    IReadOnlyDictionary<string, string> Namespaces,
    bool HitsOnly,
    string? StoredQueryId,
    string? PropertyValueReference)
{
    /// <summary>The stored query WFS 2.0 requires every server to offer.</summary>
    public const string GetFeatureByIdQuery = "urn:ogc:def:query:OGC-WFS::GetFeatureById";

    /// <summary>Binds a request, or says why it cannot be.</summary>
    /// <param name="parameters">The key-value pairs, however they arrived.</param>
    /// <param name="request">The bound request.</param>
    /// <param name="fault">Why it was refused.</param>
    /// <returns>Whether it bound.</returns>
    public static bool TryParse(
        IReadOnlyDictionary<string, string> parameters,
        out WfsRequest? request,
        out WfsFault? fault)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        request = null;
        fault = null;

        Dictionary<string, string> kvp = new(parameters, StringComparer.OrdinalIgnoreCase);

        string? Value(string name) =>
            kvp.TryGetValue(name, out string? found) && !string.IsNullOrWhiteSpace(found)
                ? found.Trim()
                : null;

        if (Value("request") is not { } requested)
        {
            fault = WfsFault.Missing("request");
            return false;
        }

        if (!Enum.TryParse(requested, ignoreCase: true, out WfsOperation operation)
            || !Enum.IsDefined(operation))
        {
            fault = new WfsFault(
                WfsFaultCode.OperationNotSupported,
                "request",
                // <b>One list, and this was the second copy of it.</b> ADR-039
                // condition 2 already repaired the capabilities abstract for saying
                // GetPropertyValue was not implemented hours after §5 started
                // advertising it. This sentence said the same thing and survived,
                // so a client that mistyped an operation was handed a list
                // contradicting the capabilities document it had just read. Found by
                // contradiction sweep 3.
                $"'{requested}' is not an operation this server offers. It offers "
                + "GetCapabilities, DescribeFeatureType, GetFeature, GetPropertyValue, "
                + "ListStoredQueries and DescribeStoredQueries. Transaction and LockFeature "
                + "are not implemented.");

            return false;
        }

        if (Value("service") is { } service
            && !string.Equals(service, WfsNames.Service, StringComparison.OrdinalIgnoreCase))
        {
            fault = WfsFault.Invalid(
                "service", $"This endpoint serves {WfsNames.Service}, and the request says '{service}'.");

            return false;
        }

        if (!TryVersion(operation, Value("version"), Value("acceptversions"), out fault))
        {
            return false;
        }

        if (!TryFormat(Value("outputformat"), out WfsOutputFormat format, out fault))
        {
            return false;
        }

        if (!TryWhole("count", Value("count") ?? Value("maxfeatures"), out int? count, out fault)
            || !TryWhole("startindex", Value("startindex"), out int? startIndex, out fault))
        {
            return false;
        }

        int? srid = null;

        if (Value("srsname") is { } srsName)
        {
            if (!GmlGeometryReader.TrySrsName(srsName, out int parsed))
            {
                fault = WfsFault.Invalid(
                    "srsName",
                    $"'{srsName}' is not a coordinate reference this server recognises. Use "
                    + "urn:ogc:def:crs:EPSG::<code>.");

                return false;
            }

            srid = parsed;
        }

        if (Value("resolve") is { } resolve
            && !string.Equals(resolve, "local", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolve, "none", StringComparison.OrdinalIgnoreCase))
        {
            // <b>local and none are the same thing here, and remote is not
            // pretendable.</b> Nothing this server writes carries a reference to
            // another resource, so there is never anything to resolve locally and
            // never anything to fetch remotely. Accepting 'remote' would be a claim
            // about behaviour that has no subject.
            fault = WfsFault.Invalid(
                "resolve",
                $"'{resolve}' is not a resolve value this server offers. It offers 'local' and "
                + "'none', which are the same here: no response carries a reference to resolve.");

            return false;
        }

        string? resultType = Value("resulttype");

        bool hits = string.Equals(resultType, "hits", StringComparison.OrdinalIgnoreCase);

        if (resultType is not null
            && !hits
            && !string.Equals(resultType, "results", StringComparison.OrdinalIgnoreCase))
        {
            fault = WfsFault.Invalid(
                "resultType", $"'{resultType}' is neither 'results' nor 'hits'.");

            return false;
        }

        request = new WfsRequest(
            operation,
            List(Value("typenames") ?? Value("typename")),
            format,
            count,
            startIndex ?? 0,
            srid,
            List(Value("resourceid") ?? Value("featureid") ?? Value("id")),
            Value("filter"),
            Value("bbox"),
            List(Value("sortby")),
            List(Value("propertyname")),
            ParseNamespaces(Value("namespaces")),
            hits,
            Value("storedquery_id"),
            Value("valuereference"));

        return true;
    }

    /// <summary>
    /// Checks the version, which is where a 1.1.0 client is turned away.
    /// </summary>
    /// <remarks>
    /// <b>GetCapabilities negotiates and everything else asserts.</b> A client
    /// discovering this server sends <c>AcceptVersions</c> and is entitled to be
    /// told what is on offer; a client that has read the capabilities and then
    /// asks for 1.1.0 is asking for a protocol this server does not speak, and
    /// answering in 2.0.0 anyway would be indistinguishable from a bug.
    /// </remarks>
    private static bool TryVersion(
        WfsOperation operation, string? version, string? acceptVersions, out WfsFault? fault)
    {
        fault = null;

        if (operation == WfsOperation.GetCapabilities)
        {
            if (acceptVersions is null)
            {
                return true;
            }

            string[] wanted = acceptVersions.Split(',', StringSplitOptions.RemoveEmptyEntries);

            if (wanted.Any(v => string.Equals(
                    v.Trim(), WfsNames.Version, StringComparison.Ordinal)))
            {
                return true;
            }

            fault = new WfsFault(
                WfsFaultCode.VersionNegotiationFailed,
                "AcceptVersions",
                $"This server speaks WFS {WfsNames.Version} and the request accepts only "
                + $"'{acceptVersions}'.");

            return false;
        }

        if (version is null)
        {
            fault = WfsFault.Missing("version");
            return false;
        }

        if (string.Equals(version, WfsNames.Version, StringComparison.Ordinal))
        {
            return true;
        }

        fault = new WfsFault(
            WfsFaultCode.VersionNegotiationFailed,
            "version",
            $"This server speaks WFS {WfsNames.Version} only, and the request asks for "
            + $"'{version}'. It answers no earlier version rather than answering approximately.");

        return false;
    }

    private static bool TryFormat(string? text, out WfsOutputFormat format, out WfsFault? fault)
    {
        format = WfsOutputFormat.Gml;
        fault = null;

        if (text is null)
        {
            return true;
        }

        // Compared with the punctuation and spacing removed, because
        // "application/gml+xml; version=3.2" arrives with and without the space,
        // url-encoded, and quoted, and none of those differences mean anything.
        string flat = new([.. text.Where(char.IsLetterOrDigit)]);

        if (flat.Contains("gml", StringComparison.OrdinalIgnoreCase)
            || flat.Equals("textxml", StringComparison.OrdinalIgnoreCase))
        {
            format = WfsOutputFormat.Gml;
            return true;
        }

        if (flat.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            format = WfsOutputFormat.GeoJson;
            return true;
        }

        fault = WfsFault.Invalid(
            "outputFormat",
            $"'{text}' is not a format this server writes. It writes "
            + $"'{WfsNames.GmlMediaType}' and '{WfsNames.GeoJsonMediaType}'.");

        return false;
    }

    private static bool TryWhole(string name, string? text, out int? value, out WfsFault? fault)
    {
        value = null;
        fault = null;

        if (text is null)
        {
            return true;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            || parsed < 0)
        {
            fault = WfsFault.Invalid(
                name, $"'{text}' is not a whole number of zero or more.");

            return false;
        }

        value = parsed;
        return true;
    }

    private static IReadOnlyList<string> List(string? text) => text is null
        ? []
        : [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// Reads the <c>NAMESPACES</c> parameter, which says what a prefix means.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A prefix in <c>typeNames</c> is meaningless on its own, and this server
    /// treated it as a name for a day.</b> WFS 2.0 §7.9.2 lets a request bind
    /// whatever prefixes it likes and then use them, so
    /// <c>typeNames=ns98:look_parcels</c> beside
    /// <c>namespaces=xmlns(ns98,urn:graticula:ns:hosted)</c> asks for exactly the
    /// same thing as <c>hosted:look_parcels</c>. Reading the prefix literally
    /// answers *no such feature type* about a type the capabilities advertise.
    /// </para>
    /// <para>
    /// <b>Found by the OGC conformance suite and by nothing else.</b> GDAL uses the
    /// prefixes it read out of the capabilities, so it never exercised this; the
    /// suite deliberately picks prefixes the server has never seen. That is the
    /// difference between a client that happens to work and a suite that tests the
    /// specification — [Q-122](../../../docs/open-questions.md).
    /// </para>
    /// <para>
    /// The grammar is <c>xmlns(prefix,uri)</c> repeated, with <c>xmlns(uri)</c>
    /// binding the default namespace. Split naively on commas and a URI containing
    /// one takes the parser with it, so the pairs are matched rather than split.
    /// </para>
    /// </remarks>
    private static Dictionary<string, string> ParseNamespaces(string? text)
    {
        Dictionary<string, string> bound = new(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(text))
        {
            return bound;
        }

        foreach (Match match in Regex.Matches(
            text, @"xmlns\(\s*([^,()\s]*)\s*(?:,\s*([^()]*?)\s*)?\)", RegexOptions.None))
        {
            string first = match.Groups[1].Value;
            string second = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;

            // xmlns(uri) binds the default namespace; xmlns(prefix,uri) binds one.
            (string prefix, string uri) = match.Groups[2].Success
                ? (first, second)
                : (string.Empty, first);

            if (!string.IsNullOrEmpty(uri))
            {
                bound[prefix] = uri;
            }
        }

        return bound;
    }
}

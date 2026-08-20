using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Graticula.Geometries;

namespace Graticula.Api.OgcFeatures;

/// <summary>What an items request may ask for.</summary>
/// <param name="DefaultLimit">How many features when the client names no limit.</param>
/// <param name="MaximumLimit">The most it may ask for.</param>
public readonly record struct OgcLimits(int DefaultLimit, int MaximumLimit)
{
    /// <summary>
    /// The specification's own numbers.
    /// </summary>
    /// <remarks>
    /// <b>10 and 1,000 are from OGC API Features Part 1 §7.15.3</b>, which recommends
    /// exactly these. Ten looks small for a data API and it is the right default for
    /// one whose landing page a person opens in a browser: the first request anybody
    /// makes returns something they can read.
    /// </remarks>
    public static OgcLimits Default => new(10, 1000);
}

/// <summary>
/// The parameters of <c>/collections/{id}/items</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every refusal names the parameter</b>, because a client showing *400 Bad
/// Request* with nothing else sends its user to a log they cannot read. The
/// documents are RFC 7807, which is what OGC API Features points at for errors.
/// </para>
/// <para>
/// <b>An unknown parameter is refused rather than ignored</b> — Part 1 §7.15.5
/// requires it, and the reason is the reason this whole server keeps giving: a
/// client that mis-spells <c>datetime</c> and receives every feature has been told
/// its filter worked.
/// </para>
/// </remarks>
public sealed class OgcRequest
{
    /// <summary>Parameters every items request may carry.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "bbox", "bbox-crs", "crs", "datetime", "limit", "offset", "f",
    };

    private OgcRequest()
    {
    }

    /// <summary>How many features to return.</summary>
    public int Limit { get; private init; }

    /// <summary>How many to skip.</summary>
    public int Offset { get; private init; }

    /// <summary>The extent to filter by, in <see cref="BboxSrid"/>, or null.</summary>
    public Envelope? Bbox { get; private init; }

    /// <summary>The reference system the bounding box is written in.</summary>
    public int BboxSrid { get; private init; } = AxisOrder.Wgs84;

    /// <summary>
    /// The eastern half of a bounding box that crosses the antimeridian, or null.
    /// </summary>
    /// <remarks>
    /// <b>A <c>bbox</c> whose first longitude is greater than its third crosses the
    /// antimeridian</b> — Part 1 §7.15.3 says so explicitly, and it is the one case
    /// where the obvious validation is wrong. Refusing it, which this did until the
    /// CITE suite asked, turns a legitimate query about the Pacific into a 400.
    /// The box becomes two, split at ±180, and the filter is their union.
    /// </remarks>
    public Envelope? BboxEast { get; private init; }

    /// <summary>The reference system the response is written in.</summary>
    public int Srid { get; private init; } = AxisOrder.Wgs84;

    /// <summary>The <c>crs</c> URI as the client wrote it, for the response header.</summary>
    public string CrsUri { get; private init; } = OgcNames.Crs84;

    /// <summary>
    /// Whether the response's coordinates go latitude first.
    /// </summary>
    /// <remarks>
    /// <b>This was computed and thrown away until 2026-08-20, and the correctness
    /// gate found it.</b> <c>CRS84</c> and <c>EPSG/0/4326</c> are the same datum in
    /// opposite orders, and asking for the second returned the first's coordinates
    /// with the second's name in the <c>Content-Crs</c> header. A conforming client
    /// trusts that header to know how to read the geometry, so it placed every point
    /// in the wrong hemisphere — and the server was **claiming** the CRS conformance
    /// class while doing it, which is worse than not offering it.
    /// </remarks>
    public bool LatitudeFirst { get; private init; }

    /// <summary>The first instant included, or null for no lower bound.</summary>
    public DateTimeOffset? From { get; private init; }

    /// <summary>The first instant excluded, or null for no upper bound.</summary>
    public DateTimeOffset? Until { get; private init; }

    /// <summary>Whether a time filter was asked for at all.</summary>
    public bool HasDateTime => From is not null || Until is not null;

    /// <summary>Attribute equality filters, by column name.</summary>
    /// <remarks>
    /// <b>Part 1 §7.15.4 makes any queryable property a parameter of its own name.</b>
    /// It is the whole of Core's attribute filtering, and it is why an unknown
    /// parameter cannot simply be refused without first asking whether it is a
    /// column.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Properties { get; private init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Reads an items request.
    /// </summary>
    /// <param name="parameter">Reads one parameter by name, exactly as written.</param>
    /// <param name="names">Every parameter the request carried.</param>
    /// <param name="collection">The collection being asked, for its columns and CRS.</param>
    /// <param name="limits">What the request is allowed to ask for.</param>
    /// <param name="request">The request.</param>
    /// <param name="problem">Why not, when it did not parse.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(
        Func<string, string?> parameter,
        IEnumerable<string> names,
        CollectionMetadata collection,
        OgcLimits limits,
        out OgcRequest? request,
        out OgcProblem? problem)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(collection);

        request = null;
        problem = null;

        Dictionary<string, string> properties = new(StringComparer.Ordinal);

        foreach (string name in names)
        {
            if (Known.Contains(name))
            {
                continue;
            }

            // A queryable property, or nothing this server understands. Matched
            // case-insensitively against the layer's own columns, because a column
            // is named by whoever made the table and a client copies it from the
            // collection document.
            string? column = null;

            foreach (Graticula.Features.FieldDescription field in collection.Fields)
            {
                if (string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    column = field.Name;
                    break;
                }
            }

            if (column is null)
            {
                problem = OgcProblem.BadRequest(
                    $"`{name}` is not a parameter of this resource and not a property of "
                    + $"`{collection.Id}`. It is refused rather than ignored: a client whose "
                    + "filter was silently dropped has been told the filter worked.");

                return false;
            }

            properties[column] = parameter(name) ?? string.Empty;
        }

        if (!TryLimit(parameter("limit"), limits, out int limit, out problem)
            || !TryOffset(parameter("offset"), out int offset, out problem))
        {
            return false;
        }

        if (!TryCrs(parameter("crs"), collection, "crs", out int srid, out string crsUri, out problem))
        {
            return false;
        }

        bool latitudeFirst =
            OgcNames.SridOf(parameter("crs"), out bool responseFirst) is not null && responseFirst;

        if (!TryCrs(
                parameter("bbox-crs"), collection, "bbox-crs",
                out int bboxSrid, out _, out problem))
        {
            return false;
        }

        bool bboxLatitudeFirst =
            OgcNames.SridOf(parameter("bbox-crs"), out bool first) is not null && first;

        if (!TryBbox(
                parameter("bbox"), bboxLatitudeFirst,
                out Envelope? bbox, out Envelope? east, out problem))
        {
            return false;
        }

        if (!TryDateTime(
                parameter("datetime"), out DateTimeOffset? from, out DateTimeOffset? until,
                out problem))
        {
            return false;
        }

        request = new OgcRequest
        {
            Limit = limit,
            Offset = offset,
            Bbox = bbox,
            BboxEast = east,
            BboxSrid = bboxSrid,
            Srid = srid,
            CrsUri = crsUri,
            LatitudeFirst = latitudeFirst,
            From = from,
            Until = until,
            Properties = properties,
        };

        return true;
    }

    private static bool TryLimit(
        string? value, OgcLimits limits, out int limit, out OgcProblem? problem)
    {
        limit = limits.DefaultLimit;
        problem = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit)
            || limit < 1)
        {
            problem = OgcProblem.BadRequest($"`limit={value}` is not a positive whole number.");
            return false;
        }

        // <b>Clamped, not refused, and this is the one place that differs from the
        // rest of this server.</b> Part 1 §7.15.3 says the server may return fewer
        // than asked and the client learns the real number from `numberReturned`.
        // Refusing would break every client that sends a large limit on purpose,
        // knowing it will be capped.
        limit = Math.Min(limit, limits.MaximumLimit);
        return true;
    }

    private static bool TryOffset(string? value, out int offset, out OgcProblem? problem)
    {
        offset = 0;
        problem = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset)
            || offset < 0)
        {
            problem = OgcProblem.BadRequest($"`offset={value}` is not a whole number of features.");
            return false;
        }

        return true;
    }

    private static bool TryCrs(
        string? value,
        CollectionMetadata collection,
        string name,
        out int srid,
        out string uri,
        out OgcProblem? problem)
    {
        srid = AxisOrder.Wgs84;
        uri = OgcNames.Crs84;
        problem = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (OgcNames.SridOf(value, out _) is not { } code)
        {
            problem = OgcProblem.BadRequest(
                $"`{name}={value}` is not a coordinate reference system this collection offers. "
                + $"It offers: {string.Join(", ", collection.CoordinateSystems)}.");

            return false;
        }

        // Part 2 §6.2: a CRS the collection does not list is refused. Answering in
        // another one would be a response whose coordinates mean something the
        // client did not ask for, in a format with nowhere to say so.
        if (!collection.CoordinateSystems.Contains(value.Trim(), StringComparer.Ordinal))
        {
            problem = OgcProblem.BadRequest(
                $"`{name}={value}` is not one of `{collection.Id}`'s reference systems. "
                + $"It offers: {string.Join(", ", collection.CoordinateSystems)}.");

            return false;
        }

        srid = code;
        uri = value.Trim();
        return true;
    }

    /// <summary>
    /// Reads <c>bbox</c>, transposing it when the named CRS is latitude first.
    /// </summary>
    /// <remarks>
    /// <b>The same trap as WFS and WMS, arriving a third time.</b> A bounding box in
    /// <c>EPSG/0/4326</c> is minimum latitude first; the same box in <c>CRS84</c> is
    /// not. Read the wrong way round, a request for Turkey selects the Indian Ocean
    /// and the response is a valid empty collection.
    /// </remarks>
    private static bool TryBbox(
        string? value,
        bool latitudeFirst,
        out Envelope? bbox,
        out Envelope? east,
        out OgcProblem? problem)
    {
        bbox = null;
        east = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);

        // Six numbers is a 3D box: the elevations are read and dropped, which is
        // what a 2D store can honestly do with them.
        if (parts.Length is not (4 or 6))
        {
            problem = OgcProblem.BadRequest(
                "`bbox` is four numbers — minx,miny,maxx,maxy — or six with minimum and maximum "
                + "elevation between them.");

            return false;
        }

        double[] numbers = new double[parts.Length];

        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(
                parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
            {
                problem = OgcProblem.BadRequest($"`{parts[i]}` in `bbox` is not a number.");
                return false;
            }
        }

        (double a, double b, double c, double d) = parts.Length == 4
            ? (numbers[0], numbers[1], numbers[2], numbers[3])
            : (numbers[0], numbers[1], numbers[3], numbers[4]);

        (double minX, double minY, double maxX, double maxY) =
            latitudeFirst ? (b, a, d, c) : (a, b, c, d);

        // <b>A zero-width or zero-height box is a legitimate query, and refusing it
        // was a rule copied from the wrong place.</b> WMS refuses one because an
        // image needs area; an intersection test does not, and this server holds two
        // layers whose whole extent is a horizontal line — so a client using the
        // published extent as its `bbox` was refused for doing the obvious thing.
        // Found by the CITE suite, which does exactly that.
        if (maxY < minY)
        {
            problem = OgcProblem.BadRequest(
                "`bbox` has its maximum latitude below its minimum. In "
                + (latitudeFirst
                    ? "EPSG:4326 the order is miny,minx,maxy,maxx — latitude first."
                    : "CRS84 the order is minx,miny,maxx,maxy — longitude first."));

            return false;
        }

        // <b>West greater than east means the box crosses the antimeridian</b>,
        // which Part 1 §7.15.3 defines rather than forbids. It becomes two boxes.
        if (maxX < minX)
        {
            bbox = new Envelope(minX, minY, 180, maxY);
            east = new Envelope(-180, minY, maxX, maxY);
            return true;
        }

        bbox = new Envelope(minX, minY, maxX, maxY);
        return true;
    }

    /// <summary>
    /// Reads <c>datetime</c>: an instant, or an interval with either end open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written here rather than shared with WMS-T's <c>TimeWindow</c>, because the
    /// two rules differ.</b> OGC API requires open ends written as <c>..</c> —
    /// <c>2026-08-01T00:00:00Z/..</c> means *from then onward* — and WMS has no such
    /// form. A shared parser accepting both would accept, on each surface, a
    /// spelling that surface's clients never send and its specification does not
    /// define.
    /// </para>
    /// <para>
    /// <b>An instant is an instant here, not a period.</b> That is the other
    /// difference: WMS-T reads <c>2026-08</c> as the whole of August because ISO 8601
    /// says a truncated timestamp denotes one; OGC API Features §7.15.2 defines
    /// <c>datetime</c> as an intersection test against an instant or an interval, and
    /// its examples are full timestamps. A date with no time is read as that day,
    /// because a client sending one means the day and no reading of *the instant of
    /// midnight* is useful.
    /// </para>
    /// </remarks>
    private static bool TryDateTime(
        string? value,
        out DateTimeOffset? from,
        out DateTimeOffset? until,
        out OgcProblem? problem)
    {
        from = null;
        until = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string text = value.Trim();
        string[] parts = text.Split('/', StringSplitOptions.None);

        if (parts.Length > 2)
        {
            problem = OgcProblem.BadRequest(
                $"`datetime={text}` is not an instant or an interval. Write an instant, or "
                + "`start/end` with `..` for an open end.");

            return false;
        }

        if (parts.Length == 1)
        {
            if (!TryInstant(parts[0], out DateTimeOffset at, out DateTimeOffset end, out problem))
            {
                return false;
            }

            from = at;
            until = end;
            return true;
        }

        if (!IsOpen(parts[0]))
        {
            if (!TryInstant(parts[0], out DateTimeOffset start, out _, out problem))
            {
                return false;
            }

            from = start;
        }

        if (!IsOpen(parts[1]))
        {
            // The end of an interval is inclusive of the period it names, so a date
            // as the upper bound means through the end of that day.
            if (!TryInstant(parts[1], out DateTimeOffset close, out DateTimeOffset after, out problem))
            {
                return false;
            }

            until = after > close ? after : close;
        }

        if (from is null && until is null)
        {
            problem = OgcProblem.BadRequest(
                "`datetime=../..` has both ends open, which selects everything and filters "
                + "nothing. Leave the parameter off instead.");

            return false;
        }

        if (from is { } lower && until is { } upper && upper <= lower)
        {
            problem = OgcProblem.BadRequest($"`datetime={text}` ends before it starts.");
            return false;
        }

        return true;
    }

    private static bool IsOpen(string part) =>
        part.Length == 0 || string.Equals(part.Trim(), "..", StringComparison.Ordinal);

    private static bool TryInstant(
        string text, out DateTimeOffset at, out DateTimeOffset end, out OgcProblem? problem)
    {
        at = default;
        end = default;
        problem = null;

        string[] formats =
        [
            "yyyy-MM-dd",
            "yyyy-MM-ddTHH:mm:ssK",
            "yyyy-MM-ddTHH:mm:ss.fK",
            "yyyy-MM-ddTHH:mm:ss.ffK",
            "yyyy-MM-ddTHH:mm:ss.fffK",
            "yyyy-MM-ddTHH:mm:ss.ffffffK",
            "yyyy-MM-ddTHH:mm:ss.fffffffK",
        ];

        for (int i = 0; i < formats.Length; i++)
        {
            if (!DateTimeOffset.TryParseExact(
                text.Trim(),
                formats[i],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
            {
                continue;
            }

            at = parsed;

            // <b>A microsecond, not a tick, and the difference is the whole bug.</b>
            // A .NET tick is 100 nanoseconds; PostgreSQL's `timestamptz` resolves to
            // a microsecond. `parsed.AddTicks(1)` round-trips through the database
            // back to `parsed`, so the predicate became `column >= X AND column < X`
            // — unsatisfiable by construction, and **every exact-instant `datetime`
            // returned nothing**. Found by the correctness gate 2026-08-20, which
            // asked a temporal layer for the moment one of its own rows carries.
            end = i == 0 ? parsed.AddDays(1) : parsed.AddMicroseconds(1);
            return true;
        }

        problem = OgcProblem.BadRequest(
            $"`{text}` is not an RFC 3339 date or timestamp. Write 2026-08-20 or "
            + "2026-08-20T14:00:00Z.");

        return false;
    }
}

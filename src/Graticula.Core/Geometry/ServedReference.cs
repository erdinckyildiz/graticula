using System;

namespace Graticula.Geometries;

/// <summary>
/// The reference something is served in: an EPSG code, or a definition written out.
/// </summary>
/// <remarks>
/// <para>
/// <b>By owner decision, 2026-09-06:</b> <i>"epsg güzel ama wkt de kabul etmemiz lazım."</i> A
/// service may name its reference by code, and it may also carry the definition itself — a
/// national grid a customer uses, a local system, anything PROJ can read and EPSG has no number
/// for.
/// </para>
/// <para>
/// <b>One type rather than two nullable fields travelling together.</b> A <c>ServedSrid</c>
/// beside a <c>ServedWkt</c> is one fact in two places, and
/// [D-179](../../../docs/architecture-debt.md) is what that costs: a row with two facts where
/// only one of them travelled, and the two documents describing one layer disagreed for a week.
/// The compiler finds every site that reads a reference because there is exactly one thing to
/// read.
/// </para>
/// <para>
/// <b>Nothing writes a definition into anybody's <c>spatial_ref_sys</c>.</b> The obvious cheap
/// route — insert the WKT under a spare code and carry on with integers everywhere — writes into
/// the database a registered source points at, which belongs to somebody else and which this
/// product does not touch: the same rule that keeps a registered table from being dropped when
/// its service is. Measured 2026-09-06 on PostGIS 3.4.3: <c>ST_Transform(geom, '&lt;wkt&gt;')</c>
/// gives the same coordinates to the digit as <c>ST_Transform(geom, 5254)</c> and needs no row,
/// so the cheap route buys nothing it is allowed to spend.
/// </para>
/// <para>
/// <b>A geometry transformed to a definition carries SRID 0</b> — measured in the same session
/// — so a document describing it says <c>wkt</c> rather than <c>wkid</c>, which is what the
/// ArcGIS spatial reference object has always allowed.
/// </para>
/// </remarks>
public sealed record ServedReference
{
    /// <summary>Nothing chosen: each layer answers in whatever its own table holds.</summary>
    public static ServedReference None { get; } = new();

    private ServedReference()
    {
    }

    /// <summary>An EPSG code, or null when the reference is a definition.</summary>
    public int? Srid { get; private init; }

    /// <summary>A written definition, or null when the reference is a code.</summary>
    /// <remarks>
    /// <b>Whatever PROJ reads</b> — WKT 1, WKT 2 and a PROJ string all work, and none of them is
    /// this server's business to parse. What it does with the text is hand it to PostGIS, which
    /// hands it to PROJ; a validator here would be a second opinion about a format somebody else
    /// defines, and it would be wrong first.
    /// </remarks>
    public string? Wkt { get; private init; }

    /// <summary>Whether anything at all was chosen.</summary>
    public bool Chosen => Srid is > 0 || Wkt is { Length: > 0 };

    /// <summary>A reference named by its EPSG code.</summary>
    /// <param name="srid">The code.</param>
    /// <returns>The reference, or <see cref="None"/> when the code is not one.</returns>
    public static ServedReference Code(int? srid) =>
        srid is { } given && given > 0 ? new ServedReference { Srid = given } : None;

    /// <summary>A reference carrying its own definition.</summary>
    /// <param name="wkt">The definition.</param>
    /// <returns>The reference, or <see cref="None"/> when there is no definition.</returns>
    public static ServedReference Definition(string? wkt) =>
        string.IsNullOrWhiteSpace(wkt) ? None : new ServedReference { Wkt = wkt.Trim() };

    /// <summary>
    /// Reads whichever of the two was given, refusing both at once.
    /// </summary>
    /// <remarks>
    /// <b>Both is a question rather than a request.</b> A caller sending a code and a definition
    /// has two answers and no way to say which governs; guessing one would be right half the
    /// time and silent about it.
    /// </remarks>
    /// <param name="srid">A code, or null.</param>
    /// <param name="wkt">A definition, or null.</param>
    /// <param name="read">What was read.</param>
    /// <param name="error">Why it could not be, when it could not.</param>
    /// <returns>Whether it read.</returns>
    public static bool TryRead(
        int? srid, string? wkt, out ServedReference read, out string? error)
    {
        read = None;
        error = null;

        bool hasCode = srid is > 0;
        bool hasText = !string.IsNullOrWhiteSpace(wkt);

        if (hasCode && hasText)
        {
            error = "A reference is an EPSG code or a written definition, not both. Send one of "
                + "them, or neither to serve each layer in its own.";

            return false;
        }

        if (srid is { } given && given <= 0)
        {
            error = $"'{given}' is not an EPSG code. Send a positive code, a written definition, "
                + "or neither.";

            return false;
        }

        read = hasCode ? Code(srid) : Definition(wkt);

        return true;
    }

    /// <summary>How this reads in a sentence to somebody.</summary>
    /// <returns>The code, or that it is a definition.</returns>
    public override string ToString() =>
        Srid is { } code ? $"EPSG:{code}"
        : Wkt is { Length: > 0 } ? "a written definition"
        : "each layer's own";
}

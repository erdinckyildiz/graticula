using System;
using System.Globalization;
using System.Linq;

namespace Graticula.Api.Tiles;

/// <summary>The WMTS operations this face knows by name.</summary>
public enum WmtsOperation
{
    /// <summary>The service metadata.</summary>
    GetCapabilities,

    /// <summary>One tile.</summary>
    GetTile,
}

/// <summary>
/// A WMTS 1.0.0 key-value-pair request, read — 07-057r7 §7.1.1.1 (GetCapabilities) and §7.2.1.1
/// (GetTile).
/// </summary>
/// <param name="Operation">What is asked.</param>
/// <param name="Layer">GetTile's layer.</param>
/// <param name="Style">GetTile's style.</param>
/// <param name="Format">GetTile's format.</param>
/// <param name="TileMatrixSet">GetTile's tile matrix set.</param>
/// <param name="TileMatrix">GetTile's tile matrix.</param>
/// <param name="TileRow">GetTile's row.</param>
/// <param name="TileCol">GetTile's column.</param>
/// <remarks>
/// <para>
/// <b>Parameter names without case, values with it</b> — OWS Common 1.1 §11.5.2: a client writing
/// <c>layer=</c> is as right as one writing <c>LAYER=</c>, and a layer called <c>Parcels</c> is not one
/// called <c>parcels</c>.
/// </para>
/// <para>
/// <b>Every GetTile parameter is required, and a missing one is named.</b> §7.2.1.1 marks all nine
/// mandatory. A server that filled in a default for a missing <c>TILEMATRIXSET</c> would be choosing a
/// grid for the client, and a tile from a grid the client did not mean is a tile in the wrong place.
/// </para>
/// </remarks>
public sealed record WmtsRequest(
    WmtsOperation Operation,
    string Layer,
    string Style,
    string Format,
    string TileMatrixSet,
    string TileMatrix,
    long TileRow,
    long TileCol)
{
    /// <summary>The one version served.</summary>
    public const string Version = "1.0.0";

    /// <summary>The one style every layer has.</summary>
    /// <remarks>
    /// <b>One style, <c>default</c>, because a vector tile is not drawn by the server.</b> The tile is the
    /// same bytes whichever style a client paints it with; the ArcGIS face's named styles (ADR-094) are
    /// documents a client reads, not tiles this face could vary.
    /// </remarks>
    public const string DefaultStyle = "default";

    /// <summary>Reads a request, or says what is wrong with it.</summary>
    /// <param name="parameter">A parameter by name, ignoring case; null when absent.</param>
    /// <param name="request">The request.</param>
    /// <param name="fault">What is wrong.</param>
    /// <returns>Whether it could be read.</returns>
    public static bool TryParse(Func<string, string?> parameter, out WmtsRequest? request, out WmtsFault? fault)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        request = null;

        if (parameter("SERVICE") is not { Length: > 0 } service)
        {
            fault = WmtsFault.Missing("SERVICE");
            return false;
        }

        if (!string.Equals(service, "WMTS", StringComparison.OrdinalIgnoreCase))
        {
            fault = WmtsFault.Invalid("SERVICE", $"This address serves WMTS, and the request asked for '{service}'.");
            return false;
        }

        if (parameter("REQUEST") is not { Length: > 0 } operation)
        {
            fault = WmtsFault.Missing("REQUEST");
            return false;
        }

        if (string.Equals(operation, "GetCapabilities", StringComparison.OrdinalIgnoreCase))
        {
            // <b>AcceptVersions, not VERSION, negotiates a GetCapabilities</b> — OWS Common §7.3.2. A list
            // without 1.0.0 is a client that cannot read what this server writes.
            if (parameter("ACCEPTVERSIONS") is { Length: > 0 } accepted
                && !accepted.Split(',').Select(v => v.Trim()).Contains(Version, StringComparer.Ordinal))
            {
                fault = new WmtsFault(
                    WmtsFault.VersionNegotiationFailed,
                    "AcceptVersions",
                    $"This server speaks WMTS {Version} only, and the request accepts {accepted}.");
                return false;
            }

            fault = null;
            request = new WmtsRequest(WmtsOperation.GetCapabilities, "", "", "", "", "", 0, 0);
            return true;
        }

        if (!string.Equals(operation, "GetTile", StringComparison.OrdinalIgnoreCase))
        {
            // GetFeatureInfo is an optional WMTS operation, and a vector tile carries its features already.
            fault = new WmtsFault(
                WmtsFault.OperationNotSupported,
                "REQUEST",
                $"'{operation}' is not offered. This server answers GetCapabilities and GetTile.");
            return false;
        }

        if (parameter("VERSION") is not { Length: > 0 } version)
        {
            fault = WmtsFault.Missing("VERSION");
            return false;
        }

        if (!string.Equals(version, Version, StringComparison.Ordinal))
        {
            fault = WmtsFault.Invalid("VERSION", $"This server speaks WMTS {Version}, and the request asked for {version}.");
            return false;
        }

        string[] required = ["LAYER", "STYLE", "FORMAT", "TILEMATRIXSET", "TILEMATRIX", "TILEROW", "TILECOL"];

        foreach (string name in required)
        {
            if (parameter(name) is null)
            {
                fault = WmtsFault.Missing(name);
                return false;
            }
        }

        if (!TryIndex(parameter("TILEROW")!, out long row))
        {
            fault = WmtsFault.Invalid("TILEROW", $"'{parameter("TILEROW")}' is not a row: a row is a whole number from 0.");
            return false;
        }

        if (!TryIndex(parameter("TILECOL")!, out long column))
        {
            fault = WmtsFault.Invalid("TILECOL", $"'{parameter("TILECOL")}' is not a column: a column is a whole number from 0.");
            return false;
        }

        fault = null;
        request = new WmtsRequest(
            WmtsOperation.GetTile,
            parameter("LAYER")!,
            parameter("STYLE")!,
            parameter("FORMAT")!,
            parameter("TILEMATRIXSET")!,
            parameter("TILEMATRIX")!,
            row,
            column);
        return true;
    }

    /// <summary>What is wrong with a GetTile's style and format, or null — the checks both bindings share.</summary>
    /// <returns>The fault, or null.</returns>
    public WmtsFault? StyleOrFormatFault()
    {
        // An empty STYLE is read as the default: 07-057r7 requires the parameter, and some clients send it
        // blank for a layer whose only style is the default.
        if (Style.Length > 0 && !string.Equals(Style, DefaultStyle, StringComparison.Ordinal))
        {
            return WmtsFault.Invalid("STYLE", $"'{Style}' is not a style of this layer; its one style is '{DefaultStyle}'.");
        }

        return string.Equals(Format, TileNames.Mvt, StringComparison.OrdinalIgnoreCase)
            ? null
            : WmtsFault.Invalid("FORMAT", $"'{Format}' is not a format of this layer; its one format is {TileNames.Mvt}.");
    }

    /// <summary>A row or column: a non-negative integer in digits only.</summary>
    private static bool TryIndex(string text, out long value) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Formats;
using Graticula.Geocoding;
using Graticula.Geometries;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host.Geocoding;

/// <summary>
/// A table of addresses turned into points by the geocoder the operator configured — ArcGIS's
/// <c>locationType: address</c> for a CSV or a workbook, in an append and in a publish (ADR-310, ADR-257 §5 amended).
/// </summary>
/// <remarks>
/// <para>
/// <b>Off unless a geocoder is configured, and refused in a sentence when it is not.</b> ADR-258 makes the geocoder
/// optional (<c>Graticula:Geocoding:PhotonUrl</c>); a table of addresses with none is refused, with the key to set,
/// rather than read as a table with no location.
/// </para>
/// <para>
/// <b>ADR-258's own rules, applied.</b> <i>Rate</i>: the rows go through the same bounded batch as
/// <c>geocodeAddresses</c> (<see cref="GeocodeServerEndpoints.BestAsync"/> — four at a time, the configured deadline, an
/// engine that is down failing the batch) and a request of more rows than <see cref="GeocoderSettings.MaxBatchSize"/> is
/// refused, whole, rather than cut: a file written for the first two hundred rows and no more would be a different
/// file. <i>Privacy</i>: the addresses leave this server for the one Photon the operator named and nowhere else;
/// nothing of them is logged or audited, and only a caller the geocoder's own sharing admits may have them
/// geocoded — the same <see cref="GeocodeServerEndpoints.Allowed"/> that decides who may call the service. <i>Air
/// gap</i>: an air-gapped deployment runs its Photon inside, as ADR-258 §5.1 says, and nothing here reaches further.
/// </para>
/// <para>
/// <b>An unmatched row is not written, and is reported</b> (INFERRED, ADR-310 §5.5): the public references say nothing of
/// what ArcGIS does with one. The report — how many rows, how many matched, the numbers of the rows that did not — is
/// left on the request for the answer to carry, so that nobody finds out from a count that came up short.
/// </para>
/// </remarks>
internal static class AddressGeocoding
{
    /// <summary>The key a <see cref="Report"/> is left under in <see cref="HttpContext.Items"/>.</summary>
    internal const string ItemKey = "graticula.address-geocoding";

    /// <summary>How many unmatched row numbers an answer lists; the count is always whole.</summary>
    internal const int ListedUnmatched = 100;

    /// <summary>What a table of addresses came to.</summary>
    /// <param name="Rows">How many rows were asked.</param>
    /// <param name="Matched">How many were located and kept.</param>
    /// <param name="Unmatched">The numbers (from 1, in the file's order) of the first rows that were not located.</param>
    internal sealed record Report(int Rows, int Matched, IReadOnlyList<int> Unmatched)
    {
        /// <summary>How many rows were not located.</summary>
        public int UnmatchedCount => Rows - Matched;
    }

    /// <summary>The parts of an address, in the order a one-line address is written from them (ADR-258 §5.5).</summary>
    private static readonly string[] Roles = ["Address", "Neighborhood", "City", "Subregion", "Region", "Postal", "CountryCode"];

    /// <summary>Whether a request asks for its rows to be located by address.</summary>
    /// <param name="form">The request's fields.</param>
    /// <returns>Whether <c>locationType</c> is <c>address</c>.</returns>
    internal static bool Asked(IFormCollection form) =>
        string.Equals(form["locationType"].ToString().Trim(), "address", StringComparison.OrdinalIgnoreCase);

    /// <summary>The report left on a request, or null when it did not geocode.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The report.</returns>
    internal static Report? ReportOf(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out object? left) ? left as Report : null;

    /// <summary>The report as the answer of an operation carries it.</summary>
    /// <param name="report">The report.</param>
    /// <returns>An object to write.</returns>
    internal static object Describe(Report report) => new
    {
        locationType = "address",
        rows = report.Rows,
        matchedCount = report.Matched,
        unmatchedCount = report.UnmatchedCount,
        unmatchedRows = report.Unmatched,
    };

    /// <summary>
    /// The table's rows located: a Point on each that the geocoder found, none on the rest, which are dropped and counted.
    /// Null, with the refusal written, when the rows cannot be geocoded.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="form">Its fields: <c>addressTemplate</c> or <c>addressFields</c>, and <c>sourceCountry</c>.</param>
    /// <param name="table">The table as the reader read it, its rows without geometry.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>The table of points, or null.</returns>
    internal static async Task<ImportedDataset?> ApplyAsync(
        HttpContext context, IFormCollection form, ImportedDataset table, CancellationToken cancellation)
    {
        IGeocoder? geocoder = context.RequestServices.GetService<IGeocoder>();
        GeocoderSettings? settings = context.RequestServices.GetService<GeocoderSettings>();

        if (geocoder is null || settings is null)
        {
            return await RefuseAsync(context, 400,
                "locationType 'address' needs a geocoder, and this server has none: an operator turns one on by pointing "
                + "Graticula:Geocoding:PhotonUrl at a Photon. A table is otherwise located by its coordinate columns "
                + "(x and y, longitude and latitude, or WKT). Nothing was written.").ConfigureAwait(false);
        }

        RequestPrincipal? current = context.Features.Get<RequestPrincipal>();

        if (current is null || !GeocodeServerEndpoints.Allowed(settings, current))
        {
            return await RefuseAsync(context, 403,
                "You may not use this server's geocoder, so a table of addresses cannot be located for you. Nothing was written.")
                .ConfigureAwait(false);
        }

        if (table.Features.Count > settings.MaxBatchSize)
        {
            return await RefuseAsync(context, 400,
                $"This table has {table.Features.Count} rows and this server's geocoder locates at most {settings.MaxBatchSize} in one "
                + "request (Graticula:Geocoding:MaxBatchSize). Split the table. Nothing was written.").ConfigureAwait(false);
        }

        if (!TryLines(form, table, out List<string> lines, out string? why))
        {
            return await RefuseAsync(context, 400, why!).ConfigureAwait(false);
        }

        if (!GeocodeServerWriter.TryCountries(form["sourceCountry"].ToString(), out IReadOnlySet<string>? countries, out string? sentence))
        {
            return await RefuseAsync(context, 400, sentence!).ConfigureAwait(false);
        }

        (GeocodePlace Place, double Score)?[] best;

        try
        {
            best = await GeocodeServerEndpoints.BestAsync(geocoder, lines, null, null, null, countries, cancellation).ConfigureAwait(false);
        }
        catch (GeocoderUnavailableException e)
        {
            return await RefuseAsync(context, 503,
                $"The geocoder did not answer, so the table was not located and nothing was written: {e.Message}").ConfigureAwait(false);
        }

        List<ImportedFeature> kept = [];
        List<int> unmatched = [];

        for (int i = 0; i < table.Features.Count; i++)
        {
            if (best[i] is { } match)
            {
                kept.Add(table.Features[i] with { Geometry = new Point(match.Place.Longitude, match.Place.Latitude) });
            }
            else if (unmatched.Count < ListedUnmatched)
            {
                unmatched.Add(i + 1);
            }
        }

        context.Items[ItemKey] = new Report(table.Features.Count, kept.Count, unmatched);

        if (kept.Count == 0)
        {
            return await RefuseAsync(context, 400,
                $"None of the {table.Features.Count} addresses was located, so there is nothing to write. Nothing was written.")
                .ConfigureAwait(false);
        }

        return table with { Features = kept, GeometryType = GeometryKind.Point, Srid = 4326 };
    }

    /// <summary>Each row's address as one line, from <c>addressTemplate</c> or from <c>addressFields</c>.</summary>
    private static bool TryLines(IFormCollection form, ImportedDataset table, out List<string> lines, out string? why)
    {
        lines = [];
        why = null;

        string template = form["addressTemplate"].ToString();
        string fields = form["addressFields"].ToString();

        Dictionary<string, string> columns = new(StringComparer.OrdinalIgnoreCase);

        foreach (InferredColumn column in table.Columns)
        {
            columns[column.Name] = column.Name;
        }

        string named = string.Join(", ", columns.Values.Select(c => $"'{c}'"));

        List<string> parts = [];

        if (!string.IsNullOrWhiteSpace(template))
        {
            foreach (Match slot in Regex.Matches(template, @"\{([^{}]+)\}", RegexOptions.None, TimeSpan.FromSeconds(1)))
            {
                if (!columns.ContainsKey(slot.Groups[1].Value.Trim()))
                {
                    why = $"'addressTemplate' names {{{slot.Groups[1].Value}}}, which is not a column of the table. Its columns are "
                        + $"{named}. Nothing was written.";
                    return false;
                }
            }

            foreach (ImportedFeature row in table.Features)
            {
                lines.Add(Tidy(Regex.Replace(
                    template,
                    @"\{([^{}]+)\}",
                    slot => Value(row, columns[slot.Groups[1].Value.Trim()]),
                    RegexOptions.None,
                    TimeSpan.FromSeconds(1))));
            }

            return true;
        }

        if (string.IsNullOrWhiteSpace(fields))
        {
            why = "locationType 'address' needs 'addressTemplate' (such as \"{Address} {City}\") or 'addressFields' (such as "
                + "{\"Address\":\"Street\",\"City\":\"City\"}) to say which columns hold the address. Nothing was written.";
            return false;
        }

        Dictionary<string, string> roles = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            using JsonDocument document = JsonDocument.Parse(fields);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                why = "'addressFields' is a JSON object of address part to column name. Nothing was written.";
                return false;
            }

            foreach (JsonProperty role in document.RootElement.EnumerateObject())
            {
                string? column = role.Value.ValueKind == JsonValueKind.String ? role.Value.GetString() : null;

                if (Array.FindIndex(Roles, r => r.Equals(role.Name, StringComparison.OrdinalIgnoreCase)) < 0)
                {
                    why = $"'addressFields' names '{role.Name}', which is not an address part this server reads: "
                        + $"{string.Join(", ", Roles)}. Nothing was written.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(column) || !columns.TryGetValue(column, out string? actual))
                {
                    why = $"'addressFields' maps {role.Name} to '{column}', which is not a column of the table. Its columns are "
                        + $"{named}. Nothing was written.";
                    return false;
                }

                roles[role.Name] = actual;
            }
        }
        catch (JsonException e)
        {
            why = $"'addressFields' is not JSON: {e.Message}. Nothing was written.";
            return false;
        }

        foreach (string role in Roles)
        {
            if (roles.TryGetValue(role, out string? column)) parts.Add(column);
        }

        if (parts.Count == 0)
        {
            why = "'addressFields' maps no address part. Nothing was written.";
            return false;
        }

        foreach (ImportedFeature row in table.Features)
        {
            lines.Add(Tidy(string.Join(", ", parts.Select(column => Value(row, column)).Where(v => v.Length > 0))));
        }

        return true;
    }

    /// <summary>A cell as text; empty for a null or a missing one.</summary>
    private static string Value(ImportedFeature row, string column)
    {
        if (!row.Values.TryGetValue(column, out JsonElement cell))
        {
            return string.Empty;
        }

        return cell.ValueKind switch
        {
            JsonValueKind.String => cell.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => cell.ToString(),
        };
    }

    /// <summary>A line with its runs of blanks made one and its commas and ends trimmed, so an empty part leaves no trace.</summary>
    private static string Tidy(string line)
    {
        string tidy = Regex.Replace(line, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim().Trim(',', ' ');

        return Regex.Replace(tidy, @"\s*,(\s*,)+", ",", RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    private static async Task<ImportedDataset?> RefuseAsync(HttpContext context, int code, string message)
    {
        await Results.Json(new { error = new { code, message, details = Array.Empty<string>() } }, statusCode: code)
            .ExecuteAsync(context).ConfigureAwait(false);

        return null;
    }
}

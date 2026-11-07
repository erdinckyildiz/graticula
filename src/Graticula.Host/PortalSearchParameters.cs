using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Graticula.Host;

/// <summary>
/// A portal search's parameters beside <c>q</c> and paging — <c>categories</c> and <c>bbox</c>, answered, and
/// <c>filter</c>, refused by name.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ArcGIS veteran's review of 2026-11-02, E: the search read <c>q</c> and paging and nothing else.</b> A client
/// that narrowed a search by <c>bbox</c>, <c>categories</c> or <c>filter</c> was sent every item <c>q</c> matched, as
/// though the narrowing had been applied. That is the failure <see cref="PortalQuery"/> was written against: answering
/// everything to a question this server cannot answer tells the client a wrong thing at every step, silently.
/// </para>
/// <para>
/// <b><c>categories</c> is answered, because every item already carries its categories</b> as the paths
/// <c>categorySchema</c> names (ADR-202). The search reference's rule: one parameter is a comma-separated list read as
/// OR, and several parameters are read together as AND. The JS SDK sends the same as JSON — an array of paths (OR),
/// or an array whose inner arrays are each such a list (AND between them). A path is matched with or without its
/// <c>/Categories</c> root and in any case, and an item in a category below the one asked for is found by it —
/// <c>INFERRED</c> from how a portal's category filter behaves, for the owner to confirm.
/// </para>
/// <para>
/// <b><c>bbox</c> is answered since 2026-11-07 (ADR-271)</b>, after being refused because a search listing carries no
/// extent. The refusal's reason was cost — a listing would pay a read of every layer's shape — and that cost is paid on
/// the items <c>q</c> and <c>categories</c> already matched, not on every item on the server: the box is the last
/// narrowing (<see cref="PortalSpatialSearch"/>). <b><c>filter</c> is still refused</b>, because it is a second query
/// language beside <c>q</c>, and half of one is the wrong answer <see cref="PortalQuery"/> refuses. Both are read only
/// when they carry a value: an empty parameter asks nothing.
/// </para>
/// </remarks>
internal static class PortalSearchParameters
{
    /// <summary>The root every category path is under — <see cref="ItemAnnotations.Root"/>.</summary>
    private const string Root = "/" + ItemAnnotations.Root;

    /// <summary>The parameters refused by name when they carry a value.</summary>
    private static readonly string[] Unanswered = ["filter"];

    /// <summary>The narrowing asked for: categories as groups (AND between groups, OR within one), or a refusal.</summary>
    /// <param name="Categories">Each group of category paths; an item must be in one of every group.</param>
    /// <param name="Refused">Why the search is not answered, naming the parameters; null when it is.</param>
    /// <param name="Box">The <c>bbox</c> asked for, in WGS 84 — minx, miny, maxx, maxy — or null when none was.</param>
    internal sealed record Narrowing(IReadOnlyList<string[]> Categories, string? Refused, double[]? Box = null);

    /// <summary>Reads <c>categories</c>, <c>bbox</c> and <c>filter</c> from the query, or from the form when the query lacks them.</summary>
    public static Narrowing Of(HttpContext context, IFormCollection? form)
    {
        StringValues Parameter(string name)
        {
            StringValues value = context.Request.Query[name];
            return value.Count > 0 ? value : form?[name] ?? StringValues.Empty;
        }

        string[] refused =
        [
            .. Unanswered.Where(name => Parameter(name).Any(v => !string.IsNullOrWhiteSpace(v))),
        ];

        if (refused.Length > 0)
        {
            return new Narrowing([],
                "This portal's search does not answer 'filter': it is a second query language beside 'q'. Search "
                + "with 'q' instead, rather than be answered as though 'filter' had been applied.");
        }

        double[]? box = null;

        if (Parameter("bbox").FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) is { } asked)
        {
            if (Box(asked) is not { } read)
            {
                return new Narrowing([],
                    $"'bbox' is four numbers in degrees, minx,miny,maxx,maxy, with min below max; '{asked}' is not.");
            }

            box = read;
        }

        List<string[]> groups = [];

        foreach (string? value in Parameter("categories"))
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (Groups(value.Trim()) is not { } read)
            {
                return new Narrowing([], $"'categories' is a comma-separated list of category paths, or a JSON array of them; '{value}' is neither.");
            }

            groups.AddRange(read.Where(g => g.Length > 0));
        }

        return new Narrowing(groups, null, box);
    }

    /// <summary>A <c>bbox</c> as four numbers in degrees, or null when it is not one.</summary>
    /// <remarks>
    /// A box across the antimeridian (minx above maxx) is refused rather than read as the whole world: what it means is
    /// two boxes, and this answers one. <c>INFERRED</c> that degrees are what ArcGIS's search reads, as its reference
    /// gives the box with no reference of its own.
    /// </remarks>
    internal static double[]? Box(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);

        if (parts.Length != 4)
        {
            return null;
        }

        double[] box = new double[4];

        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out box[i])
                || !double.IsFinite(box[i]))
            {
                return null;
            }
        }

        return box[0] <= box[2] && box[1] <= box[3] ? box : null;
    }

    /// <summary>Whether an item is in at least one category of every group.</summary>
    public static bool InCategories(object item, IReadOnlyList<string[]> groups)
    {
        if (groups.Count == 0)
        {
            return true;
        }

        string[] held = PortalQuery.Field(item, "categories") is IEnumerable<string> paths ? [.. paths.Select(Path)] : [];

        return groups.All(group => group.Any(wanted =>
            held.Any(path => string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(wanted + "/", StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>One <c>categories</c> value as groups: a comma list is one group, a JSON array one or several.</summary>
    private static List<string[]>? Groups(string value)
    {
        if (!value.StartsWith('['))
        {
            return [[.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path)]];
        }

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(value);

            if (parsed.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            List<string> flat = [];
            List<string[]> groups = [];

            foreach (JsonElement element in parsed.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    flat.Add(Path(element.GetString()!));
                }
                else if (element.ValueKind == JsonValueKind.Array
                    && element.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String))
                {
                    groups.Add([.. element.EnumerateArray().Select(e => Path(e.GetString()!))]);
                }
                else
                {
                    return null;
                }
            }

            if (flat.Count > 0)
            {
                groups.Insert(0, [.. flat]);
            }

            return groups;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A category path with its root and without a trailing <c>/</c>, as <see cref="ItemAnnotations.Resolve"/> reads one.</summary>
    private static string Path(string named)
    {
        string path = named.Trim().TrimEnd('/');
        return path.StartsWith('/') ? path : $"{Root}/{path}";
    }
}

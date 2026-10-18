using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Graticula.Host;

/// <summary>
/// One page of a portal listing — search, groups, a group's content, a user's content — by ArcGIS's <c>start</c>,
/// <c>num</c>, <c>sortField</c> and <c>sortOrder</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by the veteran review of 2026-10-17 (V-83):</b> all four listings answered every result with
/// <c>nextStart: -1</c> whatever was asked, and ignored the sort. At the scale target (1,000 services, three items
/// each) that is every item in every answer, and a client that pages — Pro, the Maps SDK, the ArcGIS API for Python —
/// asked for ten and was sent three thousand.
/// </para>
/// <para>
/// <b>Without <c>num</c>, still every result</b> — <c>INFERRED</c>, for the owner to confirm. ArcGIS's default is ten;
/// every client named above sends <c>num</c>, and this server's own callers and tests that do not were written against
/// the whole list. The answer is consistent either way: <c>nextStart</c> says whether there is more.
/// </para>
/// <para>
/// <b>Paged after filtering, in memory.</b> The listings are built from objects the item filter reads by reflection
/// (<c>PortalQuery</c>), so the page is cut from those; the size of what is built is unchanged. A deployment with
/// enough items for that to matter is the trigger for doing it in SQL.
/// </para>
/// </remarks>
internal static class PortalPage
{
    /// <summary>ArcGIS's ceiling on <c>num</c>.</summary>
    public const int MaximumNum = 100;

    internal sealed record Page(IReadOnlyList<object> Results, int Total, int Start, int Num, int NextStart);

    /// <summary>Sorts and cuts <paramref name="all"/> by the request's paging parameters, from the query or the form.</summary>
    public static Page Of(IReadOnlyList<object> all, HttpContext context, IFormCollection? form = null)
    {
        string Parameter(string name)
        {
            StringValues value = context.Request.Query[name];
            return value.Count > 0 ? value.ToString() : form?[name].ToString() ?? string.Empty;
        }

        IEnumerable<object> sorted = Sort(all, Parameter("sortField"), Parameter("sortOrder"));

        int start = int.TryParse(Parameter("start"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s >= 1 ? s : 1;
        bool asked = int.TryParse(Parameter("num"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0;
        int num = asked ? Math.Min(n, MaximumNum) : Math.Max(all.Count - (start - 1), 0);

        List<object> results = [.. sorted.Skip(start - 1).Take(num)];
        int next = start - 1 + results.Count < all.Count && results.Count > 0 ? start + results.Count : -1;

        return new Page(results, all.Count, start, results.Count, next);
    }

    /// <summary>
    /// Orders by the fields <c>sortField</c> names, comma-separated, read from the serialised object as the filter reads
    /// it; a result without the field sorts after every value ascending (before them descending), and a field no
    /// result has leaves the order as it was.
    /// </summary>
    internal static IEnumerable<object> Sort(IReadOnlyList<object> all, string sortField, string sortOrder)
    {
        string[] fields = sortField.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (fields.Length == 0)
        {
            return all;
        }

        string[] orders = sortOrder.Split(',', StringSplitOptions.TrimEntries);
        IOrderedEnumerable<object>? ordered = null;

        for (int i = 0; i < fields.Length; i++)
        {
            string field = fields[i];
            bool descending = string.Equals(i < orders.Length ? orders[i] : orders.LastOrDefault(), "desc", StringComparison.OrdinalIgnoreCase);
            Func<object, IComparable?> key = item => ValueOf(item, field);

            ordered = ordered is null
                ? descending ? all.OrderByDescending(key, Comparer) : all.OrderBy(key, Comparer)
                : descending ? ordered.ThenByDescending(key, Comparer) : ordered.ThenBy(key, Comparer);
        }

        return ordered!;
    }

    private static readonly IComparer<IComparable?> Comparer = Comparer<IComparable?>.Create((a, b) =>
        (a, b) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            (string x, string y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase),
            _ when a.GetType() == b.GetType() => a.CompareTo(b),
            _ => string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase),
        });

    private static IComparable? ValueOf(object item, string field) =>
        item.GetType().GetProperty(field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?.GetValue(item) as IComparable;
}

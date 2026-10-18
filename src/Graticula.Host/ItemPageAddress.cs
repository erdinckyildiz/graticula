using System;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Graticula.Host;

/// <summary>
/// ArcGIS's item page — <c>home/item.html?id=…</c> — sent on to Studio's, <c>/studio/item/{id}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The link Pro hands out.</b> After a publish, Pro's <i>View item details</i> opens
/// <c>{portal}/home/item.html?id=…</c>, and so does every bookmark and e-mail an ArcGIS portal's users have; all three
/// spellings — at the root, under <c>/portal</c> and under <c>/studio</c> — answered 404. Owner decision of 2026-10-18,
/// relayed by the session building Studio's item page: the item page is addressed by its id, and this is the portal's
/// half of that.
/// </para>
/// <para>
/// <b>Any id that could be one</b> — letters, digits, <c>_</c> and <c>-</c>, at most 64 — goes on, found or not; the page
/// says when it is not. Anything else is not redirected, so the address cannot carry a path or a scheme into the
/// <c>Location</c>. The other query parameters go with it.
/// </para>
/// </remarks>
internal static partial class ItemPageAddress
{
    private static readonly string[] Pages = ["/home/item.html", "/portal/home/item.html", "/studio/home/item.html"];

    /// <summary>Where an item page request goes, or null when it is not one.</summary>
    public static string? Location(PathString path, IQueryCollection query)
    {
        if (!Pages.Any(page => path.Equals(page, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        string id = query["id"].ToString();

        if (!Id().IsMatch(id))
        {
            return null;
        }

        string location = $"/studio/item/{id}";

        foreach ((string key, Microsoft.Extensions.Primitives.StringValues values) in query)
        {
            if (string.Equals(key, "id", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string? value in values)
            {
                location = QueryHelpers.AddQueryString(location, key, value ?? string.Empty);
            }
        }

        return location;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Id();
}

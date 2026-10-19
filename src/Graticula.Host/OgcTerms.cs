using System;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// What an OGC document says about who may use a service — owner decision 2026-10-19.
/// </summary>
/// <remarks>
/// <para>
/// <b>The service's own OGC text when it has one (ADR-167), and its item's terms of use otherwise (ADR-202).</b> The
/// two were kept apart and neither knew of the other: a publisher who wrote <i>internal use only</i> on the item saw it in
/// Pro and the portal, while a QGIS user reading GetCapabilities was told nothing at all, because an empty OGC field wrote
/// no element. They stay two fields, so an OGC audience can still be told something different; most people write in one
/// place, and now that place reaches both.
/// </para>
/// <para>
/// <b>As text.</b> ArcGIS's <c>licenseInfo</c> is HTML, and a capabilities element is a string a client shows as it is.
/// </para>
/// </remarks>
internal static partial class OgcTerms
{
    /// <summary>The access constraints for the service whose item is <paramref name="item"/>, or null when none are stated.</summary>
    public static string? AccessConstraints(HttpContext context, Guid item, string? own)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (own is { Length: > 0 })
        {
            return own;
        }

        return context.RequestServices.GetService<ItemAnnotations>()?.LicenseOf(item) is { Length: > 0 } terms
            ? AsText(terms)
            : null;
    }

    /// <summary>An HTML fragment as the plain text a reader would see, or null when nothing is left.</summary>
    internal static string? AsText(string html)
    {
        string text = Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(html, " ")), " ").Trim();
        return text.Length > 0 ? text : null;
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

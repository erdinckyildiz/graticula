using System;
using System.Linq;

namespace Graticula.Api.ArcGis;

/// <summary>
/// What a service's named style may be called — ADR-094.
/// </summary>
/// <remarks>
/// <para>
/// <b>A name is a path segment before it is a label.</b> A named style is served at
/// <c>resources/styles/{name}.json</c>, so the rules are the ones that keep that address one
/// segment, readable, and the same in every client: letters, digits, <c>-</c> and <c>_</c>,
/// starting with a letter or digit, at most <see cref="MaximumLength"/> characters. Nothing in it
/// needs escaping, so a name is the same string in a URL, a log line and the console.
/// </para>
/// <para>
/// <b>Compared without case, stored as written.</b> <c>Dark</c> and <c>dark</c> would be two
/// styles to a case-sensitive store and one to anybody reading a list of them, and ArcGIS
/// addresses are matched without case in practice; the store's unique index says the same.
/// </para>
/// <para>
/// <b><c>root</c> is taken.</b> <c>resources/styles/root.json</c> is the ArcGIS address of the
/// default style, whatever it is called, so a style named <c>root</c> could never be reached by its
/// own name.
/// </para>
/// </remarks>
public static class StyleNames
{
    /// <summary>The longest name.</summary>
    public const int MaximumLength = 40;

    /// <summary>What the default style is called when it was stored without a name — through the <c>/style</c> route.</summary>
    public const string Default = "default";

    /// <summary>The name the ArcGIS address of the default style uses, which no stored style may take.</summary>
    public const string Root = "root";

    /// <summary>The most styles one service may carry, the default among them.</summary>
    /// <remarks>
    /// <b>A bound, not a target.</b> Light, dark, print and a high-contrast one is four; twenty is room
    /// for a real estate and still a list the console can show, and each style may be a megabyte.
    /// </remarks>
    public const int MostPerService = 20;

    /// <summary>Checks a name.</summary>
    /// <param name="name">The name as the route gave it.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>True when a style may be stored under it.</returns>
    public static bool TryValidate(string? name, out string? error)
    {
        error = null;

        if (string.IsNullOrEmpty(name))
        {
            error = "A style needs a name, as in light or dark.";
            return false;
        }

        if (name.Length > MaximumLength)
        {
            error = $"A style name is at most {MaximumLength} characters.";
            return false;
        }

        if (!char.IsAsciiLetterOrDigit(name[0])
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            error = $"'{name}' is not a style name. Use letters, digits, '-' and '_', starting with a letter "
                  + "or digit: the name is served as resources/styles/{name}.json, so it has to be one path "
                  + "segment that needs no escaping.";
            return false;
        }

        if (string.Equals(name, Root, StringComparison.OrdinalIgnoreCase))
        {
            error = "'root' is the ArcGIS address of whichever style is the default "
                  + "(resources/styles/root.json), so no style may be called that.";
            return false;
        }

        return true;
    }

    /// <summary>Whether two names are the same style.</summary>
    /// <param name="a">One name.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when they differ only in case.</returns>
    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

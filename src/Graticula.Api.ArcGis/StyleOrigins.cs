using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Graticula.Api.ArcGis;

/// <summary>
/// One origin an administrator allows a style to fetch from — scheme, host and port — ADR-094.
/// </summary>
/// <remarks>
/// <para>
/// <b>An origin, not a URL prefix.</b> A browser's own security boundary is the origin, and so is a
/// Content-Security-Policy source, which is built from this list for the console's pages. A prefix
/// with a path would promise a narrower rule than the browser can enforce: a style allowed
/// <c>https://tiles.example.com/public/</c> is a style whose TileJSON on that host may point
/// anywhere else on it.
/// </para>
/// <para>
/// <b>Always https.</b> A plain-http source is a source anybody on the path can replace, and the
/// style is handed to every viewer of the map.
/// </para>
/// </remarks>
/// <param name="Host">The host, in its ASCII (punycode) form, lower case, without a trailing dot.</param>
/// <param name="Port">The port; 443 when the entry named none.</param>
/// <param name="Subdomains">
/// True for a <c>*.</c> entry, which matches every name under <paramref name="Host"/> and not
/// <paramref name="Host"/> itself — the reading a Content-Security-Policy gives the same text.
/// </param>
public sealed record StyleOrigin(string Host, int Port, bool Subdomains)
{
    /// <summary>The entry as this server writes it back: <c>https://host[:port]</c>.</summary>
    /// <remarks>
    /// <b>Also a valid Content-Security-Policy source, on purpose.</b> The console's policy is built
    /// from these strings, so the rule the server checks and the rule the browser enforces are
    /// spelled once.
    /// </remarks>
    public string Text =>
        "https://" + (Subdomains ? "*." : string.Empty) + Host
        + (Port == StyleOrigins.HttpsPort ? string.Empty : ":" + Port.ToString(CultureInfo.InvariantCulture));

    /// <summary>Whether a host and port read out of a URL are inside this origin.</summary>
    /// <param name="host">The URL's host, as <see cref="StyleOrigins.TryReadUrl"/> gives it.</param>
    /// <param name="port">The URL's port.</param>
    /// <returns>True when a style may fetch it.</returns>
    public bool Matches(string host, int port) =>
        port == Port
        && (Subdomains
            ? host.Length > Host.Length + 1
              && host.EndsWith("." + Host, StringComparison.Ordinal)
            : string.Equals(host, Host, StringComparison.Ordinal));
}

/// <summary>
/// The rules for the external origins a style may name, and for reading a URL out of a style — ADR-094.
/// </summary>
/// <remarks>
/// <para>
/// <b>A tokenizer of our own, not <see cref="Uri"/>.</b> The question is which host a browser will
/// connect to, and <see cref="Uri"/> is a different parser from a browser's, with different
/// opinions about backslashes, user information and percent-encoded hosts. Rather than bet that
/// they agree on every input, this refuses every input on which they could disagree — anything
/// with <c>@</c>, <c>\</c>, <c>%</c>, whitespace or a control character in it — and reads only
/// what is left, which both parsers read the same way.
/// </para>
/// <para>
/// <b>The classic tricks are the tests.</b> <c>https://allowed.com@evil.com</c> is refused for its
/// <c>@</c>, not matched against either host. <c>https://allowed.com.evil.com</c> is a host that
/// ends in <c>evil.com</c>, and a match is on the whole host or on a whole-label suffix, never on a
/// prefix. International names are compared in their punycode form, so a Unicode spelling and its
/// ASCII one are one host.
/// </para>
/// </remarks>
public static class StyleOrigins
{
    /// <summary>The port an https URL means when it names none.</summary>
    public const int HttpsPort = 443;

    /// <summary>The most origins the list may hold.</summary>
    /// <remarks>
    /// <b>A bound on a list somebody reads.</b> Every entry is an origin every console page may
    /// fetch from, written into its Content-Security-Policy; fifty is far past any real deployment's
    /// basemap vendors and still a policy a person can audit.
    /// </remarks>
    public const int MostOrigins = 50;

    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true, AllowUnassigned = false };

    /// <summary>
    /// Reads one allowlist entry, as an administrator typed it.
    /// </summary>
    /// <param name="text">The entry: <c>https://host</c>, <c>https://host:port</c> or <c>https://*.host</c>.</param>
    /// <param name="origin">The entry, normalised.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>True when it is an origin this server will allow.</returns>
    public static bool TryParse(string? text, out StyleOrigin? origin, out string? error)
    {
        origin = null;
        string entry = (text ?? string.Empty).Trim();

        if (!entry.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            error = $"'{entry}' is not an https origin. Write it as https://host or https://host:port; a "
                  + "plain-http source can be replaced by anybody between the viewer and the host, and the "
                  + "style is handed to every viewer.";
            return false;
        }

        string authority = entry["https://".Length..];

        // A trailing slash is how an origin is often copied out of an address bar; anything after it is a
        // path, and an origin has none.
        if (authority.EndsWith('/'))
        {
            authority = authority[..^1];
        }

        if (authority.IndexOfAny(['/', '?', '#']) >= 0)
        {
            error = $"'{entry}' has a path. An allowed origin is a scheme, a host and a port — the unit a "
                  + "browser enforces — so write it as https://host.";
            return false;
        }

        if (!TryAuthority(authority, allowWildcard: true, out string host, out int port, out bool subdomains, out error))
        {
            error = $"'{entry}': {error}";
            return false;
        }

        origin = new StyleOrigin(host, port, subdomains);
        return true;
    }

    /// <summary>
    /// Reads a list of entries, refusing the whole list for its first bad entry.
    /// </summary>
    /// <param name="entries">The entries as sent.</param>
    /// <param name="origins">The list, normalised, each origin once, in the order given.</param>
    /// <param name="error">Why the list was refused.</param>
    /// <returns>True when every entry was an allowed origin.</returns>
    public static bool TryParseList(
        IEnumerable<string?>? entries, out IReadOnlyList<StyleOrigin> origins, out string? error)
    {
        List<StyleOrigin> read = [];
        origins = read;
        error = null;

        foreach (string? entry in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (!TryParse(entry, out StyleOrigin? origin, out error))
            {
                origins = [];
                return false;
            }

            if (!read.Contains(origin!))
            {
                read.Add(origin!);
            }
        }

        if (read.Count > MostOrigins)
        {
            origins = [];
            error = $"The list may hold at most {MostOrigins} origins; every one of them is written into the "
                  + "security policy of every console page.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the host and port a browser would connect to for a URL found in a style.
    /// </summary>
    /// <param name="url">The URL as the style has it.</param>
    /// <param name="host">The host, punycode, lower case, without a trailing dot.</param>
    /// <param name="port">The port, 443 when the URL names none.</param>
    /// <param name="error">Why the URL could not be read as an https origin.</param>
    /// <returns>True when the URL is https and its authority is unambiguous.</returns>
    public static bool TryReadUrl(string? url, out string host, out int port, out string? error)
    {
        host = string.Empty;
        port = 0;

        string text = url ?? string.Empty;

        if (!text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            error = "only an https URL can be allowed";
            return false;
        }

        // <b>Refused anywhere in the URL, not only in the host.</b> A browser strips tabs and newlines
        // from a URL before reading it and turns a backslash into a slash, so either can move where the
        // host ends; refusing them is cheaper than reproducing that and nobody writes them on purpose.
        foreach (char c in text)
        {
            if (c <= ' ' || c == '\\' || c == '\u007f')
            {
                error = "it contains whitespace, a control character or a backslash";
                return false;
            }
        }

        string rest = text["https://".Length..];
        int end = rest.IndexOfAny(['/', '?', '#']);
        string authority = end < 0 ? rest : rest[..end];

        if (!TryAuthority(authority, allowWildcard: false, out host, out port, out _, out error))
        {
            return false;
        }

        return true;
    }

    /// <summary>The origin a URL in a style would be fetched from, for a message; null when it has none.</summary>
    /// <param name="url">The URL.</param>
    /// <returns><c>https://host[:port]</c>, or null.</returns>
    public static string? OriginOf(string? url) =>
        TryReadUrl(url, out string host, out int port, out _)
            ? new StyleOrigin(host, port, Subdomains: false).Text
            : null;

    /// <summary>Whether an allowlist admits a URL.</summary>
    /// <param name="url">The URL as the style has it.</param>
    /// <param name="allowed">The allowlist.</param>
    /// <returns>True when the URL is https and its origin is on the list.</returns>
    public static bool Admits(string? url, IReadOnlyCollection<StyleOrigin>? allowed) =>
        allowed is { Count: > 0 }
        && TryReadUrl(url, out string host, out int port, out _)
        && allowed.Any(origin => origin.Matches(host, port));

    /// <summary>
    /// Reads <c>host[:port]</c>, or <c>*.host[:port]</c> when a wildcard is allowed.
    /// </summary>
    private static bool TryAuthority(
        string authority, bool allowWildcard, out string host, out int port, out bool subdomains, out string? error)
    {
        host = string.Empty;
        port = HttpsPort;
        subdomains = false;
        error = null;

        if (authority.Length == 0)
        {
            error = "it names no host";
            return false;
        }

        // <b>A whitelist of characters, not a blacklist of tricks.</b> `@` is user information and moves
        // the host; `%` is an encoded host a browser decodes; `[` starts an IPv6 literal, which a style
        // has no reason to use. Letters outside ASCII are an international name and go to IDNA below.
        foreach (char c in authority)
        {
            bool fine = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or ':' or '*'
                        || (c > '\u007f' && char.IsLetterOrDigit(c));

            if (!fine)
            {
                error = c == '@'
                    ? "it carries user information ('@'), which moves the host a browser connects to"
                    : $"its host contains '{c}', which is not part of a host name";
                return false;
            }
        }

        string name = authority;
        int colon = authority.IndexOf(':', StringComparison.Ordinal);

        if (colon >= 0)
        {
            string digits = authority[(colon + 1)..];
            name = authority[..colon];

            if (digits.Length is 0 or > 5
                || !digits.All(char.IsAsciiDigit)
                || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port is < 1 or > 65535)
            {
                error = "its port is not a number between 1 and 65535";
                return false;
            }
        }

        if (name.StartsWith("*.", StringComparison.Ordinal))
        {
            if (!allowWildcard)
            {
                error = "a URL cannot have a wildcard host";
                return false;
            }

            subdomains = true;
            name = name[2..];
        }

        if (name.Contains('*', StringComparison.Ordinal))
        {
            error = "a wildcard is allowed only as the whole first label, as in https://*.example.com";
            return false;
        }

        // One trailing dot is the fully qualified spelling of the same name, and a browser treats it as the
        // same host for this purpose; two are an empty label.
        if (name.EndsWith('.'))
        {
            name = name[..^1];
        }

        if (name.Length == 0)
        {
            error = "it names no host";
            return false;
        }

        try
        {
            name = Idn.GetAscii(name).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            error = "its host is not a valid host name";
            return false;
        }

        string[] labels = name.Split('.');

        if (labels.Any(label => label.Length == 0))
        {
            error = "its host has an empty label";
            return false;
        }

        if (IsNumeric(labels[^1]))
        {
            if (subdomains)
            {
                error = "a wildcard cannot stand in front of an IP address";
                return false;
            }

            if (!TryReadIPv4(labels, out byte[] address))
            {
                error = "its host ends in a number and is not a plain dotted IPv4 address; a browser would "
                      + "read it as one, so it is refused rather than guessed at";
                return false;
            }

            if (IsPrivate(address))
            {
                error = $"{name} is a private, loopback, link-local or reserved address. A style is fetched "
                      + "by every viewer's browser, from inside their network";
                return false;
            }
        }

        if (name is "localhost" || name.EndsWith(".localhost", StringComparison.Ordinal))
        {
            error = "localhost is every viewer's own machine";
            return false;
        }

        // `*.com` would allow every name under a top-level domain, which is not an origin anybody means.
        if (subdomains && labels.Length < 2)
        {
            error = "a wildcard needs at least two labels after it, as in https://*.example.com";
            return false;
        }

        host = name;
        return true;
    }

    /// <summary>Whether a label is one a browser would read as a number — decimal, or <c>0x</c> hex.</summary>
    private static bool IsNumeric(string label) =>
        label.All(char.IsAsciiDigit)
        || (label.StartsWith("0x", StringComparison.Ordinal) && label[2..].All(char.IsAsciiHexDigit));

    /// <summary>Four decimal parts of 0 to 255 with no leading zero — the only IPv4 spelling accepted.</summary>
    private static bool TryReadIPv4(string[] labels, out byte[] address)
    {
        address = new byte[4];

        if (labels.Length != 4)
        {
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            string part = labels[i];

            if (part.Length is 0 or > 3
                || !part.All(char.IsAsciiDigit)
                || (part.Length > 1 && part[0] == '0')
                || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                || value > 255)
            {
                return false;
            }

            address[i] = (byte)value;
        }

        return true;
    }

    /// <summary>
    /// Whether an IPv4 address is one no style should send a browser to: RFC 1918, loopback,
    /// link-local (where cloud metadata services answer), shared address space, and the reserved blocks.
    /// </summary>
    private static bool IsPrivate(byte[] a) =>
        a[0] is 0 or 10 or 127 or >= 224
        || (a[0] == 100 && a[1] is >= 64 and <= 127)
        || (a[0] == 169 && a[1] == 254)
        || (a[0] == 172 && a[1] is >= 16 and <= 31)
        || (a[0] == 192 && a[1] == 168)
        || (a[0] == 192 && a[1] == 0 && a[2] is 0 or 2)
        || (a[0] == 198 && a[1] is 18 or 19)
        || (a[0] == 198 && a[1] == 51 && a[2] == 100)
        || (a[0] == 203 && a[1] == 0 && a[2] == 113);
}

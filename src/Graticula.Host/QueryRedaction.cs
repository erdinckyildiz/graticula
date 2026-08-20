using System;
using System.Collections.Generic;
using System.Text;

namespace Graticula.Host;

/// <summary>
/// Removes credentials from a query string before anything writes it down.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-015](../../docs/adr/ADR-015-authentication.md) §4's first mitigation, and
/// it was owed the day <c>/generateToken</c> shipped.</b> That section accepts
/// <c>token=</c> in the query string because Q-17 requires unmodified Esri clients to
/// work, and it lists four mitigations as *all required*. The first is this one, in
/// its own words: *"redaction is the code path, and logging the raw query on a
/// token-bearing route is the bug."* ADR-015 condition 2 adds that the redaction
/// *"becomes due in the same change that adds `/generateToken`, not before"*.
/// </para>
/// <para>
/// <b>That change happened on 2026-08-20 and this did not.</b> The security gate
/// found live root session tokens in the server's own log, harvested one, and
/// replayed it against a private layer. Meanwhile
/// <see cref="Authentication"/>'s class remark still said the parameter was *not
/// accepted yet* — so the code disagreed with its own documentation, in the direction
/// of a guard that had been named as a precondition and then skipped.
/// </para>
/// <para>
/// <b>A code path, not a configuration.</b> ASP.NET's own request logging writes the
/// full URL before any middleware runs, so it is silenced and this replaces it. A
/// filter that merely lowered a log level would leave the raw query one
/// configuration change away from being written again, which is exactly the
/// *"configured to be"* ADR-015 refuses.
/// </para>
/// </remarks>
internal static class QueryRedaction
{
    /// <summary>What is written in place of a credential.</summary>
    public const string Placeholder = "REDACTED";

    /// <summary>
    /// Parameters that carry a credential and must never reach a log.
    /// </summary>
    /// <remarks>
    /// <b>Named rather than pattern-matched.</b> A heuristic over parameter names
    /// would redact a layer called <c>password_zones</c> and miss a credential called
    /// something else; this list is short because the surface is, and a new
    /// credential channel is a change that should have to add its name here.
    /// </remarks>
    private static readonly string[] Credentials =
    [
        "token",
        "password",
        "pwd",
        "secret",
        "api_key",
        "apikey",
        "access_token",
        "code",
    ];

    /// <summary>
    /// A query string with every credential value replaced.
    /// </summary>
    /// <remarks>
    /// <b>The parameter is kept and only its value goes.</b> A log line that dropped
    /// the parameter entirely would hide that a caller authenticated through the URL
    /// at all, which is the thing an operator most wants to see when deciding whether
    /// their logs have ever held a credential.
    /// </remarks>
    /// <param name="query">The query string, with or without its leading question mark.</param>
    /// <returns>The same query with credential values replaced.</returns>
    public static string Redact(string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return string.Empty;
        }

        bool leading = query[0] == '?';
        ReadOnlySpan<char> body = leading ? query.AsSpan(1) : query.AsSpan();

        if (body.Length == 0)
        {
            return leading ? "?" : string.Empty;
        }

        StringBuilder text = new(query.Length);

        if (leading)
        {
            text.Append('?');
        }

        bool first = true;

        foreach (Range range in body.Split('&'))
        {
            ReadOnlySpan<char> pair = body[range];

            if (!first)
            {
                text.Append('&');
            }

            first = false;

            int equals = pair.IndexOf('=');

            if (equals < 0)
            {
                text.Append(pair);
                continue;
            }

            ReadOnlySpan<char> name = pair[..equals];

            if (IsCredential(name))
            {
                text.Append(name).Append('=').Append(Placeholder);
            }
            else
            {
                text.Append(pair);
            }
        }

        return text.ToString();
    }

    /// <summary>Whether a parameter name carries a credential.</summary>
    /// <remarks>
    /// <b>Case-insensitive, because a query string's parameter names are.</b> Esri
    /// clients send <c>token</c>; the WMS and WFS surfaces read every parameter
    /// case-insensitively, so <c>TOKEN=</c> authenticates and would otherwise be
    /// logged in full.
    /// </remarks>
    private static bool IsCredential(ReadOnlySpan<char> name)
    {
        foreach (string credential in Credentials)
        {
            if (name.Equals(credential, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

using System;
using System.Collections.Generic;

namespace Graticula.Api.OgcFeatures;

/// <summary>
/// Reads and writes the entity tags that carry a feature's version.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why there is a parser here at all — <see href="../../docs/architecture-debt.md">D-186</see>.</b>
/// The version this server compares is a database value, and the header a client sends it back
/// in is an HTTP entity tag with its own syntax: quotes, a weakness prefix, a comma-separated
/// list, and <c>*</c>. Comparing the raw header text against a database value would mean
/// <c>"1234"</c> never equals <c>1234</c> and every conditional write would be refused — a
/// failure that looks like working optimistic concurrency until somebody notices no edit ever
/// succeeds.
/// </para>
/// <para>
/// <b>Strong comparison, because that is what <c>If-Match</c> requires.</b> RFC 9110 §13.1.1
/// says a weak tag never matches under <c>If-Match</c>. A weak tag means *the same feature,
/// perhaps not byte for byte*, which is precisely the thing an edit must not be built on: two
/// representations a cache may treat as equivalent can differ in the field being edited.
/// </para>
/// </remarks>
public static class EntityTags
{
    /// <summary>What an <c>If-Match</c> header asked for.</summary>
    /// <param name="Present">Whether the client sent the header at all.</param>
    /// <param name="Any">
    /// Whether the client sent <c>*</c>, which asks only that the feature exist.
    /// </param>
    /// <param name="Versions">
    /// The versions inside the tags, with the quoting removed; empty when
    /// <paramref name="Any"/> or when the header is absent or unusable.
    /// </param>
    /// <param name="Unusable">
    /// Whether the header was there and nothing in it could be read as a strong tag — a
    /// distinct case from absent, because it must not be treated as *no precondition*.
    /// </param>
    public readonly record struct Precondition(
        bool Present, bool Any, IReadOnlyList<string> Versions, bool Unusable);

    /// <summary>The entity tag for one version.</summary>
    /// <remarks>
    /// <b>Strong, and quoted, because both are load-bearing.</b> An unquoted tag is not a valid
    /// entity tag and a proxy may drop it; a weak one would never match the <c>If-Match</c> the
    /// client sends back, so the server would refuse every conditional write it invited.
    /// </remarks>
    /// <param name="version">The version, as the source reports it.</param>
    /// <returns>The header value.</returns>
    public static string For(string version) => "\"" + version.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";

    /// <summary>Reads an <c>If-Match</c> header.</summary>
    /// <remarks>
    /// <para>
    /// <b>Absent is not the same as unreadable.</b> An absent header means the client is not
    /// asking for a precondition and the write proceeds as it always has — that is what keeps
    /// this change compatible. A header that is present but says nothing this server can compare
    /// is a client that believes it is protected and is not, so it is reported as unusable and
    /// refused rather than quietly ignored.
    /// </para>
    /// <para>
    /// <b>Weak tags are dropped rather than refused</b> when a strong one is beside them: a
    /// list matches if any member does, so a request carrying both still has something to
    /// compare. A list of nothing but weak tags is unusable, which is the same answer RFC 9110
    /// gives — a weak tag can never satisfy <c>If-Match</c>.
    /// </para>
    /// </remarks>
    /// <param name="header">The raw header value, or null when it was not sent.</param>
    /// <returns>What it asked for.</returns>
    public static Precondition Read(string? header)
    {
        if (header is null)
        {
            return new Precondition(false, false, [], false);
        }

        string trimmed = header.Trim();

        if (trimmed.Length == 0)
        {
            return new Precondition(true, false, [], true);
        }

        if (trimmed == "*")
        {
            return new Precondition(true, true, [], false);
        }

        List<string> versions = [];

        foreach (string part in trimmed.Split(','))
        {
            string tag = part.Trim();

            // A weak tag never satisfies If-Match. Skipped rather than refused, because a
            // strong one alongside it still gives this request something to compare.
            if (tag.StartsWith("W/", StringComparison.Ordinal))
            {
                continue;
            }

            if (tag.Length >= 2 && tag[0] == '"' && tag[^1] == '"')
            {
                versions.Add(tag[1..^1]);
            }
        }

        return versions.Count == 0
            ? new Precondition(true, false, [], true)
            : new Precondition(true, false, versions, false);
    }
}

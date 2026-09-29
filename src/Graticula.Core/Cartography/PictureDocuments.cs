using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Graticula.Cartography;

/// <summary>
/// What a stored document carrying pictures derives to, kept per document text — ADR-099 §5.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured 2026-09-29 before it was added.</b> A stored renderer carrying 790 KB of pictures (a document
/// of 1.14 million characters) cost, per request that read it: <b>6.1 ms</b> for the FeatureServer layer
/// document's <c>drawingInfo</c>, <b>4.8 ms</b> for the tile style and <b>7.5 ms</b> to compile for a map —
/// of which 2.2 ms is parsing the JSON and 3.0 ms decoding, sniffing and hashing the pictures. A document
/// changes when it is written and is read on every such request, so the derivation is done once per
/// document text and handed out from here.
/// </para>
/// <para>
/// <b>Only documents with a picture in them</b>, which are the ones whose size makes the derivation cost
/// anything; every other document is derived as it always was, so nothing about an ordinary layer changes.
/// </para>
/// <para>
/// <b>Keyed by the text itself</b>, so a hit is an exact match and a rewritten document is a new entry — the
/// "stored document version" is its content. <b>Bounded by characters</b>, because the key is the document:
/// past <see cref="MostCharacters"/> every entry is forgotten and the next reads refill it.
/// </para>
/// </remarks>
internal static class PictureDocuments
{
    /// <summary>How many characters of document the caches hold between them before they are emptied.</summary>
    /// <remarks>Sixteen million, about a dozen layers at the per-layer bound: a handful of busy picture layers.</remarks>
    private const long MostCharacters = 16_000_000;

    private static readonly ConcurrentDictionary<(string Document, string Name, int Geometry), object> Derived = new();

    private static long _characters;

    /// <summary>Whether a document is one this cache is for.</summary>
    /// <param name="document">The stored document.</param>
    /// <returns>True when it carries a picture marker.</returns>
    public static bool Carries(string document) =>
        document.Contains("CIMPictureMarker", StringComparison.Ordinal);

    /// <summary>A derivation of a stored document, from the cache or computed into it.</summary>
    /// <typeparam name="T">What it derives to.</typeparam>
    /// <param name="document">The stored document.</param>
    /// <param name="name">What else the derivation depends on — the layer's name, and which derivation.</param>
    /// <param name="geometry">The layer's geometry.</param>
    /// <param name="derive">The derivation.</param>
    /// <returns>The kept answer. A caller that hands out a mutable part of it clones that part.</returns>
    public static T Get<T>(string document, string name, int geometry, Func<T> derive)
        where T : class
    {
        (string, string, int) key = (document, name, geometry);

        if (Derived.TryGetValue(key, out object? kept) && kept is T known)
        {
            return known;
        }

        T answer = derive();

        if (Interlocked.Add(ref _characters, document.Length) > MostCharacters)
        {
            Derived.Clear();
            Interlocked.Exchange(ref _characters, document.Length);
        }

        Derived.TryAdd(key, answer);

        return answer;
    }
}

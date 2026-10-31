using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Graticula.Platform.Postgres;

/// <summary>
/// One request's catalogue answers, so a request that resolves its service twice asks the store once — D-261.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured 2026-10-31 on the local fixture:</b> an anonymous FeatureServer layer document read the service — its
/// layers and its group layers, two statements — twice, once where the request is authorised and once where it is
/// answered, so four of its five catalogue statements were two questions asked twice. The second asking learns nothing:
/// nothing in a read writes the catalogue.
/// </para>
/// <para>
/// <b>Held for one request and no longer, which is why this is not the revocation decision D-261 is parked on.</b> A
/// sharing change, a stop or a delete reaches the next request exactly as it did; what is held is the answer this
/// request already acted on. The host opens a scope only for <c>GET</c> and <c>HEAD</c> under <c>/rest/services</c>, the
/// path D-261 measured, where no handler writes the catalogue.
/// </para>
/// </remarks>
public static class CatalogReadScope
{
    private static readonly AsyncLocal<ConcurrentDictionary<(Guid, string, string), CatalogAnswer>?> Current = new();

    /// <summary>Opens a scope for the rest of this asynchronous flow; disposing it closes it.</summary>
    /// <returns>The scope.</returns>
    public static IDisposable Begin()
    {
        Current.Value = new ConcurrentDictionary<(Guid, string, string), CatalogAnswer>();
        return new Scope();
    }

    /// <summary>The answer this request already had for a key, if a scope is open and it asked.</summary>
    internal static bool TryGet((Guid, string, string) key, out CatalogAnswer answer)
    {
        answer = default;
        return Current.Value is { } held && held.TryGetValue(key, out answer);
    }

    /// <summary>Keeps an answer the store gave, for the rest of the request; a blind one is never kept.</summary>
    internal static void Keep((Guid, string, string) key, CatalogAnswer answer)
    {
        if (!answer.Blind && Current.Value is { } held)
        {
            held[key] = answer;
        }
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Current.Value = null;
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Graticula.Host;

/// <summary>
/// An ArcGIS request's parameters, from its query string and — on a <c>POST</c> — its form.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every ArcGIS operation could only be asked for in a URL, and the REST specification
/// documents <c>POST</c> for all of them.</b>
/// [D-139](../../docs/architecture-debt.md): `exportImage`, `identify`, `query` and the
/// service documents all answered a bare <c>405</c> with an empty body, so a client whose
/// request did not fit in a URL — a long `where`, a drawing geometry, a rendering rule — had
/// no way to send it. Browsers cap URLs near 2,000 characters and some proxies lower.
/// </para>
/// <para>
/// <b>It implements <see cref="IQueryCollection"/> so that nothing downstream has to
/// change.</b> `FeatureServerQueryParameters.TryParse` takes a query collection and is a
/// thousand lines of careful parsing; giving it a merged view is a smaller and safer move
/// than teaching it a second source. The faces that read parameters one at a time get
/// <see cref="Lookup"/> instead, which replaces two hand-rolled case-insensitive loops that
/// had drifted into separate copies in `ImageServerEndpoints` and `MapServerEndpoints`.
/// </para>
/// <para>
/// <b>The query wins over the form when a name is in both.</b> Not because either is more
/// correct, but because something has to, and a URL is the half a reader can see: a request
/// whose visible `f=json` was overridden by an invisible `f=image` in the body would be
/// unexplainable from the outside.
/// </para>
/// <para>
/// <b>This does not make a cookie work for <c>POST</c>, and that is deliberate.</b>
/// `Authentication.CookieToken` refuses anything but <c>GET</c> and <c>HEAD</c> — a stated
/// trade, argued there at length, whose whole point is that a forged cross-site request can
/// only ever read. Accepting a posted parameter does not touch it: a cross-site <c>POST</c>
/// still arrives anonymous and still sees only what is public. **A token must therefore
/// travel in the `Authorization` header or the query string, never in the form body**, which
/// is what ArcGIS clients do anyway, and which is why `Authentication` is not asked to read
/// the body — doing so would consume it before the endpoint could.
/// </para>
/// </remarks>
internal sealed class ArcGisParameters : IQueryCollection
{
    private readonly IQueryCollection _query;
    private readonly IFormCollection? _form;

    private ArcGisParameters(IQueryCollection query, IFormCollection? form)
    {
        _query = query;
        _form = form;
    }

    /// <inheritdoc/>
    public int Count => Keys.Count;

    /// <inheritdoc/>
    public ICollection<string> Keys
    {
        get
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

            foreach (string name in _query.Keys)
            {
                names.Add(name);
            }

            if (_form is not null)
            {
                foreach (string name in _form.Keys)
                {
                    names.Add(name);
                }
            }

            return names;
        }
    }

    /// <inheritdoc/>
    public StringValues this[string key] =>
        TryGetValue(key, out StringValues value) ? value : StringValues.Empty;

    /// <summary>Reads the parameters of a request, form included when it has one.</summary>
    /// <param name="context">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The merged view.</returns>
    /// <remarks>
    /// <b>The form is read only when the request says it has one.</b>
    /// `HasFormContentType` is the check, so a <c>POST</c> carrying JSON or an image is not
    /// parsed as a form — which would throw — and a <c>GET</c> costs nothing at all.
    /// </remarks>
    public static async Task<ArcGisParameters> ReadAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IFormCollection? form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false)
            : null;

        return new ArcGisParameters(context.Request.Query, form);
    }

    /// <summary>Reads one parameter at a time, case-insensitively.</summary>
    /// <param name="context">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A lookup that answers null for a name that was not sent.</returns>
    public static async Task<Func<string, string?>> LookupAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        ArcGisParameters parameters =
            await ReadAsync(context, cancellationToken).ConfigureAwait(false);

        return parameters.Lookup;
    }

    /// <inheritdoc/>
    public bool ContainsKey(string key) => TryGetValue(key, out _);

    /// <inheritdoc/>
    public bool TryGetValue(string key, out StringValues value)
    {
        // <b>Matched case-insensitively on both sides.</b> ASP.NET's own query collection is
        // already ordinal-ignore-case, and the two faces this replaces both looped over the
        // pairs comparing names that way — so the behaviour is kept rather than introduced.
        // A form collection is ordinal-ignore-case too, but looping keeps the two halves
        // reading the same way.
        foreach (KeyValuePair<string, StringValues> pair in _query)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        if (_form is not null)
        {
            foreach (KeyValuePair<string, StringValues> pair in _form)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
        }

        value = StringValues.Empty;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, StringValues>> GetEnumerator()
    {
        // <b>Every name once, the query's value winning.</b> A consumer that enumerates —
        // and `FeatureServerQueryParameters` does, to report a parameter it was sent and
        // ignored — must not see the same name twice with different values.
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, StringValues> pair in _query)
        {
            seen.Add(pair.Key);
            yield return pair;
        }

        if (_form is null)
        {
            yield break;
        }

        foreach (KeyValuePair<string, StringValues> pair in _form)
        {
            if (!seen.Contains(pair.Key))
            {
                yield return pair;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private string? Lookup(string name) =>
        TryGetValue(name, out StringValues value) ? value.ToString() : null;
}

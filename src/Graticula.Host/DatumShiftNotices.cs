using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Which layers this server has served across a datum, and to which reference.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-141](../../docs/open-questions.md), owner decision 2026-08-25: *"Operatöre söyle —
/// günlük ve /admin"*.</b> The question was where a datum-shift caution goes on a surface
/// Esri defined, and it listed three shapes — the service document, a non-standard member on
/// `query`, or refusing the transform. The answer is none of them, and the reason it is a
/// better answer than any of the three is who the caution is *for*.
/// </para>
/// <para>
/// <b>The caution is not actionable by the client, and it is actionable by the operator.</b>
/// A client asking for `outSR=4326` cannot install PROJ's shift grids, cannot choose a
/// pipeline, and — on the tile path — cannot read a caution at all, because a protobuf tile
/// has nowhere to put one. The person who can do something is the one who administers the
/// datastore, and this server already has two channels aimed at exactly them: the log and
/// `/admin/health`. Sending it there costs no compatibility risk at all, which is what
/// [Q-17](../../docs/open-questions.md) spends and what the other three shapes were asking
/// to spend.
/// </para>
/// <para>
/// <b>Once per layer and target reference, not once per request.</b>
/// [D-32](../../docs/architecture-debt.md)'s failure has no error, no log line and no visual
/// signature — the map looks right and is in the wrong place — so the first line is worth a
/// great deal and the ten-thousandth is worth less than nothing: a warning that repeats on
/// every request is a warning an operator filters out, and then the channel is gone. The
/// pair is the unit because it is what an operator would act on. *This layer, served as
/// that reference, crosses a datum* is a sentence somebody can check grids against.
/// </para>
/// <para>
/// <b>Bounded, like every other long-lived collection here.</b> The key space is layers
/// times references a caller may ask for, and the second half is attacker-controlled: a
/// caller naming ten thousand SRIDs would otherwise grow this without limit. At the ceiling
/// it stops recording and says so, rather than evicting — evicting would let the same
/// notice be logged again later, which is the repetition this exists to avoid, and an
/// operator reading a truncated list needs to know it is truncated more than they need its
/// ten-thousandth row.
/// </para>
/// <para>
/// <b>What it does not do.</b> It does not refuse, does not slow the request down by more
/// than one cached lookup, and says nothing on the response. A transformation this server
/// cannot verify is still performed, because refusing would break every client that asks
/// for one today and the honest position — geometry-crs-policy §3 — is that a documented
/// default is not the problem and a silent one is.
/// </para>
/// </remarks>
internal sealed class DatumShiftNotices
{
    /// <summary>How many distinct layer-and-reference pairs are remembered.</summary>
    /// <remarks>
    /// <b>Sized for an operator, not for a corpus.</b> The scale target is 100–1,000
    /// services ([CLAUDE.md §7](../../CLAUDE.md)); a deployment whose layers cross a datum
    /// into more than 256 distinct references has an answer long before the 257th row, and
    /// a list nobody can read is not a better warning than a list plus the word
    /// <c>truncated</c>.
    /// </remarks>
    public const int Ceiling = 256;

    private readonly ConcurrentDictionary<(Guid Layer, int To), Notice> _seen = new();

    /// <summary>Whether the ceiling was reached and notices are being dropped.</summary>
    public bool Truncated { get; private set; }

    /// <summary>
    /// Records that a layer was served in another reference, and logs the first time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Failures here are swallowed on purpose, and this is the only place in this file
    /// where that is true.</b> The notice is an aside on a request that is about to answer
    /// correctly; a projection database that cannot be read, or a circuit breaker that is
    /// open, must not turn a working query into a 500 because the server wanted to warn
    /// somebody. That is D-152's shape — a cosmetic check that could stop the thing it was
    /// commenting on — and it is worth naming here rather than rediscovering.
    /// </para>
    /// <para>
    /// <b>Nothing is recorded when the answer is not known</b>, so a refusal during an
    /// outage does not become a permanent *this pair is fine*: the pair is simply not in the
    /// dictionary and the next request asks again.
    /// </para>
    /// </remarks>
    /// <param name="layerId">The layer's identifier.</param>
    /// <param name="layerName">Its name, for the operator to read.</param>
    /// <param name="fromSrid">The reference the data is in.</param>
    /// <param name="toSrid">The reference it was served in.</param>
    /// <param name="projector">Answers whether the pair crosses a datum.</param>
    /// <param name="log">Where the first sighting goes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task NoteAsync(
        Guid layerId,
        string layerName,
        int fromSrid,
        int toSrid,
        IProjector projector,
        ILogger log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(log);

        if (fromSrid == toSrid || _seen.ContainsKey((layerId, toSrid)))
        {
            return;
        }

        if (_seen.Count >= Ceiling)
        {
            Truncated = true;
            return;
        }

        ProjectionProvenance provenance;

        try
        {
            provenance = await projector
                .DescribeAsync(fromSrid, toSrid, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // <b>An aside must not be able to fail a request.</b> D-152 is the row about the
            // last time a cosmetic check propagated out of the thing it was commenting on.
            Log.DatumShiftUnknown(log, layerName, toSrid, failure);
            return;
        }

        // <b>Both true and null are worth a line, and false is not.</b> A pair whose datums
        // could not be read is precisely the case an operator should look at, and treating
        // *could not tell* as *fine* is how D-32's failure stays invisible. 4326 to 3857 is
        // one datum and reports false, which is what keeps this list worth reading.
        if (provenance.DatumShift is false)
        {
            return;
        }

        Notice notice = new(
            layerName,
            fromSrid,
            toSrid,
            provenance.DatumShift ?? false,
            provenance.Caution ?? "This server could not determine whether this crossed a datum.");

        if (_seen.TryAdd((layerId, toSrid), notice))
        {
            Log.DatumShiftServed(log, layerName, fromSrid, toSrid, notice.Caution);
        }
    }

    /// <summary>What <c>/admin/health</c> reports, ordered so it reads the same twice.</summary>
    public IReadOnlyList<Notice> Report() =>
        [.. _seen.Values
            .OrderBy(n => n.Layer, StringComparer.Ordinal)
            .ThenBy(n => n.ServedAs)];

    /// <summary>One layer served in one reference across a datum.</summary>
    /// <param name="Layer">The layer's name.</param>
    /// <param name="StoredAs">The reference the data is in.</param>
    /// <param name="ServedAs">The reference it was asked for in.</param>
    /// <param name="CrossesDatum">
    /// True when the two datums differ, false when the server could not read one of them —
    /// which is a different thing from knowing they match, and is why the caution is carried
    /// beside it rather than derived from it.
    /// </param>
    /// <param name="Caution">What to tell whoever administers the datastore.</param>
    public sealed record Notice(
        string Layer, int StoredAs, int ServedAs, bool CrossesDatum, string Caution);
}

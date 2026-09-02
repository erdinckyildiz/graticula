using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;

namespace Graticula.Features;

/// <summary>A feature to insert.</summary>
/// <param name="Attributes">Column values, by column name. The object id is not among them.</param>
/// <param name="Geometry">Its shape, or null for an attribute-only row.</param>
public sealed record FeatureAdd(IReadOnlyDictionary<string, object?> Attributes, Geometry? Geometry);

/// <summary>A feature to change.</summary>
/// <param name="Identity">Which row, by its integer identity.</param>
/// <param name="Attributes">The columns to change. Absent columns are left alone.</param>
/// <param name="Geometry">
/// The new shape, or null to leave the existing one untouched.
/// </param>
/// <remarks>
/// <b>Null geometry means "unchanged", not "clear it".</b> ArcGIS clients
/// routinely send an attribute-only update with no geometry member, and reading
/// that as *set the shape to null* would erase the feature's location on every
/// attribute edit. Clearing a geometry is not expressible here, deliberately —
/// it needs an explicit operation rather than an omission.
/// </remarks>
public sealed record FeatureUpdate(
    long Identity, IReadOnlyDictionary<string, object?> Attributes, Geometry? Geometry);

/// <summary>What happened to one feature.</summary>
/// <param name="Identity">Its integer identity, or -1 when it never got one.</param>
/// <param name="Succeeded">Whether it worked.</param>
/// <param name="Error">Why not.</param>
/// <param name="NoSuchFeature">
/// Whether the failure is that the row was not there. <b>A distinct kind, because one
/// caller answers it differently</b> — an OGC API Features verb addresses a single
/// feature by URL, and an unknown URL is 404 rather than 400.
/// </param>
/// <param name="VersionMoved">
/// Whether the write was refused because the row is no longer at the version the caller
/// said it expected.
/// <para>
/// <b>Distinct from <see cref="NoSuchFeature"/> because the two are different answers to
/// the client — D-186.</b> A row that is not there is <c>404</c> and the client should stop
/// asking; a row somebody else has written since is <c>412</c> and the client should re-read
/// and try again. Collapsing them would tell a client its edit is impossible when the truth
/// is that it is merely out of date.
/// </para>
/// </param>
public readonly record struct EditResult(
    long Identity,
    bool Succeeded,
    string? Error,
    bool NoSuchFeature = false,
    bool VersionMoved = false)
{
    /// <summary>A success.</summary>
    public static EditResult Ok(long objectId) => new(objectId, true, null);

    /// <summary>A failure, with a reason the caller can act on.</summary>
    public static EditResult Failed(long objectId, string error) => new(objectId, false, error);

    /// <summary>
    /// The row was not there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own kind of failure because one caller has to answer it differently.</b>
    /// ArcGIS reports every failure the same way — a result with an error — and that is
    /// right for a batch. OGC API Features addresses one feature by URL, and an unknown
    /// URL is <c>404</c> rather than <c>400</c>: telling a client its request was
    /// malformed when the feature simply is not there sends it looking for a mistake it
    /// did not make.
    /// </para>
    /// <para>
    /// <b>A flag rather than a matched string, and that is the point.</b> The first
    /// version of the OGC delete read the writer's message looking for *no such* or *not
    /// found*; the writer says <em>No feature with object id 5 exists</em>, so a missing
    /// row answered 400 — a defect that would have come back the day somebody reworded
    /// the sentence. The producer of the fact says what it is.
    /// </para>
    /// </remarks>
    /// <param name="objectId">Which row was asked for.</param>
    /// <returns>The result.</returns>
    public static EditResult Missing(long objectId) =>
        new(objectId, false, $"No feature with object id {objectId} exists.", true);

    /// <summary>
    /// The row is there, but not at the version the caller expected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The whole point of the precondition — D-186.</b> Without this, a second writer's
    /// edit lands on top of a first writer's and nothing anywhere says so: the first client
    /// got a success, and its work is gone. Refusing is the only outcome that lets it find
    /// out.
    /// </para>
    /// <para>
    /// <b>The message names the fix rather than the fault.</b> The client is not wrong — it
    /// read a version and sent it back — it is out of date, and what it needs to be told is
    /// to read again.
    /// </para>
    /// </remarks>
    /// <param name="objectId">Which row was asked for.</param>
    /// <returns>The result.</returns>
    public static EditResult Stale(long objectId) =>
        new(
            objectId,
            false,
            $"Feature {objectId} has changed since the version you asked to edit. "
            + "Re-read it and apply your edit to the current version.",
            false,
            true);
}

/// <summary>One <c>applyEdits</c> call.</summary>
/// <param name="Adds">Features to insert.</param>
/// <param name="Updates">Features to change.</param>
/// <param name="Deletes">Object ids to remove.</param>
/// <param name="RollbackOnFailure">
/// Whether one failure abandons the whole batch.
/// </param>
/// <param name="AlreadyFailed">
/// How many features failed before the writer saw the batch — see
/// <see cref="AnythingAlreadyFailed"/>.
/// </param>
/// <param name="Expects">
/// The versions each identity may be at for its edit to apply, for the edits that carry a
/// precondition, or null when none do.
/// <para>
/// <b><see href="../../../docs/architecture-debt.md">D-186</see>, and
/// <see href="../../../docs/adr/ADR-005-api-architecture.md">ADR-005</see> condition 4 calls
/// getting this wrong <em>the worst defect class an editing API can have</em>.</b> Without a
/// precondition, <c>PUT</c>, <c>PATCH</c> and <c>DELETE</c> are last-write-wins with no status
/// code, no log line and no difference a client can see: the loser's edit is simply not there
/// afterwards.
/// </para>
/// <para>
/// <b>One map on the batch rather than a field on each edit.</b> An identity is unique within a
/// batch, so one lookup answers for an update and a delete alike, and the two paths cannot
/// drift into disagreeing about where the expectation lives. It is optional and null by
/// default, so every existing caller means <em>no precondition</em> without being touched —
/// which is also the compatible behaviour: a client that sends no <c>If-Match</c> gets what it
/// got before.
/// </para>
/// <para>
/// <b>A list per identity, because <c>If-Match</c> is a list.</b> RFC 9110 §13.1.1 lets a
/// client send several entity tags and the precondition passes if <em>any</em> of them matches.
/// A single value here would have made this server answer 412 to a request the specification
/// says must succeed, and the shape of the field is what makes that impossible rather than a
/// rule somebody has to remember.
/// </para>
/// <para>
/// <b>Opaque.</b> The writer compares these and never parses them. Today one is PostgreSQL's
/// <c>xmin</c>; §3.8 requires it to be something the <em>database</em> maintains, because
/// anyone with credentials can write around this server and a version we remember would be
/// wrong exactly when it matters.
/// </para>
/// </param>
/// <remarks>
/// <b>The default is true, which is not ArcGIS's default.</b> ArcGIS defaults to
/// partial application, and there is a reason to prefer it — a bulk sync of ten
/// thousand features should not lose nine thousand nine hundred good ones to a
/// single bad row. But partial application leaves the client responsible for
/// reconciling a half-applied batch, and a client that does not read the
/// per-feature results has silently lost data it believes it saved. Defaulting
/// to all-or-nothing makes the dangerous mode a deliberate request.
/// </remarks>
public sealed record EditBatch(
    IReadOnlyList<FeatureAdd> Adds,
    IReadOnlyList<FeatureUpdate> Updates,
    IReadOnlyList<long> Deletes,
    bool RollbackOnFailure = true,
    int AlreadyFailed = 0,
    IReadOnlyDictionary<long, IReadOnlyList<string>>? Expects = null)
{
    /// <summary>How many features this batch touches.</summary>
    public int Count => Adds.Count + Updates.Count + Deletes.Count;

    /// <summary>
    /// Whether anything has already failed before the writer sees the batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Set from features the request layer could not even parse</b> — an
    /// unreadable geometry, a wrong spatial reference, a column that is not
    /// there. Those never reach the writer, so without this the writer sees a
    /// batch in which everything succeeded and commits it.
    /// </para>
    /// <para>
    /// <b>Which made all-or-nothing a lie.</b> A client that asked for it, and
    /// sent one unparseable feature among ten good ones, got the ten applied and
    /// <c>rolledBack: false</c> — precisely the half-applied batch the default
    /// exists to prevent. Found by sending a geometry in the wrong spatial
    /// reference and watching the other feature survive.
    /// </para>
    /// </remarks>
    public bool AnythingAlreadyFailed => AlreadyFailed > 0;

    /// <summary>Whether it asks for anything at all.</summary>
    public bool IsEmpty => Count == 0;
}

/// <summary>What happened to a batch.</summary>
/// <param name="Adds">One result per add, in order.</param>
/// <param name="Updates">One result per update, in order.</param>
/// <param name="Deletes">One result per delete, in order.</param>
/// <param name="RolledBack">Whether nothing was applied because something failed.</param>
public sealed record EditOutcome(
    IReadOnlyList<EditResult> Adds,
    IReadOnlyList<EditResult> Updates,
    IReadOnlyList<EditResult> Deletes,
    bool RolledBack)
{
    /// <summary>Whether every edit in the batch succeeded.</summary>
    public bool AllSucceeded
    {
        get
        {
            foreach (IReadOnlyList<EditResult> results in (IReadOnlyList<EditResult>[])[Adds, Updates, Deletes])
            {
                foreach (EditResult result in results)
                {
                    if (!result.Succeeded)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}

/// <summary>
/// Applies edits to a layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="IFeatureSource"/>, because most sources are not
/// writable.</b> A registered database may grant select and nothing else, and a
/// read-only source that had to implement a write method would implement it by
/// throwing — which turns a capability question into a runtime surprise. A
/// provider that cannot write simply does not offer this.
/// </para>
/// <para>
/// <b>Every implementation must enforce ADR-008 §4.5a</b>: a write is refused
/// when the geometry it carries came from a representation that lost
/// information. Concretely, a two-dimensional geometry must not overwrite one
/// that has Z or M, because the client read it flat and never knew the third
/// ordinate was there.
/// </para>
/// </remarks>
public interface IFeatureWriter
{
    /// <summary>Applies a batch and reports what happened to each feature.</summary>
    /// <remarks>
    /// Returns results rather than throwing for an edit that fails on its own
    /// merits — a bad geometry, a missing row, a constraint violation. It may
    /// still throw for a failure that applies to the whole batch, such as the
    /// database being unreachable.
    /// </remarks>
    Task<EditOutcome> ApplyAsync(EditBatch batch, CancellationToken cancellationToken);
}

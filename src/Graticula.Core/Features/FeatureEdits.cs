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
/// <param name="ObjectId">Which row.</param>
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
    long ObjectId, IReadOnlyDictionary<string, object?> Attributes, Geometry? Geometry);

/// <summary>What happened to one feature.</summary>
/// <param name="ObjectId">Its id, or -1 when it never got one.</param>
/// <param name="Succeeded">Whether it worked.</param>
/// <param name="Error">Why not.</param>
/// <param name="NoSuchFeature">
/// Whether the failure is that the row was not there. <b>A distinct kind, because one
/// caller answers it differently</b> — an OGC API Features verb addresses a single
/// feature by URL, and an unknown URL is 404 rather than 400.
/// </param>
public readonly record struct EditResult(
    long ObjectId, bool Succeeded, string? Error, bool NoSuchFeature = false)
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
    int AlreadyFailed = 0)
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

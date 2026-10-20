using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;

namespace Graticula.Host;

/// <summary>
/// Removes the attachments of the features an edit deleted, for a registered layer that keeps them in the datastore —
/// ADR-207 — where no foreign key reaches the layer's table to cascade.
/// </summary>
/// <remarks>
/// <b>After the edit, and only for what it kept.</b> A rolled-back batch deleted nothing; a delete that failed deleted
/// nothing. An attachment left by a removal that itself fails here is the hourly sweep's to find, as one left by a
/// delete made with another tool is.
/// </remarks>
/// <param name="inner">The layer's writer.</param>
/// <param name="remove">Removes the attachments of these features.</param>
internal sealed class AttachmentRemovingWriter(
    IFeatureWriter inner,
    Func<IReadOnlyList<long>, CancellationToken, Task<long>> remove) : IFeatureWriter
{
    /// <inheritdoc/>
    public async Task<EditOutcome> ApplyAsync(EditBatch batch, CancellationToken cancellationToken)
    {
        EditOutcome outcome = await inner.ApplyAsync(batch, cancellationToken).ConfigureAwait(false);

        long[] deleted = Deleted(outcome);

        if (deleted.Length > 0)
        {
            await remove(deleted, cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>The features an outcome deleted and kept deleted.</summary>
    /// <param name="outcome">What an edit did.</param>
    /// <returns>Their object ids.</returns>
    public static long[] Deleted(EditOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return outcome.RolledBack ? [] : [.. outcome.Deletes.Where(d => d.Succeeded).Select(d => d.Identity)];
    }
}

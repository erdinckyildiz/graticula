using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Features;

/// <summary>One attachment, without its bytes.</summary>
/// <param name="Id">Its identity within the layer.</param>
/// <param name="FeatureId">The feature it belongs to.</param>
/// <param name="Name">The name the uploader gave it, treated as data.</param>
/// <param name="ContentType">What the bytes actually are, as this server determined.</param>
/// <param name="DeclaredContentType">What the uploader claimed. Kept, never trusted.</param>
/// <param name="Size">How many bytes.</param>
/// <param name="UploadedAt">When.</param>
public readonly record struct AttachmentInfo(
    int Id,
    long FeatureId,
    string Name,
    string ContentType,
    string? DeclaredContentType,
    long Size,
    DateTimeOffset UploadedAt);

/// <summary>How much of a layer's attachment quota is gone.</summary>
/// <param name="Used">Bytes stored.</param>
/// <param name="Quota">Bytes allowed.</param>
public readonly record struct AttachmentUsage(long Used, long Quota)
{
    /// <summary>Whether another attachment of this size would fit.</summary>
    /// <param name="size">Its size.</param>
    /// <returns>Whether it fits.</returns>
    public bool Admits(long size) => Used + size <= Quota;
}

/// <summary>
/// Files attached to features, stored beside them and never held in memory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every signature here takes or returns a <see cref="Stream"/>, and that is
/// the decision ADR-013 §4a rests on.</b> An attachment is arbitrarily large and
/// user-supplied. Materialising one into a <c>byte[]</c> puts it straight on the
/// large object heap and reproduces A-037's measured ceiling — 80.9% GC pause at
/// 18% CPU — except the user chooses the size. A convenience overload returning
/// bytes would be used, and would be the end of that guarantee.
/// </para>
/// <para>
/// <b>The cost of storing bytes in the database is a held connection</b>
/// (§4b). Streaming out means a pooled connection is open for as long as the
/// client takes to read, and a client reading one byte per second holds it
/// indefinitely. That is slowloris pointed at the connection pool, and the
/// mitigation is that attachment reads draw from a separate bounded pool so
/// exhausting it degrades attachments rather than the whole layer.
/// </para>
/// </remarks>
public interface IAttachmentStore
{
    /// <summary>Lists what is attached to a feature, without reading any of it.</summary>
    /// <param name="featureId">The feature.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The attachments.</returns>
    Task<IReadOnlyList<AttachmentInfo>> ListAsync(
        long featureId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens an attachment for reading.
    /// </summary>
    /// <param name="attachmentId">Which one.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// Its metadata and a stream over its bytes, or null if there is no such
    /// attachment. <b>The caller must dispose the result</b>, which releases the
    /// database connection the stream is reading through.
    /// </returns>
    Task<OpenAttachment?> OpenAsync(int attachmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores an attachment, reading it from a stream as it arrives.
    /// </summary>
    /// <param name="featureId">The feature it belongs to.</param>
    /// <param name="name">The uploader's name for it.</param>
    /// <param name="contentType">What the bytes were determined to be.</param>
    /// <param name="declaredContentType">What the uploader claimed.</param>
    /// <param name="content">The bytes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The new attachment's id.</returns>
    /// <remarks>
    /// <b>The quota is enforced inside the write.</b> Checking before means a
    /// concurrent upload can slip past between the check and the insert, and
    /// checking after without a transaction means the bytes are already on
    /// disk. Both are done in one transaction that rolls back.
    /// </remarks>
    Task<int> AddAsync(
        long featureId,
        string name,
        string contentType,
        string? declaredContentType,
        Stream content,
        CancellationToken cancellationToken);

    /// <summary>Removes attachments by id.</summary>
    /// <param name="attachmentIds">Which ones.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The ids that existed and were removed.</returns>
    Task<IReadOnlyList<int>> DeleteAsync(
        IReadOnlyList<int> attachmentIds, CancellationToken cancellationToken);

    /// <summary>How much of the layer's quota is used.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The usage.</returns>
    Task<AttachmentUsage> UsageAsync(CancellationToken cancellationToken);
}

/// <summary>
/// An attachment open for reading, holding the connection it reads through.
/// </summary>
/// <remarks>
/// <b>Disposing this is what releases the database connection.</b> A caller who
/// forgets holds one for the lifetime of the process, and enough of them
/// exhaust the attachment pool — which is the failure §4b's separate pool exists
/// to keep away from the query path, not one it prevents.
/// </remarks>
public sealed class OpenAttachment : IAsyncDisposable
{
    private readonly Func<ValueTask> _release;

    /// <summary>Creates the handle.</summary>
    /// <param name="info">What it is.</param>
    /// <param name="content">Its bytes.</param>
    /// <param name="release">What to run on disposal.</param>
    public OpenAttachment(AttachmentInfo info, Stream content, Func<ValueTask> release)
    {
        Info = info;
        Content = content;
        _release = release;
    }

    /// <summary>What it is.</summary>
    public AttachmentInfo Info { get; }

    /// <summary>Its bytes. Read once, forwards.</summary>
    public Stream Content { get; }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _release();
}

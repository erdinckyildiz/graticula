using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;

namespace Graticula.Providers.SqlServer;

/// <summary>
/// What a SQL Server layer answers to everything but reading — ADR-210 §5: it is served to read, and an edit, an
/// attachment, a history or a relationship is refused with that sentence rather than half done.
/// </summary>
/// <remarks>
/// <b>A history is "off", not refused</b>: a layer's removal asks whether one is on before it goes (ADR-207), and a
/// SQL Server layer never has one, so the answer is no rather than an error that would keep it from being removed.
/// </remarks>
public sealed class SqlServerReadOnly : IFeatureWriter, IAttachmentStore, IFeatureHistory, IRelatedRecords
{
    private readonly string _layer;

    /// <summary>The refusals for one layer.</summary>
    /// <param name="layer">The layer, named in every sentence.</param>
    public SqlServerReadOnly(LayerDefinition layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        _layer = layer.Name;
    }

    private QueryNotSupportedException No(string what) => new(
        $"Layer '{_layer}' is served from a registered SQL Server database, which this build reads and does not {what} "
        + "(ADR-210 §5).");

    /// <inheritdoc />
    public Task<EditOutcome> ApplyAsync(EditBatch batch, CancellationToken cancellationToken) => throw No("edit");

    /// <inheritdoc />
    public Task<IReadOnlyList<AttachmentInfo>> ListAsync(long featureId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AttachmentInfo>>([]);

    /// <inheritdoc />
    public Task<OpenAttachment?> OpenAsync(int attachmentId, CancellationToken cancellationToken) =>
        Task.FromResult<OpenAttachment?>(null);

    /// <inheritdoc />
    public Task<int> AddAsync(
        long featureId, string name, string contentType, string? declaredContentType, Stream content, CancellationToken cancellationToken) =>
        throw No("keep attachments for");

    /// <inheritdoc />
    public Task<bool> UpdateAsync(
        long featureId, int attachmentId, string name, string contentType, string? declaredContentType, Stream content,
        CancellationToken cancellationToken) =>
        throw No("keep attachments for");

    /// <inheritdoc />
    public Task<IReadOnlyList<int>> DeleteAsync(IReadOnlyList<int> attachmentIds, CancellationToken cancellationToken) =>
        throw No("keep attachments for");

    /// <inheritdoc />
    public Task<AttachmentUsage> UsageAsync(CancellationToken cancellationToken) => throw No("keep attachments for");

    /// <inheritdoc />
    public Task<bool> IsOnAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    /// <inheritdoc />
    public Task<HistoryKept?> KeptAsync(CancellationToken cancellationToken) => Task.FromResult<HistoryKept?>(null);

    /// <inheritdoc />
    public Task<long> EnableAsync(CancellationToken cancellationToken) => throw No("keep a history of");

    /// <inheritdoc />
    public Task DisableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<bool> SetKeepAsync(int? days, CancellationToken cancellationToken) => throw No("keep a history of");

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureChange>> ChangesAsync(DateTimeOffset? before, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FeatureChange>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureVersion>> VersionsAsync(long objectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FeatureVersion>>([]);

    /// <inheritdoc />
    public Task<RestoreOutcome> RestoreAsync(
        long objectId, long historyId, string editor, EditorTracking? tracking, CancellationToken cancellationToken) =>
        throw No("keep a history of");

    /// <inheritdoc />
    public Task<IReadOnlyList<RelatedGroup>> QueryAsync(
        string originKey, string relatedKey, IReadOnlyList<long> objectIds, IReadOnlyList<FieldDescription> fields, int limit,
        CancellationToken cancellationToken) =>
        throw No("follow relationships in");
}

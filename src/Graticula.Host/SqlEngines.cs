using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Providers.MySql;
using Graticula.Providers.Oracle;

namespace Graticula.Host;

/// <summary>
/// A registered SQL database other than PostgreSQL, as the host reaches it — ADR-208 §5.6: one seam for every engine,
/// so a capability added for one is a member every engine must answer, not a branch the next engine can miss.
/// </summary>
/// <remarks>
/// <b>Host-side, because it builds provider types</b>; nothing in Core names it. PostgreSQL and the DuckDB-read files
/// are not behind it — they were here first, each with its own pool register — and <see cref="SqlEngines.For"/> answers
/// null for them.
/// </remarks>
internal interface ISqlEngine
{
    /// <summary>The kind its data sources are registered as (<see cref="DataSourceKinds"/>).</summary>
    string Kind { get; }

    /// <summary>The engine's name, for a sentence.</summary>
    string Name { get; }

    /// <summary>Whether a stored locator is this engine's.</summary>
    bool Owns(string? locator);

    /// <summary>What it offers to publish, for registration and the database listing.</summary>
    IDataSourceProbe Probe { get; }

    /// <summary>A layer's reader.</summary>
    IFeatureSource Source(PublishedLayer layer, IProjector projector, TimeSpan timeout, IGeometryEngine? engine);

    /// <summary>A layer's writer.</summary>
    IFeatureWriter Writer(
        PublishedLayer layer, IReadOnlyList<FieldDescription> fields, IProjector projector, EditorTracking? tracking,
        LayerSubtypes? subtypes, TimeSpan timeout);

    /// <summary>Several layers' edits in one database, in one transaction.</summary>
    Task<IReadOnlyList<EditOutcome>> ApplyTogetherAsync(
        string locator, IReadOnlyList<LayerConnections.LayerEdit> edits, IProjector projector, TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>The object ids of the features carrying these GlobalIDs — <c>useGlobalIds</c>.</summary>
    Task<IReadOnlyDictionary<Guid, long>> ObjectIdsOfAsync(
        PublishedLayer layer, string globalIdColumn, IReadOnlyCollection<Guid> globalIds, CancellationToken cancellationToken);

    /// <summary>A layer's attachments kept beside its table — ADR-207.</summary>
    IAttachmentStore Attachments(PublishedLayer layer);

    /// <summary>A layer's history, kept beside its table — ADR-207.</summary>
    IFeatureHistory History(PublishedLayer layer);

    /// <summary>Follows a relationship between two layers of this engine.</summary>
    IRelatedRecords Related(PublishedLayer origin, PublishedLayer related, bool sameDatabase);

    /// <summary>Prunes every history in this database to its keeping period.</summary>
    Task<IReadOnlyList<(string Table, long Versions)>> PruneHistoryAsync(string locator, CancellationToken cancellationToken);

    /// <summary>How much of a table's geometry the engine calls valid, or null when it could not be asked.</summary>
    Task<(long Rows, long Invalid, IReadOnlyList<string> Reasons)?> ValidityAsync(
        string locator, string schema, string table, string column, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>The sentence for a database that refused the DDL a history needs, or null for another failure.</summary>
    string? HistoryRefusal(Exception failure, LayerDefinition layer);

    /// <summary>The reference a table holds against the one declared, or null when it could not be asked.</summary>
    Task<(int? Stored, string? Complaint)?> ReferenceAsync(
        string locator, string schema, string table, string column, int declared, CancellationToken cancellationToken);
}

/// <summary>The registered SQL engines, chosen by locator — ADR-208.</summary>
internal static class SqlEngines
{
    /// <summary>Every engine behind the seam.</summary>
    public static IReadOnlyList<ISqlEngine> All { get; } = [new OracleEngine(), new MySqlEngine()];

    /// <summary>The engine a locator names, or null for PostgreSQL and the DuckDB-read files.</summary>
    public static ISqlEngine? For(string? locator) => All.FirstOrDefault(engine => engine.Owns(locator));
}

/// <summary>Oracle behind the seam — ADR-205 and ADR-207.</summary>
internal sealed class OracleEngine : ISqlEngine
{
    public string Kind => DataSourceKinds.Oracle;

    public string Name => "Oracle";

    public bool Owns(string? locator) => OracleLocator.Is(locator);

    public IDataSourceProbe Probe { get; } = new OracleDataSourceProbe();

    public IFeatureSource Source(PublishedLayer layer, IProjector projector, TimeSpan timeout, IGeometryEngine? engine) =>
        new OracleFeatureSource(layer.ConnectionString, layer.Definition, projector, timeout, engine);

    public IFeatureWriter Writer(
        PublishedLayer layer, IReadOnlyList<FieldDescription> fields, IProjector projector, EditorTracking? tracking,
        LayerSubtypes? subtypes, TimeSpan timeout) =>
        new OracleFeatureWriter(layer.ConnectionString, layer.Definition, fields, projector, tracking, subtypes, timeout);

    public Task<IReadOnlyList<EditOutcome>> ApplyTogetherAsync(
        string locator, IReadOnlyList<LayerConnections.LayerEdit> edits, IProjector projector, TimeSpan timeout,
        CancellationToken cancellationToken) =>
        OracleFeatureWriter.ApplyTogetherAsync(
            locator,
            [.. edits.Select(e => new OracleFeatureWriter.LayerBatch(
                e.Layer.Definition, e.Description.Fields, e.Description.Tracking, e.Description.Subtypes, e.Batch))],
            projector,
            timeout,
            cancellationToken);

    public Task<IReadOnlyDictionary<Guid, long>> ObjectIdsOfAsync(
        PublishedLayer layer, string globalIdColumn, IReadOnlyCollection<Guid> globalIds, CancellationToken cancellationToken) =>
        OracleFeatureWriter.ObjectIdsOfAsync(layer.ConnectionString, layer.Definition, globalIdColumn, globalIds, cancellationToken);

    public IAttachmentStore Attachments(PublishedLayer layer) =>
        new OracleAttachmentStore(layer.ConnectionString, layer.Definition, layer.AttachmentQuotaBytes);

    public IFeatureHistory History(PublishedLayer layer) => new OracleFeatureHistory(layer.ConnectionString, layer.Definition);

    public string? HistoryRefusal(Exception failure, LayerDefinition layer) => OracleFeatureHistory.Refusal(failure, layer);

    public IRelatedRecords Related(PublishedLayer origin, PublishedLayer related, bool sameDatabase) =>
        new OracleRelatedRecords(origin.ConnectionString, origin.Definition, related.Definition, sameDatabase);

    public Task<IReadOnlyList<(string Table, long Versions)>> PruneHistoryAsync(string locator, CancellationToken cancellationToken) =>
        OracleFeatureHistory.PruneAsync(locator, cancellationToken);

    public async Task<(long Rows, long Invalid, IReadOnlyList<string> Reasons)?> ValidityAsync(
        string locator, string schema, string table, string column, TimeSpan timeout, CancellationToken cancellationToken) =>
        await OracleTableChecks.ValidityAsync(locator, schema, table, column, timeout, cancellationToken).ConfigureAwait(false) is { } found
            ? (found.Rows, found.Invalid, found.Reasons)
            : null;

    public async Task<(int? Stored, string? Complaint)?> ReferenceAsync(
        string locator, string schema, string table, string column, int declared, CancellationToken cancellationToken) =>
        await OracleTableChecks.ReferenceAsync(locator, schema, table, column, declared, cancellationToken).ConfigureAwait(false) is { } found
            ? (found.Stored, found.Complaint)
            : null;
}

/// <summary>MySQL behind the seam — ADR-208. Phase 1 reads; what it does not yet do it refuses by name.</summary>
internal sealed class MySqlEngine : ISqlEngine
{
    private const string NotYet = "is not built for a MySQL layer yet (ADR-208)";

    public string Kind => DataSourceKinds.MySql;

    public string Name => "MySQL";

    public bool Owns(string? locator) => MySqlLocator.Is(locator);

    public IDataSourceProbe Probe { get; } = new MySqlDataSourceProbe();

    public IFeatureSource Source(PublishedLayer layer, IProjector projector, TimeSpan timeout, IGeometryEngine? engine) =>
        new MySqlFeatureSource(layer.ConnectionString, layer.Definition, projector, timeout, engine);

    public IFeatureWriter Writer(
        PublishedLayer layer, IReadOnlyList<FieldDescription> fields, IProjector projector, EditorTracking? tracking,
        LayerSubtypes? subtypes, TimeSpan timeout) =>
        throw new QueryNotSupportedException($"Editing {NotYet}.");

    public Task<IReadOnlyList<EditOutcome>> ApplyTogetherAsync(
        string locator, IReadOnlyList<LayerConnections.LayerEdit> edits, IProjector projector, TimeSpan timeout,
        CancellationToken cancellationToken) =>
        throw new QueryNotSupportedException($"Editing {NotYet}.");

    public Task<IReadOnlyDictionary<Guid, long>> ObjectIdsOfAsync(
        PublishedLayer layer, string globalIdColumn, IReadOnlyCollection<Guid> globalIds, CancellationToken cancellationToken) =>
        throw new QueryNotSupportedException($"A GlobalID lookup {NotYet}.");

    public IAttachmentStore Attachments(PublishedLayer layer) =>
        throw new QueryNotSupportedException($"Attachments beside the table {NotYet}; keep them in the datastore.");

    public IFeatureHistory History(PublishedLayer layer) => throw new QueryNotSupportedException($"A history {NotYet}.");

    public string? HistoryRefusal(Exception failure, LayerDefinition layer) => null;

    public IRelatedRecords Related(PublishedLayer origin, PublishedLayer related, bool sameDatabase) =>
        throw new QueryNotSupportedException($"Related records {NotYet}.");

    public Task<IReadOnlyList<(string Table, long Versions)>> PruneHistoryAsync(string locator, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(string Table, long Versions)>>([]);

    public Task<(long Rows, long Invalid, IReadOnlyList<string> Reasons)?> ValidityAsync(
        string locator, string schema, string table, string column, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult<(long Rows, long Invalid, IReadOnlyList<string> Reasons)?>(null);

    public Task<(int? Stored, string? Complaint)?> ReferenceAsync(
        string locator, string schema, string table, string column, int declared, CancellationToken cancellationToken) =>
        Task.FromResult<(int? Stored, string? Complaint)?>(null);
}

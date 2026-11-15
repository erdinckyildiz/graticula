using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;

namespace Graticula.Offline.Sqlite;

/// <summary>
/// Reads the delta a client uploaded for <c>synchronizeReplica</c>, with SQLite — ADR-288 §6 and condition 7.
/// </summary>
/// <remarks>
/// <b>Constructed by <c>Graticula.Offline.Reader</c> and by tests, and by nothing in the host.</b> This parses an
/// uploaded file with the native <c>e_sqlite3</c>, which ADR-009 §2.2 keeps out of the serving process; the host's
/// <see cref="IOfflineDeltaReader"/> starts the child that runs this. <c>NativeDependencyTests</c> fails the build if the
/// host's source names this type.
/// </remarks>
public sealed class SqliteOfflineDeltaReader : IOfflineDeltaReader
{
    /// <inheritdoc />
    public Task<OfflineDelta> ReadDeltaAsync(string path, IReadOnlyList<OfflineLayer> layers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layers);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DeltaFile.Read(path, layers, cancellationToken));
    }
}

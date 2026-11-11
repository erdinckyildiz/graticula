using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Api.ArcGis.Offline;

/// <summary>
/// Reads the delta a client uploaded for <c>synchronizeReplica</c> — ADR-288 condition 7.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own port, split from <see cref="IMobileGeodatabaseWriter"/>, because the two run in different processes.</b>
/// Writing a mobile geodatabase starts from rows this server already holds, so the writer stays in the host. Reading
/// one parses a file a client chose, with a native library, and ADR-009 §2.2 puts an untrusted-file parser outside the
/// process that serves public requests. The owner's decision of 2026-11-10 (<i>"Ayrı sürece taşı"</i>, Q-233, D-402)
/// applied that line here. While the read was a member of the writer port, the host held an object that could parse an
/// upload in-process and nothing but care stopped a caller from using it.
/// </para>
/// <para>
/// The host's implementation starts <c>Graticula.Offline.Reader</c>, a child process, and receives the result in
/// <see cref="OfflineDeltaWire"/>'s format. The SQLite implementation is in <c>Graticula.Offline.Sqlite</c>, and only
/// that child constructs it.
/// </para>
/// </remarks>
public interface IOfflineDeltaReader
{
    /// <summary>Reads a delta a client uploaded for <c>synchronizeReplica</c>.</summary>
    /// <param name="path">The uploaded file.</param>
    /// <param name="layers">
    /// The replica's layers, so rows are read against the schema they were written with. The changes returned name these
    /// same instances.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The changes, per layer.</returns>
    /// <exception cref="System.InvalidOperationException">The file could not be read; the message says why.</exception>
    Task<OfflineDelta> ReadDeltaAsync(string path, IReadOnlyList<OfflineLayer> layers, CancellationToken cancellationToken);
}

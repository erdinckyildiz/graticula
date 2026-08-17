using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Schema;

/// <summary>
/// The port through which the platform store's schema is read and changed.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <c>docs/build-vs-adopt-policy.md</c> §4 forbids a library
/// type in a Tier 1 signature, and a database driver is a Tier 2 library. It is
/// <b>not</b> speculative generality: Q-70 settled that PostgreSQL is the only
/// platform store there will be, so this port is not waiting for a second
/// implementation. It is how the one implementation stays out of Tier 1.
/// </para>
/// </remarks>
public interface IPlatformSchemaStore
{
    /// <summary>
    /// Reads the store's stamp, or <see langword="null"/> if it has never been
    /// migrated.
    /// </summary>
    /// <remarks>
    /// Must return <see langword="null"/> rather than throwing when the stamp
    /// table itself is absent. That is the bootstrap case: the migrator cannot
    /// read a version from a table its own first migration creates.
    /// </remarks>
    Task<SchemaStamp?> ReadStampAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies one migration and records the resulting stamp.
    /// </summary>
    /// <param name="migration">The migration to apply.</param>
    /// <param name="resultingStamp">The stamp the store must hold afterwards.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// <b>Contract, and it is the important part: the statements and the stamp
    /// update must be one atomic unit.</b> If a crash can land between them, the
    /// store ends up either migrated while claiming it is not — so the migration
    /// runs twice — or claiming a level it never reached. Both are corruption
    /// that presents as something else, which is the failure mode ADR-016 §4b
    /// exists to prevent.
    /// </remarks>
    Task ApplyAsync(Migration migration, SchemaStamp resultingStamp, CancellationToken cancellationToken);
}

using System;

namespace Graticula.Platform.Catalog;

/// <summary>
/// Whether the platform store is worth asking right now.
/// </summary>
/// <remarks>
/// <para>
/// <b>An interface here and the implementation in the host, because the direction of the
/// dependency matters more than the size of the type.</b> The circuit breaker
/// [ADR-007](../../../docs/adr/ADR-007-service-runtime.md) §4.8 asks for is a runtime
/// concern and lives with the runtime; this assembly holds the catalogue, which needs to
/// *ask* whether the store is worth trying and must not know how the answer is decided.
/// </para>
/// <para>
/// <b>No key, unlike the host's own breaker.</b> There is one platform store per
/// deployment. A layer's data source needs a key because there are many of them, and that
/// distinction is the whole reason this interface is narrower than the thing that
/// implements it.
/// </para>
/// <para>
/// <b>Why it exists at all: [D-131](../../../docs/architecture-debt.md).</b> Measured with
/// the store stopped, a single data request cost 8.0 seconds — four for the principal and
/// four for the catalogue, both of them the same store, both blackholed. The catalogue's
/// four were the ones that survived the first repair, because the breaker was in the host
/// and the catalogue read is here.
/// </para>
/// </remarks>
public interface IStoreHealth
{
    /// <summary>Whether the store failed recently enough not to be asked again.</summary>
    bool IsOpen { get; }

    /// <summary>Records that the store could not be reached.</summary>
    /// <param name="failure">What went wrong.</param>
    void Failed(Exception failure);

    /// <summary>Records that the store answered.</summary>
    void Succeeded();
}

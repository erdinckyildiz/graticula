using System;
using System.Collections.Generic;
using System.Linq;

namespace GisServer.Platform.Catalog;

/// <summary>
/// What a service has been configured to offer — a ceiling, never a grant.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-031.</b> Effective capability is the intersection of three things: what
/// the data can support, what the service is configured to offer, and what the
/// caller's privileges allow. This type is the middle one, and the intersection is
/// performed here so that it exists in one place rather than being re-derived per
/// surface — which is the invariant <see href="Q-105">Q-105</see> named and could
/// not previously locate, because until ADR-031 there was no service-level policy
/// to compose with.
/// </para>
/// <para>
/// <b>Null means unset, everywhere, and that is what makes this additive.</b> An
/// unset limit offers whatever the data supports, which is exactly what every
/// service did before this type existed. There is deliberately no "default"
/// instance that means anything else.
/// </para>
/// <para>
/// <b>It cannot widen. There is no method here that adds a capability</b>, and
/// that absence is the design: a configuration that could grant would let a
/// service hand a caller something their role does not carry, and would make the
/// answer to *may this person edit?* depend on where they asked.
/// </para>
/// </remarks>
public sealed class ServiceCapabilityLimits
{
    /// <summary>The capability names this server understands, in document order.</summary>
    /// <remarks>
    /// ArcGIS's vocabulary, because that is what the service document speaks. The
    /// mapping from these names to our own privileges happens once, in the host.
    /// </remarks>
    public static readonly IReadOnlyList<string> Known =
        ["Query", "Create", "Update", "Delete", "Extract"];

    /// <summary>Nothing configured: the data and the caller decide everything.</summary>
    public static ServiceCapabilityLimits Unset { get; } = new(null, null, null, null);

    /// <summary>What one request may cost this service, or null for the server's own.</summary>
    /// <remarks>
    /// <b>Separate from the capability ceiling above, and the distinction is the
    /// point.</b> A capability answers *may you*; a cost ceiling answers *how much*.
    /// Turning `Update` off refuses an act; a max record count shortens an answer to
    /// an act that is permitted. Conflating them would make "this service is
    /// read-only" and "this service returns 500 rows at a time" the same kind of
    /// setting, and an operator reading the screen has to be able to tell them apart.
    /// </remarks>
    public ServiceCostCeilings Cost { get; private init; } = ServiceCostCeilings.Unset;

    /// <summary>The same limits with a cost ceiling attached.</summary>
    public ServiceCapabilityLimits With(ServiceCostCeilings cost)
    {
        ArgumentNullException.ThrowIfNull(cost);

        return new ServiceCapabilityLimits(ServesFeatures, ServesTiles, Ceiling, StatementTimeout)
        {
            Cost = cost,
        };
    }

    /// <summary>Creates a set of limits.</summary>
    /// <param name="servesFeatures">Whether the feature face is offered, or null for unset.</param>
    /// <param name="servesTiles">Whether the tile face is offered, or null for unset.</param>
    /// <param name="ceiling">
    /// The capabilities this service will offer at most, or null for unset. An
    /// empty list is meaningful and different from null: it means *no capability*,
    /// which is a service that exists and answers nothing.
    /// </param>
    /// <param name="statementTimeout">
    /// A statement timeout this service asks for, or null for the source's own.
    /// </param>
    public ServiceCapabilityLimits(
        bool? servesFeatures,
        bool? servesTiles,
        IReadOnlyList<string>? ceiling,
        TimeSpan? statementTimeout)
    {
        if (ceiling is not null)
        {
            foreach (string name in ceiling)
            {
                if (!Known.Contains(name, StringComparer.Ordinal))
                {
                    throw new ArgumentException(
                        $"'{name}' is not a capability this server understands. Known: "
                        + string.Join(", ", Known) + ". An unrecognised name would be dropped "
                        + "silently by the intersection, which is the failure mode where a "
                        + "service looks configured and is not.",
                        nameof(ceiling));
                }
            }
        }

        if (statementTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statementTimeout),
                timeout,
                "A statement timeout must be positive. Zero is how PostgreSQL spells 'no "
                + "limit', so accepting it here would turn this setting into the hole D-42 "
                + "closed: ADR-007 §4.8 makes the timeout mandatory and this may only lower it.");
        }

        ServesFeatures = servesFeatures;
        ServesTiles = servesTiles;
        Ceiling = ceiling is null ? null : [.. ceiling];
        StatementTimeout = statementTimeout;
    }

    /// <summary>Whether the feature face is offered, or null when unset.</summary>
    public bool? ServesFeatures { get; }

    /// <summary>Whether the tile face is offered, or null when unset.</summary>
    public bool? ServesTiles { get; }

    /// <summary>The configured ceiling, or null when unset.</summary>
    public IReadOnlyList<string>? Ceiling { get; }

    /// <summary>The statement timeout this service asks for, or null.</summary>
    public TimeSpan? StatementTimeout { get; }

    /// <summary>True when nothing is configured.</summary>
    public bool IsUnset =>
        ServesFeatures is null && ServesTiles is null
        && Ceiling is null && StatementTimeout is null && Cost.IsUnset;

    /// <summary>
    /// The capabilities that survive both this ceiling and what the caller may do.
    /// </summary>
    /// <param name="allowedByPrivilege">
    /// What the caller's privileges permit, already narrowed by what the data can
    /// support.
    /// </param>
    /// <returns>The intersection, in document order.</returns>
    /// <remarks>
    /// <b>The order of the two operands does not matter, and that is the property
    /// worth having.</b> Intersection is commutative, so there is no
    /// configuration that beats a privilege and no privilege that beats a
    /// configuration — which is what "composition only ever restricts" means when
    /// it is written as code instead of as a sentence.
    /// </remarks>
    public IReadOnlyList<string> Restrict(IEnumerable<string> allowedByPrivilege)
    {
        ArgumentNullException.ThrowIfNull(allowedByPrivilege);

        HashSet<string> allowed = new(allowedByPrivilege, StringComparer.Ordinal);

        if (Ceiling is { } ceiling)
        {
            allowed.IntersectWith(ceiling);
        }

        // Document order rather than set order, so the string a client reads is
        // stable across requests and diffable across builds.
        return [.. Known.Where(allowed.Contains)];
    }

    /// <summary>Whether the feature face may be served, given what the data allows.</summary>
    /// <param name="dataSupportsIt">Whether the data can support this face at all.</param>
    public bool AllowsFeatures(bool dataSupportsIt) => dataSupportsIt && ServesFeatures != false;

    /// <summary>Whether the tile face may be served, given what the data allows.</summary>
    /// <param name="dataSupportsIt">Whether the data can support this face at all.</param>
    /// <remarks>
    /// <b><paramref name="dataSupportsIt"/> is first and cannot be overridden.</b>
    /// Tiles come only from hosted data (Q-67), so a configuration that says
    /// <c>true</c> for a registered layer is still refused — the setting is a
    /// ceiling, and a ceiling cannot lift a floor.
    /// </remarks>
    public bool AllowsTiles(bool dataSupportsIt) => dataSupportsIt && ServesTiles != false;
}

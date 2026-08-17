using System;

namespace Graticula.Platform.Identity;

/// <summary>
/// Who a request is from.
/// </summary>
/// <remarks>
/// <para>
/// ADR-015 §2a: <b>anonymous is a principal, not the absence of one.</b> Open
/// data is a normal deployment of a GIS server, and modelling anonymous as a
/// null identity produces <c>if (user is null)</c> at every authorization site —
/// which is where the bug that grants too much eventually lives. It has a row, a
/// name, and can hold roles. Refusing anonymous access becomes configuration
/// rather than a special case in the code.
/// </para>
/// <para>
/// <b><see cref="Name"/> is the stable, mappable identifier</b> ADR-015 §1a
/// requires: authorization delegates to database row-level security via
/// <c>SET LOCAL ROLE</c>, so a principal has to survive into the database as a
/// role name an administrator has mapped. That is why identity here is a name
/// rather than an opaque handle or a bag of claims.
/// </para>
/// </remarks>
public sealed class Principal
{
    /// <summary>The id of the seeded anonymous principal.</summary>
    /// <remarks>
    /// Fixed rather than looked up. It is created by migration 1 of the identity
    /// schema and is structural — a deployment where this row is missing is
    /// broken, not merely unconfigured.
    /// </remarks>
    public static readonly Guid AnonymousId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>The anonymous principal.</summary>
    public static readonly Principal Anonymous =
        new(AnonymousId, PrincipalKind.Anonymous, "anonymous", "Anonymous", isDisabled: false);

    /// <summary>Creates a principal.</summary>
    /// <param name="id">Its id in the platform store.</param>
    /// <param name="kind">User, service or anonymous.</param>
    /// <param name="name">The stable name; see the type remarks.</param>
    /// <param name="displayName">A human label, or null.</param>
    /// <param name="isDisabled">Whether the account has been disabled.</param>
    public Principal(Guid id, PrincipalKind kind, string name, string? displayName, bool isDisabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Id = id;
        Kind = kind;
        Name = name;
        DisplayName = displayName;
        IsDisabled = isDisabled;
    }

    /// <summary>Its id in the platform store.</summary>
    public Guid Id { get; }

    /// <summary>User, service or anonymous.</summary>
    public PrincipalKind Kind { get; }

    /// <summary>The stable name, mappable to a database role.</summary>
    public string Name { get; }

    /// <summary>A human label, or null.</summary>
    public string? DisplayName { get; }

    /// <summary>Whether the account has been disabled.</summary>
    /// <remarks>
    /// Carried on the principal rather than filtered out at lookup so that a
    /// disabled account is distinguishable from one that never existed. The
    /// distinction never reaches a caller — ADR-015 §5's login path reports both
    /// identically — but an administrator reading an audit trail needs it.
    /// </remarks>
    public bool IsDisabled { get; }

    /// <summary>Whether this principal may own items (ADR-015 §7).</summary>
    /// <remarks>
    /// Users only. Ownership carries sharing decisions, and a service principal
    /// has no judgement to exercise about them.
    /// </remarks>
    public bool CanOwnItems => Kind == PrincipalKind.User;

    /// <summary>Whether this is the anonymous principal.</summary>
    public bool IsAnonymous => Kind == PrincipalKind.Anonymous;
}

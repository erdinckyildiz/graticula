using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Platform.Tests.Identity;

/// <summary>
/// The invariant behind read authorization, stated once and checked exhaustively.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-105](../../docs/open-questions.md): <em>no step may widen what a narrower
/// element allowed</em>.</b> The question was whether that becomes a stated property
/// with a test, or stays an outcome each new surface has to arrive at independently.
/// This is the property. It is deliberately about <see cref="LayerAccess.Evaluate"/>
/// rather than about any one protocol face, because a rule enforced per surface is a
/// rule that is missing on the next surface.
/// </para>
/// <para>
/// <b>Exhaustive rather than sampled, because the space is small enough to be.</b> Four
/// scopes, five callers and both group arrangements is forty combinations — every one is
/// evaluated, so this cannot pass by testing the easy half.
/// </para>
/// <para>
/// <b>The one widening is named, and that is the second half of the invariant.</b>
/// <c>AdminViewAllContent</c> can turn a denial into an allowance. That is legitimate and
/// is exactly why ADR-018 condition 3 makes it its own <see cref="LayerAccess.Reason"/>:
/// an override that is indistinguishable from an ordinary read cannot be audited, and a
/// sharing model nobody can audit is decorative. So the test does not merely permit the
/// widening — it requires that when it happens the answer says so.
/// </para>
/// </remarks>
public sealed class AuthorizationOnlyNarrowsTests
{
    /// <summary>Scopes from narrowest to widest — the order the invariant is about.</summary>
    /// <remarks>
    /// <b>Group sits between private and organisation.</b> A group is a named set of
    /// members; the organisation is every account. So group reaches more people than
    /// private and fewer than organisation, and an ordering that put it anywhere else
    /// would make the property below assert something untrue about the product.
    /// </remarks>
    private static readonly SharingScope[] WideningOrder =
    [
        SharingScope.Private,
        SharingScope.Group,
        SharingScope.Organization,
        SharingScope.Public,
    ];

    private static readonly Guid Owner = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Stranger = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Planning = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Cadastre = new("44444444-4444-4444-4444-444444444444");

    /// <summary>A signed-in account, which is every caller here except the anonymous one.</summary>
    private static Principal SignedIn(Guid id, string name) =>
        new(id, PrincipalKind.User, name, name, isDisabled: false);

    /// <summary>What a caller may do, resolved the way the server resolves it.</summary>
    /// <remarks>
    /// <b>Through <see cref="Authorization.Resolve"/> rather than assembled by hand.</b>
    /// The user-type ceiling and the administrator flag are part of what the answer
    /// means, and a test that skipped them would be asserting the invariant about a
    /// authorization the product never produces.
    /// </remarks>
    private static Authorization Rights(
        IEnumerable<Privilege> privileges, IEnumerable<Guid> groups) =>
        Authorization.Resolve(
            // <b><c>Unrestricted</c>, and finding out why cost a red test worth
            // keeping.</b> ADR-018 §3a intersects a role's grants with the user type's
            // ceiling, and `creator` does not carry `admin:viewAllContent` — so
            // resolving the administrator caller as a creator produced an
            // authorization that held the privilege on paper and not in the answer.
            // A test that had assembled the authorization by hand would have missed
            // that the product cannot produce the caller it was asserting about.
            UserTypes.Unrestricted, ["reader"], new Granting(privileges), groups);

    /// <summary>Every kind of caller this decision distinguishes.</summary>
    private static IEnumerable<(string Name, Principal Caller, Authorization Rights)> Callers()
    {
        yield return ("anonymous", Principal.Anonymous, Rights([], []));

        yield return (
            "a signed-in stranger",
            SignedIn(Stranger, "stranger"),
            Rights([], []));

        yield return ("the owner", SignedIn(Owner, "owner"), Rights([], []));

        yield return (
            "a member of the planning group",
            SignedIn(Stranger, "stranger"),
            Rights([], [Planning]));

        yield return (
            "an administrator who may view all content",
            SignedIn(Stranger, "stranger"),
            Rights([Privilege.AdminViewAllContent], []));
    }

    /// <summary>A grant store holding one role with exactly the privileges given.</summary>
    private sealed class Granting(IEnumerable<Privilege> privileges) : IRoleGrants
    {
        private readonly ImmutableHashSet<Privilege> _held = [.. privileges];

        public ImmutableHashSet<Privilege> PrivilegesOf(string role) =>
            string.Equals(role, "reader", StringComparison.Ordinal) ? _held : [];

        public ImmutableDictionary<string, ImmutableHashSet<Privilege>> All() =>
            ImmutableDictionary<string, ImmutableHashSet<Privilege>>.Empty
                .Add("reader", _held);

        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void Narrowing_an_items_scope_never_turns_a_denial_into_an_allowance()
    {
        List<string> widened = [];

        foreach ((string name, Principal caller, Authorization rights) in Callers())
        {
            foreach (Guid[] groups in new[] { new[] { Planning }, new[] { Cadastre } })
            {
                for (int narrow = 0; narrow < WideningOrder.Length; narrow++)
                {
                    bool allowedNarrow = LayerAccess
                        .Evaluate(WideningOrder[narrow], Owner, caller, rights, groups)
                        .IsAllowed();

                    if (!allowedNarrow)
                    {
                        continue;
                    }

                    // Anything at least as wide must also allow it. The failure this
                    // catches is a scope that is narrower on paper and more permissive
                    // in code — which is how a `group` share came to be wider than the
                    // organisation would have been.
                    for (int wide = narrow + 1; wide < WideningOrder.Length; wide++)
                    {
                        if (!LayerAccess
                            .Evaluate(WideningOrder[wide], Owner, caller, rights, groups)
                            .IsAllowed())
                        {
                            widened.Add(
                                $"{name} may read a {WideningOrder[narrow]} item and may not "
                                + $"read the same item shared {WideningOrder[wide]}");
                        }
                    }
                }
            }
        }

        Assert.Empty(widened);
    }

    [Fact]
    public void The_only_thing_that_widens_a_denial_is_the_override_and_it_says_so()
    {
        // <b>The second half of the invariant.</b> A widening is not forbidden — an
        // administrator reading a private layer is a legitimate act. What is forbidden
        // is a widening nobody can see afterwards, which is why the answer carries a
        // reason rather than a boolean.
        Principal stranger = SignedIn(Stranger, "stranger");
        List<string> quiet = [];

        foreach (SharingScope scope in WideningOrder)
        {
            bool plain = LayerAccess
                .Evaluate(scope, Owner, stranger, Rights([], []), [Cadastre])
                .IsAllowed();

            LayerAccess.Reason overridden = LayerAccess.Evaluate(
                scope,
                Owner,
                stranger,
                    Rights([Privilege.AdminViewAllContent], []),
                [Cadastre]);

            Assert.True(
                overridden.IsAllowed(),
                $"AdminViewAllContent did not open a {scope} item, so the override is not "
                + "the escape hatch ADR-018 §3b says it is.");

            if (!plain && overridden != LayerAccess.Reason.AdministrativeOverride)
            {
                quiet.Add(
                    $"a {scope} item that a stranger may not read was opened by the override "
                    + $"and reported as `{overridden}`, so the audit trail does not record that "
                    + "the override was used");
            }
        }

        Assert.Empty(quiet);
    }

    [Fact]
    public void An_administrator_reading_what_they_could_have_read_anyway_is_not_an_override()
    {
        // The control on the test above: if every read by an administrator were reported
        // as an override, the record would be noise and the first test would still pass.
        Principal admin = SignedIn(Stranger, "admin");
        Authorization rights = Rights([Privilege.AdminViewAllContent], []);

        Assert.Equal(
            LayerAccess.Reason.Public,
            LayerAccess.Evaluate(SharingScope.Public, Owner, admin, rights));

        Assert.Equal(
            LayerAccess.Reason.Organization,
            LayerAccess.Evaluate(SharingScope.Organization, Owner, admin, rights));
    }

    [Fact]
    public void The_widening_order_covers_every_scope_this_product_has()
    {
        // <b>A new scope must join the ordering or the property above quietly stops
        // covering it.</b> `group` was added by ADR-036 after this decision was made,
        // and a test that enumerated three scopes would have gone on passing while
        // saying nothing about the fourth.
        Assert.Equal(
            Enum.GetValues<SharingScope>().OrderBy(s => s.ToString(), StringComparer.Ordinal),
            WideningOrder.OrderBy(s => s.ToString(), StringComparer.Ordinal));
    }
}

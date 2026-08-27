using System;
using Graticula.Host;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// An outage says so, instead of telling an administrator their credentials are wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-192](../../docs/architecture-debt.md), found by
/// [tools/outage-rehearsal.sh](../../tools/outage-rehearsal.sh) on 2026-08-27.</b> Sessions
/// live in the platform store, so while the store is down this server cannot tell a valid
/// token from a forged one and refuses both — which is right. What it said while refusing was
/// *"you are not signed in. Sign in at /rest/auth/login"*: a sentence about the caller's
/// credentials, pointing at a login route that also cannot work, at the moment an
/// administrator most needs to look. That is the confusion
/// [ADR-017](../../docs/adr/ADR-017-admin-api.md) §6 exists to prevent — *a data-plane failure
/// must not blind the management plane.*
/// </para>
/// <para>
/// <b>The rehearsal cannot run in CI and this can.</b> Stopping a real datastore needs Docker
/// and a container this machine happens to have. What is portable is the decision: given a
/// principal that is anonymous *because the store could not be asked*, the refusal names the
/// outage.
/// </para>
/// </remarks>
public sealed class OutageRefusalTests
{
    private static RequestPrincipal Anonymous(bool storeWasUnreachable) =>
        new(Principal.Anonymous, null, Authorization.Nothing)
        {
            StoreWasUnreachable = storeWasUnreachable,
        };

    [Fact]
    public void An_outage_is_named_rather_than_reported_as_a_bad_credential()
    {
        (int status, string message) =
            Authorize.Refusal(Anonymous(storeWasUnreachable: true), Privilege.AdminViewAllContent);

        // Still 401. An outage is not a reason to trust a token, so what changes is the
        // sentence and not the decision -- a 503 would have altered the contract of every
        // authenticated route for a condition the caller cannot act on either way.
        Assert.Equal(401, status);

        Assert.Contains("platform store", message, StringComparison.Ordinal);

        // The two sentences an administrator actually needs: it is not you, and here is the
        // one route that still answers.
        Assert.Contains("credentials are probably fine", message, StringComparison.Ordinal);
        Assert.Contains("/admin/health", message, StringComparison.Ordinal);

        // And it must not send them somewhere that cannot work either.
        Assert.DoesNotContain("/rest/auth/login", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_caller_who_presented_nothing_is_still_told_to_sign_in()
    {
        (int status, string message) =
            Authorize.Refusal(Anonymous(storeWasUnreachable: false), Privilege.AdminViewAllContent);

        Assert.Equal(401, status);

        // The ordinary case is unchanged, which is the half a repair like this most easily
        // breaks: a server that blames an outage for every anonymous request has replaced one
        // misleading sentence with another.
        Assert.Contains("/rest/auth/login", message, StringComparison.Ordinal);
        Assert.DoesNotContain("platform store", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reason_survives_the_breaker_which_is_where_the_first_repair_lost_it()
    {
        // <b>The defect survived its own repair once, here.</b> `Authentication` learns the
        // store is down in a `catch`, which fires on exactly one request -- the one that
        // discovers the outage. Every request after it is short-circuited by
        // `SourceBreaker`, which is what the breaker is for, and the first repair set the
        // flag only in the catch. So one caller got the right sentence and everybody after
        // got the misleading one, and the rehearsal -- which signs in *before* it stops the
        // database -- read the second kind and showed no change at all.
        //
        // This test cannot open a breaker without a store, so what it pins is the shape the
        // two paths must share: a principal built for an unreachable store is anonymous, holds
        // nothing, and says why.
        RequestPrincipal principal = Anonymous(storeWasUnreachable: true);

        Assert.True(principal.Principal.IsAnonymous);
        Assert.Null(principal.SessionId);
        Assert.True(principal.StoreWasUnreachable);

        // Anonymous by default, so a principal built anywhere else does not accidentally claim
        // an outage.
        Assert.False(
            new RequestPrincipal(Principal.Anonymous, null, Authorization.Nothing)
                .StoreWasUnreachable);
    }
}

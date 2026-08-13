namespace GisServer.Host;

/// <summary>
/// Whether the server is still waiting to be set up.
/// </summary>
/// <remarks>
/// <para>
/// ADR-015 §6: on first start with no administrator, the server refuses
/// everything except the setup endpoint. This holds that one bit.
/// </para>
/// <para>
/// <b>In memory, and it is the right place for exactly one reason:</b> the
/// authoritative answer is in the database — a user principal either exists or
/// does not — and asking it per request would be a query on every request to
/// answer a question that changes once, ever. This is a cache of a durable fact,
/// which is why it can only ever move in one direction.
/// </para>
/// <para>
/// <b>The failure mode of a stale value is the safe one.</b> If another node
/// completes setup, this node keeps refusing until it restarts: annoying, and
/// visible. The opposite arrangement — assuming setup is done when it is not —
/// would leave the setup endpoint open, and that is the race ADR-015 §6
/// rejected an unauthenticated setup window to avoid.
/// </para>
/// </remarks>
internal sealed class ServerState
{
    private volatile bool _setupPending;

    /// <summary>Whether setup is still required.</summary>
    public bool IsSetupPending => _setupPending;

    /// <summary>Records that the server has no administrator yet.</summary>
    public void RequireSetup() => _setupPending = true;

    /// <summary>Records that an administrator now exists.</summary>
    public void SetupCompleted() => _setupPending = false;
}

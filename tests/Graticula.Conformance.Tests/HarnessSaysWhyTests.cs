using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// That a failure from this harness carries the server's own explanation.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-174](../../docs/architecture-debt.md).</b> `GetJsonAsync` asserted on the status
/// code one line before it read the body, so every failure said only
/// *GET … returned 503. An ArcGIS client stops here.* Four different causes answer 503 —
/// an unreachable source, a full connection budget, an unreadable platform store, and a
/// credential that cannot be decrypted — and this server names which one in the body it
/// was discarding. A CI failure on 2026-08-26 was unattributable for exactly that reason.
/// </para>
/// <para>
/// <b>Why this is a test and not a comment.</b> The remedy is *read the body first*, and
/// it is written in four places across two suites. [D-46](../../docs/architecture-debt.md)
/// is the record of what happens to a behaviour fixed in one of the several places that
/// carry it, so the one that is easiest to reintroduce is held by an assertion rather than
/// by whoever remembers.
/// </para>
/// <para>
/// <b>It asks for a service that is not there</b>, because a 404 is the refusal this
/// server is most certain to produce with a body, on every deployment, with no fixture of
/// its own. What is asserted is the shape of the message, not the words of the refusal.
/// </para>
/// </remarks>
public sealed class HarnessSaysWhyTests : ArcGisClient
{
    [Fact]
    public async Task A_refused_request_reaches_the_failure_with_the_reason_in_it()
    {
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(
            () => GetJsonAsync("/rest/services/no_such_service_exists/FeatureServer/0/query"));

        Assert.Contains("returned 404", failure.Message, StringComparison.Ordinal);

        Assert.True(
            failure.Message.Contains("The server said:", StringComparison.Ordinal),
            "The failure did not carry the server's own words, which is D-174 exactly — the "
            + "body was read and discarded, or asserted after the status. What it said was:\n"
            + failure.Message);
    }
}

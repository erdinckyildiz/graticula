using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// How long a client may occupy a service, and what happens when they exceed it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner requirement, restated 2026-08-18 with the reference's Pooling page open:</b>
/// *"sadece geometri değil, tüm servislerde timeout olmalı"* — not only the geometry
/// service; every service needs a timeout. The first version of this requirement was
/// narrowed to the geometry service, which is where the settable deadline, wait and idle
/// bounds already live (ADR-022). This is the general answer.
/// </para>
/// <para>
/// <b>What existed before this, measured rather than assumed.</b> One fixed
/// 30-second <c>statement_timeout</c> on the connection pool
/// (<see cref="LayerConnections.StatementTimeout"/>), shared by every layer on a data
/// source. That bounds a <em>database statement</em> and nothing else: projecting the
/// geometry, encoding the response and writing a hundred thousand features all happen
/// after the statement has returned, and none of it was bounded. So the answer to *how
/// long can a client occupy a service* was: indefinitely. ArcGIS's own Pooling page calls
/// this *the maximum time a client can use a service*, and that is the thing being added.
/// </para>
/// <para>
/// <b>Two stages, because the token has to exist before the handler is invoked.</b> A
/// minimal-API handler's <c>CancellationToken</c> parameter is bound from
/// <see cref="HttpContext.RequestAborted"/> before its body runs, so replacing the token
/// inside the handler — after the service has been resolved — is too late for the
/// parameter it already received. And resolving the service in middleware, to know its
/// deadline, would read the catalogue a second time on every request, which is the waste
/// D-17 was about.
/// </para>
/// <para>
/// So: the middleware starts every request on the server-wide default and replaces
/// <c>RequestAborted</c> once. <see cref="ServiceLookup"/> — the one place that resolves a
/// URL to a service, and which reads the catalogue anyway — then calls
/// <see cref="LowerTo"/> if that service asks for less. **Lowering is the only operation
/// offered**, which is ADR-031's *may only lower* rule expressed as an API rather than as
/// a check somebody has to remember.
/// </para>
/// <para>
/// <b>Distinguishing our timer from a client hanging up.</b> Both surface as
/// <see cref="OperationCanceledException"/>, and they deserve different answers: a
/// disconnected client gets 499 for the access log, and a request that ran out of time
/// gets 504 with a sentence saying so. <see cref="Expired"/> is how
/// <c>ErrorResponse</c> tells them apart — without it every timeout would be logged as
/// *the caller went away*, which is the diagnosis that sends somebody to look at the
/// network.
/// </para>
/// </remarks>
internal sealed class RequestDeadline : IDisposable
{
    private const string Key = "gis-request-deadline";

    private readonly CancellationTokenSource _source;
    private bool _disposed;

    private RequestDeadline(CancellationTokenSource source, TimeSpan allowed)
    {
        _source = source;
        Allowed = allowed;
    }

    /// <summary>How long this request is allowed, after any lowering.</summary>
    public TimeSpan Allowed { get; private set; }

    /// <summary>Whether the deadline fired, as opposed to the client going away.</summary>
    public bool Fired { get; private set; }

    /// <summary>
    /// The deadline behind this request, or null outside a service route.
    /// </summary>
    public static RequestDeadline? Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(Key, out object? held)
            ? held as RequestDeadline
            : null;
    }

    /// <summary>Whether this request ran out of time rather than being abandoned.</summary>
    /// <param name="context">The request.</param>
    public static bool Expired(HttpContext context) => Of(context)?.Fired == true;

    /// <summary>
    /// Shortens this request's deadline, never lengthens it.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="wanted">What the service allows.</param>
    /// <remarks>
    /// <b>Silently ignored when it would raise the bound, and that is the point.</b> A
    /// service is configured with a ceiling it may narrow, not with a budget it may claim
    /// — ADR-031 §4 for the statement timeout, and the same argument here: a per-service
    /// override that can raise the server's bound is a per-service way around it. So this
    /// takes the smaller of the two and says nothing, because a caller asking for more
    /// time is not making an error, they are being held to the deployment's limit.
    /// </remarks>
    public static void LowerTo(HttpContext context, TimeSpan? wanted)
    {
        ArgumentNullException.ThrowIfNull(context);

        // <b>Nullable, because *not configured* is what almost every service says.</b> The seam
        // that calls this reads a nullable column, and making the caller write the null check
        // would put the same three lines at every future call site — where one of them would
        // eventually be written as `?? TimeSpan.Zero` and mean something else.
        if (wanted is not { } asked || Of(context) is not { } deadline || asked <= TimeSpan.Zero
            || asked >= deadline.Allowed)
        {
            return;
        }

        deadline.Allowed = asked;
        deadline._source.CancelAfter(asked);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.Dispose();
    }

    /// <summary>Marks the deadline as the reason this request was cancelled.</summary>
    private void FireIfOurs(CancellationToken clientGoneAway)
    {
        // <b>Ours only if the client is still there.</b> When a browser closes the tab both
        // tokens end up cancelled, and attributing that to the deadline would report a
        // timeout for a request nobody was waiting on any more.
        Fired = !clientGoneAway.IsCancellationRequested;
    }

    /// <summary>
    /// Puts every request on a deadline, which a service may then shorten.
    /// </summary>
    /// <remarks>
    /// <b>Registered for everything, not only for service routes.</b> An administrative
    /// call that never returns occupies a connection and a thread exactly as a query does,
    /// and the argument for bounding one is the argument for bounding the other. What
    /// differs is only that a service can ask for less.
    /// </remarks>
    internal sealed class Middleware
    {
        private readonly RequestDelegate _next;
        private readonly TimeSpan _default;

        /// <summary>Creates the middleware.</summary>
        public Middleware(RequestDelegate next, TimeSpan allowed)
        {
            _next = next;
            _default = allowed;
        }

        /// <summary>Runs the request under a deadline.</summary>
        public async Task InvokeAsync(HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (_default <= TimeSpan.Zero)
            {
                // Nought means no bound, which a deployment may choose and which this
                // records by doing nothing rather than by a flag somewhere else.
                await _next(context).ConfigureAwait(false);
                return;
            }

            CancellationToken client = context.RequestAborted;

            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(client);

            using RequestDeadline deadline = new(linked, _default);

            linked.CancelAfter(_default);
            context.Items[Key] = deadline;
            context.RequestAborted = linked.Token;

            try
            {
                await _next(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                deadline.FireIfOurs(client);
                throw;
            }
            finally
            {
                // <b>Put back, because the framework owns this.</b> Kestrel reads
                // RequestAborted after the pipeline unwinds, and leaving it pointing at a
                // disposed source is an ObjectDisposedException in somebody else's code.
                //
                // <b>The deadline itself stays in Items, and removing it was a defect.</b>
                // The exception handler runs outside this middleware, so a cleared entry
                // meant `Expired` was always false and every timeout would have been
                // answered 499 — *the caller went away* — about a request the server
                // stopped. Found by the test that asks whether a fired deadline was
                // recorded. `Items` is per request, so there was nothing to clean up: the
                // next request has its own dictionary. What must not happen after this
                // point is `LowerTo`, and nothing outside the pipeline calls it.
                context.RequestAborted = client;
            }
        }
    }
}

/// <summary>Where the deadline middleware is added.</summary>
internal static class RequestDeadlineExtensions
{
    /// <summary>
    /// Bounds how long any request may run, before the endpoints see it.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <param name="allowed">The server-wide default. Zero or less means no bound.</param>
    public static IApplicationBuilder UseRequestDeadline(
        this IApplicationBuilder app, TimeSpan allowed)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<RequestDeadline.Middleware>(allowed);
    }
}

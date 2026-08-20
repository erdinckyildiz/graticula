using System;
using System.Collections.Generic;
using Graticula.Platform.Postgres;
using System.IO;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace Graticula.Host;

/// <summary>
/// What the server needs in order to start.
/// </summary>
/// <remarks>
/// <para>
/// ADR-017 §5c: <b>configuration holds what is needed to start; everything else
/// is state and goes through the admin API.</b> Certificates, migrations and
/// pinning are all operations rather than file edits. If this class grows a
/// setting that changes behaviour at runtime, that setting is in the wrong
/// place.
/// </para>
/// <para>
/// Validated once, here, at startup. A server that starts and then fails on the
/// first request because a setting was wrong is harder to diagnose than one that
/// refuses with a reason.
/// </para>
/// </remarks>
internal sealed record HostSettings(
    string PlatformStore,
    string SecretKeyBase64,
    int SecretKeyVersion,
    IPAddress ListenAddress,
    int Port,
    string HostName,
    bool RequireHttps,
    string? CertificatePath,
    string? CertificatePassword,
    TimeSpan SessionLifetime,
    string StatePath,
    string ImportScratchPath,
    long ImportScratchBudgetBytes,
    string TileCachePath,
    long TileCacheBudgetBytes,
    long TileCacheLayerBudgetBytes,
    TimeSpan TileCacheLifetime,
    int OverlayWorkers,
    TimeSpan OverlayDeadline,
    long OverlayPreflightPairs,
    TimeSpan OverlayWait,
    TimeSpan OverlayIdle,
    TimeSpan CatalogFallbackWindow,
    long MaximumResponseBytes,
    TimeSpan RequestDeadline,
    int ConnectionBudget,
    int PerSourceConcurrency,
    int MaximumRecordCount,
    int DefaultRecordCount,
    int MaximumImageWidth,
    int MaximumImageHeight,
    int JpegQuality,
    IReadOnlyList<string>? LegacyKeys = null)
{
    /// <summary>Reads and validates settings.</summary>
    /// <exception cref="InvalidOperationException">A setting is missing or unusable.</exception>
    public static HostSettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Reader keys = new(configuration);

        string platformStore = keys.Require("PlatformStore",
            "the connection string for the platform store");

        string key = keys.Require("SecretKey",
            "the base64 AES-256 key that seals data source credentials (ADR-002 §4.7)");

        try
        {
            int length = Convert.FromBase64String(key).Length;
            if (length != 32)
            {
                throw new InvalidOperationException(
                    $"Graticula:SecretKey decodes to {length} bytes; AES-256 needs 32.");
            }
        }
        catch (FormatException e)
        {
            throw new InvalidOperationException("Graticula:SecretKey is not valid base64.", e);
        }

        string listen = keys.Text("Listen") ?? "0.0.0.0";
        if (!IPAddress.TryParse(listen, out IPAddress? address))
        {
            throw new InvalidOperationException(
                $"Graticula:Listen '{listen}' is not an IP address.");
        }

        // HTTPS unless explicitly disabled — ADR-014 §2a. The default is the
        // secure one, and turning it off is a deliberate act that warns at every
        // startup rather than once.
        bool requireHttps = keys.Value("RequireHttps", true);

        // <b>A budget with a default, because N6's finding was that there was
        // none at all.</b> 2 GB holds a useful seeded pyramid for a handful of
        // layers and will not surprise anybody running this on a laptop; a
        // deployment that wants more sets it, and the number is visible in one
        // place rather than implied by whatever the disk happened to have.
        long budget = keys.Value("TileCacheBudgetMB", 2048L);

        // <b>Per-layer quota, so one layer cannot take the whole cache.</b>
        // A quarter of the total: enough that a single busy layer is not
        // artificially starved, small enough that four of them cannot crowd
        // everything else out between them.
        long layerBudget = keys.Value("TileCacheLayerBudgetMB", Math.Max(1, budget / 4));

        return new HostSettings(
            platformStore,
            key,
            keys.Value("SecretKeyVersion", 1),
            address,
            keys.Value("Port", requireHttps ? 8443 : 8080),
            keys.Text("HostName") ?? Dns.GetHostName(),
            requireHttps,
            keys.Text("CertificatePath"),
            keys.Text("CertificatePassword"),

            // Twelve hours: long enough that a working day does not need a
            // second sign-in, short enough that a token copied out of a browser
            // is not useful next week. ADR-015 3 makes this cheap to change --
            // sessions are server-side, so shortening it takes effect at once
            // rather than waiting for issued tokens to expire.
            TimeSpan.FromHours(keys.Value("SessionHours", 12)),

            // ADR-016 §3's secret volume. Defaults to a directory beside the
            // process for a local run, and is mounted in a container. Anything
            // that must survive a container replacement and is not in the
            // platform database lives here — today that is the serving
            // certificate.
            keys.Text("StatePath")
                ?? Path.Combine(AppContext.BaseDirectory, "state"),

            // <b>Where an uploaded archive waits for a worker, and it is not under StatePath.</b>
            // Same reasoning as the tile cache below: this holds nothing that must survive a container
            // replacement. An archive whose job never ran is a job that failed, and the honest recovery
            // is to upload it again — not to back up somebody's geodatabase on the volume that carries
            // the serving certificate.
            //
            // <b>Shared with the worker, which is why it is configurable at all.</b> ADR-016 §2 puts
            // the worker in its own container, so this path is a volume both mount — the server writes,
            // the worker reads. A default beside the process works for a local run and is wrong in a
            // deployment, which is what the compose file is for.
            keys.Text("ImportScratchPath")
                ?? Path.Combine(AppContext.BaseDirectory, "import"),

            // <b>ADR-037 condition 6, and the number is the format's rather than round.</b> The
            // owner's three real geodatabases are 0.3, 3.9 and 0.06 MB compressed; 2 GB is three
            // orders of magnitude above the largest and still below what would trouble a disk. It
            // bounds **one upload at its transferred size** — the expansion is gone, because GDAL
            // reads inside the archive and nothing unpacks here.
            keys.Value("ImportScratchBudgetMB", 2048L) * 1024 * 1024,

            // <b>Not under StatePath, deliberately.</b> StatePath holds things
            // that must survive a container replacement — the serving
            // certificate. A tile cache must not: it is derived data, it is the
            // largest thing this server writes, and putting it on the volume
            // that must be backed up would make every backup carry gigabytes of
            // tiles that can be rebuilt from the database in seconds.
            keys.Text("TileCachePath")
                ?? Path.Combine(AppContext.BaseDirectory, "tilecache"),

            budget * 1024 * 1024,
            layerBudget * 1024 * 1024,

            // <b>One hour, and it is a placeholder for a per-layer setting.</b>
            // ADR-010 §5.3 is explicit that TTL cannot be one global number — a
            // cadastral layer that changes twice a year and an incident layer
            // that changes every minute need opposite answers, and the
            // administrator is the only person who knows which is which (A-028).
            // Volatility is not in the schema yet, so this is the floor until it
            // is, and it is written here rather than left at some library
            // default so the gap is visible.
            TimeSpan.FromMinutes(
                keys.Value("TileCacheMinutes", 60)),

            // <b>Two, and the number is a memory budget rather than a
            // throughput one.</b> Each overlay worker may allocate up to its
            // 1 GB ceiling, so the server's total exposure to overlay is this
            // number times that ceiling and nothing else — which is the property
            // Q-97 exists to give an operator. Raising it raises the worst case
            // linearly, and that is the trade to state rather than to bury.
            Math.Max(1, keys.Value("OverlayWorkers", 2)),

            // <b>The overlay deadline, a setting since 2026-08-17 and a constant before it.</b>
            // The owner asked what the geometry service's timeout was — *"geometry server'in,
            // startı stop'u, timeout'u vs si yok mu?"* — and there is one, ten seconds, compiled
            // in. Their own earlier instruction is what makes a fixed one wrong: when they
            // removed the rule refusing six operations for being *potentially* expensive, the
            // instruction was **let them, and put a timeout on it.** A timeout the operator
            // cannot move is half of that.
            //
            // <b>Ten stays the default on the measurement.</b> Every real case in
            // benchmarks/geometry-overlay finished inside 350 ms and the smallest adversarial
            // input that matters took 17 seconds, so ten leaves real work thirty times its
            // measured cost and still refuses the attack.
            TimeSpan.FromSeconds(Math.Max(1, keys.Value("OverlayDeadlineSeconds", 10))),

            // <b>The pre-flight, zero meaning off, and zero is the default because it was
            // measured leaky.</b> It under-predicted cost by fourteen times: it admits a comb
            // that takes 884 ms and turns away nothing the deadline would not catch. Kept
            // reachable because a deployment that would rather refuse a heavy request in 80 ms
            // than spend the deadline on it can choose that — GeometryWorkerPool.PreflightAbove
            // is the value it used to have.
            Math.Max(0, (long)keys.Value("OverlayPreflightPairs", 0)),

            // <b>The queue wait, its own budget since 2026-08-17.</b> It used to be the work's
            // deadline, one number doing two jobs, and the comment beside it argued for the split
            // it was not making. ArcGIS Server Manager's Pooling page keeps them apart — *the
            // maximum time a client can use a service* and *the maximum time a client will wait to
            // get* one — and the owner asked whether that was a good example. It is: a deployment
            // can accept long work and still refuse to hold a connection behind somebody else's.
            //
            // The default equals the deadline, so an untouched deployment behaves exactly as it did.
            TimeSpan.FromSeconds(Math.Max(1, keys.Value("OverlayWaitSeconds",
                Math.Max(1, keys.Value("OverlayDeadlineSeconds", 10))))),

            // <b>How long an unused worker is kept, and this one closed a real gap.</b> A returned
            // worker went into a bag and came out again for ever, so a deployment that ran one
            // overlay at nine in the morning held two worker processes — each able to have grown to
            // its 1 GB ceiling — until it was restarted. Thirty minutes is the reference's own
            // number and it survives measurement: the cost of reclaiming is one cold start, and a
            // real one measured **674 ms against 26 ms warm** for the same trivial overlay — mostly
            // process launch and first-call JIT rather than the work. Half an hour of quiet is not
            // a window in which 650 ms matters, and two idle processes are memory nobody is getting
            // back. Zero keeps them for ever, which is the old behaviour.
            TimeSpan.FromSeconds(Math.Max(0, keys.Value("OverlayIdleSeconds",
                (int)GeometryWorkerPool.DefaultIdleBudget.TotalSeconds))),

            // <b>How long a remembered catalogue entry may be served while the
            // platform store is unreachable (Q-95).</b> Zero disables degraded
            // serving entirely, which is the posture for a deployment that would
            // rather stop than answer on a permission nobody can confirm — and
            // that is a real preference, so it is reachable from configuration
            // rather than only from a code change.
            TimeSpan.FromMinutes(keys.Value("CatalogFallbackMinutes", (int)CatalogFallback.DefaultWindow.TotalMinutes)),

            // <b>A ceiling on a response body, in bytes, because bytes are what a
            // ceiling is for (Q-113).</b> `resultRecordCount` bounds rows and
            // nothing bounded their width: every field of every feature at full
            // precision stays inside every limit this server had and can still be
            // hundreds of megabytes. A row is not a unit of cost — one polygon can
            // outweigh ten thousand points.
            //
            // <b>64 MiB, and the number is a judgement rather than a
            // measurement.</b> Large enough that no ordinary page reaches it, small
            // enough to bound one request's memory and one client's bandwidth.
            // **Zero disables it**, which is the behaviour of every build before
            // this one, so a deployment that would rather stream without a limit
            // can say so.
            Math.Max(0, keys.Value<long>("MaximumResponseBytes", 64L * 1024 * 1024)),

            // <b>How long a client may occupy any service, and 600 seconds is not our
            // number.</b> Owner requirement 2026-08-18: every service needs a timeout, not
            // only the geometry service. The default is what the reference's Pooling page
            // shows for *the maximum time a client can use a service* — ten minutes, which
            // is generous enough that no ordinary request meets it and finite enough that
            // one runaway does not hold a connection for ever.
            //
            // <b>A service may lower it and never raise it</b> (RequestDeadline.LowerTo),
            // which is ADR-031 §4's rule for the statement timeout applied to the whole
            // request. **Zero disables it**, which is the behaviour of every build before
            // this one — so a deployment that has its own front-end timeout can say so
            // rather than having two bounds disagree.
            TimeSpan.FromSeconds(Math.Max(0, keys.Value("RequestDeadlineSeconds", 600))),

            // <b>ADR-007 §4.8's two bounds, and the numbers come from a measurement rather than from
            // taste.</b> [Q-04](../../docs/open-questions.md) counted what a request costs: peak
            // backends track concurrent requests at about 1.3×, and every pool takes Npgsql's default
            // maximum of 100 because nothing sets it — so six data sources is 700 potential
            // connections per worker against a default PostgreSQL's `max_connections` of 100.
            //
            // **64 per worker** is chosen so that the worker's whole demand fits inside an
            // unconfigured PostgreSQL beside the platform store's own pool, with room for the
            // administrative connections a DBA needs to get in and look. **24 per data source** is
            // sized for blast radius rather than throughput, which is N4's whole point: one slow
            // database can then occupy at most a third of the worker instead of all of it.
            //
            // Zero means unbounded, for a deployment that has measured its own database and wants
            // this out of the way. It is not the default, because the state before this existed was
            // unbounded and that is what Q-04 found.
            Math.Max(0, keys.Value("ConnectionBudget", 64)),
            Math.Max(0, keys.Value("PerSourceConcurrency", 24)),

            // <b>The most rows one query may return, for the whole deployment.</b> Owner, 2026-08-19:
            // *"dönebilecek max recordlarla alakalı genel bir kural olması lazım… düşünsene 3 milyonluk
            // record'u olan bir veriye n tane request atıldığını."* It was a compile-time constant —
            // `FeatureQuery.MaximumLimit`, 50,000 — and a service could lower it one service at a time
            // (ADR-031, Q-113), which means the next service published starts at 50,000 again. An
            // operator had no way to say *nothing on this server returns more than two thousand*.
            //
            // <b>The default stays at the model's own bound so that nothing changes by upgrading.</b>
            // Lowering it is the operator's decision, not ours to make on their behalf — but 1,000 to
            // 5,000 is the range a deployment serving a three-million-row layer wants, and it is what
            // ArcGIS Server itself ships (1,000). Clamped to the model's ceiling because
            // `FeatureQuery` refuses a larger limit outright: a setting that produced a request the
            // domain rejects would be a configuration error surfacing as a 500 on every query.
            Math.Clamp(
                keys.Value("MaximumRecordCount", Graticula.Features.FeatureQuery.MaximumLimit),
                1,
                Graticula.Features.FeatureQuery.MaximumLimit),

            // <b>And the page size when the caller does not ask.</b> Kept at 1,000, which is what the
            // parser's own constant was and what every ArcGIS client expects to page against. Clamped
            // to the ceiling above rather than validated against it, because the two are set
            // independently and a default larger than the maximum is a contradiction the operator
            // should not be able to write.
            Math.Clamp(keys.Value("DefaultRecordCount", 1000), 1, Math.Clamp(
                keys.Value("MaximumRecordCount", Graticula.Features.FeatureQuery.MaximumLimit),
                1,
                Graticula.Features.FeatureQuery.MaximumLimit)),

            // <b>The largest image a stranger may ask this server to allocate.</b>
            // ADR-041: a WMS WIDTH and HEIGHT are a memory allocation the caller
            // chooses, and 20,000 x 20,000 is four gigabytes of surface in a request
            // that costs nothing to send. 4,096 covers every real map client and a
            // print at 300 dpi on A3; an operator with a plotter raises it knowingly.
            // Published in the capabilities document as MaxWidth and MaxHeight, so a
            // client learns the limit rather than discovering it by refusal.
            Math.Clamp(keys.Value("MaximumImageWidth", 4096), 16, 16384),
            Math.Clamp(keys.Value("MaximumImageHeight", 4096), 16, 16384),

            // JPEG quality. 85 is where the artefacts stop being visible on a map's
            // hard edges, which is a harder case than a photograph: a one-pixel road
            // over a flat fill is exactly what block transforms smear.
            Math.Clamp(keys.Value("JpegQuality", 85), 1, 100),

            // What this start read under the former name, for the warning that tells the
            // operator which keys to move. Empty on a deployment configured as Graticula.
            keys.Legacy);
    }

    /// <summary>
    /// Reads a setting under the product's name, and under the old one if it has to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fallback is the point, and it is not politeness</b> — ADR-032 §5. The
    /// product was renamed from <c>gis-server</c> on 2026-08-17, which renames its
    /// configuration section with it. One of those keys, <c>SecretKey</c>, is what
    /// decrypts every stored data-source credential: a rename that silently stopped
    /// reading the old name would turn a working server into one that cannot open its
    /// own catalogue, and would say so with a message about a *missing* setting rather
    /// than a *renamed* one. So both names are read, the new one wins, and a start that
    /// used an old key reports which — because a compatibility path nobody is told about
    /// is one nobody knows to stop relying on.
    /// </para>
    /// <para>
    /// Removing the fallback is a separate decision for a separate day, and the list of
    /// keys still in use is the evidence for taking it.
    /// </para>
    /// </remarks>
    private sealed class Reader(IConfiguration configuration)
    {
        private const string Section = "Graticula";
        private const string Was = "GisServer";

        private readonly List<string> _legacy = [];

        /// <summary>Old-name keys this start actually read, in the order found.</summary>
        public IReadOnlyList<string> Legacy => _legacy;

        /// <summary>The value under either name, or null.</summary>
        public string? Text(string name)
        {
            if (configuration[$"{Section}:{name}"] is { Length: > 0 } current)
            {
                return current;
            }

            if (configuration[$"{Was}:{name}"] is { Length: > 0 } legacy)
            {
                _legacy.Add($"{Was}:{name}");
                return legacy;
            }

            return null;
        }

        /// <summary>The typed value under either name, or the default.</summary>
        /// <remarks>
        /// `GetValue` is declared as returning <c>T?</c>, so the coalesce is what makes
        /// the signature honest rather than a suppression: a key that is present but
        /// unparseable is the one case where it can hand back null, and the default is
        /// the right answer to that.
        /// </remarks>
        public T Value<T>(string name, T defaultValue)
        {
            // Bound through the configuration provider rather than parsed here, so a
            // malformed number fails the same way it always did.
            if (configuration[$"{Section}:{name}"] is { Length: > 0 })
            {
                return configuration.GetValue($"{Section}:{name}", defaultValue) ?? defaultValue;
            }

            if (configuration[$"{Was}:{name}"] is { Length: > 0 })
            {
                _legacy.Add($"{Was}:{name}");
                return configuration.GetValue($"{Was}:{name}", defaultValue) ?? defaultValue;
            }

            return defaultValue;
        }

        /// <summary>The value under either name, or a refusal that says what to set.</summary>
        public string Require(string name, string what) =>
            Text(name)
            ?? throw new InvalidOperationException(
                $"{Section}:{name} is not configured. It is {what}. Set it in "
                + "appsettings.json, in user secrets, or as the environment variable "

                // Two underscores, not one. The environment provider maps '__'
                // to ':'; a single underscore produces a variable that is set,
                // looks right, and is never read. The first version of this
                // message said one underscore and cost a startup to find.
                + $"{Section}__{name}. The former name {Was}__{name} is still read, so "
                + "an existing deployment does not have to be reconfigured to start.");
    }
}

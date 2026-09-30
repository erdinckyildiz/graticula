using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Claims feature export jobs and writes some of a service's layers into a file a caller downloads — ADR-106.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tile exporter's machinery with rows for tiles.</b> The claim, the lease kept by
/// <see cref="JobLeaseKeeper"/>, the two-second checkpoint that learns of a cancel, the sweep of expired files and
/// strays, the <c>.part</c> written whole and renamed, and one export at a time per process are
/// <see cref="TileExporter"/>'s — its own worker rather than the tile exporter's, so a long tile package does not queue a
/// small CSV behind it (ADR-106 §5.1). What differs is the walk: for each chosen layer the host reads the rows through
/// the layer's feature source (<see cref="LayerExportRows"/>, the synchronous route's own reader) into a staging file,
/// and the import reader's GDAL writes the format from it, so GDAL stays out of this process (ADR-009 §2.2).
/// </para>
/// <para>
/// <b>Restarted, not resumed — ADR-106 §5.1.</b> A half-written GeoPackage has no durable cursor, so a run that loses its
/// lease leaves nothing behind (its staging folder and <c>.part</c> go with it) and the next run reads from the first
/// row. The kind is <see cref="JobRerun.Harmless"/>, so a second loss fails it.
/// </para>
/// <para>
/// <b>More rows than the cap fail the export and say so; nothing is cut.</b> The cap is
/// <see cref="HostSettings.FeatureExportMaximumRows"/> across the chosen layers. It is checked when the export is asked
/// for, and again here as each layer is read, because a layer can grow between the two: the writer reads one row past
/// what is left of the cap, which is the proof there is more.
/// </para>
/// <para>
/// <b>Its files are under <c>&lt;exports&gt;/data/</c></b> (<see cref="HostSettings.FeatureExportDirectory"/>) — the
/// result as <c>&lt;token&gt;.export</c>, the file being written as <c>&lt;token&gt;.part</c> and the layers' staging
/// under <c>&lt;token&gt;.staging/</c> — so the tile exporter's stray sweep, which reads the folder above, cannot take
/// them and this one cannot take a tile package.
/// </para>
/// </remarks>
internal sealed partial class FeatureExporter : BackgroundService
{
    /// <summary>The most rows one export may hold across its layers unless a deployment says otherwise.</summary>
    public const long DefaultMaximumRows = 2_000_000;

    /// <summary>How long the reader may take over one layer unless a deployment says otherwise.</summary>
    public const int DefaultTimeoutMinutes = 60;

    /// <summary>How many rows are read to weigh a layer for the estimate.</summary>
    internal const int SampleRows = 500;

    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StrayAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan CheckpointEvery = TimeSpan.FromSeconds(2);

    /// <summary>What this worker calls itself when it claims a job — D-96.</summary>
    private static readonly string Who =
        "graticula/feature-export " + Environment.MachineName + "#" + Environment.ProcessId;

    private const int Speaks = 1;

    private readonly IJobStore _jobs;
    private readonly IFeatureExportStore _exports;
    private readonly JobSignal _signal;
    private readonly PostgresLayerCatalog _catalog;
    private readonly ServiceContexts _contexts;
    private readonly GeodatabaseReader _reader;
    private readonly IAuditLog _audit;
    private readonly HostSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<FeatureExporter> _log;

    private readonly RepeatedFailure _claims = new();
    private readonly LeaseSweep _leases = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    private TimeSpan _waiting = Idle;
    private DateTimeOffset _swept = DateTimeOffset.MinValue;
    private DateTimeOffset _strays = DateTimeOffset.MinValue;

    public FeatureExporter(
        IJobStore jobs,
        IFeatureExportStore exports,
        JobSignal signal,
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        GeodatabaseReader reader,
        IAuditLog audit,
        HostSettings settings,
        TimeProvider clock,
        ILogger<FeatureExporter> log)
    {
        _jobs = jobs;
        _exports = exports;
        _signal = signal;
        _catalog = catalog;
        _contexts = contexts;
        _reader = reader;
        _audit = audit;
        _settings = settings;
        _clock = clock;
        _log = log;
    }

    /// <summary>The finished file of a token, inside the export directory and nowhere else — or null for a non-token.</summary>
    /// <param name="directory">The feature export directory.</param>
    /// <param name="token">The token.</param>
    /// <remarks>
    /// <b>No part of a request becomes a path</b>, for <see cref="TileExporter.FileOf"/>'s reason: the token is the
    /// store's, 32 characters of <c>0-9a-f</c>, and the result is checked once more to lie inside the directory.
    /// </remarks>
    public static string? FileOf(string directory, string token) => TileExporter.Inside(directory, token + ".export", token);

    /// <summary>The file being written — renamed to <see cref="FileOf"/> when it is complete.</summary>
    public static string? PartOf(string directory, string token) => TileExporter.Inside(directory, token + ".part", token);

    /// <summary>The folder an export's layers are staged and assembled in.</summary>
    public static string? StagingOf(string directory, string token) => TileExporter.Inside(directory, token + ".staging", token);

    /// <summary>Stops an export this process is running at once, rather than at its next checkpoint.</summary>
    /// <param name="job">The export.</param>
    /// <returns>True when it was running here.</returns>
    public bool Stop(Guid job)
    {
        if (!_running.TryGetValue(job, out CancellationTokenSource? stop))
        {
            return false;
        }

        try
        {
            stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    /// <summary>Deletes an export's files, whatever state they are in; true when none is left.</summary>
    public bool DeleteFiles(string token, ILogger? log = null)
    {
        bool clean = true;

        foreach (string? path in (IEnumerable<string?>)
                 [
                     FileOf(_settings.FeatureExportDirectory, token),
                     PartOf(_settings.FeatureExportDirectory, token),
                 ])
        {
            if (path is null || !File.Exists(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A file a download is reading is tried again by the sweep.
                LogFileStuck(log ?? _log, path, e.Message);
                clean = false;
            }
        }

        if (StagingOf(_settings.FeatureExportDirectory, token) is { } staging && Directory.Exists(staging))
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // <b>A staging folder still open is its own run's</b>, which deletes it when it ends; the sweep takes
                // it if that run was killed.
                LogFileStuck(log ?? _log, staging, e.Message);
            }
        }

        return clean;
    }

    /// <summary>What counting and weighing the chosen layers came to.</summary>
    /// <param name="Rows">Rows across the layers, counted up to one past what the cap allows.</param>
    /// <param name="EstimatedBytes">The upper bound on the disk the export takes.</param>
    /// <param name="RowsByLayer">Each layer's count, in the order given.</param>
    internal sealed record Weighed(long Rows, long EstimatedBytes, IReadOnlyList<long> RowsByLayer);

    /// <summary>
    /// Counts each layer's rows, up to one past the cap in all, and weighs a sample of them — the dry run's numbers and
    /// the start's.
    /// </summary>
    /// <param name="layers">The chosen layers.</param>
    /// <param name="contexts">The layers' sources.</param>
    /// <param name="cap">The most rows one export may hold.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The counts and the estimate.</returns>
    /// <remarks>
    /// <b>The estimate is the size of the first <see cref="SampleRows"/> rows as staging, scaled to the count and
    /// doubled</b> (<see cref="FeatureExportPackaging.EstimateBytes"/>). It is not taken when the count already passes
    /// the cap — the export is refused, and reading a sample of every layer to say so would spend the source for
    /// nothing.
    /// </remarks>
    internal static async Task<Weighed> WeighAsync(
        IReadOnlyList<PublishedLayer> layers, ServiceContexts contexts, long cap, CancellationToken cancellation)
    {
        List<long> counts = [];
        long total = 0;

        foreach (PublishedLayer layer in layers)
        {
            (IFeatureSource source, _) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

            // One past what is left, which is all that is needed to know it does not fit.
            long counted = await source
                .CountUpToAsync(new FeatureQuery(1), Math.Max(1, cap - total + 1), cancellation)
                .ConfigureAwait(false);

            counts.Add(counted);
            total += counted;

            if (total > cap)
            {
                return new Weighed(total, 0, counts);
            }
        }

        long bytes = 0;

        for (int i = 0; i < layers.Count; i++)
        {
            if (counts[i] == 0)
            {
                bytes += FeatureExportPackaging.PerLayerOverhead;
                continue;
            }

            (IFeatureSource source, LayerDescription described) =
                await contexts.GetAsync(layers[i], cancellation).ConfigureAwait(false);

            ByteCounter weighed = new();

            long sampled = await LayerExportRows.WriteGeoJsonAsync(
                    weighed, source, described, layers[i], layers[i].Definition.Srid, SampleRows - 1, SampleRows, null, cancellation)
                .ConfigureAwait(false);

            bytes += FeatureExportPackaging.EstimateBytes(weighed.Length, sampled, counts[i]);
        }

        return new Weighed(total, bytes, counts);
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            Directory.CreateDirectory(_settings.FeatureExportDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Said when an export tries to write there, where somebody is looking.
            LogFileStuck(_log, _settings.FeatureExportDirectory, e.Message);
        }

        while (!stopping.IsCancellationRequested)
        {
            JobRecord? job;

            try
            {
                job = await _jobs.ClaimAsync(JobKind.FeatureExport, Who, Speaks, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception unreachable)
            {
                Report(unreachable);
                await Wait(stopping).ConfigureAwait(false);
                continue;
            }

            Claimed();

            if (job is null)
            {
                await _leases.SweepAsync(_jobs, _log, stopping).ConfigureAwait(false);
                await SweepAsync(stopping).ConfigureAwait(false);
                await Wait(stopping).ConfigureAwait(false);
                continue;
            }

            _waiting = Idle;

            await RunAsync(job, stopping).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes files past their time and the files of exports that ended without one, and, once an hour, files in the
    /// directory that no live export owns — ADR-106 §5.4.
    /// </summary>
    internal async Task SweepAsync(CancellationToken stopping)
    {
        DateTimeOffset now = _clock.GetUtcNow();

        if (now - _swept < SweepEvery)
        {
            return;
        }

        _swept = now;

        try
        {
            foreach ((Guid job, string token) in
                     await _exports.DueForRemovalAsync(now, 100, stopping).ConfigureAwait(false))
            {
                // <b>Marked only when the file is gone</b>, so a file a download is still reading is tried again on
                // the next sweep rather than forgotten on disk.
                if (DeleteFiles(token) && await _exports.MarkRemovedAsync(job, stopping).ConfigureAwait(false))
                {
                    LogRemoved(job);
                }
            }

            if (now - _strays >= StrayAfter)
            {
                _strays = now;
                IReadOnlySet<string> live = await _exports.LiveTokensAsync(stopping).ConfigureAwait(false);

                DeleteStrays(live, now);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    /// <summary>
    /// Deletes what an hour has passed over whose token no live export owns — a file whose row went with its service, a
    /// staging folder a killed process left.
    /// </summary>
    /// <remarks>
    /// <b>Only names this server makes</b> — a token and <c>.export</c>, <c>.part</c> or <c>.staging</c> — in its own
    /// subfolder, so nothing an operator put there, and nothing of the tile exporter's, is touched.
    /// </remarks>
    private void DeleteStrays(IReadOnlySet<string> live, DateTimeOffset now)
    {
        if (!Directory.Exists(_settings.FeatureExportDirectory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFileSystemEntries(_settings.FeatureExportDirectory))
        {
            string name = Path.GetFileName(path);
            int dot = name.IndexOf('.', StringComparison.Ordinal);

            if (dot != 32 || !TileExporter.IsToken(name[..dot])
                || name[dot..] is not (".export" or ".part" or ".staging")
                || live.Contains(name[..dot]))
            {
                continue;
            }

            try
            {
                bool folder = Directory.Exists(path);
                DateTime written = folder ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);

                if (now - written < StrayAfter)
                {
                    continue;
                }

                if (folder)
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LogFileStuck(_log, path, e.Message);
            }
        }
    }

    private async Task RunAsync(JobRecord job, CancellationToken stopping)
    {
        await using JobLeaseKeeper lease = JobLeaseKeeper.Hold(_jobs, job.Id, Who, _log, stopping);

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(lease.Working);
        _running[job.Id] = stop;

        FeatureExportState? state = null;

        try
        {
            state = await _exports.FindAsync(job.Id, stop.Token).ConfigureAwait(false);
            await ExportAsync(job, state, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The server stopping, a lost lease or a cancel: the partial files go, and nothing is written — a cancel
            // has already said `cancelled`, and a lost lease belongs to whoever has the job now.
            if (state is not null)
            {
                DeleteFiles(state.Token);
            }

            if (!stopping.IsCancellationRequested && !lease.Lost)
            {
                LogReleased(job.Id);
            }
        }
        catch (Exception failed)
        {
            if (state is not null)
            {
                DeleteFiles(state.Token);
            }

            await FailAsync(job, failed.Message, failed, stopping).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(job.Id, out _);
        }
    }

    private async Task ExportAsync(JobRecord job, FeatureExportState? state, CancellationTokenSource stop)
    {
        CancellationToken working = stop.Token;

        if (state is null)
        {
            await FailAsync(
                job, "The service this export was for no longer exists, so there is nothing to export.", null, working)
                .ConfigureAwait(false);
            return;
        }

        (string? folder, string? name) = TileSeeder.Address(job.Detail);

        PublishedService? service = name is null
            ? null
            : await _catalog.FindServiceAsync(folder, name, working).ConfigureAwait(false);

        if (service is null || service.Id != state.ServiceId)
        {
            await FailAsync(
                job,
                $"The service '{(folder is null ? name : folder + "/" + name)}' was renamed or moved after this export "
                + "was asked for. Ask for an export of it where it is now.",
                null,
                working).ConfigureAwait(false);
            return;
        }

        List<PublishedLayer> layers = [];

        foreach (int id in state.Layers)
        {
            if (service.Layer(id) is not { } layer)
            {
                await FailAsync(
                    job,
                    $"Layer {id} of '{service.QualifiedName}' was removed after this export was asked for. "
                    + "Ask for an export of the layers it has now.",
                    null,
                    working).ConfigureAwait(false);
                return;
            }

            layers.Add(layer);
        }

        string directory = _settings.FeatureExportDirectory;
        Directory.CreateDirectory(directory);

        string staging = StagingOf(directory, state.Token)
            ?? throw new InvalidOperationException("The export's token is not one this server makes.");
        string part = PartOf(directory, state.Token)!;
        string final = FileOf(directory, state.Token)!;

        // <b>From nothing, every run</b> — §5.1. Whatever a lost run left is gone before this one writes.
        DeleteFiles(state.Token);
        Directory.CreateDirectory(staging);

        FeatureExportFormat format = state.Format;
        FeatureExportPackaging packaging = FeatureExportPackaging.Of(format, layers.Count);
        IReadOnlyList<string> names = FeatureExportPackaging.LayerNames(layers.Select(l => l.Definition.Name), format);
        long cap = _settings.FeatureExportMaximumRows;

        string wire = PostgresFeatureExportStore.Wire(format);

        LogStarted(job.Id, service.QualifiedName, wire, layers.Count, state.RowsTotal);

        Checkpoint checkpoint = new(_exports, job.Id, Who, Math.Max(state.RowsTotal, 1), stop, _clock);

        try
        {
            // What each layer wrote, for the zip: the file and the name it has inside.
            List<(string Path, string Entry)> written = [];
            string? dataset = null;
            long rows = 0;

            for (int i = 0; i < layers.Count; i++)
            {
                PublishedLayer layer = layers[i];
                string layerName = names[i];

                (IFeatureSource source, LayerDescription described) =
                    await _contexts.GetAsync(layer, working).ConfigureAwait(false);

                // <b>One row past what is left</b> is read and proves there is more; a workbook's sheet has a limit of
                // its own, which is what is left if it is the smaller.
                long left = cap - rows;
                long allowed = format == FeatureExportFormat.Excel ? Math.Min(left, FeatureExportPackaging.WorkbookSheetRows) : left;
                long before = rows;

                await checkpoint.ReportAsync(rows, FeatureExportPhase.Reading, working, force: true).ConfigureAwait(false);

                long read;

                if (format == FeatureExportFormat.EsriJson)
                {
                    string target = Path.Combine(staging, layerName + ".json");

                    await using (FileStream file = File.Create(target))
                    {
                        read = await LayerExportRows.WriteEsriJsonAsync(file, layer, source, described, allowed, working)
                            .ConfigureAwait(false);
                    }

                    written.Add((target, layerName + ".json"));
                }
                else
                {
                    string input = Path.Combine(staging, $"in-{i}.geojson");

                    await using (FileStream file = File.Create(input))
                    {
                        read = await LayerExportRows.WriteGeoJsonAsync(
                                file, source, described, layer, layer.Definition.Srid, allowed, FeatureQuery.MaximumLimit,
                                (count, token) => checkpoint.ReportAsync(before + count, FeatureExportPhase.Reading, token),
                                working)
                            .ConfigureAwait(false);
                    }

                    if (read <= allowed)
                    {
                        await checkpoint.ReportAsync(before + read, FeatureExportPhase.Writing, working, force: true)
                            .ConfigureAwait(false);

                        dataset = await WriteLayerAsync(
                                format, packaging, input, layerName, i, staging, dataset, written, checkpoint, working)
                            .ConfigureAwait(false);
                    }

                    File.Delete(input);
                }

                if (read > allowed)
                {
                    throw new InvalidOperationException(RefusedForRows(layer.Definition.Name, format, cap, left, allowed));
                }

                rows += read;

                await CheckDiskAsync(state, staging, part, working).ConfigureAwait(false);
            }

            await checkpoint.ReportAsync(rows, FeatureExportPhase.Writing, working, force: true).ConfigureAwait(false);

            await AssembleAsync(format, packaging, part, dataset, written, staging, service.Name, checkpoint, rows, working)
                .ConfigureAwait(false);

            File.Move(part, final, overwrite: true);

            long bytes = new FileInfo(final).Length;
            DateTimeOffset now = _clock.GetUtcNow();

            if (!await _exports.FinishAsync(job.Id, Who, bytes, rows, _settings.TileExportRetention, working)
                    .ConfigureAwait(false))
            {
                // Cancelled while the file was being written: nobody will be given its address.
                DeleteFiles(state.Token);
                LogReleased(job.Id);
                return;
            }

            LogFinished(job.Id, service.QualifiedName, rows, bytes, now + _settings.TileExportRetention);
        }
        finally
        {
            // Whatever state the job ends in, the staging folder is nobody's.
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LogFileStuck(_log, staging, e.Message);
            }
        }
    }

    /// <summary>
    /// Has the reader write one staged layer into the output, and notes where it went; returns the dataset so far.
    /// </summary>
    /// <remarks>
    /// <b>The first layer makes the file and the rest are appended to it</b> for the four formats that hold every layer in
    /// one (<see cref="FeatureExportPackaging.Appends"/>); for the others each layer is a file of its own, made in
    /// <paramref name="staging"/>. <b>The reader is watched while it works</b>: a checkpoint every two seconds keeps the
    /// lease and learns of a cancel, and a cancel kills the child.
    /// </remarks>
    private async Task<string?> WriteLayerAsync(
        FeatureExportFormat format,
        FeatureExportPackaging packaging,
        string input,
        string layerName,
        int index,
        string staging,
        string? dataset,
        List<(string Path, string Entry)> written,
        Checkpoint checkpoint,
        CancellationToken working)
    {
        string output;
        bool append = false;

        if (packaging.OneDataset)
        {
            // <b>Named for what it is, in the staging folder, and moved to the `.part` when it is whole.</b> A driver
            // may choose how to create a file by its extension — GDAL's XLSX driver refused a `.part` (measured
            // 2026-09-30) — so the extension the format has is the one it is written under. A File Geodatabase is a
            // folder, zipped at the end.
            output = dataset ?? (format == FeatureExportFormat.FileGeodatabase
                ? Path.Combine(staging, "gdb", "export.gdb")
                : Path.Combine(staging, "dataset" + packaging.Suffix));

            if (format == FeatureExportFormat.FileGeodatabase && index == 0)
            {
                // GDAL makes the .gdb folder but not the one it sits in.
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            }

            append = dataset is not null;
        }
        else
        {
            output = format switch
            {
                FeatureExportFormat.Shapefile => Path.Combine(staging, "shp", layerName),
                FeatureExportFormat.Csv => Path.Combine(staging, layerName + ".csv"),
                _ => Path.Combine(staging, layerName + ".geojson"),
            };

            // GDAL makes the folder a Shapefile is written into but not the one that sits in.
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        }

        using CancellationTokenSource idle = new();
        Task pulse = checkpoint.PulseAsync(idle.Token);

        try
        {
            using JsonDocument answer = await _reader.AskAsync(
                new
                {
                    op = "export",
                    @in = input,
                    @out = output,
                    format = FeatureExportPackaging.ReaderToken(format),
                    layer = layerName,
                    append,
                },
                _settings.FeatureExportTimeout,
                working).ConfigureAwait(false);

            if (!answer.RootElement.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean())
            {
                string why = answer.RootElement.TryGetProperty("error", out JsonElement e) ? e.GetString() ?? string.Empty : string.Empty;

                throw new InvalidOperationException($"The reader could not write '{layerName}' as {FeatureExportPackaging.ReaderToken(format)}. {why}".TrimEnd());
            }
        }
        finally
        {
            await idle.CancelAsync().ConfigureAwait(false);
            await pulse.ConfigureAwait(false);
        }

        if (!packaging.OneDataset)
        {
            if (format == FeatureExportFormat.Shapefile)
            {
                foreach (string file in Directory.EnumerateFiles(output))
                {
                    written.Add((file, Path.GetFileName(file)));
                }
            }
            else
            {
                written.Add((output, Path.GetFileName(output)));
            }
        }

        return packaging.OneDataset ? output : dataset;
    }

    /// <summary>Makes the finished file at <paramref name="part"/> — a rename, a move or a zip, by the packaging.</summary>
    private static async Task AssembleAsync(
        FeatureExportFormat format,
        FeatureExportPackaging packaging,
        string part,
        string? dataset,
        List<(string Path, string Entry)> written,
        string staging,
        string service,
        Checkpoint checkpoint,
        long rows,
        CancellationToken working)
    {
        if (packaging.OneDataset && !packaging.Zipped)
        {
            // The GeoPackage, workbook or KML the reader wrote in the staging folder.
            File.Move(dataset!, part, overwrite: true);
            return;
        }

        if (!packaging.Zipped)
        {
            // One layer of CSV, GeoJSON or Esri JSON: the bare file.
            File.Move(written[0].Path, part, overwrite: true);
            return;
        }

        List<(string Path, string Entry)> entries = written;

        if (format == FeatureExportFormat.FileGeodatabase)
        {
            // <b>With the .gdb folder itself inside</b>, as ArcGIS Online's File Geodatabase download is: unzipped, it is
            // a geodatabase and not a loose set of tables. Named for the service, as Pro shows the folder's name.
            string folder = FeatureExportPackaging.DownloadName(service, format, 1);
            folder = folder[..^".gdb.zip".Length] + ".gdb";

            entries =
            [
                .. Directory.EnumerateFiles(dataset!, "*", SearchOption.AllDirectories)
                    .Select(file => (file, folder + "/" + Path.GetRelativePath(dataset!, file).Replace('\\', '/'))),
            ];
        }

        await checkpoint.ReportAsync(rows, FeatureExportPhase.Writing, working, force: true).ConfigureAwait(false);

        await Task.Run(
            () =>
            {
                using ZipArchive zip = ZipFile.Open(part, ZipArchiveMode.Create);

                foreach ((string path, string entry) in entries)
                {
                    working.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(path, entry, CompressionLevel.Optimal);
                }
            },
            working).ConfigureAwait(false);
    }

    /// <summary>
    /// Fails the export when its files have passed what it was estimated at — ADR-106 §5.4: the disk is another
    /// export's, counted for it, and a running export does not take it.
    /// </summary>
    private static async Task CheckDiskAsync(FeatureExportState state, string staging, string part, CancellationToken working)
    {
        long used = await Task.Run(
            () => Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                + (File.Exists(part) ? new FileInfo(part).Length : 0),
            working).ConfigureAwait(false);

        // A quarter and a megabyte of slack: the estimate is an upper bound from a sample, and a false failure of an
        // export that would have fitted costs its owner more than a few megabytes over the budget costs anyone.
        long allowed = state.EstimatedBytes + (state.EstimatedBytes / 4) + (1024 * 1024);

        if (used > allowed)
        {
            throw new InvalidOperationException(
                $"This export has written {TileSeedEstimate.Size(used)} and was counted against the exports budget at "
                + $"{TileSeedEstimate.Size(state.EstimatedBytes)}. It is stopped rather than take disk another export was "
                + "counted for (exceedsExportBudget). Export fewer layers, or raise Graticula:ExportBudgetMB.");
        }
    }

    /// <summary>The sentence a job fails with when the rows do not fit — the same words the request refuses with.</summary>
    internal static string RefusedForRows(string layer, FeatureExportFormat format, long cap, long left, long allowed) =>
        format == FeatureExportFormat.Excel && allowed < left
            ? $"'{layer}' has more than {FeatureExportPackaging.WorkbookSheetRows:N0} rows, which is more than one sheet of a "
              + "workbook holds. Export it as another format."
            : $"The layers of this export hold more than {cap:N0} rows in all, which is more than one export writes "
              + "(Graticula:FeatureExportMaxRows). Choose fewer layers.";

    private async Task FailAsync(JobRecord job, string why, Exception? cause, CancellationToken token)
    {
        try
        {
            await _jobs.FinishAsync(job.Id, JobStatus.Failed, null, why, token, Who).ConfigureAwait(false);
        }
        catch (Exception unwritable) when (unwritable is not OperationCanceledException)
        {
            LogFailed(job.Id, why, unwritable);
            return;
        }

        LogFailed(job.Id, why, cause);

        // <b>Audited with its first failure</b> (ADR-106 §5.7), in the name of the caller who asked, since the worker has
        // no session of its own. Best effort: a failure that could not be recorded is already in the log and on the job.
        try
        {
            (string? folder, string? name) = TileSeeder.Address(job.Detail);

            await _audit.RecordAsync(
                new AuditEvent(
                    job.Owner,
                    "feature exporter",
                    null,
                    "service.data.export.failed",
                    name is null ? null : folder is null ? name : folder + "/" + name,
                    JsonSerializer.Serialize(new { job = job.Id, why }),
                    false),
                token).ConfigureAwait(false);
        }
        catch (Exception unrecorded) when (unrecorded is not OperationCanceledException)
        {
            LogFailed(job.Id, "The failure could not be audited: " + unrecorded.Message, null);
        }
    }

    private void Report(Exception unreachable)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        switch (_claims.Failed(unreachable.Message, now))
        {
            case RepeatedFailure.Action.InFull:
                LogClaimFailed(unreachable);
                break;

            case RepeatedFailure.Action.Summarise:
                Log.ClaimStillFailing(_log, _claims.Times, _claims.For(now).TotalMinutes, unreachable.Message);
                break;

            default:
                break;
        }
    }

    private void Claimed()
    {
        int failures = _claims.Recovered(DateTimeOffset.UtcNow, out TimeSpan over);

        if (failures > 0)
        {
            Log.ClaimRecovered(_log, failures, over.TotalMinutes);
        }
    }

    private async Task Wait(CancellationToken stopping)
    {
        bool woken = await _signal.WaitAsync(JobKind.FeatureExport, _waiting, stopping).ConfigureAwait(false);

        _waiting = woken ? Idle : TimeSpan.FromTicks(Math.Min(_waiting.Ticks * 2, Patience.Ticks));
    }

    /// <summary>The detail an export's job carries: the service's address and what was asked — ADR-011 condition 4.</summary>
    internal static string Detail(PublishedService service, FeatureExportFormat format, IReadOnlyList<int> layers, long rows) =>
        JsonSerializer.Serialize(new
        {
            folder = service.Folder,
            service = service.Name,
            format = PostgresFeatureExportStore.Wire(format),
            layers,
            rows,
        });

    [LoggerMessage(
        EventId = 1400,
        Level = LogLevel.Warning,
        Message = "The feature exporter could not claim work and will try again shortly. The platform database is the "
                + "thing it asks, so this is usually that being briefly away.")]
    private partial void LogClaimFailed(Exception? exception);

    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Information,
        Message = "Feature export {Job} of {Service} started: {Format}, {Layers} layers, {Rows} rows counted.")]
    private partial void LogStarted(Guid job, string service, string format, int layers, long rows);

    [LoggerMessage(
        EventId = 1402,
        Level = LogLevel.Information,
        Message = "Feature export {Job} of {Service} is written: {Rows} rows, {Bytes} bytes. It is kept until {Expires:u}.")]
    private partial void LogFinished(Guid job, string service, long rows, long bytes, DateTimeOffset expires);

    [LoggerMessage(
        EventId = 1403,
        Level = LogLevel.Warning,
        Message = "Feature export {Job} failed and its partial files were deleted: {Why}")]
    private partial void LogFailed(Guid job, string why, Exception? exception);

    [LoggerMessage(
        EventId = 1404,
        Level = LogLevel.Information,
        Message = "Feature export {Job} stopped: it was cancelled, or its job was taken back by another worker. Its "
                + "partial files were deleted.")]
    private partial void LogReleased(Guid job);

    [LoggerMessage(
        EventId = 1405,
        Level = LogLevel.Information,
        Message = "Removed the file of feature export {Job}: it expired, or its export ended without one.")]
    private partial void LogRemoved(Guid job);

    [LoggerMessage(
        EventId = 1406,
        Level = LogLevel.Warning,
        Message = "The feature export file or folder {Path} could not be deleted and will be tried again: {Why}")]
    private static partial void LogFileStuck(ILogger logger, string path, string why);

    /// <summary>
    /// A running export's progress, written at most every two seconds — and how it learns it was cancelled.
    /// </summary>
    /// <remarks>
    /// <b>A checkpoint that is refused means the job is no longer this worker's</b> — cancelled, or its lease lost — so
    /// the run's own token is cancelled and the next await stops. While the reader child works nothing else is
    /// running on this thread, so <see cref="PulseAsync"/> keeps the checkpoint going beside it.
    /// </remarks>
    private sealed class Checkpoint(
        IFeatureExportStore exports, Guid job, string worker, long total, CancellationTokenSource stop, TimeProvider clock)
    {
        private long _rows;
        private int _phase;
        private long _last;

        /// <summary>Records progress and, at most every two seconds or when forced, writes it.</summary>
        public async Task ReportAsync(long rows, FeatureExportPhase phase, CancellationToken cancellation, bool force = false)
        {
            Volatile.Write(ref _rows, rows);
            Volatile.Write(ref _phase, (int)phase);

            long now = clock.GetTimestamp();

            long last = Volatile.Read(ref _last);

            if (!force && last != 0 && clock.GetElapsedTime(last, now) < CheckpointEvery)
            {
                return;
            }

            Volatile.Write(ref _last, now);

            int percent = (int)Math.Min(99, rows * 100 / total);

            if (!await exports.CheckpointAsync(job, worker, rows, phase, percent, cancellation).ConfigureAwait(false))
            {
                await stop.CancelAsync().ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
            }
        }

        /// <summary>Checkpoints every two seconds until told to stop; never throws.</summary>
        public async Task PulseAsync(CancellationToken done)
        {
            while (!done.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(CheckpointEvery, done).ConfigureAwait(false);
                    await ReportAsync(
                        Volatile.Read(ref _rows), (FeatureExportPhase)Volatile.Read(ref _phase), stop.Token, force: true)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // The platform database being briefly away is the claim loop's to say; the next beat tries again.
                }
            }
        }
    }

    /// <summary>A stream that keeps nothing and counts what it was given — the estimate's weighing scale.</summary>
    private sealed class ByteCounter : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => Length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Add(count);

        public override void Write(ReadOnlySpan<byte> buffer) => Add(buffer.Length);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Add(count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Add(buffer.Length);
            return ValueTask.CompletedTask;
        }

        private void Add(int count) => _length += count;

        private long _length;
    }
}

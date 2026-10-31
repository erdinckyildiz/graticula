using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Providers.PostGis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// A deleted service, web map or document set aside whole and brought back with its ids — ADR-203, Q-180.
/// </summary>
/// <remarks>
/// <para>
/// <b>The datastore first, then the catalogue, and each undone if the other fails.</b> A hosted layer's tables are moved
/// into the recycle schema before its rows are kept and its service deleted; if keeping the rows fails, the tables come
/// back, so a failure leaves the item as it was rather than half in the bin. Restoring goes the same way round: the
/// tables back first, refused if their names have been taken since, then the rows, and the tables set aside again if
/// the rows will not go back.
/// </para>
/// <para>
/// <b>An entry is deleted for good when it expires</b> — fourteen days unless <c>Graticula:RecycleDays</c> says
/// otherwise — by this service, once an hour.
/// </para>
/// </remarks>
internal sealed partial class RecycleBinService(
    IRecycleBin bin, PostGisImporter importer, IConfiguration configuration, TimeProvider time, HostSettings settings,
    ILogger<RecycleBinService> log, SiteSingletons? site = null)
    : BackgroundService
{
    /// <summary>What a relation of kind <c>file</c> is: an uploaded image moved under the state directory's recycle bin.</summary>
    private const string FileKind = "file";

    /// <summary>Where uploaded files wait — D-306: beside the state they came from, never under a datastore.</summary>
    private string FileBin => Path.Combine(settings.StatePath, "recycle");

    private static bool IsFile(RecycledRelation relation) => relation.Kind == FileKind;

    /// <summary>How long an entry waits.</summary>
    public TimeSpan Retention { get; } =
        int.TryParse(configuration["Graticula:RecycleDays"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) && days > 0
            ? TimeSpan.FromDays(days)
            : RecycleBins.DefaultRetention;

    /// <summary>The store, for listing.</summary>
    public IRecycleBin Bin => bin;

    /// <summary>
    /// Sets a service aside: each hosted table it alone publishes, then its catalogue rows. The entry, or why not, with
    /// nothing changed.
    /// </summary>
    public async Task<(Guid? Entry, string? Error)> RecycleServiceAsync(
        Guid serviceId, string title, Guid? owner, Guid? deletedBy, IReadOnlyList<(string Table, bool View)> tables,
        CancellationToken cancellationToken)
    {
        Guid provisional = Guid.NewGuid();
        string key = provisional.ToString("N")[..8];
        List<(string Schema, string Original, string Recycled, string Kind)> moved = [];

        try
        {
            // Each table its own key: a service of several layers sets several tables aside, and their waiting names
            // must not meet.
            for (int i = 0; i < tables.Count; i++)
            {
                moved.AddRange(await importer.SetAsideAsync(tables[i].Table, tables[i].View, $"{key}t{i}", cancellationToken)
                    .ConfigureAwait(false));
            }

            Guid? entry = await bin.RecycleAsync(
                "service", serviceId, serviceId, "service", title, owner, deletedBy, time.GetUtcNow() + Retention,
                [.. moved.Select(m => new RecycledRelation(m.Schema, m.Original, m.Recycled, m.Kind))], cancellationToken)
                .ConfigureAwait(false);

            if (entry is null)
            {
                await BringBackQuietlyAsync(moved).ConfigureAwait(false);
                return (null, "The service went while it was being deleted.");
            }

            return (entry, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await BringBackQuietlyAsync(moved).ConfigureAwait(false);
            return (null, $"It could not be set aside, and nothing was deleted: {e.Message}");
        }
    }

    /// <summary>
    /// Sets an image service aside — D-306: the files uploaded for it moved under the state directory's recycle bin, then
    /// its catalogue rows. A file registered in place is somebody else's and stays where it is, as a delete leaves it.
    /// </summary>
    public async Task<(Guid? Entry, string? Error)> RecycleCoverageAsync(
        Guid serviceId, string title, Guid? owner, Guid? deletedBy, IReadOnlyList<string> uploadedFiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploadedFiles);

        string key = Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(FileBin, key);
        List<RecycledRelation> moved = [];

        try
        {
            foreach (string file in uploadedFiles.Where(File.Exists))
            {
                Directory.CreateDirectory(directory);
                string waiting = Path.Combine(directory, $"{moved.Count}_{Path.GetFileName(file)}");
                File.Move(file, waiting);
                moved.Add(new RecycledRelation(FileKind, file, waiting, FileKind));
            }

            Guid? entry = await bin.RecycleAsync(
                "service", serviceId, serviceId, "image service", title, owner, deletedBy, time.GetUtcNow() + Retention, moved,
                cancellationToken).ConfigureAwait(false);

            if (entry is null)
            {
                MoveFilesBack(moved);
                return (null, "The image service went while it was being deleted.");
            }

            return (entry, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            MoveFilesBack(moved);
            return (null, $"It could not be set aside, and nothing was deleted: {e.Message}");
        }
    }

    /// <summary>Sets an item with no hosted tables aside — a web map, a document.</summary>
    public async Task<(Guid? Entry, string? Error)> RecycleRowsAsync(
        string rootTable, object rootId, Guid itemId, string kind, string title, Guid? owner, Guid? deletedBy,
        CancellationToken cancellationToken)
    {
        try
        {
            Guid? entry = await bin.RecycleAsync(
                rootTable, rootId, itemId, kind, title, owner, deletedBy, time.GetUtcNow() + Retention, [], cancellationToken)
                .ConfigureAwait(false);
            return entry is null ? (null, "It went while it was being deleted.") : (entry, null);
        }
        catch (Exception e) when (e is not OperationCanceledException and not ArgumentException)
        {
            return (null, $"It could not be set aside, and nothing was deleted: {e.Message}");
        }
    }

    /// <summary>Brings an entry back with its ids; null on success, or why not, with nothing changed.</summary>
    public async Task<string?> RestoreAsync(RecycleEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var relations = entry.Relations.Where(r => !IsFile(r)).Select(r => (r.Schema, r.Original, r.Recycled, r.Kind)).ToList();
        List<RecycledRelation> files = [.. entry.Relations.Where(IsFile)];

        if (files.FirstOrDefault(f => File.Exists(f.Original)) is { } occupied)
        {
            return $"A file has been put at {Path.GetFileName(occupied.Original)} since it was deleted; nothing was restored.";
        }

        if (files.FirstOrDefault(f => !File.Exists(f.Recycled)) is { } lost)
        {
            return $"The file it waited with, {Path.GetFileName(lost.Original)}, is no longer in the recycle bin; nothing was restored.";
        }

        if (relations.Count > 0
            && await importer.BringBackAsync(relations, cancellationToken).ConfigureAwait(false) is { } taken)
        {
            return $"A table named {taken} has been made since it was deleted. Rename it to restore; nothing was restored.";
        }

        foreach (RecycledRelation file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Original)!);
            File.Move(file.Recycled, file.Original);
        }

        string? refused = await bin.RestoreRowsAsync(entry.Id, cancellationToken).ConfigureAwait(false);

        if (refused is not null && relations.Count > 0)
        {
            await importer.SetAsideAgainAsync(relations, cancellationToken).ConfigureAwait(false);
        }

        if (refused is not null)
        {
            foreach (RecycledRelation file in files)
            {
                File.Move(file.Original, file.Recycled);
            }
        }

        return refused;
    }

    /// <summary>Deletes an entry for good: its tables dropped, its rows forgotten.</summary>
    public async Task PurgeAsync(RecycleEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Relations.Any(r => !IsFile(r)))
        {
            await importer.PurgeAsync([.. entry.Relations.Where(r => !IsFile(r)).Select(r => (r.Schema, r.Original, r.Recycled, r.Kind))],
                cancellationToken).ConfigureAwait(false);
        }

        foreach (RecycledRelation file in entry.Relations.Where(IsFile))
        {
            File.Delete(file.Recycled);
            string? directory = Path.GetDirectoryName(file.Recycled);

            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }

        await bin.RemoveAsync(entry.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // ADR-012 phase 0 item 1: once per site, so two nodes never purge one entry together.
                IEnumerable<RecycleEntry> entries = site is null || await site.LeadsAsync(SiteSingletons.RecycleBinExpiry, stoppingToken).ConfigureAwait(false)
                    ? await bin.ListAsync(stoppingToken).ConfigureAwait(false)
                    : [];

                foreach (RecycleEntry expired in entries.Where(e => e.ExpiresAt <= time.GetUtcNow()))
                {
                    await PurgeAsync(expired, stoppingToken).ConfigureAwait(false);
                    LogPurged(log, expired.Kind, expired.Title);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogPurgeFailed(log, e);
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void MoveFilesBack(List<RecycledRelation> moved)
    {
        foreach (RecycledRelation file in moved)
        {
            try
            {
                File.Move(file.Recycled, file.Original);
            }
            catch (IOException e)
            {
                LogBringBackFailed(log, file.Recycled, e);
            }
        }
    }

    private async Task BringBackQuietlyAsync(List<(string Schema, string Original, string Recycled, string Kind)> moved)
    {
        if (moved.Count == 0)
        {
            return;
        }

        try
        {
            await importer.BringBackAsync(moved, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            LogBringBackFailed(log, string.Join(", ", moved.Select(m => m.Recycled)), e);
        }
    }

    [LoggerMessage(EventId = 2354, Level = LogLevel.Information, Message = "The recycle bin deleted the {Kind} '{Title}' for good: it expired.")]
    private static partial void LogPurged(ILogger logger, string kind, string title);

    [LoggerMessage(EventId = 2355, Level = LogLevel.Warning, Message = "The recycle bin could not delete its expired entries; it will try again in an hour.")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2356, Level = LogLevel.Error, Message = "A delete failed after its tables were set aside, and they could not be moved back: {Tables} are in the recycle schema.")]
    private static partial void LogBringBackFailed(ILogger logger, string tables, Exception exception);
}

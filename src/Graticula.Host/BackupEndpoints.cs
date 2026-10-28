using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// <b>Server › Site › Backups, as an API</b> — ADR-193 §5.6: take a backup as a background run, list, download and
/// delete them, and a schedule, off until an administrator turns it on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Restore is not here, deliberately</b> (§5.5): it replaces the database this process serves, so it is
/// <c>graticula restore</c> against a stopped server.
/// </para>
/// <para>
/// <b>One backup at a time, in this process.</b> A second request while one runs is refused with a 409 rather than
/// queued; the list says one is running. A backup is a folder with a manifest, so nothing else records it.
/// </para>
/// </remarks>
internal static partial class BackupEndpoints
{
    [LoggerMessage(EventId = 2351, Level = LogLevel.Error, Message = "A backup failed and nothing was kept: {Reason}")]
    private static partial void BackupFailed(ILogger log, Exception failed, string reason);

    /// <summary>The server setting the schedule is kept under.</summary>
    public const string ScheduleSetting = "backupSchedule";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static int _running;

    /// <summary>The last run's outcome, for the list.</summary>
    private static (DateTimeOffset Started, DateTimeOffset? Finished, string? Name, string? Error)? _last;

    /// <summary>When a backup runs and how many are kept; off by default (owner decision, ADR-193 §5.6).</summary>
    /// <param name="Enabled">Whether scheduled backups are taken.</param>
    /// <param name="HourUtc">The hour of the day, UTC, a scheduled backup is taken in.</param>
    /// <param name="Keep">How many backups are kept; older ones are deleted after a scheduled one.</param>
    internal sealed record BackupSchedule(bool Enabled, int HourUtc, int Keep)
    {
        /// <summary>Off, at 02:00 UTC, keeping seven.</summary>
        public static BackupSchedule Default { get; } = new(false, 2, 7);
    }

    /// <summary>Maps the routes.</summary>
    public static void Map(WebApplication app)
    {
        app.MapGet("/admin/backups", ListAsync).Governed(SharingGovernedExtensions.ByPrivilege);
        app.MapPost("/admin/backups", TakeAsync).Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
        app.MapGet("/admin/backups/schedule", ScheduleAsync).Governed(SharingGovernedExtensions.ByPrivilege);
        app.MapPut("/admin/backups/schedule", SetScheduleAsync).Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
        app.MapGet("/admin/backups/{name}/download", DownloadAsync).Governed(SharingGovernedExtensions.ByPrivilege);
        app.MapDelete("/admin/backups/{name}", DeleteAsync).Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
    }

    /// <summary>Starts a backup unless one is running; answers whether it started.</summary>
    internal static bool Start(Backups backups, ILogger log, Func<string, Task>? after = null)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return false;
        }

        DateTimeOffset started = DateTimeOffset.UtcNow;
        _last = (started, null, null, null);

        _ = Task.Run(async () =>
        {
            try
            {
                using StringWriter said = new();
                string folder = await backups.TakeAsync(said, CancellationToken.None).ConfigureAwait(false);
                _last = (started, DateTimeOffset.UtcNow, Path.GetFileName(folder), null);
                if (after is not null)
                {
                    await after(folder).ConfigureAwait(false);
                }
            }
            catch (Exception failed) when (failed is InvalidOperationException or IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception or Npgsql.NpgsqlException)
            {
                _last = (started, DateTimeOffset.UtcNow, null, failed.Message);
                BackupFailed(log, failed, failed.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });

        return true;
    }

    private static async Task<IResult> ListAsync(HttpContext context, Backups backups, IServerSettingStore settings, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        return Results.Ok(new
        {
            location = backups.Location,
            toolsMissing = backups.ToolsMissing(),
            running = Volatile.Read(ref _running) == 1,
            last = _last is { } last
                ? new { started = last.Started, finished = last.Finished, name = last.Name, error = last.Error }
                : null,
            schedule = await ReadScheduleAsync(settings, cancellation).ConfigureAwait(false),
            backups = backups.List().Select(b => new
            {
                name = b.Name,
                taken = b.Manifest.Taken,
                version = b.Manifest.Version,
                schema = b.Manifest.Schema,
                bytes = b.Manifest.Parts.Sum(p => p.Bytes),
                keyFingerprint = b.Manifest.KeyFingerprint,
                roles = Backups.HasRoles(b.Manifest),
            }),
            note = "A backup on the server's own disk is not a disaster recovery until it is copied elsewhere — download it, "
                + "or point Graticula:BackupPath at storage that is. Restore is `graticula restore <name>` with the server stopped. "
                + "The secret key is not in a backup; keep it apart.",
        });
    }

    private static async Task<IResult> TakeAsync(HttpContext context, Backups backups, IAuditLog audit, ILoggerFactory loggers, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        if (!Start(backups, loggers.CreateLogger("backup")))
        {
            return Results.Json(new { error = new { code = 409, message = "A backup is already running; the list says when it finishes.", details = Array.Empty<string>() } },
                statusCode: StatusCodes.Status409Conflict);
        }

        await AdminEndpoints.AuditAsync(context, audit, "backup.take", null, AdminEndpoints.Detail(new { location = backups.Location }),
            succeeded: true, cancellation).ConfigureAwait(false);
        return Results.Accepted("/admin/backups", new { running = true, note = "Started. It is listed here once its manifest is written." });
    }

    private static async Task<IResult> DownloadAsync(HttpContext context, Backups backups, IAuditLog audit, string name, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        if (Folder(backups, name) is not { } folder)
        {
            return NotFound(name);
        }

        // <b>`check=1` asks, and sends nothing</b> — the console's download asks first, as its other downloads do, so a
        // refusal is a sentence on the page rather than a broken file in the browser's list. The size is the parts'; the
        // tar adds a header per file.
        if (context.Request.Query["check"] == "1")
        {
            return Results.Ok(new { fileName = $"{name}.tar", bytes = Backups.ReadManifest(folder)!.Parts.Sum(p => p.Bytes) });
        }

        await AdminEndpoints.AuditAsync(context, audit, "backup.download", name, AdminEndpoints.Detail(new { name }),
            succeeded: true, cancellation).ConfigureAwait(false);

        context.Response.ContentType = "application/x-tar";
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}.tar\"";
        await System.Formats.Tar.TarFile.CreateFromDirectoryAsync(folder, context.Response.Body, includeBaseDirectory: true, cancellation)
            .ConfigureAwait(false);
        return Results.Empty;
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, Backups backups, IAuditLog audit, string name, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        if (Folder(backups, name) is not { } folder)
        {
            return NotFound(name);
        }

        Directory.Delete(folder, recursive: true);
        await AdminEndpoints.AuditAsync(context, audit, "backup.delete", name, AdminEndpoints.Detail(new { name }),
            succeeded: true, cancellation).ConfigureAwait(false);
        return Results.Ok(new { deleted = name });
    }

    private static async Task<IResult> ScheduleAsync(HttpContext context, IServerSettingStore settings, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        return Results.Ok(await ReadScheduleAsync(settings, cancellation).ConfigureAwait(false));
    }

    private static async Task<IResult> SetScheduleAsync(
        HttpContext context, BackupSchedule request, IServerSettingStore settings, IAuditLog audit, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        if (request.HourUtc is < 0 or > 23 || request.Keep is < 1 or > 365)
        {
            return Results.Json(new { error = new { code = 400, message = "hourUtc is 0 to 23 and keep is 1 to 365.", details = Array.Empty<string>() } },
                statusCode: StatusCodes.Status400BadRequest);
        }

        Guid? who = context.Features.Get<RequestPrincipal>()?.Principal.Id;
        await settings.WriteAsync(ScheduleSetting, JsonSerializer.Serialize(request, Json), who, cancellation).ConfigureAwait(false);
        await AdminEndpoints.AuditAsync(context, audit, "backup.schedule", null, AdminEndpoints.Detail(request),
            succeeded: true, cancellation).ConfigureAwait(false);
        return Results.Ok(request);
    }

    /// <summary>The stored schedule, or the default when none is stored.</summary>
    internal static async Task<BackupSchedule> ReadScheduleAsync(IServerSettingStore settings, CancellationToken cancellation)
    {
        StoredSetting? stored = await settings.ReadAsync(ScheduleSetting, cancellation).ConfigureAwait(false);
        try
        {
            return stored is null ? BackupSchedule.Default : JsonSerializer.Deserialize<BackupSchedule>(stored.Value, Json) ?? BackupSchedule.Default;
        }
        catch (JsonException)
        {
            return BackupSchedule.Default;
        }
    }

    /// <summary>A backup's folder by its name, never a path outside the backup location.</summary>
    private static string? Folder(Backups backups, string name)
    {
        if (!name.StartsWith(Backups.Prefix, StringComparison.Ordinal) || name.IndexOfAny(['/', '\\']) >= 0 || name.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        string folder = Path.Combine(backups.Location, name);
        return Directory.Exists(folder) && Backups.ReadManifest(folder) is not null ? folder : null;
    }

    private static IResult NotFound(string name) =>
        Results.Json(new { error = new { code = 404, message = $"No finished backup '{name}'.", details = Array.Empty<string>() } },
            statusCode: StatusCodes.Status404NotFound);
}

/// <summary>
/// Takes the scheduled backup once a day in its hour, and keeps the number the schedule says — ADR-193 §5.6.
/// </summary>
internal sealed partial class BackupScheduler(Backups backups, IServerSettingStore settings, ILogger<BackupScheduler> log) : BackgroundService
{
    [LoggerMessage(EventId = 2352, Level = LogLevel.Warning, Message = "The backup schedule could not be read or applied: {Reason}")]
    private static partial void ScheduleFailed(ILogger log, Exception failed, string reason);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMinutes(10));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                BackupEndpoints.BackupSchedule schedule = await BackupEndpoints.ReadScheduleAsync(settings, stoppingToken).ConfigureAwait(false);
                DateTimeOffset now = DateTimeOffset.UtcNow;

                if (!schedule.Enabled || now.Hour != schedule.HourUtc
                    || backups.List().Any(b => b.Manifest.Taken.UtcDateTime.Date == now.UtcDateTime.Date))
                {
                    continue;
                }

                BackupEndpoints.Start(backups, log, _ =>
                {
                    backups.Prune(schedule.Keep);
                    return Task.CompletedTask;
                });
            }
            catch (Exception failed) when (failed is Npgsql.NpgsqlException or IOException)
            {
                ScheduleFailed(log, failed, failed.Message);
            }
        }
    }
}

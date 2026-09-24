using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;

namespace Graticula.Host;

/// <summary>
/// The map ground an operator chose for the whole server: tile services drawn beneath everybody's data —
/// Q-110, ADR-086.
/// </summary>
/// <remarks>
/// <para>
/// <b>A list of this server's own VectorTileServer services, bottom first.</b> The owner's answer to Q-110
/// was that the operator chooses and OpenStreetMap's tiles stay when nobody has: a ground of districts
/// <em>and</em> roads is two services, so one choice would mean picking half a map.
/// </para>
/// <para>
/// <b>Read from the store on every ask, not held.</b> It is read once per page load, by
/// <c>portals/self</c>, where the page size is read once per query; a thirty-second copy would buy nothing
/// and would make a second node draw the old ground after Save.
/// </para>
/// </remarks>
internal sealed class ServerGround
{
    /// <summary>The setting's name in the store.</summary>
    public const string Name = "map_ground";

    /// <summary>The most services a ground may be. Each one is a tile request per tile on screen.</summary>
    public const int MostServices = 8;

    private readonly IServerSettingStore _store;
    private readonly CatalogFallback _catalog;

    /// <summary>Creates the reader.</summary>
    /// <param name="store">Where the choice is kept.</param>
    /// <param name="catalog">The services a choice has to name.</param>
    public ServerGround(IServerSettingStore store, CatalogFallback catalog)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>The chosen services, qualified names bottom first, and when they were chosen.</summary>
    /// <param name="Services">The services; empty when nobody chose.</param>
    /// <param name="ChangedAt">When it was set, or null.</param>
    internal sealed record Reading(IReadOnlyList<string> Services, DateTimeOffset? ChangedAt);

    /// <summary>What is chosen. An unreadable store reads as nothing chosen: a ground is not worth an error.</summary>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The reading.</returns>
    public async Task<Reading> ReadAsync(CancellationToken cancellation)
    {
        StoredSetting? stored;

        try
        {
            stored = await _store.ReadAsync(Name, cancellation).ConfigureAwait(false);
        }
        catch (System.Data.Common.DbException)
        {
            return new Reading([], null);
        }

        return new Reading(Parse(stored?.Value), stored?.ChangedAt);
    }

    /// <summary>
    /// Why these services cannot be the ground, or null when they can.
    /// </summary>
    /// <param name="services">The names asked for, as <c>folder/name</c> or <c>name</c>.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The refusal and the names in the catalogue's own spelling.</returns>
    /// <remarks>
    /// <b>Each name has to be a service that serves tiles today.</b> A ground naming a service that does not
    /// exist is stored silently and draws nothing for everybody, which is the failure Q-110's first answer —
    /// OpenStreetMap's tiles by default — was chosen to avoid. Sharing is not checked: a ground only some
    /// callers may read is a choice an operator can make, and the screen says who will not see it.
    /// </remarks>
    public async Task<(string? Refusal, IReadOnlyList<string> Names)> CheckAsync(
        IReadOnlyList<string> services, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Count > MostServices)
        {
            return ($"A ground is at most {MostServices} services; each one is a request for every tile on screen.",
                []);
        }

        List<string> names = [];

        foreach (string asked in services)
        {
            string trimmed = (asked ?? string.Empty).Trim().Trim('/');
            int slash = trimmed.IndexOf('/', StringComparison.Ordinal);
            string? folder = slash < 0 ? null : trimmed[..slash];
            string name = slash < 0 ? trimmed : trimmed[(slash + 1)..];

            if (name.Length == 0 || name.Contains('/', StringComparison.Ordinal))
            {
                return ($"'{asked}' is not a service name. Write it as folder/name, as in hosted/tr_il.", []);
            }

            CatalogAnswer answer = await _catalog.FindServiceAsync(folder, name, cancellation).ConfigureAwait(false);

            // A name with no folder is looked for in the hosted folder too, as the service URLs do — an
            // imported ground is hosted, and "tr_il" is how an operator will write it.
            if (answer.Service is null && folder is null)
            {
                answer = await _catalog
                    .FindServiceAsync(Graticula.Api.ArcGis.FeatureServerMetadataWriter.HostedFolder, name, cancellation)
                    .ConfigureAwait(false);
            }

            if (answer.Service is not { } service)
            {
                return ($"There is no service '{trimmed}' on this server.", []);
            }

            if (!service.Limits.AllowsTiles(dataSupportsIt: true) || service.Layers.Count == 0)
            {
                return ($"'{trimmed}' does not serve vector tiles, so it cannot be drawn as a ground.", []);
            }

            if (!names.Contains(service.QualifiedName, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(service.QualifiedName);
            }
        }

        return (null, names);
    }

    /// <summary>Stores the ground, or removes it when <paramref name="names"/> is empty.</summary>
    /// <param name="names">Names that passed <see cref="CheckAsync"/>.</param>
    /// <param name="changedBy">Who changed it.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>What was stored before.</returns>
    public async Task<IReadOnlyList<string>> SetAsync(
        IReadOnlyList<string> names, Guid? changedBy, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(names);

        StoredSetting? before = await _store
            .WriteAsync(Name, names.Count == 0 ? null : JsonSerializer.Serialize(names), changedBy, cancellation)
            .ConfigureAwait(false);

        return Parse(before?.Value);
    }

    private static string[] Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(value)?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()
                ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// The hosting server's admin documents ArcGIS Pro reads before it shares a map image layer that references registered
/// data — ADR-299.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each answer is the shape a federated ArcGIS Server 12.0 gave to the same request</b> (esriqa.aecon.com, the owner's
/// test Enterprise, 2026-11-10), trimmed to what is true here. The list of requests is not guessed either: it is what Pro
/// 3.7 asked dev.graticula.com, in order, once the SOAP door answered — the root, the directories, the service
/// properties, the MapServer type and its extensions, the machines, the licences, the data config, four data-item
/// searches and <c>canCreateService</c>.
/// </para>
/// <para>
/// <b>The registered databases are the point.</b> Pro asks <c>data/findItems?parentPath=/enterpriseDatabases&amp;types=egdb</c>
/// and compares each item's <c>connectionString</c> with the connection its layers were added through; a layer whose
/// database is on the list is shared by reference, and one that is not gets analyzer 00231. So every registered
/// PostgreSQL source is listed here in the connection-string form Pro writes — <c>SERVER</c>, <c>INSTANCE</c>,
/// <c>DB_CONNECTION_PROPERTIES</c>, <c>DATABASE</c>, <c>USER</c> — <b>and never with its password</b>; Esri's carries an
/// encrypted one, which this server has no reason to hand to a client.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    /// <summary>Answers the admin documents of the reference-publishing exchange, or null for any other path.</summary>
    private static async Task<IResult?> ProReferenceAdminAsync(HttpContext context, string what, CancellationToken cancellation)
    {
        string trimmed = what.Trim('/');

        switch (trimmed)
        {
            case "services/properties":
                return Results.Ok(new { preferSharedInstances = true });

            case "services/types/providers/ArcObjects11/MapServer":
            case "services/types/providers/ArcObjects11/MapServer/extensions":
                return Results.Ok(MapServerType);

            case var machine when machine.StartsWith("machines/", StringComparison.Ordinal):
                return Results.Ok(new
                {
                    machineName = context.Request.Host.Host.ToUpperInvariant(),
                    platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    ports = new Dictionary<string, int> { [context.Request.IsHttps ? "HTTPS" : "HTTP"] = context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80) },
                    adminURL = $"{context.Request.Scheme}://{context.Request.Host}/arcgis/admin",
                    configuredState = "STARTED",
                    synchronize = false,
                    underMaintenance = false,
                });

            case "machines":
                return Results.Ok(new
                {
                    machines = new[]
                    {
                        new
                        {
                            machineName = context.Request.Host.Host.ToUpperInvariant(),
                            adminURL = $"{context.Request.Scheme}://{context.Request.Host}/arcgis/admin",
                            synchronize = false,
                            underMaintenance = false,
                        },
                    },
                    DatastoreMachines = Array.Empty<object>(),
                    Protocol = context.Request.IsHttps ? "HTTPS" : "HTTP",
                });

            case "system/licenses":
                // What the server can do, in the vocabulary Pro reads for analyzer 00147 ("the GIS Server role is not
                // licensed"): a federated 12.0 server lists its roles under `features`, each `isValid`. No expiry and no
                // key: nothing here is licensed by anyone, and nothing pretends to carry a licence file.
                return Results.Ok(new
                {
                    edition = new { name = "svrstandard", version = ServerVersion, canExpire = false, featureName = "esriServerLicenseStandard" },
                    level = new { name = "svrenterprise", version = ServerVersion, canExpire = false },
                    extensions = Array.Empty<object>(),
                    features = new object[]
                    {
                        new { name = "esriServerLicenseStandard", displayName = "GIS Server Standard", version = ServerVersion, canExpire = false, isValid = true },
                        new { name = "esriServerEnterprise", displayName = "Server Enterprise", version = ServerVersion, canExpire = false, isValid = true },
                    },
                });

            case "services/types/extensions":
                return Results.Ok(new { });

            case "data/config":
                return Results.Ok(new { });

            case "data/items":
                return Results.Ok(new { rootItems = DataRootItems });

            case "data/items/enterpriseDatabases":
                return Results.Ok(new
                {
                    path = "/enterpriseDatabases",
                    type = "datadir",
                    childItems = await RegisteredDatabaseItemsAsync(context, cancellation).ConfigureAwait(false),
                });

            case "data/findItems":
                string parent = context.Request.Query["parentPath"].ToString();
                return Results.Ok(new
                {
                    items = string.Equals(parent, "/enterpriseDatabases", StringComparison.OrdinalIgnoreCase)
                        ? await RegisteredDatabaseItemsAsync(context, cancellation).ConfigureAwait(false)
                        : [],
                });

            case "data/validateDataItem":
                return Results.Ok(new { status = "success" });

            case "services/canCreateService":
                // A map image layer is published here as a FeatureServer and a MapServer over the same layers (ADR-299 §5d).
                return Results.Ok(new { status = "success" });

            default:
                return null;
        }
    }

    /// <summary>Each registered PostgreSQL database as an ArcGIS Server data item.</summary>
    private static async Task<List<object>> RegisteredDatabaseItemsAsync(HttpContext context, CancellationToken cancellation)
    {
        IAdminCatalog catalog = context.RequestServices.GetRequiredService<IAdminCatalog>();
        IReadOnlyDictionary<Guid, string> publishers = catalog is Graticula.Platform.Postgres.PostgresAdminCatalog postgres
            ? await postgres.PublisherAddressesAsync(cancellation).ConfigureAwait(false)
            : new Dictionary<Guid, string>();
        List<object> items = [];

        foreach (RegisteredDataSource source in await catalog.ListDataSourcesAsync(cancellation).ConfigureAwait(false))
        {
            if (!string.Equals(source.Kind, DataSourceKinds.PostGis, StringComparison.Ordinal))
            {
                continue;
            }

            string? stored;

            try
            {
                stored = await catalog.ConnectionStringOfAsync(source.Id, cancellation).ConfigureAwait(false);
            }
            catch (CryptographicException)
            {
                continue; // Sealed with another key: it cannot be matched, so it is not offered.
            }

            if (stored is not { Length: > 0 } || ArcGisConnection.FromNpgsql(stored) is not { } found)
            {
                continue;
            }

            ArcGisConnection connection = found;

            // ADR-299 §5c: Pro compares with the connection it holds, so a database its publishers reach at another
            // address is listed at that address — ArcGIS's publisher connection. The server keeps its own.
            if (publishers.TryGetValue(source.Id, out string? publisher))
            {
                connection = connection.At(publisher);
            }

            items.Add(new
            {
                path = $"/enterpriseDatabases/{source.Name}",
                type = "egdb",
                id = source.Id.ToString(),
                childItems = Array.Empty<object>(),
                totalRefCount = source.LayerCount,
                info = new
                {
                    dataStoreConnectionType = "shared",
                    isManaged = false,
                    connectionString = connection.ToArcGis(),
                },
            });
        }

        return items;
    }

    private static string ServerVersion => Graticula.Api.ArcGis.FeatureServerMetadataWriter.FullVersion;

    private static readonly string[] DataRootItems = ["/enterpriseDatabases", "/fileShares", "/cloudStores", "/nosqlDatabases"];

    /// <summary>The MapServer service type with the one extension this server has: feature access.</summary>
    private static readonly object MapServerType = new
    {
        Name = "MapServer",
        DisplayName = "Map Service",
        Description = "Map service",
        MaxUploadFileSize = -1,
        AllowedUploadFileTypes = string.Empty,
        Properties = new { },
        Info = new { DefaultWebCapabilities = "Map,Query,Data", AllWebCapabilities = "Map,Query,Data", SupportsSharedInstances = "true" },
        Resources = new { },
        ExtensionTypes = new object[]
        {
            new
            {
                Name = "FeatureServer",
                DisplayName = "Feature Access",
                ParentServiceType = "MapServer",
                Description = "Feature access",
                MaxUploadFileSize = 2000,
                AllowedUploadFileTypes = string.Empty,
                Properties = new { },
                Info = new
                {
                    DefaultWebCapabilities = "Query",
                    AllWebCapabilities = "Query,Create,Update,Delete,Uploads,Extract,Append",
                    SupportsSharedInstances = "true",
                    SupportsMSD = "true",
                },
                Resources = new { },
            },
        },
    };
}

/// <summary>
/// A database connection in the form ArcGIS writes it — <c>SERVER=…;INSTANCE=sde:postgresql:host,port;DBCLIENT=…;
/// DB_CONNECTION_PROPERTIES=host,port;DATABASE=…;USER=…</c> — read from Pro's side and written from this server's (ADR-299).
/// </summary>
/// <param name="Client">The database client, lower case: <c>postgresql</c>.</param>
/// <param name="Host">The host as written.</param>
/// <param name="Port">The port, or null for the client's default.</param>
/// <param name="Database">The database.</param>
/// <param name="User">The user, or null.</param>
internal sealed record ArcGisConnection(string Client, string Host, int? Port, string Database, string? User)
{
    /// <summary>The key two connections share when they reach one database: what <see cref="SourceQuiesce.DatabaseKey"/> says.</summary>
    public string DatabaseKey => SourceQuiesce.DatabaseKey(
        $"Host={Host};Port={(Port ?? 5432).ToString(CultureInfo.InvariantCulture)};Database={Database}");

    /// <summary>Reads Pro's form; null when it names no PostgreSQL database.</summary>
    public static ArcGisConnection? Parse(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        Dictionary<string, string> parts = new(StringComparer.OrdinalIgnoreCase);

        foreach (string part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = part.IndexOf('=', StringComparison.Ordinal);
            if (at > 0)
            {
                parts[part[..at].Trim()] = part[(at + 1)..].Trim().Trim('"');
            }
        }

        string client = parts.GetValueOrDefault("DBCLIENT", string.Empty).ToLowerInvariant();
        string instance = parts.GetValueOrDefault("INSTANCE", string.Empty);

        if (client.Length == 0 && instance.StartsWith("sde:postgresql:", StringComparison.OrdinalIgnoreCase))
        {
            client = "postgresql";
        }

        if (client != "postgresql" || !parts.TryGetValue("DATABASE", out string? database) || database.Length == 0)
        {
            return null;
        }

        // DB_CONNECTION_PROPERTIES is what the client actually dialled; SERVER and the INSTANCE tail are the same thing
        // written twice more. "host" or "host,port".
        string address = parts.GetValueOrDefault("DB_CONNECTION_PROPERTIES")
            ?? (instance.StartsWith("sde:postgresql:", StringComparison.OrdinalIgnoreCase) ? instance["sde:postgresql:".Length..] : null)
            ?? parts.GetValueOrDefault("SERVER", string.Empty);

        string[] hostPort = address.Split(',', 2, StringSplitOptions.TrimEntries);
        int? port = hostPort.Length == 2 && int.TryParse(hostPort[1], NumberStyles.None, CultureInfo.InvariantCulture, out int p) ? p : null;

        return hostPort[0].Length == 0
            ? null
            : new ArcGisConnection(client, hostPort[0], port, database, parts.GetValueOrDefault("USER"));
    }

    /// <summary>Reads a registered source's Npgsql connection string; null when it is not one.</summary>
    public static ArcGisConnection? FromNpgsql(string stored)
    {
        if (SqlLocators.Is(stored))
        {
            return null;
        }

        string host = string.Empty;
        int? port = null;
        string database = string.Empty;
        string? user = null;

        foreach (string part in stored.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = part.IndexOf('=', StringComparison.Ordinal);
            if (at <= 0)
            {
                continue;
            }

            string name = part[..at].Trim().ToLowerInvariant();
            string value = part[(at + 1)..].Trim();

            switch (name)
            {
                case "host" or "server":
                    host = value;
                    break;
                case "port" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int p):
                    port = p;
                    break;
                case "database" or "initial catalog":
                    database = value;
                    break;
                case "username" or "user id" or "userid" or "user name" or "uid":
                    user = value;
                    break;
            }
        }

        return host.Length == 0 || database.Length == 0 ? null : new ArcGisConnection("postgresql", host, port, database, user);
    }

    /// <summary>The same database and user at another address — <c>host</c> or <c>host,port</c>.</summary>
    public ArcGisConnection At(string address)
    {
        string[] hostPort = address.Split(',', 2, StringSplitOptions.TrimEntries);
        int? port = hostPort.Length == 2 && int.TryParse(hostPort[1], NumberStyles.None, CultureInfo.InvariantCulture, out int p) ? p : null;
        return this with { Host = hostPort[0], Port = port };
    }

    /// <summary>Written in ArcGIS's form, without a password.</summary>
    public string ToArcGis()
    {
        string address = Port is { } p && p != 5432 ? $"{Host},{p.ToString(CultureInfo.InvariantCulture)}" : Host;
        return $"SERVER={address};INSTANCE=sde:{Client}:{address};DBCLIENT={Client};DB_CONNECTION_PROPERTIES={address};"
            + $"DATABASE={Database};" + (User is { Length: > 0 } u ? $"USER={u};" : string.Empty) + "AUTHENTICATION_MODE=DBMS";
    }
}

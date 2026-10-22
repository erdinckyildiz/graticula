using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Postgres;
using Graticula.Platform.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Graticula.Host.Tools;

/// <summary>
/// <c>graticula tools manifest export|apply</c> — the catalogue as a file, and back (ADR-183).
/// </summary>
/// <remarks>
/// <para>
/// <b>The work is <see cref="CatalogManifest"/>'s; this reads the command line and prints.</b> Where a data source
/// points is said the way the console says it (<see cref="AdminEndpoints.Summarise"/>), so the manifest and the
/// screen name a source alike.
/// </para>
/// <para>
/// <b>Credentials come from a file, never from an argument</b>, for the reason <c>admincreator</c> gives for its
/// password: an argument lands in the shell's history and in the process table.
/// </para>
/// </remarks>
internal static class CatalogManifestTool
{
    /// <summary>Runs the command.</summary>
    /// <param name="services">The built application's services.</param>
    /// <param name="args">The command line.</param>
    /// <param name="cancellationToken">The caller's.</param>
    /// <returns>Zero on success; one when the apply was refused or the command was wrong.</returns>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);

        NpgsqlDataSource store = services.GetRequiredService<NpgsqlDataSource>();
        SecretProtector secrets = services.GetRequiredService<SecretProtector>();

        try
        {
            store = await OrganisationStoreAsync(store, After(args, "--organization"), cancellationToken).ConfigureAwait(false);

            return args switch
            {
                ["tools", "manifest", "export", ..] => await ExportAsync(store, secrets, args, cancellationToken).ConfigureAwait(false),
                ["tools", "manifest", "apply", string file, ..] when !file.StartsWith("--", StringComparison.Ordinal)
                    => await ApplyAsync(store, secrets, file, args, cancellationToken).ConfigureAwait(false),
                _ => Wrong(),
            };
        }
        catch (ManifestException e)
        {
            await Console.Error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 1;
        }
        catch (PostgresException e)
        {
            // The one transaction was rolled back, so this is said as plainly as a refusal.
            await Console.Error.WriteLineAsync(
                $"The store refused a write, and nothing was written: {e.MessageText}"
                + (e.ConstraintName is { } c ? $" (constraint {c})" : string.Empty)).ConfigureAwait(false);
            return 1;
        }
    }

    private static int Wrong()
    {
        Console.Error.WriteLine(
            "Usage: graticula tools manifest export [--out <file>] [--organization <key>]\n"
            + "       graticula tools manifest apply <file> [--credentials <file>] [--apply] [--organization <key>]");
        return 1;
    }

    /// <summary>
    /// The store as one organisation sees it — ADR-204 §5e item 14. A manifest names services by folder and name, which
    /// are an organisation's own; read across every organisation, two `hosted/Parcels` were one key and the apply
    /// updated whichever survived. With several organisations the tool needs to be told which.
    /// </summary>
    private static async Task<NpgsqlDataSource> OrganisationStoreAsync(
        NpgsqlDataSource store, string? key, CancellationToken cancellationToken)
    {
        List<(Guid Id, string Key)> organisations = [];

        try
        {
            await using NpgsqlCommand command = store.CreateCommand("select id, key from tenant order by key");
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                organisations.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }
        catch (PostgresException missing) when (missing.SqlState == "42P01")
        {
            // A store from before ADR-204 holds one organisation.
        }

        if (organisations.Count <= 1)
        {
            return store;
        }

        if (key is null)
        {
            throw new ManifestException(
                "This server holds several organizations, and a manifest is one organization's catalogue: say which "
                + "with --organization <key> (" + string.Join(", ", organisations.Select(o => o.Key)) + ").");
        }

        (Guid Id, string Key) chosen =
            organisations.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));

        if (chosen.Key is null)
        {
            throw new ManifestException($"There is no organization '{key}' on this server.");
        }

        Graticula.Platform.Tenancy.TenantScope.SetCount(organisations.Count);
        return TenantDataSources.For(store, chosen.Id);
    }

    private static string? After(string[] args, string flag)
    {
        int at = Array.IndexOf(args, flag);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static async Task<int> ExportAsync(
        NpgsqlDataSource store, SecretProtector secrets, string[] args, CancellationToken cancellationToken)
    {
        string text = CatalogManifest.Write(
            await CatalogManifest.ExportAsync(store, secrets, AdminEndpoints.Summarise, cancellationToken).ConfigureAwait(false));

        if (After(args, "--out") is { } path)
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            Console.Error.WriteLine($"Wrote {path}. It names every data source and carries no credential.");
        }
        else
        {
            Console.Write(text);
        }

        return 0;
    }

    private static async Task<int> ApplyAsync(
        NpgsqlDataSource store, SecretProtector secrets, string file, string[] args, CancellationToken cancellationToken)
    {
        JsonObject manifest;

        try
        {
            manifest = JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)) as JsonObject
                ?? throw new ManifestException($"{file} is not a JSON object.");
        }
        catch (JsonException e)
        {
            throw new ManifestException($"{file} is not JSON: {e.Message}", e);
        }

        Dictionary<string, string> credentials = new(StringComparer.Ordinal);

        if (After(args, "--credentials") is { } path)
        {
            try
            {
                credentials = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) ?? credentials;
            }
            catch (JsonException e)
            {
                throw new ManifestException(
                    $"{path} is not a JSON object of data source names to connection strings: {e.Message}", e);
            }
        }

        bool apply = Array.IndexOf(args, "--apply") >= 0;

        ManifestPlan plan = await CatalogManifest
            .ApplyAsync(store, secrets, AdminEndpoints.Summarise, manifest, credentials, apply, cancellationToken)
            .ConfigureAwait(false);

        foreach (string line in plan.Lines)
        {
            Console.WriteLine(line);
        }

        // <b>The refusals last, and the exit code follows them</b> — SymbologyMigrator's reason: a plan whose last
        // line is a success is the line somebody reads.
        if (plan.Refusals.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Refused, and nothing was written ({plan.Refusals.Count}):");

            foreach (string why in plan.Refusals)
            {
                Console.WriteLine($"  {why}");
            }

            return 1;
        }

        Console.WriteLine();
        Console.WriteLine(plan.Applied
            ? "Applied, in one transaction."
            : "Nothing was written. Add --apply to write this plan.");

        return 0;
    }
}

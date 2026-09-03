using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Platform.Admin;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host.Tools;

/// <summary>
/// Rewrites every layer's symbology in the canonical vocabulary ADR-052 chose.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-052](../../../docs/adr/ADR-052-the-canonical-symbology-document-is-cim.md) §3.6.</b>
/// A document stored before 2026-09-03 is a MapLibre style, and every face reads it by
/// converting on the fly. That is correct and it is slower and it means a deployment carries
/// two shapes indefinitely, so there is a command that ends it.
/// </para>
/// <para>
/// <b>It is not run at startup, and that is the decision rather than an omission.</b> A
/// migration that runs itself is one nobody can decline, cannot rehearse, and discovers its
/// failures in production at the least convenient moment. This one says what it would do,
/// changes nothing until it is told to, and reports every layer by name.
/// </para>
/// <para>
/// <b>A layer it cannot convert is named and left alone.</b> Stopping at the first failure
/// would leave a store half migrated with no record of which half, which is worse than not
/// migrating: the two shapes at least both work.
/// </para>
/// </remarks>
internal static class SymbologyMigrator
{
    /// <summary>
    /// Reports or performs the rewrite.
    /// </summary>
    /// <param name="services">The built application's services.</param>
    /// <param name="args">The command line, for <c>--apply</c>.</param>
    /// <param name="cancellationToken">The caller's.</param>
    /// <returns>Zero when nothing failed.</returns>
    public static async Task<int> RunAsync(
        IServiceProvider services, string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);

        bool apply = Array.IndexOf(args, "--apply") >= 0;

        using IServiceScope scope = services.CreateScope();

        IAdminCatalog catalog = scope.ServiceProvider.GetRequiredService<IAdminCatalog>();

        IReadOnlyList<AdminLayer> layers =
            await catalog.ListLayersAsync(cancellationToken).ConfigureAwait(false);

        List<string> converted = [];
        List<string> already = [];
        List<string> refused = [];

        foreach (AdminLayer listed in layers)
        {
            if (await catalog.FindLayerForSymbologyAsync(listed.Name, cancellationToken)
                    .ConfigureAwait(false)
                is not { Symbology: { Length: > 0 } stored } found)
            {
                continue;
            }

            JsonObject? body;

            try
            {
                body = JsonNode.Parse(stored) as JsonObject;
            }
            catch (System.Text.Json.JsonException why)
            {
                refused.Add($"{listed.Name}: the stored document is not JSON — {why.Message}");
                continue;
            }

            if (body is null)
            {
                refused.Add($"{listed.Name}: the stored document is not an object.");
                continue;
            }

            if (Cim.IsRenderer(body))
            {
                already.Add(listed.Name);
                continue;
            }

            try
            {
                CimWrite rewritten = CimStyle.FromMapLibre(body, found.Geometry);

                if (apply)
                {
                    await catalog
                        .SetSymbologyAsync(
                            listed.Name,
                            rewritten.Renderer.ToJsonString(),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                converted.Add(
                    rewritten.Losses.Count == 0
                        ? listed.Name
                        : $"{listed.Name} ({rewritten.Losses.Count} thing(s) the CIM document "
                            + $"cannot carry: {string.Join(" ", rewritten.Losses)})");
            }
            catch (SymbologyException why)
            {
                refused.Add($"{listed.Name}: {why.Message}");
            }
        }

        Console.WriteLine(
            apply
                ? $"Rewrote {converted.Count} layer(s) as CIM."
                : $"Would rewrite {converted.Count} layer(s) as CIM. Nothing was changed — add "
                    + "--apply.");

        foreach (string one in converted)
        {
            Console.WriteLine($"  {one}");
        }

        if (already.Count > 0)
        {
            Console.WriteLine($"{already.Count} layer(s) already stored CIM.");
        }

        // <b>The refusals last and the exit code follows them.</b> A migration that printed a
        // success line above a list of failures is one somebody reads the first line of.
        foreach (string one in refused)
        {
            Console.Error.WriteLine($"  {one}");
        }

        if (refused.Count > 0)
        {
            Console.Error.WriteLine(
                $"{refused.Count} layer(s) were left as they are. They still serve: every face "
                + "converts a MapLibre document on read (ADR-052 §3.6). Fix the document, or "
                + "PUT a new one through /admin/layers/{name}/symbology.");
        }

        return refused.Count == 0 ? 0 : 1;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Features;
using Graticula.Formats;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Providers.PostGis;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// The append of a file into a registered layer — ADR-310, the half of ADR-105 that said <i>hosted only</i>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through <c>applyEdits</c>, not through the importer.</b> The importer writes the datastore's own tables, with the
/// columns and the geometry column it made; a registered table is somebody else's, with whatever columns, key and
/// geometry column its owner gave it, in an engine this server may not even share with the datastore. What every engine
/// already has is the layer's edit writer, so the file's features become the <c>adds</c> of one batch and go through
/// <see cref="Program.PrepareEditAsync"/> — the privileges, the capability ceiling, the domains, editor tracking, the edit
/// ceiling — and the layer's writer, and the batch is all or nothing as an <c>applyEdits</c> with
/// <c>rollbackOnFailure</c> is.
/// </para>
/// <para>
/// <b>What follows from that is what is refused.</b> <c>truncateExisting</c> and <c>upsert</c> are not offered on a
/// registered layer (the table is the database owner's to empty, and an update is an edit of named features); a layer
/// over a file or a source that is read only is refused by the writer's own refusal; and a file with more features than
/// the service accepts in one edit is refused rather than cut, as an <c>applyEdits</c> is.
/// </para>
/// </remarks>
internal static partial class ArcGisAppendEndpoints
{
    private static async Task<Written?> AppendRegisteredAsync(
        HttpContext context, Target target, ImportedDataset dataset, ServiceContexts contexts, LayerConnections connections,
        IAuditLog audit, CancellationToken cancellation)
    {
        PublishedLayer layer = target.Layer;
        (_, LayerDescription described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        if (described.Writable == false)
        {
            await RefuseAsync(context, 400,
                $"'{layer.Definition.Name}' is a registered layer whose source this server only reads, so nothing can be appended "
                + "to it. Nothing was written.").ConfigureAwait(false);
            return null;
        }

        // Which of the file's columns go into which of the layer's, by name unless fieldMappings says otherwise; the
        // identity column and the ones this server fills in (editor tracking, GlobalID) are never the file's to write.
        Dictionary<string, FieldDescription> fields = new(StringComparer.OrdinalIgnoreCase);
        string? globalId = GlobalIds.FieldOf(described.Fields);

        foreach (FieldDescription field in described.Fields)
        {
            if (!field.Maintained
                && !string.Equals(field.Name, layer.Definition.IntegerIdentityColumn, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(field.Name, globalId, StringComparison.OrdinalIgnoreCase))
            {
                fields[field.Name] = field;
            }
        }

        List<(string Source, string Target)> columns = [];

        foreach (InferredColumn column in dataset.Columns)
        {
            string? name = target.Mappings is not null && target.Mappings.TryGetValue(column.Name, out string? mapped)
                ? mapped
                : fields.ContainsKey(column.Name) ? column.Name
                : fields.ContainsKey(PostGisImporter.ColumnNameFor(column.Name)) ? PostGisImporter.ColumnNameFor(column.Name)
                : null;

            if (name is not null && fields.TryGetValue(name, out FieldDescription field)
                && (target.Only is null || target.Only.Contains(field.Name, StringComparer.OrdinalIgnoreCase)))
            {
                columns.Add((column.Name, field.Name));
            }
        }

        using MemoryStream body = new();

        await using (Utf8JsonWriter json = new(body))
        {
            json.WriteStartArray();

            foreach (ImportedFeature feature in dataset.Features)
            {
                json.WriteStartObject();
                json.WriteStartObject("attributes");

                foreach ((string source, string name) in columns)
                {
                    if (feature.Values.TryGetValue(source, out JsonElement value))
                    {
                        json.WritePropertyName(name);
                        value.WriteTo(json);
                    }
                }

                json.WriteEndObject();

                if (feature.Geometry is { } geometry)
                {
                    json.WritePropertyName("geometry");
                    ArcGisGeometryWriter.Write(json, geometry, dataset.Srid);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        string adds = System.Text.Encoding.UTF8.GetString(body.ToArray());

        if (await Program.PrepareEditAsync(context, layer, adds, null, null, rollbackOnFailure: true, contexts, cancellation)
                .ConfigureAwait(false) is not { } prepared)
        {
            return null;
        }

        EditOutcome outcome = await connections
            .WriterFor(layer, prepared.Description.Fields, prepared.Description.Tracking, prepared.Description.Subtypes)
            .ApplyAsync(prepared.Batch, cancellation)
            .ConfigureAwait(false);

        await Program.AuditEditsAsync(context, audit, layer.Definition.Name, prepared.Parsed, outcome, cancellation)
            .ConfigureAwait(false);

        if (outcome.RolledBack || !outcome.AllSucceeded)
        {
            string why = outcome.Adds.Where(r => !r.Succeeded).Select(r => r.Error).FirstOrDefault(e => e is { Length: > 0 })
                ?? "The features were not written.";
            int row = outcome.Adds.ToList().FindIndex(r => !r.Succeeded) + 1;

            await RefuseAsync(context, 400,
                $"'{layer.Definition.Name}' was not changed: feature {row} of the file was refused — {why}")
                .ConfigureAwait(false);
            return null;
        }

        return new Written(layer, outcome.Adds.Count);
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Providers.PostGis;

namespace Graticula.Host.Forms;

/// <summary>
/// Where a form with a <c>submission_url</c> writes: the address on the settings sheet, read as the address of a feature
/// service or layer — ADR-287 §5.10 and §5.17.
/// </summary>
/// <param name="Authority">
/// The host and port of an absolute address, or null for a relative one (which is this server's by being relative).
/// </param>
/// <param name="Scheme">The scheme of an absolute address, or null.</param>
/// <param name="Folder">The service's folder, or null for the root.</param>
/// <param name="Service">The service's name.</param>
/// <param name="Layer">The layer the address names, or null when it names the service.</param>
public sealed record SubmissionAddress(string? Scheme, string? Authority, string? Folder, string Service, int? Layer)
{
    /// <summary>The address as this server answers it, with the layer when there is one.</summary>
    public string Path(int? layer = null) =>
        $"/rest/services/{(Folder is null ? string.Empty : Folder + "/")}{Service}/FeatureServer"
        + (layer is { } index ? "/" + index.ToString(CultureInfo.InvariantCulture) : string.Empty);
}

/// <summary>Reads a <c>submission_url</c> and says whether it is this server's.</summary>
/// <remarks>
/// <para>
/// <b>The forms an ArcGIS address takes.</b> <c>https://host/rest/services/{folder}/{name}/FeatureServer/{n}</c>, the
/// same with the layer left off, the same relative (<c>/rest/services/…</c>), and with the prefixes ArcGIS Server
/// answers under that this server also accepts (<c>/arcgis/rest/services</c>, <c>/server/rest/services</c>), a path base
/// when the server runs under one, a trailing slash, and a query such as <c>?f=json</c>, which is ignored. Anything else
/// is refused with a sentence saying what an address looks like.
/// </para>
/// <para>
/// <b>Whether the host is this server's is a separate question</b> (<see cref="IsOwn"/>), because it needs the request;
/// whether the address is shaped like a feature service's does not, and is checked every time a form is saved.
/// </para>
/// </remarks>
public static class SubmissionTarget
{
    /// <summary>Reads an address.</summary>
    /// <param name="url">The settings sheet's value.</param>
    /// <param name="address">The address read, when it is one.</param>
    /// <param name="error">Why it is not, when it is not.</param>
    /// <param name="pathBase">The server's path base, stripped from the front when present.</param>
    /// <returns>Whether it is shaped like a feature service's address.</returns>
    public static bool TryParse(string url, out SubmissionAddress? address, out string? error, string? pathBase = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        address = null;
        string text = url.Trim();
        string? scheme = null;
        string? authority = null;
        string path;

        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            error = $"'{text}' has no scheme. Write https://host/rest/services/…/FeatureServer/0, or a path starting with one slash.";
            return false;
        }

        if (text.StartsWith('/'))
        {
            path = text;
        }
        else if (Uri.TryCreate(text, UriKind.Absolute, out Uri? absolute) && absolute.Scheme is "http" or "https")
        {
            scheme = absolute.Scheme;
            authority = absolute.Authority;
            path = absolute.AbsolutePath;

            if (absolute.UserInfo.Length > 0)
            {
                error = "A submission_url holds no user name or password.";
                return false;
            }
        }
        else
        {
            error = $"'{text}' is not the address of a feature layer. It is an http or https address, or a path starting with a slash, ending in /FeatureServer or /FeatureServer/0.";
            return false;
        }

        int cut = path.IndexOfAny(['?', '#']);
        path = cut >= 0 ? path[..cut] : path;

        List<string> parts = [.. path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString)];

        if (pathBase is { Length: > 1 })
        {
            List<string> baseParts = [.. pathBase.Split('/', StringSplitOptions.RemoveEmptyEntries)];

            if (parts.Count >= baseParts.Count
                && parts.Take(baseParts.Count).SequenceEqual(baseParts, StringComparer.OrdinalIgnoreCase))
            {
                parts.RemoveRange(0, baseParts.Count);
            }
        }

        // A check that does not know the server's path base (a form is saved with no request to ask) accepts any prefix
        // before /rest; publishing, which knows it, does not.
        if (pathBase is null && parts.FindIndex(p => p.Equals("rest", StringComparison.OrdinalIgnoreCase)) is > 0 and var prefix)
        {
            parts.RemoveRange(0, prefix);
        }

        if (parts.Count > 0 && (parts[0].Equals("arcgis", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("server", StringComparison.OrdinalIgnoreCase)))
        {
            parts.RemoveAt(0);
        }

        int at = parts.FindIndex(p => p.Equals("FeatureServer", StringComparison.OrdinalIgnoreCase));
        int? layer = null;

        if (at >= 0 && at == parts.Count - 2)
        {
            if (!int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                error = $"'{parts[^1]}' after FeatureServer is not a layer number.";
                return false;
            }

            layer = index;
            parts.RemoveAt(parts.Count - 1);
        }

        if (at < 0 || at != parts.Count - 1
            || parts.Count < 4 || parts.Count > 5
            || !parts[0].Equals("rest", StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals("services", StringComparison.OrdinalIgnoreCase))
        {
            error = $"'{text}' is not the address of a feature layer of this server: it is …/rest/services/{{folder}}/{{name}}/FeatureServer/{{layer}}, "
                + "and the folder is left out for a service at the root.";
            return false;
        }

        address = new SubmissionAddress(scheme, authority, parts.Count == 5 ? parts[2] : null, parts[^2], layer);
        error = null;
        return true;
    }

    /// <summary>Whether an absolute address names the host and port that answered the request. A relative one always does.</summary>
    /// <param name="address">The address.</param>
    /// <param name="host">The request's host, with its port when it has one.</param>
    /// <param name="scheme">The request's scheme.</param>
    public static bool IsOwn(SubmissionAddress address, string host, string scheme)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (address.Authority is null)
        {
            return true;
        }

        static (string Name, int Port) Split(string authority, string scheme)
        {
            Uri parsed = new($"{scheme}://{authority}");
            return (parsed.IdnHost.ToLowerInvariant(), parsed.Port);
        }

        (string name, int port) = Split(address.Authority, address.Scheme ?? scheme);
        (string ownName, int ownPort) = Split(host, scheme);
        return name == ownName && port == ownPort;
    }
}

/// <summary>A layer or table that already exists, as far as matching a form to it needs to know.</summary>
/// <param name="Index">Its number in its service.</param>
/// <param name="Id">Its catalogue identity, which a relationship names.</param>
/// <param name="Name">Its name.</param>
/// <param name="Geometry">Its geometry, or null for a table.</param>
/// <param name="Hosted">Whether it is a hosted layer with an integer key, the only kind an answers view can be made over (ADR-113).</param>
/// <param name="KeepsAttachments">Whether it takes attachments.</param>
/// <param name="Fields">Its fields, as a client sees them.</param>
/// <param name="Related">The relationships it is the origin of.</param>
public sealed record ExistingLayer(
    int Index,
    Guid Id,
    string Name,
    GeometryKind? Geometry,
    bool Hosted,
    bool KeepsAttachments,
    IReadOnlyList<FieldDescription> Fields,
    IReadOnlyList<ExistingRelationship> Related);

/// <summary>A relationship of an existing layer: which of its keys joins which key of which other layer.</summary>
public sealed record ExistingRelationship(string OriginKey, Guid RelatedLayer, string RelatedKey, int? Number);

/// <summary>An existing service's layers and tables.</summary>
public sealed record ExistingService(string? Folder, string Name, IReadOnlyList<ExistingLayer> Layers);

/// <summary>A form's table matched to the layer or table it writes to.</summary>
public sealed record MatchedLayer(PlannedLayer Planned, ExistingLayer Existing, int? RelationshipNumber);

/// <summary>The outcome of matching a form to an existing service: every refusal, or every pairing.</summary>
public sealed record SubmissionMatch(IReadOnlyList<FormIssue> Refusals, IReadOnlyList<MatchedLayer> Matched)
{
    /// <summary>Whether the form can write to the service.</summary>
    public bool Accepted => Refusals.Count == 0;
}

/// <summary>
/// Checks a form against the layer it is to write to, and refuses what would fail the first time somebody submits —
/// ADR-287 §5.17.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is changed in the target.</b> Publishing to a new layer adds fields, GlobalIDs, editor tracking and
/// choice lists; publishing to an existing one adds none of them. INFERRED: Survey123 does not alter an existing
/// layer's schema when it publishes to one, and a layer somebody else designed is theirs. Whatever the form needs that the
/// layer lacks is a refusal that says so, with the row of the question and the column that decided it.
/// </para>
/// <para>
/// <b>Every refusal is found, not the first.</b> The publisher edits the form once and publishes once.
/// </para>
/// </remarks>
public static class SubmissionMatcher
{
    /// <summary>Matches a form's plan to a service.</summary>
    /// <param name="definition">The checked form, for the rows its questions and repeats are on.</param>
    /// <param name="plan">What publishing would have made.</param>
    /// <param name="address">The address the form names.</param>
    /// <param name="service">The service the address resolved to.</param>
    public static SubmissionMatch Match(FormDefinition definition, FormLayerPlan plan, SubmissionAddress address, ExistingService service)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(service);

        List<FormIssue> refusals = [];
        List<MatchedLayer> matched = [];
        Dictionary<string, FormTable> tables = definition.Root.AllTables.ToDictionary(t => t.Name, StringComparer.Ordinal);

        foreach (PlannedLayer planned in plan.Layers)
        {
            FormTable table = tables[planned.Repeat];
            int? relationship = null;
            ExistingLayer? layer;

            if (planned.ParentRepeat is null)
            {
                layer = Top(planned, address, service, refusals);
            }
            else if (matched.FirstOrDefault(m => m.Planned.Repeat == planned.ParentRepeat) is { } parent)
            {
                (layer, relationship) = Child(planned, table, parent.Existing, service, refusals);
            }
            else
            {
                continue;
            }

            if (layer is null)
            {
                continue;
            }

            Check(planned, table, layer, refusals);
            matched.Add(new MatchedLayer(planned, layer, relationship));
        }

        return new SubmissionMatch(refusals, matched);
    }

    private static ExistingLayer? Top(PlannedLayer planned, SubmissionAddress address, ExistingService service, List<FormIssue> refusals)
    {
        const string Sheet = "settings";

        if (address.Layer is { } asked)
        {
            if (service.Layers.FirstOrDefault(l => l.Index == asked) is { } named)
            {
                return named;
            }

            refusals.Add(new FormIssue(Sheet, 2, "submission_url",
                $"The service '{service.Name}' has no layer {asked}. It has {string.Join(", ", service.Layers.Select(l => l.Index))}."));
            return null;
        }

        // INFERRED: an address that names the service and no layer means its first layer of the kind the form writes —
        // a layer when the form has a location question, a table when it has none.
        if (service.Layers.FirstOrDefault(l => (l.Geometry is null) == (planned.GeometryType is null)) is { } first)
        {
            return first;
        }

        refusals.Add(new FormIssue(Sheet, 2, "submission_url",
            $"The service '{service.Name}' has no {(planned.GeometryType is null ? "table" : "layer with geometry")}, and the form {(planned.GeometryType is null ? "has no location question" : "has a location question")}. "
            + "Name the layer to write to, ending the address in /FeatureServer/{n}."));
        return null;
    }

    private static (ExistingLayer? Layer, int? Number) Child(
        PlannedLayer planned, FormTable table, ExistingLayer parent, ExistingService service, List<FormIssue> refusals)
    {
        string[] names = [planned.Repeat, FormLayerPlan.Identifier(planned.Repeat), $"{service.Name}_{FormLayerPlan.Identifier(planned.Repeat)}"];

        foreach (ExistingRelationship related in parent.Related)
        {
            if (service.Layers.FirstOrDefault(l => l.Id == related.RelatedLayer) is not { } candidate
                || !names.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!related.OriginKey.Equals("globalid", StringComparison.OrdinalIgnoreCase)
                || !related.RelatedKey.Equals(FormLayerPlan.ParentColumn, StringComparison.OrdinalIgnoreCase))
            {
                refusals.Add(new FormIssue("survey", table.Row, "name",
                    $"The repeat '{planned.Repeat}' is related to '{candidate.Name}' by {related.OriginKey} and {related.RelatedKey}; a form's repeat is related by globalid and {FormLayerPlan.ParentColumn}."));
                return (null, null);
            }

            return (candidate, related.Number);
        }

        refusals.Add(new FormIssue("survey", table.Row, "name",
            $"The repeat '{planned.Repeat}' needs a table of the service '{service.Name}' that is named '{planned.Repeat}' and related to '{parent.Name}' by globalid and {FormLayerPlan.ParentColumn}; there is none. Nothing is added to an existing service."));
        return (null, null);
    }

    private static void Check(PlannedLayer planned, FormTable table, ExistingLayer layer, List<FormIssue> refusals)
    {
        string what = planned.Repeat.Length == 0 ? $"the layer '{layer.Name}'" : $"the table '{layer.Name}' for the repeat '{planned.Repeat}'";
        string where = char.ToUpperInvariant(what[0]) + what[1..];
        int row = planned.Repeat.Length == 0 ? 2 : table.Row;
        string column = planned.Repeat.Length == 0 ? "submission_url" : "name";
        string sheet = planned.Repeat.Length == 0 ? "settings" : "survey";

        if (!layer.Hosted)
        {
            refusals.Add(new FormIssue(sheet, row, column,
                $"{where} is a registered table. A form writes to a hosted layer, because the view that lets people it is shared with send answers without reading any back is made over a hosted layer's rows (ADR-113)."));
        }

        if (planned.GeometryType is not null && layer.Geometry is null)
        {
            refusals.Add(new FormIssue("survey", table.Location?.Row ?? row, "type", $"{where} is a table, and the form's location question '{planned.LocationQuestion}' would have nowhere to go."));
        }
        else if (planned.GeometryType is not null && layer.Geometry is { } have && !Fits(planned.GeometryType, have))
        {
            refusals.Add(new FormIssue("survey", table.Location?.Row ?? row, "type",
                $"The question '{planned.LocationQuestion}' is a {planned.GeometryType} and {what} holds {have}."));
        }
        else if (planned.GeometryType is null && layer.Geometry is not null)
        {
            refusals.Add(new FormIssue(sheet, row, column, $"{where} holds {layer.Geometry} geometry and the form has no location question to give it; add one, or name a table."));
        }

        if (Graticula.Features.GlobalIds.FieldOf(layer.Fields) is null)
        {
            refusals.Add(new FormIssue(sheet, row, column,
                $"{where} has no GlobalID field, and a form sends every record under its own GlobalID so that one sent twice lands once. A form does not add one to an existing layer."));
        }

        if (planned.ParentRepeat is not null
            && !layer.Fields.Any(f => f.Type == FieldType.Guid && f.Name.Equals(FormLayerPlan.ParentColumn, StringComparison.OrdinalIgnoreCase)))
        {
            refusals.Add(new FormIssue(sheet, row, column, $"{where} has no {FormLayerPlan.ParentColumn} field to name the record above it by."));
        }

        Dictionary<string, FormQuestion> questions = table.Questions.GroupBy(q => q.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (PlannedField field in planned.Fields)
        {
            FormQuestion question = questions[field.Question];
            string name = PostGisImporter.ColumnNameFor(field.Column);
            FieldDescription? found = layer.Fields.Where(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Cast<FieldDescription?>().FirstOrDefault();

            if (found is not { } existing)
            {
                refusals.Add(new FormIssue("survey", question.Row, "name",
                    $"The question '{field.Question}' is stored in '{name}', and {what} has no such field. Nothing is added to an existing layer: rename the question to a field it has, or add the field to the layer."));
                continue;
            }

            string typeColumn = question.EsriFieldType is null ? "type" : "bind::esri:fieldType";

            if (!Compatible(field.Type, existing.Type))
            {
                refusals.Add(new FormIssue("survey", question.Row, typeColumn,
                    $"The question '{field.Question}' is {field.Type} and the field '{existing.Name}' of {what} is {existing.Type}."));
                continue;
            }

            if (field.Type == FieldType.Text && question.FieldLength is { } length && existing.MaxLength is { } room && room < length)
            {
                refusals.Add(new FormIssue("survey", question.Row, "bind::esri:fieldLength",
                    $"The question '{field.Question}' allows {length} characters and the field '{existing.Name}' holds {room}."));
            }

            if (existing.Domain is { Kind: Graticula.Catalog.DomainKind.CodedValue } domain && field.Domain is { } choices)
            {
                HashSet<string> allowed = [.. domain.Codes.Select(c => c.Code.ToString())];

                if (choices.Codes.Select(c => c.Code.ToString()).FirstOrDefault(c => !allowed.Contains(c)) is { } missing)
                {
                    refusals.Add(new FormIssue("survey", question.Row, "type",
                        $"The choice '{missing}' of '{choices.List}' is not in the domain of the field '{existing.Name}', which would refuse an answer of it."));
                }
            }
        }

        if (planned.AttachmentQuestions.Count > 0 && !layer.KeepsAttachments)
        {
            foreach (string name in planned.AttachmentQuestions)
            {
                refusals.Add(new FormIssue("survey", questions.TryGetValue(name, out FormQuestion? attached) ? attached.Row : row, "type",
                    $"The question '{name}' is an attachment, and {what} does not keep attachments."));
            }
        }

    }

    private static bool Fits(string planned, GeometryKind have) => planned switch
    {
        "Point" => have == GeometryKind.Point,
        "LineString" => have is GeometryKind.LineString or GeometryKind.MultiLineString,
        _ => have is GeometryKind.Polygon or GeometryKind.MultiPolygon,
    };

    /// <summary>
    /// Whether an answer of the form's type can be stored in a field of the layer's. A number goes into a wider one; nothing
    /// else changes type, because converting would change the value.
    /// </summary>
    public static bool Compatible(FieldType asked, FieldType have) => asked == have || (asked, have) switch
    {
        (FieldType.SmallInteger, FieldType.Integer or FieldType.BigInteger or FieldType.Double) => true,
        (FieldType.Integer, FieldType.BigInteger or FieldType.Double) => true,
        (FieldType.Single, FieldType.Double) => true,
        _ => false,
    };
}

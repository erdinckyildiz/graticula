using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Graticula.Features;
using Graticula.Platform.Admin;

namespace Graticula.Host;

/// <summary>
/// Which webhook events this server raises, and from what — ADR-266 §2.
/// </summary>
/// <remarks>
/// <para>
/// <b>A portal event is an audited act.</b> Every act a portal webhook can be told about already writes an audit row
/// (ADR-017 §5d) — who, what, to which resource, whether it worked — so the audit log is the one place they are all
/// seen, through whichever door they came: Studio, the portal's REST, ArcGIS Pro. The table below names each audit
/// action that is one of ArcGIS's portal events; an action not in it raises nothing, and a failed act raises nothing.
/// </para>
/// <para>
/// <b>A feature-service event is an edit that kept something</b>, seen where every editing face gets its writer
/// (<see cref="LayerConnections"/>), after it committed.
/// </para>
/// </remarks>
internal static class WebhookEvents
{
    /// <summary>The ArcGIS source and operation each audited act is, by its audit action.</summary>
    internal static readonly IReadOnlyDictionary<string, (string Source, string Operation)> PortalActs =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            // Items: a service, a document (and an app or link, which is one), a web map, a toolbox.
            ["service.publish"] = ("item", "add"),
            ["service.view"] = ("item", "add"),
            ["document.create"] = ("item", "add"),
            ["webmap.create"] = ("item", "add"),
            ["toolbox.publish"] = ("item", "publish"),
            ["service.title"] = ("item", "update"),
            ["service.tags"] = ("item", "update"),
            ["service.update"] = ("item", "update"),
            ["service.credits"] = ("item", "update"),
            ["document.update"] = ("item", "update"),
            ["document.replace"] = ("item", "update"),
            ["document.tags"] = ("item", "update"),
            ["webmap.update"] = ("item", "update"),
            ["webmap.tags"] = ("item", "update"),
            ["item.categories"] = ("item", "update"),
            ["item.license"] = ("item", "update"),
            ["service.delete"] = ("item", "delete"),
            ["document.delete"] = ("item", "delete"),
            ["webmap.delete"] = ("item", "delete"),
            ["toolbox.delete"] = ("item", "delete"),
            ["service.share"] = ("item", "share"),
            ["toolbox.share"] = ("item", "share"),
            ["service.owner"] = ("item", "reassign"),
            ["webmap.owner"] = ("item", "reassign"),
            ["document.owner"] = ("item", "reassign"),
            ["service.protection"] = ("item", "protect"),
            ["webmap.protection"] = ("item", "protect"),

            // Comments, since ADR-267 gave items them (ADR-266 condition 1).
            ["item.comment.add"] = ("item", "addComment"),
            ["item.comment.update"] = ("item", "updateComment"),
            ["item.comment.delete"] = ("item", "deleteComment"),

            // Groups.
            ["group.create"] = ("group", "add"),
            ["group.settings"] = ("group", "update"),
            ["group.delete"] = ("group", "delete"),
            ["group.member"] = ("group", "addUsers"),
            ["group.join"] = ("group", "addUsers"),
            ["group.member.remove"] = ("group", "removeUsers"),
            ["group.leave"] = ("group", "removeUsers"),
            ["group.share"] = ("group", "itemShare"),
            ["group.share.document"] = ("group", "itemShare"),
            ["group.share.map"] = ("group", "itemShare"),
            ["group.unshare"] = ("group", "itemUnshare"),
            ["group.unshare.document"] = ("group", "itemUnshare"),
            ["group.unshare.map"] = ("group", "itemUnshare"),

            // Users.
            ["member.create"] = ("user", "add"),
            ["member.remove"] = ("user", "delete"),
            ["member.transfer"] = ("user", "delete"),
            ["member.disable"] = ("user", "disable"),
            ["member.enable"] = ("user", "enable"),
            ["member.profile"] = ("user", "update"),
            ["member.role"] = ("user", "updateUserRole"),
            ["member.usertype"] = ("user", "updateUserLicenseType"),

            // Roles.
            ["role.create"] = ("role", "add"),
            ["role.privileges"] = ("role", "update"),
            ["role.delete"] = ("role", "delete"),
        };

    /// <summary>Every operation a portal webhook may name under each source, ArcGIS's own list.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> PortalOperations = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["items"] = ["add", "delete", "protect", "unprotect", "update", "move", "publish", "share", "unshare", "reassign",
            "addComment", "deleteComment", "updateComment"],
        ["groups"] = ["add", "update", "delete", "protect", "unprotect", "invite", "addUsers", "removeUsers", "updateUsers",
            "reassign", "itemShare", "itemUnshare", "requestJoin"],
        ["users"] = ["add", "delete", "signin", "signout", "signIn", "signOut", "update", "disable", "enable", "updateUserRole",
            "updateUserLicenseType", "bulkEnable", "bulkDisable"],
        ["roles"] = ["add", "update", "delete"],
    };

    /// <summary>ArcGIS's feature service change types, <c>*</c> for all.</summary>
    internal static readonly string[] ChangeTypes =
    [
        "*", "FeaturesCreated", "FeaturesUpdated", "FeaturesDeleted", "FeaturesEdited", "AttachmentsCreated",
        "AttachmentsUpdated", "AttachmentsDeleted", "LayerSchemaChanged", "LayerDefinitionChanged",
        "FeatureServiceDefinitionChanged",
    ];

    /// <summary>
    /// The change types this server raises — every one ArcGIS lists but <c>LayerDefinitionChanged</c>, since ADR-266
    /// condition 1: there is no layer-level <c>updateDefinition</c> here to change one.
    /// </summary>
    internal static readonly string[] ChangeTypesRaised =
    [
        "FeaturesCreated", "FeaturesUpdated", "FeaturesDeleted", "FeaturesEdited", "AttachmentsCreated", "AttachmentsUpdated",
        "AttachmentsDeleted", "LayerSchemaChanged", "FeatureServiceDefinitionChanged",
    ];

    /// <summary>The most object ids of each kind one feature-service event carries.</summary>
    internal const int ObjectIdLimit = 1000;

    /// <summary>A portal event: what it is, what it names, every URI it answers to, and its JSON.</summary>
    /// <param name="Name">Its name for the delivery log — <c>/items/add</c>.</param>
    /// <param name="Uris">Every event URI a webhook may have named to hear it.</param>
    /// <param name="Body">The event object of ArcGIS's payload.</param>
    internal sealed record PortalEvent(string Name, IReadOnlyList<string> Uris, JsonObject Body);

    /// <summary>
    /// The portal event an audited act is, or null when it is none — an act not in <see cref="PortalActs"/>, one that
    /// failed, or a layer group of a service, which shares <c>group.delete</c> with a portal group.
    /// </summary>
    /// <param name="entry">The audit row.</param>
    /// <param name="id">The id ArcGIS names the source by, already resolved; null when it cannot be.</param>
    /// <param name="when">When it happened.</param>
    /// <returns>The event, or null.</returns>
    internal static PortalEvent? FromAudit(AuditEvent entry, string? id, DateTimeOffset when)
    {
        if (!entry.Succeeded || !PortalActs.TryGetValue(entry.Action, out (string Source, string Operation) act))
        {
            return null;
        }

        JsonObject? detail = ParseDetail(entry.Detail);

        // A service's layer group is removed under the same action as a portal group, and names its index.
        if (entry.Action == "group.delete" && detail?["index"] is not null)
        {
            return null;
        }

        string operation = act.Operation;

        if (operation == "protect" && detail?["protected"] is JsonValue value && value.TryGetValue(out bool on) && !on)
        {
            operation = "unprotect";
        }

        string plural = act.Source + "s";
        List<string> uris = ["/", $"/{plural}", $"/{plural}/{operation}"];

        if (id is not null)
        {
            uris.Add($"/{plural}/{id}");
            uris.Add($"/{plural}/{id}/{operation}");
        }

        JsonObject properties = PropertiesOf(entry.Action, detail);

        JsonObject body = new()
        {
            ["id"] = id,
            ["operation"] = operation,
            ["properties"] = properties,
            ["source"] = act.Source,
            ["userId"] = entry.Principal == Guid.Empty ? null : entry.Principal.ToString("N"),
            ["username"] = entry.PrincipalName,
            ["when"] = when.ToUnixTimeMilliseconds(),
        };

        return new PortalEvent($"/{plural}/{operation}", uris, body);
    }

    /// <summary>
    /// What the source is named by in the audit row, and how its id is found: a document's or a web map's row names its
    /// id; a service's, a group's and a member's name them by name; a role's names it by name, which is its id.
    /// </summary>
    /// <param name="entry">The audit row.</param>
    /// <returns>The source, the name or id the row carries, and the service's folder where it is one.</returns>
    internal static (string Source, string? Named, string? Folder) NamedBy(AuditEvent entry)
    {
        string source = PortalActs.TryGetValue(entry.Action, out (string Source, string Operation) act) ? act.Source : string.Empty;
        string? folder = ParseDetail(entry.Detail)?["folder"] is JsonValue f && f.TryGetValue(out string? text) ? text : null;

        return entry.Action.StartsWith("service.", StringComparison.Ordinal)
            ? ("service", entry.Resource, string.IsNullOrEmpty(folder) ? null : folder)
            : (source, entry.Resource, null);
    }

    /// <summary>An id as ArcGIS writes one: a GUID without its hyphens, anything else as it is.</summary>
    /// <param name="id">The id.</param>
    /// <returns>It, written so.</returns>
    internal static string? ArcGisId(string? id) =>
        id is null ? null : Guid.TryParse(id, out Guid guid) ? guid.ToString("N") : id;

    /// <summary>The <c>properties</c> ArcGIS documents for the events that carry some.</summary>
    private static JsonObject PropertiesOf(string action, JsonObject? detail)
    {
        JsonObject properties = [];
        JsonNode? member = detail?["member"];
        JsonNode? to = detail?["to"];

        if (action == "group.member" && member is JsonValue added)
        {
            properties["addedUserNames"] = new JsonArray(added.DeepClone());
        }
        else if (action == "group.member.remove" && member is JsonValue removed)
        {
            properties["removedUserNames"] = new JsonArray(removed.DeepClone());
        }
        else if (action == "member.role" && to is not null)
        {
            properties["userRoleUpdatedTo"] = to.DeepClone();
        }
        else if (action == "member.usertype" && to is not null)
        {
            properties["userLicenseTypeUpdatedTo"] = to.DeepClone();
        }
        else if (action is "service.owner" or "webmap.owner" or "document.owner" && to is not null)
        {
            properties["reassignedTo"] = to.DeepClone();
        }

        return properties;
    }

    private static JsonObject? ParseDetail(string detail)
    {
        try
        {
            return JsonNode.Parse(detail) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A feature-service event: the change types it is, with <c>*</c>, and its JSON.</summary>
    /// <param name="Name">Its name for the delivery log.</param>
    /// <param name="ChangeTypes">The change types, with <c>*</c>.</param>
    /// <param name="Body">The event, without the webhook's own name, which is added when it is sent.</param>
    internal sealed record ServiceEvent(string Name, IReadOnlyList<string> ChangeTypes, JsonObject Body);

    /// <summary>
    /// The feature-service event an edit is, or null when it kept nothing. <b>Object ids rather than a
    /// <c>changesUrl</c></b>: ArcGIS's names an <c>extractChanges</c> request, which needs sync this server does not have
    /// (ADR-082); the ids are what that request would return the keys of, and <c>queryUrl</c> reads the rows (ADR-266 §3).
    /// </summary>
    /// <param name="serviceName">The service's name.</param>
    /// <param name="folder">Its folder, or null.</param>
    /// <param name="layerId">The layer's index in it.</param>
    /// <param name="outcome">What the edit did.</param>
    /// <param name="when">When.</param>
    /// <returns>The event, or null.</returns>
    internal static ServiceEvent? FromEdit(string serviceName, string? folder, int layerId, EditOutcome outcome, DateTimeOffset when)
    {
        if (outcome.RolledBack)
        {
            return null;
        }

        long[] adds = Kept(outcome.Adds);
        long[] updates = Kept(outcome.Updates);
        long[] deletes = Kept(outcome.Deletes);

        if (adds.Length + updates.Length + deletes.Length == 0)
        {
            return null;
        }

        List<string> events = [];

        if (adds.Length > 0)
        {
            events.Add("FeaturesCreated");
        }

        if (updates.Length > 0)
        {
            events.Add("FeaturesUpdated");
        }

        if (deletes.Length > 0)
        {
            events.Add("FeaturesDeleted");
        }

        JsonObject body = new()
        {
            ["layerId"] = layerId,
            ["serviceName"] = serviceName,
            ["folder"] = folder,
            ["lastUpdatedTime"] = when.ToUnixTimeMilliseconds(),
            ["events"] = new JsonArray([.. events.Select(e => (JsonNode)JsonValue.Create(e)!)]),
            ["objectIds"] = new JsonObject
            {
                ["adds"] = Ids(adds),
                ["updates"] = Ids(updates),
                ["deletes"] = Ids(deletes),
            },
            ["truncated"] = adds.Length > ObjectIdLimit || updates.Length > ObjectIdLimit || deletes.Length > ObjectIdLimit,
        };

        return new ServiceEvent(string.Join(",", events), ["*", "FeaturesEdited", .. events], body);
    }

    /// <summary>
    /// A feature-service change that is not an edit of rows (ADR-266 condition 1): its one change type, the layer when it
    /// has one, and what it touched under <paramref name="detail"/>'s keys — the attachment's parent and id, the fields.
    /// </summary>
    /// <param name="serviceName">The service's name.</param>
    /// <param name="folder">Its folder, or null.</param>
    /// <param name="layerId">The layer's index, or null for the service.</param>
    /// <param name="changeType">The change type.</param>
    /// <param name="detail">What it touched, or null.</param>
    /// <param name="when">When.</param>
    /// <returns>The event.</returns>
    internal static ServiceEvent FromChange(
        string serviceName, string? folder, int? layerId, string changeType, JsonObject? detail, DateTimeOffset when)
    {
        JsonObject body = new()
        {
            ["layerId"] = layerId,
            ["serviceName"] = serviceName,
            ["folder"] = folder,
            ["lastUpdatedTime"] = when.ToUnixTimeMilliseconds(),
            ["events"] = new JsonArray(JsonValue.Create(changeType)),
        };

        foreach ((string key, JsonNode? value) in detail ?? [])
        {
            body[key] = value?.DeepClone();
        }

        return new ServiceEvent(changeType, ["*", changeType], body);
    }

    private static long[] Kept(IReadOnlyList<EditResult> results) =>
        [.. results.Where(r => r.Succeeded).Select(r => r.Identity)];

    private static JsonArray Ids(long[] ids) =>
        new([.. ids.Take(ObjectIdLimit).Select(id => (JsonNode)JsonValue.Create(id))]);
}

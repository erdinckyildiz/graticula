using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// Turns ArcGIS Field Maps tasks on and off for a web map — ADR-286.
/// </summary>
/// <remarks>
/// <para>
/// <b>Studio does what Field Maps Designer does, because Designer cannot do it here.</b> Designer looks up a layer's
/// <c>serviceItemId</c> in its own portal and greys out a layer it cannot find, so a map of this server's layers cannot
/// be given tasks from it (measured on ArcGIS Enterprise 2025.2, 2026-11-08).
/// </para>
/// <para>
/// <b>Turning tasks on changes the layer; turning them off changes only the map.</b> On adds the task fields the layer
/// lacks and gives them their lists, then writes the map's task configuration. Off removes the configuration and
/// nothing else: the fields hold the tasks people have worked, and dropping them is the Fields page's act, where
/// it says what is lost.
/// </para>
/// </remarks>
internal static partial class AdminEndpoints
{
    private static void MapWebMapTasks(WebApplication app)
    {
        app.MapGet("/content/webmaps/{id}/tasks", GetWebMapTasksAsync);
        app.MapPut("/content/webmaps/{id}/tasks", SetWebMapTasksAsync);
        app.MapDelete("/content/webmaps/{id}/tasks", RemoveWebMapTasksAsync);
    }

    /// <summary>What <c>PUT /content/webmaps/{id}/tasks</c> takes.</summary>
    /// <param name="Layer">The operational layer's id in the map.</param>
    /// <param name="Types">The task types, in the order they are listed.</param>
    /// <param name="Assignees">Who a task may be given to.</param>
    internal sealed record WebMapTasksRequest(
        [property: JsonPropertyName("layer")] string? Layer,
        [property: JsonPropertyName("types")] IReadOnlyList<string>? Types,
        [property: JsonPropertyName("assignees")] IReadOnlyList<WebMapTaskAssignee>? Assignees);

    /// <summary>One person a task may be assigned to.</summary>
    /// <param name="Username">The account name, which Field Maps matches against whoever is signed in.</param>
    /// <param name="Label">What the list shows; the account name when empty.</param>
    internal sealed record WebMapTaskAssignee(
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("label")] string? Label);

    /// <summary>The longest task type or assignee label: the length ArcGIS allows a coded value's name.</summary>
    private const int LongestTaskLabel = 255;

    /// <summary>The most task types or assignees one list takes.</summary>
    private const int MostTaskValues = 500;

    /// <summary>The map's tasks, if it has them, and the layers they could be put on.</summary>
    private static async Task GetWebMapTasksAsync(
        HttpContext context, string id, IWebMapStore maps, PostgresLayerCatalog layers,
        IItemResourceStore resources, IGroupDirectory groups, IMemberDirectory members, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await maps.FindAsync(id, cancellation).ConfigureAwait(false) is not { } map || !Readable(current, map))
        {
            await Refuse(context, 404, NoMap(id)).ConfigureAwait(false);
            return;
        }

        byte[]? file = (await resources.FindAsync(Guid.Parse(map.Id), FieldMapsTasks.ResourcePath, cancellation).ConfigureAwait(false))?.Content;
        string? configured = file is null ? null : FieldMapsTasks.LayerIdOf(file) ?? string.Empty;

        (string Id, string Title, string? Url)[] operational = [.. OperationalFeatureLayers(map.Document)];

        // <b>The settings outlive their layer when the layer is taken out of the map elsewhere</b> — ArcGIS's Map Viewer
        // does not remove them either, and Designer then asks to *update the map to remove task settings*. Said here as
        // `orphaned`, with no layer, so the page can offer the same removal (ADR-286 §5.7).
        bool orphaned = configured is not null && !operational.Any(l => l.Id == configured);

        // The layer the left-behind settings were for, by name, when this server wrote them (the review's 13).
        string? orphanTitle = orphaned && file is not null && FieldMapsTasks.LayerOf(file) is { } gone
            && await layers.FindByIdAsync(gone, cancellation).ConfigureAwait(false) is { } was
                ? was.Definition.Name
                : null;

        // <b>A layer can hold the tasks of more than one map</b>, and the lists are the layer's: changing them here changes
        // what every such map shows. The other maps are named so that is not a surprise (the review's 16).
        Dictionary<Guid, List<string>> alsoIn = [];

        if (LayerAccess.MayManage(map.Owner, current.Principal, current.Authorization))
        {
            IReadOnlyList<WebMap> all = await maps.ListAsync(cancellation).ConfigureAwait(false);
            WebMap[] others = [.. all.Where(m => m.Id != map.Id && Guid.TryParse(m.Id, out _) && Readable(current, m))];
            IReadOnlySet<Guid> tasked = await resources.ItemsHoldingAsync(
                [.. others.Select(m => Guid.Parse(m.Id))], FieldMapsTasks.ResourcePath, cancellation).ConfigureAwait(false);

            foreach (WebMap other in others.Where(m => tasked.Contains(Guid.Parse(m.Id))))
            {
                if ((await resources.FindAsync(Guid.Parse(other.Id), FieldMapsTasks.ResourcePath, cancellation).ConfigureAwait(false))
                        is { } theirs
                    && FieldMapsTasks.LayerOf(theirs.Content) is { } shared)
                {
                    if (!alsoIn.TryGetValue(shared, out List<string>? titles))
                    {
                        alsoIn[shared] = titles = [];
                    }

                    titles.Add(other.Title);
                }
            }
        }

        bool manages = LayerAccess.MayManage(map.Owner, current.Principal, current.Authorization);
        Access access = manages ? await AccessAsync(groups, members, cancellation).ConfigureAwait(false) : Access.None;

        List<TaskLayerAnswer> candidates = [];

        foreach ((string layerId, string title, string? url) in operational)
        {
            (PublishedLayer? layer, string? why) = await TaskLayerOfAsync(context, layers, url, cancellation).ConfigureAwait(false);

            if (layer is not null && !LayerAccess.MayManage(layer.Owner, current.Principal, current.Authorization))
            {
                why = "it is not yours, so its fields are not yours to add to.";
            }

            Dictionary<string, FieldDomain> lists = layer is null
                ? []
                : layer.FieldOverrides.Where(o => o.Domain is not null)
                    .ToDictionary(o => o.Column, o => o.Domain!, StringComparer.Ordinal);

            // ADR-075: who may change a task on this layer — its owner, an administrator, and the members of a shared
            // update group it is shared with. Field Maps refuses a Start or a Finish to anybody else.
            GroupSummary[] sharedUpdate = layer is null
                ? []
                : [.. access.Groups.Where(g => g.ItemUpdate == GroupItemUpdate.AllItems && layer.SharedWith.Contains(g.Id))];

            HashSet<string> editors = new(access.Administrators, StringComparer.OrdinalIgnoreCase);

            if (layer?.Owner is { } owner && access.NameOf.TryGetValue(owner, out string? ownerName))
            {
                editors.Add(ownerName);
            }

            foreach (GroupSummary group in sharedUpdate)
            {
                editors.UnionWith(await access.MembersOfAsync(group.Name).ConfigureAwait(false));
            }

            candidates.Add(new TaskLayerAnswer(
                layerId,
                title,
                why is null,
                why,

                // The layer's own service, so the page can open its Share dialog.
                layer is null ? null : layer.Folder is { Length: > 0 } f ? $"{f}/{layer.ServiceName}" : layer.ServiceName,

                // The lists the layer already holds, so turning tasks on again starts from them (the review's M4).
                lists.TryGetValue(FieldMapsTasks.TypeField, out FieldDomain? typeList)
                    ? [.. typeList.Codes.OrderBy(c => c.Code.Number).Select(c => c.Name)]
                    : [],
                lists.TryGetValue(FieldMapsTasks.AssigneeField, out FieldDomain? people)
                    ? [.. people.Codes.Select(c => new TaskAssigneeAnswer(c.Code.Text ?? string.Empty, c.Name))]
                    : [],
                [.. sharedUpdate.Select(g => new TaskGroupAnswer(g.Name, g.Title ?? g.Name))],
                manages ? [.. editors.Order(StringComparer.OrdinalIgnoreCase)] : null,
                layer is not null && alsoIn.TryGetValue(layer.Id, out List<string>? elsewhere) ? [.. elsewhere] : []));
        }

        // Who the map reaches: everyone signed in when it is shared with the organization or everyone, and otherwise its
        // owner and the members of the groups it is shared with. Null is *everyone*.
        string[]? readers = null;

        if (manages && map.Sharing is SharingScope.Private or SharingScope.Group)
        {
            HashSet<string> reach = new(StringComparer.OrdinalIgnoreCase) { map.OwnerName };

            foreach (GroupSummary group in access.Groups.Where(g => (map.SharedWith ?? []).Contains(g.Id)))
            {
                reach.UnionWith(await access.MembersOfAsync(group.Name).ConfigureAwait(false));
            }

            reach.UnionWith(access.Administrators);
            readers = [.. reach.Order(StringComparer.OrdinalIgnoreCase)];
        }

        TaskLayerAnswer? on = configured is null || orphaned ? null : candidates.FirstOrDefault(c => c.Id == configured);

        await Results.Json(new
        {
            enabled = configured is not null && !orphaned,
            orphaned,
            orphanTitle,
            layer = string.IsNullOrEmpty(configured) ? null : configured,
            types = on?.Types ?? [],
            assignees = on?.Assignees ?? [],
            // Every scope by name (D-74). `allorganizations` reaches every member of this organization and more, so the
            // question this answers — can an assignee open the map — has the organization's answer.
            sharing = map.Sharing switch
            {
                SharingScope.Public => "public",
                SharingScope.Organization or SharingScope.AllOrganizations => "organization",
                SharingScope.Private or SharingScope.Group => (map.SharedWith ?? []).Count > 0 ? "groups" : "private",
                _ => throw new InvalidOperationException($"Sharing scope {map.Sharing} is not handled."),
            },
            readers,
            layers = candidates,
            manages,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The organization's groups, administrators and names, read once for a tasks answer.</summary>
    private sealed class Access
    {
        public static readonly Access None = new([], [], new Dictionary<Guid, string>(), null);

        private readonly IGroupDirectory? _groups;
        // Bounded by the organization's groups, and it lives for one request: an Access is built per tasks answer.
        private readonly Dictionary<string, string[]> _members = new(StringComparer.Ordinal);

        public Access(IReadOnlyList<GroupSummary> groups, string[] administrators, IReadOnlyDictionary<Guid, string> names, IGroupDirectory? directory)
        {
            Groups = groups;
            Administrators = administrators;
            NameOf = names;
            _groups = directory;
        }

        public IReadOnlyList<GroupSummary> Groups { get; }

        public string[] Administrators { get; }

        public IReadOnlyDictionary<Guid, string> NameOf { get; }

        public async Task<string[]> MembersOfAsync(string group)
        {
            if (_groups is null)
            {
                return [];
            }

            if (!_members.TryGetValue(group, out string[]? names))
            {
                names = [.. (await _groups.MembersAsync(group, CancellationToken.None).ConfigureAwait(false))
                    .Where(m => m.Standing != GroupStanding.Outside)
                    .Select(m => m.Name)];
                _members[group] = names;
            }

            return names;
        }
    }

    private static async Task<Access> AccessAsync(IGroupDirectory groups, IMemberDirectory members, CancellationToken cancellation)
    {
        IReadOnlyList<Member> everyone = await members.ListMembersAsync(cancellation).ConfigureAwait(false);

        return new Access(
            await groups.ListAsync(Guid.Empty, all: true, cancellation).ConfigureAwait(false),
            [.. everyone.Where(m => !m.IsDisabled && m.Roles.Contains(Roles.Administrator, StringComparer.Ordinal)).Select(m => m.Name)],
            everyone.ToDictionary(m => m.Id, m => m.Name),
            groups);
    }

    /// <summary>One feature layer of a map, as the tasks answer describes it.</summary>
    internal sealed record TaskLayerAnswer(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("eligible")] bool Eligible,
        [property: JsonPropertyName("why")] string? Why,
        [property: JsonPropertyName("service")] string? Service,
        [property: JsonPropertyName("types")] string[] Types,
        [property: JsonPropertyName("assignees")] TaskAssigneeAnswer[] Assignees,
        [property: JsonPropertyName("sharedUpdateGroups")] TaskGroupAnswer[] SharedUpdateGroups,
        [property: JsonPropertyName("editors")] string[]? Editors,
        [property: JsonPropertyName("alsoTasksFor")] string[] AlsoTasksFor);

    /// <summary>One assignee in a layer's list.</summary>
    internal sealed record TaskAssigneeAnswer(
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("label")] string Label);

    /// <summary>A shared update group a layer is shared with.</summary>
    internal sealed record TaskGroupAnswer(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("title")] string Title);

    /// <summary>Turns tasks on, or changes their lists: adds what the layer lacks, then writes the map's configuration.</summary>
    private static async Task SetWebMapTasksAsync(
        HttpContext context,
        string id,
        WebMapTasksRequest request,
        IWebMapStore maps,
        PostgresLayerCatalog layers,
        IItemResourceStore resources,
        PostGisImporter importer,
        ServiceContexts contexts,
        ITileCache tiles,
        IAdminCatalog catalog,
        IFieldDomainStore sharedDomains,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ManagedMapAsync(context, current, id, "give tasks to", maps, audit, cancellation).ConfigureAwait(false)
            is not { } map)
        {
            return;
        }

        if (TaskListsOf(request, out string[] types, out (string Username, string Label)[] people) is { } unreadable)
        {
            await Refuse(context, 400, unreadable).ConfigureAwait(false);
            return;
        }

        (string LayerId, string Title, string? Url)? chosen = OperationalFeatureLayers(map.Document)
            .Cast<(string, string, string?)?>()
            .FirstOrDefault(l => l!.Value.Item1 == request.Layer);

        if (string.IsNullOrWhiteSpace(request.Layer) || chosen is null)
        {
            await Refuse(context, 400,
                $"Name the layer of '{map.Title}' that holds the tasks, by its id in the map. "
                + "Only a feature layer of this map can hold them.").ConfigureAwait(false);
            return;
        }

        // <b>One task layer per map, as ArcGIS has it</b> — a map with tasks on another layer is changed by turning them off
        // first; and a map whose settings name a layer it no longer holds is cleared first, as Designer asks before it lets
        // any other layer be enabled (ADR-286 §5.7, INFERRED from Esri's *Prepare tasks*).
        if (await resources.FindAsync(Guid.Parse(map.Id), FieldMapsTasks.ResourcePath, cancellation).ConfigureAwait(false) is { } existing
            && FieldMapsTasks.LayerIdOf(existing.Content) is var held && held != request.Layer)
        {
            bool gone = !OperationalFeatureLayers(map.Document).Any(l => l.Id == held);

            await Refuse(context, 409, gone
                ? $"'{map.Title}' still holds task settings for a layer that is no longer in it. Remove those settings "
                  + "first; until then no other layer of this map can be enabled for tasks."
                : $"Tasks are enabled on another layer of '{map.Title}'. A map has one task layer: disable tasks first "
                  + "to enable them on this one.").ConfigureAwait(false);
            return;
        }

        (PublishedLayer? named, string? why) = await TaskLayerOfAsync(context, layers, chosen.Value.Url, cancellation).ConfigureAwait(false);

        if (named is null)
        {
            await Refuse(context, 400, $"'{chosen.Value.Title}' cannot hold tasks: {why}").ConfigureAwait(false);
            return;
        }

        // ADR-075: the fields are the layer's, so whoever adds them must be the layer's owner or an administrator —
        // the same door that adds a field on the Fields page.
        string service = named.Folder is { Length: > 0 } folder ? $"{folder}/{named.ServiceName}" : named.ServiceName;

        if (await HostedDataEndpoints.HostedLayerAsync(context, layers, named.Definition.Name, "give tasks to", cancellation, service)
            .ConfigureAwait(false) is not { } found)
        {
            return;
        }

        (_, LayerDescription table) = await contexts.TableAsync(found, cancellation).ConfigureAwait(false);

        // A field that is there already must be of a kind a task can be kept in, or Field Maps would fail on it later.
        foreach ((string name, _, FieldType type, _) in FieldMapsTasks.Fields)
        {
            if (table.Find(name) is { } there && !Fits(type, there.Type))
            {
                await Refuse(context, 409,
                    $"'{found.Definition.Name}' already has a field '{name}' of type {there.Type}, and tasks keep "
                    + $"{(type == FieldType.Text ? "text" : type == FieldType.Date ? "a date" : "a whole number")} in it. "
                    + "Rename or drop that field on the layer's Fields page first.").ConfigureAwait(false);
                return;
            }
        }

        if (found.FieldOverrides.FirstOrDefault(o => o.Subtypes is not null && IsTaskField(o.Subtypes.Field)) is { Column: not null } typed)
        {
            await Refuse(context, 409,
                $"'{typed.Subtypes!.Field}' holds this layer's subtypes, and Field Maps reads a task field's list "
                + "from the field itself. Remove the subtypes on the layer's Fields page first.").ConfigureAwait(false);
            return;
        }

        if (found.FieldOverrides.FirstOrDefault(o => o.Hidden && IsTaskField(o.Column)) is { Column: { } hidden })
        {
            await Refuse(context, 409,
                $"'{hidden}' is hidden on this layer, and Field Maps cannot show or set a field it is not given. "
                + "Show it on the layer's Fields page first.").ConfigureAwait(false);
            return;
        }

        List<string> added = [];

        foreach ((string name, string alias, FieldType type, int? length) in FieldMapsTasks.Fields)
        {
            if (table.Find(name) is not null)
            {
                continue;
            }

            if (await HostedDataEndpoints.AddFieldToAsync(
                    context, found, new HostedDataEndpoints.FieldDesign(name, type.ToString(), Nullable: true, length),
                    importer, contexts, tiles, catalog, audit, cancellation).ConfigureAwait(false) is not { } column)
            {
                return;
            }

            added.Add(column);
        }

        // Read again: adding a field changes the layer the overrides below are written against.
        if (await OneNamedLayerAsync(context, layers, found.Definition.Name, cancellation, service).ConfigureAwait(false)
            is not { } layer)
        {
            return;
        }

        Dictionary<Guid, FieldDomain> known = (await sharedDomains.ListAsync(cancellation).ConfigureAwait(false))
            .ToDictionary(d => d.Id, d => d.Domain);

        List<FieldOverride> wanted = [.. layer.FieldOverrides];

        async Task<string?> ListOn(string column, FieldDomain fresh, bool replaceValues)
        {
            int at = wanted.FindIndex(o => o.Matches(column));
            FieldOverride existing = at >= 0 ? wanted[at] : new FieldOverride(column, null, Hidden: false);

            if (existing.Domain is { Id: { } shared } list)
            {
                if (!replaceValues || list.SameAs(fresh.Named(list.Name)))
                {
                    return null;
                }

                // A list the layer already has is changed where it is kept, which every field using it sees (ADR-087).
                if (!await sharedDomains.UpdateAsync(shared, fresh.Named(list.Name).WithId(shared), cancellation).ConfigureAwait(false))
                {
                    return $"The list '{list.Name}' could not be changed.";
                }

                known[shared] = fresh.Named(list.Name).WithId(shared);
                wanted[at] = existing with { Domain = known[shared] };
                return null;
            }

            FieldOverride with = existing with
            {
                Domain = fresh,
                Alias = existing.Alias ?? FieldMapsTasks.Fields.First(f => f.Name == column).Alias,
            };

            if (at >= 0)
            {
                wanted[at] = with;
            }
            else
            {
                wanted.Add(with);
            }

            return null;
        }

        Guid layerId = layer.Id;
        string? failed =
            await ListOn(FieldMapsTasks.TypeField, FieldMapsTasks.TypeDomain(FieldMapsTasks.DomainName(FieldMapsTasks.TypeField, layerId), types), true).ConfigureAwait(false)
            ?? await ListOn(FieldMapsTasks.AssigneeField, FieldMapsTasks.AssigneeDomain(FieldMapsTasks.DomainName(FieldMapsTasks.AssigneeField, layerId), people), true).ConfigureAwait(false)
            ?? await ListOn(FieldMapsTasks.StatusField, FieldMapsTasks.StatusDomain(FieldMapsTasks.DomainName(FieldMapsTasks.StatusField, layerId)), false).ConfigureAwait(false)
            ?? await ListOn(FieldMapsTasks.PriorityField, FieldMapsTasks.PriorityDomain(FieldMapsTasks.DomainName(FieldMapsTasks.PriorityField, layerId)), false).ConfigureAwait(false);

        if (failed is not null)
        {
            await Refuse(context, 409, failed).ConfigureAwait(false);
            return;
        }

        // The other task fields are labelled as Designer labels them, where nobody has labelled them already.
        foreach ((string name, string alias, _, _) in FieldMapsTasks.Fields)
        {
            int at = wanted.FindIndex(o => o.Matches(name));

            if (at < 0)
            {
                wanted.Add(new FieldOverride(name, alias, Hidden: false));
            }
            else if (wanted[at].Alias is null)
            {
                wanted[at] = wanted[at] with { Alias = alias };
            }
        }

        (List<FieldOverride>? shared, int status, string? refusal) = await ShareAsync(
            wanted, known, sharedDomains, current.Principal.Id, cancellation).ConfigureAwait(false);

        if (shared is null)
        {
            await Refuse(context, status, refusal!).ConfigureAwait(false);
            return;
        }

        if (!await catalog.SetFieldOverridesAsync(layer.Id, shared, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No layer '{layer.Definition.Name}'.").ConfigureAwait(false);
            return;
        }

        string Named(string column) => shared.First(o => o.Matches(column)).Domain!.Name;

        byte[] configuration = FieldMapsTasks.Configuration(
            chosen.Value.LayerId, Named(FieldMapsTasks.TypeField), Named(FieldMapsTasks.AssigneeField), Named(FieldMapsTasks.StatusField));

        ItemResourceWrite written = await resources.WriteAsync(
            Guid.Parse(map.Id), FieldMapsTasks.ResourcePath, "application/json", configuration, "inherit",
            replace: false, 100L * 1024 * 1024, cancellation).ConfigureAwait(false);

        if (written == ItemResourceWrite.AlreadyThere)
        {
            written = await resources.WriteAsync(
                Guid.Parse(map.Id), FieldMapsTasks.ResourcePath, "application/json", configuration, "inherit",
                replace: true, 100L * 1024 * 1024, cancellation).ConfigureAwait(false);
        }

        if (written is not (ItemResourceWrite.Added or ItemResourceWrite.Replaced))
        {
            await Refuse(context, 409, $"The task configuration could not be written to '{map.Title}' ({written}).").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "webmap.tasks", map.Id,
            Detail(new { layer = layer.Definition.Name, added, types = types.Length, assignees = people.Length }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            enabled = true,
            layer = chosen.Value.LayerId,
            added,
            types,
            assignees = people.Select(p => new { username = p.Username, label = p.Label }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Turns tasks off: removes the map's configuration, and leaves every field and value where it is.</summary>
    private static async Task RemoveWebMapTasksAsync(
        HttpContext context, string id, IWebMapStore maps, IItemResourceStore resources, IAuditLog audit, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ManagedMapAsync(context, current, id, "change the tasks of", maps, audit, cancellation).ConfigureAwait(false)
            is not { } map)
        {
            return;
        }

        int removed = await resources.RemoveAsync(Guid.Parse(map.Id), FieldMapsTasks.ResourcePath, cancellation).ConfigureAwait(false);

        await AuditAsync(context, audit, "webmap.tasks.off", map.Id, Detail(new { removed }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        await Results.Json(new { enabled = false, removed = removed > 0 }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Why the request's lists cannot be used, or null; and the lists, trimmed.</summary>
    internal static string? TaskListsOf(
        WebMapTasksRequest request, out string[] types, out (string Username, string Label)[] people)
    {
        types = [.. (request.Types ?? []).Select(t => (t ?? string.Empty).Trim()).Where(t => t.Length > 0)];
        people = [.. (request.Assignees ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a?.Username))
            .Select(a => (a!.Username!.Trim(), string.IsNullOrWhiteSpace(a.Label) ? a.Username!.Trim() : a.Label.Trim()))];

        if (types.Length == 0)
        {
            return "Give at least one task type. Field Maps files every task under one, and offers no tasks without them.";
        }

        if (people.Length == 0)
        {
            return "Give at least one person a task can be assigned to. Field Maps lists a member's tasks by the "
                + "account named on them.";
        }

        if (types.Length > MostTaskValues || people.Length > MostTaskValues)
        {
            return $"A list holds at most {MostTaskValues} entries.";
        }

        if (types.Concat(people.Select(p => p.Label)).FirstOrDefault(t => t.Length > LongestTaskLabel) is { } longest)
        {
            return $"'{longest[..40]}…' is longer than {LongestTaskLabel} characters, the most a list entry may be.";
        }

        if (types.GroupBy(t => t, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            return $"'{twice.Key}' is given twice as a task type.";
        }

        if (people.GroupBy(p => p.Username, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } same)
        {
            return $"'{same.Key}' is given twice as an assignee.";
        }

        return people.FirstOrDefault(p => p.Username.Length > LongestTaskLabel || p.Username.Contains('\'', StringComparison.Ordinal))
            is { Username.Length: > 0 } odd
                ? $"'{odd.Username}' cannot be an account name."
                : null;
    }

    /// <summary>The feature layers of a web map document: each layer's id, title and address.</summary>
    internal static IEnumerable<(string Id, string Title, string? Url)> OperationalFeatureLayers(string? document)
    {
        if (string.IsNullOrEmpty(document))
        {
            yield break;
        }

        JsonDocument parsed;

        try
        {
            parsed = JsonDocument.Parse(document);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (parsed)
        {
            if (!parsed.RootElement.TryGetProperty("operationalLayers", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (JsonElement layer in Flatten(list))
            {
                if (layer.TryGetProperty("layerType", out JsonElement kind) && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() == "ArcGISFeatureLayer"
                    && layer.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                {
                    string? url = layer.TryGetProperty("url", out JsonElement u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    string title = layer.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()!
                        : id.GetString()!;

                    yield return (id.GetString()!, title, url);
                }
            }
        }

        static IEnumerable<JsonElement> Flatten(JsonElement list)
        {
            foreach (JsonElement layer in list.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.Object))
            {
                yield return layer;

                if (layer.TryGetProperty("layers", out JsonElement inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement child in Flatten(inner))
                    {
                        yield return child;
                    }
                }
            }
        }
    }

    /// <summary>The hosted layer of this server an address names, or why it cannot hold tasks.</summary>
    private static async Task<(PublishedLayer? Layer, string? Why)> TaskLayerOfAsync(
        HttpContext context, PostgresLayerCatalog layers, string? url, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (null, "it has no address.");
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? absolute)
            && !string.Equals(absolute.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase))
        {
            return (null, "it is another server's layer, and only a hosted layer of this server can be given task fields here.");
        }

        int at = url.IndexOf("/rest/services/", StringComparison.OrdinalIgnoreCase);
        string[] path = at < 0 ? [] : url[(at + "/rest/services/".Length)..].Split('?')[0].Trim('/').Split('/');
        int face = Array.IndexOf(path, "FeatureServer");

        if (face < 1 || face + 1 >= path.Length || !int.TryParse(path[face + 1], out int index))
        {
            return (null, "its address does not name one layer of a feature service.");
        }

        PublishedService? service = await layers.FindServiceAsync(face == 2 ? path[0] : null, path[face - 1], cancellation)
            .ConfigureAwait(false);

        if (service is null || !string.Equals(service.Folder, "hosted", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "it is not a hosted layer; tasks need fields added, and a registered table is not changed from here.");
        }

        if (service.ViewOf is not null)
        {
            return (null, "it is a view, whose fields are its source layer's.");
        }

        if (service.Layers.FirstOrDefault(l => l.LayerIndex == index) is not { } layer)
        {
            return (null, "its service has no layer by that number.");
        }

        return layer.GeometryType is GeometryKind.MultiPoint or null
            ? (null, "Field Maps puts tasks on points, lines and polygons, and this layer is not one of them.")
            : (layer, null);
    }

    private static bool IsTaskField(string column) =>
        FieldMapsTasks.Fields.Any(f => string.Equals(f.Name, column, StringComparison.Ordinal));

    private static bool Fits(FieldType wanted, FieldType there) => wanted switch
    {
        FieldType.Integer => there is FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger,
        _ => there == wanted,
    };
}

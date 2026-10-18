using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Every item's terms of use and categories, held for the portal's documents — ADR-202.
/// </summary>
/// <remarks>
/// <para>
/// <b>Held, because the documents are built in five places from five models</b> — a service, a coverage, a basemap, a
/// web map and a document — none of which carries these, and threading two fields through every one of them for what
/// a few items have is the larger change. Read whole at start, again after every write here, and once a minute, so a
/// second node's write reaches this one within that minute.
/// </para>
/// <para>
/// <b>A category is named by its path</b>, as ArcGIS's are: <c>/Categories/Water/Rivers</c>, under the one root its
/// <c>categorySchema</c> answers.
/// </para>
/// </remarks>
internal sealed partial class ItemAnnotations(IItemAnnotationStore store, ILogger<ItemAnnotations> log) : BackgroundService
{
    /// <summary>The root every category path starts under.</summary>
    public const string Root = "Categories";

    private static readonly ItemAnnotationSnapshot Empty = new(
        new Dictionary<Guid, string>(), new Dictionary<Guid, IReadOnlyList<Guid>>(), []);

    private volatile ItemAnnotationSnapshot _held = Empty;

    // <b>Only the newest read is kept.</b> A read that began before a write and ended after the read that write started
    // would put the old terms back; each read takes a number before it starts, and one older than the last kept is dropped.
    private readonly object _publish = new();
    private long _started;
    private long _kept;
    private volatile Dictionary<Guid, string> _paths = [];

    /// <summary>An item's terms of use, or null.</summary>
    public string? LicenseOf(Guid item) => _held.Licenses.TryGetValue(item, out string? text) ? text : null;

    /// <summary>An item's categories, as paths.</summary>
    public string[] CategoriesOf(Guid item)
    {
        Dictionary<Guid, string> paths = _paths;
        return _held.Categories.TryGetValue(item, out IReadOnlyList<Guid>? ids)
            ? [.. ids.Select(id => paths.TryGetValue(id, out string? path) ? path : null).OfType<string>().Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>The tree as held.</summary>
    public IReadOnlyList<ItemCategory> Tree => _held.Tree;

    /// <summary>A category's path, or null when there is none by that id.</summary>
    public string? PathOf(Guid category) => _paths.TryGetValue(category, out string? path) ? path : null;

    /// <summary>A category's id, by its path (with or without the root) or by its id; null when none.</summary>
    public Guid? Resolve(string named)
    {
        if (Guid.TryParse(named, out Guid id))
        {
            return _paths.ContainsKey(id) ? id : null;
        }

        string wanted = named.Trim().TrimEnd('/');
        wanted = wanted.StartsWith('/') ? wanted : $"/{Root}/{wanted}";

        foreach ((Guid key, string path) in _paths)
        {
            if (string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>The category schema as ArcGIS's portal answers it: one root, its children nested; empty when none.</summary>
    public object[] Schema()
    {
        IReadOnlyList<ItemCategory> tree = _held.Tree;

        if (tree.Count == 0)
        {
            return [];
        }

        object[] Children(Guid? parent) =>
            [.. tree.Where(c => c.ParentId == parent).Select(c => (object)new { title = c.Name, categories = Children(c.Id) })];

        return [new { title = Root, categories = Children(null) }];
    }

    /// <summary>Reads everything again.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        long ticket = Interlocked.Increment(ref _started);
        ItemAnnotationSnapshot read = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<Guid, ItemCategory> byId = read.Tree.ToDictionary(c => c.Id);
        Dictionary<Guid, string> paths = [];

        foreach (ItemCategory category in read.Tree)
        {
            List<string> names = [];

            for (ItemCategory? at = category; at is not null && names.Count <= byId.Count;
                 at = at.ParentId is { } up && byId.TryGetValue(up, out ItemCategory? parent) ? parent : null)
            {
                names.Insert(0, at.Name);
            }

            paths[category.Id] = $"/{Root}/{string.Join('/', names)}";
        }

        lock (_publish)
        {
            if (ticket < _kept)
            {
                return;
            }

            _kept = ticket;
            _paths = paths;
            _held = read;
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // What was held stays held: terms and categories a minute old are better than none.
                LogRefreshFailed(log, e);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(EventId = 2353, Level = LogLevel.Warning, Message = "The items' terms and categories could not be read again; the last ones read stay in use.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);
}

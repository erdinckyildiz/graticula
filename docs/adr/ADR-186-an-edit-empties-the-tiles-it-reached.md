# ADR-186 — An edit empties the tiles it reached, not the whole layer

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-10-15, by owner decision. Asked about the structural items [ADR-101](ADR-101-studio-is-walked-the-way-a-portal-user-walks-it.md) condition 4 lists, the owner answered *"Vaktimiz var. Hepsini."* — this is the item *tile-level invalidation instead of whole-layer purge*. |
| **Supersedes** | — (amends [ADR-069](ADR-069-query-responses-carry-the-layer-s-cache-lifetime.md)'s edit purge: a located edit empties by address) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Since 2026-09-15 a kept edit empties its layer's cached tiles (`TilePurgingWriter`, and `ApplyTogetherAsync` for a
service-level `applyEdits`). It empties all of them: one point moved in Ankara threw away a pyramid seeded for the whole
country, and the next viewer anywhere rebuilt it tile by tile from the datastore. On a layer edited through the day that
is a cache that is cold most of the time, which is what the seeding work ([ADR-093](ADR-093-seeding-the-tile-cache.md))
exists to prevent.

An edit knows where it was. The features it updates or deletes were somewhere before it, and the features it adds or
updates are somewhere after it; the tiles that can have changed are the ones over those two places.

## 2. Alternatives considered

### Alternative A — read the touched features' extent from the store before and after, and purge by address *(chosen)*

**Argument for.** The store is the one place that knows where a feature was before an update moved it, and asking it in
the tiling scheme's own reference (`st_transform` to the scheme's SRID) means nothing here projects anything. The
answer is two rectangles; the cache keeps every tile outside them.

**Argument against.** Two extra queries per edit, and a rectangle is coarse: a feature moved from one corner of the
country to the other empties the tiles over everything between at the levels where those overlap.

### Alternative B — compute the tiles from the edit's own geometries

**Argument for.** No extra query for adds.

**Argument against.** It does not know where an updated or deleted feature *was*, which is half of the answer; and it
would project client geometry in the host, which the store already does. Not chosen.

### Alternative C — keep the whole-layer purge

**Argument for.** Nothing can be missed.

**Argument against.** A cold cache after every edit. Not chosen; it stays as the answer whenever the extent is unknown.

## 3. Counterarguments to the preferred option

- **A tile shows features from beyond its own box** — the buffer at its edge, and at a generalised level a feature drawn
  over neighbours it does not touch. The test grows each tile by one whole tile on every side before asking whether the
  edit's rectangle reaches it; a tile's span is more than either.
- **A delete that fails leaves a file the read path serves** (the read is one `File.Exists`, N2). The first failed delete
  turns the purge into the whole-layer purge, whose rename and stamp are what hold
  (`FileSystemTileCache.Purge(Guid, Func<TileAddress, bool>)`).
- **A view of the edited rows ([ADR-113](ADR-113-a-hosted-layer-has-views.md)) may be cut on another
  scheme**, which the extent was not read in. Every layer showing the rows other than the edited one is still emptied
  whole.
- **Anything that cannot be read is the old answer.** No scheme, a store that cannot say (`IEditedExtent` not
  implemented), or the store refusing — a point outside the scheme's domain — is an unknown extent, and an unknown
  extent empties the whole layer.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A delete empties the tile over the feature and keeps a tile on the far side of the world | `AnEditEmptiesTheTileCacheTests` — the far tile is a HIT after the delete | local fixture, 2026-10-15 |
| That assertion is the mechanism's, not the fixture's | Same test with the scheme resolver made to find nothing: fails, *far tile … was not a HIT after the delete* | local fixture, 2026-10-15 (control) |
| A moved feature empties where it was and where it is, and nothing between | `TilePurgingWriterTests.A_located_edit_empties_the_tiles_it_reached_and_keeps_the_rest` | unit |
| An unknown extent, and a view, are emptied whole | `TilePurgingWriterTests.An_edit_whose_extent_cannot_be_read_empties_the_whole_layer`, `A_view_of_the_edited_rows_is_emptied_whole` | unit |
| The cache drops by address across fingerprints and no other layer | `FileSystemTileCacheTests.Purging_by_address_drops_only_the_addresses_it_names` | unit |

## 5. Decision

5.1 **`ITileCache.Purge(Guid, Func<TileAddress, bool>)`** drops a layer's cached tiles at the addresses the test accepts,
for every fingerprint the layer has. It has no default body: an implementer that could not tell addresses apart would
answer by purging nothing.

5.2 **`IEditedExtent`**, implemented by `PostGisFeatureWriter`, gives the extent of a set of features in a reference —
empty when none has a geometry, null when it cannot be read.

5.3 **An edit reads the extent of what it updates and deletes before it, and of what it adds and updates after it**, in
the layer's service's tiling scheme, and empties the edited layer's tiles that **`EditedTiles.Reached`** accepts: the
tile's box grown by one tile on every side meets the union of the two. An address the scheme does not have is accepted.

5.4 **The whole layer is emptied** when the scheme or either extent is unknown, for every view of the rows, and when a
file could not be deleted. Every other reason to purge — unpublishing, sharing, schema or scheme changes — empties the
whole layer as before.

## 6. Consequences

**Positive.** A seeded pyramid survives a day of edits everywhere but where they were.

**Negative.** Two queries per kept edit, on the store the edit already wrote to.

**Ports created.** `IEditedExtent` (core). **State.** None.

## 7. Assumptions this decision rests on

None new.

## 8. Dependencies

**Depends on:** [ADR-069](ADR-069-query-responses-carry-the-layer-s-cache-lifetime.md),
[ADR-093](ADR-093-seeding-the-tile-cache.md), [ADR-113](ADR-113-a-hosted-layer-has-views.md).

**Depended on by:** —

## 9. Revisit triggers

- A tile shown stale after an edit near it — the one-tile margin is then too small for some generalisation.
- A GeoParquet or other read-only source becoming editable: it implements `IEditedExtent` or is emptied whole.

## 10. Dissent

None recorded.

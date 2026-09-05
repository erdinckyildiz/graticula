# ADR-056 — An item is its own thing, and a service is one kind of it

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` for the model · `MEDIUM` for which fields move onto the item |
| **Decided** | 2026-09-05, by owner decision |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

[ADR-034](ADR-034-server-and-studio.md) split the console into two surfaces and gave Studio
*My content*: what I own and what is shared with me. What it did not settle is what a piece
of content **is**, and the implementation answered that by not answering it.

`GET /content/items` is a projection of published services. One loop, over
`PublishedService`, one `items.Add`. Each row comes back with a name, a folder, a kind, a
description, an owner, a sharing scope and a status, counted into *mine*, *group*,
*organization*, *public* and *administrative*. There is no item record anywhere; there is a
service, and a way of listing services that calls them items.

The owner stated the model on 2026-09-05, while the Publish screen was being designed:

> **Server'da publish edilen her servisin bir Studio item'i var ama her Studio item'in bir
> servis ilişkisi yok. Icon da olabilir başka bir şey de.**

Every published service has an item; not every item has a service. An item may be an icon,
a document, something with nothing served behind it at all.

**The gap is not hypothetical and it is not in the future.** A coverage —
[ADR-043](ADR-043-imageserver-and-the-raster-face.md)'s image service — is published, has a folder, a name,
an owner, a sharing scope and a status, and appears in **no** content listing, because
neither `/content/items` nor `/content/layers` reads the coverage catalogue at all. Somebody
who publishes a raster on this server cannot find it in their own content. Asked about it,
the owner: *ImageServer'ın da Studio'da bir item'i olmalı. Mantıklı olan o.*

## 2. Alternatives considered

### Alternative A — Union the projections: list coverages beside services

**Argument for.** It is the smallest change that fixes the visible defect. A second loop in
`ListContentItemsAsync` over the coverage catalogue, projected into the same shape, and a
raster appears in My content this afternoon.

**Argument against.** It fixes the symptom and entrenches the cause. The third kind — an
icon, a document, a style, a layer definition — needs a third loop, a third projection and a
third place where paging, counting and the sharing filter are re-derived. The counts are
already computed in that method; three sources means three ways for them to disagree, and
the one that is wrong will be the one nobody looked at.

### Alternative B — An item is a record, and a service is one kind of item

**Argument for.** It is the model the owner stated, and it is the one where *not every item
has a service* is expressible rather than a special case. One table with an identity, a
kind, an owner, a sharing scope, a folder and a nullable reference to whatever it is *of*.
Listing, counting, paging, searching and sharing are written once, against items, and a new
kind is a row rather than a branch.

**Argument against.** It is a migration, and it moves ownership and sharing — which are
today read off the service and evaluated per layer — onto something above them. Two places
that both claim to know who may see a thing is worse than one place that is in the wrong
place.

### Alternative C — Make everything a service

**Argument for.** No new table. An icon becomes a degenerate service; the catalogue already
knows how to own, share and fold one.

**Argument against.** A service is a thing that answers requests, has faces, capabilities,
a status and cost ceilings. An icon has none of those, and giving it all of them so it can
be listed makes *stopped* and *Query,Create* meaningful properties of a PNG. It is
Alternative A's problem wearing the other hat: instead of three ways to list one concept,
one concept carrying three sets of fields that are null for most of it.

## 3. Counterarguments to the preferred option

**Nothing needs an icon today, so this is a table for a feature nobody has asked for.**
Two things need it today. A coverage is published and unfindable, which is a defect rather
than a wish; and the Publish screen collects a summary and *who can see it*, which read like
an item's properties and currently have to be written onto a service row. The icon is the
third case, and it is the one that makes the shape obvious rather than the one that
justifies it.

**Sharing belongs to the layer, and this puts it two levels up.** It does not move it —
this decision does not decide where sharing lives, and says so in §5. Today
`LayerAccess.Evaluate` reads the service's scope; whether an item's scope replaces that,
overrides it or simply mirrors it is left open on purpose, because getting it wrong makes
something visible to somebody it was not shared with. That is §7's first condition.

**A nullable foreign key is a design smell.** It is, when the null means *not filled in
yet*. Here it means *this item is not of anything*, which is the whole content of the
owner's sentence. An icon with a null service is not an incomplete service.

## 4. Evidence

Read from the code on 2026-09-05.

| | |
|---|---|
| Sources `/content/items` draws from | **1** — `foreach (PublishedService service in served)` |
| `items.Add` calls in that method | **1** |
| Scopes it counts | 5 — mine, group, organization, public, administrative |
| Content endpoints that read the coverage catalogue | **0** |
| Fields a `PublishedCoverage` already carries | `Folder`, `Name`, `Sharing`, `Status`, `Owner` |

The last row is the argument in miniature. A coverage is **already item-shaped** — it has
every field an item needs and it is not one, because being one is not a thing you can be.
What is missing is not data; it is a common identity.

## 5. Decision

**An item is a record of its own.** It carries an id, a kind, a title, a folder, an owner, a
sharing scope, a description and a nullable reference to the thing it is *of*.

**A service is one kind of item, and so is a coverage.** Publishing either creates the item
in the same act; removing either removes it. An item whose reference is null is an item
that is not of anything — an icon, a document — and is the case the model exists to allow.

**One listing.** `/content/items` reads items. Paging, counting, the sharing filter and the
search are written against that one source, and a new kind adds no branch to any of them.

**Where the fields live is deliberately not decided here.** A summary and a sharing scope
read like an item's; sharing is evaluated per layer today, through the service. This ADR
introduces the item and leaves the move as §7's first condition, because a wrong answer
there makes something visible to somebody it was not shared with — and that is not a
mistake a migration should be allowed to make quietly.

## 6. Consequences

- **A coverage becomes findable by whoever published it**, which it is not today. That is
  the defect this decision closes on the way past.
- **Tags become possible.** A service has no tag column and this study refused to draw the
  control for one; on an item they are obvious, and the Publish screen can ask for them.
- **State.** This adds a table. One row per published thing, holding identity, kind,
  ownership, folder and sharing, plus a nullable link into the service or coverage
  catalogue. It is new shared state and it is the point of the decision rather than a side
  effect of it: what exists today is two catalogues and no common record of what a person
  owns.
- **The migration backfills.** Every existing service and every existing coverage gets an
  item at the version that introduces the table, or My content empties on upgrade.
- **Publish writes two rows rather than one**, in one transaction. A service with no item
  is invisible in Studio and an item with a dangling reference is a listing that cannot be
  opened, so neither is allowed to exist alone.

## 7. Conditions

1. **Where ownership and sharing live is settled before the table is written, not after.**
   The item carries a scope in §5's sketch and the service carries one today, and two
   places that both answer *who may see this* is the failure mode this decision would
   otherwise introduce. The answer has to be one of: the item's replaces the service's, the
   service's stays and the item mirrors it, or the item's is advisory. Until it is chosen,
   the confidence on the field layout stays `MEDIUM`.
2. **The first kind with no service behind it is built before the model is called proved.**
   An icon or a document, listed, owned, shared and opened. Coverages and services both have
   something behind them, so a table that only ever holds those two has not yet demonstrated
   the sentence it was built for.

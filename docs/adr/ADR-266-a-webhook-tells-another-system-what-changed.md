# ADR-266 — A webhook tells another system what changed

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the two surfaces follow ArcGIS's public REST documentation, and the delivery semantics are the outbox this repository already runs for jobs (ADR-011 §3.2); which events fire, what replaces `changesUrl`, who may make each kind and the private-network setting are this session's, and five of them are `INFERRED` (§5.10) |
| **Decided** | 2026-11-04, overnight, on the owner's instruction to build the ArcGIS veteran's review item #15 (*webhooks — organisation and feature service*). The owner was asleep; every choice the owner has not made is labelled `INFERRED` and listed in §5.10. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

ArcGIS has two kinds of webhook, and the integrations an ArcGIS shop already runs — Power Automate, Make, Zapier, FME,
an integration server of its own — use both:

- **An organisation's webhook** (Portal Directory, `portals/{id}/webhooks`, since 10.7): `createWebhook` with `name`,
  `url`, `secret`, `config` (a `deactivationPolicy` of `numberOfFailures` within `daysInPast`, and `properties`, where a
  notebook server's `federatedServer` goes), `changes` (`manualChanges` or `allChanges`) and `events`, a list of event
  URIs such as `/items/add`, `/items/<itemID>/update`, `/groups/addUsers` or `/users/<username>/delete`. Each webhook has
  `update`, `delete`, `activate`, `deactivate` and `notificationStatus`. The payload is
  `{info: {webhookId, webhookName, portalURL, when}, properties, events: [{id, operation, source, userId, username, when,
  properties}]}`.
- **A feature service's webhook** (`.../FeatureServer/WebHooks`, at the service's administration): `create` with
  `name`, `hookUrl`, `changeTypes` (`FeaturesCreated`, `FeaturesUpdated`, `FeaturesDeleted`, `FeaturesEdited`,
  `AttachmentsCreated`/`Updated`/`Deleted`, `LayerSchemaChanged`, `LayerDefinitionChanged`,
  `FeatureServiceDefinitionChanged`, or `*`), `signatureKey`, `active` (false unless said), `contentType`,
  `payloadFormat` and `scheduleInfo`; at most ten per service; `edit`, `delete`, `deleteAll`. The payload is an array of
  `{name, layerId, orgId, serviceName, lastUpdatedTime, changesUrl, events}`, where `changesUrl` is an `extractChanges`
  request against the service's change tracking.

Both sign a payload with HMAC-SHA256 of the body under the secret, in `x-esriHook-Signature: sha256=<hex>`.

Graticula had neither. A webhook is also the first thing this server would fetch **on an administrator's word, from
inside the deployment's network, and report on** — the shape of a server-side request forgery — so where it may post
matters more than any field of the payload.

Sources, all public: *Webhooks*, *Create Webhook*, *Webhook settings* (Portal Directory, developers.arcgis.com/rest/
users-groups-and-items); *Create (Feature Service webhooks)* and *Webhooks — Payloads (Feature Service)*
(developers.arcgis.com/rest/services-reference/online); *Feature layer webhook payloads* (doc.arcgis.com, ArcGIS
Online); *Create and manage webhooks* (Portal for ArcGIS help). No ArcGIS install file was read.

## 2. Alternatives considered

### Alternative A — Leave webhooks out

An integration polls instead. Every one of the integrations named above is built on the push, and a poll of a portal's
search for what changed has no way to see a delete. Not chosen.

### Alternative B — Send in the request that raised the event

Simplest: the request that added the item posts to the receiver before it answers. A slow receiver then slows the
portal, a receiver that is down fails nothing and is never told again, and a restart loses everything in flight. Not
chosen.

### Alternative C — A queue in memory per node

Fast and simple, and it loses whatever is queued at a restart, and on a site of several nodes each node sends only its
own. Not chosen.

### Alternative D — A durable outbox in the platform store, claimed with `skip locked` *(chosen)*

Each event is a row from the moment it is raised; any node's sender claims due rows with `for update skip locked` and a
lease, as a job is claimed (ADR-011 §3.2, D-243). A restart loses nothing; a site sends each delivery from one node at a
time; a node that dies mid-send leaves a lease that lapses, and another node sends it again.

### Alternative E — Feature-service events from database triggers

A trigger on each hosted table would see an edit made by any client, including SQL. It would also put a write to the
platform store inside every edit's transaction on the data store, which is a different database for a registered
layer, and nothing else in this server edits a hosted table outside its own writers. Not chosen; the writers are where
every edit this server makes already passes (§5.2).

## 3. Counterarguments to the preferred option

- **The audit log as the source of portal events couples two things.** An act that forgets its audit row raises no event
  either. The answer is that an act that forgets its audit row is already a defect (ADR-017 §5d), and the webhook makes
  it a visible one; the alternative — a second call at every act — is the propagation failure D-130 records, in forty
  places.
- **Raising after the act commits is not atomic with it.** A process killed between an item's insert and its audit row
  loses the event. The audit row is written in the same request, not the same transaction, and that window already
  exists for the audit row itself. At-least-once here means *from the moment the event is a row*; recorded in §6.
- **Per-attempt failure counting is harsher than per-delivery.** One receiver outage of an hour, with three retries,
  counts as four failures. ArcGIS documents `numberOfFailures` without saying which it counts; counting attempts deactivates
  sooner, which is the safer side of the two for a URL that is not answering.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Both surfaces, their parameters and their payloads | The public pages named in §1 | developers.arcgis.com, doc.arcgis.com |
| An organisation's webhook fires on an item add and is signed so a receiver can check it | `WebhooksTests.An_organizations_webhook_posts_an_item_add_signed_with_its_secret` — a listener inside the test on 127.0.0.1 receives the `/items/add` payload, and its `x-esriHook-Signature` equals the HMAC the test computes | tests/Graticula.Conformance.Tests |
| A feature service's webhook fires on an add, carries the object id, and does not fire on a type it did not ask for | `WebhooksTests.A_feature_services_webhook_posts_an_edit_with_its_object_ids_and_signature` | same |
| Deactivation after the policy's failures, and activation again | `WebhooksTests.A_webhook_deactivates_after_the_failures_its_policy_allows`; `WebhookOutboxTests.A_webhook_is_deactivated_when_its_failures_reach_its_policy_and_what_waited_is_dropped` | Conformance, Platform.Postgres |
| A non-administrator, and a member who does not own the service, is refused | `WebhooksTests.A_member_who_is_not_an_administrator_or_the_owner_is_refused` | Conformance |
| Two senders claiming at once take disjoint deliveries and all of them between them; a lapsed lease is claimed again and the first sender's late answer is ignored | `WebhookOutboxTests.Two_senders_claiming_at_once_…`, `A_sender_that_dies_loses_its_claim_…` | Platform.Postgres |
| Loopback, private, link-local, IPv4-mapped and a name resolving to any of them are refused; the connection itself refuses a loopback address | `AWebhookReachesOnlyWhatItMayTests` | tests/Graticula.Host.Tests |

## 5. Decision

### 5.1 Two kinds, one table

`webhook` (migration 130) holds both kinds — `portal` and `featureService` — with the service a feature-service webhook
watches and its number within it, its URL, its events or change types, its sealed secret, whether it is active, its
owner, its deactivation policy, its content type and format, its retries, its `config.properties`, the server's address
as its maker reached it (which payloads name), and why the server deactivated it. `webhook_delivery` is the outbox. Both
carry `tenant_id` under the forced `tenant_floor` policy, as every organisation table does (ADR-204), and go with the
owner (`on delete cascade`) and with the service.

### 5.2 Which events this server raises, and where

- **Portal events are audited acts.** The audit log is decorated (`WebhookAuditLog`): after the row is written, the act's
  audit action is looked up in `WebhookEvents.PortalActs` and, when it is one of ArcGIS's events and it succeeded, the
  event is queued. Items: add (`service.publish`, `service.view`, `document.create` — apps and links included —
  `webmap.create`), publish (`toolbox.publish`), update, delete, share, reassign, protect/unprotect. Groups: add, update,
  delete, addUsers (`group.member`, `group.join`), removeUsers (`group.member.remove`, `group.leave`), itemShare,
  itemUnshare. Users: add, delete, disable, enable, update, updateUserRole, updateUserLicenseType. Roles: add, update,
  delete. **Not raised:** sign-in and sign-out (not audited as acts; a webhook on every sign-in is a second
  authentication log), comments (this server has none), `requestJoin`, `invite`, `updateUsers`, `move`, `bulkEnable`,
  `bulkDisable`. Each is accepted in `events`, and never fires.
- **An event's id** is ArcGIS's: an item's id (a service's own id, a document's, a web map's) and a group's id in 32
  hexadecimal digits, a user's username, a role's name. A service and a group are named by name in their audit rows and
  are looked up in the act's organisation; a deleted one has no id left, and its event fires under `/items/delete` but
  not under `/items/<id>/delete`.
- **Feature-service events are edits that kept something**, told by every writer `LayerConnections.WriterFor` hands out
  (`EditTellingWriter`) and by the multi-layer transaction (`ApplyTogetherAsync`), after commit — so applyEdits,
  addFeatures, updateFeatures, deleteFeatures, OGC API Features writes and WFS-T all raise them, on hosted and
  registered layers alike. **Raised:** FeaturesCreated, FeaturesUpdated, FeaturesDeleted, FeaturesEdited. **Accepted
  and not raised:** the attachment, schema and definition types; a webhook's document lists `changeTypesRaised`.

### 5.3 What a feature-service payload carries instead of `changesUrl`

ArcGIS's `changesUrl` names an `extractChanges` request against server generations, which needs sync and change
tracking this server does not have (ADR-082). **The payload carries what that request would return the keys of** —
`objectIds: {adds, updates, deletes}`, at most 1,000 of each with `truncated` when there were more — and `layerUrl`, and
`queryUrl`, a `query` for the added and updated ids when there are at most a hundred. `changesUrl` is absent rather than
pointed at something that answers differently. (`INFERRED`, §5.10 Q2.)

### 5.4 Where a webhook may post — the SSRF rule

- **HTTPS, no user name or password in the URL, and no private, loopback, link-local, unique-local, shared-address,
  benchmarking or multicast address** — the rule `RemoteGeoParquetLocations.IsPrivate` already keeps for remote
  GeoParquet (ADR-067 §5.2), IPv4 inside IPv6 judged as IPv4.
- **Checked at creation and at every connection.** `WebhookTargets.Handler` resolves the name in its `ConnectCallback`,
  refuses when **any** address it answers is refused, and connects to the addresses it checked, so a name that moves
  inward after the webhook was made is refused at the moment of connecting, and there is no gap between check and use.
- **No redirect is followed** (`AllowAutoRedirect = false`): a 3xx is a failed delivery, said so in the log. No proxy,
  no cookies.
- **Bounded:** ten seconds to connect, fifteen for the whole delivery to its status line; response headers at most 16
  KB; the body is never read.
- **`Graticula:WebhooksAllowPrivate`, off by default**, allows a private address — an integration server beside this
  one — and, with it, plain HTTP to such an address and only to one. (`INFERRED`, §5.10 Q3.)

### 5.5 Delivery

- **At least once.** An event is a row from the moment it is raised; every node runs a sender (`WebhookDeliverer`) that
  claims up to eight due rows with `for update skip locked` and a two-minute lease, sends them, and records the
  outcome only if it still holds the claim. A delivery carries `X-Graticula-Delivery` (and `deliveryId` in its body) so a
  receiver can drop a duplicate.
- **Retries with backoff:** ten seconds, a minute, five minutes, thirty, two hours; `maxRetries` (0 to 5, three unless
  said) after the first attempt; then the delivery is failed.
- **A bounded queue:** a webhook holding 1,000 undelivered events records the next as `dropped` with the reason, rather
  than queueing it. Finished deliveries are kept seven days and at most the newest 200 per webhook.
- **One event per delivery**; `scheduleInfo` is kept and not honoured (§5.10 Q4).
- **`application/json`**, or ArcGIS's form `payload=<json>` when `contentType` says so; `pjson` is indented.

### 5.6 The secret

Sealed with the deployment's key (`SecretProtector`, as an identity provider's client secret is), never returned —
a webhook's document says `"secret": ""` (or `"signatureKey": ""`) and `hasSecret` — and unsealed only by the sender to
sign. **The signature is `sha256=` and the hex HMAC-SHA256 of the exact bytes posted**, in `x-esriHook-Signature`, for
both kinds. An audit row of a webhook act records the receiver's host, never its URL, whose query may carry a key (a
Power Automate URL does).

### 5.7 Who may make them, and who may be told

- **An organisation's webhook:** whoever holds `admin:manageSecurity` **and** `admin:viewAllContent` — an administrator
  who may already see every item, group and member the webhook will describe (§5.10 Q1).
- **A feature service's webhook:** the service's owner, or whoever manages it — the door `updateDefinition` already uses
  (`ManagedServiceAsync`).
- **Asked again at every delivery.** The sender reads the owner's user type, roles, whether they are disabled, and —
  for a feature-service webhook — whether they still own the service, and drops the delivery when the owner may no
  longer be told: a portal webhook's owner without both privileges, a feature-service webhook's owner who neither owns
  the service nor holds `admin:manageAllContent`, an owner disabled or gone.
- **An organisation's events reach only its own webhooks**: the queueing statement matches the event's organisation
  (the request's, or the default organisation where none is in scope), under the floor as well.
- **A feature-service payload names which features changed, never what they now hold**; reading them is a `query`
  under the reader's own access.

### 5.8 Deactivation

ArcGIS's `deactivationPolicy`: a webhook whose **failed attempts** within `daysInPast` reach `numberOfFailures` (five and
five unless said; a feature-service webhook takes the default) is deactivated, with the reason kept, and its waiting
deliveries are dropped. `activate` turns it on again and clears the reason; a webhook deactivated by hand sends nothing
that was raised while it was off.

### 5.9 The routes

- `sharing/rest/portals/self/webhooks` (list), `/createWebhook`, `/{id}`, `/{id}/update`, `/{id}/delete`,
  `/{id}/activate`, `/{id}/deactivate`, `/{id}/notificationStatus` (the newest deliveries: state, receiver's status,
  attempts, error — never the payload).
- `rest/admin/services[/{folder}]/{name}/FeatureServer/webhooks` and `{name}.FeatureServer/webhooks` (list), `/create`
  (and `/createWebhook`), `/{n}`, `/{n}/edit` (and `/update`), `/{n}/delete`, `/deleteAll`, `/{n}/notificationStatus`.

### 5.10 `INFERRED`, for the owner to confirm

- **Q1 — Who makes an organisation's webhook.** `admin:manageSecurity` and `admin:viewAllContent`. Alternative: any
  administrator role, or a new privilege `admin:manageWebhooks`.
- **Q2 — `changesUrl`.** Absent; `objectIds`, `layerUrl` and `queryUrl` in its place. Alternative: `changesUrl` pointing
  at the `query`, which a client that only follows the link would read, and one that parses `extractChanges` would
  misread.
- **Q3 — The private-network setting** and plain HTTP to a private receiver with it on. Alternative: HTTPS always, and a
  receiver on the private network must present a certificate this server trusts.
- **Q4 — `scheduleInfo`** kept and not honoured; one event per delivery. Alternative: gather a webhook's events over its
  interval into one payload, as ArcGIS's feature-service webhooks do.
- **Q5 — Failures counted per attempt**, not per delivery, against `numberOfFailures`.

## 6. Consequences

- **State.** Two tables in the platform store (migration 130): `webhook` and `webhook_delivery`, both floored per
  organisation; the outbox is bounded per webhook (1,000 waiting) and pruned (seven days, 200 finished per webhook).
  At runtime: one sender per node, holding an `HttpClient`, a semaphore and a pool of two connections of its own,
  `graticula-jobs-webhooks` — a poller claims on its own pool, not the request pool (D-110, `PollerPoolTests`, which
  the first CI run of this branch failed on); and, since condition 5, a per-node memory of which services have a
  webhook (`WebhookedServices`, at most 10,000 entries, forgotten on every webhook write and announced).
- This server now makes outbound HTTP requests when, and only when, an administrator has made a webhook and an event it
  listens for happens. An air-gapped deployment that makes none sends nothing; `tools/registers-check.py` names the two
  files that may.
- An event raised in a request whose process dies before the event is queued is lost; from the moment it is a row it is
  delivered at least once.
- A portal event costs one more statement per audited act — an insert that selects the organisation's matching active
  webhooks, which is none on a deployment without any — and a service's or a group's act one more, to find its id. **An
  edit that kept something costs one more statement on the platform store per layer edited**, after the edit's own
  commit, on every deployment, webhooks or none. ~~Not measured; a cache of which services have webhooks would remove it
  and would need to be told across nodes when one is made, which is why it was not built the first night.~~ Measured on
  2026-11-04 at about 3 ms, and removed by condition 5's cache: one question per service per node, not one statement per
  edit.

## 7. Conditions

1. **The events §5.2 accepts and does not raise** — attachments, layer schema and definition, sign-in, comments — are
   raised, or a webhook naming only those is refused at creation with the reason. Until then the document's
   `changeTypesRaised` is the only place a caller learns it.
2. **A Studio screen under Organization** lists the organisation's webhooks with create, activate, deactivate, delete
   and the recent deliveries, and a feature service's page lists its own. Not built this night; the REST API came first.
3. **The five `INFERRED` choices of §5.10** are put to the owner and either confirmed or changed. **DISCHARGED
   2026-11-04, by owner decision** (*"Hepsi önerildiği gibi"*): all five confirmed as built — the two privileges for an
   organisation's webhook, objectIds in place of `changesUrl`, the private-network setting with plain HTTP only to a
   private address, `scheduleInfo` kept but not honoured, and failures counted per attempt.
4. **A real integration** — Power Automate's or Make's ArcGIS connector, or the ArcGIS API for Python's
   `gis.admin.webhooks` — is pointed at this server and its result recorded. **DISCHARGED 2026-11-04** with the ArcGIS API
   for Python 2.4.3 against the local fixture, signed in by name and password. It found three gaps, each fixed the same
   day: `list()` follows `nextStart` and raised a KeyError on the bare array the list answered (now paged as Portal pages
   it); `Webhook.notifications` reads its rows under `WebhookStatus` and follows `nextStart` (now there, beside this
   server's `notifications`); and `settings` read `webhooks/settings`, a 404 (now the three numbers the sender actually
   uses: 4 attempts, 15 s, 10 s to the first retry). After them, every member of its `WebhookManager` and `Webhook` ran:
   `create` with a secret, `list`, `get`, an item added through `gis.content` delivered to a receiver with its
   `x-esriHook-Signature`, `deactivate`, `activate`, `update` of name and events, `notifications`, `settings`, `delete`.
   `settings` assigned is refused in words — **INFERRED**: the retry ladder is the server's, and making it an
   organisation's setting is a decision of its own. Power Automate and Make were not tried; they need accounts this
   session does not hold. The shapes are in `WebhooksTests`.
5. **The edit path's extra statement (§6) is measured**: `applyEdits` of one feature, p50 and p95, with and without
   the webhook hook, on the fixture; if it adds more than a tenth to p50, the services that have webhooks are cached per
   node and the cache told when one is made or removed. **DISCHARGED 2026-11-04: measured, the tenth crossed, and the remedy built.**
   On the local fixture (Windows, PostgreSQL 16 on the same machine), 500 sequential `applyEdits` updating one feature of
   `ci_editable` after 50 to warm up, three runs each. With the hook: p50 14.8–29.8 ms, p95 35.8–37.9 ms, mean
   20.9–23.9 ms over six runs. Without it (`RaiseAsync` returning at once, a build made for the measurement and never
   committed): p50 13.0–15.1 ms, p95 34.7–35.9 ms, mean 18.8–19.6 ms. The p50 is bimodal on this machine, about 14 or
   about 30 ms from run to run, so the means carry the comparison: about 3 ms, some 15 %, on a deployment with **no**
   webhooks. That is over the tenth this condition set, so its remedy is owed: which services have webhooks, cached per
   node and told across nodes when one is made or removed. **Built the same day:** `WebhookedServices` remembers, per
   node and per service, whether it has an active feature-service webhook (one `exists` with the enqueue's organisation
   clause, asked on the service's first edit); `ForgettingWebhookStore`, the store every endpoint writes webhooks
   through, forgets it all on a create, update or delete, and `SiteAnnouncements.Webhooks` tells the site's other nodes;
   an answer is trusted five minutes at most, the backstop for a missed announcement. Counted from PostgreSQL's own
   statistics, 100 edits of a service with no webhook read the `webhook` table 94 times before (the collector lagged by
   six) and 0 times after. Timing the two again was not possible that evening: the same build gave means from 25.8 to
   37.6 ms run to run while the machine was busy, so the count is the evidence. `WebhooksTests` edits the layer before
   its webhook exists and then expects the webhook's delivery; with `Forget` disabled it failed, as it should.

## 8. Dependencies

- [ADR-011](ADR-011-job-system.md) §3.2 — the `skip locked` claim, and the lease (D-243).
- [ADR-017](ADR-017-admin-api.md) §5d — the audit log the portal events are raised from.
- [ADR-067](ADR-067-duckdb-sources-beyond-a-local-folder.md) §5.2 — the private-address rule.
- [ADR-082](ADR-082-offline-sync-is-not-in-v1.md) — why there is no `extractChanges`.
- [ADR-204](ADR-204-one-deployment-holds-several-organisations.md) — the floor both tables are under.

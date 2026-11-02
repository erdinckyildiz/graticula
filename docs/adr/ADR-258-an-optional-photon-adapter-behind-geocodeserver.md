# ADR-258 — An optional Photon adapter behind GeocodeServer

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the engine is measured, the wire format is tested against a stub of it, a live Photon has been driven through this server, and the ArcGIS API for Python and the Maps SDK for JavaScript have read it — the SDK's Search widget finding it through the portal; Pro has not (§6, §9) |
| **Decided** | 2026-11-02, by owner decision on [Q-226](../open-questions.md): *"İsteğe bağlı bağdaştırıcı"* — an optional adapter onto a Photon the operator runs. The shape below — the port, the configuration keys, the field mapping, the score — is this ADR's. |
| **Supersedes** | — (amends [v1-scope](../v1-scope.md) §3d, which deferred geocoding; see §5.6) |
| **Superseded by** | — |

---

## 1. Context

The owner asked on 2026-11-01 for an open-source, worldwide geocoder, then for Photon to be tried on the showcase's VPS
(*"photon'u deneyelim vps te"*). It was measured the same night
([photon-turkiye-2026-11-02.md](../research/photon-turkiye-2026-11-02.md)): a Türkiye index of 464 MB built in nine
minutes, 356 MB of memory serving, p95 89 ms, places, streets and districts right, type-ahead and spelling without
Turkish letters working, house numbers sparse because OpenStreetMap's are.

What the server lacks without it: ArcGIS Pro, the Maps SDK's Search widget and Field Maps look for a **locator** — a
`GeocodeServer` — and this server had none. A web map with no search box is a map nobody can find a street on.

Geocoding is deferred in [v1-scope](../v1-scope.md) §3d, and [Q-84](../open-questions.md) recommended a different shape
— *our* engine over the customer's own address data, offline. The two are not exclusive: Photon answers *where is this
place in the world*, Q-84's locator answers *where is this address in my table*. This ADR takes the first and leaves
the second where it was.

§6 is the constraint: the baseline deployment is `graticula → PostgreSQL/PostGIS`, and everything beyond that must be
optional. Photon is a Java service with its own index; it cannot be a requirement.

## 2. Alternatives considered

### Alternative A — no geocoder until Q-84's own locator is built

**Argument for.** v1-scope deferred it on purpose, and one geocoding surface is less to maintain than one now and
another later. Q-84's locator is the one that serves an organisation's own address points, which is what a
municipality or a utility actually wants.

**Argument against.** Q-84's locator is an engine to write — parsing, normalising, ranking, an index — and nothing of it
exists. The owner asked for a worldwide geocoder now, and the measured engine answers the common question (a place, a
street, a district) well. Waiting buys nothing the adapter forecloses: §5.1's port is what Q-84's locator would
implement too.

### Alternative B — Nominatim instead of Photon

**Argument for.** OpenStreetMap's reference geocoder, better at structured addresses and house-number interpolation.

**Argument against.** It needs its own PostgreSQL with a full OSM import — a planet is about a terabyte, Türkiye tens of
gigabytes and hours of import — and its search-as-you-type is weak; Photon was built over Nominatim's data for exactly
the type-ahead `suggest` needs. It was not measured; Photon was, and fits beside everything else on the VPS.

### Alternative C — Pelias, or a hosted geocoding API

**Argument for.** Pelias combines OSM, OpenAddresses and Who's On First and scores its answers; a hosted API is no
operation at all.

**Argument against.** Pelias is several services and Elasticsearch — far past §6. A hosted API sends every search to a
third party, has terms and quotas, and fails the air-gap property Q-15 protects. Neither was asked for.

### Alternative D — embed a geocoding library in the server

**Argument for.** No second process.

**Argument against.** There is no .NET OSM geocoder to embed, and the index is gigabytes that would then live in the
server's memory. A separate process the operator runs is the shape every other optional engine here has taken.

### Alternative E (chosen) — an optional adapter onto a Photon the operator runs, behind a port

Off unless configured; when configured, a `GeocodeServer` in the directory.

## 3. Counterarguments to the preferred option

- **A second engine is a second thing to operate.** An operator who turns this on runs a Java service, imports an
  extract, and updates it. That is real cost, and it is theirs to choose — which is why it is off by default and the
  server is complete without it.
- **Photon gives no score, so the score is invented.** ArcGIS clients act on it: Pro's automatic match takes the top
  candidate above a threshold. §5.4's rule is a defensible reading, not ArcGIS's algorithm, and a client that has tuned
  its thresholds on Esri's World locator will see different numbers.
- **House numbers are weak**, and a utility asking *where is Atatürk Bulvarı 100* gets a bus stop on the boulevard. The
  score says so (75, not 100), but the answer is still not the house. Q-84 is the answer to that, not this.
- **Two geocoding surfaces later.** If Q-84's locator is built it will want the same `GeocodeServer` shape; the port is
  the seam that keeps that one surface with two engines rather than two surfaces.
- **A public geocoder is a public proxy onto the operator's Photon.** Sharing is configurable (§5.2), and the default is
  ~~public (INFERRED, §5.7)~~ **the organisation's — owner decision 2026-11-02** (§5.7 (2)).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Photon fits a small server for a country extract | 464 MB index, 356 MB memory serving, import 530 s under a 1.5 GB cap | [photon-turkiye-2026-11-02.md](../research/photon-turkiye-2026-11-02.md) |
| It is fast enough to sit behind a search box | p50 44.6 ms, p95 88.7 ms, type-ahead `Ank` 49 ms | the same |
| Places, streets and districts are right; house numbers sparse | the query table in the same document | the same |
| The ArcGIS wire format this ADR implements | ArcGIS REST API reference: the geocode service resource, `findAddressCandidates`, `suggest`, `reverseGeocode` — public documentation | developers.arcgis.com, REST API reference |
| Photon's request and answer shape | Photon's README: `/api?q=&limit=&lat=&lon=&lang=&bbox=`, `/reverse?lon=&lat=&radius=&limit=`; GeoJSON with `osm_type`, `osm_id`, `osm_key`, `osm_value`, `type`, `name`, `housenumber`, `street`, `locality`, `district`, `city`, `county`, `state`, `postcode`, `country`, `countrycode`, `extent` | github.com/komoot/photon |
| The mapping and the refusals hold end to end | `GeocodeServerTests` (18), through the real routes, filter and adapter, against a stub Photon answering hand-written GeoJSON in the measured shape; `GeocodeServerWriterTests` (12) for the score and labels | `tests/Graticula.Host.Tests`, `tests/Graticula.Api.ArcGis.Tests` |
| A real ArcGIS client reads it | ArcGIS API for Python's `Geocoder`, `geocode` (with `out_sr=3857` and `max_locations`), `suggest`, `geocode` by `magic_key` and `reverse_geocode`, against a local server configured with a stub Photon on the loopback — all five answered as expected, 2026-11-02. The engine was the stub, so this proves the wire format, not Photon's answers | a probe script, not kept |
| The batch operation, the filters and the portal entry, as ArcGIS documents them | ArcGIS REST API reference: `geocodeAddresses` (the record set, `OBJECTID` returned as `ResultID`, the unmatched record without a `location`, records past `MaxBatchSize` not geocoded, `outFields` `*`/empty/`none`), the geocode service resource (`capabilities` `Geocode,ReverseGeocode,Suggest`, `locatorProperties`), category filtering (the five top-level categories and their children, a parent meaning its children); `distance` as a boost; and two public documents read on 2026-11-03 — ArcGIS Online's `portals/self` (`helperServices.geocode` as an array of `url`, `northLat` … `westLon` naming candidate fields, `name`, `batch`, `placefinding`, `suggest`) and the World locator's service resource (`capabilities` without `Batch` although it serves batches; `MaxBatchSize` 1000, `SuggestedBatchSize` 150) | developers.arcgis.com; geocode.arcgis.com and www.arcgis.com, `?f=json` |
| Photon narrows by kind and country | Photon 1.3.0's `docs/api-v1.md`: `layer` (`house`, `street`, `locality`, `district`, `city`, `county`, `state`, `country`, `other`), repeatable; `countrycode`, repeatable; `zoom` and `location_bias_scale` for the location bias | github.com/komoot/photon, tag 1.3.0 |
| §5.8 holds end to end | `GeocodeServerTests` (30 — 12 new: the batch in order and by `ResultID`, a posted form and `outFields=none`, one record failing, an engine down answered 503 after at most eight tries of sixty, at most four at once and `MaxBatchSize` honoured, refusals; categories narrowed and filtered, an unmapped category refused, `sourceCountry`, `distance`; the portal entry; the setting) and `GeocodeServerWriterTests` (16 — 4 new); `PortalConformanceTests` — the portal names a locator exactly when the directory lists one to the same caller, anonymous and signed in | `tests/Graticula.Host.Tests`, `tests/Graticula.Api.ArcGis.Tests`, `tests/Graticula.Conformance.Tests` |
| The Maps SDK finds it through the portal and uses it | §6, 2026-11-03: `tools/sdk-probe/geocode.html`, Maps SDK 4.30 in headless Chrome, against Esri's World locator and ArcGIS Online first (the control) and then a local server with a stub Photon (`tools/sdk-probe/photon-stub.py`) | `tools/sdk-probe/` |
| The Python API finds it through the portal and geocodes a table | §6, 2026-11-03: `tools/sdk-probe/python-geocode.py` — `get_geocoders`, `geocode` with the GIS's default locator, `batch_geocode` as a list and as a feature set, anonymous and signed in | `tools/sdk-probe/` |

## 5. Decision

**An optional GeocodeServer, `Utilities/Geocode/GeocodeServer`, answered by a Photon the operator runs, through a port
of ours; absent unless `Graticula:Geocoding:PhotonUrl` is set.**

### 5.1 The port and the adapter

- **`IGeocoder`** (`Graticula.Core`, namespace `Graticula.Geocoding`): `SearchAsync(GeocodeSearch)` — text, limit, an
  optional position to prefer, an optional box, a language, and since §5.8 the kinds of place and the countries wanted — and `ReverseAsync(lon, lat, radius, language)`. Places
  come back as `GeocodePlace`, our own record (position, kind, key, name, house number, street, neighbourhood, district,
  city, county, state, postcode, country, country code, OSM category, extent). Every failure of the engine is one
  exception, `GeocoderUnavailableException`. No Photon type appears in the signature (build-vs-adopt §4); `suggest` is a
  search with a small limit, because a type-ahead engine has no separate operation for it.
- **`PhotonGeocoder`** (`Graticula.Host/Geocoding`): calls `{PhotonUrl}api?q=…&limit=…[&lat=&lon=][&bbox=][&lang=]`
  and `{PhotonUrl}reverse?lon=…&lat=…&limit=1[&radius=km][&lang=]`, with the configured timeout, redirects not
  followed, and **at most 2 MB of answer read** (`MaxResponseContentBufferSize`). A refusal, a timeout, a non-2xx
  answer, an answer that is not JSON or not a feature collection, and an answer over the bound are all
  `GeocoderUnavailableException`, with a sentence that never repeats Photon's body.
- `lang` is sent only when the request's `langCode` is one of `Graticula:Geocoding:Languages` — Photon refuses a
  language its index was not imported with, and a refused language should not take the search down.
- It is the eighth named exception in `tools/registers-check.py`'s outbound-connection check, with its reason: it reaches
  only the address an operator configured, and only when configured. An air-gapped deployment runs its Photon inside.

### 5.2 Configuration

| Key | Default | Meaning |
|---|---|---|
| `Graticula:Geocoding:PhotonUrl` | unset — **no service** | The Photon to ask, absolute `http`/`https`, no query; a trailing `/` is added |
| `Graticula:Geocoding:TimeoutSeconds` | 5 | One request's deadline, above 0 and at most 120 |
| `Graticula:Geocoding:Sharing` | `organization` | `public`, `allorganizations`, `organization` or `private` — who may use the service; `group` is refused, as a service in configuration has no groups |
| `Graticula:Geocoding:Languages` | empty | Two-letter codes the index was imported with, e.g. `tr,en` |
| `Graticula:Geocoding:MaxBatchSize` | 200 | The most records one `geocodeAddresses` request geocodes, 1 to 2000 — the service resource's `MaxBatchSize`; `SuggestedBatchSize` is the smaller of it and 100 (§5.8) |

Configuration rather than the admin API, by ADR-017 §5c: where an engine beside the server lives is a fact the server
needs to start, like the platform store's connection string. A bad value refuses to start with the key named (D-171).
The keys are new, so only the `Graticula:` spelling is read (ADR-032 §5 keeps `GisServer:` for keys that already
existed).

### 5.3 The service

- **Absent when not configured**: no route is mapped, no settings or geocoder are registered, and the directory lists
  nothing. `/rest/services/Utilities/Geocode/GeocodeServer` answers 404 like any other path that is not a service.
- **When configured**, the `Utilities` folder lists `Geocode` as a `GeocodeServer` beside `Geometry`, counted on the
  root's folder card, and listed while the platform store is unreachable too, because nothing has to be read to know it.
  Sharing is the configured scope, evaluated by the same `LayerAccess` rule as the system services; refused is 404, or
  the token challenge for an anonymous caller (D-347). Routes are marked `SharingGoverned.ByConfiguration`.
- **Service resource** (`?f=json`, and a directory page for a browser): `currentVersion`, `serviceDescription`,
  `addressFields` (§5.5), `singleLineAddressField` `SingleLine`, `candidateFields` (§5.5), `spatialReference`
  `{"wkid":4326,"latestWkid":4326}`, `locatorProperties` (~~no batch: `SuggestedBatchSize` and `MaxBatchSize` 0~~
  `MaxBatchSize` as configured and `SuggestedBatchSize`, `supportsBatchOutFields`, since §5.8; `MaxResultSize` 50),
  `capabilities` `Geocode,ReverseGeocode,Suggest`, `categories` (§5.8), and **`copyrightText`
  `© OpenStreetMap contributors`** — ODbL asks for attribution in what is shown, and a client that draws a locator's
  copyright draws it from there.
- **`findAddressCandidates`**: `SingleLine` (any case — the SDK sends `singleLine`) or the address fields joined into
  one line; `maxLocations` (default 20, above 50 answered with 50, not a number refused); `outFields` (`*`, a list, or
  the default `Score,Match_addr`); `location` (`x,y` or a point object with its spatial reference) as Photon's position
  bias, and the `Distance` attribute measured from it; `searchExtent` (`xmin,ymin,xmax,ymax` or an envelope object) as
  Photon's `bbox`; `langCode`; `magicKey` (§5.4); `outSR` (a code or `{"wkid":…}`), applied to every location and
  extent through the server's `IProjector` in one call, and echoed with `latestWkid`. ~~`distance` and `category` are
  accepted and not applied: Photon's search has no radius, and its categories are OSM tags rather than ArcGIS's.~~
  `distance`, `category` and `sourceCountry` are applied since 2026-11-03 (§5.8).
- **`suggest`**: `text`, `maxSuggestions` (default 5, ceiling 15), `location`, `searchExtent`, `langCode`, and since
  §5.8 `category` and `sourceCountry` → `suggestions`
  of `{text, magicKey, isCollection:false}`, where `text` is the place's long label and `magicKey` its OSM element key.
- **`reverseGeocode`**: `location` (required, as above), `distance` (metres, sent as Photon's `radius` in km), `outSR`,
  `langCode` → `{address:{…}, location:{x, y, spatialReference}}`. Nothing near is the reference's own refusal: 400,
  *Cannot perform query. Invalid query parameters.*, detail *Unable to find address for the specified location.*
- **An engine that does not answer is 503** with the ArcGIS error envelope and the cause in `details`, logged as event
  2581 — not a 500. The service resource needs no engine and keeps answering.
- **Not served**: ~~`geocodeAddresses` (batch), `findAddressCandidates`'s `category` and~~ `forStorage` semantics and
  intersections. Batch and `category` are served since 2026-11-03 (§5.8).

### 5.4 Score and magic key — INFERRED

**Score: the share of the request's words that the candidate's long label carries, ×100, rounded to two places.** Case
and the diacritics of Latin alphabets (ç ğ ı ö ş ü and accents) are folded first, and a request word matches a label word
that equals it or begins with it — so *Kizilay* matches *Kızılay*, *cad* matches *Caddesi*, *Ank* matches *Ankara*. The
country code counts as a label word. Candidates are sorted by score, **stably**, so Photon's own ranking (which knows
importance) orders the ties. `Status` is `M` at 100, `T` above 0, `U` at 0.

*Why this and not something else:* ArcGIS's score is documented as how completely the input was matched, 0–100. Photon
returns an order and no number. Rank-based numbers (100, 95, 90 …) would say the first answer matched perfectly whatever
it was; coverage says *Atatürk Bulvarı 100 Ankara* answered by a bus stop is 75, which is true. The rule is the owner's to
change — this is listed for confirmation.

**Magic key: the OSM element key Photon answers with** — `osm_type` then `osm_id`, such as `W123456`. Photon has no
lookup by id, so `findAddressCandidates` with a `magicKey` searches its text (asking for at least ten) and, when the keyed
place is among the answers, returns it alone with score 100; when it is not, the ordinary candidates.

### 5.5 Field mapping

| ArcGIS (candidate attribute / reverse address) | From Photon |
|---|---|
| `Loc_name` | `Photon` |
| `Match_addr` | the long label without the country |
| `LongLabel` | name, street + house number, locality, district, city, county, state, postcode, country — each part once, in that order |
| `ShortLabel` | the first part of the long label |
| `Addr_type` | `PointAddress` (a house number on an unnamed thing or a building), `POI` (a named thing that is not a place or a building, with or without a number), `StreetName` (`type=street`), `Postal` (`osm_value=postcode`), `Locality` (a `place`/`boundary`, or `type` locality … country) |
| `Type` | `osm_value` |
| `PlaceName` | `name`, for a POI or a locality |
| `AddNum` | `housenumber` |
| `StName` / reverse `Address` | `street` / street and number |
| `Nbrhd` / reverse `Neighborhood` | `locality` |
| `District` | `district` |
| `City` | `city` |
| `Subregion` | `county` |
| `Region` | `state` |
| `Postal` | `postcode` |
| `CntryName` | `country` |
| `CountryCode` | `countrycode`, upper case — **ISO alpha-2, where ArcGIS's World locator writes alpha-3** (INFERRED: no table is invented to convert) |
| `X`, `Y`, `DisplayX`, `DisplayY` | the point, in `outSR` |
| `Xmin` … `Ymax` / candidate `extent` | Photon's `extent` (read by min and max, so its west-north-east-south order does not matter), in `outSR`; **when Photon gives none, a box sized by kind** — ±0.001° for an address or POI, ±0.005° for a street, ±0.02° otherwise (INFERRED) |
| `Distance` | metres from the request's `location`, great-circle; 0 without one |
| `Osm_key` | `osm_key=osm_value` — our own field, beyond ArcGIS's list |

`addressFields` are `Address`, `Neighborhood`, `City`, `Subregion`, `Region`, `Postal`, `CountryCode`; a structured
request is **joined into one line** in that order (INFERRED: Photon's search is free text).

### 5.6 v1-scope

[v1-scope](../v1-scope.md) §3d is amended with a dated note: **geocoding stays out of v1 as an engine of ours (Q-84),
and an optional GeocodeServer over an operator-run Photon is in**, off by default. The baseline deployment does not
change.

### 5.7 INFERRED items, listed for confirmation

1. The score rule (§5.4).
2. ~~`public` as the default sharing — the data is OpenStreetMap's and public already, and a web map's search box asks
   anonymously; an operator who does not want an open proxy onto their Photon sets `organization`.~~ **Answered
   2026-11-02 by owner decision: `organization` by default** (*"Kuruluş içi"*) — a configured server is not an open
   proxy onto the operator's Photon; an operator who wants an anonymous search box sets `public`.
3. The service's place and name, `Utilities/Geocode`, beside the geometry service, fixed rather than configurable.
4. The default `outFields` (`Score`, `Match_addr`) — the reference describes the default as the minimum.
5. The extent box for a place Photon gives no extent (§5.5) and alpha-2 country codes.
6. A structured request joined into one line.
7. `reverseGeocode` answering in the location's own reference when `outSR` is not sent.
8. *(2026-11-03, §5.8)* The category mapping — which ArcGIS categories are served and onto which kinds of place; the
   children refused.
9. *(2026-11-03)* `distance` as a boost that puts the places inside it first, each group in score order, and asks Photon
   for twice the candidates (at least 20, at most 50) so a near place it ranked lower is there to be lifted.
10. *(2026-11-03)* `sourceCountry` read as alpha-2 codes only, a three-letter code refused with a sentence.
11. *(2026-11-03)* The batch: the best of five candidates per record, a score of 0 unmatched; `MaxBatchSize` 200 by
    default and `SuggestedBatchSize` the smaller of it and 100; four records at a time; the batch failed as 503 when
    the first four asked all failed and none succeeded; `outFields=none` meaning `ResultID`, `Status`, `Score` and
    `Match_addr`.
12. *(2026-11-03)* The portal entry's `name` (*Photon geocoder (OpenStreetMap)*), `zoomScale` 10000, and the locator
    named only to a caller the service would answer.
13. *(2026-11-03)* No `Batch` in `capabilities` — the coordinator's brief asked for it; the reference's example and
    Esri's own World locator, which serves batches, do not name it, so it is not named here either.

### 5.8 The portal's locator, batch, and the filters — 2026-11-03

What §6 first listed as left out, built on the coordinator's brief of the same day.

- **The portal names it.** When a geocoder is configured and the caller may use it — by the rule the service applies to
  itself (§5.3) — `portals/self` (and the organisation document, which is the same document) carries
  `helperServices.geocode` as a one-entry array: `url` (the service, under the server's path base), `northLat` `Ymax`,
  `southLat` `Ymin`, `eastLon` `Xmax`, `westLon` `Xmin` (the candidate attributes that hold a match's extent),
  `name`, **`singleLineFieldName` `SingleLine`**, `suggest`, `zoomScale` 10000, `placefinding`, `batch` — all `true`.
  The shape is ArcGIS Online's own public `portals/self`; `singleLineFieldName` is not in it and is not optional (§6).
  A caller the service would refuse is not given the link, as the geometry service's link is not.
- **Pro's hosting-server listing** (`/arcgis/admin/services/Utilities`) lists `Geocode` as a `GeocodeServer` by the
  same rule as the services directory. The admin reference describes its `services` resource as the container of every
  service in a folder and the Utilities folder as automatically deployed services; it does not name GeocodeServer, so
  this is agreement with the directory rather than a documented requirement.
- **`geocodeAddresses`**: `addresses` is a record set — `{"records":[{"attributes":{"OBJECTID":…,"SingleLine":…}}]}`,
  or the address fields joined into one line as `findAddressCandidates` joins them (§5.5); names in any case; an
  `OBJECTID` (number or text) is required of every record. Also `outSR`, `category`, `sourceCountry`, `searchExtent`,
  `langCode` and `outFields`; `matchOutOfRange`, `locationType` and `preferredLabelValues` are accepted with nothing to
  apply — Photon has no address ranges, no rooftop/street distinction and one label. Each record is answered with the
  best of five candidates, by the §5.4 score, Photon's order settling ties: `address`, `location`, `score` and
  `attributes` with `ResultID` (the `OBJECTID`) and every candidate field — or the fields `outFields` names, `none`
  meaning `ResultID`, `Status`, `Score` and `Match_addr`. **A record with no line, or whose best scores 0, is the
  reference's unmatched record**: no `location`, empty `address`, score 0, every string empty and every number 0,
  `Status` `U`. Answers come in the order the records came, as well as by `ResultID`, because the Python API pairs them
  by position. **Records past `MaxBatchSize` are not geocoded and not answered**, as the reference says.
- **Bounded against Photon**: at most four records are asked at once (`GeocoderSettings.BatchConcurrency`), each with
  the configured timeout. One record Photon fails on is that record's unmatched answer. **When the first four asked
  have all failed and none has succeeded, the rest are not sent** and the request is a 503 with the cause, as every
  other operation answers a Photon that is down — a 200-row table of `U` after 200 timeouts would tell an operator
  nothing and hold the request for minutes. A batch in which nothing succeeded at all is the same 503.
- **`capabilities` does not name `Batch`.** What tells a client a locator takes batches is `MaxBatchSize` above 0 —
  which the ArcGIS API for Python reads to split its table — and the portal entry's `batch: true`. Esri's World locator
  serves `geocodeAddresses` and answers `Geocode,ReverseGeocode,Suggest`; so does this one.
- **`category`** (on `findAddressCandidates`, `suggest` and `geocodeAddresses`): one or several names, commas between,
  a parent meaning its children. Mapped onto the port's kinds of place, which the adapter sends Photon as `layer` where
  it can and filters what Photon answered by in every case (the port promises only places of the kinds asked):

  | ArcGIS category | Kind of place | Photon `layer` |
  |---|---|---|
  | `Address` | house number, street | `house`, `street` |
  | `Subaddress`, `Point Address`, `Street Address` | house number | `house` |
  | `Street Name` | street | `street` |
  | `Postal`, `Primary Postal` | postcode | — (Photon has no postcode layer; filtered here only) |
  | `Populated Place` | locality | `locality`, `district`, `city`, `county`, `state`, `country` |
  | `POI` | point of interest | `house`, `other` |

  **Refused by name, with the list of the ones that are served**: `Coordinate System` and its children, `Intersection`,
  `Distance Marker`, `Street Midblock`, `Street Between`, `Postal Locality`, `Postal Extension`, the children of
  `Populated Place` (a neighbourhood, a district and a city are one kind to the port) and every child of `POI` — `Food`,
  `Coffee Shop` and the rest would need a table from OpenStreetMap's tags to Esri's place types that nobody has written
  and this service will not invent. The service resource's `categories` lists what is served. INFERRED (§5.7 (8)).
- **`sourceCountry`**: two-letter codes, sent to Photon as `countrycode` and filtered by here too; a three-letter code is
  refused with a sentence, since nothing here converts alpha-3 (§5.5). INFERRED (§5.7 (10)).
- **`distance`** (on `findAddressCandidates`, with `location`, which it needs — alone it is refused): the reference
  describes it as the radius within which candidates are *boosted* so the closest come first, not as a filter. So the
  places inside it come first and the rest after, each group in score order, and Photon is asked for twice the
  candidates (at least 20, at most 50) so a near one it ranked lower is there to be lifted. Photon's own `zoom` is not
  used: how its radius maps to metres is not documented. INFERRED (§5.7 (9)).
- **The filters ask for more**: when a kind or a country is filtered here, Photon is asked for twice the limit (at least
  10, at most 50) and the answer cut back after filtering, so a filter does not leave a short list that Photon would
  have filled.


## 6. Consequences

**Positive.** A deployment that runs a Photon gets a locator ArcGIS clients can add by URL, with type-ahead, in 5 to 90
ms for a country. A deployment that does not is exactly as it was: no route, no entry, no client, no dependency. The
port is the seam Q-84's own locator would plug into.

**Negative.**
- The score is ours and differs from Esri's; thresholds tuned elsewhere will not transfer.
- House numbers are only as good as OSM's.
- An operator runs and updates a Java service and an index; the planet needs about 95 GB of disk and 64 GB of RAM
  (Photon's README), Europe a machine of its own.
- ~~Not yet driven against a live Photon: the ArcGIS API for Python has read it over a stub (§4), and the live check on the
  VPS and a Pro or Maps SDK locator check are owed (§9).~~ **Driven against a live Photon 2026-11-02**, the same day:
  Photon 1.3.0 on the VPS with the Türkiye index (`/opt/photon-probe`, capped at 1.2 GB, loopback only, reached through
  an SSH tunnel), behind a local fixture on this branch. Over HTTP, *Anıtkabir* answered the mausoleum at
  32.837, 39.925 (score 100, POI); *İstiklal Caddesi 100 Beyoğlu* the address and the museum at it (100, PointAddress and
  POI); *Ankara* two Localities; *Atatürk Bulvarı Ankara* two POIs on the boulevard at 75 (the street itself is not among
  Photon's first answers); `reverseGeocode` at Kızılay *Yüksel Caddesi, Kültür, Çankaya, Ankara, 06420*; `suggest` for
  *Kızıla* Kızılay first; one request about 0.25 s through the tunnel. The ArcGIS API for Python's `Geocoder` read the
  same service: capabilities, `geocode` in 3857, `suggest`, `geocode` by `magicKey`, `reverse_geocode` in Kadıköy —
  each a correct answer. Nothing the stub did not cover was found, so §9's first trigger did not fire. **Still owed:** a
  Pro or Maps SDK Search widget pointed at it. **The Maps SDK half was done 2026-11-03** (below); Pro is
  still owed.
- ~~The portal's `helperServices.geocode` — where Pro and the Maps SDK look for an organisation's default locator — is not
  set by this change; a client adds the locator by URL until it is.~~ **Set since 2026-11-03** (§5.8), with batch,
  `category`, `distance` and `sourceCountry` — everything this ADR first left out but `forStorage` and intersections.
  **Driven against the live Photon 2026-11-03** (VPS, Türkiye index, capped, loopback, stopped after): `category` *Street Name* for *Kızılay* answered three Kızılay streets in Çanakkale, Adana and Mersin; *Populated Place* the Kızılay neighbourhoods; *POI* the square and the Red Crescent buildings; *Coordinate System* a 400 naming the nine served. `sourceCountry=TR` kept Ankara's answers and `DE` answered none. *Cumhuriyet Caddesi* with an İzmir `location` and `distance=5000` put three places 0.9–3.3 km away first, where without them the first were in İstanbul. A five-record `geocodeAddresses` (Anıtkabir, Galata Kulesi, İstiklal Caddesi 100, nonsense, empty) answered M 100 ×3 at the right places and U 0 ×2, in order, in 0.6 s. Anonymous `portals/self` carried the `helperServices.geocode` entry, and the ArcGIS API for Python's `get_geocoders` found it, `geocode()` used it with no geocoder named, and `batch_geocode` read MaxBatchSize 200 and answered in order. Photon's `layer` and `countrycode` behaved as the stub had assumed; nothing new was found.
- **The Maps SDK check, 2026-11-03** — `tools/sdk-probe/geocode.html` in headless Chrome, Maps SDK 4.30, driven by
  `drive.py`. *Control first*, to separate the harness from the server: by URL against Esri's World locator,
  `suggestLocations`, `addressToLocations` (by text and by a suggestion's `magicKey`), `locationToAddress` and a Search
  widget with it as its source all answered; and with ArcGIS Online as the portal, the Search widget's **default
  sources** were the World locator from its `helperServices.geocode`, and its suggest and search answered. *Then this
  server* (a local fixture, `Sharing=public`, a stub Photon): the four `locator` calls answered — *Kızılay* 100 first,
  the key found again, Kadıköy reverse-geocoded — but **the Search widget made no source of the portal's locator**
  (`suggest:no-sources-defined`, no request sent). A fake portal serving this server's own `portals/self`, varied one
  field at a time, found the cause: the widget took any entry whose `url` was Esri's World locator and none other —
  until the entry named **`singleLineFieldName`**, which ArcGIS Online's entry does not carry and an Enterprise portal's
  custom locator evidently must. With `"singleLineFieldName": "SingleLine"` added, the widget's default source was this
  server's locator, with no URL given to it, and its suggest and search answered from it. A conformance test now asserts
  the field, so it does not go missing again.
- **The ArcGIS API for Python, 2026-11-03** — `tools/sdk-probe/python-geocode.py`, anonymous and signed in:
  `get_geocoders(gis)` returned this server's locator; `geocode` with no geocoder argument used it as the GIS's default;
  `batch_geocode` read `MaxBatchSize` 200 and answered four records in order — *Kızılay, Ankara* M 100, a structured
  *Mühürdar Caddesi 22, İstanbul* M 100, a record the stub fails U 0 without a location, an empty one U 0 — in 3857 as
  asked; as a feature set, two features. Over plain HTTP the batch with a failing record answered the others.
- **Pro still owed**: its Geocode Table over this locator and its use of the portal's default locator have not been
  run; the arcpy harness is the way in.

**State.** None in the catalogue: the service is configuration, read at startup. At runtime each node holds one HTTP client to its configured Photon and nothing else — no cache of answers — so every node of a site answers alike when they share the configuration, and the index is Photon's, outside this server.

**Ports created.** `Graticula.Geocoding.IGeocoder` isolates the engine. Photon is not a library: it is a separate HTTP
service, nothing of it is linked or shipped, and [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md) records it as
external (Apache-2.0) with OpenStreetMap's ODbL attribution carried in `copyrightText`.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Photon 1.x keeps its `/api` and `/reverse` GeoJSON shape | Measured against 1.3.0 on 2026-11-02 |
| — | ArcGIS clients accept a locator without `Batch` and with this score | Partly measured: the Maps SDK and the Python API, 2026-11-03 (§6); Pro `UNVALIDATED` — §9 |
| — | A portal locator entry needs `singleLineFieldName` for the Maps SDK's Search widget | Measured against SDK 4.30, 2026-11-03 (§6) |

## 8. Dependencies

**Depends on:** [ADR-017](ADR-017-admin-api.md) §5c (configuration holds what is needed to start),
[ADR-018](ADR-018-authorization-and-roles.md) (sharing scopes), [ADR-032](ADR-032-the-product-is-named-graticula.md) §5
(configuration names), [ADR-022](ADR-022-geometry-server.md) (the `Utilities` folder and the projector).

**Depended on by:** Q-84's own locator, if it is built, as a second `IGeocoder`.

## 9. Revisit triggers

- **The live check** — the coordinator pointing this server at the VPS's Photon (`/opt/photon-probe`) and a Maps SDK
  Search widget, Pro or the Python API's `Geocoder` at the service — finds a request the stub did not cover. Then the
  mapping is corrected here and in the tests.
- Pro or the SDK refuses the locator without `Batch`, or acts on `MinimumCandidateScore` in a way the score rule makes
  wrong.
- Photon changes its answer format in a release the operator would run.
- Q-84's locator is built: then the service's description, the score rule and `Loc_name` say which engine answered.

## 10. Dissent

None recorded. §3's counterarguments are the case against, and the owner chose with them in view.

# ADR-032 — The product is named Graticula

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-08-17 |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The working title has been `gis-server` since 2026-08-12, recorded as `TBD` in
[product-context.md](../product-context.md) and on the first line of
[CLAUDE.md](../../CLAUDE.md). It was never a name — it was a description of the
category, held open on purpose while the product's shape was still moving.

The owner asked for one on 2026-08-17: *"bu arada gisserver saçma bir isim. buna güzel
bir isim verelim"*, and then, on being offered a Turkish name, corrected the requirement
in a way that decided the answer: *"bu uygulama sadece türkiyede kullanılmayacak. daha
generic, daha genel bir isim olabilir mi?"* — this will not be used only in Turkey.

A name is not an architectural decision, so this ADR exists for a narrower reason:
**the rename touches the schema of a running deployment's configuration, and that part
is a decision with consequences.** §2 is the name; §5 is the part that can break
somebody's server.

## 2. Decision — Graticula

**The product is named Graticula.** The repository, the console, the documentation and
the published artefacts use it. `gis-server` is retired as a working title and stops
appearing except where a document is recording its own history.

A *graticule* is the network of meridians and parallels drawn on a map — the coordinate
grid itself, as distinct from the projected grid of a particular plane coordinate
system. The English word was borrowed from French *graticule*, which came through
Medieval Latin **grātīcula**, from Latin *crāticula*, a grating or grill.¹ `Graticula` is
that Latin original.

**Why this one, in the terms the owner set:**

- **It travels.** *Graticule* is standard cartographic English; the word exists as
  *gratícula* in Spanish and Portuguese and *graticola* in Italian, and it reads
  naturally in Turkish orthography (gra-ti-ku-la). It carries no meaning that only one
  country can decode — which is what ruled out the strongest candidate of the first
  round, `Pafta`, the Turkish term for a systematic map sheet.
- **It does not narrow the product.** The graticule is under everything this server
  does: projection, spatial indexing, tiling, and every query expressed in coordinates.
  Names that were better *sounding* were worse *fitting*: `Tilery` and `Tileworks` say
  tiles, when tiles are one of three v1 protocols and features are the first;
  `Cadastra` says land registry, which is a domain we serve and not the product.
- **It is free everywhere that matters**, which for something being given away is not a
  vanity concern — a project whose name is already an npm package is a project whose
  users cannot find it. See §4.
- **It collides with no GIS product.** Not GeoServer, MapServer, QGIS Server, Mapnik,
  Tegola, Martin, MapProxy, or the anonymised reference (ADR-030).

**What the name deliberately does not claim.** Not *geo-* anything: the prefix is the
most crowded shelf in this field and says only "this is about maps", which the reader
already knows. Not a compass, a star or a globe, all of which say *navigation app*.

## 3. Counterarguments

**It is nine letters, and English speakers need to be told where the stress falls.**
True, and the cost is real for a name typed at a shell prompt. It is mitigated rather
than dismissed: the product is `Graticula`, and nothing forbids a short binary or
package name later. What is not acceptable is a coined misspelling — `Gratica`,
`Graticle` — which buys two characters and loses the only thing the name has, which is
that it is a real word with the right meaning.

**Nobody outside cartography knows the word.** Also true, and it is a weaker objection
than it looks: the audience is people who administer spatial data servers, and
*graticule* is vocabulary they already have. A name that has to be explained once and
then means something exactly is better than one that is instantly readable and means
nothing.

**A name should be decided later, with a wider view.** Rejected on the evidence of this
repository: the working title has already leaked into 234 identifiers, 26 documents, a
solution file, a configuration prefix and a database schema name in five days. It gets
more expensive every day, not less, and the owner has decided.

## 4. Evidence — availability, and how it was checked

Asked of each registry directly, on 2026-08-17:

| | Result | Source |
|---|---|---|
| `graticula.io` | available | `whois.nic.io`, port 43 |
| `graticula.com` | available | `whois.verisign-grs.com` |
| `graticula.org` | available | `whois.pir.org` |
| `github.com/graticula` | available (404) | HTTP |
| npm `graticula` | available (404) | registry.npmjs.org |
| PyPI `graticula` | available (404) | pypi.org |

**The method is recorded because the first one was wrong.** The candidate sweep began
against `rdap.org`, which reported twelve of twelve `.io` names as unregistered —
including names that were obviously taken. **`.io` is not in IANA's RDAP bootstrap at
all**: `data.iana.org/rdap/dns.json` has no `io` entry, so each 404 meant *this service
does not know that TLD*, not *nobody has registered this*. The corrected method asks the
registry that holds the zone, and was validated against a known registration
(`github.io`, created 2013-03-08) before being trusted. DNS resolution was rejected as a
test for the same class of reason: a registered domain that is parked does not resolve,
so silence would read as available. The same trap was avoided a second time when
checking `.org` and `.dev` — Verisign answers `No match` for every domain outside
`.com`/`.net`, which would have produced two more false positives.

¹ [Graticule (cartography)](https://en.wikipedia.org/wiki/Graticule_(cartography));
etymology from [Wiktionary](https://en.wiktionary.org/wiki/graticule) and
[Merriam-Webster](https://www.merriam-webster.com/dictionary/graticule), first English
use recorded 1914.

## 5. The rename, in three layers, because one of them can break a server

**Layer 1 — text and surfaces. Done with this ADR.** README, CLAUDE.md,
product-context, the console's title and header, the REST directory's heading. No
behaviour changes and nothing outside this repository is affected.

**Layer 2 — configuration keys. Done with this ADR, and compatibly.** Settings are read
as `Graticula:PlatformStore`, `Graticula:SecretKey` and so on — environment
`Graticula__PlatformStore`. **The old `GisServer:*` keys keep working**, and a server
started on them logs that it did, naming the replacement. This is not politeness: the
key holding `SecretKey` is what decrypts every stored data-source credential, and a
rename that silently stopped reading it would turn a working server into one that
cannot open its own catalogue — with a message about a missing setting rather than
about a renamed one. The fallback is removable, but not by whoever does the rename, and
not on the same day.

**Layer 3 — identifiers: namespaces, assemblies, project folders, the solution.**
Mechanical, large, and behaviour-free: it belongs in its own commit whose diff is
reviewable as *nothing but a rename*, verified by the full test suite rather than by
reading it. Doing it inside this one would bury both.

**Not renamed at all: the default database schema, `gisserver`.** A schema name is a
deployment's choice, not the product's identity; ours only matched the working title
because nobody had chosen otherwise. Renaming it would mean a data migration on every
existing deployment — including the owner's, which holds 32 published layers — in
exchange for nothing an operator can see. New deployments may name it whatever they
like; the documentation stops implying that the product's name and the schema's name are
the same fact.

## 6. Consequences

- The repository is `graticula`. The GitHub organisation, the domain and the package
  names are claimed before the first release rather than after somebody else takes them.
- `gis-server` survives in exactly one role: as the former working title, in documents
  whose subject is the project's own history. A grep for it should return history, not
  live text.
- [Q-106](../open-questions.md) — the licence — is untouched by this and still open.
  README currently says Apache-2.0 while CLAUDE.md §7 says copyleft is acceptable; that
  contradiction is older than this ADR and is not resolved by it.

## 7. Conditions

1. **Layer 3 lands as one commit that changes no behaviour**, proven by the full suite
   passing before and after with no test edited except for renamed identifiers.
   *(Discharged 2026-08-17 — eight projects and eight test projects moved, 637
   identifiers rewritten across 235 files, the solution renamed, and the whole suite
   green on the renamed tree. The only test bodies edited were the three that name
   identifiers as strings: the tier-boundary project paths, the query model's namespace,
   and the solution file the repository root is found by.)*
2. **The `GisServer:*` fallback is tested**, both that it still works and that using it
   says so — an undocumented compatibility path is one nobody knows to stop relying on.
   *(Discharged 2026-08-17 — five tests in `HostSettingsTests`, and measured live: a
   server started entirely on `GisServer__*` serves the console and logs
   `Configured under the former product name: GisServer:PlatformStore,
   GisServer:SecretKey, GisServer:RequireHttps, GisServer:Port`.)*
3. **The names are actually registered.** An availability table in an ADR is a
   measurement with a shelf life of days; until the domain, the organisation and the
   package names are held, this decision is reversible by a stranger. Owner action.
4. **No document keeps `gis-server` as live text.** Checked by
   [tools/registers-check.py](../../tools/registers-check.py) rather than by memory,
   the way the banned-tally check already works.
   *(Discharged 2026-08-17 — the check is `the_former_product_name`, it found thirty
   occurrences on its first run, and history is allowed by naming it as history. Two of
   its own rules came from its own false positives: `arcgis-server` is not this product,
   and an excuse may fall across a line break.)*

## 8. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-076 | A real cartographic word costs less over a product's life than a short coinage, because it can be explained once and then means something precise | `UNVALIDATED`, and it is a judgement rather than a measurement. The counter-case is every successful product named after nothing at all |

# ADR-067 — DuckDB sources beyond a local folder: remote GeoParquet, a DuckDB database file, MotherDuck

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-13, by owner decision. Asked how a user registers *their own DuckDB, or one from outside*, and shown that none of the three shapes existed, the owner answered: *"local duckdb saçma geldi. web uygulaması açısından. ama üçünü de destekleyelim bence."* — support all three: a DuckDB database file, remote Parquet over `https://` and `s3://`, and a remote DuckDB service (MotherDuck). **Which three, and that all three are wanted, is the owner's. Everything below about how — the kind names, the locators, the sandbox, the order they ship in, what is refused — is `INFERRED`** and listed in §11 |
| **Supersedes** | — (amends [ADR-066](ADR-066-geoparquet-layers-read-by-duckdb.md) §9's second revisit trigger, which this fires) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[ADR-066](ADR-066-geoparquet-layers-read-by-duckdb.md) made one DuckDB-backed source: a folder of
GeoParquet files that the operator places under a root the deployment names, read in the serving
process by an in-memory DuckDB confined to that folder — no extensions, no external access, the
configuration locked. Its §9 named the day that stops being enough: *remote object storage is asked
for (`s3://`, `https://`) — `httpfs` is an extension, and external access is exactly what the sandbox
switches off; that is a new decision, not a setting.* The owner asked for it, and for two more.

The owner's own reading of the first shape matters for the order: a file on the server's disk is an
odd thing to ask a *web* application's user for. It is still wanted, and it is the cheapest of the
three to make safe, but the one a user of a web application can actually use without shell access
to the server is the remote one.

What the three have in common is that DuckDB reads them. What separates them is where the bytes are
and who may reach them, which is what the sandbox is about — so the evidence (§4) was taken before
the design, and it changed the design twice.

## 2. Alternatives considered

### Alternative A — three kinds on the ADR-066 engine, each with its own sandbox *(chosen, `INFERRED`)*

**Argument for.** The provider already answers every face over a DuckDB relation; a remote file is
the same `read_parquet` with a URL where the path was, and a table in an attached database is a
relation too. One instance per source, confined before first use to exactly the location it names,
keeps ADR-066's property that a source can read only itself.

**Argument against.** Two of the three need a DuckDB extension in the serving process (`httpfs`,
`motherduck`), and one of those is proprietary. The network leaves the process from a native
library whose redirects this server cannot see (§4).

### Alternative B — import instead: pull remote data into the datastore on a schedule

**Argument for.** Nothing remote is read on the request path, PostGIS answers every face, and the
sandbox question disappears.

**Argument against.** It is ADR-066's Alternative B again and fails the same way: it does not need
DuckDB, so it does not answer the request, and a bucket that changes hourly becomes an import
hourly. It stays the recommendation for data that must be edited or queried spatially at high rates.

### Alternative C — let an administrator write DuckDB SQL or a DuckDB connection string

**Argument for.** Maximum reach: any extension, any secret, any `ATTACH`.

**Argument against.** Every protection in ADR-066 rests on this server never handing DuckDB text
somebody else wrote. A connection string is text. Rejected.

## 3. Counterarguments to the preferred option

**The allow-list is not the whole of the network boundary, measured.** DuckDB's
`allowed_directories` confines a remote read to the prefixes listed, but **it checks the URL it was
given, not the one a redirect leads to**: with only `https://github.com/opengeospatial/` allowed, a
GitHub URL that redirects to `raw.githubusercontent.com` was read (§4). DuckDB 1.5.5 has no setting
that turns redirects off. So an allowed host that redirects inward — to a cloud metadata address, to
a service on the private network — takes the read with it. **This server's answer is the private
address check at registration and at open, and it does not cover a redirect.** That residue is
recorded here and as condition 2 rather than argued away.

**`CREATE SECRET` switches the allow-list off, measured on both architectures.** With any DuckDB
secret created — S3 or HTTP, scoped or not — before `allowed_directories` is set, a read of a URL
outside the list is sent anyway, including `http://127.0.0.1`. The legacy settings
(`s3_access_key_id`, `s3_secret_access_key`, `s3_region`, `s3_endpoint`) do not have the defect.
**So this server never runs `CREATE SECRET`**, a test pins that no statement it sends contains it,
and credentials go through the legacy settings only. It is a defect in DuckDB rather than a design
choice, and reporting it upstream is the owner's call (condition 3).

**A proprietary binary in a source-available product.** MotherDuck's terms grant a customer the
right to *download and install* its extension, non-transferably, and say nothing about
redistribution. So the image does not carry it. An operator who enables MotherDuck has the server
download it from DuckDB's extension repository, where DuckDB checks its signature on load. That is
the customer installing it, which is what the terms grant — **`INFERRED`, and not legal advice.**

**A remote read in a request is a network call per request.** A small file on GitHub answered in
400 ms; a footer on S3 in 600–1,600 ms from the development machine and the VPS to Oregon (§4). A layer on the other side of an
ocean draws like one. The statement deadline, thirty seconds, is what bounds a slow bucket, and the
metadata cache is what keeps a map from paying the footer on every tile.

**A DuckDB database file carries no reference the core can read.** `GEOMETRY('EPSG:3857')` is
refused by core DuckDB (*unrecognized coordinate system*), and `st_crs` on a core-created geometry
is null. A table in a `.duckdb` file or on MotherDuck therefore has no reference of its own that this
server can check, and the publisher declares it — the one place a DuckDB-backed layer's reference is
asserted rather than read, and the reason condition 5 exists.

## 4. Evidence

All on DuckDB 1.5.5 through DuckDB.NET 1.5.5, 2026-09-13, Windows x64 and linux-arm64 (the VPS). The
spike is disposable (`scratchpad/duckremote`) and is not promoted; the tests below are the record.

| Claim | Evidence |
|---|---|
| Both extensions exist for 1.5.5 on every platform this ships or tests on | `httpfs` and `motherduck` downloaded for `linux_arm64`, `linux_amd64`, `windows_amd64` from `extensions.duckdb.org/v1.5.5/…`; MotherDuck reports `v1.5.5-2026-09-75` |
| A signed extension loads from a local file with autoinstall and autoload off | `LOAD '<path>/httpfs.duckdb_extension'` 70–85 ms; `motherduck` 7.5 s cold, 1.1–1.4 s warm, 235 ms a second time in the same process |
| The sandbox confines a remote read to a prefix | `allowed_directories=['https://raw.githubusercontent.com/opengeospatial/']`, external access off, locked: the example GeoParquet read (5 rows, WKB) in 402 ms; a local file, loopback, and another path on the same host refused with *file system operations are disabled by configuration* |
| …and to a bucket and prefix | `s3://overturemaps-us-west-2/release/2026-07-22.0/theme=divisions/` allowed: a footer (138,218 rows) in 1,628 ms, the `geo` key, a glob of 8 files; a sibling prefix, another bucket (read, glob of a prefix, glob of a root), https and loopback refused |
| **Redirects are not checked** | only `https://github.com/opengeospatial/` allowed; `…/raw/main/examples/example.parquet`, which redirects to `raw.githubusercontent.com`, read 5 rows |
| **`CREATE SECRET` before the allow-list disables it** | with an S3 secret (config provider, scoped or not) or an HTTP bearer secret created first: another bucket answered HTTP 403 *from S3*, an https URL outside the list returned its content, and `http://127.0.0.1:9` was connected to. Same on linux-arm64. Legacy `s3_*` settings in the same position: every one refused. A secret created *after* the lock did not reopen anything |
| No redirect setting exists | `duckdb_settings()` after `LOAD httpfs`: logging, metadata cache, proxy — nothing for redirects |
| A DuckDB file is readable under the same sandbox | `ATTACH '<file>' (READ_ONLY)` with the file's directory allowed, external access off, locked: `information_schema.columns` lists `GEOMETRY` and `BLOB` columns; `st_aswkb(geom)` reads; an `INSERT` is refused (*attached in read-only mode*); attaching a file outside is refused |
| Core cannot type a reference on a geometry column | `create table … (geom GEOMETRY('EPSG:3857'))`: *Encountered unrecognized coordinate system 'EPSG:3857'*; `st_crs` of a core geometry is null |
| MotherDuck with a token in the URL waits for a browser | `ATTACH 'md:my_db?motherduck_token=…'`: *Attempting to automatically open the SSO authorization page*, a one-minute wait, then *no token provided* |
| …and with the token set first it does not | `SET motherduck_token=…` before the lock, then `ATTACH 'md:' (READ_ONLY)`: an authentication error in 56 ms (arm64) / 1.4 s (Windows), no browser |
| MotherDuck does not reopen the local sandbox | on the same confined instance, `read_text` of a local file and an https read are refused |
| **A file URL is a path, not a directory** | the provider's first test run: `allowed_directories=['http://…/files/grid.parquet']` refused the file itself; allowing its directory would allow its siblings. `allowed_paths` takes the one file, and the sibling on the same server is refused — `The_instance_reads_its_own_location_and_nothing_else` |
| **Real file names are not identifiers** | the first end-to-end run listed Overture's prefix as *no .parquet files*: every name was `part-00000-3d6dbc8d-…-c000.zstd.parquet` and was dropped silently. Names are now sanitised to identifiers, and the listing maps each back to its file |
| **Proving an identity unique is the probe's dominant cost** | from the VPS to `us-west-2`, per Overture file: `geo` key 1,569 ms, footer 607, describe 664, `count(distinct)` over its two integer columns **3,731**; eight files took longer than the client's minute and the request was abandoned (499). Without the uniqueness scan and with four footers read at once: **8 files in 6.3–6.5 s** |
| **An independent security review of §5.2, before release** | No injection path: every field reaches DuckDB through `Literal()` after its own rule. **Repaired, each with a test:** the address check resolved an S3-compatible endpoint while DuckDB connected to `bucket.endpoint` — bucket `169.254.169.254` on endpoint `nip.io` passed (H1; the bucket's own host is now checked unless the style is `path`, and address-shaped buckets are refused); **loading httpfs copied the server's `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`, `AWS_REGION` and `DUCKDB_S3_ENDPOINT` into its settings**, measured, so an anonymous registration would have read as the server (P1; every S3 setting is now written every time); metadata refreshed by every request at once when its minute ended, with no bound on a slow server (M1; one refresh at a time serving the previous answer, `http_timeout` 30 s, one retry); a probe with no bound on files (M2; 1,000, said when reached); the key id returned by the readback (M4; masked); a DuckDB exception kept inside the refusal, where a logger would print the statement and its secret (L2); two files sanitising to one name numbered in listing order, so a writer could take over a published name (L3; both refused); a torn listing cache (L5); IPv4 inside NAT64, 6to4 and IPv4-compatible IPv6, and 198.18/15 (L1); listed keys with pattern characters and recursive globbing (P2); records whose generated text printed secrets (P3); the extension version read from the package cache rather than the pin (L4). **Not repaired:** DNS rebinding and redirects between the check and DuckDB's own resolution (M3) — the check runs at registration and when an instance opens, not per read, and condition 2 is where it stays |
| **The first version's S3 settings never reached a query** | found by the test written for P1: a plain `set` changes the session that ran it, and every query runs on a connection duplicated from it, which reads the *global* value — the environment's key was still in force there. So a registration's region and keys had never been used by a query; the public Overture bucket answered anyway. Now `set global`, and a test shows two instances each seeing only their own key |
| **A DuckDB file does not keep a geometry column's reference** | a table created with `GEOMETRY('OGC:CRS84')`, or from a GeoParquet file whose column is typed so, reports the reference while the writer has it open and `GEOMETRY` after `checkpoint` and reopening; `st_crs` is null. `GEOMETRY('EPSG:4326')` is refused by core. Windows x64 and linux-arm64 |
| **MotherDuck does** | the owner's account, 2026-09-13: a table created from the places file lists `GEOMETRY('OGC:CRS84')` through `information_schema.columns` on a confined, read-only attach |
| MotherDuck with a real account, confined | extension loaded and token set 0.5 s; confined attach 112–228 ms; 3,405 rows counted in 14 ms; geometry as WKB; a box through core `st_intersects_extent` 37–44 ms; an `INSERT` refused; databases not attached not visible; a local file refused; `rowid` and its alias work. **An https read on the same confined, authenticated connection returned content** — it did not with a bad token, so it is taken to run on MotherDuck's servers; not verified further |
| MotherDuck does not fall back to the environment, nor mix instances | the real token in `motherduck_token` and `MOTHERDUCK_TOKEN`, and a bogus or empty token set globally: attach refused both times. Two instances in one process, one with the real token and one with a bogus one: the second refused, the first still reading |
| A security review of §5.3–5.4, before release | No injection, no file escape; the environment and isolation questions it raised measured and closed (row above). **Repaired, each with a test where one can be written:** listing and extent scans unbounded, uncancellable and repeated by every request when the minute ended (one refresh at a time serving the previous listing, measurements kept per version, thirty-second deadlines, a thousand tables); a column named `rowid` hiding the row number, and MotherDuck's `rowid` not being stable (neither offered); a malformed geometry making a description a 500; a `.wal` link; the extension downloaded on a request thread under a lock with no body deadline, into a shared partial name, never checked (background, bounded, unique, loaded before kept); a declared reference with no known area of use waved through (refused). **Not repaired:** a crafted `.duckdb` parsed in process (trusted as placed, §6), and a MotherDuck table's owner changing its data after publish (the version sees row counts and columns, not values) |
| **The extension download, found broken on the showcase** | 1.0.51 on the showcase left a 960 KB partial file and never finished, past its five-minute deadline, with the process idle; the same streaming loop finished in 0.7 s standing alone in that container, and why it hung inside the server was not found. Rewritten as a buffered request with retries, it then failed three times with *DuckDB would not load the downloaded file* — which every download would have: DuckDB takes an extension's name from its file name and refuses one not ending in `.duckdb_extension`, and the file was being checked as `….duckdb_extension.<pid>.<guid>.partial`. Staged under its own name in a folder of its own, the fixture installed it in 1 s at startup and `gp-attached-e2e.py` passed on the downloaded copy |
| Both kinds end to end | `gp-attached-e2e.py`, fixture, linux-arm64: five refusals; the file registered on EPSG:4326, published, counted (3,405, 110 ms), boxed (1,570, 70 ms), extent computed; the same file declared EPSG:2320 refused at publish by the area-of-use check; MotherDuck `graticula_demo` registered with the owner's token, reference read from the column type, published, counted (40–50 ms), 5 features in 3857 (130 ms), boxed (40 ms); `hasToken` and no token in the readback, the listing or the logs — **all pass** |
| Every face asked answers, over both schemes | `gp-remote-e2e.py` against the fixture, linux-arm64: seven refusals (http, a private endpoint, `169.254.169.254`, a signed URL, a key without a secret, remote fields on a folder, a hand-written locator); an https file tested (0.2–0.6 s), registered, published, counted (5, 70–240 ms) and returned in 3857 (290–340 ms); the Overture prefix tested (8 files), registered, one file published and counted from its footer (138,218, 610–670 ms), an envelope answered in 600–670 ms; a keyed registration's secret absent from the listing, the readback (`hasSecret: true`) and the logs endpoint — **all pass** |

## 5. Decision

**Three more kinds of data source, each read by its own DuckDB instance, each confined to what it
names.** They ship in this order, one release each, and this ADR is the decision for all three.

### 5.1 Common to all three

1. **Read-only.** Capabilities are `Query`; every write path refuses as ADR-066 §9 does.
2. **No SQL from outside.** Everything DuckDB runs is built here, from a parsed where clause and
   identifiers this code already holds. No request field reaches DuckDB as text except a URL, a
   bucket region, an endpoint host, a database name and a credential — each validated by its own
   rule below and bound as a literal.
3. **Never `CREATE SECRET`.** Credentials are legacy settings, set before the allow-list and the
   lock. Pinned by a test over every statement an instance runs.
4. **Extensions from a directory the deployment names**, `Graticula:DuckDbExtensions`
   (`/app/duckdb-extensions` in the image), loaded by path with autoinstall and autoload off.
   DuckDB verifies each signature on load; unsigned extensions stay off.
5. **Bounded like ADR-066**: one gigabyte and two threads per instance, the thirty-second statement
   deadline, the vertex and match caps.
6. **Credentials are sealed** with the rest of the locator, never returned by the readback endpoint
   (the key id comes back masked, to be recognised), and never written to a log or an error.
7. **Nothing is inherited from the server's environment**: every S3 and http setting is written
   globally on every instance, because loading `httpfs` copies the process's AWS variables into them
   (§4).

### 5.2 Remote GeoParquet — `geoparquet-remote` *(first)*

1. **A location**: an `s3://bucket/prefix/` whose `.parquet` files directly under it are the
   tables, or one `https://…/name.parquet` file. `http://` is refused.
2. **The private address check**: the host — the bucket's endpoint, or the https host — is resolved
   at registration and at every open, and a loopback, private, link-local, unique-local or
   unspecified address is refused unless `Graticula:RemoteDataAllowPrivate` is true. **A redirect is
   not covered (§3), and the redirect residue is condition 2.**
3. **The sandbox**: `httpfs` loaded, `allowed_directories` set to exactly the prefix or
   `allowed_paths` to exactly the one file, external access off, locked.
4. **Optional S3 credentials**: access key id, secret, region, endpoint (for S3-compatible stores),
   URL style. Absent, the bucket is read anonymously.
5. **Metadata is cached for a minute** (`INFERRED`): a remote file has no modification time this
   server can read cheaply, so a table's version is the hash of its footer's row count, row-group
   count, writer and `geo` key, and the cache is refreshed at most once a minute. A rewrite that
   changes none of those keeps its old version.
6. **A table name is the file's name made into an identifier** — letters, digits and underscores,
   at most 63, a leading underscore before a digit, a numbered suffix when two collide in listing
   order — because a bucket's file names are rarely the registrant's to change (§4). A local folder
   keeps ADR-066's rule and asks for a rename.
7. **The identity is the row number, and nothing is scanned to offer another** (§4): proving a
   column unique reads the whole column over the network. A remote file is as immutable as its
   footer, so the row number is as stable as a local file's. Footers are read four at a time.
8. **Off unless `httpfs` is in the extension directory.** The image carries it for both
   architectures; `httpfs` is MIT, part of DuckDB itself.

### 5.3 A DuckDB database file — `duckdb`

1. **A `.duckdb` file under `Graticula:GeoParquetRoot`**, placed by the operator as a folder is, and
   attached `READ_ONLY` with **the file and its `.wal` as the only allowed paths** — not its folder, so a
   Parquet file beside it is not readable through it. A link at the file, its log or any folder between
   it and the root is refused.
2. **Tables in the `main` schema** with a `GEOMETRY` column are the tables; views are not listed (they
   are not in `duckdb_tables()`), nor are other schemas, and a `BLOB` of WKB is listed with the reason.
   At most a thousand tables.
3. **The reference is declared at registration** (`srid`), because a file cannot keep one (§4); a column
   type that does name one wins, and the two disagreeing is refused. At publish, the table's extent is
   moved into degrees from the declared reference and must fall inside that reference's area of use,
   widened by a degree; a reference with no known area of use is refused (condition 5).
4. **Identity** is an integer column measured unique and never null, else `rowid` aliased as the row
   number — except when the table has a column of its own named `rowid`, which hides DuckDB's.
5. **The extent is computed here**, once per table version, by reading the geometry column: core DuckDB has
   no aggregate for it. One scan at a time, under a thirty-second deadline; a failure is an unknown extent.
6. **The version** changes with the file's length and modification time; the catalogue is read again when
   it does, one refresh at a time, and a table's measurements are kept while its version holds.

### 5.4 MotherDuck — `motherduck`

1. **A database name and a token**, the token set globally before the lock, `md:` the only allowed
   location, the database attached `READ_ONLY`. A registration with no token, or one not shaped like a
   token, is refused before DuckDB is asked, so the browser flow never starts. The database name rule
   (letters, digits, underscores) keeps a registration to the token's own account's databases.
2. **Tables, reference and extent as §5.3**, except that a MotherDuck column type usually names its
   reference — measured, `GEOMETRY('OGC:CRS84')` survives there — so `srid` is optional.
3. **No row number**: MotherDuck does not promise a row keeps its `rowid` through deletes and compaction,
   so a table needs a unique integer column of its own.
4. **The catalogue is refreshed at most once a minute**, one refresh at a time, serving the previous one
   meanwhile; a table's version is its row count and columns, and its identity is measured once per version.
5. **The extension is not in the image.** With `Graticula:MotherDuck=true` the server starts downloading
   it in the background at startup, from `extensions.duckdb.org` for the version it carries, into
   `<StatePath>/duckdb-extensions`, under a five-minute deadline and a size cap, under a name no other
   process uses, and keeps it only after DuckDB has loaded it. A request that arrives first is told so.

## 6. Consequences

**Positive.** A bucket of GeoParquet becomes a layer with no import and no copy, which is the
reference-layer case the research note put against DuckDB in the first place. A DuckDB file or a
MotherDuck database becomes layers the same way. The same provider serves all five kinds.

**Negative.** Network reads on the request path, at the bucket's latency. Two native extensions in
the serving process beside DuckDB, one of them closed-source and fetched at run time. A redirect
from an allowed host is followed. A declared reference is checked by a heuristic that cannot tell two
projected grids with overlapping areas apart. A `.duckdb` file is parsed by native code in the serving
process, so a file under the root is trusted as ADR-066 trusts a Parquet file there: the operator placed
it. **What MotherDuck runs on its own servers is outside this server's sandbox** (§4) — safe only while no
caller's SQL reaches DuckDB, which is ADR-066's rule and is kept.
The image grows by `httpfs` for two architectures (about 20 MB on arm64, 28 MB on x64, uncompressed).

**State.** *Catalogue*: `data_source.kind` gains `geoparquet-remote`, `duckdb` and `motherduck`
(migration 47, all three at once so the later two need no migration of their own); each sealed
locator names its engine at its front. *Runtime, node-local*: one DuckDB instance per source, as
ADR-066. *Files*: the extension directory.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-016 | The serving process never loads GDAL | holds — neither `httpfs` nor `motherduck` is the spatial extension |
| — | DuckDB's signature check is what makes a downloaded extension trustworthy | assumed from DuckDB's documentation; not audited here |
| — | An administrator who can register a PostGIS connection to any host may register a bucket | the same trust the PostGIS probe already extends; the private address check narrows it rather than widening it |

## 8. Dependencies

**Depends on** — [ADR-066](ADR-066-geoparquet-layers-read-by-duckdb.md) (the engine, the provider,
the refusals), [ADR-002](ADR-002-primary-data-architecture.md) §4.7 (sealing a data source's
locator).

## 9. Revisit triggers

- **DuckDB adds a way to refuse or check redirects** — use it; condition 2 is discharged by it.
- **DuckDB fixes `CREATE SECRET` disabling the allow-list** — the rule in §5.1.3 stays anyway until a
  test shows the fix on both architectures.
- **A caller, not an administrator, can name a remote location** — the trust in §7's third row is
  gone and the design is reopened.
- **MotherDuck's terms change on redistribution**, in either direction.

## 10. Dissent

None recorded. Decided in one session without a reviewer, like ADR-066, and §3's two measured
defects are the reason an independent security review is condition 1 rather than a nicety.

## 11. Conditions

1. **An independent security review of each kind before its release**, against §3's two defects and
   the credential path. **PARTLY DISCHARGED 2026-09-13 — for `geoparquet-remote`**: reviewed before its
   release, and every finding but DNS rebinding repaired with a test (§4). **DISCHARGED 2026-09-13 for
   `duckdb` and `motherduck` too**: reviewed before their release, two suspicions measured and closed,
   and every other finding repaired or recorded in §6 (§4).
2. **The redirect residue** — a redirect from an allowed host is followed (§3), and a name that
   resolves publicly at the check can resolve privately when DuckDB connects (the review's M3).
   Discharged by a DuckDB setting that refuses or checks redirects, by routing DuckDB through
   `http_proxy` to an egress proxy that refuses private destinations — which answers both — or by an
   egress rule in the deployment that the README documents.
3. **The owner decides whether to report the `CREATE SECRET` defect to DuckDB** (§4); it is a
   security report about somebody else's product, and not this server's to file unasked.
4. **MotherDuck is tested against a real account** before it is described as working. This build
   has only ever seen it refuse a bad token. **DISCHARGED 2026-09-13** — the owner opened one and put a
   token on the VPS; registered, published and queried through every step in §4.
5. **A declared reference on a `duckdb` or `motherduck` layer is checked against its data** — at
   least that the extent falls inside the reference's domain — before those kinds ship. **DISCHARGED
   2026-09-13** — `DeclaredReferenceRefusalAsync`, at both publish routes: the extent moved into degrees must
   fall inside the reference's area of use; a reference with no known area is refused; degrees declared as
   a Turkish grid were refused end to end. It is the heuristic §6 names, not a proof.
6. **Every new form in the console goes through the UX review** the owner requires of every screen.
   **PARTLY DISCHARGED 2026-09-13 — for `geoparquet-remote`**: reviewed from headless-Chrome
   screenshots, since the reviewer had no browser. Repaired: bucket settings typed for an s3:// location
   and left in place were sent with an https one and refused (they are hidden and not sent now); the
   folder-versus-file distinction was buried in the hint; the secret's fate was explained only when
   correcting; a long prefix widened the Sources table past the page. Not repaired, as older than this
   change: the console's width at 400 px, and focus passing through the page once between the dialog's
   last and first controls. **DISCHARGED 2026-09-13 for `duckdb` and `motherduck`** the same way. Repaired:
   the file's required reference was said only in the paragraph under it (now on the label, and asked for
   before the server is); the refusal did not name the likely codes; MotherDuck's bad token arrived as the
   driver's sentence alone; the optional reference was not called optional; where a token comes from was
   far from its field; a refused table was named `places.parquet`. Not repaired: one flat list of five kinds.

**`INFERRED`, for the owner to confirm:** the kind names; S3 prefixes rather than globs, and one
https file per source; the one-minute metadata cache; the order of the three; downloading MotherDuck
at run time rather than shipping it; refusing private addresses by default; a `.duckdb` file living
under the GeoParquet root rather than a separate one; one declared reference per DuckDB source rather than
per table; only the `main` schema; no row number on MotherDuck.

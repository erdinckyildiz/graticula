# Stitch prompts — remaining screens

> **Superseded in part, 2026-10-16 (owner).** The accent is **blue**, not teal — *"teal falan da istemiyorum. bu renkler
> olsun"*, given with two reference images of Studio and Server in light and dark. Light theme: white cards on a
> pale grey page (`--paper #f4f6f9`), accent `#1565d8`; dark theme: navy (`--paper #0a1120`), accent `#2563eb`. The
> owner also ended the Stitch work (*"bunları artık stitche göndermek istemiyorum"*): the screens are built in code
> from those images, and the build is the reference now. Where this file says teal or names Stitch as the
> source of a screen, it is the record of the direction before that day. [ADR-187](../adr/ADR-187-primary-navigation-is-a-top-bar.md) carries the build notes.


Project: "Graticula Hybrid — Studio & Server redesign". Use the project's design system "Graticula Hybrid" for every screen, Desktop.
Paste one block per generation. Structure follows public screenshots of ArcGIS Server Manager and Portal (layout only — no Esri pixels, icons or branding).

Shared header text — already inside each block below.

---

## SV-S1 · Server › Site › Data sources

Graticula Server — Site › Data sources (route /server/#/sources), desktop, light, Graticula Hybrid design system (teal #0d7d70, Inter, 5px radius, dense, no decoration). Structure like ArcGIS Server Manager's Site › Data Stores page; Graticula's own look. Add nothing not listed.
Top bar (white 56px): navy square logo with a white 3x3 grid glyph, "GRATICULA", small teal "Server" badge. Horizontal nav, exactly four items: Services, Site (selected, teal underline), Security, Logs. Right: green dot + "Store reachable · 128 layers", segmented "Server | Studio" (Server selected), "Erdinç Yıldız" / "administrator", "Sign out". No avatar, bell or app grid.
LEFT PANEL (248px, list like Server Manager's grey box): "Data sources" (selected, teal-soft background, teal left bar), "Status", "General".
MAIN: heading "Data sources" with a small "Help" link on the far right. Two short paragraphs: "Registering a source gives this server a credential for a database or file store it publishes from." and "Tick one or more sources and press Probe to check they answer; Probe all checks every one."
Toolbar row: secondary buttons "Probe", "Probe all" on the left; on the right "Register:" followed by a split button "PostGIS database ▾" (menu: PostGIS database, GeoParquet folder, GeoParquet on the web or S3, DuckDB file, MotherDuck).
Table: [checkbox] | Status (icon + word: green ✓ "Answers", amber ⏸ "Paused", red ! "Unreachable") | Name (bold, connection in muted monospace under it) | Kind | Layers | actions (icon buttons with tooltips: Pause, Edit, Remove).
Rows: datastore — gis@db:5432/graticula — PostGIS (grey pill "built-in") — 128 — no actions, muted "set by Graticula:PlatformStore"; ibb_planning — planner@10.2.0.14:5432/planning — PostGIS — 34; census_parquet — /data/census/ — GeoParquet folder — 6; osm_s3 — s3://osm-tr/2026-10/ — GeoParquet on S3 — 3; analysis_duck — /data/analysis.duckdb — DuckDB file — 2 — status Paused, and an amber line under the row "Paused until 14:30 by erdinc: re-indexing" with a "Resume" text button.
Under the table: a panel "Probe result — ibb_planning": green line "Answered in 38 ms · PostGIS 3.4 · 41 tables with geometry", filter input "Filter tables", compact table Table | Geometry | SRID | Rows (parcels MultiPolygon 5254 812,004; zoning MultiPolygon 5254 12,330; roads MultiLineString 4326 94,120).

---

## SV-S2 · Server › Site › Status

Graticula Server — Site › Status (route /server/#/operations), desktop, light, Graticula Hybrid design system (teal #0d7d70, Inter, 5px radius, dense, no decoration, NO charts). Structure like ArcGIS Server Manager's Site pages (left list, heading with Help link, plain tables); Graticula's own look. Add nothing not listed.
Top bar: navy square logo with a white 3x3 grid glyph, "GRATICULA", small teal "Server" badge. Nav, exactly four: Services, Site (selected), Security, Logs. Right: green dot + "Store reachable · 128 layers", segmented "Server | Studio" (Server selected), "Erdinç Yıldız" / "administrator", "Sign out". No avatar.
LEFT PANEL: "Data sources", "Status" (selected), "General".
MAIN: heading "Status" with "Help" on the right; muted "What the process is doing now · read at 10:42:07" and a secondary button "Read again".
Four plain panels in a 2x2 grid (1px rule, no shadow), each a two-column definition list:
"Platform store": Status — green dot "Reachable"; Layers 128; Version PostgreSQL 16.4 · PostGIS 3.4.
"Runtime": Uptime 6 days 4 h; Heap 412 MB; Allocated 1.9 GB; GC pause 0.8 %; Collections 1,204; Cores 4.
"Caches": Tiles cached 284,110; On disk 3.1 GB; Building 0; Shapes remembered 96; Shape lifetime 10 min.
"Datastore": Hosted data 2.4 GB; Database size 3.0 GB; By owner — erdinc 1.1 GB, ayse.k 0.6 GB, mehmet.t 0.4 GB; measured 10:30.
Then two panels side by side: "Route governance" (green dot; Routes 412; Ungoverned 0; Cross-origin reads: allowed for 2 origins) and "Empty services" (buttons "Look for them", danger-outline "Remove them"; table Service | Folder | Sharing with one row old_test · Sandbox · Owner).
Then a collapsed row "Every route (412)" with chevron.

---

## SV-S3 · Server › Site › General

Graticula Server — Site › General (route /server/#/settings), desktop, light, Graticula Hybrid design system (teal #0d7d70, Inter, 5px radius, dense, no decoration). Structure like ArcGIS Server Manager's Site › Settings pages; Graticula's own look. Add nothing not listed.
Top bar: navy square logo with a white 3x3 grid glyph, "GRATICULA", small teal "Server" badge. Nav, exactly four: Services, Site (selected), Security, Logs. Right: green dot + "Store reachable · 128 layers", segmented "Server | Studio" (Server selected), "Erdinç Yıldız" / "administrator", "Sign out". No avatar.
LEFT PANEL: "Data sources", "Status", "General" (selected).
MAIN: heading "General" with "Help" on the right. Sections separated by grey bold headings with a thin rule (Server Manager style), labels left, controls in a right column:
"Query results": Page size (features) [2000] muted "default 1000"; buttons Save, Use the default.
"Basemap gallery": Gallery group [basemaps ▾] muted "6 entries"; Default basemap [Light gray ▾]; button Save basemaps.
"Map ground": "Drawing order, top first" ordered list with ↑ ↓ buttons — graticule, places, rivers, countries; checkboxes "Basemaps/city_base (vector tiles)" ticked, "Basemaps/hillshade" unticked; buttons Save ground, Undo changes, Use OpenStreetMap.
"Style sources": textarea "Allowed origins, one per line" with https://basemaps.arcgis.com and https://tiles.ibb.gov.tr; button Save style sources.
"Geometry service": URL input https://demo.graticula.com/rest/services/Utilities/Geometry/GeometryServer; buttons Save geometry service, Use this server's own.

---

## SV-S4 · Server › Security › Members

Graticula Server — Security › Members (route /server/#/members), desktop, light, Graticula Hybrid design system (teal #0d7d70, Inter, 5px radius, dense, no decoration). Structure like ArcGIS Portal's Organization › Members (filters column on the left of the table, inline role select); Graticula's own look. Add nothing not listed.
Top bar: navy square logo with a white 3x3 grid glyph, "GRATICULA", small teal "Server" badge. Nav, exactly four: Services, Site, Security (selected), Logs. Right: green dot + "Store reachable · 128 layers", segmented "Server | Studio" (Server selected), "Erdinç Yıldız" / "administrator", "Sign out". No avatar.
LEFT PANEL: "Members" (selected), "Roles", "Sign-in", "Apps"; below a divider, filter groups: "Role" checkboxes (Administrator 1, Publisher 2, User 3, Viewer 1), "User type" checkboxes (Creator 4, Contributor 2, Viewer 1), "State" (Active 6, Disabled 1).
MAIN: heading "Members"; toolbar: primary "New member", search "Search members", muted "1 – 7 of 7".
Table: Member (grey initials circle, bold name, display name muted, and "signs in with Kurum AD" where it applies) | Role (inline select) | User type (inline select) | State (pill with word) | Owns | Since | ⋯ menu (Set password, Disable/Enable, Remove).
Rows: erdinc · Erdinç Yıldız · Administrator · Creator · Active · 41 · 3 Mar 2026; ayse.k · Ayşe Kaya · signs in with Kurum AD · Publisher (select disabled, small link "change on Sign-in") · Creator · Active · 12 · 9 Apr 2026; mehmet.t · Mehmet Tan · Publisher · Creator · Active · 9 · 2 May 2026; zeynep.a · Zeynep Arslan · User · Contributor · Active · 0 · 18 Jun 2026; selin.d · User · Contributor · Active · 0; okan.b · User · Creator · Active · 3; gis-viewer · Viewer · Viewer · Disabled · 0.
Footnote: "A member who owns items is removed by transferring them first; nothing is deleted silently."

---

## SV-S5 · Server › Security › Roles

Graticula Server — Security › Roles (route /server/#/roles), desktop, light, Graticula Hybrid design system (teal #0d7d70, Inter, 5px radius, dense, no decoration). Structure like ArcGIS Portal's Member roles editor (role name, description, "Set from existing role", collapsible General / Administrative privileges with Enable all, checkbox groups in columns); Graticula's own look. Add nothing not listed.
Top bar: as Security pages — nav Services, Site, Security (selected), Logs; Server badge; health line; switch; name; Sign out. No avatar.
LEFT PANEL: "Members", "Roles" (selected), "Sign-in", "Apps".
MAIN two columns:
LEFT (38%): heading "Roles", muted "5 roles · 38 privileges", secondary "New role". Table Role | Privileges | Members | (delete): Administrator "built in" 38 1; Publisher "built in" 21 2 (selected, teal-soft); User "built in" 9 3; Viewer "built in" 4 1; Field editor 7 0 with a small danger "Delete".
RIGHT (62%) editor: "Role name" [Publisher] (read-only for built-in, muted "built in"); "Description" textarea; "Held by 2 members"; link "Set from existing role ▾"; note "Creator or Contributor user types can hold this role."
"General privileges — 15 of 22" heading with "Enable all · Disable all" on the right; under it checkbox groups in three columns, each group with a bold parent checkbox: Content (Create, update and delete; Publish hosted feature layers; Publish hosted tile layers; Share with the organization; Share publicly — unticked), Groups (Create groups; Join groups outside the organization — unticked), Data (Edit features; Export data).
Collapsed "Administrative privileges — 6 of 16" with chevron.
Footer right: primary "Save privileges", secondary "Cancel".

---

## SV-S6 · Server › Security › Sign-in

Graticula Server — Security › Sign-in (route /server/#/signin), desktop, light, Graticula Hybrid design system (teal #0d7d70, Inter, 5px radius, dense, no decoration). Structure like ArcGIS Portal's Organization › Settings › Security › Logins section (help paragraph, "New SAML login" / "New OpenID Connect login" buttons, one bordered row per login with its switch); Graticula's own look. Add nothing not listed.
Top bar: nav Services, Site, Security (selected), Logs; Server badge; health line; switch; name; Sign out. No avatar.
LEFT PANEL: "Members", "Roles", "Sign-in" (selected), "Apps".
MAIN: heading "Sign-in", paragraph "How people sign in to this server. Built-in accounts always work; each provider below can be turned off without deleting it."
Buttons row: secondary "New OpenID Connect provider", "New SAML provider", "New directory (LDAP)".
List of bordered rows (one per provider), each: name bold + kind pill (OIDC / SAML / Directory) + issuer or address in muted monospace; middle: "First sign-in: create a User" and "Accounts 14"; right: an On/Off switch with its word, and text buttons "Check", "Edit", "Groups", "Remove".
Rows: "Built-in accounts" (no kind pill, switch On and disabled, note "always on"); "Kurum AD" Directory ldaps://dc01.ibb.local:636 · Accounts 14 · On; "Entra ID" OIDC https://login.microsoftonline.com/…/v2.0 · Accounts 3 · On; "e-Devlet test" SAML · amber note "certificate expires in 12 days" · Accounts 0 · Off.
Below: an expanded "Groups — Kurum AD" panel: table Their group | Role here | Group here | Remove — "GIS-Admins" → Administrator → —; "Planlama" → Publisher → planners; buttons "Add a group", "Save groups", "Close"; amber warning "A row maps to Administrator."

---

## SV-S7 · Server › Security › Apps

Graticula Server — Security › Apps (route /server/#/apps), desktop, light, Graticula Hybrid design system (teal #0d7d70, Inter, 5px radius, dense, no decoration). Add nothing not listed.
Top bar: nav Services, Site, Security (selected), Logs; Server badge; health line; switch; name; Sign out. No avatar.
LEFT PANEL: "Members", "Roles", "Sign-in", "Apps" (selected).
MAIN: heading "Apps", paragraph "Apps registered here may sign people in to this server with OAuth (ArcGIS Pro, Field Maps, your own)." Primary "Register an app".
An inline form card (open): Name [Field survey], "Redirect addresses, one per line" textarea (https://survey.ibb.gov.tr/callback), "App ID (optional)" input; buttons Register, Cancel.
Table: App (name + muted "Came registered with this server" or "Registered 2 Sep 2026") | App ID (monospace) | Returns to (monospace, truncated) | Remove (text button).
Rows: "ArcGIS Pro" · arcgisprodesktop · urn:ietf:wg:oauth:2.0:oob · "Came registered with this server"; "Field Maps" · arcgisfieldmaps · arcgis-fieldmaps://auth; "Planning dashboard" · 9f2c41ab77 · https://plan.ibb.gov.tr/auth.

---

## SV-L1 · Server › Logs

Graticula Server — Logs (route /server/#/logs), desktop, light, Graticula Hybrid design system (teal #0d7d70, Inter, 5px radius, dense, no decoration, no charts). Structure like ArcGIS Server Manager's Logs › View Logs (heading, help sentences, a filter row ending in a Query button, a Level/Time/Message/Source table, a footer "Showing logs from … to …" with Newer / Older); Graticula's own look. Add nothing not listed.
Top bar: nav Services, Site, Security, Logs (selected); Server badge; health line; switch; name; Sign out. No avatar.
LEFT PANEL: "Administration" (selected), "Requests", "Studio viewer", "Server warnings".
MAIN: heading "Administration log" with "Help" on the right; one sentence "Every change made through Server and Studio, newest first."
Filter row: "Contains" input, "Who" input, "Since" [Last day ▾], "Action" [All (312) ▾], checkbox "Only failures", primary "Query".
Table: When | What | Who | From | Resource (monospace). Rows: 10:41:12 service.start erdinc 10.2.0.31 Planning/parcels; 10:38:55 service.cache.clear ayse.k 10.2.0.44 Planning/istanbul_districts (expanded: monospace JSON block {"service":"Planning/istanbul_districts","tiles":18402,"bytes":222298112}); 10:22:03 member.role erdinc 10.2.0.31 "zeynep.a → User"; 09:58:40 sharing.set mehmet.t 10.2.0.52 "Transport/roads → Everyone"; 09:31:17 red pill "refused" + service.delete zeynep.a 10.2.0.60 Planning/zoning_plan; six more.
Footer bar: "Showing 10:41 back to 09:02" left; secondary "Older" right.

---

## SV-V1 · Server › Services › Manage services (four-tab revision)

Graticula Server — Services › Manage services (route /server/#/services/Planning), desktop, light, Graticula Hybrid design system. Structure like ArcGIS Server Manager's Manage Services (Folders box on the left with a new-folder button, search over services, Publish button top right, one row per service with thumbnail and start/stop/delete actions); Graticula's own look. Add nothing not listed: no instance counts, no runtime, no Restart.
Top bar: nav exactly Services (selected), Site, Security, Logs; teal "Server" badge; health line; switch; name; Sign out. No avatar.
LEFT PANEL: "Manage services" (selected), "Publish a service"; "Folders" with "+" button: Site (root) 0, Planning 11 (selected), Transport 8, Imagery 5, Basemaps 3, Utilities 1; "Service health": Started 26, Stopped 2, Error 0 (dimmed), Total 28; "Server resources": CPU 12 %, Heap 412 MB, Tiles 3.1 GB with tiny sparklines, Uptime 6 days.
MAIN: breadcrumb "Site (root) › Planning"; title "Services"; primary "+ New service" top right. Toolbar: search "Search services in every folder…", muted "11 services in Planning", secondary "Group layers…", refresh icon.
Table: thumbnail | Service (teal name + muted "Feature service · vector tiles · 2 layers") | Status pill with word | Sharing pill | Owner (plain Inter) | actions: Stop/Start, "Map" link, ⋯.
Rows: istanbul_districts Started Organization erdinc; parcels Stopped Groups (2) erdinc; zoning_plan Started Organization ayse.k; land_use_2026 Started Everyone erdinc; buildings Started Groups (1) ayse.k; green_areas Started Owner mehmet.t; address_points Started Organization erdinc; census_tracts Started Organization mehmet.t. Pager "1–8 of 11".

---

## SV-V2 · Server › Services › editing a service

Graticula Server — editing a service, General section (route /server/#/service/Planning/istanbul_districts?section=general), desktop, light, Graticula Hybrid design system. Structure like ArcGIS Server Manager's service editor: a title row "Editing: Site (root) › Planning › istanbul_districts" with Help, "Save" and "Cancel" on the right; a left list of sections; settings on the right as grey bold section headings with labels left and controls in a right column. Graticula's own look. Add nothing not listed: no Pooling, Processes or Caching sections.
Top bar: nav Services (selected), Site, Security, Logs; teal "Server" badge; health line; switch; name; Sign out. No avatar.
Title row as above, plus status pill "Started" and secondary "Stop" and "Open the item in Studio".
LEFT LIST (220px): "General" (selected), "Parameters", "Capabilities", "Layers".
RIGHT:
"General": Name istanbul_districts; Type Feature service; Folder Planning; Owner erdinc; Sharing Organization pill (link "Change in Studio").
"URLs": REST URL (monospace link) …/rest/services/Planning/istanbul_districts/FeatureServer; Vector tiles …/VectorTileServer; Map image …/MapServer; WMS …/MapServer/WMSServer; each with a Copy button.
"Layers": "2 layers · 0 groups" with link "See Layers".

---

## SV-V3 · Server › Services › editing a service › Parameters

Same shell and title row as SV-V2. LEFT LIST: General, Parameters (selected), Capabilities, Layers.
RIGHT, Server Manager-style sections:
"Response": Maximum number of records returned [2000]; Maximum response size [50] MB.
"Request": Maximum request size [10] MB; Maximum edits in one request [1000].
"Time": Client deadline [60] seconds; Statement timeout [30000] ms.
Muted hints under each section ("Defaults come from the server; a blank field uses them.").

---

## SV-P1 · Server › Services › Publish a service

Graticula Server — Publish a service (route /server/#/publish), desktop, light, Graticula Hybrid design system (teal #0d7d70, Inter, 5px radius, dense, no decoration). Add nothing not listed.
Top bar: nav Services (selected), Site, Security, Logs; Server badge; health line; switch; name; Sign out.
LEFT PANEL: "Manage services", "Publish a service" (selected).
MAIN: title "Publish a service", lede "Compose layers from registered databases, preview them, then publish them as one service." Buttons right: secondary "Preview", ghost "Clear", primary "Publish…" (disabled).
Three panes side by side:
"Contents" (260px): a tree — root "New map · EPSG:3857" with ⋯; children "parcels" (polygon swatch), "zoning" (polygon swatch), "roads" (line swatch); a right-click style menu open on "zoning": Rename, Group, Zoom to layer, Remove, Properties.
"Preview" (centre): a map of Istanbul with the three layers drawn by the server over a light ground; muted "Drop a table here".
"Databases" (280px): tree "ibb_planning" › parcels, zoning, roads, buildings; "census_parquet" › tracts_2025; each table draggable (grip icon).

---

## ST-G2 · Studio › Groups list (re-send if the 15:xx one never appeared)

(see the prompt sent at 15:xx; Portal structure: "+ New group" button above a filter column on the left — "Owner: Owned by me / Owned by others", "You are: Owner / Manager / Member" — search and sort on the right, then the table.)

## ST-D1 · Studio › Domains

Graticula Studio — Domains (route /studio/#/domains), desktop, light, Graticula Hybrid design system (teal #0d7d70, Inter, 5px radius, dense, no decoration). Add nothing not listed.
Top bar (white 56px): navy square logo with a white 3x3 grid glyph, "GRATICULA", small violet "Studio" badge. Nav: My content, Map, Anonymous view, Groups, Domains (selected). Right: segmented "Server | Studio" (Studio selected), "Erdinç Yıldız" / "administrator", "Sign out". No avatar.
MAIN: title "Domains", lede "Lists and ranges of allowed values that several layers' fields share." Primary "New domain".
Table: Domain | Values | Used by | Owner | actions.
Rows: yaka_values · "List: Avrupa, Anadolu" · details "3 fields" (expanded: istanbul_districts · yaka, neighbourhoods · yaka, census_tracts · side — each a link) · erdinc · "Change"; landuse_codes · "List: 14 values" · "6 fields" · ayse.k · Change; floor_count · "Range 0 – 120" · "1 field" · mehmet.t · Change; old_status · "List: 3 values" · "Not used" · erdinc · Change, Delete.
An inline editor row open under "floor_count": name input, radio List / Range (Range selected), Min [0] Max [120], Save, Cancel.

## ST-A1 · Studio › Anonymous view

Graticula Studio — Anonymous view (route /studio/#/anonymous). Same Studio top bar with "Anonymous view" selected.
MAIN: title "Anonymous view", lede "What somebody who is not signed in actually gets from each layer you can see." Primary "Run the probe"; muted "Last run 10:12 · 48 layers".
Table: Layer | Shared | Service | Layer request | Count | Reality against intent. Rows: istanbul_districts/districts · Organization · 401 · 401 · — · green "Hidden, as shared"; roads/roads · Everyone · 200 · 200 · 94,120 · green "Public, as shared"; parcels/parcels · Groups (2) · 401 · 401 · — · green; land_use_2026/landuse · Everyone · 200 · 403 · — · amber "Shared publicly but the layer refuses"; one red row "Owner · 200 · 200 · 812 · Reachable although private".
Footnote: "A 404 means the server does not admit the layer exists, which is how a private layer should answer."

## ST-M1 · Studio › Web map item

Graticula Studio — web map item page (route /studio/#/map/7f3a…), same Studio top bar, "My content" selected. Breadcrumb "My content › Land use 2026"; title "Land use 2026"; subtitle "Web map · 4 layers · owner erdinc". No tabs.
Two columns like the feature-layer Overview: MAIN — card with the saved map picture, summary "Land use plan, 2026 revision", tag chips, "Description" paragraph, button "Edit title, summary, tags and description"; card "Layers" — rows with a kind tag (Features / Vector tiles / Map image), the layer title as a link and muted "styled in this map · filtered".
ASIDE "Details": primary "Open in Map Viewer", secondary "Share", danger-outline "Delete" (disabled) with checkbox "Protect from deletion" ticked; "Link" read-only field + "Copy link"; facts Type Web map, Owner erdinc (Change owner…), Sharing Everyone (blue-soft pill), Layers 4, Created 2 Sep 2026, Updated 11 Oct 2026.

## ST-V1 · Studio › Map Viewer

Graticula Studio — Map Viewer (/studio/webmap.html?id=…), desktop, light, Graticula Hybrid design system. Structure like ArcGIS Map Viewer: narrow header with the map title and Save; a left vertical toolbar of icon+label buttons; a Layers panel beside it; the map filling the rest; a popup card on the map. Graticula's own look. Add nothing not listed: no charts, sketch, analysis or app-builder buttons.
Header (48px): navy logo, map title "Land use 2026" with a pencil, muted "Saved", primary "Save" (hidden state shown as disabled), link "My content".
Left toolbar (72px, labels under icons): Layers (selected), Search, Measure, Bookmarks, Print, Map.
Layers panel (300px): title "Layers", buttons "Add layer" and "Zoom to all"; rows each with visibility checkbox, swatch, name, ⋯ and up/down; first row expanded showing Opacity slider and buttons Style, Pop-up, Labels, Table, Filter.
Map: Istanbul land use in categorical colours, scale and coordinates strip at the bottom, +/− at bottom right; a popup card "Parcel 4412" with attribute rows and buttons "Edit", "Zoom to".
Bottom: a closed "Table" drawer handle.

## ST-L1 · Sign-in

Graticula — Sign in page (any surface when signed out), desktop, light, Graticula Hybrid design system. Centered card 400px on Paper background, no top nav.
Card: navy logo + "GRATICULA", heading "Sign in to Graticula", muted host "demo.graticula.com".
Buttons stacked: secondary "Sign in with Kurum AD", "Sign in with Entra ID"; divider "or with an account on this server".
Fields: Name; Password with a "Show" toggle; checkbox "Keep me signed in"; primary full-width "Sign in"; link "Forgot password?".
Below the card, muted: "Graticula Server and Studio · v1.0.381".

## ST-X1 · Export data dialog

Over the istanbul_districts item Overview (dimmed). Modal 560px "Export data" with ×. "Layers": checkboxes districts (ticked), neighbourhoods (ticked). "With the geometry": radio list GeoPackage (selected), File Geodatabase, Shapefile, KML, GeoJSON, Esri JSON. "As a table": CSV, Excel. Muted link "Tile packages are in Settings › Tile layer". Footer: status "Ready"; Cancel; primary "Download".

## ST-U1 · Update data dialog

Over the item Overview (dimmed). Modal 640px "Update data" with ×. "Layer" select [districts]. "What to do" radio cards: "Add features", "Update matching features, add the rest" (selected, with "Match on" select [ilce_kodu]), "Replace all features" (danger tone). File input "districts_2026.gpkg · 2.1 MB". A mapping table from the dry run: Your column → Layer field (ilce_adi → ilce_adi; nufus → nufus_2025; kod → ilce_kodu; extra_col → "not used"). Collapsed "What is kept". Footer: Cancel, primary "Update and add".

---

## FIX-1 · Consistency pass (edit_screens over every draft)

Apply to the selected screens, change nothing else:
1. Logo: navy square #171b49 with a white 3x3 grid glyph, the same on every screen.
2. No avatar/initials circle in the header; just name, role line, Sign out.
3. Server screens: the nav is exactly four items — Services, Site, Security, Logs.
4. Dates are plain Ink text, never teal.
5. Checkboxes and radios use the teal accent #0d7d70.
6. Remove any explanatory sentence that is not in the prompt (e.g. "Generate tile bundles…", "Package tiles into portable…").
7. My content (Balanced): remove the "Storage quota" block; folders without emoji; thumbnails are small map previews.

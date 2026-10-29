# ADR-223 — User tools are Python toolboxes written against grapy, run in a sandboxed process with no network

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — every mechanism named is a documented, unprivileged Linux facility, and none is yet measured holding against a hostile tool here; the conditions are the measurements |
| **Decided** | 2026-10-29, by owner decision, four answers put as questions with their consequences: isolation *"Ayrı süreç, sınırlı"*; network *"Hayır"*; who publishes *"Yeni bir yetki"*; the form *"arcpy yapısına benzer bir yapı olsun. ama arkada grapy çalışsın. metodlar birebir oluşturulsun. ekstra toollar da olabilir."* Before them, [Q-192](../open-questions.md): *"Kullanıcı araçları da gelsin"*. What this ADR adds to those answers is **INFERRED** and listed in §5.9 for confirmation in [Q-194](../open-questions.md). |
| **Supersedes** | [v1-scope](../v1-scope.md) §3c's *"the Python SDK, the sandbox and the user wheel set remain out"* — reversed by Q-192, designed here |
| **Superseded by** | — |

---

## 1. Context

[ADR-222](ADR-222-geoprocessing-is-built-in-analysis-tools.md) built the analysis tools this server writes. The owner then
brought **tools users write** into scope (Q-192), which v1-scope §3c had cut for one reason, written there in one
sentence: *"their tool against our server is arbitrary code execution."* Review finding O3 called it *"the largest
security surface in the product by a wide margin"*, and [Q-75](../open-questions.md) has held the open half since
2026-08-13: process user and container boundary, CPU / memory / wall-clock / disk limits, network, the data path.

The owner answered the four choices that are theirs on 2026-10-29. This ADR is the design under those answers.

ArcGIS's equivalent is the **web tool**: a Python toolbox (`.pyt`) or a model published from Pro to a GIS Server site and
served as a GPServer, run by the site's own Python with `arcpy`. `arcpy` is Esri's closed library and cannot run here; the
owner's answer is a library of our own — **grapy** — shaped as `arcpy` is, so a tool written for one reads like a tool for
the other.

## 2. Alternatives considered

### Alternative A — One sandboxed process per run, on the server's machine, fenced by the kernel (chosen)

The owner's answer. The tool runs as a child process of the server, and before it runs a line of the user's code the
process fences itself: Landlock for the filesystem, seccomp for sockets and the system calls a tool has no business
making, rlimits for memory, CPU time, file size and processes; the server kills it at the wall-clock limit. No new
dependency: these are syscalls an unprivileged process may make on itself.

### Alternative B — A container per run

Stronger isolation, but the server would have to drive Docker, which is a second dependency, and access to the Docker
socket is root on the host — the cure larger than the disease. The owner chose A.

### Alternative C — A separate worker machine

The deployment target is `graticula → PostgreSQL` (CLAUDE.md §6). A second machine for the first user tool is the shape
§6 tells us to justify by evidence first.

### Alternative D — An `arcpy` compatibility module

Shipping a module named `arcpy` would let ArcGIS tools run unchanged in name, and would be a module that claims to be
Esri's while being something else — a tool that ran would still behave unlike ArcGIS wherever grapy differs, and the name
is Esri's. Rejected for the name, not the shape: grapy keeps `arcpy`'s documented names one to one, and a tool begins
`import grapy as arcpy`.

## 3. Counterarguments to the preferred option

- **Same machine, same kernel.** A kernel exploit escapes Landlock and seccomp both; a container shares the kernel too, so
  B does not change that, only C does. Recorded, not answered.
- **Same user.** The tool runs as the server's own OS user, because changing user needs privilege the server does not
  hold. Landlock is what keeps it out of the server's files, and `/proc` is outside what it may read, so the server's
  environment — its keys and connection strings — is not readable through `/proc/<pid>/environ`.
- **Windows.** Landlock and seccomp are Linux's. A Windows host has job objects for limits and nothing comparable for the
  filesystem and network without administrator rights, so **user tools do not run on Windows** unless an operator
  switches on a development setting that says, in its name, that it is unsafe.
- **"birebir" is large.** `arcpy` documents well over a thousand tools. The surface is built in order of use and counted
  against `arcpy`'s published reference (condition 6), so what is and is not there is a number, not a claim.

## 4. Evidence

- Landlock: Linux 5.13+ restricts filesystem access for an unprivileged process; ABI 4 (Linux 6.7+) restricts TCP bind
  and connect. seccomp-bpf with `PR_SET_NO_NEW_PRIVS` is available to any process. The showcase VPS and CI runners'
  kernels are measured in condition 1.
- The data path is the one ADR-037 settled for imports: files in a job directory, no database connection held by
  anything but the server.
- ArcGIS's Python toolbox shape — `Toolbox`, `Tool`, `getParameterInfo`, `execute(parameters, messages)`,
  `isLicensed`, `updateParameters`, `updateMessages` — is publicly documented ("Defining a tool in a Python toolbox").

## 5. Decision

1. **A user tool is a Python toolbox** — one `.pyt` or `.py` file, `class Toolbox` naming its tools, each `class Tool`
   with `label`, `description`, `getParameterInfo()`, `execute(parameters, messages)`, and the optional
   `isLicensed`, `updateParameters`, `updateMessages` — written against **grapy**: `import grapy as arcpy`.
2. **grapy keeps `arcpy`'s documented names, one to one**, and implements them on shapely, pyproj, pyogrio and pyarrow:
   `Parameter`, `AddMessage`/`AddWarning`/`AddError`, `GetParameterAsText`/`SetParameter`, `env`, `Describe`,
   `ListFields`, `SpatialReference`, the geometry classes, `da.SearchCursor`/`InsertCursor`/`UpdateCursor`, and the
   `analysis` and `management` tools in order of use; a name not yet built raises `NotImplementedError` naming it.
   **Extra tools** live under `grapy.graticula`, never under an `arcpy` name.
3. **It runs in a child process fenced before the user's code is imported** (Alternative A): Landlock — read the
   Python runtime and grapy, read and write the run's own directory, nothing else; seccomp — no `socket` outside
   `AF_UNIX`, no `ptrace`, `mount`, `unshare`, `setns`, `keyctl`, `bpf`; rlimits — address space, CPU seconds,
   file size, processes, open files; a clean environment holding nothing of the server's; the server kills its process
   group at the wall-clock limit. **A fence that cannot be put up stops the run**: an older kernel is a refusal with the
   reason, never a run without it.
4. **No network** (owner): seccomp refuses the socket, and Landlock's network rules refuse TCP where the kernel has them.
5. **Data crosses as files.** Each layer input is written to the run's directory as a GeoPackage by the server; the tool
   reads and writes feature classes there through grapy; each output feature class is imported as a hosted layer, as an
   upload is. The tool never sees a connection string.
6. **No packages at runtime** ([Q-76](../open-questions.md), ADR-016 §7): the image carries a curated wheel set — numpy,
   pandas, shapely, pyproj, pyogrio, pyarrow, geopandas — and grapy.
7. **Publishing takes a new privilege, `content:publishTools`**, held by no built-in role but the administrator (owner:
   *a new privilege*; which roles hold it is the administrator's). A published toolbox is an item served as a GPServer,
   `/rest/services/{folder}/{name}/GPServer`, one task per tool, its parameters read from `getParameterInfo` by a fenced
   run at publish; every task is asynchronous. Running a tool takes read access to its item and a signed-in caller.
8. **A run is a job** (`tool.run`), as an analysis is: its parameters, messages and results kept, its status the GP
   job's, cancel stops the process.
9. **INFERRED, for confirmation in Q-194:** (a) no `arcpy`-named module — `import grapy as arcpy`; (b) user tools
   refused on Windows unless an operator opts into an unsafe development setting; (c) default limits — 2 GB of memory,
   10 minutes of CPU, 30 minutes of wall clock, 1 GB written, and a ceiling of 1,024 tasks — *not* 64 as first written:
   Linux counts RLIMIT_NPROC per OS user, the server's own threads included, so it is a ceiling against a fork bomb
   rather than a count of the tool's processes (found writing the fence, 2026-10-29); (d) `content:publishTools` held by the
   administrator role only by default; (e) GeoPackage as the exchange file.

## Conditions

1. **The fence is measured holding**, on the CI runner's and the showcase's kernels: a tool that opens a socket, reads
   the server's state directory, reads `/proc/1/environ`, forks without limit, allocates past its memory, or loops past
   its time is stopped, each by name, and the run says which fence stopped it. **PARTLY DISCHARGED 2026-10-29** — on the showcase's machine (Linux 6.1, arm64, inside Docker's own seccomp profile) by grapy's `TheFenceHolds`: a socket, a file outside the run, `/proc/1/environ`, memory past the limit and a process are each refused, and the same toolbox run unfenced did all four, the control that shows the tests can fail. CI runs the same tests on its runner's kernel (the `grapy` job, which also fails by name on a kernel without Landlock) and runs every conformance tool fenced; the showcase image itself is measured when it ships.
2. **A Python toolbox publishes** as an item and a GPServer whose tasks and parameters are read from its tools. **DISCHARGED 2026-10-29** by `AToolboxIsPublishedAndRunTests`: two tools, their parameters' types and directions as the task resource lists them; a toolbox with no `Toolbox` or one that will not import is refused with what its describe run said.
3. **A run reads a hosted layer and writes one**, through `da.SearchCursor` and `da.InsertCursor`, end to end. **DISCHARGED 2026-10-29**, the same test: a number back typed as its parameter declares (a GPLong is a number), and a layer back — every point buffered — published as the caller's and counted; a required parameter not sent is refused before a run is queued.
4. **The privilege exists** and a member without it is refused publishing with its name. **PARTLY DISCHARGED 2026-10-29**: `content:publishTools`, last in the enum so no stored grant moves, held by the administrator role alone (`RolePrivilegeCatalogueTests`), and the publish route asks it first. Open: the refusal of a member without it, asserted.
5. **Studio publishes and runs a toolbox**, designed with the ux-designer agent.
6. **grapy's surface is counted** against `arcpy`'s published function and tool list by a tool in this repository, and
   the count is on the status page.

## 6. Consequences

- The server image carries a Python runtime and a wheel set again — the cost v1-scope §3c's amendment named — and A-049
  (that a curated set covers realistic work) becomes load-bearing.
- A new job kind and table, a new privilege, a new item type.
- The README's limits change from *"nothing a user uploads is run"* to what condition 1 measured.
- **State.** In the catalogue: the toolbox as an item with its source and its tools' parameter lists; a `job` row and a
  run row per run. On disk: the run's directory, removed when the run's results are imported. At runtime: one child
  process per running tool, held by the node that claimed the job under its lease.

## 7. Assumptions this decision rests on

- Deployments run Linux 5.13 or later (Landlock); network rules want 6.7 (A-NEW, measured in condition 1).
- A curated wheel set covers the tools people write (A-049).

## 8. Dependencies

ADR-222 (the GPServer protocol and jobs), ADR-037 (jobs and files), ADR-015 §7 (who publishes code), ADR-016 §7 (nothing
fetched at runtime), Q-74, Q-75, Q-76.

## 9. Revisit triggers

- A kernel exploit class that defeats Landlock or seccomp: Alternative B or C.
- A deployment on Windows that needs user tools.
- A user tool that needs a package outside the set.

## 10. Dissent

Recorded in §3: the same kernel and the same OS user are real limits of the owner's choice, accepted with it, not hidden.

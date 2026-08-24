# D-94 — what the geodatabase reader holds, and what a ceiling on it costs

**Run 2026-08-24.** Windows 11, the reader as the build ships it beside
`Graticula.Host.Tests` — GDAL 3.13.1 with its native payload. `peak.py` in this directory
starts the child with the same four environment variables the host sets, feeds it one
`ping`, and samples its working set every 5 ms from outside through
`GetProcessMemoryInfo`.

[D-94](../../docs/architecture-debt.md) said the reader runs inside the serving process
where ADR-016 §2 puts it in its own container, and named the gap in its own words: *what
it does not provide is a CPU or memory bound*. Two numbers had to exist before a ceiling
could be chosen, because a ceiling picked without them is a number that either does
nothing or kills healthy work.

## What the child actually holds

| | |
|---|---:|
| Peak working set answering `ping` | **38.2 MB** |
| Time to answer | 0.113 s |
| Samples taken while it ran | 20 |
| Ceiling now enforced | 2,048 MB |
| Headroom | **54×** |

`ping` is the largest allocation the child makes before it has read anything: it is
GDAL's whole native payload loading, both drivers registered. Every archive this
repository has is refused before it is parsed — there is no real geodatabase in the tree,
which is [D-95](../../docs/architecture-debt.md) — so **the reading side of this table is
unmeasured, and the ceiling is a judgement rather than a measurement.** What the number
above establishes is only the floor: 2 GB is not a limit a healthy child brushes against,
by a factor of fifty.

The three real archives the reader was measured against — 12, 55 and 8 layers,
[file-geodatabase-readers.md](../../docs/research/file-geodatabase-readers.md) §8f — are
somebody's data and are not in this repository, so they cannot be re-run here. The
listing times recorded there (largest 0.06 s) say what they say about time and nothing
about memory.

## What the guard costs

The parent polls four times a second. One sample is `Process.Refresh()` and one read of
`WorkingSet64`, which on this machine is **0.6 µs** — 10,000 samples in 6 ms. Over the
whole two-minute deadline that is **0.29 ms of parent CPU**, against a child that
allocates in megabytes.

The cost that is not free is the resolution: **a parser can allocate a gigabyte between
two samples**, so this bound is not exact. It turns a runaway child from something that
holds the machine for two minutes into something that holds it for a quarter of a second
past the ceiling. A job object on Windows and a cgroup on Linux would be exact; they are
two platform-specific paths to keep in step, and the row records them as the stricter
thing this is not.

## Priority

The second half is a line rather than a measurement: the child is started
`BelowNormal`. It still competes for the machine and now loses. `ping` answers with the
priority it is running at, so the setting is observable from outside — the test asserts
`BelowNormal` and fails with `Normal` when the call is removed, which is how it was
verified.

No before-and-after timing is reported for the priority change, and that is deliberate:
on an idle machine it changes nothing, and the case it is for — an import competing with
served requests — needs an import of a real archive to stage. That is D-95's dataset
again.

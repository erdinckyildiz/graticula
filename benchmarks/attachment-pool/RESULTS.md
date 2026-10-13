# The attachment pool under slow readers

**Run 2026-10-12** for [ADR-013](../../docs/adr/ADR-013-feature-service-data-model.md) condition 2, which asks for
*eight concurrent throttled reads against a layer with attachments, showing the ninth waits and the feature path keeps
serving*, and whether *a size threshold plus buffer-and-release is needed on top of pool isolation*.

Script: [`pool.py`](pool.py). It defines a point layer, adds one feature with one attachment, and opens N downloads.
Each download reads through a 16 KB receive buffer at 100 kB/s, a slow mobile connection, and gives up after a hold
time. Meanwhile it sends a feature query every half second. The layer is dropped afterwards.

| | |
|---|---|
| Machine | the local Windows machine, loopback |
| Server | v1.0.338's code, Debug build, the local console fixture on 18495 |
| Database | portable PostgreSQL 16 + PostGIS 3.6, `max_connections` 100 |
| Attachment pool | `MaxPoolSize = 8`, no statement timeout (`LayerConnections.BuildAttachmentPool`) |

## 1. Nine readers of a 24 MB attachment

| Reader | First byte | How it ended |
|---|---|---|
| 1–8 | 15–47 ms | `200`, still reading when the 40 s hold ran out (4.0 MB each) |
| **9** | **15,015 ms** | **`504 Gateway Timeout`**, no body read |

The feature queries alongside: **76 queries, median 16 ms, max 47 ms, none failed.**

**This is the claim ADR-013 §4b makes, measured.** Eight slow readers hold all eight connections for as long as they
read, at 100 kB/s about four minutes for this file. The ninth waits for Npgsql's 15-second pool timeout and is answered
504. The feature path, on its own pool, does not notice.

**Control:** eight readers of the same file. All eight started in 0–16 ms, and 38 feature queries had a median of 16 ms
and none failed. So the ninth's wait comes from the pool's eight, not from the machine.

## 2. Twenty readers of a 1 MB attachment

| Readers | First byte |
|---|---|
| 1–8 | 0–16 ms |
| 9–16 | 6.3–7.0 s |
| 17–20 | **12.6–13.2 s** |

All twenty read to the end with `200`. Feature queries: 45, median 16 ms, max 47 ms, none failed.

**A small attachment does not release its connection early.** The question was whether server and socket buffers absorb
a small file so the connection comes back at once. They do not. A 1 MB attachment held its connection for about 6.5 s
of a 10-second read, so the readers went through in waves of eight. The third wave waited 13 s, two seconds short of
the timeout. A fourth wave, readers 25 to 32 in a run like this one, would get the 504.

## What this answers, and what it leaves

- **Pool isolation does what it was split for.** A slow reader stops attachments, not the layer.
- **The bound decides how many slow downloads can run at once, and nothing else.** Each slow reader holds one
  connection for the whole read, whatever the file's size. So a size for the pool cannot come out of a throughput
  number. It is the number of database connections the deployment will give to people reading slowly: eight of 100 here.
- **Buffer-and-release would change the small-file case, which is the common one.** A photo from Field Maps is 1–5 MB,
  and its reader is the slow mobile client measured here. Read into memory and released, it would hold its connection
  for milliseconds instead of seconds. The cost moves to memory: once a buffered download no longer waits for a
  connection, nothing bounds the bytes held. It needs a per-file threshold **and** a cap on the total bytes buffered at
  once. Both are a decision, and this run measures why it is worth taking. It does not take it.

## 3. After buffer-and-release — 2026-10-13

The owner chose the two numbers: attachments up to 8 MB are read whole and their connection given back before the
client is written to, with at most 128 MB held at once (`AttachmentBuffer`). Run 2 again, on the same fixture:

| Readers | First byte, before | First byte, after |
|---|---|---|
| 1–8 | 0–16 ms | 0–31 ms |
| 9–16 | 6.3–7.0 s | 0–31 ms |
| 17–20 | 12.6–13.2 s | 0–31 ms |

All twenty read to the end with `200`. The waves are gone: twenty phones reading a photo hold twenty megabytes for
as long as they read, and no connection. A 24 MB attachment is over the 8 MB threshold and streams exactly as in
run 1, so the ninth of nine such readers still waits for the pool; that case is what the pool's eight is for.

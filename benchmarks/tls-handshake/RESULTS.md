# What a TLS handshake to the datastore costs

**Run 2026-08-27.** **Settles:** [ADR-014](../../docs/adr/ADR-014-tls-and-certificates.md)
condition 2. **Runner:** [`run.py`](run.py).

---

## The question

[ADR-014](../../docs/adr/ADR-014-tls-and-certificates.md) §3's cost note names an interaction
between two decisions taken separately:

> **ADR-007 §4.8 shrinks idle pools to zero. Every pool refill is then a fresh TLS handshake,
> which is far more expensive than a plain connect.** This is a real interaction between two
> decisions and belongs in Q-04's connection budget measurement rather than being assumed
> away.

Nobody had put a number on it. [Q-04's measurement](../connection-budget/RESULTS.md) counts
backends and deliberately does not time anything, so the handshake was outside it.

## The numbers

Six runs, 60–100 connects per arm, against PostgreSQL 16 in `gis-experiment-postgis` with
`ssl = on` and a self-signed certificate, over Docker's published port. TLS 1.3,
`TLS_AES_256_GCM_SHA384`.

| | min | median |
|---|---|---|
| plain connect + SSLRequest | **1.77 – 2.25 ms** | 3.2 – 5.6 ms |
| the same, then the handshake | **4.67 – 5.79 ms** | 7.2 – 13.5 ms |
| **the handshake** | **≈ 2.8 – 3.5 ms** | 3.8 – 10.3 ms |

**The minimum is the figure, and that is a choice with a reason.** Across six runs the minima
sit inside 0.5 ms of each other and the medians move by a factor of nearly three. The path is
a Docker port proxy on Windows, and its median measures the scheduler. What a handshake
*costs* is what it costs when nothing is in the way. The median is reported beside it because
a deployment lives on the noisy number even though the clean one is the cost.

**About 2.5× a plain connect**, which is the same order §3 assumed without measuring — so the
sentence *far more expensive than a plain connect* was right, and it is now 2.8 ms rather
than an adjective.

## What it means for the budget

[ADR-046](../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md) bounds a
worker at 64 concurrent database operations and one source at 24. A pool that has shrunk to
zero pays the connect on every one of them:

| Refill from empty | connect, at best | of which handshake |
|---|---|---|
| 24 (one source's budget) | **112 ms** | 67 ms |
| 64 (the worker's budget) | **299 ms** | 178 ms |

**That is the cost of an idle period, paid by whoever arrives after it** — and it is per
source, so a deployment with several sources waking together multiplies it. It is not
catastrophic and it is not negligible: a third of a second of pure connect before the first
row moves, on the worker's whole budget.

## What this does not settle

- **Loopback, so both arms are missing the network.** A remote database adds round trips to
  both, and adds *more* of them to the handshake: TLS 1.3 is one extra round trip and TLS 1.2
  is two. On a 20 ms link the handshake's share grows rather than shrinks, so this figure is
  a floor.
- **No authentication, no startup packet, no driver.** Npgsql's pool bookkeeping and SCRAM
  are real and are not in this number; what is measured is the cost a refill pays *extra*
  because the connection is encrypted, which is what §3's note is about.
- **Certificate verification is off**, because the certificate is self-signed and validating
  it would measure this script's trust store. Chain validation is a few hundred microseconds
  of CPU; the asymmetric operation is what is counted here.
- **One machine, one server version, one cipher suite.** RSA-2048 at both ends.
- **This says nothing about whether pools should shrink to zero.** It says what the decision
  costs, which is what the condition asked for.

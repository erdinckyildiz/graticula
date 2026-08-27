#!/usr/bin/env python3
"""What a TLS handshake to the datastore costs, against a plain connect.

[ADR-014](../../docs/adr/ADR-014-tls-and-certificates.md) condition 2
---------------------------------------------------------------------
*Handshake cost enters Q-04's connection budget measurement rather than being assumed
negligible against shrink-to-zero pools.*

§3's cost note states the interaction: **ADR-007 §4.8 shrinks idle pools to zero. Every pool
refill is then a fresh TLS handshake, which is far more expensive than a plain connect.** Two
decisions taken separately, and until this nobody had put a number on where they meet.

What this measures
------------------
The connect, and nothing after it. Postgres announces TLS before it authenticates anything:
the client sends an eight-byte SSLRequest and the server answers with a single `S`, and
everything after that is an ordinary TLS handshake. So both arms do exactly the same TCP
connect and the same eight bytes, and only one of them goes on to negotiate — which makes
the difference the handshake and nothing else.

**No authentication, no query, no driver.** Npgsql's pool, its startup packet and SCRAM
would all be in the number, and none of them is what §3 is about. This is the cost a refill
pays *extra* because the connection is encrypted.

Usage:  python benchmarks/tls-handshake/run.py [host] [port] [rounds]
"""

import socket
import ssl
import statistics
import sys
import time

HOST = sys.argv[1] if len(sys.argv) > 1 else "localhost"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 55432
ROUNDS = int(sys.argv[3]) if len(sys.argv) > 3 else 60

# Length 8, then the magic 80877103.
SSL_REQUEST = bytes([0, 0, 0, 8, 4, 210, 22, 47])


def plain():
    """TCP connect plus the SSLRequest exchange, declining to go further."""
    started = time.perf_counter()

    with socket.create_connection((HOST, PORT), timeout=10) as raw:
        raw.sendall(SSL_REQUEST)
        answer = raw.recv(1)

    return (time.perf_counter() - started) * 1000, answer


def encrypted():
    """The same, and then the handshake."""
    started = time.perf_counter()

    with socket.create_connection((HOST, PORT), timeout=10) as raw:
        raw.sendall(SSL_REQUEST)
        answer = raw.recv(1)

        if answer != b"S":
            return None, answer

        # <b>Verification off, deliberately, and it does not flatter the result.</b> The
        # certificate is self-signed, and validating it would measure this script's trust
        # store rather than the handshake. Chain verification is a few hundred microseconds
        # of CPU at most; the asymmetric operation is the cost being counted.
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE

        with context.wrap_socket(raw, server_hostname=HOST) as tls:
            _ = tls.version()

    return (time.perf_counter() - started) * 1000, answer


def measure(what, rounds):
    # Two uncounted, because the first connect of a process pays for a DNS answer and a
    # warm socket layer, and counting it measures the warm-up.
    for _ in range(2):
        what()

    samples = []

    for _ in range(rounds):
        taken, answer = what()

        if taken is None:
            print("The server declined TLS (answered " + repr(answer) + "). "
                  "Nothing to measure: turn ssl on.", file=sys.stderr)
            raise SystemExit(2)

        samples.append(taken)

    samples.sort()

    return {
        "min": samples[0],
        "median": statistics.median(samples),
        "p90": samples[int(len(samples) * 0.9)],
        "max": samples[-1],
    }


def main():
    print(f"{HOST}:{PORT}, {ROUNDS} rounds each, milliseconds\n")

    without = measure(plain, ROUNDS)
    with_tls = measure(encrypted, ROUNDS)

    print(f"{'':16}{'min':>9}{'median':>9}{'p90':>9}{'max':>9}")

    for name, taken in (("plain connect", without), ("with handshake", with_tls)):
        print(f"{name:16}{taken['min']:9.2f}{taken['median']:9.2f}"
              f"{taken['p90']:9.2f}{taken['max']:9.2f}")

    # <b>The minimum, and that is a choice with a reason.</b> Five runs of this on the
    # same machine put the medians between 3.8 and 10.3 ms and the minima inside 0.25 ms
    # of each other: the path is a Docker port proxy on Windows and its median measures
    # the scheduler. What the handshake *costs* is what it costs when nothing is in the
    # way, and that number reproduces. The median is printed beside it rather than
    # hidden, because a deployment lives on the noisy number even though the clean one
    # is the cost.
    cost = with_tls["min"] - without["min"]
    ratio = with_tls["min"] / without["min"] if without["min"] else 0
    noisy = with_tls["median"] - without["median"]

    print(f"\nthe handshake costs {cost:.2f} ms of the cheapest connect, "
          f"{ratio:.1f}x a plain one")
    print(f"  at the median on this path it reads {noisy:.2f} ms, mostly scheduler")

    # <b>What it means for the budget, which is the reason the condition exists.</b>
    # ADR-046's worker budget is 64 and the per-source budget is 24. A pool that has shrunk
    # to zero pays this on every one of them.
    for permits in (24, 64):
        print(f"  refilling {permits} connections from empty: "
              f"{permits * with_tls['min']:.0f} ms of connect at best, of which "
              f"{permits * cost:.0f} ms is the handshake")


if __name__ == "__main__":
    main()

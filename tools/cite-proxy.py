#!/usr/bin/env python3
"""A forwarding proxy that signs every request in as one account, for the CITE engines.

Why this exists
---------------
[ADR-169](../docs/adr/ADR-169-wfs-t-through-the-one-write-path.md) condition 1: OGC's WFS 2.0 Transactional
class is run *as a caller who may edit*. TEAM Engine has no way to send a credential, and this server offers
WFS-T only to a caller who may edit -- rightly -- so the anonymous nightly run never reaches the class.

This sits between the two: it adds ``Authorization: Bearer <token>`` to every request and passes the
``Host`` header through unchanged, so the addresses the server writes into its documents point back at the
proxy and every later request the engine makes is signed in too. It forwards; it reads and changes nothing
else. It is a test fixture, used only by ``cite.yml``.

Usage:  tools/cite-proxy.py <listen-port> <target-base-url> <token-file>
"""

import http.server
import socketserver
import sys
import urllib.error
import urllib.request

LISTEN = int(sys.argv[1])
TARGET = sys.argv[2].rstrip("/")
TOKEN = open(sys.argv[3], encoding="utf-8").read().strip()
HOP = {"connection", "keep-alive", "proxy-authenticate", "proxy-authorization", "te", "trailers",
       "transfer-encoding", "upgrade", "content-length", "content-encoding"}


class Forward(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _forward(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length) if length else None
        request = urllib.request.Request(TARGET + self.path, data=body, method=self.command)

        # Accept-Encoding is dropped so the answer comes back as it is read: this does not decompress, and
        # passing a compressed body on without its Content-Encoding would hand the engine bytes it cannot read.
        for name, value in self.headers.items():
            if name.lower() not in HOP and name.lower() not in ("authorization", "accept-encoding"):
                request.add_header(name, value)

        request.add_header("Authorization", "Bearer " + TOKEN)

        try:
            response = urllib.request.urlopen(request, timeout=300)
            status, headers, payload = response.status, response.headers, response.read()
        except urllib.error.HTTPError as refused:
            status, headers, payload = refused.code, refused.headers, refused.read()

        self.send_response(status)
        for name, value in headers.items():
            if name.lower() not in HOP:
                self.send_header(name, value)
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    do_GET = do_POST = do_PUT = do_DELETE = do_HEAD = _forward

    def log_message(self, format, *args):  # noqa: A002 - the base class names it so
        pass


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


if __name__ == "__main__":
    Server(("0.0.0.0", LISTEN), Forward).serve_forever()

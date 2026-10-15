# Kept as evidence for ADR-067 condition 3 (2026-10-15): run with Python duckdb 1.5.5 and 1.5.6, all reads refused.
"""Does CREATE SECRET before the lock let a locked connection read outside allowed_directories? (ADR-067 §4)

A local HTTP server answers on 127.0.0.1; the connection is locked to an unrelated https prefix the way
GeoParquetFolder locks one, with and without a secret created first.
"""
import http.server
import threading

import duckdb

server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), type("H", (http.server.BaseHTTPRequestHandler,), {
    "do_GET": lambda self: (self.send_response(200), self.send_header("Content-Type", "text/plain"),
                            self.end_headers(), self.wfile.write(b"inside the private network")),
    "do_HEAD": lambda self: (self.send_response(200), self.send_header("Content-Length", "26"), self.end_headers()),
    "log_message": lambda self, *a: None,
}))
threading.Thread(target=server.serve_forever, daemon=True).start()
url = f"http://127.0.0.1:{server.server_address[1]}/x.txt"


def attempt(secret, allowed="https://example.org/allowed/", proxy=False):
    con = duckdb.connect()
    con.execute("INSTALL httpfs; LOAD httpfs;")
    if secret:
        con.execute(secret)
    lines = (["set global http_proxy = ''"] if proxy else []) + [f"set allowed_directories = ['{allowed}']",
                 "set autoinstall_known_extensions = false",
                 "set autoload_known_extensions = false",
                 "set enable_external_access = false",
                 "set lock_configuration = true"]
    for line in lines:
        con.execute(line)
    try:
        return "READ: " + con.execute(f"select content from read_text('{url}')").fetchone()[0]
    except Exception as e:
        return "refused: " + str(e).splitlines()[0][:120]


print("duckdb", duckdb.__version__)
print("no secret       ", attempt(None))
print("http secret     ", attempt("CREATE SECRET (TYPE http, BEARER_TOKEN 'x')"))
print("s3 secret       ", attempt("CREATE SECRET (TYPE s3, KEY_ID 'a', SECRET 'b', REGION 'eu-west-1')"))
same = url.rsplit('/', 1)[0] + '/allowed/'
print("same host, none ", attempt(None, same, True))
print("same host, http ", attempt("CREATE SECRET (TYPE http, BEARER_TOKEN 'x')", same, True))
print("same host, s3   ", attempt("CREATE SECRET (TYPE s3, KEY_ID 'a', SECRET 'b', REGION 'eu-west-1')", same, True))

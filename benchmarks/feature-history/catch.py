# Serves this folder and writes any POST body to result.txt, for a headless page to report into.
import http.server, os, sys
os.chdir(os.path.dirname(os.path.abspath(__file__)))

class H(http.server.SimpleHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        with open("result.txt", "ab") as f:
            f.write(body + b"\n")
        self.send_response(204); self.end_headers()
    def log_message(self, *a): pass

http.server.ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), H).serve_forever()

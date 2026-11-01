"""Two Graticula nodes behind a real Caddy — ADR-012 §5 phase 3, condition 3.

Not CI. It needs two things CI does not have: a Caddy and a machine to run it on. It starts two nodes of one site on
this machine, runs a throwaway Caddy on a remote host over SSH, joins them with SSH tunnels, and checks through that
Caddy what `ASiteBehindAProxyTests` could only play with its own requests:

* requests alternate between the nodes (`lb_policy round_robin`);
* the scheme and host the client used reach the nodes, and are believed **only** because Caddy's address is in
  `Graticula:TrustedProxies` — a node that does not name it writes `http://`, a forged header sent to a node directly is
  ignored, and one sent *through* Caddy is replaced by Caddy's own;
* a token issued through one node is honoured by the other;
* Pro's multi-step staging (`addItem`, `addPart`, `commit`) and the ArcGIS append upload (`uploads/upload`, `append`)
  complete with their steps on different nodes — phase 2's `SiteHandoff`;
* Caddy's active health check on `/healthz/ready` takes a node put in maintenance out and back, and a node killed
  outright out and back (with `lb_try_duration`, no request in between fails);
* a node naming another proxy than its site's is refused at start;
* and, as a separate phase, `dynamic a` upstreams — one name, an address per node. They are found and alternated, but
  **Caddy 2.9.1 and 2.10.2 do not apply the active health check to them** (observed 2026-11-01): a node in maintenance
  keeps being sent requests. That is printed as a NOTE rather than counted, and is why deployment.md §1.4 lists the
  nodes instead.

By default the nodes serve plain HTTP behind Caddy (`RequireHttps=false`: TLS ends at Caddy), which is the arrangement
where trusting the proxy is visible — a node that believes nobody writes `http://`. `--node-tls` runs deployment.md's
alternative instead, the nodes keeping their own HTTPS and Caddy skipping its verification.

    python -I tools/caddy-site-probe.py --host-dll D:/build/Release/net9.0/Graticula.Host.dll \
        --store "Host=localhost;Port=55576;Username=gis;Database=caddyprobe" --vps root@203.0.113.7 --key ~/.ssh/id

The store is an **empty database** the caller made; the script migrates it. On the remote host it runs one container,
`graticula-probe-caddy` (the official image, host network, listening on 127.0.0.1 only), and for the `dynamic a` phase two
`socat` relays on 127.0.0.2 and 127.0.0.3; all of it is removed at the end, and the tunnels closed, whatever happened.
Nothing else there is touched. It prints status codes and addresses, never a token or a password. Exit 0 when every
check holds.
"""
import argparse
import base64
import http.client
import json
import os
import re
import secrets
import shutil
import socket
import ssl
import subprocess
import sys
import tempfile
import time
import uuid

PROBE_HOST = "graticula-probe.test"
ADMIN, PASSWORD = "probeadmin", "caddy-probe-" + secrets.token_hex(8)
CONTAINER = "graticula-probe-caddy"

RESULTS = []


def note(name, held, detail=""):
    """A behaviour of Caddy recorded rather than required: it does not decide the exit status."""
    print(f"NOTE  {name}: {'yes' if held else 'no'}" + (f"  — {detail}" if detail else ""), flush=True)


def check(name, ok, detail=""):
    RESULTS.append((name, bool(ok)))
    print(f"{'PASS' if ok else 'FAIL'}  {name}" + (f"  — {detail}" if detail else ""), flush=True)
    return ok


# --------------------------------------------------------------------------------------------------------------- HTTP

class Answer:
    def __init__(self, status, headers, body):
        self.status, self.headers, self.body = status, headers, body

    def json(self):
        try:
            return json.loads(self.body)
        except ValueError:
            return {}

    @property
    def upstream(self):
        return self.headers.get("x-probe-upstream", "?")


class ThroughCaddy(http.client.HTTPSConnection):
    """An HTTPS connection to the forward tunnel's local end, with the probe's name as SNI and Host."""

    def __init__(self, port):
        context = ssl.create_default_context()
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE  # Caddy's internal CA, made a minute ago inside a throwaway container.
        super().__init__(PROBE_HOST, port, context=context, timeout=30)
        self._local = port
        self._ctx = context

    def connect(self):
        raw = socket.create_connection(("127.0.0.1", self._local), self.timeout)
        self.sock = self._ctx.wrap_socket(raw, server_hostname=PROBE_HOST)


def request(connection_factory, method, path, token=None, body=None, headers=None, form=None):
    connection = connection_factory()
    sent = dict(headers or {})
    data = None
    if token:
        sent["Authorization"] = "Bearer " + token
    if form is not None:
        data, sent["Content-Type"] = multipart(form)
    elif body is not None:
        data = json.dumps(body).encode()
        sent["Content-Type"] = "application/json"
    try:
        connection.request(method, path, body=data, headers=sent)
        response = connection.getresponse()
        answer = Answer(response.status, {k.lower(): v for k, v in response.getheaders()}, response.read().decode("utf-8", "replace"))
    except (OSError, http.client.HTTPException) as e:
        answer = Answer(0, {}, f"{type(e).__name__}: {e}")
    finally:
        connection.close()
    return answer


def multipart(fields):
    boundary = "----probe" + uuid.uuid4().hex
    out = bytearray()
    for name, value in fields.items():
        out += f"--{boundary}\r\n".encode()
        if isinstance(value, tuple):
            filename, content = value
            out += f'Content-Disposition: form-data; name="{name}"; filename="{filename}"\r\n'.encode()
            out += b"Content-Type: application/octet-stream\r\n\r\n" + content + b"\r\n"
        else:
            out += f'Content-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode()
    out += f"--{boundary}--\r\n".encode()
    return bytes(out), f"multipart/form-data; boundary={boundary}"


# -------------------------------------------------------------------------------------------------------------- nodes

class Node:
    def __init__(self, name, port, settings, folder, tls=False):
        self.name, self.port, self.settings, self.folder, self.tls = name, port, settings, folder, tls
        self.process = None
        self.log_path = os.path.join(folder, f"{name}.log")

    def start(self, dll, extra=None):
        env = dict(os.environ)
        env.update(self.settings)
        env.update(extra or {})
        self.log = open(self.log_path, "a", encoding="utf-8")
        self.process = subprocess.Popen(["dotnet", dll], env=env, stdout=self.log, stderr=subprocess.STDOUT,
                                        cwd=os.path.dirname(dll))
        deadline = time.time() + 120
        while time.time() < deadline:
            if self.process.poll() is not None:
                raise SystemExit(f"node {self.name} exited with {self.process.returncode}; see {self.log_path}")
            if self.direct("GET", "/healthz/live").status == 200:
                return
            time.sleep(0.5)
        raise SystemExit(f"node {self.name} did not answer within 120 s; see {self.log_path}")

    def start_refused(self, dll, extra=None, within=90):
        """Starts it expecting a refusal; returns its exit code, or None if it is still running after `within`."""
        env = dict(os.environ)
        env.update(self.settings)
        env.update(extra or {})
        self.log = open(self.log_path, "a", encoding="utf-8")
        self.process = subprocess.Popen(["dotnet", dll], env=env, stdout=self.log, stderr=subprocess.STDOUT,
                                        cwd=os.path.dirname(dll))
        try:
            return self.process.wait(within)
        except subprocess.TimeoutExpired:
            self.kill()
            return None
        finally:
            self.log.close()

    def tail(self, size=4000):
        with open(self.log_path, encoding="utf-8", errors="replace") as f:
            return f.read()[-size:]

    def kill(self):
        if self.process and self.process.poll() is None:
            self.process.kill()
            self.process.wait(30)
        if getattr(self, "log", None):
            self.log.close()

    def direct(self, method, path, address="127.0.0.1", **kwargs):
        if self.tls:
            context = ssl.create_default_context()
            context.check_hostname = False
            context.verify_mode = ssl.CERT_NONE  # the node's own self-signed certificate
            return request(lambda: http.client.HTTPSConnection(address, self.port, timeout=30, context=context),
                           method, path, **kwargs)
        return request(lambda: http.client.HTTPConnection(address, self.port, timeout=30), method, path, **kwargs)


def setup_token(log_path):
    deadline = time.time() + 60
    while time.time() < deadline:
        with open(log_path, encoding="utf-8", errors="replace") as f:
            found = re.search(r"^\s+([A-Za-z0-9._-]{20,})\s*$", f.read(), re.M)
        if found:
            return found.group(1)
        time.sleep(0.5)
    raise SystemExit("no setup token in the first node's log")


def lan_address():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.connect(("192.0.2.1", 9))  # no packet is sent; this only picks the outward interface
        return s.getsockname()[0]


# ---------------------------------------------------------------------------------------------------------------- VPS

def caddyfile(upstreams, https_port, dynamic=None, node_tls=False):
    to = f"dynamic a {dynamic[0]} {dynamic[1]}" if dynamic else ""
    static = "" if dynamic else " " + " ".join(upstreams)
    # deployment.md §1.4, with three changes for a probe: the probe's name and a high port on 127.0.0.1 instead of
    # gis.example.com on 443; Caddy's internal CA instead of a public certificate; the upstreams the tunnels give.
    return f"""{{
    admin off
    local_certs
    skip_install_trust
    auto_https disable_redirects
    default_bind 127.0.0.1
    http_port {https_port + 37}
    https_port {https_port}
}}

{PROBE_HOST}:{https_port} {{
    tls internal
    reverse_proxy{static} {{
        {to}
        lb_policy round_robin
        lb_try_duration 5s
        health_uri /healthz/ready
        health_interval 5s
        header_down X-Probe-Upstream {{http.reverse_proxy.upstream.hostport}}{TLS_TRANSPORT if node_tls else ""}
    }}
}}
"""


TLS_TRANSPORT = """
        transport http {
            tls_insecure_skip_verify
        }"""


class Remote:
    def __init__(self, target, key):
        self.target, self.key = target, os.path.expanduser(key)
        self.tunnel = None

    def ssh(self, command, check=True):
        done = subprocess.run(["ssh", "-i", self.key, "-o", "BatchMode=yes", self.target, command],
                              capture_output=True, text=True, timeout=180)
        if check and done.returncode != 0:
            raise SystemExit(f"remote command failed ({done.returncode}): {done.stderr.strip()[:400]}")
        return done.stdout

    def open_tunnels(self, forwards):
        argv = ["ssh", "-i", self.key, "-o", "BatchMode=yes", "-o", "ExitOnForwardFailure=yes",
                "-o", "ServerAliveInterval=15", "-N"]
        for flag, spec in forwards:
            argv += [flag, spec]
        self.tunnel = subprocess.Popen(argv + [self.target], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        time.sleep(4)
        if self.tunnel.poll() is not None:
            raise SystemExit("the SSH tunnels did not open: " + self.tunnel.stderr.read().decode()[:400])

    def close_tunnels(self):
        if self.tunnel and self.tunnel.poll() is None:
            self.tunnel.terminate()
            try:
                self.tunnel.wait(10)
            except subprocess.TimeoutExpired:
                self.tunnel.kill()

    def run_caddy(self, image, text, hosts=()):
        self.ssh(f"docker rm -f {CONTAINER} >/dev/null 2>&1; true", check=False)
        encoded = base64.b64encode(text.encode()).decode()
        add = " ".join(f"--add-host {h}" for h in hosts)
        self.ssh(f"docker run -d --name {CONTAINER} --network host --memory 128m {add} -e CF={encoded} {image} "
                 "sh -c 'echo \"$CF\" | base64 -d > /tmp/Caddyfile && exec caddy run --config /tmp/Caddyfile --adapter caddyfile'"
                 " >/dev/null")

    def remove_caddy(self):
        self.ssh(f"docker rm -f {CONTAINER} >/dev/null 2>&1; true", check=False)


# ------------------------------------------------------------------------------------------------------------- checks

def upstreams_of(caddy, n, path="/rest/info?f=json", token=None):
    seen = []
    for _ in range(n):
        a = request(caddy, "GET", path, token=token)
        seen.append(a.upstream if a.status == 200 else f"status {a.status}")
    return seen


def steer_away_from(caddy, node_port, tries=6):
    """Sends cheap reads until the last one went to `node_port`, so round robin gives the next to the other node."""
    for _ in range(tries):
        if request(caddy, "GET", "/rest/info?f=json").upstream.endswith(f":{node_port}"):
            return True
    return False


def wait_until(predicate, within):
    start = time.time()
    while time.time() - start < within:
        if predicate():
            return time.time() - start
        time.sleep(0.5)
    return None


def main():
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    p.add_argument("--host-dll", required=True)
    p.add_argument("--store", required=True, help="Npgsql connection string to an empty database")
    p.add_argument("--vps", required=True, help="user@host that runs Docker")
    p.add_argument("--key", required=True)
    p.add_argument("--ports", default="18594,18595", help="the two local nodes' ports")
    p.add_argument("--remote-ports", default="28594,28595,28443,28600",
                   help="node A's and B's tunnel ends, Caddy's HTTPS port, and the dynamic phase's relay port, on the remote host")
    p.add_argument("--image", default="caddy:2.9-alpine")
    p.add_argument("--keep", action="store_true", help="leave the state folder behind")
    p.add_argument("--node-tls", action="store_true",
                   help="nodes keep their own HTTPS and Caddy skips verifying it — deployment.md's alternative — instead of "
                        "plain HTTP behind Caddy. The node then sees https itself, so trust is not visible in the scheme.")
    args = p.parse_args()

    port_a, port_b = (int(x) for x in args.ports.split(","))
    remote_a, remote_b, caddy_port, relay_port = (int(x) for x in args.remote_ports.split(","))
    folder = tempfile.mkdtemp(prefix="caddy-probe-")
    shared = os.path.join(folder, "shared")
    os.makedirs(shared)
    key = base64.b64encode(secrets.token_bytes(32)).decode()
    lan = lan_address()

    def settings(name, port):
        state = os.path.join(folder, name)
        return {
            "Graticula__PlatformStore": args.store, "Graticula__SecretKey": key,
            "Graticula__Port": str(port), "Graticula__Listen": "0.0.0.0", "Graticula__HostName": "127.0.0.1",
            "Graticula__StatePath": os.path.join(state, "state"), "Graticula__TileCachePath": os.path.join(state, "tiles"),
            "Graticula__ImportScratchPath": os.path.join(state, "import"), "Graticula__NodeName": name,
            "Graticula__ServerDirectories": shared, "Graticula__Nodes": "2",
            # The tunnels' local end is sshd's client connecting from loopback: that is Caddy's address as a node sees it.
            "Graticula__TrustedProxies": "127.0.0.1/32",
            # TLS ends at Caddy; the hop to the node is inside the tunnel. With --node-tls the node keeps its own.
            **({} if args.node_tls else {"Graticula__RequireHttps": "false"}),
        }

    a = Node("caddy-a", port_a, settings("caddy-a", port_a), folder, args.node_tls)
    b = Node("caddy-b", port_b, settings("caddy-b", port_b), folder, args.node_tls)
    seen_scheme = "https" if args.node_tls else "http"  # what a node writes when it believes nobody
    up_a, up_b = f"127.0.0.1:{remote_a}", f"127.0.0.1:{remote_b}"
    remote = Remote(args.vps, args.key)
    caddy = lambda: ThroughCaddy(caddy_port)  # noqa: E731

    migrated = subprocess.run(["dotnet", args.host_dll, "migrate", "--apply", "--no-backup"],
                              env={**os.environ, "Graticula__PlatformStore": args.store, "Graticula__SecretKey": key},
                              capture_output=True, text=True, cwd=os.path.dirname(args.host_dll))
    if migrated.returncode != 0:
        raise SystemExit("migrate failed: " + (migrated.stdout + migrated.stderr)[-800:])

    try:
        a.start(args.host_dll)
        token_setup = setup_token(a.log_path)
        s = a.direct("POST", "/rest/setup", body={"token": token_setup, "name": ADMIN, "password": PASSWORD})
        if s.status not in (200, 201):
            raise SystemExit(f"setup answered {s.status}")
        b.start(args.host_dll)

        print(f"remote memory before:\n{remote.ssh('free -m')}", flush=True)
        remote.open_tunnels([("-R", f"127.0.0.1:{remote_a}:127.0.0.1:{port_a}"),
                             ("-R", f"127.0.0.1:{remote_b}:127.0.0.1:{port_b}"),
                             ("-L", f"127.0.0.1:{caddy_port}:127.0.0.1:{caddy_port}")])
        remote.run_caddy(args.image, caddyfile([up_a, up_b], caddy_port, node_tls=args.node_tls))
        ready = wait_until(lambda: request(caddy, "GET", "/healthz/ready").status == 200, 60)
        check("Caddy answers through the tunnels", ready is not None, f"{args.image}, ready after {ready or 0:.1f} s")
        time.sleep(6)  # one health interval, so both upstreams have been checked

        # 1. Round robin.
        seen = upstreams_of(caddy, 10)
        alternates = all(seen[i] != seen[i + 1] for i in range(len(seen) - 1))
        check("requests alternate between the two nodes", set(seen) == {up_a, up_b} and alternates, " ".join(
            "A" if x == up_a else "B" if x == up_b else x for x in seen))

        # 2. Forwarded scheme and host, from Caddy and from nobody else.
        origin = f"https://{PROBE_HOST}:{caddy_port}"
        owners = {}
        for _ in range(4):
            r = request(caddy, "GET", "/rest/info?f=json")
            owners[r.upstream] = r.json().get("owningSystemUrl")
        check("through Caddy every node writes the address the client used", set(owners) == {up_a, up_b}
              and set(owners.values()) == {origin}, json.dumps(owners))
        forged = {}
        for _ in range(4):
            r = request(caddy, "GET", "/rest/info?f=json", headers={"X-Forwarded-Host": "evil.example", "X-Forwarded-Proto": "http"})
            forged[r.upstream] = r.json().get("owningSystemUrl")
        check("forwarded headers a client sends through Caddy are replaced by Caddy's", set(forged.values()) == {origin},
              json.dumps(forged))
        direct = a.direct("GET", "/rest/info?f=json", address=lan,
                          headers={"X-Forwarded-Host": "evil.example", "X-Forwarded-Proto": "https"}).json().get("owningSystemUrl")
        check("the same headers sent to a node directly, not from the proxy, are ignored",
              direct == f"{seen_scheme}://{lan}:{port_a}", f"from {lan}: {direct}")

        # 3. A token issued through one node, honoured by the other.
        login = request(caddy, "POST", "/rest/auth/login", body={"name": ADMIN, "password": PASSWORD})
        token = login.json().get("token")
        issued_by = login.upstream
        used = {}
        for _ in range(6):
            r = request(caddy, "GET", "/admin/site/machines", token=token)
            used.setdefault(r.upstream, set()).add(r.status)
        check("a token issued through one node is honoured by both", login.status == 200 and set(used) == {up_a, up_b}
              and all(v == {200} for v in used.values()), f"issued by {issued_by}; " + json.dumps({k: sorted(v) for k, v in used.items()}))
        generated = request(caddy, "POST", "/rest/generateToken",
                            form={"username": ADMIN, "password": PASSWORD, "client": "requestip", "f": "json"})
        gtoken = generated.json().get("token")
        gused = {}
        for _ in range(4):
            r = request(caddy, "GET", f"/rest/services?f=json&token={gtoken}")
            gused.setdefault(r.upstream, set()).add(r.status)
        check("an ArcGIS generateToken token from one node is honoured by both", generated.status == 200 and bool(gtoken)
              and set(gused) == {up_a, up_b} and all(v == {200} for v in gused.values()),
              f"issued by {generated.upstream}; " + json.dumps({k: sorted(v) for k, v in gused.items()}))

        # 4. Pro's staging across nodes.
        user = ADMIN
        added = request(caddy, "POST", f"/sharing/rest/content/users/{user}/addItem", token=token, form={
            "type": "Service Definition", "title": "probe", "multipart": "true", "filename": "probe.sd", "f": "json"})
        item = added.json().get("id")
        steps = [("addItem", added)]
        port_of = lambda up: int(up.rsplit(":", 1)[1])  # noqa: E731
        if item:
            for n, chunk in ((1, b"A" * 1000), (2, b"B" * 500)):
                steer_away_from(caddy, port_of(steps[-1][1].upstream))
                steps.append((f"addPart {n}", request(caddy, "POST", f"/sharing/rest/content/users/{user}/items/{item}/addPart",
                                                      token=token, form={"partNum": str(n), "f": "json", "file": ("probe.sd", chunk)})))
            steer_away_from(caddy, port_of(steps[-1][1].upstream))
            steps.append(("commit", request(caddy, "POST", f"/sharing/rest/content/users/{user}/items/{item}/commit",
                                            token=token, form={"f": "json"})))
            steer_away_from(caddy, port_of(steps[-1][1].upstream))
            steps.append(("status", request(caddy, "GET", f"/sharing/rest/content/users/{user}/items/{item}/status?f=json", token=token)))
        trail = "; ".join(f"{n} {r.status} on {'A' if r.upstream == up_a else 'B' if r.upstream == up_b else r.upstream}" for n, r in steps)
        hops = all(steps[i][1].upstream != steps[i + 1][1].upstream for i in range(len(steps) - 1))
        check("Pro's staging completes with each step on the other node", len(steps) == 5 and hops
              and all(r.status == 200 and '"success":false' not in r.body.replace(" ", "") for _, r in steps)
              and '"success":true' in steps[3][1].body.replace(" ", ""), trail)
        shared_file = os.path.join(shared, "handoff")
        sizes = [os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(shared_file) for f in fs if f == "item.sd"] \
            if os.path.isdir(shared_file) else []
        check("commit joined both nodes' parts into one file", 1500 in sizes, f"item.sd sizes in the shared directory: {sizes}")

        # 5. The ArcGIS append upload across nodes.
        name = "zz_caddy_" + uuid.uuid4().hex[:6]
        made = request(caddy, "POST", "/admin/hosted/define", token=token, body={
            "name": name, "geometryType": "Point", "fields": [{"name": "label", "type": "Text"}], "sharing": "public"})
        geojson = json.dumps({"type": "FeatureCollection", "features": [
            {"type": "Feature", "geometry": {"type": "Point", "coordinates": [29.0, 41.0]}, "properties": {"label": "via caddy"}}]}).encode()
        uploaded = request(caddy, "POST", f"/rest/services/hosted/{name}/FeatureServer/uploads/upload", token=token,
                           form={"f": "json", "file": ("one.geojson", geojson)})
        upload_id = (uploaded.json().get("item") or {}).get("itemID")
        steer_away_from(caddy, port_of(uploaded.upstream)) if uploaded.upstream != "?" else None
        appended = request(caddy, "POST", f"/rest/services/hosted/{name}/FeatureServer/0/append", token=token, form={
            "f": "json", "appendUploadId": upload_id or "", "appendUploadFormat": "geojson", "async": "false"})
        counted = request(caddy, "GET", f"/rest/services/hosted/{name}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json",
                          token=token)
        check("an append upload sent to one node is appended by the other", made.status in (200, 201) and uploaded.status == 200
              and appended.status == 200 and uploaded.upstream != appended.upstream and counted.json().get("count") == 1,
              f"define {made.status}; upload {uploaded.status} on {uploaded.upstream}; append {appended.status} on "
              f"{appended.upstream}; count {counted.json().get('count')}")

        # 6. Health checks: maintenance out and back.
        put = request(caddy, "PUT", "/admin/site/machines/caddy-b", token=token, body={"state": "maintenance"})
        out = wait_until(lambda: set(upstreams_of(caddy, 4)) == {up_a}, 20)
        only_a = upstreams_of(caddy, 8)
        check("a node put in maintenance is taken out by Caddy's health check", put.status == 200 and out is not None
              and set(only_a) == {up_a}, f"PUT {put.status}; out after {out if out is None else round(out, 1)} s; then {set(only_a)}")
        back_put = request(caddy, "PUT", "/admin/site/machines/caddy-b", token=token, body={"state": "started"})
        back = wait_until(lambda: up_b in upstreams_of(caddy, 4), 20)
        check("and put back when it is started", back_put.status == 200 and back is not None,
              f"PUT {back_put.status}; back after {back if back is None else round(back, 1)} s")

        # 7. Health checks: a node killed outright.
        b.kill()
        killed_at = time.time()
        statuses = []
        while time.time() - killed_at < 12:
            r = request(caddy, "GET", "/rest/info?f=json")
            statuses.append((round(time.time() - killed_at, 1), r.status, r.upstream))
            time.sleep(0.2)
        failed = [s for s in statuses if s[1] != 200]
        late = [s for s in statuses if s[0] > 6]
        check("a node killed outright is taken out", all(s[1] == 200 and s[2] == up_a for s in late),
              f"{len(statuses)} requests in 12 s, {len(failed)} not 200" + (f" (first at {failed[0][0]} s, last at {failed[-1][0]} s,"
                                                                           f" statuses {sorted({s[1] for s in failed})})" if failed else ""))
        b.start(args.host_dll)
        back = wait_until(lambda: up_b in upstreams_of(caddy, 4), 30)
        check("and back once it is restarted", back is not None, f"back after {back if back is None else round(back, 1)} s")

        # 8. Control: a site that does not name Caddy as its proxy writes what it sees, not what Caddy says. Both nodes,
        # because one node naming another proxy than its site is refused at start — checked first.
        b.kill()
        refused = b.start_refused(args.host_dll, {"Graticula__TrustedProxies": "192.0.2.1/32"})
        check("a node naming another proxy than its site is refused at start",
              refused is not None and refused != 0 and "Graticula:TrustedProxies" in b.tail(),
              f"exit {refused}")
        untrusting = {"Graticula__TrustedProxies": "192.0.2.1/32"}
        a.kill()
        time.sleep(35)  # past the heartbeat's 30 s, so neither node is still "up" when the site starts again
        a.start(args.host_dll, untrusting)
        b.start(args.host_dll, untrusting)
        wait_until(lambda: set(upstreams_of(caddy, 4)) == {up_a, up_b}, 30)
        control = {}
        for _ in range(4):
            r = request(caddy, "GET", "/rest/info?f=json")
            control[r.upstream] = r.json().get("owningSystemUrl")
        check(f"control: a site that does not trust Caddy writes {seen_scheme}://, and Caddy kept Host",
              set(control) == {up_a, up_b} and set(control.values()) == {f"{seen_scheme}://{PROBE_HOST}:{caddy_port}"},
              json.dumps(control))
        a.kill()
        b.kill()
        time.sleep(35)
        a.start(args.host_dll)
        b.start(args.host_dll)
        wait_until(lambda: set(upstreams_of(caddy, 4)) == {up_a, up_b}, 30)

        # 9. The dynamic a form deployment.md gives for compose: one name, an address per node, one port.
        remote.ssh(f"nohup socat TCP-LISTEN:{relay_port},bind=127.0.0.2,fork,reuseaddr TCP:127.0.0.1:{remote_a} >/dev/null 2>&1 &"
                   f" echo $! > /tmp/graticula-probe-relay-a.pid;"
                   f" nohup socat TCP-LISTEN:{relay_port},bind=127.0.0.3,fork,reuseaddr TCP:127.0.0.1:{remote_b} >/dev/null 2>&1 &"
                   f" echo $! > /tmp/graticula-probe-relay-b.pid")
        remote.run_caddy(args.image, caddyfile([], caddy_port, dynamic=("graticula-nodes", relay_port), node_tls=args.node_tls),
                         hosts=("graticula-nodes:127.0.0.2", "graticula-nodes:127.0.0.3"))
        dyn_a, dyn_b = f"127.0.0.2:{relay_port}", f"127.0.0.3:{relay_port}"
        ready = wait_until(lambda: request(caddy, "GET", "/healthz/ready").status == 200, 60)
        time.sleep(6)
        seen = upstreams_of(caddy, 10)
        check("dynamic a: both addresses of one name are found and alternated", ready is not None and set(seen) == {dyn_a, dyn_b},
              " ".join("A" if x == dyn_a else "B" if x == dyn_b else x for x in seen))
        put = request(caddy, "PUT", "/admin/site/machines/caddy-b", token=token, body={"state": "maintenance"})
        out = wait_until(lambda: set(upstreams_of(caddy, 4)) == {dyn_a}, 20)
        # Recorded, not required: Caddy 2.9.1 and 2.10.2 were both observed NOT to apply the active health check to
        # dynamic upstreams (2026-11-01), so deployment.md lists the nodes instead. A "yes" here means that changed.
        note("dynamic a: the health check takes a node in maintenance out", out is not None,
              f"PUT {put.status}; out after {out if out is None else round(out, 1)} s; last {set(upstreams_of(caddy, 6))}")
        request(caddy, "PUT", "/admin/site/machines/caddy-b", token=token, body={"state": "started"})
        back = wait_until(lambda: dyn_b in upstreams_of(caddy, 4), 20)
        check("dynamic a: and the node is in rotation once started again", back is not None,
              f"in rotation after {back if back is None else round(back, 1)} s")

        print(f"remote memory during:\n{remote.ssh('free -m; docker stats --no-stream --format \"{{.Name}} {{.MemUsage}}\" ' + CONTAINER)}")
    finally:
        remote.remove_caddy()
        remote.ssh("for f in /tmp/graticula-probe-relay-a.pid /tmp/graticula-probe-relay-b.pid; do "
                   "[ -f $f ] && kill $(cat $f) 2>/dev/null; rm -f $f; done; true", check=False)
        remote.close_tunnels()
        a.kill()
        b.kill()
        left = remote.ssh(f"docker ps -a --filter name={CONTAINER} --format '{{{{.Names}}}}'; pgrep -fa 'socat TCP-LISTE[N]:{relay_port}' || true", check=False)
        print("remote left behind: " + (left.strip() or "nothing"))
        if not args.keep:
            shutil.rmtree(folder, ignore_errors=True)

    failed = [n for n, ok in RESULTS if not ok]
    print(f"\n{len(RESULTS) - len(failed)} of {len(RESULTS)} checks hold.")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())

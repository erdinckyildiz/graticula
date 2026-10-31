"""ADR-088 condition 1: a real Keycloak signs somebody in to a running server, through its own login form.

Run on 2026-10-16: Keycloak 26.0.7 in a container on a host's loopback (`docker run -d --name kc-probe -p
127.0.0.1:18180:8080 -e KC_BOOTSTRAP_ADMIN_USERNAME=admin -e KC_BOOTSTRAP_ADMIN_PASSWORD=... quay.io/keycloak/keycloak:26.0.7
start-dev`), `setup.sh` beside this making a realm, a confidential client and a user with secrets kept in files,
`ssh -L 18180:127.0.0.1:18180` to that host, and the two secret files copied beside this script. GRATICULA_TEST_URL,
_USER and _PASSWORD name the server and its administrator.

Registers Keycloak (reached through an SSH tunnel on 127.0.0.1:18181, where setup.sh now starts it; 18180 on that host is another project's) as an OpenID Connect provider, walks the
browser's redirects with a cookie jar, fills Keycloak's own form, and reads /rest/whoami. Removes the provider and the
account it made afterwards. Secrets are read from files beside this script and never printed."""
import html, json, os, re, ssl, sys, urllib.error, urllib.parse, urllib.request
from http.cookiejar import CookieJar, DefaultCookiePolicy


class LocalPolicy(DefaultCookiePolicy):
    """A browser sends a Secure cookie back to http://127.0.0.1 (localhost is a secure context); the jar does not."""

    def return_ok_secure(self, cookie, request):
        return True


HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.environ.get("GRATICULA_TEST_URL", "https://127.0.0.1:18491")
ISSUER = "http://127.0.0.1:18181/realms/graticula"
CTX = ssl._create_unverified_context()


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def opener(jar=None):
    handlers = [NoRedirect(), urllib.request.HTTPSHandler(context=CTX)]
    if jar is not None:
        handlers.append(urllib.request.HTTPCookieProcessor(jar))
    return urllib.request.build_opener(*handlers)


def go(op, url, data=None, headers=None):
    req = urllib.request.Request(url, data=data, headers=headers or {})
    try:
        r = op.open(req)
        return r.status, dict(r.headers), r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read().decode("utf-8", "replace")


admin = opener()
status, _, body = go(admin, ROOT + "/rest/auth/login", json.dumps({"name": os.environ.get("GRATICULA_TEST_USER", "ci"), "password": os.environ["GRATICULA_TEST_PASSWORD"]}).encode(),
                     {"Content-Type": "application/json"})
token = json.loads(body)["token"]
auth = {"Authorization": "Bearer " + token, "Content-Type": "application/json"}

secret = open(os.path.join(HERE, "secret"), encoding="utf-8").read().strip()
password = open(os.path.join(HERE, "userpass"), encoding="utf-8").read().strip()

status, _, body = go(admin, ROOT + "/admin/identity-providers", json.dumps({
    "name": "zz Keycloak probe", "issuer": ISSUER, "clientId": "graticula-probe", "clientSecret": secret,
    "autoCreate": True, "defaultRole": "viewer"}).encode(), auth)
print("register provider:", status, body[:200] if status != 201 else "")
if status != 201:
    sys.exit(1)
provider = json.loads(body)["id"]
made = None

try:
    browser = opener(CookieJar(LocalPolicy()))
    status, headers, body = go(browser, f"{ROOT}/rest/auth/oidc/{provider}/start?return=/rest/whoami")
    print("start:", status, (headers.get("Location") or body)[:90])
    at_keycloak = headers["Location"]

    status, headers, page = go(browser, at_keycloak)
    print("keycloak login page:", status, "form found" if "kc-form-login" in page else page[:200])
    action = html.unescape(re.search(r'<form[^>]+id="kc-form-login"[^>]+action="([^"]+)"', page).group(1))

    status, headers, body = go(browser, action, urllib.parse.urlencode(
        {"username": "kc.alice", "password": password, "credentialId": ""}).encode(),
        {"Content-Type": "application/x-www-form-urlencoded"})
    callback = headers.get("Location")
    print("keycloak answers:", status, (callback or re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", body))[-300:])[:300].split("?")[0])

    status, headers, body = go(browser, callback)
    print("callback:", status, headers.get("Location") or body[:300])

    status, _, who = go(browser, ROOT + "/rest/whoami")
    who = json.loads(who)
    made = who.get("name")
    print("whoami:", status, {k: who.get(k) for k in ("name", "fullName", "role", "authenticated") if k in who})
    # ---- the same sign-in reached from an ArcGIS app's OAuth page (ADR-076, ADR-088 c2), ending in a token ----
    app = opener(CookieJar(LocalPolicy()))
    OOB = "urn:ietf:wg:oauth:2.0:oob"
    status, headers, page = go(app, f"{ROOT}/sharing/rest/oauth2/authorize?client_id=pythonapi&response_type=code&redirect_uri={urllib.parse.quote(OOB)}")
    link = html.unescape(re.search(r'href="([^"]*/external\?[^"]+)"', page).group(1))
    print("authorize page offers the provider:", status, link.split("?")[0])
    status, headers, page = go(app, link if link.startswith("http") else ROOT + link)
    while headers.get("Location") and "kc-form-login" not in page:
        hop = headers["Location"]
        status, headers, page = go(app, hop if hop.startswith("http") else ROOT + hop)
    action = html.unescape(re.search(r'<form[^>]+id="kc-form-login"[^>]+action="([^"]+)"', page).group(1))
    status, headers, _ = go(app, action, urllib.parse.urlencode({"username": "kc.alice", "password": password, "credentialId": ""}).encode(),
                            {"Content-Type": "application/x-www-form-urlencoded"})
    hop = headers.get("Location")
    for _ in range(4):
        status, headers, page = go(app, hop if hop.startswith("http") else ROOT + hop)
        hop = headers.get("Location")
        if not hop:
            break
    code = re.search(r"<title>SUCCESS code=([^<]+)</title>", page).group(1)
    print("approval page carries a code:", status, len(code), "characters")
    status, _, body = go(opener(), f"{ROOT}/sharing/rest/oauth2/token", urllib.parse.urlencode({
        "client_id": "pythonapi", "grant_type": "authorization_code", "code": code, "redirect_uri": OOB, "f": "json"}).encode(),
        {"Content-Type": "application/x-www-form-urlencoded"})
    answer = json.loads(body)
    print("token exchange:", status, "username" in answer and answer["username"], "token" if "access_token" in answer else body[:200])
finally:
    if made:
        req = urllib.request.Request(f"{ROOT}/admin/members/{urllib.parse.quote(made)}", method="DELETE", headers=auth)
        try:
            admin.open(req)
        except urllib.error.HTTPError as e:
            print("member delete:", e.code)
    req = urllib.request.Request(f"{ROOT}/admin/identity-providers/{provider}", method="DELETE", headers=auth)
    try:
        admin.open(req)
    except urllib.error.HTTPError as e:
        print("provider delete:", e.code)
    print("cleaned up member", made, "and the provider")

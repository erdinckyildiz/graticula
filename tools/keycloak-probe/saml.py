"""A real Keycloak, as a SAML 2.0 identity provider, signs somebody in to a running server (ADR-090 condition 1's
shape, with Keycloak standing where AD FS or Entra ID would).

Registers the provider by Keycloak's own metadata URL, hands this server's SP metadata to Keycloak to make the client
(over ssh, with kcadm), signs in through Keycloak's form, posts the auto-submit form Keycloak answers with to this
server's ACS, and reads /rest/whoami. Removes the member and the provider afterwards. The user's password is read
from a file beside this script and never printed."""
import html, json, os, re, ssl, subprocess, urllib.error, urllib.parse, urllib.request
from http.cookiejar import CookieJar, DefaultCookiePolicy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.environ.get("GRATICULA_TEST_URL", "https://127.0.0.1:18491")
METADATA = "http://127.0.0.1:18180/realms/graticula/protocol/saml/descriptor"
SSH = ["ssh", "-i", os.path.expanduser("~/.ssh/id_ada_de"), "-o", "BatchMode=yes", "root@152.53.224.34"]
CTX = ssl._create_unverified_context()


class LocalPolicy(DefaultCookiePolicy):
    """A browser sends a Secure cookie back to http://127.0.0.1 (localhost is a secure context); the jar does not."""

    def return_ok_secure(self, cookie, request):
        return True


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def opener(jar=None):
    handlers = [NoRedirect(), urllib.request.HTTPSHandler(context=CTX)]
    if jar is not None:
        handlers.append(urllib.request.HTTPCookieProcessor(jar))
    return urllib.request.build_opener(*handlers)


def go(op, url, data=None, headers=None, method=None):
    req = urllib.request.Request(url, data=data, headers=headers or {}, method=method)
    try:
        r = op.open(req)
        return r.status, dict(r.headers), r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read().decode("utf-8", "replace")


admin = opener()
_, _, body = go(admin, ROOT + "/rest/auth/login", json.dumps({"name": os.environ.get("GRATICULA_TEST_USER", "ci"),
               "password": os.environ["GRATICULA_TEST_PASSWORD"]}).encode(), {"Content-Type": "application/json"})
auth = {"Authorization": "Bearer " + json.loads(body)["token"], "Content-Type": "application/json"}
password = open(os.path.join(HERE, "userpass"), encoding="utf-8").read().strip()

status, _, body = go(admin, ROOT + "/admin/identity-providers", json.dumps({
    "kind": "saml", "name": "zz Keycloak SAML probe", "metadataUrl": METADATA, "autoCreate": True, "defaultRole": "viewer"}).encode(), auth)
print("register provider:", status, body[:240] if status != 201 else "")
made_provider = json.loads(body) if status == 201 else None
provider = made_provider and made_provider["id"]
made = None

try:
    if not provider:
        raise SystemExit(1)

    # The SP's own metadata, handed to Keycloak to make the client exactly as this server describes itself.
    _, _, sp = go(opener(), f"{ROOT}/rest/auth/saml/{provider}/metadata")
    print("SP metadata:", re.search(r'entityID="([^"]+)"', sp).group(1), "| AuthnRequestsSigned:",
          (re.search(r'AuthnRequestsSigned="([^"]+)"', sp) or [None, "unsaid"])[1])
    made_client = subprocess.run(SSH + ["sh /opt/kc-probe/saml-client.sh"], input=sp, capture_output=True, text=True)
    print("keycloak client from SP metadata:", made_client.returncode, (made_client.stdout or made_client.stderr).strip()[:120])

    browser = opener(CookieJar(LocalPolicy()))
    status, headers, page = go(browser, f"{ROOT}/rest/auth/saml/{provider}/start?return=/rest/whoami")
    print("start:", status, (headers.get("Location") or page)[:80])
    status, headers, page = go(browser, headers["Location"])
    print("keycloak login page:", status, "form found" if "kc-form-login" in page else page[:200])
    action = html.unescape(re.search(r'<form[^>]+id="kc-form-login"[^>]+action="([^"]+)"', page).group(1))
    status, headers, page = go(browser, action, urllib.parse.urlencode(
        {"username": "kc.alice", "password": password, "credentialId": ""}).encode(), {"Content-Type": "application/x-www-form-urlencoded"})
    target = html.unescape(re.search(r'<form[^>]+action="([^"]+)"', page).group(1))
    fields = {html.unescape(n): html.unescape(v) for n, v in re.findall(r'<input[^>]+name="([^"]+)"[^>]+value="([^"]*)"', page)}
    print("keycloak answers:", status, "posts to", target, "with", sorted(fields))

    status, headers, page = go(browser, target, urllib.parse.urlencode(fields).encode(), {"Content-Type": "application/x-www-form-urlencoded"})
    print("ACS:", status, headers.get("Location") or re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", page))[:300])
    status, _, who = go(browser, ROOT + "/rest/whoami")
    who = json.loads(who)
    made = who.get("name")
    print("whoami:", status, {k: who.get(k) for k in ("name", "authenticated") if k in who})
finally:
    if made:
        go(admin, f"{ROOT}/admin/members/{urllib.parse.quote(made)}", None, auth, "DELETE")
    if provider:
        print("provider delete:", go(admin, f"{ROOT}/admin/identity-providers/{provider}", None, auth, "DELETE")[0])
    print("cleaned up member", made)

"""A real Keycloak, as a SAML 2.0 identity provider, signs somebody in to a running server (ADR-090 condition 1's
shape, with Keycloak standing where AD FS or Entra ID would) — and, since 2026-10-31, with its assertions encrypted to
this server's key (condition 3).

Registers the provider by Keycloak's own metadata URL with assertion encryption on, hands this server's SP metadata to
Keycloak to make the client (over ssh, saml-client.sh), signs in through Keycloak's form, posts the auto-submit form
Keycloak answers with to this server's ACS, and reads /rest/whoami. Then:
- with Keycloak's "Encrypt assertions" turned off for the client, the same sign-in is refused by this server;
- with this server's key rotated and the client made again from the new SP metadata, it signs in again.
Says of each response whether its assertion arrived encrypted. Removes the member and the provider afterwards. The
user's password is read from a file beside this script and never printed. ENCRYPT=0 runs the 2026-10-16 probe."""
import base64, html, json, os, re, ssl, subprocess, urllib.error, urllib.parse, urllib.request
from http.cookiejar import CookieJar, DefaultCookiePolicy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.environ.get("GRATICULA_TEST_URL", "https://127.0.0.1:18491")
METADATA = "http://127.0.0.1:18181/realms/graticula/protocol/saml/descriptor"
SSH = ["ssh", "-i", os.path.expanduser("~/.ssh/id_ada_de"), "-o", "BatchMode=yes", "root@152.53.224.34"]
CTX = ssl._create_unverified_context()
ENCRYPT = os.environ.get("ENCRYPT", "1") != "0"


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


def client(provider, mode=""):
    """This server's SP metadata, handed to Keycloak to make the client exactly as this server describes itself."""
    _, _, sp = go(opener(), f"{ROOT}/rest/auth/saml/{provider}/metadata")
    cert = re.search(r'<md:KeyDescriptor use="encryption">.*?<ds:X509Certificate>([^<]+)<', sp)
    print("SP metadata:", re.search(r'entityID="([^"]+)"', sp).group(1), "| encryption certificate:",
          (cert.group(1)[-16:] if cert else "none"))
    made = subprocess.run(SSH + [f"sh /opt/kc-probe/saml-client.sh {mode}".strip()], input=sp, capture_output=True, text=True)
    print("keycloak client:", made.returncode, " / ".join((made.stdout or made.stderr).strip().splitlines())[:400])


def sign_in(provider):
    """One sign-in through Keycloak's form; who it ended as, if anybody, and whether the assertion was encrypted."""
    browser = opener(CookieJar(LocalPolicy()))
    status, headers, page = go(browser, f"{ROOT}/rest/auth/saml/{provider}/start?return=/rest/whoami")
    status, headers, page = go(browser, headers["Location"])
    action = html.unescape(re.search(r'<form[^>]+id="kc-form-login"[^>]+action="([^"]+)"', page).group(1))
    status, headers, page = go(browser, action, urllib.parse.urlencode(
        {"username": "kc.alice", "password": password, "credentialId": ""}).encode(), {"Content-Type": "application/x-www-form-urlencoded"})
    target = html.unescape(re.search(r'<form[^>]+action="([^"]+)"', page).group(1))
    fields = {html.unescape(n): html.unescape(v) for n, v in re.findall(r'<input[^>]+name="([^"]+)"[^>]+value="([^"]*)"', page)}
    xml = base64.b64decode(fields.get("SAMLResponse", "")).decode("utf-8", "replace")
    encrypted = "EncryptedAssertion" in xml and not re.search(r"<(\w+:)?Assertion[\s>]", xml)
    print("   keycloak's response:", "assertion ENCRYPTED" if encrypted else "assertion in the clear",
          "|", ", ".join(a.split("#")[-1] for a in re.findall(r'EncryptionMethod[^>]*Algorithm="([^"]+)"', xml)))

    status, headers, page = go(browser, target, urllib.parse.urlencode(fields).encode(), {"Content-Type": "application/x-www-form-urlencoded"})
    print("   ACS:", status, headers.get("Location") or re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", page))[:200])
    status, _, who = go(browser, ROOT + "/rest/whoami")
    name = json.loads(who).get("name")
    print("   whoami:", name)
    return name


status, _, body = go(admin, ROOT + "/admin/identity-providers", json.dumps({
    "kind": "saml", "name": "zz Keycloak SAML probe", "metadataUrl": METADATA, "autoCreate": True, "defaultRole": "viewer",
    "encryptAssertions": ENCRYPT}).encode(), auth)
print("register provider (encryptAssertions=%s):" % ENCRYPT, status, body[:240] if status != 201 else "")
provider = json.loads(body)["id"] if status == 201 else None
made = None

try:
    if not provider:
        raise SystemExit(1)

    print("1. the client as Keycloak's converter makes it from this server's metadata")
    client(provider)
    made = sign_in(provider) or made

    if ENCRYPT:
        print("2. Keycloak's Encrypt assertions turned off for the client: this server requires it")
        client(provider, "clear")
        made = sign_in(provider) or made

        print("3. this server's key rotated, and the client made again from the new metadata")
        status, _, body = go(admin, f"{ROOT}/admin/identity-providers/{provider}/saml/encryption-key", b"", auth, "POST")
        print("   rotate:", status, json.loads(body)["saml"]["encryptionKey"]["sha256"][-16:] if status == 200 else body[:200])
        client(provider)
        made = sign_in(provider) or made
finally:
    if made:
        go(admin, f"{ROOT}/admin/members/{urllib.parse.quote(made)}", None, auth, "DELETE")
    if provider:
        print("provider delete:", go(admin, f"{ROOT}/admin/identity-providers/{provider}", None, auth, "DELETE")[0])
    print("cleaned up member", made)

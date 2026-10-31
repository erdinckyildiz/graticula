using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Conformance.Tests;

/// <summary>
/// An LDAP directory small enough to read: simple bind, search by base or subtree with and, or, not, equality and
/// presence filters, and unbind — the part of RFC 4511 a sign-in uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written with the base library's BER reader and writer</b>, for FakeOidcProvider's reason: a directory built on
/// the client library the server uses would agree with it by construction. The server's client is the platform's
/// own — OpenLDAP's on Linux, Windows' on Windows — so a pass here is two independent implementations agreeing.
/// </para>
/// <para>
/// <b>Plain LDAP on the loopback address</b>, which the server accepts only there — or <c>ldaps://</c> with a
/// certificate for 127.0.0.1 issued by a certificate authority made for the run (<see cref="Tls"/>), which nothing
/// trusts until it is given to the server.
/// </para>
/// <para>
/// <b>Groups inside groups</b>: a group entry lists its <c>member</c>s, and the extensible filter Active Directory
/// answers for transitive membership — <c>(member:1.2.840.113556.1.4.1941:=dn)</c> — is walked here as AD walks it.
/// </para>
/// </remarks>
internal sealed class FakeLdapServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An LDAP result code, as the writer wants an ENUMERATED to be one.</summary>
    private enum ResultCode
    {
        Success = 0,
        ProtocolError = 2,
        InvalidCredentials = 49,
    }

    /// <summary>An entry: its DN, its password (or null), and its attributes.</summary>
    internal sealed record Entry(string Dn, string? Password, Dictionary<string, List<string>> Attributes);

    /// <summary>Active Directory's transitive-membership matching rule.</summary>
    private const string InChain = "1.2.840.113556.1.4.1941";

    private readonly X509Certificate2? _serverCertificate;

    /// <summary>Starts the directory with its base and a search account.</summary>
    /// <param name="baseDn">The base, as in <c>dc=test</c>.</param>
    /// <param name="tls">Whether it speaks <c>ldaps://</c>, with a certificate from an authority made for the run.</param>
    public FakeLdapServer(string baseDn, bool tls = false)
    {
        if (tls)
        {
            (Tls, _serverCertificate) = MakeCertificates();
        }

        BaseDn = baseDn;
        SearchDn = $"cn=reader,{baseDn}";
        SearchPassword = "reader-" + Guid.NewGuid().ToString("N")[..8];

        Add(baseDn, null, ("objectClass", ["top"]));
        Add($"ou=people,{baseDn}", null, ("objectClass", ["organizationalUnit"]));
        Add(SearchDn, SearchPassword, ("objectClass", ["person"]));

        _listener.Start();
        Address = $"{(tls ? "ldaps" : "ldap")}://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = Task.Run(AcceptAsync);
    }

    /// <summary>The certificate authority that issued this directory's certificate, in PEM; null over plain LDAP.</summary>
    public string? Tls { get; }

    /// <summary>The address to configure on the server.</summary>
    public string Address { get; }

    /// <summary>The directory's base.</summary>
    public string BaseDn { get; }

    /// <summary>The account the server searches with.</summary>
    public string SearchDn { get; }

    /// <summary>Its password.</summary>
    public string SearchPassword { get; }

    /// <summary>How many simple binds as a person — not the search account — this directory has answered.</summary>
    public int PersonBinds;

    /// <summary>How many TLS handshakes a client broke off — over <c>ldaps://</c>, a certificate it did not trust.</summary>
    public int RefusedHandshakes;

    /// <summary>How many searches asked for transitive membership.</summary>
    public int ChainSearches;

    /// <summary>Adds a person under <c>ou=people</c> with a uid, a password, a display name and groups.</summary>
    public string AddPerson(string uid, string password, string displayName, params string[] groups)
    {
        string dn = $"uid={uid},ou=people,{BaseDn}";
        Add(dn, password,
            ("objectClass", ["person"]), ("uid", [uid]), ("displayName", [displayName]), ("memberOf", [.. groups]),
            ("entryUUID", [Guid.NewGuid().ToString()]));
        return dn;
    }

    /// <summary>Adds a group under <c>ou=groups</c> with its members' DNs — a person's or another group's.</summary>
    public string AddGroup(string cn, params string[] members)
    {
        string dn = $"cn={cn},ou=groups,{BaseDn}";
        Add(dn, null, ("objectClass", ["group"]), ("cn", [cn]), ("member", [.. members]));
        return dn;
    }

    /// <summary>Replaces a person's groups, as an administrator of the directory would.</summary>
    public void SetGroups(string dn, params string[] groups) => _entries[dn].Attributes["memberOf"] = [.. groups];

    public void Dispose()
    {
        _serverCertificate?.Dispose();
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    private void Add(string dn, string? password, params (string Name, List<string> Values)[] attributes) =>
        _entries[dn] = new Entry(dn, password,
            attributes.ToDictionary(a => a.Name, a => a.Values, StringComparer.OrdinalIgnoreCase));

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            Stream stream = client.GetStream();

            if (_serverCertificate is not null)
            {
                SslStream secure = new(stream, leaveInnerStreamOpen: false);

                try
                {
                    await secure.AuthenticateAsServerAsync(_serverCertificate, clientCertificateRequired: false,
                        System.Security.Authentication.SslProtocols.None, checkCertificateRevocation: false).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException)
                {
                    // A client that does not trust the certificate hangs up during the handshake; that is the point.
                    Interlocked.Increment(ref RefusedHandshakes);
                    await secure.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                stream = secure;
            }

            while (!_stop.IsCancellationRequested)
            {
                byte[]? message;

                try
                {
                    message = await ReadMessageAsync(stream).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    return;
                }

                if (message is null)
                {
                    return;
                }

                AsnReader outer = new AsnReader(message, AsnEncodingRules.BER).ReadSequence();
                int id = (int)outer.ReadInteger();
                Asn1Tag op = outer.PeekTag();

                if (op.TagClass != TagClass.Application)
                {
                    return;
                }

                switch (op.TagValue)
                {
                    case 0: // BindRequest
                        AsnReader bind = outer.ReadSequence(op);
                        bind.ReadInteger();
                        string name = Text(bind.ReadOctetString());
                        string password = Text(bind.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0)));
                        int result = Bind(name, password);
                        await WriteAsync(stream, Result(id, 1, result)).ConfigureAwait(false);
                        break;

                    case 2: // UnbindRequest
                        return;

                    case 3: // SearchRequest
                        foreach (byte[] reply in Search(id, outer.ReadSequence(op)))
                        {
                            await WriteAsync(stream, reply).ConfigureAwait(false);
                        }

                        break;

                    default:
                        // Anything else — an extended request, an abandon — is answered as not supported.
                        if (op.TagValue == 23)
                        {
                            await WriteAsync(stream, Result(id, 24, 2)).ConfigureAwait(false);
                        }

                        break;
                }
            }
        }
    }

    private int Bind(string name, string password)
    {
        if (name.Length == 0)
        {
            return 0;
        }

        if (!_entries.TryGetValue(name, out Entry? entry) || entry.Password is null
            || !string.Equals(entry.Password, password, StringComparison.Ordinal))
        {
            return 49; // invalidCredentials
        }

        if (!string.Equals(name, SearchDn, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref PersonBinds);
        }

        return 0;
    }

    private IEnumerable<byte[]> Search(int id, AsnReader request)
    {
        string baseDn = Text(request.ReadOctetString());
        int scope = request.ReadEnumeratedBytes().Span[^1];
        request.ReadEnumeratedBytes();
        request.ReadInteger();
        request.ReadInteger();
        request.ReadBoolean();
        ReadOnlyMemory<byte> filter = request.ReadEncodedValue();
        AsnReader wanted = request.ReadSequence();

        List<string> attributes = [];
        while (wanted.HasData)
        {
            attributes.Add(Text(wanted.ReadOctetString()));
        }

        IEnumerable<Entry> candidates = scope == 0
            ? _entries.TryGetValue(baseDn, out Entry? one) ? [one] : []
            : _entries.Values.Where(e => e.Dn.EndsWith(baseDn, StringComparison.OrdinalIgnoreCase));

        if (new AsnReader(filter, AsnEncodingRules.BER).PeekTag().TagValue == 9)
        {
            Interlocked.Increment(ref ChainSearches);
        }

        foreach (Entry entry in candidates.Where(e => Matches(e, new AsnReader(filter, AsnEncodingRules.BER))).ToList())
        {
            AsnWriter writer = new(AsnEncodingRules.BER);
            using (writer.PushSequence())
            {
                writer.WriteInteger(id);
                using (writer.PushSequence(new Asn1Tag(TagClass.Application, 4, true)))
                {
                    writer.WriteOctetString(Encoding.UTF8.GetBytes(entry.Dn));
                    using (writer.PushSequence())
                    {
                        foreach ((string name, List<string> values) in entry.Attributes)
                        {
                            bool asked = attributes.Count == 0 || attributes.Contains("*")
                                || attributes.Contains(name, StringComparer.OrdinalIgnoreCase);

                            if (!asked || attributes.Contains("1.1"))
                            {
                                continue;
                            }

                            using (writer.PushSequence())
                            {
                                writer.WriteOctetString(Encoding.UTF8.GetBytes(name));
                                using (writer.PushSetOf())
                                {
                                    foreach (string value in values)
                                    {
                                        writer.WriteOctetString(Encoding.UTF8.GetBytes(value));
                                    }
                                }
                            }
                        }
                    }
                }
            }

            yield return writer.Encode();
        }

        yield return Result(id, 5, 0);
    }

    private bool Matches(Entry entry, AsnReader reader)
    {
        Asn1Tag tag = reader.PeekTag();

        switch (tag.TagValue)
        {
            case 0: // and
            case 1: // or
                AsnReader set = reader.ReadSetOf(true, tag);
                List<bool> each = [];
                while (set.HasData)
                {
                    each.Add(Matches(entry, new AsnReader(set.ReadEncodedValue(), AsnEncodingRules.BER)));
                }

                return tag.TagValue == 0 ? each.All(b => b) : each.Any(b => b);

            case 2: // not
                AsnReader not = reader.ReadSequence(tag);
                return !Matches(entry, new AsnReader(not.ReadEncodedValue(), AsnEncodingRules.BER));

            case 3: // equalityMatch
                AsnReader pair = reader.ReadSequence(tag);
                string attribute = Text(pair.ReadOctetString());
                string value = Text(pair.ReadOctetString());
                return entry.Attributes.TryGetValue(attribute, out List<string>? values)
                    && values.Contains(value, StringComparer.OrdinalIgnoreCase);

            case 7: // present
                string present = Text(reader.ReadOctetString(tag));
                return present.Equals("objectClass", StringComparison.OrdinalIgnoreCase) || entry.Attributes.ContainsKey(present);

            case 9: // extensibleMatch: only AD's transitive membership on an attribute that names entries
                AsnReader rule = reader.ReadSequence(tag);
                string? matchingRule = null;
                string? type = null;
                string? wanted = null;

                while (rule.HasData)
                {
                    Asn1Tag part = rule.PeekTag();
                    string text = Text(rule.ReadOctetString(part));
                    switch (part.TagValue)
                    {
                        case 1: matchingRule = text; break;
                        case 2: type = text; break;
                        case 3: wanted = text; break;
                    }
                }

                return matchingRule == InChain && type is not null && wanted is not null
                    && Reaches(entry, type, wanted, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            default:
                return false;
        }
    }

    /// <summary>Whether an entry names <paramref name="dn"/> in <paramref name="attribute"/>, directly or through entries it names.</summary>
    private bool Reaches(Entry entry, string attribute, string dn, HashSet<string> seen)
    {
        if (!seen.Add(entry.Dn) || !entry.Attributes.TryGetValue(attribute, out List<string>? named))
        {
            return false;
        }

        return named.Contains(dn, StringComparer.OrdinalIgnoreCase)
            || named.Any(n => _entries.TryGetValue(n, out Entry? inner) && Reaches(inner, attribute, dn, seen));
    }

    /// <summary>A certificate authority for the run, in PEM, and a server certificate for 127.0.0.1 it issued.</summary>
    private static (string Authority, X509Certificate2 Server) MakeCertificates()
    {
        using RSA authorityKey = RSA.Create(2048);
        CertificateRequest authorityRequest = new("CN=Graticula test directory CA", authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        authorityRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(authorityRequest.PublicKey, false));
        using X509Certificate2 authority = authorityRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        using RSA serverKey = RSA.Create(2048);
        CertificateRequest serverRequest = new("CN=127.0.0.1", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        serverRequest.CertificateExtensions.Add(names.Build());
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        serverRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));

        using X509Certificate2 issued = serverRequest.Create(
            authority, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(7), RandomNumberGenerator.GetBytes(12));
        using X509Certificate2 withKey = issued.CopyWithPrivateKey(serverKey);

        // Through PKCS#12 so Windows' TLS stack has a key it can use; an ephemeral one is refused there.
        X509Certificate2 server = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
        return (authority.ExportCertificatePem(), server);
    }

    private static byte[] Result(int id, int operation, int code)
    {
        AsnWriter writer = new(AsnEncodingRules.BER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(id);
            using (writer.PushSequence(new Asn1Tag(TagClass.Application, operation, true)))
            {
                writer.WriteEnumeratedValue((ResultCode)code);
                writer.WriteOctetString([]);
                writer.WriteOctetString([]);
            }
        }

        return writer.Encode();
    }

    private static async Task<byte[]?> ReadMessageAsync(Stream stream)
    {
        byte[] head = new byte[2];
        if (!await FillAsync(stream, head).ConfigureAwait(false))
        {
            return null;
        }

        int length;
        byte[] lengthBytes = [];

        if ((head[1] & 0x80) == 0)
        {
            length = head[1];
        }
        else
        {
            lengthBytes = new byte[head[1] & 0x7F];
            if (!await FillAsync(stream, lengthBytes).ConfigureAwait(false))
            {
                return null;
            }

            length = lengthBytes.Aggregate(0, (n, b) => (n << 8) | b);
        }

        byte[] content = new byte[length];
        if (!await FillAsync(stream, content).ConfigureAwait(false))
        {
            return null;
        }

        return [.. head, .. lengthBytes, .. content];
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int got = await stream.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(false);
            if (got == 0)
            {
                return false;
            }

            read += got;
        }

        return true;
    }

    private static async Task WriteAsync(Stream stream, byte[] bytes) =>
        await stream.WriteAsync(bytes).ConfigureAwait(false);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}

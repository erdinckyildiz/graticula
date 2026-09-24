using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
/// <b>Plain LDAP on the loopback address</b>, which the server accepts only there.
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

    /// <summary>Starts the directory with its base and a search account.</summary>
    /// <param name="baseDn">The base, as in <c>dc=test</c>.</param>
    public FakeLdapServer(string baseDn)
    {
        BaseDn = baseDn;
        SearchDn = $"cn=reader,{baseDn}";
        SearchPassword = "reader-" + Guid.NewGuid().ToString("N")[..8];

        Add(baseDn, null, ("objectClass", ["top"]));
        Add($"ou=people,{baseDn}", null, ("objectClass", ["organizationalUnit"]));
        Add(SearchDn, SearchPassword, ("objectClass", ["person"]));

        _listener.Start();
        Address = $"ldap://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = Task.Run(AcceptAsync);
    }

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

    /// <summary>Adds a person under <c>ou=people</c> with a uid, a password, a display name and groups.</summary>
    public string AddPerson(string uid, string password, string displayName, params string[] groups)
    {
        string dn = $"uid={uid},ou=people,{BaseDn}";
        Add(dn, password,
            ("objectClass", ["person"]), ("uid", [uid]), ("displayName", [displayName]), ("memberOf", [.. groups]),
            ("entryUUID", [Guid.NewGuid().ToString()]));
        return dn;
    }

    /// <summary>Replaces a person's groups, as an administrator of the directory would.</summary>
    public void SetGroups(string dn, params string[] groups) => _entries[dn].Attributes["memberOf"] = [.. groups];

    public void Dispose()
    {
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
            NetworkStream stream = client.GetStream();

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

    private static bool Matches(Entry entry, AsnReader reader)
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

            default:
                return false;
        }
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

    private static async Task<byte[]?> ReadMessageAsync(NetworkStream stream)
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

    private static async Task<bool> FillAsync(NetworkStream stream, byte[] buffer)
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

    private static async Task WriteAsync(NetworkStream stream, byte[] bytes) =>
        await stream.WriteAsync(bytes).ConfigureAwait(false);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}

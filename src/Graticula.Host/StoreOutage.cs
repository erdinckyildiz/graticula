using System;
using System.IO;
using System.Net.Sockets;
using Npgsql;

namespace Graticula.Host;

/// <summary>Whether a failure means the store could not be reached for a while, which a loop waits out.</summary>
/// <remarks>
/// <b>Not only <see cref="NpgsqlException"/>.</b> Npgsql wraps what fails inside a conversation with the server, but a
/// connection that cannot begin can surface as the socket's own exception: on 2026-11-07 the showcase, short of memory,
/// could not resolve its datastore's name, <c>Dns.GetHostEntry</c> threw <see cref="SocketException"/> ("Resource
/// temporarily unavailable") straight through <c>NpgsqlConnection.Open</c>, the site heartbeat's catch did not know it,
/// and the host stopped — an hour's outage from a name lookup that would have worked ten seconds later. Every
/// background loop that keeps going through a store outage asks here, so the next exception of this kind is learnt
/// once.
/// </remarks>
internal static class StoreOutage
{
    /// <summary>True for a failure to reach or talk to the store, as opposed to a fault in what was asked.</summary>
    public static bool Is(Exception failure) =>
        failure is NpgsqlException or SocketException or IOException or TimeoutException;
}

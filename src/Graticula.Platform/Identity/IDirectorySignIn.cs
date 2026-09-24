using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Identity;

/// <summary>
/// A password checked against a directory rather than a hash here — ADR-089.
/// </summary>
/// <remarks>
/// <b>Asked by <see cref="LoginService"/>, and only for a name with no password here</b>, so the throttle, the
/// timing equalisation and the attempt record are the one sign-in's for both, and a local account is always its own:
/// ADR-088's rule that an account is never joined to another by a name they share.
/// </remarks>
public interface IDirectorySignIn
{
    /// <summary>The account a directory's password opens, found or made, with its groups applied; or null.</summary>
    /// <param name="name">The name typed.</param>
    /// <param name="password">The password typed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The principal, or null when no directory accepts the pair or none may make its account.</returns>
    Task<Principal?> SignInAsync(string name, string password, CancellationToken cancellationToken);
}

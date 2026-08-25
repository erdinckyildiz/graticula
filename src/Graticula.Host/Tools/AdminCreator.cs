using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host.Tools;

/// <summary>
/// Puts an administrator back on a store that has none.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-137](../../../docs/open-questions.md), owner decision 2026-08-25:</b> *"öyle bir
/// şeyin olamaması lazım. eğer ki olabiliyorsa uygulama içerisine bir cmd koyalım.
/// kurulum yerine."* — that state should not be reachable, and if it is, the answer is a
/// command inside the application rather than a re-armed setup.
/// </para>
/// <para>
/// <b>The state and why it is rare.</b>
/// [D-14](../../../docs/architecture-debt.md): a store with accounts and nobody holding
/// <c>administrator</c> keeps serving reads and can do nothing administrative. ADR-035's
/// guards refuse every API route that could produce it — the last administrator cannot be
/// demoted, deleted or disabled, and the role itself cannot be edited or dropped — so it
/// is now reachable only by a migration, a partial restore, or somebody writing to the
/// platform store directly.
/// </para>
/// <para>
/// <b>Why not re-arm setup, which was the obvious answer.</b> Issuing a fresh setup token
/// on that path would print a credential to the log every time the last administrator's
/// grant disappeared — and *the last administrator's grant disappeared* is a state an
/// attacker would like to arrange. A command needs a shell on the machine and the
/// platform store's connection string, which is the same access the recovery SQL already
/// required. It adds no authority to anybody who did not already have it; what it adds is
/// that the recovery is a supported operation with a password policy and an audit trail
/// rather than an <c>INSERT</c> somebody writes at three in the morning.
/// </para>
/// <para>
/// <b>It refuses when an administrator already exists</b>, and says so. A recovery tool
/// that also works on a healthy store is a way to mint an administrator, and the fact
/// that its user could have done it in SQL anyway is not a reason to make it one call.
/// </para>
/// <para>
/// <b>The password it sets is one-use, and that is the store's rule rather than this
/// command's.</b> Both of <c>PostgresMemberDirectory</c>'s write paths mark the credential
/// <c>must_change</c>, so the account signs in and then reaches nothing but
/// <c>POST /rest/auth/password</c> until its owner sets their own. That is worth more here
/// than anywhere else: a recovery password may have been typed as an argument, and this
/// makes the copy sitting in the shell history stop working the moment it is used once.
/// </para>
/// <para>
/// <b>Measured against a store with no administrator rather than assumed.</b> The first
/// draft of the success message sent the operator to the members screen — which is one of
/// the things that does not answer while the password is still the issued one. The account
/// it creates was signed in with, and the 403 it met named the route this text now names.
/// </para>
/// </remarks>
internal static class AdminCreator
{
    /// <summary>The name used when none is given — the owner's choice.</summary>
    private const string DefaultName = "admin";

    /// <summary>Where a password may come from instead of the command line.</summary>
    /// <remarks>
    /// <b>Because an argument lands in shell history and in the process table.</b> Both
    /// are readable by somebody who should not be reading the password of the account
    /// this command is creating. The argument form is still accepted, because a recovery
    /// at three in the morning should not fail on ergonomics — and it says what it cost.
    /// </remarks>
    private const string PasswordVariable = "GRATICULA_ADMIN_PASSWORD";

    /// <summary>The shortest password this accepts.</summary>
    /// <remarks>
    /// <b>The server's number, not a second one.</b> The first draft of this file carried
    /// its own <c>12</c> and a comment saying it matched the setup flow. It did not:
    /// ADR-015 §6a lowered the floor to 8 in 2026-08-14, for the reason that a rule nobody
    /// can justify gets routed around rather than followed — and the route around that one
    /// was a direct write to the platform store, which is the same access this command
    /// needs. A recovery path with a stricter rule than the login it recovers is exactly
    /// the propagation shape [D-130](../../../docs/architecture-debt.md) records: a number
    /// decided in one file and restated in another, where it goes stale alone.
    /// </remarks>
    private static int MinimumPasswordLength => AuthEndpoints.MinimumPasswordLength;

    /// <summary>Runs the command.</summary>
    /// <param name="services">The application's services.</param>
    /// <param name="args">The whole command line.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A process exit code.</returns>
    public static async Task<int> RunAsync(
        IServiceProvider services, string[] args, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);

        string name = Value(args, "--name") ?? DefaultName;
        string? fromArgument = Value(args, "--password");
        string? password = fromArgument ?? Environment.GetEnvironmentVariable(PasswordVariable);

        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine(
                $"A password is required. Set {PasswordVariable}, or pass --password — which "
                + "puts it in your shell history and in the process table, where somebody who "
                + "should not read it can.");

            return 2;
        }

        if (password.Length < MinimumPasswordLength)
        {
            Console.Error.WriteLine(
                $"The password must be at least {MinimumPasswordLength} characters. Length is the "
                + "only rule: composition requirements push people toward predictable passwords.");

            return 2;
        }

        HostSettings settings = services.GetRequiredService<HostSettings>();

        if (CommonPasswords.Known(password, settings.CommonPasswords))
        {
            Console.Error.WriteLine(
                "That password is one of the ones attackers try first, or a decorated version of "
                + "one. Three unrelated words are stronger than anything a composition rule can "
                + "ask for.");

            return 2;
        }

        IIdentityStore identity = services.GetRequiredService<IIdentityStore>();

        if (await identity.AnyPrincipalHoldingAsync(Roles.Administrator, cancellation)
                .ConfigureAwait(false))
        {
            Console.Error.WriteLine(
                "This store already has an administrator, so it does not need recovering. This "
                + "command exists for the state D-14 describes — accounts and nobody who can "
                + "administer them — and refusing here is deliberate: a recovery tool that also "
                + "works on a healthy store is a way to mint an administrator. Use the members "
                + "screen, or sign in as the one that exists.");

            return 3;
        }

        IMemberDirectory members = services.GetRequiredService<IMemberDirectory>();
        IPasswordHasher hasher = services.GetRequiredService<IPasswordHasher>();
        PasswordHash hashed = hasher.Hash(password);

        // <b>Created or repaired, because both states reach here.</b> A partial restore
        // can leave the account and lose the grant, and a migration can leave neither.
        // Refusing the first would send somebody to SQL for the case this exists to take
        // away from them.
        Principal? made = await members
            .CreateMemberAsync(name, name, hashed, Roles.Administrator, UserTypes.Unrestricted,
                cancellation)
            .ConfigureAwait(false);

        if (made is not null)
        {
            Console.WriteLine(
                $"Created '{name}' and granted {Roles.Administrator}. Sign in at "
                + "/rest/auth/login, then replace this password with POST /rest/auth/password. "
                + "Nothing else answers until you do.");

            return 0;
        }

        // <b>Null means the name is taken</b>, which on a store with no administrator is
        // the partial-restore case rather than a mistake.
        if (!await members.SetPasswordAsync(name, hashed, cancellation).ConfigureAwait(false))
        {
            Console.Error.WriteLine(
                $"'{name}' exists and its password could not be set. Nothing was changed.");

            return 4;
        }

        if (await members.SetRoleAsync(name, Roles.Administrator, cancellation)
                .ConfigureAwait(false) is null)
        {
            Console.Error.WriteLine(
                $"'{name}' exists, its password was set, and the {Roles.Administrator} grant was "
                + "not. The account can sign in and can do nothing; grant the role in the "
                + "platform store, or run this again once that is understood.");

            return 5;
        }

        Console.WriteLine(
            $"'{name}' already existed. Its password was replaced and it was granted "
            + $"{Roles.Administrator}. Sign in at /rest/auth/login, then replace this password "
            + "with POST /rest/auth/password. Nothing else answers until you do.");

        return 0;
    }

    /// <summary>The value after a flag, or null.</summary>
    private static string? Value(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}

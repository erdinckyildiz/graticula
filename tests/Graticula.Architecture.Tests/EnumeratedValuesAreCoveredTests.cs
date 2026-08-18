using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// Every value of an authorization enumeration is handled everywhere it is read.
/// </summary>
/// <remarks>
/// <para>
/// <b>The guard <see href="../../docs/architecture-debt.md">D-74</see> asked for, and it was asked for
/// after the same mistake four times in one day.</b> Adding <c>SharingScope.Group</c> left five
/// parsers behind — two in the host, three in the store — plus a user-type ceiling that withheld all
/// four new <c>groups:*</c> privileges, and two role validators that still checked a fixed list of
/// five. **The compiler finds none of them**, because the old code still compiles and still means
/// something: a `switch` with a discard arm is exhaustive as far as C# is concerned, and a set literal
/// that has stopped being complete is still a valid set.
/// </para>
/// <para>
/// <b>The failure mode is worse than a crash, which is why this is worth a test.</b> A parser that
/// does not know a value <em>refuses</em> it — so the feature silently does nothing, and a measurement
/// written around it passes. The group read-path measurement showed 200 for all four callers because
/// the service had never left `public`.
/// </para>
/// <para>
/// <b>It reads the source, and that is a deliberate choice.</b> Reflection can see a `switch`
/// expression's arms only as compiled branches, and the thing worth asserting is textual: *does the
/// name of every value appear where the others do*. A test that asked the type system would be asking
/// the wrong question — the type system is what missed this.
/// </para>
/// </remarks>
public sealed class EnumeratedValuesAreCoveredTests
{
    /// <summary>Where the source lives, from the test assembly's own location.</summary>
    private static string Root
    {
        get
        {
            DirectoryInfo? at = new(AppContext.BaseDirectory);

            while (at is not null && !Directory.Exists(Path.Combine(at.FullName, "src")))
            {
                at = at.Parent;
            }

            Assert.True(at is not null, "Could not find the repository root from the test assembly.");

            return Path.Combine(at!.FullName, "src");
        }
    }

    /// <summary>The two ways the compiled grants can be read.</summary>
    private static readonly string[] Readers = ["Roles.Grants", "Roles.PrivilegesOf"];

    /// <summary>
    /// A file's code with its comments removed.
    /// </summary>
    /// <remarks>
    /// <b>Because a doc comment naming a member is not a call to it.</b> The first version of the
    /// grants check reported four offenders and all four were <c>see cref</c> references — including
    /// one explaining the *reason* not to read it. A guard that fires on its own documentation
    /// teaches people to disable guards.
    /// </remarks>
    private static string CodeOnly(string text) =>
        string.Join(
            "\n",
            text.Split('\n')
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal)));

    /// <summary>Every C# file under `src`, excluding build output.</summary>
    private static IEnumerable<string> Sources() =>
        Directory.EnumerateFiles(Root, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
                !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)
                && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal));

    /// <summary>
    /// Every file that names two sharing scopes names all of them.
    /// </summary>
    /// <remarks>
    /// <b>Two, because that is what makes it a set rather than a mention.</b> A file that says
    /// `"public"` once is talking about one scope; a file that says `"private"` and `"organization"`
    /// is enumerating them, and an enumeration that is missing one is the defect.
    /// </remarks>
    [Fact]
    public void Every_file_that_enumerates_sharing_scopes_enumerates_all_of_them()
    {
        string[] scopes =
        [
            .. Enum.GetValues<SharingScope>().Select(s => s.ToString().ToLowerInvariant()),
        ];

        List<string> wrong = [];

        foreach (string file in Sources())
        {
            string text = File.ReadAllText(file);

            // <b>An explicit opt-out, and it has to name the value.</b> `HostedDataEndpoints` maps
            // everything it does not recognise to `private` on purpose, with a documented reason: a
            // default of *organisation* would share an import before its owner had seen it. That is a
            // decision and should not be a test failure.
            //
            // <b>A discard arm is not proof of exhaustiveness in a parser</b>, which is why the
            // opt-out is a marker rather than the arm itself. Four of the five parsers that missed
            // `group` had discard arms: they read the fourth scope as `private` and served a
            // group-scoped service to nobody, silently. A refusal is loud; a silent mis-mapping is
            // the failure this class exists for.
            if (text.Contains("enum-default-is-deliberate:", StringComparison.Ordinal))
            {
                continue;
            }

            // Quoted, so prose mentioning the word is not an enumeration.
            string[] present =
            [
                .. scopes.Where(scope => text.Contains(
                    "\"" + scope + "\"", StringComparison.Ordinal)),
            ];

            if (present.Length < 2 || present.Length == scopes.Length)
            {
                continue;
            }

            wrong.Add(
                Path.GetFileName(file) + " names " + string.Join(", ", present) + " and not "
                + string.Join(", ", scopes.Except(present)));
        }

        Assert.True(
            wrong.Count == 0,
            "These files enumerate some sharing scopes and not others, which is how a fourth scope "
            + "came to be refused by five parsers while the schema accepted it (D-74):\n  "
            + string.Join("\n  ", wrong)
            + "\n\nA scope a caller may set and a reader cannot parse is a feature that silently "
            + "does nothing. If a file maps the rest to one value on purpose, say so with a comment "
            + "reading `enum-default-is-deliberate: <value>` and this leaves it alone.");
    }

    /// <summary>
    /// Every privilege is admitted by at least one user type's ceiling.
    /// </summary>
    /// <remarks>
    /// <b>A ceiling that does not list a privilege withholds it.</b> The four <c>groups:*</c>
    /// privileges were added to the catalogue and to no ceiling, so a role granted `groups:create` was
    /// refused for every member whose user type is `creator` — which is every member who is not
    /// unrestricted. The refusal was correct and said so; the grant was simply unreachable.
    /// </remarks>
    [Fact]
    public void Every_privilege_is_reachable_through_some_user_type()
    {
        List<string> unreachable = [];

        foreach (Privilege privilege in Roles.AllPrivileges)
        {
            bool reachable = UserTypes.All.Any(
                type => UserTypes.CeilingOf(type).Contains(privilege));

            if (!reachable)
            {
                unreachable.Add(Roles.NameOf(privilege));
            }
        }

        Assert.True(
            unreachable.Count == 0,
            "No user type admits these privileges, so a role granted one confers nothing on anybody: "
            + string.Join(", ", unreachable)
            + ". ADR-018 §3a's ceiling withholds whatever it does not list, and a privilege in the "
            + "catalogue and in no ceiling is a tick an operator can make and cannot use (D-74).");
    }

    /// <summary>
    /// The privilege catalogue and the user-type ceilings agree about what exists.
    /// </summary>
    /// <remarks>
    /// <b>The other direction, which is cheaper to get wrong.</b> A ceiling naming a privilege the
    /// catalogue has dropped is dead configuration; it confers nothing and reads as though it does.
    /// </remarks>
    [Fact]
    public void No_ceiling_admits_a_privilege_that_does_not_exist()
    {
        ImmutableHashSet<Privilege> catalogue = [.. Roles.AllPrivileges];

        foreach (string type in UserTypes.All)
        {
            foreach (Privilege privilege in UserTypes.CeilingOf(type))
            {
                Assert.Contains(privilege, catalogue);
            }
        }
    }

    /// <summary>
    /// Nothing outside the seed and the resolver reads the compiled role grants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The guard <see href="../../docs/architecture-debt.md">D-71</see> asked for.</b> Role grants
    /// moved into the store on 2026-08-18 and three readers of the constant were left behind —
    /// <c>WithheldByUserType</c>, and two blocks of <c>/admin/members</c> — each of which agreed with
    /// the store until something edited a role, which is the worst kind of agreement: it survives
    /// every test and breaks the first time the feature is used.
    /// </para>
    /// <para>
    /// <b>Two callers are legitimate and are named here rather than excused in a comment.</b>
    /// `PlatformMigrations` reads them to write the seed, which is the whole point of the seed; and
    /// `Privilege.cs` defines them. Everything else must go through <see cref="IRoleGrants"/>, or it
    /// is answering from what this build was compiled with rather than from what the deployment
    /// decided.
    /// </para>
    /// </remarks>
    [Fact]
    public void Only_the_seed_and_the_definition_read_the_compiled_role_grants()
    {
        string[] permitted = ["PlatformMigrations.cs", "Privilege.cs", "IRoleGrants.cs"];

        List<string> offenders = [];

        foreach (string file in Sources())
        {
            if (permitted.Contains(Path.GetFileName(file), StringComparer.Ordinal))
            {
                continue;
            }

            string code = CodeOnly(File.ReadAllText(file));

            foreach (string reader in Readers)
            {
                if (code.Contains(reader, StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetFileName(file) + " reads " + reader);
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These read the grants this build was compiled with rather than the ones the deployment "
            + "decided (D-71):\n  " + string.Join("\n  ", offenders)
            + "\n\nUse IRoleGrants. The compiled table is the migration seed and nothing else — a "
            + "reader of it agrees with the store until somebody edits a role, and then reports what "
            + "the server no longer believes.");
    }
}

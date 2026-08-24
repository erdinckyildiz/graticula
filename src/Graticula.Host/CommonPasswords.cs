using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Graticula.Host;

/// <summary>
/// Refuses a password that is already published, as far as this deployment can tell.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-23](../../docs/architecture-debt.md): nothing checked a password against known-breached
/// lists.</b> Length was the only rule, so <c>Passw0rd!</c> and a random nine characters were
/// treated alike — and the first is in every wordlist. The row also said why it had not been
/// repaired: Pwned Passwords is about 10 GB, and the k-anonymity API leaks a hash prefix to a
/// third party. **Neither fits a product whose baseline is one deployable against one
/// PostgreSQL**, and that is still true.
/// </para>
/// <para>
/// <b>So this is deliberately not a breach corpus, and says so.</b> It carries a few hundred
/// bases — the passwords that appear at the top of every published list, which is where credential
/// stuffing actually lives — and normalises before comparing, so the decorations people add to
/// them do not buy anything. <c>Passw0rd!</c>, <c>p@ssword</c>, <c>PASSWORD123</c> and
/// <c>password!!</c> all reduce to <c>password</c>.
/// </para>
/// <para>
/// <b>And a deployment that wants the real thing can have it.</b>
/// <c>Graticula:CommonPasswordFile</c> takes a path to a newline-separated list — the top ten
/// thousand, the top million, or a full breach corpus if somebody wants to keep one — and it is
/// read at startup and added to these. The mechanism is the durable part; the built-in list is the
/// floor under it.
/// </para>
/// <para>
/// <b>What this does not claim.</b> It is not a breach check and a password it accepts may still
/// be published. [ADR-015](../../docs/adr/ADR-015-authentication.md) §6a's other defences —
/// Argon2id at 19 MiB per guess, and the rate limit — are what carry the weight against guessing.
/// This is aimed at the one thing they do nothing about: a password that is already known.
/// </para>
/// </remarks>
internal static class CommonPasswords
{
    /// <summary>
    /// The bases that appear at the top of every published list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bases rather than passwords, because the normalisation below does the rest.</b> There is
    /// no entry for <c>password1</c>, <c>Password!</c> or <c>P@ssw0rd</c>: all three reduce to
    /// <c>password</c>, which is here once.
    /// </para>
    /// <para>
    /// <b>Short ones are here even though they cannot pass the length rule alone</b>, because
    /// <c>abc123</c> is under eight characters and <c>abc123!!</c> is not.
    /// </para>
    /// </remarks>
    private static readonly string[] Bases =
    [
        // Keyboard walks and the digits everybody types first.
        "123456", "12345678", "123456789", "1234567890", "1234567", "12345", "111111", "000000",
        "121212", "123123", "654321", "666666", "888888", "999999", "112233", "789456",
        "qwerty", "qwertyuiop", "qwe123", "1q2w3e4r", "1qaz2wsx", "zaq12wsx", "asdfgh", "asdf",
        "zxcvbn", "zxcvbnm", "qazwsx", "poiuyt", "azerty", "qwertz",

        // The words themselves.
        "password", "passwd", "pass", "letmein", "welcome", "admin", "administrator", "root",
        "login", "user", "guest", "test", "demo", "default", "changeme", "secret", "master",
        "access", "manager", "superman", "batman", "trustno", "iloveyou", "sunshine", "princess",
        "dragon", "monkey", "shadow", "football", "baseball", "soccer", "hockey", "starwars",
        "computer", "internet", "samsung", "google", "facebook", "whatever", "freedom", "hello",
        "charlie", "michael", "jennifer", "jordan", "harley", "ranger", "hunter", "buster",
        "thomas", "robert", "daniel", "matthew", "andrew", "joshua", "amanda", "ashley",
        "nicole", "jessica", "michelle", "chocolate", "cookie", "flower", "summer", "winter",
        "spring", "autumn", "orange", "purple", "silver", "golden", "diamond", "money",
        "killer", "ginger", "pepper", "cheese", "banana", "apple", "mustang", "corvette",
        "ferrari", "porsche", "yamaha", "honda", "toyota", "nissan", "maggie", "jasmine",

        // Turkish, because this is where the deployment is and every list is local.
        "sifre", "parola", "sifrem", "merhaba", "selam", "deneme", "gizli", "bilgisayar",
        "galatasaray", "fenerbahce", "besiktas", "trabzonspor", "ankara", "istanbul", "izmir",
        "turkiye", "seninle", "askim", "canim", "hayat", "gunes", "kaplan", "aslan",

        // What people type when a form insists on something.
        "abc123", "abcdef", "abcd", "aaaaaa", "asdasd", "qweasd", "temp", "temppass",
        "letmein2", "opensesame", "whocares", "nopassword", "notapassword", "iforgot",

        // And the ones this kind of software collects.
        "postgres", "postgresql", "database", "server", "arcgis", "esri", "geoserver",
        "mapserver", "qgis", "gisadmin", "gisuser", "spatial", "graticula",
    ];

    /// <summary>Leet substitutions, undone in the second reading of a password.</summary>
    /// <remarks>
    /// <para>
    /// <b>Only the ones people actually use</b>, and applied in a second reading rather than the
    /// only one. Mapping <c>1</c> to <c>i</c> and <c>5</c> to <c>s</c> is right for
    /// <c>P@ssw0rd</c> and catastrophic for <c>12345678</c>, which is the single most common
    /// password there is — under one normalisation the digits become letters and the entry in the
    /// list stops matching itself. Found by writing exactly that and running the tests.
    /// </para>
    /// <para>
    /// <b><c>!</c> is deliberately not here.</b> It is decoration far more often than it is an
    /// <c>i</c>, and mapping it turned <c>Passw0rd!</c> into <c>passwordi</c> — which is in no
    /// list at all.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<char, char> Unleet = new()
    {
        ['0'] = 'o',
        ['1'] = 'i',
        ['3'] = 'e',
        ['4'] = 'a',
        ['5'] = 's',
        ['7'] = 't',
        ['@'] = 'a',
        ['$'] = 's',
        ['+'] = 't',
    };

    /// <summary>
    /// Reads a deployment's own list, if it named one.
    /// </summary>
    /// <param name="path">Where the file is, or null.</param>
    /// <returns>Everything in it, normalised.</returns>
    /// <remarks>
    /// <b>A named file that is not there is a startup failure.</b> A deployment that pointed at a
    /// breach corpus and got a silent fallback to a few hundred entries would believe it had a
    /// defence it does not have — which is the shape this whole row is about.
    /// </remarks>
    public static IReadOnlySet<string> Load(string? path)
    {
        HashSet<string> known = new(StringComparer.Ordinal);

        foreach (string entry in Bases)
        {
            known.Add(entry);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return known;
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Graticula:CommonPasswordFile points at '{path}', which is not there. It is "
                + "refused rather than ignored: a deployment that named a breach list and silently "
                + "fell back to the built-in few hundred would believe it had a defence it does "
                + "not have.");
        }

        foreach (string line in File.ReadLines(path))
        {
            string normalised = Reduce(line, unleet: false);

            if (normalised.Length > 0)
            {
                known.Add(normalised);
            }
        }

        return known;
    }

    /// <summary>
    /// Whether this password is one the list already knows.
    /// </summary>
    /// <param name="password">What somebody chose.</param>
    /// <param name="known">The list, normalised.</param>
    /// <returns>True when it should be refused.</returns>
    /// <remarks>
    /// <b>Compared after normalising, which is where the value is.</b> A list of literal strings
    /// catches <c>password</c> and misses <c>Passw0rd!</c> — the row's own example, and the one
    /// people actually choose when a form asks for a capital and a digit.
    /// </remarks>
    public static bool Known(string password, IReadOnlySet<string> known)
    {
        ArgumentNullException.ThrowIfNull(known);

        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        string literal = Reduce(password, unleet: false);

        if (literal.Length == 0)
        {
            // All punctuation: `!!!!!!!!` reduces to nothing, and nothing is not in the list —
            // so without this clause a password made entirely of the characters a composition
            // rule asks for would pass the rule aimed at it.
            return true;
        }

        // <b>Two readings, and either one matching is enough.</b> The literal reading keeps digits
        // as digits, which is what catches `12345678` and `password123`; the unleeted one turns
        // `P@ssw0rd` into `password`. Neither alone covers the other's cases.
        return Matches(literal, known) || Matches(Reduce(password, unleet: true), known);
    }

    /// <summary>Whether a reading, or that reading without its trailing digits, is in the list.</summary>
    /// <remarks>
    /// <b>A trailing year or run of digits is decoration, not entropy.</b> `password2024` and
    /// `sifre123` are the two most common shapes a composition rule produces, and both are the
    /// base with something stuck on the end.
    /// </remarks>
    private static bool Matches(string reading, IReadOnlySet<string> known)
    {
        if (reading.Length == 0)
        {
            return false;
        }

        if (known.Contains(reading))
        {
            return true;
        }

        string trimmed = reading.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        return trimmed.Length >= 3 && trimmed.Length != reading.Length && known.Contains(trimmed);
    }

    /// <summary>
    /// Reduces a password to the thing a wordlist would hold.
    /// </summary>
    /// <param name="value">What was typed.</param>
    /// <param name="unleet">Whether to read digits and symbols as the letters they stand in for.</param>
    /// <returns>Lower case, letters and digits only.</returns>
    /// <remarks>
    /// <b>Two readings rather than one, and the parameter is which.</b> See <see cref="Unleet"/>
    /// for why: a single normalisation that unleets destroys every numeric password, and one that
    /// does not misses every substituted one.
    /// </remarks>
    private static string Reduce(string value, bool unleet)
    {
        StringBuilder reduced = new(value.Length);

        foreach (char raw in value.Trim().ToLowerInvariant())
        {
            char letter = unleet && Unleet.TryGetValue(raw, out char plain) ? plain : raw;

            if (char.IsLetterOrDigit(letter))
            {
                reduced.Append(letter);
            }
        }

        return reduced.ToString();
    }

    /// <summary>How the list reads back, for a startup line an operator can check.</summary>
    /// <param name="known">The list.</param>
    /// <param name="path">The file it was extended from, if any.</param>
    /// <returns>A short description.</returns>
    public static string Describe(IReadOnlySet<string> known, string? path)
    {
        ArgumentNullException.ThrowIfNull(known);

        string size = known.Count.ToString("N0", CultureInfo.InvariantCulture);

        return string.IsNullOrWhiteSpace(path)
            ? $"{size} common passwords, built in — not a breach corpus (D-23)"
            : $"{size} common passwords, built in plus '{path}'";
    }
}

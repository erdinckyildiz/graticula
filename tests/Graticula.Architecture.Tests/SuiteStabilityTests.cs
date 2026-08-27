using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// The two conventions that keep the integration suites from failing on each other.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-111](../../docs/architecture-debt.md) was fixed in three passes and left two things
/// open, in its own words: <i>nothing detects a new class which publishes and forgets the
/// collection, and nothing stops the next reused class name from becoming somebody's oracle.
/// Both are conventions written down, which is weaker than a check.</i></b> These are the
/// checks.
/// </para>
/// <para>
/// <b>Source walks rather than browser runs</b>, because both faults are visible in the text and
/// neither needs a server. A failure here is a sentence about a file, before a suite has run at
/// all — which is the point: the defect they replace cost a morning of failures attributed twice
/// to the wrong cause.
/// </para>
/// </remarks>
public sealed class SuiteStabilityTests
{
    /// <summary>The prefixes `ArcGisClient.Fixture` treats as a test's own, not a deployment's.</summary>
    private static readonly string[] FixturePrefixes = ["zz_", "corpus_"];

    /// <summary>The collection that serialises the classes walking the catalogue against the ones publishing into it.</summary>
    private const string WalkCollection = "catalogue walk";

    private static DirectoryInfo Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!;
    }

    /// <summary>
    /// A conformance class that publishes names its fixtures so the walkers can tell them apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two ways to be safe, and the check accepts either</b> — because the repair D-89 and
    /// D-111 arrived at together is two mechanisms rather than one. A class in the
    /// <c>catalogue walk</c> collection cannot run beside a walker at all. A class outside it can
    /// still publish safely if what it publishes is named `zz_` or `corpus_`, because
    /// <c>ArcGisClient.Fixture</c> is what the walkers use to tell a test's own service from a
    /// deployment's.
    /// </para>
    /// <para>
    /// <b>What it checks is the literal, which is what makes it mechanical.</b> A publish whose
    /// name comes from a variable is not resolved — but the class still has to introduce that
    /// name somewhere, and a class with no fixture-prefixed literal at all is the case this is
    /// for: somebody adding a test that publishes `probe` and watching four unrelated suites go
    /// red an hour later.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_conformance_class_that_publishes_names_its_fixtures_or_joins_the_collection()
    {
        string folder = Path.Combine(Root().FullName, "tests", "Graticula.Conformance.Tests");

        Assert.True(Directory.Exists(folder), $"The conformance suite is not at '{folder}'.");

        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(folder, "*.cs"))
        {
            string source = File.ReadAllText(file);

            if (!source.Contains("serviceName", StringComparison.Ordinal))
            {
                continue;
            }

            if (source.Contains($"[Collection(\"{WalkCollection}\")]", StringComparison.Ordinal))
            {
                continue;
            }

            // A plain literal handed straight to the publish body, without a prefix: the
            // unambiguous form of the fault, and worth naming on its own.
            foreach (Match named in Regex.Matches(source, "serviceName\\s*=\\s*\"([^\"]+)\""))
            {
                if (!FixturePrefixes.Any(p => named.Groups[1].Value.StartsWith(p, StringComparison.Ordinal)))
                {
                    offenders.Add(
                        $"{Path.GetFileName(file)} publishes '{named.Groups[1].Value}', which is "
                        + "neither a fixture name nor inside the collection");
                }
            }

            bool anyFixtureName = FixturePrefixes.Any(p =>
                Regex.IsMatch(source, "\"" + Regex.Escape(p)));

            if (!anyFixtureName)
            {
                offenders.Add(
                    $"{Path.GetFileName(file)} publishes and holds no zz_ or corpus_ name at all");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A conformance class publishes into the catalogue that three other classes walk, "
            + "without either of the two things that make that safe: joining the "
            + $"'{WalkCollection}' collection, or naming what it publishes with a fixture prefix "
            + "so ArcGisClient.Fixture skips it. This is D-111's first open half — the convention "
            + "was written down and nothing checked it.\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The conformance suite's anonymous reader stays anonymous.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[ADR-023](../../docs/adr/ADR-023-rest-services-directory.md) condition 5.</b> *"The
    /// conformance suite's sign-in stays optional and stays off by default, so that **is this
    /// resource public?** remains a question the suite can answer."* The suite grew an optional
    /// sign-in because an organization-shared service cannot be tested anonymously, and the ADR
    /// recorded the cost in the same breath: *a suite that can sign in is a suite that can hide
    /// an authorization regression if somebody makes the login unconditional.*
    /// </para>
    /// <para>
    /// <b>The decay is silent and that is why it is checked.</b> Adding
    /// <c>await AuthenticateAsync(request, root)</c> to <c>AnonymousAsync</c> would be a
    /// one-line convenience; every anonymous test would keep passing while asserting nothing
    /// about anonymous callers, and nothing would look wrong. That is
    /// [D-174](../../docs/architecture-debt.md)'s shape — a check that passes for the wrong
    /// reason — applied to a whole class of test.
    /// </para>
    /// <para>
    /// <b>Read from the source rather than run, because the fault is in the code and not in an
    /// answer.</b> A request that carried a credential would produce exactly the responses these
    /// tests expect on a public deployment, so no run distinguishes the two.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_conformance_suites_anonymous_reader_sends_no_credential()
    {
        string file = Path.Combine(
            Root().FullName, "tests", "Graticula.Conformance.Tests", "ArcGisClient.cs");

        Assert.True(File.Exists(file), $"The conformance client is not at '{file}'.");

        string source = File.ReadAllText(file);

        int start = source.IndexOf("AnonymousAsync(string path)", StringComparison.Ordinal);

        Assert.True(start >= 0, "ArcGisClient has no AnonymousAsync, which every anonymous test uses.");

        int open = source.IndexOf('{', start);
        int depth = 0;
        int end = open;

        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') { depth++; }
            else if (source[i] == '}' && --depth == 0) { end = i; break; }
        }

        string body = source[open..end];

        foreach (string credential in (string[])
                 ["AuthenticateAsync", "Authorization", "TokenAsync", "?token="])
        {
            Assert.False(
                body.Contains(credential, StringComparison.Ordinal),
                $"ArcGisClient.AnonymousAsync mentions '{credential}', so the suite's anonymous "
                + "reader is not anonymous. Every test that asks whether a resource is public "
                + "would then be asking whether an administrator can read it, and would pass. "
                + "ADR-023 condition 5.");
        }

        // And somebody has to be using it, or the guard above guards nothing.
        string folder = Path.Combine(Root().FullName, "tests", "Graticula.Conformance.Tests");

        int callers = Directory.EnumerateFiles(folder, "*.cs")
            .Where(f => Path.GetFileName(f) != "ArcGisClient.cs")
            .Count(f => File.ReadAllText(f).Contains("AnonymousAsync(", StringComparison.Ordinal));

        Assert.True(
            callers > 0,
            "No conformance class reads anything anonymously any more, so the suite can no "
            + "longer answer whether a resource is public. ADR-023 condition 5.");
    }

    /// <summary>
    /// A class that changes or counts what the whole catalogue holds joins the collection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-185](../../docs/architecture-debt.md), and it is the check above having been half
    /// right.</b> That one accepts two ways to be safe: join the collection, or name what you
    /// publish `zz_`/`corpus_` so <c>ArcGisClient.Fixture</c> skips it. **The naming escape only
    /// protects a name.** A test that reads *how many layers exist*, does something, and asserts
    /// the number is unchanged is not protected by any naming convention at all — the count moves
    /// when anybody else publishes, whatever it is called.
    /// </para>
    /// <para>
    /// <b>Found by a failure that would not reproduce.</b> On 2026-08-27 a full conformance run
    /// failed `SilentPublishTests.A_publish_from_a_source_that_does_not_exist_is_refused`, whose
    /// assertion is that the layer count is the same before and after a refused publish; three
    /// further runs of the same binary passed. That signature — a failure set that changes
    /// between runs of unchanged code — is [D-75](../../docs/architecture-debt.md)'s, and the
    /// class was outside the collection while `EmptiedServiceTests` published and unpublished a
    /// layer in parallel.
    /// </para>
    /// <para>
    /// <b>So this check has no naming escape.</b> A class that publishes into the catalogue or
    /// reads the whole of it belongs in the collection, full stop. The cost is that those classes
    /// no longer run in parallel with each other; the alternative is a suite that fails somebody
    /// once every few runs and cannot say why.
    /// </para>
    /// <para>
    /// <b>One name is allowed through, with its reason.</b> <c>SecurityHeaderConformanceTests</c>
    /// names <c>/admin/layers</c> in an <c>InlineData</c> and asserts response headers; it reads
    /// the listing and consumes nothing from it. An allow-list of one with a stated reason is
    /// this repository's usual escape — <c>registers-check.py</c> does the same for the one
    /// external test name it cannot resolve — and it is deliberately not a pattern, because a
    /// pattern would wave through the next real one too.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_conformance_class_that_touches_the_whole_catalogue_joins_the_collection()
    {
        string folder = Path.Combine(Root().FullName, "tests", "Graticula.Conformance.Tests");

        Assert.True(Directory.Exists(folder), $"The conformance suite is not at '{folder}'.");

        // Reads the listing to assert headers on it, and consumes nothing from the body.
        string[] excused = ["SecurityHeaderConformanceTests.cs"];

        // Each is a whole-catalogue effect: the first three change what it holds, the last two
        // read all of it. None of them is made safe by what the fixture is called.
        (string Pattern, string What)[] reaches =
        [
            ("HttpMethod\\.Post,\\s*\"/admin/layers\"", "publishes a layer"),
            ("HttpMethod\\.Post,\\s*\"/admin/featureservices\"", "creates a service"),
            ("\"/admin/hosted/import\"", "imports into the catalogue"),
            ("EveryServiceNameAsync\\(", "enumerates every service"),
            ("HttpMethod\\.Get,\\s*\"/admin/layers\"", "lists every layer"),
        ];

        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(folder, "*.cs"))
        {
            string name = Path.GetFileName(file);

            if (excused.Contains(name, StringComparer.Ordinal) || name == "ArcGisClient.cs")
            {
                continue;
            }

            string source = File.ReadAllText(file);

            if (source.Contains($"[Collection(\"{WalkCollection}\")]", StringComparison.Ordinal))
            {
                continue;
            }

            foreach ((string pattern, string what) in reaches)
            {
                if (Regex.IsMatch(source, pattern))
                {
                    offenders.Add($"{name} {what}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"A conformance class reaches the whole catalogue from outside the '{WalkCollection}' "
            + "collection. Naming its fixtures does not make this safe: a layer count or a "
            + "service enumeration moves when anybody else publishes, whatever the fixture is "
            + "called. D-185.\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A console test's class selector is qualified when that class is used on more than one tag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-111's third pass, and the one that was not a race.</b> `.empty` names two things in
    /// this console — the empty-list <c>&lt;td&gt;</c> and the placeholder
    /// <c>&lt;div class="thumb empty"&gt;</c> a row shows when its service has no cover — and
    /// three tests used the loose selector as their oracle for *is this list empty*. One
    /// assertion became unsatisfiable; two would have read a full page as an empty one.
    /// </para>
    /// <para>
    /// <b>The rule is not *no bare class selectors*, because most classes are unambiguous.</b>
    /// Twenty-five of the console's classes are on more than one kind of element and nearly all
    /// of them should be — <c>mono</c>, <c>tiny</c>, <c>primary</c> are styling. What the check
    /// forbids is asking for one of *those* by class alone: `td.empty` says which one, `.empty`
    /// does not.
    /// </para>
    /// <para>
    /// <b>Read from the console's own source, so it moves when the console does.</b> A
    /// hard-coded list of ambiguous classes would be a second place to keep in step, which is the
    /// shape of the defect one row along.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_console_test_qualifies_a_class_selector_that_names_more_than_one_kind_of_element()
    {
        DirectoryInfo root = Root();
        string web = Path.Combine(root.FullName, "src", "Graticula.Host", "wwwroot");

        Dictionary<string, SortedSet<string>> tagsOfClass = new(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(web, "*.js")
                     .Concat(Directory.EnumerateFiles(web, "*.html")))
        {
            foreach (Match element in Regex.Matches(
                         File.ReadAllText(file),
                         "<([a-zA-Z][a-zA-Z0-9]*)\\b[^>]*?class=\\\\?\"([^\"\\\\]+)"))
            {
                foreach (string name in element.Groups[2].Value.Split(
                             ' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    // Interpolated pieces are not class names; they are whatever the value is.
                    if (name.Contains('$', StringComparison.Ordinal)
                        || name.Contains('{', StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!tagsOfClass.TryGetValue(name, out SortedSet<string>? tags))
                    {
                        tags = new SortedSet<string>(StringComparer.Ordinal);
                        tagsOfClass[name] = tags;
                    }

                    tags.Add(element.Groups[1].Value.ToLowerInvariant());
                }
            }
        }

        Assert.True(
            tagsOfClass.Count > 20,
            $"Only {tagsOfClass.Count} classes were found in the console's source, which means the "
            + "markup moved and this check is reading nothing. A check that cannot fail is worse "
            + "than no check.");

        string tests = Path.Combine(root.FullName, "tests", "Graticula.Console.Tests");
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(tests, "*.cs"))
        {
            foreach (Match call in Regex.Matches(
                         File.ReadAllText(file), "querySelector(?:All)?\\('(\\.[^']+)'\\)"))
            {
                string selector = call.Groups[1].Value;

                // The first component only: `.empty` is the fault, `td.empty` is the repair, and
                // `.gdbpick td.tick span.val` is already saying which element it means.
                string first = selector.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                string name = first.TrimStart('.').Split('.', ':', '[')[0];

                if (tagsOfClass.TryGetValue(name, out SortedSet<string>? tags) && tags.Count > 1)
                {
                    offenders.Add(
                        $"{Path.GetFileName(file)} asks for '{selector}', and '{name}' is on "
                        + string.Join(", ", tags));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A console test selects by a class name that the console puts on more than one kind "
            + "of element. Qualify it with the tag — 'td.empty' rather than '.empty'. This is "
            + "D-111's second open half: three tests used '.empty' as their oracle for an empty "
            + "list and two of them would have read a full page as an empty one.\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Every page the console serves is covered by every guard that claims to cover every page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-46, found inside D-46's own named remedy.</b> That row calls the enumerating form
    /// the fix — <c>Every_file_a_console_page_asks_for_is_permitted</c> walks whatever a page
    /// references, "which is the property the other two lack" — and then the enumerating test
    /// carried its own hand-typed list of pages, as did the inline-script guard beside it, and
    /// the two disagreed. Four pages against three: <c>/studio/view.html</c> was checked for a
    /// permitted subresource and not for an inline script it must never carry. The page
    /// happened to be clean, so nothing was broken; what was missing was the reason to believe
    /// it would stay that way.
    /// </para>
    /// <para>
    /// <b>So the list is read rather than written.</b> <c>Program</c> serves one physical
    /// <c>wwwroot</c> under two request paths, so the set of console pages is the set of
    /// <c>.html</c> files in that directory, and a new one is covered the moment it is added
    /// rather than when somebody remembers two <c>[Theory]</c> attributes. This is the same
    /// argument the class-selector check one method along already makes: a hard-coded list is a
    /// second place to keep in step, which is the defect rather than the repair.
    /// </para>
    /// <para>
    /// <b>What it does not claim.</b> Not every guard should walk every page.
    /// <c>The_console_reads_its_session_before_it_paints</c> is about <c>session.js</c>, which
    /// only the two shells load, and its two-page list is correct. The rule is scoped to the
    /// guards whose own names say <em>console page</em> — those are the ones whose coverage is
    /// a claim about all of them.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_console_page_is_covered_by_every_guard_that_claims_all_of_them()
    {
        DirectoryInfo root = Root();
        string web = Path.Combine(root.FullName, "src", "Graticula.Host", "wwwroot");

        // The default document answers the surface root; the rest answer their own name. Both
        // spellings are accepted, because a guard naming `/studio/` is covering `index.html`.
        Dictionary<string, string[]> spellings = new(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(web, "*.html"))
        {
            string name = Path.GetFileName(file);

            spellings[name] = name == "index.html"
                ? ["/server/", "/studio/"]
                : [$"/server/{name}", $"/studio/{name}"];
        }

        Assert.True(
            spellings.Count > 1,
            $"Only {spellings.Count} console page(s) were found under {web}, which means the "
            + "console moved and this check is reading nothing. A check that cannot fail is "
            + "worse than no check.");

        string guards = Path.Combine(
            root.FullName, "tests", "Graticula.Conformance.Tests",
            "SecurityHeaderConformanceTests.cs");

        string source = File.ReadAllText(guards);
        List<string> missing = [];

        // A guard is its `[InlineData]` run, and it ends at the method the attributes decorate.
        foreach (Match guard in Regex.Matches(
                     source,
                     "((?:\\s*\\[InlineData\\(\"[^\"]+\"\\)\\])+)\\s*public\\s+async\\s+Task\\s+"
                     + "(\\w*console_page\\w*)\\("))
        {
            string method = guard.Groups[2].Value;

            HashSet<string> covered = Regex.Matches(guard.Groups[1].Value, "\"([^\"]+)\"")
                .Select(path => path.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (KeyValuePair<string, string[]> page in spellings)
            {
                if (!page.Value.Any(covered.Contains))
                {
                    missing.Add($"{method} does not cover {page.Key} ({string.Join(" or ", page.Value)})");
                }
            }
        }

        Assert.True(
            missing.Count > 0 || Regex.IsMatch(source, "\\w*console_page\\w*\\("),
            "No guard in SecurityHeaderConformanceTests names a console page, so this check "
            + "matched nothing. Either the guards were renamed or they were removed.");

        Assert.True(
            missing.Count == 0,
            "A guard whose name claims every console page is missing one. The console serves one "
            + "directory under two paths, so adding a page adds it to every surface at once — and "
            + "a guard that lists its pages by hand does not follow. Read the directory, or add "
            + "the page to the attribute. D-46.\n  "
            + string.Join("\n  ", missing));
    }

    /// <summary>
    /// A sign-out that the server refused leaves the console's own session state alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-51](../../docs/architecture-debt.md), and the rule it states:</b> a client that
    /// holds a copy of session state clears it <em>only after the server has confirmed</em>,
    /// never before and never regardless. A session here exists in two forms — a bearer
    /// token the console keeps in <c>sessionStorage</c>, and a <c>gis-session</c> cookie it
    /// cannot touch by design — so **only the server can end one**. The original defect was
    /// a handler that swallowed the failure of the single request that does it
    /// (<c>catch { /* already gone */ }</c>) and reloaded anyway: the cookie signed the
    /// operator straight back in and the button appeared to do nothing. Reported as
    /// <em>"it does not sign out, it comes back to the same page."</em>
    /// </para>
    /// <para>
    /// <b>Checked from the source rather than through a browser</b>, because the console
    /// suites need a running server and this invariant does not. What it asserts is the
    /// shape of the failure path: the sign-out handler's <c>catch</c> must leave, and must
    /// not clear the token on its way out. The success path is where the clearing belongs
    /// and this check requires it to be there — a handler that never cleared would sign
    /// nobody out, so the absence has to fail too.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_refused_sign_out_does_not_clear_the_console_s_own_session()
    {
        string console = File.ReadAllText(Path.Combine(
            Root().FullName, "src", "Graticula.Host", "wwwroot", "console.js"));

        Match handler = Regex.Match(
            console,
            @"\$\(""signout""\)\.addEventListener\(.*?\n\}\);",
            RegexOptions.Singleline);

        Assert.True(
            handler.Success,
            "No sign-out handler was found in console.js, so this check is reading nothing. "
            + "Either the control was renamed or the handler moved, and a check that cannot "
            + "fail is worse than no check.");

        Match failure = Regex.Match(
            handler.Value, @"catch\s*\([^)]*\)\s*\{(.*?)\n  \}", RegexOptions.Singleline);

        Assert.True(
            failure.Success,
            "The sign-out handler has no catch block. D-51's whole subject is what happens "
            + "when the one request that ends a session fails: swallowing it, or not having a "
            + "path for it, is how the button came to look dead.\n" + handler.Value);

        Assert.DoesNotContain("sessionStorage", failure.Groups[1].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("token = null", failure.Groups[1].Value, StringComparison.Ordinal);

        // It must also stop rather than fall through to the clearing below.
        Assert.Contains("return", failure.Groups[1].Value, StringComparison.Ordinal);

        // And the caller must be told, because a refusal nobody sees is the same button.
        Assert.Contains("toast", failure.Groups[1].Value, StringComparison.Ordinal);

        // The success path still clears, or nobody signs out at all.
        Assert.Contains(
            "sessionStorage.removeItem(\"gis-token\")", handler.Value, StringComparison.Ordinal);
    }

    /// <summary>The symbology page shows its colours as colours, and divides the alpha.</summary>
    /// <remarks>
    /// <para>
    /// <b>[D-99](../../docs/architecture-debt.md): the least visual page in a product about
    /// maps.</b> A derived renderer was shown as raw JSON and a colour inside it as an RGBA
    /// quad. The swatches are the repair, and a repair with nothing holding it is one somebody
    /// removes while tidying — which is the lesson
    /// [D-26](../../docs/architecture-debt.md) records about a refusal nobody exercised.
    /// </para>
    /// <para>
    /// <b>Two things, and the second is the one that fails silently.</b> That the page draws
    /// swatches at all is visible the moment it stops. That the alpha is divided by 255 is
    /// not: ArcGIS writes <c>[r, g, b, a]</c> with a in 0–255, CSS wants 0–1, and handing the
    /// quad over unchanged makes every opaque colour clamp to 1 — which looks perfectly
    /// correct, on every colour that was opaque anyway. The half-transparent ones are the only
    /// evidence, and they are the rare case.
    /// </para>
    /// <para>
    /// <b>Read from source, because the console suites need a running server and this does
    /// not</b> — the same argument as the sign-out check above.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_symbology_page_draws_its_colours_and_scales_the_alpha()
    {
        string console = File.ReadAllText(Path.Combine(
            Root().FullName, "src", "Graticula.Host", "wwwroot", "console.js"));

        Assert.Contains("drawSwatches(r.drawingInfo)", console, StringComparison.Ordinal);
        Assert.Contains("id=\"symSwatches\"", console, StringComparison.Ordinal);

        Match convert = Regex.Match(
            console, @"function rgba\(color\)\s*\{(.*?)\n\}", RegexOptions.Singleline);

        Assert.True(
            convert.Success,
            "console.js has no rgba(color) helper, so the symbology swatches either draw "
            + "nothing or convert their colours somewhere this check cannot see. D-99.");

        Assert.Contains("/ 255", convert.Groups[1].Value, StringComparison.Ordinal);

        // <b>And the shape is read from the symbol rather than assumed — D-99.</b> A square is
        // right for a fill and wrong for a line, so the three ArcGIS symbol kinds have to reach
        // the chip. A swatch that drew every symbol as a square would still pass every check
        // above, because every check above is about colour.
        foreach (string kind in new[] { "esriSLS", "esriSMS" })
        {
            Assert.Contains(kind, console, StringComparison.Ordinal);
        }
    }

    /// <summary>A protocol face reads the catalogue through the fallback, not around it.</summary>
    /// <remarks>
    /// <para>
    /// <b>[D-127](../../docs/architecture-debt.md) was found by counting call sites by
    /// hand</b>, on 2026-08-20, and what it found was four of seven faces with no degraded
    /// path at all: same service, same instant, its MapServer legend answering 200 while its
    /// WMS `GetMap` refused 503. All four were given the fallback on 2026-08-23. Counting by
    /// hand is how the gap was found and is not how it stays closed.
    /// </para>
    /// <para>
    /// <b>The invariant, and it needs no list of faces.</b> `CatalogFallback` is the only
    /// thing that remembers a catalogue it cannot currently read, so a face that takes the
    /// raw catalogue has opted out of degrading by taking a different parameter — which is
    /// exactly how the four came to differ from the three without anybody choosing it.
    /// </para>
    /// <para>
    /// <b>`AdminEndpoints` is exempt and the exemption is the point.</b> An administrative
    /// action during a catalogue outage must fail rather than act on remembered state: a
    /// publish written against a fifteen-minute-old listing is a decision taken about a
    /// server nobody can currently see. Serving from memory is degradation; writing from
    /// memory is a different thing wearing the same word.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_protocol_face_takes_the_catalogue_without_the_fallback()
    {
        string host = Path.Combine(Root().FullName, "src", "Graticula.Host");
        List<string> offenders = [];
        int examined = 0;

        foreach (string file in Directory.EnumerateFiles(host, "*Endpoints.cs"))
        {
            string name = Path.GetFileName(file);

            if (name == "AdminEndpoints.cs" || name == "CoverageAdminEndpoints.cs")
            {
                continue;
            }


            examined++;

            string text = Regex.Replace(
                File.ReadAllText(file), @"^[ \t]*(///|//).*$", string.Empty,
                RegexOptions.Multiline);

            // <b>An administrative handler is exempt, and the exemption is derived rather than
            // typed.</b> The first version of this check exempted whole files, and it named
            // `RelationshipEndpoints` — which mixes both kinds, because relationships are
            // declared through `/admin/relationships` and *queried* through the service. Its
            // three raw-catalogue methods are all admin handlers and are right to be: an
            // administrative action during a catalogue outage must fail rather than act on
            // remembered state. That was recorded as
            // [D-153](../../docs/architecture-debt.md) and withdrawn the same day, once the
            // routes rather than the filename were read.
            //
            // So the exemption is the route: whatever `app.Map*("/admin/…", Handler)` names.
            // And only a **handler** is examined at all — a private helper takes whatever its
            // caller already has, so `Find` holding a raw catalogue says nothing about a face;
            // it says its callers are administrative, which they are.
            HashSet<string> administrative = new(StringComparer.Ordinal);
            HashSet<string> handlers = new(StringComparer.Ordinal);

            foreach (Match route in Regex.Matches(
                         text, @"app\.Map\w+\(\s*(?:\$?""|[\w.]+)([^,]*),[^,]*?(\w+)\s*\)"))
            {
                handlers.Add(route.Groups[2].Value);

                if (route.Groups[1].Value.Contains("/admin/", StringComparison.Ordinal))
                {
                    administrative.Add(route.Groups[2].Value);
                }
            }

            foreach (Match handler in Regex.Matches(
                         text,
                         @"static\s+async\s+Task(?:<[^>]+>)?\s+(\w+)\s*\((.*?)\)\s*\{",
                         RegexOptions.Singleline))
            {
                if (!handlers.Contains(handler.Groups[1].Value)
                    || administrative.Contains(handler.Groups[1].Value))
                {
                    continue;
                }

                if (Regex.IsMatch(
                        handler.Groups[2].Value, @"\b(PostgresLayerCatalog|ILayerCatalog)\b"))
                {
                    offenders.Add(
                        $"{name}.{handler.Groups[1].Value} takes the catalogue directly");
                }
            }
        }

        Assert.True(
            examined >= 4,
            $"Only {examined} endpoint files were examined, so this check is reading nothing. "
            + "A check that cannot fail is worse than no check.");

        Assert.True(
            offenders.Count == 0,
            "A protocol face takes the catalogue directly instead of through CatalogFallback, "
            + "so it cannot serve from the remembered listing when the platform store is "
            + "unreachable — while the faces beside it can. That difference is visible from "
            + "outside: same service, same instant, one face 200 and another 503. D-127.\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A conformance test that asks one service says why one is enough, in its own words.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-65](../../docs/architecture-debt.md): a test whose coverage is a fact about the
    /// data reports on the data rather than on the server.</b> Most of the ArcGIS suite asks
    /// its question of one service and is right to — a form's parameters, a button, an
    /// <c>Accept</c> header. Some claims are universal, and those must walk every layer. From
    /// the call site the two were indistinguishable, and one of them —
    /// <c>Pages_do_not_overlap_or_skip</c> — sat in the suite passing for four days while three
    /// of the owner's ten layers were skipping rows.
    /// </para>
    /// <para>
    /// <b>The compiler now asks for the reason and this asks for it to be a real one.</b>
    /// <c>FirstServiceNameAsync</c> takes a <c>whyOneIsEnough</c> string, so a new test cannot
    /// be written without stating which kind it is; what a compiler cannot check is that the
    /// string says anything. So: long enough to be a sentence, and not a restatement of the
    /// method's own name, which is the shape a placeholder takes.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_test_that_asks_one_service_says_why_one_is_enough()
    {
        string conformance = Path.Combine(
            Root().FullName, "tests", "Graticula.Conformance.Tests");

        List<string> thin = [];
        int examined = 0;

        foreach (string file in Directory.EnumerateFiles(conformance, "*.cs"))
        {
            string text = File.ReadAllText(file);

            foreach (Match call in Regex.Matches(
                         text, @"FirstServiceNameAsync\(\s*""([^""]*)""\s*\)", RegexOptions.Singleline))
            {
                examined++;

                string reason = Regex.Replace(call.Groups[1].Value, @"\s+", " ").Trim();

                if (reason.Length < 30)
                {
                    thin.Add(
                        $"{Path.GetFileName(file)}: \"{reason}\" is too short to say which kind "
                        + "of claim this test makes");
                }
            }
        }

        Assert.True(
            examined > 4,
            $"Only {examined} call sites were found, so this check is reading nothing. Either "
            + "the helper was renamed or the suite moved. A check that cannot fail is worse "
            + "than no check.");

        Assert.True(
            thin.Count == 0,
            "A conformance test asks one service and does not say why one is enough. The "
            + "difference between 'should ask one' and 'forgot to ask all' is invisible from "
            + "the call site, and it hid for four days once. D-65.\n  "
            + string.Join("\n  ", thin));
    }
}

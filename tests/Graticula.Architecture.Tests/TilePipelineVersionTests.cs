using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Graticula.Tiles;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// The tiling pipeline's declared generation cannot go stale.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-155](../../docs/architecture-debt.md), and the fix it names is the one that
/// does not work.</b> The tile cache key described the data and never the code, so an
/// upgrade that changed how a tile is drawn kept every key it had. Stamping the release
/// into the path throws away every tile on every release, including the ones that change
/// nothing about tiling — which is
/// [ADR-010](../../docs/adr/ADR-010-caching.md) §8's objection arriving from the other
/// side.
/// </para>
/// <para>
/// <b>So the generation is declared, and this is what stops the declaration lying.</b>
/// D-155 names that risk exactly: a stamp maintained by whoever changes the pipeline is
/// unmaintained the first time somebody forgets, and then it lies in the situation it
/// exists for — the same argument [Q-101](../../docs/open-questions.md) makes about a
/// hand-maintained capability vocabulary, and the same answer. The build fails when the
/// files that decide a tile's bytes change and <see cref="TilePipeline.Version"/> does
/// not.
/// </para>
/// <para>
/// <b>It hashes source text, which is coarse on purpose.</b> A comment edit trips it. The
/// alternative — hashing IL, or listing methods — is a second description of what the
/// pipeline is, and a wrong one drifts silently while this one fails loudly and is
/// answered in one line. Failing on a comment costs a moment's thought about whether the
/// bytes moved; failing to fail costs a map drawn from two generations of code.
/// </para>
/// </remarks>
public sealed class TilePipelineVersionTests
{
    /// <summary>
    /// The files whose contents decide what bytes a tile is made of.
    /// </summary>
    /// <remarks>
    /// <b>Named rather than globbed.</b> A glob over <c>src/</c> would trip on every
    /// commit and teach whoever hits it to raise the number without thinking, which is
    /// worse than no check. A file joining this list is a decision somebody takes when
    /// they add something that draws a tile.
    /// </remarks>
    private static readonly string[] Pipeline =
    [
        "src/Graticula.Core/Tiles/TileCacheKey.cs",
        "src/Graticula.Core/Tiles/TileAddress.cs",
        "src/Graticula.Core/Tiles/ITileSource.cs",
        "src/Graticula.Providers.PostGis/PostGisTileSource.cs",
        "src/Graticula.Host/VectorTileEndpoints.cs",
    ];

    /// <summary>
    /// The hash the files had when <see cref="TilePipeline.Version"/> was last raised.
    /// </summary>
    /// <remarks>
    /// <b>Recorded here rather than generated into source.</b> A file the build rewrites
    /// is a file that agrees with itself by construction, which is the check answering
    /// its own question.
    /// </remarks>
    private const string RecordedHash =
        "a02814428b7ffbf5543b31a350a19c863d8bf5de0373654e063593c53777a5c2";

    /// <summary>The generation that hash belongs to.</summary>
    private const int RecordedVersion = 1;

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

            return at!.FullName;
        }
    }

    private static string HashOf(out List<string> missing)
    {
        StringBuilder all = new();
        missing = [];

        foreach (string file in Pipeline)
        {
            string path = Path.Combine(Root, file.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                missing.Add(file);
                continue;
            }

            // Line endings normalised: this repository checks out CRLF on Windows and LF
            // in CI, and a hash that changed with the working copy would fail on one of
            // them for a reason that has nothing to do with tiles.
            all.Append(file).Append('\n')
               .Append(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal))
               .Append('\n');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(all.ToString())));
    }

    [Fact]
    public void The_pipeline_has_not_changed_without_its_generation_changing()
    {
        string actual = HashOf(out List<string> missing);

        Assert.True(
            missing.Count == 0,
            $"These files are in the pipeline list and not on disk: {string.Join(", ", missing)}. "
            + "A renamed or deleted file must leave the list, and whoever moves it is the person "
            + "who knows whether the bytes moved with it.");

        Assert.Equal(RecordedVersion, TilePipeline.Version);

        Assert.True(
            string.Equals(actual, RecordedHash, StringComparison.Ordinal),
            "The tiling pipeline's source has changed and `TilePipeline.Version` has not.\n\n"
            + $"  recorded: {RecordedHash}\n"
            + $"  now:      {actual}\n\n"
            + "If the change alters what bytes a tile is made of, raise `TilePipeline.Version` "
            + "and `RecordedVersion` together — every cached tile in every deployment becomes "
            + "unreachable, which is the intended effect and is why it is a decision rather than "
            + "a side effect of shipping (ADR-010 §8, D-155).\n\n"
            + "If it does not — a comment, a rename, a refusal message — leave the version alone "
            + "and update `RecordedHash` to the value above. Both answers are deliberate; what "
            + "this check exists to prevent is neither being given.");
    }

    [Fact]
    public void A_tile_path_carries_the_generation_that_built_it()
    {
        // <b>Under the layer id, not above it.</b> `FileSystemTileCache.Purge` matches
        // index keys by the layer id as a prefix and deletes `{root}/{layerId}` as a
        // directory, so a version segment in front would make every purge match nothing
        // and delete nothing — silently, which is how an unpublished layer's tiles would
        // survive and be served under a republished name.
        TileCacheKey key = new(
            new Guid("11111111-2222-3333-4444-555555555555"),
            "abcd1234",
            new TileAddress(5, 6, 7));

        string path = key.Path();

        Assert.StartsWith(
            "11111111222233334444555555555555/",
            path,
            StringComparison.Ordinal);

        Assert.Contains(
            $"/v{TilePipeline.Version.ToString(CultureInfo.InvariantCulture)}/",
            path,
            StringComparison.Ordinal);
    }
}

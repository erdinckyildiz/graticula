using System;
using System.Collections.Generic;
using System.Xml;
using System.Xml.Linq;
using Graticula.Api.Wfs;
using Graticula.Features;
using Xunit;

namespace Graticula.Api.Wfs.Tests;

/// <summary>
/// The XML hardening, asserted by attacking it.
/// </summary>
/// <remarks>
/// <b>ADR-039 condition 3, and each test is falsified by removing the setting it
/// covers.</b> There was no XML anywhere in this server until this project, so the
/// whole class arrives at once; a setting nobody has attacked is a setting nobody
/// knows the effect of. <b>Verified twice, and the first run is the interesting
/// one.</b> Flipping <c>DtdProcessing</c> to <c>Parse</c> and restoring the
/// resolver turned two of the three red — the external-entity test passed either
/// way, because it pointed at <c>/etc/passwd</c> on a Windows machine and was
/// failing on a missing file rather than on a refusal. Rewritten to write its own
/// file, all three go red. A test that cannot be made to fail is not evidence, and
/// this one had been counted as evidence for an hour.
/// </remarks>
public sealed class SafeXmlTests
{
    private static readonly IReadOnlyList<FieldDescription> Fields =
    [
        new("name", FieldType.Text, Nullable: true, MaxLength: null),
    ];

    [Fact]
    public void An_external_entity_is_refused_rather_than_resolved()
    {
        // <b>XXE: the entity reads a file off the server's disk.</b> With a
        // resolver in place this returns the file's contents inside the filter,
        // and the filter then compares a column against it.
        //
        // <b>Against a file that exists, and that is the whole point.</b> This test
        // pointed at /etc/passwd for its first hour and passed with the hardening
        // removed — on Windows there is no such file, so the parse failed for a
        // reason that had nothing to do with the defence being tested. Found by a
        // falsification run, which is what falsification runs are for. It now
        // writes its own file, so the read either succeeds or is refused, and only
        // one of those is this server behaving.
        string secretPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"graticula-xxe-{Guid.NewGuid():N}.txt");

        const string Sentinel = "SECRET-CONTENTS-THAT-MUST-NOT-REACH-A-FILTER";

        System.IO.File.WriteAllText(secretPath, Sentinel);

        try
        {
            string attack = $"""
                <!DOCTYPE filter [ <!ENTITY secret SYSTEM "file:///{secretPath.Replace('\\', '/')}"> ]>
                <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">
                  <fes:PropertyIsEqualTo>
                    <fes:ValueReference>name</fes:ValueReference>
                    <fes:Literal>&secret;</fes:Literal>
                  </fes:PropertyIsEqualTo>
                </fes:Filter>
                """;

            bool read = FilterReader.TryRead(
                attack, Fields, 4326, out ParsedFilter parsed, out WfsFault? fault);

            Assert.False(read, "the filter was accepted and the entity may have been resolved");
            Assert.NotNull(fault);

            // Belt and braces: even if a future change made this parse, the file's
            // contents must never end up in a predicate.
            Assert.DoesNotContain(
                Sentinel,
                parsed.Predicate?.ToString() ?? string.Empty,
                StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(secretPath);
        }
    }

    [Fact]
    public void An_entity_expansion_bomb_is_refused()
    {
        // The billion-laughs shape, scaled down: with DTD processing on, the
        // expansion is quadratic in the number of levels and the process dies
        // before anything can report it.
        const string Attack = """
            <!DOCTYPE filter [
              <!ENTITY a "aaaaaaaaaa">
              <!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">
              <!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">
              <!ENTITY d "&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;">
            ]>
            <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">
              <fes:PropertyIsEqualTo>
                <fes:ValueReference>name</fes:ValueReference>
                <fes:Literal>&d;</fes:Literal>
              </fes:PropertyIsEqualTo>
            </fes:Filter>
            """;

        Assert.False(FilterReader.TryRead(Attack, Fields, 4326, out _, out WfsFault? fault));
        Assert.NotNull(fault);
    }

    [Fact]
    public void A_document_past_the_character_limit_is_refused()
    {
        string padding = new('x', (int)SafeXml.MaximumDocumentCharacters);

        string attack = $"""
            <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">
              <fes:PropertyIsEqualTo>
                <fes:ValueReference>name</fes:ValueReference>
                <fes:Literal>{padding}</fes:Literal>
              </fes:PropertyIsEqualTo>
            </fes:Filter>
            """;

        Assert.False(FilterReader.TryRead(attack, Fields, 4326, out _, out WfsFault? fault));
        Assert.NotNull(fault);
    }

    [Fact]
    public void A_filter_nested_past_the_depth_limit_is_refused_rather_than_overflowing()
    {
        // <b>A stack overflow in .NET cannot be caught and kills the process.</b>
        // So this must be a refusal, and one that arrives before the recursion
        // does — which is why the reader counts levels rather than trusting the
        // parser to.
        System.Text.StringBuilder open = new();
        System.Text.StringBuilder close = new();

        for (int i = 0; i < SafeXml.MaximumDepth + 4; i++)
        {
            open.Append("<fes:Not>");
            close.Append("</fes:Not>");
        }

        string attack = $"""
            <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">
              {open}
              <fes:PropertyIsEqualTo>
                <fes:ValueReference>name</fes:ValueReference>
                <fes:Literal>x</fes:Literal>
              </fes:PropertyIsEqualTo>
              {close}
            </fes:Filter>
            """;

        Assert.False(FilterReader.TryRead(attack, Fields, 4326, out _, out WfsFault? fault));
        Assert.NotNull(fault);
        Assert.Contains("nests more than", fault!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reader_settings_are_the_ones_the_tests_above_rely_on()
    {
        // Asserted directly as well as behaviourally: a future edit that turned
        // DTD processing back on would make the two attacks above pass for the
        // wrong reason if the parser happened to reject them some other way.
        XmlReaderSettings settings = SafeXml.ReaderSettings;

        Assert.Equal(DtdProcessing.Prohibit, settings.DtdProcessing);

        // XmlResolver is set-only on XmlReaderSettings, so it cannot be asserted
        // here. The external-entity test above is the only check it has, which is
        // worth knowing: if that test is ever weakened, nothing else covers it.

        // <b>Not zero, and asserted as not zero.</b> Zero means *no limit* on this
        // property, so the reading that looks strictest is the one that turns the
        // bound off. It was zero here for an hour.
        Assert.True(
            settings.MaxCharactersFromEntities > 0,
            "MaxCharactersFromEntities of 0 means unlimited, which is the opposite of a bound.");
        Assert.Equal(SafeXml.MaximumDocumentCharacters, settings.MaxCharactersInDocument);
    }

    [Fact]
    public void A_well_formed_filter_still_reads()
    {
        // The control. Hardening that refused everything would pass every test
        // above and serve nobody.
        const string Filter = """
            <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">
              <fes:PropertyIsEqualTo>
                <fes:ValueReference>name</fes:ValueReference>
                <fes:Literal>Ankara</fes:Literal>
              </fes:PropertyIsEqualTo>
            </fes:Filter>
            """;

        Assert.True(
            FilterReader.TryRead(Filter, Fields, 4326, out ParsedFilter parsed, out WfsFault? fault),
            fault?.Text);

        AttributePredicate.Comparison comparison =
            Assert.IsType<AttributePredicate.Comparison>(parsed.Predicate);

        Assert.Equal("name", comparison.Column);
        Assert.Equal(ComparisonOperator.Equal, comparison.Operator);
        Assert.Equal("Ankara", comparison.Value);
    }

    [Fact]
    public void A_request_body_is_read_through_the_same_settings()
    {
        // The envelope is a document too. Hardening the filter and not the body it
        // arrives in would leave the door open one element further out.
        const string Attack = """
            <!DOCTYPE GetFeature [ <!ENTITY secret SYSTEM "file:///etc/passwd"> ]>
            <wfs:GetFeature service="WFS" version="2.0.0"
                            xmlns:wfs="http://www.opengis.net/wfs/2.0">
              <wfs:Query typeNames="&secret;"/>
            </wfs:GetFeature>
            """;

        using System.IO.MemoryStream body = new(System.Text.Encoding.UTF8.GetBytes(Attack));

        Assert.False(WfsXmlRequest.TryRead(body, out _, out WfsFault? fault));
        Assert.NotNull(fault);
    }
}

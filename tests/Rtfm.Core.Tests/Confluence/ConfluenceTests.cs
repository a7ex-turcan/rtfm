using System.Text.Json;
using Rtfm.Core.Confluence;

namespace Rtfm.Core.Tests.Confluence;

/// <summary>
/// The Phase 26 step-1 pure-logic seams (§2.17): page-id parsing, the synthetic
/// source key, base-URL normalization, JSON parsing (hierarchy, version,
/// in-body link extraction), and rendering. No network — <see cref="ConfluenceClient.Parse"/>
/// is fed a fixture element, per the suite's unit-only boundary.
/// </summary>
public class ConfluenceTests
{
    [Theory]
    [InlineData("https://x.atlassian.net/wiki/spaces/AISDLC/pages/6598099635/AI+SDLC", "6598099635")]
    [InlineData("6598099635", "6598099635")]
    [InlineData("  https://x.atlassian.net/wiki/spaces/DEV/pages/12345/Some+Title?focusedCommentId=9 ", "12345")]
    public void ParsePageId_extracts_from_url_or_bare_id(string input, string expected)
        => Assert.Equal(expected, ConfluenceSource.ParsePageId(input));

    [Theory]
    [InlineData("")]
    [InlineData("https://x.atlassian.net/wiki/x/abcDEF")]   // short link — no numeric id
    [InlineData("not a page")]
    public void ParsePageId_returns_null_when_no_id(string input)
        => Assert.Null(ConfluenceSource.ParsePageId(input));

    [Fact]
    public void ParseSeed_classifies_the_three_url_shapes_and_the_space_override()
    {
        Assert.Equal(new ConfluenceSeed(ConfluenceSeedKind.Space, "PR"),
            ConfluenceSource.ParseSeed("https://x.atlassian.net/wiki/spaces/PR/overview?homepageId=5538054612"));
        Assert.Equal(new ConfluenceSeed(ConfluenceSeedKind.Folder, "6416695310"),
            ConfluenceSource.ParseSeed("https://x.atlassian.net/wiki/spaces/PR/folder/6416695310"));
        Assert.Equal(new ConfluenceSeed(ConfluenceSeedKind.Page, "6491078666"),
            ConfluenceSource.ParseSeed("https://x.atlassian.net/wiki/spaces/PR/pages/6491078666/Current+PAM+PMM+overview"));
        Assert.Equal(new ConfluenceSeed(ConfluenceSeedKind.Page, "12345"), ConfluenceSource.ParseSeed("12345"));

        // --space override wins regardless of the positional input.
        Assert.Equal(new ConfluenceSeed(ConfluenceSeedKind.Space, "DEV"),
            ConfluenceSource.ParseSeed("https://x.atlassian.net/wiki/spaces/PR/pages/1/x", spaceOverride: "DEV"));

        Assert.Null(ConfluenceSource.ParseSeed(""));
    }

    [Fact]
    public void Source_key_round_trips()
    {
        Assert.Equal("confluence://6598099635", ConfluenceSource.Key("6598099635"));
        Assert.True(ConfluenceSource.IsConfluenceKey("confluence://6598099635"));
        Assert.False(ConfluenceSource.IsConfluenceKey("jira://AEXP-1"));
    }

    [Theory]
    [InlineData("internationalsos.atlassian.net", "https://internationalsos.atlassian.net")]
    [InlineData("https://x.atlassian.net/wiki/spaces/DEV", "https://x.atlassian.net")]
    public void NormalizeBaseUrl_reduces_to_scheme_and_host(string input, string expected)
        => Assert.Equal(expected, ConfluenceConfig.NormalizeBaseUrl(input));

    [Fact]
    public void ResolveToken_expands_env_and_fails_loudly_when_unset()
    {
        var setVar = "RTFM_TEST_CONF_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(setVar, "secret-token");
        try
        {
            var config = new ConfluenceConfig("https://x.atlassian.net", "me@x.com", $"${{{setVar}}}");
            Assert.Equal("secret-token", config.ResolveToken());
            Assert.Throws<InvalidDataException>(() => (config with { Token = "${RTFM_TEST_CONF_UNSET}" }).ResolveToken());
        }
        finally
        {
            Environment.SetEnvironmentVariable(setVar, null);
        }
    }

    [Fact]
    public void Parse_maps_page_reads_hierarchy_version_and_in_body_links()
    {
        const string json =
            """
            {
              "id": "6598099635",
              "title": "AI SDLC Programme",
              "type": "page",
              "space": {"key": "AISDLC"},
              "version": {"number": 3, "when": "2026-07-01T12:18:00.611Z", "by": {"displayName": "Michael Dimitriadis"}},
              "ancestors": [{"title": "Home"}, {"title": "Programmes"}],
              "children": {"page": {"results": [{"id": "111", "title": "Scope"}, {"id": "222", "title": "Plan"}], "size": 2}},
              "body": {"view": {"value": "<h2>Description</h2><p>See <a href=\"/wiki/spaces/AISDLC/pages/333/Other\">other</a> and <a href=\"/wiki/spaces/X/pages/6598099635/self\">self</a>.</p>"}}
            }
            """;

        using var doc = JsonDocument.Parse(json);
        var page = ConfluenceClient.Parse(doc.RootElement, "6598099635");

        Assert.Equal("6598099635", page.Id);
        Assert.Equal("AI SDLC Programme", page.Title);
        Assert.Equal("AISDLC", page.SpaceKey);
        Assert.Equal(new[] { "Home", "Programmes" }, page.Ancestors);
        Assert.Equal(3, page.VersionNumber);
        Assert.Equal("Michael Dimitriadis", page.VersionBy);
        Assert.Equal(2026, page.VersionWhen!.Value.Year);
        Assert.Equal(new[] { "111", "222" }, page.ChildPageIds);
        Assert.Contains("<h2>Description</h2>", page.BodyHtml);

        // In-body page links: the linked page (333); the self-reference (6598099635) is excluded.
        Assert.Equal(new[] { "333" }, page.LinkedPageIds);
    }

    [Fact]
    public void Render_uses_the_title_as_h1_and_keeps_body_headings()
    {
        var when = new DateTimeOffset(2026, 7, 1, 12, 18, 0, TimeSpan.Zero);
        var page = new ConfluencePage(
            Id: "6598099635",
            Title: "AI SDLC Programme",
            Type: "page",
            SpaceKey: "AISDLC",
            Ancestors: ["Home", "Programmes"],
            VersionNumber: 3,
            VersionWhen: when,
            VersionBy: "Michael Dimitriadis",
            BodyHtml: "<h2>Description</h2><p>The programme body.</p><h3>Scope</h3><p>details</p>",
            ChildPageIds: ["111"],
            LinkedPageIds: []);

        var rendered = new ConfluenceDocumentRenderer().Render(page, "https://x.atlassian.net", new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("6598099635", rendered.PageId);
        Assert.Equal("AI SDLC Programme", rendered.Title);
        Assert.Equal(when, rendered.ModifiedAt);

        var md = rendered.Markdown;
        Assert.StartsWith("# AI SDLC Programme", md);
        Assert.Contains("Space: AISDLC", md);
        Assert.Contains("Path: Home > Programmes", md);
        Assert.Contains("Version 3", md);
        // Body headings survive as h2/h3 → they nest under the page's h1 title.
        Assert.Contains("## Description", md);
        Assert.Contains("### Scope", md);
        Assert.Contains("The programme body.", md);
    }
}

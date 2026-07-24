using System.Text.Json;
using Rtfm.Core.Jira;

namespace Rtfm.Core.Tests.Jira;

/// <summary>
/// The Phase 25 pure-logic seams (§2.16): date parsing (Jira's colon-less zone
/// offset), the synthetic source key, base-URL normalization, JSON parsing
/// (comment id-join, link direction), and rendering (thread granularity +
/// heading escape). No network — <see cref="JiraClient.Parse"/> is fed a fixture
/// element, per the test suite's unit-only boundary.
/// </summary>
public class JiraTests
{
    [Theory]
    [InlineData("2026-07-15T12:33:20.256+0400", 2026, 7)]   // colon-less offset — the Jira Cloud shape
    [InlineData("2026-07-16T09:00:00.000+0000", 2026, 7)]
    [InlineData("2026-01-02T03:04:05.000Z", 2026, 1)]
    [InlineData("2026-03-04T05:06:07+02:00", 2026, 3)]
    public void JiraDate_parses_real_shapes(string input, int year, int month)
    {
        var parsed = JiraDate.Parse(input);
        Assert.NotNull(parsed);
        Assert.Equal(year, parsed!.Value.Year);
        Assert.Equal(month, parsed.Value.Month);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    public void JiraDate_returns_null_for_absent_or_garbage(string? input)
        => Assert.Null(JiraDate.Parse(input));

    [Fact]
    public void JiraDate_honours_the_colon_less_offset()
    {
        // +0400 must be read as +4h, not dropped to UTC.
        var parsed = JiraDate.Parse("2026-07-15T12:00:00.000+0400");
        Assert.Equal(TimeSpan.FromHours(4), parsed!.Value.Offset);
        Assert.Equal(8, parsed.Value.UtcDateTime.Hour);
    }

    [Theory]
    [InlineData("AEXP-123", "jira://AEXP-123")]
    [InlineData("aexp-123", "jira://AEXP-123")]   // case-insensitive → canonical upper
    [InlineData("  tod-1 ", "jira://TOD-1")]
    public void JiraSource_key_is_canonical(string key, string expected)
    {
        Assert.Equal(expected, JiraSource.Key(key));
        Assert.True(JiraSource.IsJiraKey(JiraSource.Key(key)));
    }

    [Fact]
    public void JiraSource_rejects_a_file_path()
        => Assert.False(JiraSource.IsJiraKey("d:/docs/a.md"));

    [Theory]
    [InlineData("internationalsos.atlassian.net", "https://internationalsos.atlassian.net")]
    [InlineData("https://x.atlassian.net/", "https://x.atlassian.net")]
    [InlineData("https://x.atlassian.net/jira/software/projects", "https://x.atlassian.net")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    public void NormalizeBaseUrl_reduces_to_scheme_and_host(string input, string expected)
        => Assert.Equal(expected, JiraConfig.NormalizeBaseUrl(input));

    [Fact]
    public void ResolveToken_expands_env_and_fails_loudly_when_unset()
    {
        var setVar = "RTFM_TEST_JIRA_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(setVar, "secret-token");
        try
        {
            var config = new JiraConfig("https://x.atlassian.net", "me@x.com", $"${{{setVar}}}");
            Assert.Equal("secret-token", config.ResolveToken());

            var missing = config with { Token = "${RTFM_TEST_JIRA_DEFINITELY_UNSET}" };
            Assert.Throws<InvalidDataException>(() => missing.ResolveToken());
        }
        finally
        {
            Environment.SetEnvironmentVariable(setVar, null);
        }
    }

    [Fact]
    public void Parse_maps_fields_joins_comment_bodies_by_id_and_reads_links()
    {
        const string json =
            """
            {
              "key": "TOD-3112",
              "fields": {
                "summary": "Provide View Definition permission",
                "status": {"name": "Awaiting Review"},
                "issuetype": {"name": "Task"},
                "reporter": {"displayName": "Alexandru Turcan"},
                "assignee": {"displayName": "Hari Kolluri"},
                "priority": {"name": "Medium"},
                "labels": ["Unicorn", "PMM"],
                "created": "2026-07-15T12:33:20.256+0400",
                "updated": "2026-07-16T23:34:42.305+0400",
                "description": {"type": "doc"},
                "comment": {"comments": [
                  {"id": "10", "author": {"displayName": "Ankita Arora"}, "created": "2026-07-15T13:58:00.000+0400", "body": {"type": "doc"}},
                  {"id": "20", "author": {"displayName": "Bob"}, "created": "2026-07-16T09:00:00.000+0000", "body": {"type": "doc"}}
                ]},
                "issuelinks": [
                  {"type": {"inward": "is blocked by", "outward": "blocks"}, "outwardIssue": {"key": "TOD-3200"}},
                  {"type": {"inward": "relates to", "outward": "relates to"}, "inwardIssue": {"key": "TOD-3000"}}
                ],
                "parent": {"key": "TOD-3000"}
              },
              "renderedFields": {
                "description": "<p>Please grant view definition.</p>",
                "comment": {"comments": [
                  {"id": "10", "body": "<p>Approved!</p>", "created": "2026/07/15 01:58"},
                  {"id": "20", "body": "<p>Done.</p>", "created": "2026/07/16 05:00"}
                ]}
              }
            }
            """;

        using var doc = JsonDocument.Parse(json);
        var issue = JiraClient.Parse(doc.RootElement, "TOD-3112");

        Assert.Equal("TOD-3112", issue.Key);
        Assert.Equal("Provide View Definition permission", issue.Summary);
        Assert.Equal("Awaiting Review", issue.Status);
        Assert.Equal("Task", issue.IssueType);
        Assert.Equal("Alexandru Turcan", issue.Reporter);
        Assert.Equal("Hari Kolluri", issue.Assignee);
        Assert.Equal(new[] { "Unicorn", "PMM" }, issue.Labels);
        Assert.Equal("<p>Please grant view definition.</p>", issue.DescriptionHtml);
        Assert.Equal("TOD-3000", issue.ParentKey);
        Assert.Equal(2026, issue.Updated!.Value.Year);

        // Rendered HTML bodies joined onto raw metadata by comment id.
        Assert.Equal(2, issue.Comments.Count);
        Assert.Equal("Ankita Arora", issue.Comments[0].Author);
        Assert.Equal("<p>Approved!</p>", issue.Comments[0].BodyHtml);
        Assert.Equal(2026, issue.Comments[0].Created!.Value.Year);
        Assert.Equal("<p>Done.</p>", issue.Comments[1].BodyHtml);

        // Link direction: outwardIssue → Outward true; inwardIssue → false.
        Assert.Equal(2, issue.Links.Count);
        Assert.Equal(("blocks", "TOD-3200", true), (issue.Links[0].Relationship, issue.Links[0].Key, issue.Links[0].Outward));
        Assert.Equal(("relates to", "TOD-3000", false), (issue.Links[1].Relationship, issue.Links[1].Key, issue.Links[1].Outward));
    }

    [Fact]
    public void Render_produces_thread_granular_markdown_and_escapes_comment_headings()
    {
        var updated = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        var issue = new JiraIssue(
            Key: "ABC-1",
            Summary: "Do the thing",
            Status: "Open",
            IssueType: "Task",
            Reporter: "Alice",
            Assignee: "Bob",
            Priority: "High",
            Labels: ["infra"],
            Created: new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero),
            Updated: updated,
            DescriptionHtml: "<p>The description body.</p>",
            Comments: [new JiraComment("Carol", new DateTimeOffset(2026, 7, 16, 8, 30, 0, TimeSpan.Zero), "<h1>Injected heading</h1><p>comment text</p>")],
            Links: [new JiraLink("blocks", "ABC-2", true)],
            ParentKey: "ABC-0",
            Subtasks: []);

        var rendered = new JiraDocumentRenderer().Render(issue, "https://x.atlassian.net", new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("ABC-1", rendered.Key);
        Assert.Equal("ABC-1: Do the thing", rendered.Title);
        Assert.Equal(updated, rendered.ModifiedAt);          // source_modified_at = the ticket's updated

        var md = rendered.Markdown;
        Assert.StartsWith("# ABC-1: Do the thing", md);
        Assert.Contains("## Description", md);
        Assert.Contains("The description body.", md);
        Assert.Contains("## Comment by Carol, 2026-07-16 08:30", md);
        Assert.Contains("## Linked issues", md);
        Assert.Contains("blocks ABC-2", md);

        // The comment's own <h1> must be escaped so it can't be read as a section.
        Assert.Contains(@"\# Injected heading", md);
        Assert.DoesNotContain("\n# Injected heading", md);
    }
}

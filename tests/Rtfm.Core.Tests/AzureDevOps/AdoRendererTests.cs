using Rtfm.Core.AzureDevOps;
using Rtfm.Core.Chunking;
using Rtfm.Core.Contradictions;

namespace Rtfm.Core.Tests.AzureDevOps;

public class AdoRendererTests
{
    private static readonly DateTimeOffset Pulled = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static AdoWorkItem Item(IReadOnlyList<AdoComment>? comments = null) => new(
        Id: 101,
        Rev: 3,
        Title: "Default role for new tenants",
        WorkItemType: "User Story",
        State: "Active",
        Reason: "Implementation started",
        AssignedTo: "Dana Dev",
        CreatedBy: "Pat Product",
        Created: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        Changed: new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
        Priority: "2",
        AreaPath: @"Team Alpha\Platform",
        IterationPath: @"Team Alpha\Sprint 4",
        Tags: ["auth"],
        TeamProject: "Team Alpha",
        CommentCount: 0,
        ParentId: 100,
        RichFields:
        [
            new AdoRichField("System.Description", "Description", "<h2>Context</h2><p>New tenants get <b>admin</b>.</p>", false),
            new AdoRichField("Microsoft.VSTS.Common.AcceptanceCriteria", "Acceptance criteria", "# not a heading\n- role is admin", true),
        ],
        Links: [new AdoLink("Parent", 100), new AdoLink("Related", 102)],
        Artifacts: [],
        Hyperlinks: [new AdoHyperlink("https://wiki.contoso.com/roles", "role matrix")],
        Comments: comments ?? [new AdoComment("Lee", new DateTimeOffset(2026, 10, 3, 14, 5, 0, TimeSpan.Zero), "<p>Decided: <b>super-admin</b>.</p>", false)],
        WebUrl: "https://dev.azure.com/Contoso/Team%20Alpha/_workitems/edit/101");

    private static AdoDevelopment Development() => new(
        PullRequests:
        [
            new AdoPullRequest(501, "Seed super-admin", "completed", false, "Dana", "feature/sso", "main", "Platform",
                "https://dev.azure.com/Contoso/Team%20Alpha/_git/Platform/pullrequest/501", Pulled, Pulled,
                "## Why\nadmin was too broad.", [new AdoReviewer("Lee", 10), new AdoReviewer("Sam", -5)]),
            new AdoPullRequest(502, "Docs", "active", true, null, null, null, null, null, null, null, null, []),
        ],
        Commits: [new AdoCommit("aaa1112223334444", "Dana", Pulled, "Seed roles\n\n# rationale line", "Platform", null)],
        Branches: [new AdoBranch("feature/sso", "Platform")],
        OmittedPullRequests: 3,
        OmittedCommits: 0,
        Unreadable: 1);

    [Fact]
    public void Renders_metadata_sections_prs_and_comments_as_separate_chunks()
    {
        var rendered = new AdoDocumentRenderer().Render(Item(), AdoFixtures.Org, Pulled, Development());
        var md = rendered.Markdown;

        Assert.Equal("User Story 101: Default role for new tenants", rendered.Title);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), rendered.ModifiedAt);
        Assert.Contains("> Type: User Story · State: Active (Implementation started) · Priority: 2 · Assigned to: Dana Dev", md);
        Assert.Contains(@"> Area: Team Alpha\Platform · Iteration: Team Alpha\Sprint 4", md);
        Assert.Contains("> Parent: #100", md);

        // Embedded headings are escaped — they must not become sections.
        Assert.Contains(@"\## Context", md);
        Assert.Contains(@"\# not a heading", md);
        Assert.Contains(@"\## Why", md);
        Assert.Contains(@"\# rationale line", md);

        Assert.Contains("- Reviewers: Lee (approved), Sam (waiting for author)", md);
        Assert.Contains("- Status: active, draft", md);
        Assert.Contains("_… 3 more linked pull request(s), not shown._", md);
        Assert.Contains("_1 linked development item(s) could not be read", md);

        var chunks = new MarkdownChunker().Chunk(md, new ChunkMetadata("ado://contoso/101", rendered.Title, rendered.ModifiedAt, "p"));
        var headings = chunks.Select(c => c.HeadingPath[(rendered.Title.Length)..]).ToList();

        Assert.Contains(" > Description", headings);
        Assert.Contains(" > Acceptance criteria", headings);
        Assert.Contains(" > Development > Pull requests > PR 501: Seed super-admin", headings);
        Assert.Contains(" > Development > Pull requests > PR 502: Docs", headings);
        Assert.Contains(" > Development > Commits", headings);
        Assert.Contains(" > Comment by Lee, 2026-10-03 14:05", headings);
        Assert.Contains(chunks, c => c.Text.Contains("super-admin") && c.HeadingPath.EndsWith("Comment by Lee, 2026-10-03 14:05", StringComparison.Ordinal));
    }

    [Fact]
    public void Record_chunks_are_exactly_the_lead_links_and_development_sections()
    {
        // The contradiction exemption (§2.18) depends on these section names;
        // render a real document and classify every chunk it produces.
        var rendered = new AdoDocumentRenderer().Render(Item(), AdoFixtures.Org, Pulled, Development());
        var chunks = new MarkdownChunker().Chunk(rendered.Markdown, new ChunkMetadata("ado://contoso/101", rendered.Title, rendered.ModifiedAt, "p"));

        var eligible = chunks
            .Where(c => !ContradictionDetector.IsRecordChunk(c.SourcePath, c.HeadingPath, c.DocumentTitle))
            .Select(c => c.HeadingPath[rendered.Title.Length..])
            .Distinct()
            .ToList();

        Assert.Equal([" > Description", " > Acceptance criteria", " > Comment by Lee, 2026-10-03 14:05"], eligible);
    }

    [Fact]
    public void Markdown_comments_pass_through_and_empty_ones_say_so()
    {
        var item = Item([new AdoComment("Lee", null, "**bold** call", true), new AdoComment("Sam", null, "", false)]);
        var md = new AdoDocumentRenderer().Render(item, AdoFixtures.Org, Pulled).Markdown;

        Assert.Contains("## Comment by Lee, unknown date\n\n**bold** call", md);
        Assert.Contains("## Comment by Sam, unknown date\n\n_(no text)_", md);
        Assert.DoesNotContain("## Development", md); // no development record passed
    }
}

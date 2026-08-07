using System.Net;
using System.Text.Json;
using Rtfm.Core.Jira;

namespace Rtfm.Core.Tests.Jira;

/// <summary>
/// The Development-panel seams: planning which dev-status detail calls are worth
/// making, folding the detail payloads into the model, and rendering the section.
/// Fixtures mirror the <em>shape</em> of a real <c>/rest/dev-status/</c> response
/// with synthetic content, per this suite's unit-only, no-real-corpus boundary.
/// </summary>
public class JiraDevelopmentTests
{
    private const string SummaryWithPullRequestAndCommits =
        """
        {
          "errors": [],
          "summary": {
            "pullrequest": {
              "overall": { "count": 1, "state": "OPEN", "dataType": "pullrequest" },
              "byInstanceType": { "bitbucket": { "count": 1, "name": "Bitbucket Cloud" } }
            },
            "branch": {
              "overall": { "count": 1, "dataType": "branch" },
              "byInstanceType": { "bitbucket": { "count": 1, "name": "Bitbucket Cloud" } }
            },
            "build": {
              "overall": { "count": 0, "dataType": "build" },
              "byInstanceType": {}
            },
            "repository": {
              "overall": { "count": 1, "dataType": "repository" },
              "byInstanceType": { "bitbucket": { "count": 1, "name": "Bitbucket Cloud" } }
            }
          }
        }
        """;

    [Fact]
    public void Plan_asks_only_for_categories_that_hold_something()
    {
        using var doc = JsonDocument.Parse(SummaryWithPullRequestAndCommits);

        var plan = JiraClient.PlanDevelopmentFetches(doc.RootElement);

        // Two calls, not three: `branch` folds into the pullrequest fetch (that
        // dataType was measured to return a byte-identical payload), and the
        // zero-count `build` category is not fetched at all.
        Assert.Equal(
            [("bitbucket", "pullrequest"), ("bitbucket", "repository")],
            plan.ToArray());
    }

    [Fact]
    public void Plan_is_empty_when_the_panel_is_empty()
    {
        const string empty =
            """
            {
              "summary": {
                "pullrequest": { "overall": { "count": 0 }, "byInstanceType": {} },
                "repository":  { "overall": { "count": 0 }, "byInstanceType": {} }
              }
            }
            """;

        using var doc = JsonDocument.Parse(empty);
        Assert.Empty(JiraClient.PlanDevelopmentFetches(doc.RootElement));
    }

    [Fact]
    public void Plan_survives_a_response_shape_it_does_not_recognize()
    {
        // The endpoint is undocumented; an unfamiliar payload must degrade to
        // "nothing to fetch", never throw.
        using var doc = JsonDocument.Parse("""{"somethingElse": true}""");
        Assert.Empty(JiraClient.PlanDevelopmentFetches(doc.RootElement));
    }

    private const string PullRequestDetail =
        """
        {
          "errors": [],
          "detail": [
            {
              "branches": [
                { "name": "feat/abc-1-widget", "url": "https://git.example/branch", "repository": { "name": "acme/widgets-api" } }
              ],
              "pullRequests": [
                {
                  "id": "63",
                  "name": "ABC-1: Add the widget endpoint",
                  "status": "OPEN",
                  "author": { "name": "Dev Eloper" },
                  "source": { "branch": "feat/abc-1-widget" },
                  "destination": { "branch": "main" },
                  "reviewers": [
                    { "name": "Dana Reviewer", "approved": true },
                    { "name": "Sam Pending", "approved": false }
                  ],
                  "url": "https://git.example/acme/widgets-api/pull-requests/63",
                  "lastUpdate": "2026-08-06T11:29:06.463+0000",
                  "repositoryName": "acme/widgets-api"
                }
              ],
              "repositories": []
            }
          ]
        }
        """;

    private const string RepositoryDetail =
        """
        {
          "errors": [],
          "detail": [
            {
              "branches": [],
              "pullRequests": [],
              "repositories": [
                {
                  "name": "acme/widgets-api",
                  "commits": [
                    {
                      "id": "2a81d20006b8054763bc2d40d32895af95c64d39",
                      "displayId": "2a81d20",
                      "authorTimestamp": "2026-08-06T08:52:50.000+0000",
                      "url": "https://git.example/commit/2a81d20",
                      "author": { "name": "dev@example.com" },
                      "merge": false,
                      "message": "ABC-1: add the widget endpoint\n\nBatch-loads counts for the whole page rather than per row."
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static JiraDevelopment Merge(params string[] payloads)
    {
        List<JiraPullRequest> prs = [];
        List<JiraBranch> branches = [];
        List<JiraCommit> commits = [];

        foreach (var payload in payloads)
        {
            using var doc = JsonDocument.Parse(payload);
            JiraClient.MergeDevelopmentDetail(doc.RootElement, prs, branches, commits);
        }

        return new JiraDevelopment(prs, branches, commits);
    }

    [Fact]
    public void Detail_reads_the_pull_request_with_its_branches_and_reviewers()
    {
        var development = Merge(PullRequestDetail);

        var pr = Assert.Single(development.PullRequests);
        Assert.Equal("ABC-1: Add the widget endpoint", pr.Name);
        Assert.Equal("OPEN", pr.Status);
        Assert.Equal("Dev Eloper", pr.Author);
        Assert.Equal("feat/abc-1-widget", pr.SourceBranch);
        Assert.Equal("main", pr.DestinationBranch);
        Assert.Equal("acme/widgets-api", pr.Repository);
        Assert.Equal("https://git.example/acme/widgets-api/pull-requests/63", pr.Url);
        Assert.Equal(2026, pr.LastUpdate!.Value.Year);

        Assert.Collection(
            pr.Reviewers,
            dana => Assert.Equal(("Dana Reviewer", true), (dana.Name, dana.Approved)),
            sam => Assert.Equal(("Sam Pending", false), (sam.Name, sam.Approved)));

        var branch = Assert.Single(development.Branches);
        Assert.Equal(("feat/abc-1-widget", "acme/widgets-api"), (branch.Name, branch.Repository));
    }

    [Fact]
    public void Detail_reads_commits_with_the_full_message()
    {
        var development = Merge(RepositoryDetail);

        var commit = Assert.Single(development.Commits);
        Assert.Equal("2a81d20", commit.DisplayId);
        Assert.Equal("dev@example.com", commit.Author);
        Assert.Equal(2026, commit.AuthoredAt!.Value.Year);
        Assert.False(commit.Merge);

        // The body, not just the subject — it is the reason this section exists.
        Assert.Contains("Batch-loads counts for the whole page", commit.Message);
    }

    [Fact]
    public void Detail_deduplicates_when_the_same_item_arrives_twice()
    {
        // The same PR/commit can surface under more than one instance type.
        var development = Merge(PullRequestDetail, PullRequestDetail, RepositoryDetail, RepositoryDetail);

        Assert.Single(development.PullRequests);
        Assert.Single(development.Branches);
        Assert.Single(development.Commits);
    }

    [Theory]
    [InlineData("AEXP-19", "AEXP")]
    [InlineData("cem-231", "CEM")]
    [InlineData("NOTAKEY", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Project_is_read_off_the_ticket_key(string? key, string? expected)
        => Assert.Equal(expected, JiraClient.ProjectOf(key));

    /// <summary>
    /// The regression that shipped in 1.10.0: <c>View Development Tools</c> is a
    /// <b>per-project</b> permission, but a 403 latched the whole feature off —
    /// so one cross-project link into an inaccessible project stripped the
    /// Development panel from every ticket crawled after it, while reporting the
    /// endpoint as globally unavailable.
    /// </summary>
    [Fact]
    public async Task A_forbidden_project_does_not_disable_the_others()
    {
        var handler = new PerProjectHandler { ForbiddenIssueId = "2000" };
        using var client = new JiraClient(new JiraConfig("https://x.atlassian.net", "me@x.com", "tok"), handler);

        var warnings = new List<string>();

        // The forbidden ticket comes first, exactly as a cross-project link does.
        var forbidden = await client.FetchDevelopmentAsync("2000", "CEM-231", warnings.Add);
        var allowed = await client.FetchDevelopmentAsync("1000", "AEXP-19", warnings.Add);

        Assert.True(forbidden.IsEmpty);
        Assert.Single(allowed.PullRequests);          // <- the bug: this was empty

        var warning = Assert.Single(warnings);
        Assert.Contains("project CEM", warning, StringComparison.Ordinal);
        Assert.Contains("other projects are unaffected", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_withdrawn_endpoint_still_latches_off_for_the_run()
    {
        // 404/401 really are global, so one warning and no further attempts.
        var handler = new PerProjectHandler { NotFoundAlways = true };
        using var client = new JiraClient(new JiraConfig("https://x.atlassian.net", "me@x.com", "tok"), handler);

        var warnings = new List<string>();
        await client.FetchDevelopmentAsync("1000", "AEXP-19", warnings.Add);
        await client.FetchDevelopmentAsync("1001", "AEXP-20", warnings.Add);

        Assert.Single(warnings);
        Assert.Equal(1, handler.Requests);   // latched: the second ticket never called out
    }

    /// <summary>Serves dev-status per issue id, 403ing one of them like a closed project.</summary>
    private sealed class PerProjectHandler : HttpMessageHandler
    {
        public string? ForbiddenIssueId { get; init; }

        public bool NotFoundAlways { get; init; }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);

            if (NotFoundAlways)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
            }

            if (query["issueId"] == ForbiddenIssueId)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"errorMessages":["You do not have permission to perform the requested action"]}"""),
                });
            }

            var body = request.RequestUri!.AbsolutePath.Contains("/summary", StringComparison.Ordinal)
                ? SummaryWithPullRequestAndCommits
                : PullRequestDetail;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static JiraIssue Issue() => new(
        Key: "ABC-1",
        Summary: "Add the widget endpoint",
        Id: "10001",
        Status: "In Progress",
        IssueType: "Story",
        Reporter: "Alice",
        Assignee: "Dev Eloper",
        Priority: "High",
        Labels: [],
        Created: new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero),
        Updated: new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero),
        DescriptionHtml: "<p>The description body.</p>",
        Comments: [],
        Links: [],
        ParentKey: null,
        Subtasks: []);

    private static string RenderWith(JiraDevelopment development) =>
        new JiraDocumentRenderer()
            .Render(Issue(), "https://x.atlassian.net", new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero), development)
            .Markdown;

    [Fact]
    public void Render_gives_pull_requests_and_commits_their_own_chunkable_sections()
    {
        var markdown = RenderWith(Merge(PullRequestDetail, RepositoryDetail));

        // '##' + '###' is what buys the breadcrumb `ABC-1: … > Development >
        // Pull requests`, so a PR question lands on the PR chunk.
        Assert.Contains("## Development", markdown);
        Assert.Contains("### Pull requests", markdown);
        Assert.Contains("### Branches", markdown);
        Assert.Contains("### Commits", markdown);

        Assert.Contains("**ABC-1: Add the widget endpoint** — OPEN", markdown);
        Assert.Contains("- Branch: `feat/abc-1-widget` → `main`", markdown);
        Assert.Contains("- Repository: acme/widgets-api", markdown);
        Assert.Contains("- Reviewers: Dana Reviewer (approved), Sam Pending", markdown);
        Assert.Contains("- URL: https://git.example/acme/widgets-api/pull-requests/63", markdown);

        Assert.Contains("**`2a81d20`**", markdown);
        Assert.Contains("Batch-loads counts for the whole page rather than per row.", markdown);

        // The section sits above the comment sections, and the description survives.
        Assert.Contains("## Description", markdown);
    }

    [Fact]
    public void Render_omits_the_section_entirely_when_there_is_no_development_data()
    {
        Assert.DoesNotContain("## Development", RenderWith(JiraDevelopment.None));

        var noArgument = new JiraDocumentRenderer()
            .Render(Issue(), "https://x.atlassian.net", DateTimeOffset.UnixEpoch)
            .Markdown;
        Assert.DoesNotContain("## Development", noArgument);
    }

    [Theory]
    [InlineData("# Subject line\n\nbody", "\\# Subject line")]
    [InlineData("### Deep\n\nbody", "\\### Deep")]
    public void Render_escapes_headings_inside_a_commit_message(string message, string expected)
    {
        // An unescaped '#' in a commit message would open a new section and
        // shatter the ticket's structure (the Phase 24 heading-escape lesson).
        Assert.Contains(expected, JiraDocumentRenderer.PlainBlock(message));
    }

    [Fact]
    public void PlainBlock_normalizes_line_endings_and_tolerates_nothing()
    {
        Assert.Equal("a\nb", JiraDocumentRenderer.PlainBlock("a\r\nb\r\n"));
        Assert.Equal(string.Empty, JiraDocumentRenderer.PlainBlock(null));
        Assert.Equal(string.Empty, JiraDocumentRenderer.PlainBlock("   "));
    }

    [Fact]
    public void Render_reports_the_commit_overflow_rather_than_dropping_it_silently()
    {
        var commits = Enumerable.Range(0, JiraDocumentRenderer.MaxCommitsRendered + 3)
            .Select(i => new JiraCommit($"sha{i}", $"sha{i}", "dev", null, $"commit {i}", null, "acme/widgets-api", false))
            .ToList();

        var markdown = RenderWith(new JiraDevelopment([], [], commits));

        Assert.Contains($"_… 3 more commit(s) on this ticket, not shown._", markdown);
        Assert.Contains("commit 0", markdown);
        Assert.DoesNotContain($"commit {JiraDocumentRenderer.MaxCommitsRendered + 2}", markdown);
    }
}

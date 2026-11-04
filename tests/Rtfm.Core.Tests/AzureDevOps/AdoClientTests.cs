using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Rtfm.Core.AzureDevOps;

namespace Rtfm.Core.Tests.AzureDevOps;

/// <summary>
/// <see cref="AdoClient"/> parsing and transport behaviour, offline. Payload
/// shapes mirror what the real Azure DevOps REST API returned during Phase 27
/// validation (field names, relation shapes, the mixed-case <c>%2f</c>/<c>%2F</c>
/// artifact escapes) — but every value here is synthetic.
/// </summary>
public class AdoClientTests
{
    [Fact]
    public void Parse_reads_fields_identities_relations_and_rich_fields()
    {
        var json = """
            {"id":101,"rev":12,
             "fields":{
               "System.Title":"Default role for new tenants",
               "System.WorkItemType":"User Story","System.State":"Active","System.Reason":"Implementation started",
               "System.AssignedTo":{"displayName":"Dana Dev","uniqueName":"dana@contoso.com"},
               "System.CreatedBy":"Pat Product <pat@contoso.com>",
               "System.CreatedDate":"2026-09-01T08:00:00Z","System.ChangedDate":"2026-10-02T09:30:00.5Z",
               "Microsoft.VSTS.Common.Priority":2,
               "System.AreaPath":"Team Alpha\\Platform","System.IterationPath":"Team Alpha\\Sprint 4",
               "System.Tags":"auth; tenancy ;",
               "System.TeamProject":"Team Alpha","System.CommentCount":3,"System.Parent":100,
               "System.Description":"<p>New tenants get <b>admin</b>.</p>",
               "Microsoft.VSTS.Common.AcceptanceCriteria":"- role is admin",
               "Custom.TechnicalNotes":"<div>Use the role seeder.</div>",
               "System.History":"<div>latest comment echo</div>"},
             "multilineFieldsFormat":{"System.Description":"html","Microsoft.VSTS.Common.AcceptanceCriteria":"markdown",
                                      "Custom.TechnicalNotes":"html","System.History":"html"},
             "relations":[
               {"rel":"System.LinkTypes.Hierarchy-Reverse","url":"https://dev.azure.com/Contoso/abc/_apis/wit/workItems/100","attributes":{"name":"Parent"}},
               {"rel":"System.LinkTypes.Dependency-Forward","url":"https://dev.azure.com/Contoso/abc/_apis/wit/workItems/102","attributes":{}},
               {"rel":"ArtifactLink","url":"vstfs:///Git/PullRequestId/p1%2fr1%2f501","attributes":{"name":"Pull Request","resourceCreatedDate":"2026-10-01T00:00:00Z"}},
               {"rel":"Hyperlink","url":"https://wiki.contoso.com/roles","attributes":{"comment":"role matrix"}},
               {"rel":"AttachedFile","url":"https://dev.azure.com/Contoso/_apis/wit/attachments/x","attributes":{"name":"spec.pdf"}}],
             "_links":{"html":{"href":"https://dev.azure.com/Contoso/abc/_workitems/edit/101"}}}
            """;
        using var doc = JsonDocument.Parse(json);

        var item = AdoClient.Parse(doc.RootElement, AdoFixtures.Org);

        Assert.Equal(101, item.Id);
        Assert.Equal(12, item.Rev);
        Assert.Equal("Default role for new tenants", item.Title);
        Assert.Equal("Dana Dev", item.AssignedTo);
        Assert.Equal("Pat Product", item.CreatedBy);   // legacy "Name <email>" identity
        Assert.Equal("2", item.Priority);              // numeric field → text
        Assert.Equal(["auth", "tenancy"], item.Tags);
        Assert.Equal(100, item.ParentId);
        Assert.Equal(3, item.CommentCount);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 9, 30, 0, 500, TimeSpan.Zero), item.Changed);
        Assert.Equal("https://dev.azure.com/Contoso/abc/_workitems/edit/101", item.WebUrl);

        // Known fields first, in display order, then custom ones; History never.
        Assert.Equal(["Description", "Acceptance criteria", "Technical notes"], item.RichFields.Select(f => f.Label));
        Assert.True(item.RichFields.Single(f => f.Label == "Acceptance criteria").IsMarkdown);
        Assert.False(item.RichFields.Single(f => f.Label == "Description").IsMarkdown);

        Assert.Equal([new AdoLink("Parent", 100), new AdoLink("Dependency forward", 102)], item.Links);
        Assert.Equal(AdoArtifactKind.PullRequest, Assert.Single(item.Artifacts).Kind);
        Assert.Equal(new AdoHyperlink("https://wiki.contoso.com/roles", "role matrix"), Assert.Single(item.Hyperlinks));
    }

    [Theory]
    [InlineData("vstfs:///Git/PullRequestId/p1%2fr1%2f812", AdoArtifactKind.PullRequest, "p1", "r1", "812")]
    [InlineData("vstfs:///Git/PullRequestId/p1%2Fr1%2F813", AdoArtifactKind.PullRequest, "p1", "r1", "813")]
    [InlineData("vstfs:///Git/Commit/p1%2fr1%2fABCDEF0123", AdoArtifactKind.Commit, "p1", "r1", "abcdef0123")]
    [InlineData("vstfs:///Git/Ref/p1%2Fr1%2FGBfeature%2Fsso", AdoArtifactKind.Branch, "p1", "r1", "feature/sso")]
    [InlineData("vstfs:///Git/Ref/p1%2Fr1%2FGBfeature%252Fsso", AdoArtifactKind.Branch, "p1", "r1", "feature/sso")]
    public void ParseArtifact_decodes_git_links_whatever_the_escape_case(string uri, AdoArtifactKind kind, string project, string repo, string value)
    {
        var artifact = AdoClient.ParseArtifact(uri);
        Assert.NotNull(artifact);
        Assert.Equal((kind, project, repo, value), (artifact.Kind, artifact.ProjectId, artifact.RepositoryId, artifact.Value));
    }

    [Theory]
    [InlineData("vstfs:///Git/Ref/p1%2Fr1%2FGTv1.0")]          // a tag, not a branch
    [InlineData("vstfs:///Build/Build/12345")]                  // a build
    [InlineData("vstfs:///Git/PullRequestId/p1%2Fr1%2Fnot-a-number")]
    [InlineData("vstfs:///Git/Commit/p1%2Fsha-without-repo")]
    public void ParseArtifact_ignores_what_it_cannot_resolve(string uri)
        => Assert.Null(AdoClient.ParseArtifact(uri));

    [Fact]
    public void PlanDevelopment_dedupes_caps_newest_first_and_counts_the_rest()
    {
        var day = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var artifacts = new List<AdoArtifactLink>();
        for (var i = 0; i < 25; i++)
        {
            artifacts.Add(new AdoArtifactLink(AdoArtifactKind.PullRequest, "p", "r", (1000 + i).ToString(), day.AddDays(i)));
        }

        artifacts.Add(new AdoArtifactLink(AdoArtifactKind.PullRequest, "p", "R", "1024", day)); // duplicate (repo id case differs)
        artifacts.Add(new AdoArtifactLink(AdoArtifactKind.Commit, "p", "r", "aaa", day));
        artifacts.Add(new AdoArtifactLink(AdoArtifactKind.Branch, "p", "r", "main", day));

        var plan = AdoClient.PlanDevelopment(artifacts);

        Assert.Equal(AdoClient.MaxPullRequestsFetched, plan.PullRequests.Count);
        Assert.Equal("1024", plan.PullRequests[0].Value);   // newest-linked first
        Assert.Equal(5, plan.OmittedPullRequests);          // 25 distinct − 20
        Assert.Single(plan.Commits);
        Assert.Equal(0, plan.OmittedCommits);
        Assert.Single(plan.Branches);
    }

    [Fact]
    public async Task Every_request_is_a_GET()
    {
        // The §2.16/§2.18 invariant: exercise each public read path and assert
        // that nothing but GET ever leaves the client.
        var handler = new AdoStubHandler()
            .OnJson(p => p.Contains("connectionData"), """{"authenticatedUser":{"providerDisplayName":"me@contoso.com"}}""")
            .OnJson(p => AdoFixtures.IsItem(p, 1), AdoFixtures.WorkItem(1, commentCount: 1,
                relationsExtra: """{"rel":"ArtifactLink","url":"vstfs:///Git/PullRequestId/p%2Fr%2F9","attributes":{}}"""))
            .OnJson(p => p.Contains("/comments"), """{"comments":[{"text":"hi","format":"html","createdBy":{"displayName":"A"},"createdDate":"2026-10-01T00:00:00Z"}]}""")
            .OnJson(p => p.Contains("/pullrequests/9"), """{"pullRequestId":9,"title":"t","repository":{"name":"repo","project":{"name":"Team Alpha"}}}""")
            .OnJson(p => p.Contains("/_apis/wit/workitems?ids="), """{"value":[{"id":1,"rev":7}]}""");
        using var client = AdoFixtures.Client(handler);

        await client.VerifyAuthAsync();
        var item = await client.FetchWorkItemAsync(1);
        await client.FetchDevelopmentAsync(item);
        await client.FetchRevisionsAsync([1]);

        Assert.True(handler.Requests.Count >= 5);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task Comments_are_fetched_only_when_asked_and_present()
    {
        var handler = new AdoStubHandler()
            .OnJson(p => AdoFixtures.IsItem(p, 1), AdoFixtures.WorkItem(1, commentCount: 2))
            .OnJson(p => AdoFixtures.IsItem(p, 2), AdoFixtures.WorkItem(2, commentCount: 0))
            .OnJson(p => p.Contains("/workItems/1/comments"), """
                {"comments":[
                  {"text":"<p>first</p>","format":"html","createdBy":{"displayName":"A"},"createdDate":"2026-10-01T00:00:00Z"},
                  {"text":"gone","isDeleted":true,"createdBy":{"displayName":"B"}},
                  {"text":"**second**","format":"markdown","createdBy":{"displayName":"C"},"createdDate":"2026-10-02T00:00:00Z"}]}
                """);
        using var client = AdoFixtures.Client(handler);

        var withComments = await client.FetchWorkItemAsync(1, includeComments: true);
        Assert.Equal(["A", "C"], withComments.Comments.Select(c => c.Author)); // deleted comment dropped
        Assert.True(withComments.Comments[1].IsMarkdown);
        Assert.Contains(handler.Paths, p => p.StartsWith("/Contoso/Team Alpha/_apis/wit/workItems/1/comments", StringComparison.Ordinal)); // project-scoped route

        var descriptionOnly = await client.FetchWorkItemAsync(1, includeComments: false);
        Assert.Empty(descriptionOnly.Comments);

        await client.FetchWorkItemAsync(2, includeComments: true);           // CommentCount 0 → no call
        Assert.Single(handler.Paths, p => p.Contains("/comments"));
    }

    [Fact]
    public async Task A_401_on_a_refreshable_credential_is_retried_once_with_a_fresh_token()
    {
        var handler = new AdoStubHandler()
            .On(p => AdoFixtures.IsItem(p, 1), request => request.Headers.Authorization?.Parameter == "fresh"
                ? AdoStubHandler.Json(AdoFixtures.WorkItem(1))
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var credential = new CountingCredential(canRefresh: true);
        using var client = AdoFixtures.Client(handler, credential);

        var item = await client.FetchWorkItemAsync(1, includeComments: false);

        Assert.Equal(1, item.Id);
        Assert.Equal(1, credential.Refreshes);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_static_pat_is_not_retried_and_203_sign_in_pages_count_as_auth_failure()
    {
        // Azure DevOps answers a bad PAT with 203 + an HTML sign-in page.
        var handler = new AdoStubHandler()
            .On(p => AdoFixtures.IsItem(p, 1), _ => new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation)
            {
                Content = new StringContent("<html>Sign in</html>", System.Text.Encoding.UTF8, "text/html"),
            });
        using var client = AdoFixtures.Client(handler, new CountingCredential(canRefresh: false));

        var ex = await Assert.ThrowsAsync<AdoException>(() => client.FetchWorkItemAsync(1));
        Assert.Contains("not authorized", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_429_waits_for_Retry_After_then_succeeds()
    {
        var calls = 0;
        var handler = new AdoStubHandler()
            .On(p => AdoFixtures.IsItem(p, 1), _ =>
            {
                if (++calls == 1)
                {
                    var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                    return throttled;
                }

                return AdoStubHandler.Json(AdoFixtures.WorkItem(1));
            });
        var delays = new List<TimeSpan>();
        using var client = AdoFixtures.Client(handler, delays: delays);

        var item = await client.FetchWorkItemAsync(1, includeComments: false);

        Assert.Equal(1, item.Id);
        Assert.Equal([TimeSpan.FromSeconds(7)], delays);
    }

    [Theory]
    [InlineData(0, 1)]      // clamped up
    [InlineData(7, 7)]
    [InlineData(600, 60)]   // clamped down
    public void RetryDelay_is_clamped(int seconds, int expected)
        => Assert.Equal(TimeSpan.FromSeconds(expected), AdoClient.RetryDelay(new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)), DateTimeOffset.UtcNow));

    [Fact]
    public async Task Missing_work_item_is_a_clean_AdoException()
    {
        using var client = AdoFixtures.Client(new AdoStubHandler());
        var ex = await Assert.ThrowsAsync<AdoException>(() => client.FetchWorkItemAsync(404404));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task FetchRevisions_batches_by_200_and_tolerates_omitted_ids()
    {
        var handler = new AdoStubHandler()
            .On(p => p.Contains("/_apis/wit/workitems?ids="), request =>
            {
                var query = Uri.UnescapeDataString(request.RequestUri!.Query);
                Assert.Contains("errorPolicy=omit", query);
                var ids = query.Split("ids=")[1].Split('&')[0].Split(',').Select(int.Parse).ToList();
                Assert.True(ids.Count <= 200);

                // Deleted ids come back missing (or null) — never as a failed batch.
                var value = string.Join(",", ids.Where(i => i % 2 == 0).Select(i => $$"""{"id":{{i}},"rev":{{i + 1}}}""").Append("null"));
                return AdoStubHandler.Json($$"""{"value":[{{value}}]}""");
            });
        using var client = AdoFixtures.Client(handler);

        var revisions = await client.FetchRevisionsAsync(Enumerable.Range(1, 450).ToList());

        Assert.Equal(3, handler.Requests.Count);    // 200 + 200 + 50
        Assert.Equal(225, revisions.Count);         // only the even (surviving) ids
        Assert.Equal(11, revisions[10]);
    }

    [Fact]
    public async Task Development_resolves_prs_and_batched_commits_and_refetches_truncated_messages()
    {
        var relations = string.Join(",",
            """{"rel":"ArtifactLink","url":"vstfs:///Git/PullRequestId/p1%2Fr1%2F501","attributes":{"resourceCreatedDate":"2026-10-03T00:00:00Z"}}""",
            """{"rel":"ArtifactLink","url":"vstfs:///Git/Commit/p1%2Fr1%2Faaa111","attributes":{"resourceCreatedDate":"2026-10-02T00:00:00Z"}}""",
            """{"rel":"ArtifactLink","url":"vstfs:///Git/Commit/p1%2Fr1%2Fbbb222","attributes":{"resourceCreatedDate":"2026-10-01T00:00:00Z"}}""",
            """{"rel":"ArtifactLink","url":"vstfs:///Git/Ref/p1%2Fr1%2FGBfeature%2Fsso","attributes":{}}""");
        var handler = new AdoStubHandler()
            .OnJson(p => AdoFixtures.IsItem(p, 1), AdoFixtures.WorkItem(1, relationsExtra: relations))
            .OnJson(p => p.Contains("/p1/_apis/git/pullrequests/501"), """
                {"pullRequestId":501,"title":"Seed super-admin","status":"completed","sourceRefName":"refs/heads/feature/sso",
                 "targetRefName":"refs/heads/main","createdBy":{"displayName":"Dana"},"description":"Because admin was too broad.",
                 "repository":{"name":"Platform","project":{"name":"Team Alpha"}},"reviewers":[{"displayName":"Lee","vote":10}]}
                """)
            .OnJson(p => p.Contains("/r1/commits?") && p.Contains("searchCriteria.ids=aaa111") && p.Contains("searchCriteria.ids=bbb222"), """
                {"value":[
                  {"commitId":"aaa111","comment":"short message","author":{"name":"Dana","date":"2026-10-02T00:00:00Z"},
                   "remoteUrl":"https://dev.azure.com/Contoso/Team%20Alpha/_git/Platform/commit/aaa111"},
                  {"commitId":"bbb222","comment":"long message tru","commentTruncated":true,"author":{"name":"Dana","date":"2026-10-01T00:00:00Z"}}]}
                """)
            .OnJson(p => p.Contains("/r1/commits/bbb222"), """{"commitId":"bbb222","comment":"long message truncated no more","author":{"name":"Dana"}}""");
        using var client = AdoFixtures.Client(handler);

        var item = await client.FetchWorkItemAsync(1, includeComments: false);
        var development = await client.FetchDevelopmentAsync(item);

        var pr = Assert.Single(development.PullRequests);
        Assert.Equal(("Seed super-admin", "feature/sso", "main", "Because admin was too broad."), (pr.Title, pr.SourceBranch, pr.TargetBranch, pr.Description));
        Assert.Equal("https://dev.azure.com/Contoso/Team%20Alpha/_git/Platform/pullrequest/501", pr.Url);
        Assert.Equal(["short message", "long message truncated no more"], development.Commits.Select(c => c.Message));
        Assert.Equal("Platform", development.Commits[0].Repository);
        Assert.Equal(new AdoBranch("feature/sso", "Platform"), Assert.Single(development.Branches)); // repo name borrowed from the PR
        Assert.Equal(0, development.Unreadable);
        Assert.Single(handler.Paths, p => p.Contains("/commits?"));   // one batched call for the repository
    }

    [Fact]
    public async Task A_forbidden_project_latches_off_with_one_warning_and_spares_other_projects()
    {
        // Code: Read is per project — the Jira 1.11.1 lesson.
        var relations = string.Join(",",
            """{"rel":"ArtifactLink","url":"vstfs:///Git/PullRequestId/closed%2Fr%2F1","attributes":{}}""",
            """{"rel":"ArtifactLink","url":"vstfs:///Git/PullRequestId/closed%2Fr%2F2","attributes":{}}""",
            """{"rel":"ArtifactLink","url":"vstfs:///Git/PullRequestId/open%2Fr%2F3","attributes":{}}""");
        var handler = new AdoStubHandler()
            .OnJson(p => AdoFixtures.IsItem(p, 1), AdoFixtures.WorkItem(1, relationsExtra: relations))
            .On(p => p.Contains("/closed/_apis/git/"), _ => new HttpResponseMessage(HttpStatusCode.Forbidden))
            .OnJson(p => p.Contains("/open/_apis/git/pullrequests/3"), """{"pullRequestId":3,"title":"visible"}""");
        using var client = AdoFixtures.Client(handler);
        var warnings = new List<string>();

        var item = await client.FetchWorkItemAsync(1, includeComments: false);
        var first = await client.FetchDevelopmentAsync(item, warnings.Add);
        var second = await client.FetchDevelopmentAsync(item, warnings.Add);

        Assert.Equal("visible", Assert.Single(first.PullRequests).Title);
        Assert.Equal(2, first.Unreadable);
        Assert.Single(warnings);                                   // one warning, not one per PR / per item
        Assert.Contains("Team Alpha", warnings[0]);
        Assert.Equal("visible", Assert.Single(second.PullRequests).Title);
        Assert.True(handler.Paths.Count(p => p.Contains("/closed/")) <= 2); // latched: the second item spends nothing there
    }

    [Fact]
    public async Task A_transient_failure_costs_one_artifact_not_the_section()
    {
        var relations = string.Join(",",
            """{"rel":"ArtifactLink","url":"vstfs:///Git/PullRequestId/p%2Fr%2F1","attributes":{}}""",
            """{"rel":"ArtifactLink","url":"vstfs:///Git/PullRequestId/p%2Fr%2F2","attributes":{}}""");
        var handler = new AdoStubHandler()
            .OnJson(p => AdoFixtures.IsItem(p, 1), AdoFixtures.WorkItem(1, relationsExtra: relations))
            .On(p => p.Contains("/pullrequests/1"), _ => throw new HttpRequestException("boom"))
            .OnJson(p => p.Contains("/pullrequests/2"), """{"pullRequestId":2,"title":"fine"}""");
        using var client = AdoFixtures.Client(handler);
        var warnings = new List<string>();

        var item = await client.FetchWorkItemAsync(1, includeComments: false);
        var development = await client.FetchDevelopmentAsync(item, warnings.Add);

        Assert.Equal("fine", Assert.Single(development.PullRequests).Title);
        Assert.Equal(1, development.Unreadable);
        Assert.Contains("boom", Assert.Single(warnings));
    }

    [Theory]
    [InlineData("System.LinkTypes.Dependency-Forward", "Dependency forward")]
    [InlineData("Microsoft.VSTS.Common.TestedBy-Forward", "Testedby forward")]
    public void HumanizeRelation_is_a_readable_fallback(string rel, string expected)
        => Assert.Equal(expected, AdoClient.HumanizeRelation(rel));

    [Theory]
    [InlineData("Custom.TechnicalNotes", "Technical notes")]
    [InlineData("Contoso.Risk_Assessment", "Risk assessment")]
    public void HumanizeField_turns_reference_names_into_labels(string field, string expected)
        => Assert.Equal(expected, AdoClient.HumanizeField(field));
}

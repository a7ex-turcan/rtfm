using System.Net;
using Rtfm.Core.Jira;

namespace Rtfm.Core.Tests.Jira;

/// <summary>
/// Regression cover for the project-key lookup that always came back empty.
/// <c>/rest/api/3/project/search</c> treats <c>keys</c> as a <b>filter by
/// project key</b>, not a "return only keys" projection, so the old
/// <c>&amp;keys=true</c> filtered the search to a project literally keyed
/// <c>true</c> and matched nothing. Nothing errored: an empty key set makes
/// mention-following a silent no-op, so <c>--follow-mentions</c> quietly
/// followed nothing at all. These tests pin both halves — the request shape and
/// the crawler behaviour that depends on it.
/// </summary>
public class JiraProjectKeysTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);

    private static JiraClient NewClient(HttpMessageHandler handler)
        => new(new JiraConfig("https://x.atlassian.net", "me@x.com", "tok"), handler);

    [Fact]
    public async Task Project_keys_come_back_and_carry_no_keys_filter()
    {
        var handler = new ProjectHandler();
        using var client = NewClient(handler);

        var keys = await client.FetchProjectKeysAsync();

        // Two pages, both collected via startAt/isLast (this endpoint does
        // honour the offset, unlike Confluence's content search).
        Assert.Equal(["AEXP", "PAM", "TOD", "ZZ"], keys.OrderBy(k => k, StringComparer.Ordinal));

        // The bug in one assertion.
        Assert.DoesNotContain(handler.Requests, r => r.Contains("keys=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Follow_mentions_follows_a_real_project_key_and_ignores_a_lookalike()
    {
        using var client = NewClient(new ProjectHandler());
        var crawler = new JiraCrawler(client, new JiraDocumentRenderer());

        var result = await crawler.CrawlAsync(
            "AEXP-1", "https://x.atlassian.net", When,
            new JiraCrawlOptions(MaxDepth: 1, MaxTickets: 20, FollowMentions: true));

        // The seed's text mentions ZZ-9 (a real project) and SHA-256 (not one).
        // Before the fix the key set was empty, so *neither* was followed and
        // this crawl returned the seed alone.
        Assert.Equal(["AEXP-1", "ZZ-9"], result.Nodes.Select(n => n.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Mentions_stay_unfollowed_when_the_flag_is_off()
    {
        using var client = NewClient(new ProjectHandler());
        var crawler = new JiraCrawler(client, new JiraDocumentRenderer());

        var result = await crawler.CrawlAsync(
            "AEXP-1", "https://x.atlassian.net", When,
            new JiraCrawlOptions(MaxDepth: 1, MaxTickets: 20, FollowMentions: false));

        Assert.Equal(["AEXP-1"], result.Nodes.Select(n => n.Key));
    }

    private sealed class ProjectHandler : HttpMessageHandler
    {
        // Paged deliberately, so the startAt/isLast walk is exercised too.
        private static readonly string[][] ProjectPages = [["AEXP", "PAM"], ["TOD", "ZZ"]];

        private static readonly HashSet<string> Existing = ["AEXP-1", "ZZ-9"];

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            Requests.Add(uri.ToString());
            string body;

            if (path.Contains("/project/search", StringComparison.Ordinal))
            {
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var index = query["startAt"] == "100" ? 1 : 0;

                // Faithful to the real endpoint: `keys` FILTERS by project key.
                // So `keys=true` matches the project named "true" — i.e. nothing
                // — which is the whole bug. Without this the crawler test below
                // would pass against the broken code.
                var filter = query["keys"]?.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var page = filter is null
                    ? ProjectPages[index]
                    : ProjectPages[index].Where(k => filter.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray();

                var values = string.Join(",", page.Select(k => $"{{\"key\":\"{k}\"}}"));
                body = $"{{\"values\":[{values}],\"isLast\":{(index == ProjectPages.Length - 1).ToString().ToLowerInvariant()}}}";
            }
            else if (path.Contains("/search/jql", StringComparison.Ordinal))
            {
                body = "{\"issues\":[],\"isLast\":true}";
            }
            else if (path.Contains("/dev-status/", StringComparison.Ordinal))
            {
                // An empty Development panel — one summary call, no detail calls.
                body = "{\"summary\":{\"pullrequest\":{\"overall\":{\"count\":0},\"byInstanceType\":{}}}}";
            }
            else if (path.Contains("/issue/", StringComparison.Ordinal))
            {
                var key = path[(path.IndexOf("/issue/", StringComparison.Ordinal) + "/issue/".Length)..];
                if (!Existing.Contains(key))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
                }

                // Seed text mentions a real key and a lookalike that must not be chased.
                var description = key == "AEXP-1"
                    ? "<p>See ZZ-9 for the hashing work. Uses SHA-256 and UTF-8.</p>"
                    : "<p>body</p>";

                body =
                    "{\"key\":\"" + key + "\",\"id\":\"1000\",\"fields\":{\"summary\":\"" + key
                    + " summary\",\"status\":{\"name\":\"Open\"},\"issuetype\":{\"name\":\"Task\"},"
                    + "\"labels\":[],\"issuelinks\":[],\"subtasks\":[]},"
                    + "\"renderedFields\":{\"description\":\"" + description + "\"}}";
            }
            else
            {
                body = "{\"displayName\":\"Test\"}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}

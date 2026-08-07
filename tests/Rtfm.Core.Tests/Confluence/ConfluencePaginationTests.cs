using System.Net;
using System.Web;
using Rtfm.Core.Confluence;

namespace Rtfm.Core.Tests.Confluence;

/// <summary>
/// Regression cover for the scope-enumeration bug: Confluence Cloud's
/// <c>/rest/api/content/search</c> <b>ignores <c>start</c></b>, so offset paging
/// returned the same first 100 results forever — every scope larger than a page
/// was silently truncated, and re-indexing could not recover the remainder. The
/// stub below reproduces that server behaviour exactly (a <c>start</c>-only
/// request always yields page one) so a return to offset paging fails here
/// rather than in a user's corpus.
/// </summary>
public class ConfluencePaginationTests
{
    private static ConfluenceClient NewClient(HttpMessageHandler handler)
        => new(new ConfluenceConfig("https://x.atlassian.net", "me@x.com", "tok"), handler);

    [Fact]
    public async Task Search_follows_the_cursor_across_every_page()
    {
        var handler = new CursorHandler();
        using var client = NewClient(handler);

        var pages = await client.SearchPagesAsync("space = \"PH\" AND type = page", max: 1000);

        // Three server pages of three, two, and one — all of it, in order.
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7"], pages.Select(p => p.Id));
        Assert.Equal("Page 4", pages[3].Title);
        Assert.Equal(3, handler.Requests.Count);

        // The bug in one assertion: the walk must be driven by the cursor, and
        // must never lean on `start` (which this server ignores, as the real one does).
        Assert.Contains("cursor=c1", handler.Requests[1], StringComparison.Ordinal);
        Assert.Contains("cursor=c2", handler.Requests[2], StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Contains("start=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_stops_at_max_without_over_reading()
    {
        var handler = new CursorHandler();
        using var client = NewClient(handler);

        var pages = await client.SearchPagesAsync("space = \"PH\"", max: 4);

        Assert.Equal(["1", "2", "3", "4"], pages.Select(p => p.Id));
        Assert.Equal(2, handler.Requests.Count);   // two pages cover four items
    }

    [Fact]
    public async Task Search_resolves_a_next_link_that_carries_no_base()
    {
        var handler = new CursorHandler { OmitBase = true };
        using var client = NewClient(handler);

        var pages = await client.SearchPagesAsync("space = \"PH\"", max: 1000);

        // `/wiki/rest/...` with no `_links.base` must still land on /wiki/rest/...,
        // not at the host root (which would 404 the rest of the scope away).
        Assert.Equal(7, pages.Count);
        Assert.All(handler.Requests, r => Assert.Contains("/wiki/rest/api/content/search", r, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failure_midway_yields_a_prefix_and_says_so()
    {
        var handler = new CursorHandler { FailFromRequest = 2 };
        using var client = NewClient(handler);

        var warnings = new List<string>();
        var pages = await client.SearchPagesAsync("space = \"PH\"", max: 1000, warn: warnings.Add);

        // Partial enumeration must never pass as a complete scope (§5).
        Assert.Equal(["1", "2", "3"], pages.Select(p => p.Id));
        var warning = Assert.Single(warnings);
        Assert.Contains("incomplete", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_single_page_response_ends_the_walk()
    {
        var handler = new CursorHandler { SinglePage = true };
        using var client = NewClient(handler);

        var pages = await client.SearchPagesAsync("space = \"PH\"", max: 1000);

        Assert.Equal(["1", "2", "3"], pages.Select(p => p.Id));
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// Stands in for Confluence Cloud's content search: pages are addressed by an
    /// opaque <c>cursor</c>, and <c>start</c> is <b>ignored</b> — a request
    /// without a cursor always returns page one, however large its <c>start</c>.
    /// </summary>
    private sealed class CursorHandler : HttpMessageHandler
    {
        private static readonly string[][] PagesOfIds = [["1", "2", "3"], ["4", "5", "6"], ["7"]];

        public List<string> Requests { get; } = [];

        /// <summary>Drop <c>_links.base</c>, leaving only the relative <c>next</c>.</summary>
        public bool OmitBase { get; init; }

        /// <summary>Return only the first page, with no <c>next</c> link.</summary>
        public bool SinglePage { get; init; }

        /// <summary>1-based request ordinal that starts failing with HTTP 500.</summary>
        public int FailFromRequest { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.ToString());

            if (FailFromRequest > 0 && Requests.Count >= FailFromRequest)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") });
            }

            // The bug being pinned: `start` buys nothing. Only the cursor moves.
            var cursor = HttpUtility.ParseQueryString(uri.Query)["cursor"];
            var index = cursor switch { "c1" => 1, "c2" => 2, _ => 0 };

            var results = string.Join(",", PagesOfIds[index].Select(id => $"{{\"id\":\"{id}\",\"title\":\"Page {id}\"}}"));
            var hasNext = !SinglePage && index < PagesOfIds.Length - 1;
            var links = hasNext
                ? (OmitBase ? string.Empty : "\"base\":\"https://x.atlassian.net/wiki\",")
                    + $"\"next\":\"/{(OmitBase ? "wiki/" : string.Empty)}rest/api/content/search?cursor=c{index + 1}&limit=100\""
                : "\"self\":\"https://x.atlassian.net/wiki/rest/api/content/search\"";

            var body = $"{{\"results\":[{results}],\"_links\":{{{links}}}}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}

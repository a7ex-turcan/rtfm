using System.Net;
using System.Text.RegularExpressions;
using Rtfm.Core.Confluence;

namespace Rtfm.Core.Tests.Confluence;

/// <summary>
/// The Phase 26 step-3 watch machinery (§2.17): version-based change detection
/// (<see cref="ConfluenceMonitor.SelectChanged"/>), the per-project registry
/// round-trip, and the batched version probe
/// (<see cref="ConfluenceClient.FetchVersionsAsync"/>, driven offline).
/// </summary>
public class ConfluenceMonitorTests
{
    [Fact]
    public void SelectChanged_flags_higher_version_only()
    {
        var monitor = new ConfluenceMonitor();
        monitor.Set(new MonitoredPage("A", 1));   // will be v2 live → changed
        monitor.Set(new MonitoredPage("B", 3));   // same → unchanged
        monitor.Set(new MonitoredPage("C", 1));   // missing from poll → left alone
        monitor.Set(new MonitoredPage("D", 5));   // live lower (shouldn't happen) → not changed

        var current = new Dictionary<string, int?>(StringComparer.Ordinal)
        {
            ["A"] = 2,
            ["B"] = 3,
            ["D"] = 4,
            // C absent
        };

        Assert.Equal(new[] { "A" }, monitor.SelectChanged(current));
    }

    [Fact]
    public void Store_round_trips_and_removes()
    {
        var project = "test-conf-monitor-" + Guid.NewGuid().ToString("N");
        try
        {
            var monitor = new ConfluenceMonitor { LastPolledAt = JiraLikeStamp() };
            monitor.Set(new MonitoredPage("100", 4));
            monitor.Set(new MonitoredPage("200", 1));
            ConfluenceMonitorStore.Save(project, monitor);

            var loaded = ConfluenceMonitorStore.Load(project);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(4, loaded.Pages["100"].LastVersion);
            Assert.Equal(1, loaded.Pages["200"].LastVersion);
            Assert.NotNull(loaded.LastPolledAt);

            Assert.True(ConfluenceMonitorStore.Remove(project));
            Assert.Equal(0, ConfluenceMonitorStore.Load(project).Count);
        }
        finally
        {
            ConfluenceMonitorStore.Remove(project);
        }
    }

    [Fact]
    public async Task FetchVersionsAsync_returns_numbers_and_omits_missing()
    {
        using var client = new ConfluenceClient(new ConfluenceConfig("https://x.atlassian.net", "me@x.com", "tok"), new VersionHandler());

        var versions = await client.FetchVersionsAsync(["100", "200", "gone"]);

        Assert.Equal(2, versions.Count);
        Assert.Equal(4, versions["100"]);
        Assert.Equal(1, versions["200"]);
        Assert.False(versions.ContainsKey("gone"));
    }

    private static DateTimeOffset JiraLikeStamp() => new(2026, 7, 24, 10, 0, 0, TimeSpan.Zero);

    private sealed class VersionHandler : HttpMessageHandler
    {
        private static readonly Dictionary<string, int> Versions = new(StringComparer.Ordinal)
        {
            ["100"] = 4,
            ["200"] = 1,
            // "gone" deliberately absent
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var cql = Uri.UnescapeDataString(request.RequestUri!.Query);
            var ids = Regex.Match(cql, @"id in \((?<ids>[^)]*)\)").Groups["ids"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var results = ids.Where(Versions.ContainsKey)
                .Select(id => $"{{\"id\":\"{id}\",\"version\":{{\"number\":{Versions[id]}}}}}");
            var body = $"{{\"results\":[{string.Join(",", results)}]}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}

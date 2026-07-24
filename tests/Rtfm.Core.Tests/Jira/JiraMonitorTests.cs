using System.Net;
using System.Text.RegularExpressions;
using Rtfm.Core.Jira;

namespace Rtfm.Core.Tests.Jira;

/// <summary>
/// The Phase 25 step-3 watch machinery (§2.16): change detection
/// (<see cref="JiraMonitor.SelectChanged"/>), the per-project registry
/// round-trip, and the batched <c>updated</c>-stamp probe
/// (<see cref="JiraClient.FetchUpdatedAsync"/>, driven offline).
/// </summary>
public class JiraMonitorTests
{
    private static DateTimeOffset At(string iso) => JiraDate.Parse(iso)!.Value;

    [Fact]
    public void SelectChanged_flags_newer_and_never_stamped_but_not_same_or_missing()
    {
        var monitor = new JiraMonitor();
        monitor.Set(new MonitoredTicket("A", At("2026-07-01T00:00:00Z"), true));  // will be newer
        monitor.Set(new MonitoredTicket("B", At("2026-07-01T00:00:00Z"), false)); // unchanged
        monitor.Set(new MonitoredTicket("C", null, true));                        // never stamped → always changed
        monitor.Set(new MonitoredTicket("E", At("2026-07-01T00:00:00Z"), true));  // missing from the poll

        var current = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = At("2026-07-02T09:00:00Z"),   // newer
            ["B"] = At("2026-07-01T00:00:00Z"),   // same
            ["C"] = At("2026-07-01T00:00:00Z"),   // any value; stored was null
            // E absent — deleted / not visible
        };

        Assert.Equal(new[] { "A", "C" }, monitor.SelectChanged(current).OrderBy(k => k));
    }

    [Fact]
    public void Store_round_trips_and_removes()
    {
        var project = "test-jira-monitor-" + Guid.NewGuid().ToString("N");
        try
        {
            var monitor = new JiraMonitor { LastPolledAt = At("2026-07-24T10:00:00Z") };
            monitor.Set(new MonitoredTicket("AEXP-1", At("2026-07-01T00:00:00Z"), Full: true));
            monitor.Set(new MonitoredTicket("aexp-2", At("2026-07-02T00:00:00Z"), Full: false)); // lower-case in
            JiraMonitorStore.Save(project, monitor);

            var loaded = JiraMonitorStore.Load(project);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(At("2026-07-24T10:00:00Z"), loaded.LastPolledAt);
            Assert.True(loaded.Tickets.ContainsKey("AEXP-1"));
            Assert.True(loaded.Tickets.ContainsKey("AEXP-2"));   // canonicalized to upper on Set
            Assert.True(loaded.Tickets["AEXP-1"].Full);
            Assert.False(loaded.Tickets["AEXP-2"].Full);

            Assert.True(JiraMonitorStore.Remove(project));
            Assert.Equal(0, JiraMonitorStore.Load(project).Count);
        }
        finally
        {
            JiraMonitorStore.Remove(project);
        }
    }

    [Fact]
    public async Task FetchUpdatedAsync_returns_stamps_and_omits_missing_keys()
    {
        using var client = new JiraClient(new JiraConfig("https://x.atlassian.net", "me@x.com", "tok"), new StampHandler());

        var stamps = await client.FetchUpdatedAsync(["A", "B", "GONE"]);

        Assert.Equal(2, stamps.Count);                          // GONE not returned by the server
        Assert.Equal(2026, stamps["A"]!.Value.Year);
        Assert.Equal(7, stamps["B"]!.Value.Month);
        Assert.False(stamps.ContainsKey("GONE"));
    }

    /// <summary>Answers <c>key in (…)</c> searches with a fixed updated stamp per known key.</summary>
    private sealed class StampHandler : HttpMessageHandler
    {
        private static readonly Dictionary<string, string> Updated = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = "2026-07-10T12:00:00.000+0000",
            ["B"] = "2026-07-11T12:00:00.000+0000",
            // "GONE" deliberately absent — simulates a deleted/invisible ticket.
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var jql = Uri.UnescapeDataString(request.RequestUri!.Query);
            var keys = Regex.Matches(jql, "\"(?<k>[A-Z0-9-]+)\"").Select(m => m.Groups["k"].Value);
            var issues = keys
                .Where(Updated.ContainsKey)
                .Select(k => $"{{\"key\":\"{k}\",\"fields\":{{\"updated\":\"{Updated[k]}\"}}}}");
            var body = $"{{\"issues\":[{string.Join(",", issues)}],\"isLast\":true}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}

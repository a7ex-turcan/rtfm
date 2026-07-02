using Rtfm.Core.Watch;

namespace Rtfm.Core.Tests.Watch;

/// <summary>
/// Pins <see cref="WatchEvent.ToString"/> to the pre-Phase-7 plain log lines.
/// Scripts (and the watch smoke tests) parse these exact strings from stderr in
/// non-interactive mode — changing them is a breaking change, not a tweak.
/// </summary>
public class WatchEventTests
{
    [Theory]
    [InlineData(WatchEventKind.Reconciled, "a.md", 3, null, "  reconciled a.md → 3 chunks")]
    [InlineData(WatchEventKind.Indexed, "b.doc", 25, null, "  indexed b.doc → 25 chunks")]
    [InlineData(WatchEventKind.Deleted, "c.md", null, null, "  deleted c.md")]
    [InlineData(WatchEventKind.Removed, "d:/docs/d.md", null, null, "  removed d:/docs/d.md (gone from disk)")]
    [InlineData(WatchEventKind.Failed, "e.docx", null, "boom", "  FAILED e.docx: boom")]
    [InlineData(WatchEventKind.WatcherError, null, null, "overflow", "  watcher error: overflow")]
    [InlineData(WatchEventKind.ReconcileComplete, null, null, "2 indexed/updated, 1 removed, 2 tracked.", "Reconcile complete: 2 indexed/updated, 1 removed, 2 tracked.")]
    public void ToString_matches_the_plain_log_line_contract(
        WatchEventKind kind, string? path, int? chunks, string? detail, string expected)
    {
        Assert.Equal(expected, new WatchEvent(kind, path, chunks, detail).ToString());
    }

    [Fact]
    public void Watching_event_renders_the_startup_line()
    {
        var e = new WatchEvent(WatchEventKind.Watching, Path: "d:/docs", Detail: "d:/docs (project 'pam') — Ctrl+C to stop.");
        Assert.Equal("Watching d:/docs (project 'pam') — Ctrl+C to stop.", e.ToString());
    }
}

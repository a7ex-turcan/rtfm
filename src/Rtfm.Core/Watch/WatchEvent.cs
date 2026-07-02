namespace Rtfm.Core.Watch;

/// <summary>What happened in watch mode — see <see cref="WatchEvent"/>.</summary>
public enum WatchEventKind
{
    /// <summary>Watching has started (after reconcile).</summary>
    Watching,

    /// <summary>Startup reconcile (re)indexed a file that changed while the watcher was off.</summary>
    Reconciled,

    /// <summary>Startup reconcile finished; <see cref="WatchEvent.Detail"/> carries the summary.</summary>
    ReconcileComplete,

    /// <summary>A live change was indexed.</summary>
    Indexed,

    /// <summary>A live delete removed the doc's chunks.</summary>
    Deleted,

    /// <summary>Reconcile removed a doc that vanished while the watcher was off.</summary>
    Removed,

    /// <summary>An operation on one file failed; <see cref="WatchEvent.Detail"/> carries the error.</summary>
    Failed,

    /// <summary>The underlying <c>FileSystemWatcher</c> reported an error.</summary>
    WatcherError,
}

/// <summary>
/// One structured watch-mode event (Phase 7). Presentation-agnostic: the CLI's
/// live dashboard consumes the fields, while <see cref="ToString"/> renders the
/// plain log line — byte-identical to the pre-Phase-7 output — for redirected /
/// non-interactive use, so scripts that parse watch output keep working.
/// </summary>
public sealed record WatchEvent(
    WatchEventKind Kind,
    string? Path = null,
    int? ChunkCount = null,
    string? Detail = null)
{
    public override string ToString() => Kind switch
    {
        WatchEventKind.Watching => $"Watching {Detail}",
        WatchEventKind.Reconciled => $"  reconciled {Path} → {ChunkCount} chunks",
        WatchEventKind.ReconcileComplete => $"Reconcile complete: {Detail}",
        WatchEventKind.Indexed => $"  indexed {Path} → {ChunkCount} chunks",
        WatchEventKind.Deleted => $"  deleted {Path}",
        WatchEventKind.Removed => $"  removed {Path} (gone from disk)",
        WatchEventKind.Failed => $"  FAILED {Path}: {Detail}",
        WatchEventKind.WatcherError => $"  watcher error: {Detail}",
        _ => Detail ?? string.Empty,
    };
}

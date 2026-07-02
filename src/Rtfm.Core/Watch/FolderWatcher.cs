using System.Collections.Concurrent;
using Rtfm.Core.Indexing;
using Rtfm.Core.Manifest;

namespace Rtfm.Core.Watch;

/// <summary>
/// Keeps the index in step with a docs folder as files change (§2.8, Phase 5).
///
/// The naive <see cref="FileSystemWatcher"/> misbehaves, so this wraps it with
/// the four pieces §2.8 calls for:
/// <list type="bullet">
///   <item><b>Debounce</b> — one save fires several events; changes are coalesced
///     per normalized path over a quiet window before acting.</item>
///   <item><b>Lock retry</b> — <c>Changed</c> often fires while the editor is
///     still writing, so opening throws <see cref="IOException"/>; ingest retries
///     with backoff.</item>
///   <item><b>Deletes &amp; renames</b> — a delete drops the doc's chunks; a rename
///     is a delete of the old path plus an upsert of the new one.</item>
///   <item><b>Startup reconcile</b> — the watcher only sees changes from launch
///     onward, so on start it diffs the folder against the stored manifest and
///     catches up on anything edited while it was off.</item>
/// </list>
///
/// The queue key is the normalized source path (so events coalesce and match the
/// index key), but the <em>original</em> path is kept for file I/O — the
/// normalized key is lower-cased and would not open on a case-sensitive OS.
/// </summary>
public sealed class FolderWatcher
{
    private enum ChangeKind { Upsert, Delete }

    private readonly record struct Pending(ChangeKind Kind, string OriginalPath, DateTime QueuedUtc);

    private readonly string _folder;
    private readonly string _project;
    private readonly DocumentIngestor _ingestor;
    private readonly ManifestStore _manifestStore;
    private readonly Action<string> _log;
    private readonly TimeSpan _debounceWindow;

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _drainLock = new(1, 1);
    private DocumentManifest _manifest = new();

    public FolderWatcher(
        string folder,
        string project,
        DocumentIngestor ingestor,
        ManifestStore manifestStore,
        Action<string>? log = null,
        TimeSpan? debounceWindow = null)
    {
        _folder = folder;
        _project = project;
        _ingestor = ingestor;
        _manifestStore = manifestStore;
        _log = log ?? (_ => { });
        _debounceWindow = debounceWindow ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>
    /// Ensures the index exists, reconciles against the manifest, then watches
    /// until <paramref name="cancellationToken"/> is cancelled (Ctrl+C).
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _ingestor.EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
        _manifest = _manifestStore.Load();
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);

        using var watcher = CreateWatcher();
        watcher.EnableRaisingEvents = true;
        _log($"Watching {_folder} (project '{_project}') — Ctrl+C to stop.");

        try
        {
            // Tick faster than the debounce window; DrainAsync only acts on
            // entries that have been quiet for the full window.
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await DrainAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Ctrl+C.
        }
        finally
        {
            watcher.EnableRaisingEvents = false;
            // Best-effort flush of anything still queued at shutdown.
            await DrainAsync(CancellationToken.None, ignoreDebounce: true).ConfigureAwait(false);
        }
    }

    private FileSystemWatcher CreateWatcher()
    {
        var watcher = new FileSystemWatcher(_folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        watcher.Created += (_, e) => Enqueue(ChangeKind.Upsert, e.FullPath);
        watcher.Changed += (_, e) => Enqueue(ChangeKind.Upsert, e.FullPath);
        watcher.Deleted += (_, e) => Enqueue(ChangeKind.Delete, e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            // A rename is a delete of the old path + an upsert of the new one
            // (§2.8). Either side is dropped if its extension isn't supported.
            Enqueue(ChangeKind.Delete, e.OldFullPath);
            Enqueue(ChangeKind.Upsert, e.FullPath);
        };
        watcher.Error += (_, e) => _log($"  watcher error: {e.GetException().Message}");

        return watcher;
    }

    private void Enqueue(ChangeKind kind, string fullPath)
    {
        if (!DocumentIngestor.IsSupported(fullPath))
        {
            return;
        }

        var key = PathNormalizer.Normalize(fullPath);
        // Last event wins per path: changed-then-deleted → delete;
        // deleted-then-created → upsert. QueuedUtc resets the quiet window.
        _pending[key] = new Pending(kind, fullPath, DateTime.UtcNow);
    }

    /// <summary>Startup catch-up: diff the folder against the manifest (§2.8).</summary>
    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var indexedAt = DateTimeOffset.UtcNow;

        var onDisk = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in DocumentIngestor.EnumerateSupportedFiles(_folder))
        {
            onDisk[PathNormalizer.Normalize(file)] = file;
        }

        int indexed = 0, removed = 0;

        foreach (var (key, original) in onDisk)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryReadEntry(original, out var entry))
            {
                continue;
            }

            if (_manifest.TryGet(key, out var existing) && existing == entry)
            {
                continue; // unchanged since last run
            }

            try
            {
                var n = await _ingestor.IngestFileAsync(original, _project, indexedAt, cancellationToken).ConfigureAwait(false);
                _manifest.Set(key, entry);
                indexed++;
                _log($"  reconciled {Path.GetFileName(original)} → {n} chunks");
            }
            catch (Exception ex)
            {
                _log($"  FAILED {Path.GetFileName(original)}: {ex.Message}");
            }
        }

        // Anything the manifest tracked but that is no longer on disk was deleted
        // while the watcher was off.
        foreach (var key in _manifest.Paths.Where(k => !onDisk.ContainsKey(k)).ToList())
        {
            try
            {
                await _ingestor.RemoveFileAsync(key, cancellationToken).ConfigureAwait(false);
                _manifest.Remove(key);
                removed++;
                _log($"  removed {key} (gone from disk)");
            }
            catch (Exception ex)
            {
                _log($"  FAILED remove {key}: {ex.Message}");
            }
        }

        if (indexed > 0 || removed > 0)
        {
            await _ingestor.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        _manifestStore.Save(_manifest);
        _log($"Reconcile complete: {indexed} indexed/updated, {removed} removed, {onDisk.Count} tracked.");
    }

    /// <summary>Processes queued changes that have been quiet for the debounce window.</summary>
    private async Task DrainAsync(CancellationToken cancellationToken, bool ignoreDebounce = false)
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        // Only one drain at a time; if one is already running, skip this tick.
        if (!await _drainLock.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            var ready = _pending
                .Where(kv => ignoreDebounce || now - kv.Value.QueuedUtc >= _debounceWindow)
                .ToList();

            if (ready.Count == 0)
            {
                return;
            }

            var indexedAt = DateTimeOffset.UtcNow;
            var dirty = false;

            foreach (var (key, pending) in ready)
            {
                // Remove only if the entry is unchanged since the snapshot — a
                // newer event arriving during the window keeps it for next tick.
                if (!_pending.TryRemove(new KeyValuePair<string, Pending>(key, pending)))
                {
                    continue;
                }

                try
                {
                    if (pending.Kind == ChangeKind.Delete)
                    {
                        await _ingestor.RemoveFileAsync(pending.OriginalPath, cancellationToken).ConfigureAwait(false);
                        _manifest.Remove(key);
                        _log($"  deleted {Path.GetFileName(pending.OriginalPath)}");
                        dirty = true;
                    }
                    else if (await TryIngestWithRetryAsync(pending.OriginalPath, indexedAt, cancellationToken).ConfigureAwait(false) is { } chunks)
                    {
                        if (TryReadEntry(pending.OriginalPath, out var entry))
                        {
                            _manifest.Set(key, entry);
                        }

                        _log($"  indexed {Path.GetFileName(pending.OriginalPath)} → {chunks} chunks");
                        dirty = true;
                    }
                }
                catch (Exception ex)
                {
                    _log($"  FAILED {Path.GetFileName(pending.OriginalPath)}: {ex.Message}");
                }
            }

            if (dirty)
            {
                await _ingestor.RefreshAsync(cancellationToken).ConfigureAwait(false);
                _manifestStore.Save(_manifest);
            }
        }
        finally
        {
            _drainLock.Release();
        }
    }

    /// <summary>
    /// Ingests with lock-retry backoff. Returns the chunk count, or null if the
    /// file vanished before it could be read (a create+delete inside one window).
    /// A persistent <see cref="IOException"/> is rethrown for the caller to log.
    /// </summary>
    private async Task<int?> TryIngestWithRetryAsync(string path, DateTimeOffset indexedAt, CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;

        for (var attempt = 1; ; attempt++)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return await _ingestor.IngestFileAsync(path, _project, indexedAt, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                // Editor still writing; the debounce window absorbs most of this,
                // this backoff catches the rest.
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool TryReadEntry(string path, out ManifestEntry entry)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                entry = new ManifestEntry(info.LastWriteTimeUtc.Ticks, info.Length);
                return true;
            }
        }
        catch (IOException)
        {
            // Fall through.
        }

        entry = default;
        return false;
    }
}

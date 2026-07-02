namespace Rtfm.Core.Manifest;

/// <summary>
/// A file's change-detection fingerprint: last-write time and length. If either
/// differs from the stored value the file is treated as changed and re-indexed.
/// (§2.8 stores "last-write / content hash"; last-write+length is the cheap,
/// reliable signal for an occasionally-changing corpus — no full read on scan.)
/// </summary>
public readonly record struct ManifestEntry(long LastWriteUtcTicks, long Length);

/// <summary>
/// The startup-reconcile manifest (§2.8): a map of normalized source path
/// (<see cref="Indexing.PathNormalizer"/>) → <see cref="ManifestEntry"/>. It is
/// the record of what the watcher believes is indexed and how fresh it was, so
/// on the next start it can catch up on anything edited, added, or deleted while
/// it was off. Not thread-safe: mutate it from a single drain path.
/// </summary>
public sealed class DocumentManifest
{
    private readonly Dictionary<string, ManifestEntry> _entries;

    public DocumentManifest()
        => _entries = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);

    internal DocumentManifest(Dictionary<string, ManifestEntry> entries)
        => _entries = entries;

    /// <summary>The normalized paths currently tracked.</summary>
    public IReadOnlyCollection<string> Paths => _entries.Keys;

    public int Count => _entries.Count;

    public bool TryGet(string key, out ManifestEntry entry) => _entries.TryGetValue(key, out entry);

    public void Set(string key, ManifestEntry entry) => _entries[key] = entry;

    public bool Remove(string key) => _entries.Remove(key);

    /// <summary>A read-only view for serialization.</summary>
    internal IReadOnlyDictionary<string, ManifestEntry> Entries => _entries;
}

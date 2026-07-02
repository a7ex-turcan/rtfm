namespace Rtfm.Core.Indexing;

/// <summary>
/// Produces the canonical <c>source_path</c> key stored on every chunk and used
/// for exact-match delete-by-query (§2.9). This is the one normalization that
/// must be applied *identically* on index, delete, and rename, or stale chunks
/// leak (§2.12).
///
/// The documented rule: absolute path → forward slashes → lower-cased
/// (invariant). Lower-casing keeps the key stable across Windows'
/// case-insensitive filesystem and any casing drift in watcher events; the
/// theoretical cost (two files differing only by case on Linux) does not happen
/// for real document filenames.
/// </summary>
public static class PathNormalizer
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Path.GetFullPath(path)
            .Replace('\\', '/')
            .ToLowerInvariant();
    }
}

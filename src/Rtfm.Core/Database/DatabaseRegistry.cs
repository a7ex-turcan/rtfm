using System.Text.Json;
using Rtfm.Core.Manifest;

namespace Rtfm.Core.Database;

/// <summary>One discovered <c>.rtfmdb</c> connector, for <c>list_databases</c> / <c>rtfm db</c>.</summary>
/// <param name="Name">The descriptor's friendly <c>name</c>, or the filename stem when it has none.</param>
/// <param name="Handle">The filename stem — a stable, typeable identifier to pass back to <c>query_database</c>.</param>
/// <param name="Project">The project whose indexed folder the descriptor was found under.</param>
/// <param name="Provider">Lower-cased provider (<c>postgres</c> / <c>sqlserver</c>).</param>
/// <param name="Queryable">True when the descriptor carries a <c>query</c> block (Phase 23 opt-in).</param>
/// <param name="Writable">True when that block also sets <c>allowWrites</c>; reads are the default.</param>
/// <param name="DescriptorPath">Absolute path to the descriptor file (used to re-read it at query time).</param>
public sealed record DatabaseInfo(string Name, string Handle, string Project, string Provider, bool Queryable, bool Writable, string DescriptorPath);

/// <summary>
/// Discovers <c>.rtfmdb</c> connector descriptors on disk (Phase 23). Rather than
/// a new env var or a stored path in the index (the stored source-path key is
/// lower-cased and won't reopen on a case-sensitive filesystem — §2.12), it reuses
/// the watch manifests: every indexed folder is recorded there with its
/// original casing (<see cref="ManifestInfo.OpenableFolder"/>), so scanning those
/// folders for <c>*.rtfmdb</c> finds exactly the descriptors that were indexed,
/// scoped by the same project. The precondition — the folder was indexed — is one
/// the caller already met by having the schema searchable at all.
/// </summary>
public static class DatabaseRegistry
{
    /// <summary>
    /// Every discoverable connector, optionally scoped to one project. A descriptor
    /// reachable from several manifest folders is returned once (first-seen wins).
    /// </summary>
    public static IReadOnlyList<DatabaseInfo> List(string? project = null)
    {
        var databases = new List<DatabaseInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in ManifestStore.ListAll())
        {
            if (project is not null && !string.Equals(manifest.Project, project, StringComparison.Ordinal))
            {
                continue;
            }

            var folder = manifest.OpenableFolder;
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.rtfmdb", SearchOption.AllDirectories))
            {
                if (!seen.Add(Path.GetFullPath(file)))
                {
                    continue;
                }

                if (TryRead(file, manifest.Project) is { } info)
                {
                    databases.Add(info);
                }
            }
        }

        return databases
            .OrderBy(d => d.Project, StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Resolves a database by the identifier an agent/user typed: the filename
    /// handle first (stable), then the descriptor's friendly name — both
    /// case-insensitive. Scope narrows ambiguity when the same handle exists in
    /// two projects. Null when nothing matches.
    /// </summary>
    public static DatabaseInfo? Resolve(string identifier, string? project = null)
    {
        var all = List(project);
        return all.FirstOrDefault(d => string.Equals(d.Handle, identifier, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(d => string.Equals(d.Name, identifier, StringComparison.OrdinalIgnoreCase));
    }

    private static DatabaseInfo? TryRead(string file, string project)
    {
        try
        {
            var descriptor = DbDescriptor.Parse(File.ReadAllText(file));
            var handle = Path.GetFileNameWithoutExtension(file);
            var name = string.IsNullOrWhiteSpace(descriptor.Name) ? handle : descriptor.Name.Trim();
            return new DatabaseInfo(name, handle, project, descriptor.Provider.ToLowerInvariant(), descriptor.IsQueryable, descriptor.AllowsWrites, Path.GetFullPath(file));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            // A malformed descriptor is skipped, not fatal — same tolerance as the manifest scan.
            return null;
        }
    }
}

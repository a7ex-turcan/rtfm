using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rtfm.Core.Indexing;

namespace Rtfm.Core.Manifest;

/// <summary>
/// Loads and persists a <see cref="DocumentManifest"/> for one (folder, project)
/// pair. The file lives outside the corpus (under the user's local application
/// data) so it never pollutes the docs folder or gets picked up as a document.
/// Keyed by a hash of the normalized folder path + project so watching the same
/// folder under two projects keeps two independent manifests.
/// </summary>
public sealed class ManifestStore
{
    private const int Version = 1;

    private readonly string _folder;
    private readonly string _project;
    private readonly string _filePath;

    private ManifestStore(string folder, string project, string filePath)
    {
        _folder = folder;
        _project = project;
        _filePath = filePath;
    }

    /// <summary>The resolved manifest file path (useful for diagnostics / logging).</summary>
    public string FilePath => _filePath;

    /// <summary>Resolves the manifest store for a folder + project.</summary>
    public static ManifestStore For(string folder, string project)
    {
        var normalizedFolder = PathNormalizer.Normalize(folder);
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{normalizedFolder}|{project}")))[..16].ToLowerInvariant();

        var dir = Path.Combine(ResolveStateDirectory(), "rtfm", "manifests");
        Directory.CreateDirectory(dir);
        return new ManifestStore(normalizedFolder, project, Path.Combine(dir, $"{hash}.json"));
    }

    /// <summary>Reads the manifest, or an empty one if none exists / it can't be parsed.</summary>
    public DocumentManifest Load()
    {
        if (!File.Exists(_filePath))
        {
            return new DocumentManifest();
        }

        try
        {
            var file = JsonSerializer.Deserialize<ManifestFile>(File.ReadAllText(_filePath));
            if (file?.Entries is null)
            {
                return new DocumentManifest();
            }

            var entries = file.Entries.ToDictionary(
                kv => kv.Key,
                kv => new ManifestEntry(kv.Value.T, kv.Value.L),
                StringComparer.Ordinal);
            return new DocumentManifest(entries);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt manifest is not fatal: treat it as empty and reconcile
            // rebuilds it from disk + the index.
            return new DocumentManifest();
        }
    }

    /// <summary>Writes the manifest atomically (temp file + move) so a crash mid-write can't corrupt it.</summary>
    public void Save(DocumentManifest manifest)
    {
        var file = new ManifestFile(
            Version,
            _folder,
            _project,
            manifest.Entries.ToDictionary(kv => kv.Key, kv => new EntryDto(kv.Value.LastWriteUtcTicks, kv.Value.Length)));

        var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _filePath, overwrite: true);
    }

    private static string ResolveStateDirectory()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(dir) ? Path.GetTempPath() : dir;
    }

    private sealed record ManifestFile(int Version, string Folder, string Project, Dictionary<string, EntryDto> Entries);

    private sealed record EntryDto(long T, long L);
}

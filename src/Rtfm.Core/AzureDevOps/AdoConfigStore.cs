using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rtfm.Core.AzureDevOps;

/// <summary>
/// Loads and persists an <see cref="AdoConfig"/> per project (§2.18) under the
/// user's local application data — never in the docs folder or the repo — so it
/// is never committed and never picked up as a document. Keyed by a hash of the
/// project name (also stored inside for enumeration), mirroring
/// <see cref="Jira.JiraConfigStore"/>.
/// </summary>
public static class AdoConfigStore
{
    private const int Version = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Reads the config for <paramref name="project"/>, or null if none is configured.</summary>
    public static AdoConfig? Load(string project)
    {
        var file = PathFor(project);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(file), Json)?.Config;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Writes the config for <paramref name="project"/> atomically (temp + move).</summary>
    public static void Save(string project, AdoConfig config)
    {
        Directory.CreateDirectory(Directory_);
        var file = PathFor(project);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new ConfigFile(Version, project, config), Json));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>Removes the config for <paramref name="project"/>. Returns true if a file was deleted.</summary>
    public static bool Remove(string project)
    {
        var file = PathFor(project);
        if (!File.Exists(file))
        {
            return false;
        }

        File.Delete(file);
        return true;
    }

    /// <summary>Every configured (project, config) pair, project-sorted. Unreadable files are skipped.</summary>
    public static IReadOnlyList<(string Project, AdoConfig Config)> List()
    {
        if (!Directory.Exists(Directory_))
        {
            return [];
        }

        var results = new List<(string, AdoConfig)>();
        foreach (var file in Directory.EnumerateFiles(Directory_, "*.json"))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(file), Json);
                if (parsed is { Project: not null, Config: not null })
                {
                    results.Add((parsed.Project, parsed.Config));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Skip — same tolerance as the manifest scan.
            }
        }

        return results.OrderBy(r => r.Item1, StringComparer.Ordinal).ToList();
    }

    internal static string Directory_ => Path.Combine(AdoState.Directory, "rtfm", "ado");

    internal static string HashName(string project)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project)))[..16].ToLowerInvariant();

    private static string PathFor(string project) => Path.Combine(Directory_, $"{HashName(project)}.json");

    private sealed record ConfigFile(int Version, string Project, AdoConfig Config);
}

/// <summary>Where per-user connector state lives (LocalApplicationData, temp as a last resort).</summary>
internal static class AdoState
{
    public static string Directory
    {
        get
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(dir) ? Path.GetTempPath() : dir;
        }
    }
}

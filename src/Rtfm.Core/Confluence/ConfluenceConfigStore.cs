using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rtfm.Core.Confluence;

/// <summary>
/// Loads and persists a <see cref="ConfluenceConfig"/> per project (§2.17) under
/// <c>LocalApplicationData/rtfm/confluence</c> — the Confluence twin of
/// <see cref="Jira.JiraConfigStore"/>, never committed and never indexed. Keyed
/// by a hash of the project name (also stored inside for enumeration).
/// </summary>
public static class ConfluenceConfigStore
{
    private const int Version = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Reads the config for <paramref name="project"/>, or null if none is configured.</summary>
    public static ConfluenceConfig? Load(string project)
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
    public static void Save(string project, ConfluenceConfig config)
    {
        Directory.CreateDirectory(Directory_);
        var file = PathFor(project);
        var json = JsonSerializer.Serialize(new ConfigFile(Version, project, config), Json);
        var temp = file + ".tmp";
        File.WriteAllText(temp, json);
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
    public static IReadOnlyList<(string Project, ConfluenceConfig Config)> List()
    {
        if (!Directory.Exists(Directory_))
        {
            return [];
        }

        var results = new List<(string, ConfluenceConfig)>();
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

    private static string Directory_ => Path.Combine(StateDirectory, "rtfm", "confluence");

    private static string PathFor(string project)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project)))[..16].ToLowerInvariant();
        return Path.Combine(Directory_, $"{hash}.json");
    }

    private static string StateDirectory
    {
        get
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(dir) ? Path.GetTempPath() : dir;
        }
    }

    private sealed record ConfigFile(int Version, string Project, ConfluenceConfig Config);
}

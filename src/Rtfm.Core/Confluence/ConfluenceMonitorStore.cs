using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rtfm.Core.Confluence;

/// <summary>
/// One monitored Confluence page (§2.17 step 3). <see cref="LastVersion"/> is the
/// page's <c>version.number</c> at the last pull — the watch loop compares it
/// against the live value (a monotonic edit counter, cleaner than a timestamp)
/// to detect change.
/// </summary>
public sealed record MonitoredPage(string Id, int LastVersion);

/// <summary>
/// A project's monitored Confluence page set (§2.17 step 3) — the crawled page
/// ids so <c>rtfm confluence watch</c> knows what to re-poll. Not thread-safe.
/// </summary>
public sealed class ConfluenceMonitor
{
    private readonly Dictionary<string, MonitoredPage> _pages;

    public ConfluenceMonitor() => _pages = new Dictionary<string, MonitoredPage>(StringComparer.Ordinal);

    internal ConfluenceMonitor(Dictionary<string, MonitoredPage> pages, DateTimeOffset? lastPolledAt)
    {
        _pages = pages;
        LastPolledAt = lastPolledAt;
    }

    /// <summary>When the watch loop last completed a poll (or the seeding index run).</summary>
    public DateTimeOffset? LastPolledAt { get; set; }

    public int Count => _pages.Count;

    public IReadOnlyDictionary<string, MonitoredPage> Pages => _pages;

    /// <summary>Adds or replaces a page (keyed by id).</summary>
    public void Set(MonitoredPage page) => _pages[page.Id] = page;

    public bool Remove(string id) => _pages.Remove(id);

    /// <summary>
    /// The monitored page ids whose live <c>version.number</c> is higher than
    /// what we last stored — the re-index set. A page absent from
    /// <paramref name="current"/> (deleted, or no longer visible) is left
    /// untouched. Pure logic — unit-tested.
    /// </summary>
    public IReadOnlyList<string> SelectChanged(IReadOnlyDictionary<string, int?> current)
    {
        var changed = new List<string>();
        foreach (var (id, page) in _pages)
        {
            if (current.TryGetValue(id, out var live) && live is { } version && version > page.LastVersion)
            {
                changed.Add(id);
            }
        }

        return changed;
    }
}

/// <summary>
/// Persists a <see cref="ConfluenceMonitor"/> per project under
/// <c>LocalApplicationData/rtfm/confluence/monitor</c> — the Confluence twin of
/// <see cref="Jira.JiraMonitorStore"/>. Atomic temp+move write, unreadable files
/// tolerated.
/// </summary>
public static class ConfluenceMonitorStore
{
    private const int Version = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Reads the monitor for <paramref name="project"/>, or an empty one if none exists / it can't be parsed.</summary>
    public static ConfluenceMonitor Load(string project)
    {
        var file = PathFor(project);
        if (!File.Exists(file))
        {
            return new ConfluenceMonitor();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<MonitorFile>(File.ReadAllText(file), Json);
            if (parsed?.Pages is null)
            {
                return new ConfluenceMonitor();
            }

            var pages = parsed.Pages.ToDictionary(
                kv => kv.Key,
                kv => new MonitoredPage(kv.Key, kv.Value),
                StringComparer.Ordinal);
            return new ConfluenceMonitor(pages, parsed.LastPolledAt);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new ConfluenceMonitor();
        }
    }

    /// <summary>Writes the monitor atomically (temp + move).</summary>
    public static void Save(string project, ConfluenceMonitor monitor)
    {
        Directory.CreateDirectory(Directory_);
        var file = PathFor(project);
        var dto = new MonitorFile(
            Version,
            project,
            monitor.LastPolledAt,
            monitor.Pages.ToDictionary(kv => kv.Key, kv => kv.Value.LastVersion));
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(dto, Json));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>Removes the monitor for <paramref name="project"/>. Returns true if a file was deleted.</summary>
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

    private static string Directory_ => Path.Combine(StateDirectory, "rtfm", "confluence", "monitor");

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

    private sealed record MonitorFile(int Version, string Project, DateTimeOffset? LastPolledAt, Dictionary<string, int> Pages);
}

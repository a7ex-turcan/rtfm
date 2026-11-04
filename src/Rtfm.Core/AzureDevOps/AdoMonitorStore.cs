using System.Text.Json;

namespace Rtfm.Core.AzureDevOps;

/// <summary>
/// One work item in a project's monitored set. <see cref="LastRev"/> is the
/// item's <c>System.Rev</c> at the last pull — a monotonic revision counter,
/// Azure DevOps' analogue of Confluence's <c>version.number</c> (§2.17), and a
/// cleaner change signal than a timestamp. <see cref="Full"/> preserves crawl
/// fidelity: a seed keeps its comments on re-pull, a deeper item stays
/// comment-free.
/// </summary>
public sealed record MonitoredWorkItem(int Id, int LastRev, bool Full);

/// <summary>
/// A project's monitored work item set (§2.18) — what a crawl produced, so
/// <c>rtfm ado watch</c> knows what to re-poll. Not thread-safe; mutate from a
/// single path.
/// </summary>
public sealed class AdoMonitor
{
    private readonly Dictionary<int, MonitoredWorkItem> _items;

    public AdoMonitor() => _items = [];

    internal AdoMonitor(Dictionary<int, MonitoredWorkItem> items, DateTimeOffset? lastPolledAt)
    {
        _items = items;
        LastPolledAt = lastPolledAt;
    }

    /// <summary>When the watch loop last completed a poll (or the seeding index run).</summary>
    public DateTimeOffset? LastPolledAt { get; set; }

    public int Count => _items.Count;

    public IReadOnlyDictionary<int, MonitoredWorkItem> Items => _items;

    public void Set(MonitoredWorkItem item) => _items[item.Id] = item;

    public bool Remove(int id) => _items.Remove(id);

    /// <summary>
    /// The monitored ids whose live revision is higher than the stored one — the
    /// re-pull set. An id absent from <paramref name="current"/> (deleted, or no
    /// longer visible) is left untouched, never treated as changed. Pure logic.
    /// </summary>
    public IReadOnlyList<int> SelectChanged(IReadOnlyDictionary<int, int> current)
    {
        var changed = new List<int>();
        foreach (var (id, item) in _items)
        {
            if (current.TryGetValue(id, out var live) && live > item.LastRev)
            {
                changed.Add(id);
            }
        }

        changed.Sort();
        return changed;
    }
}

/// <summary>
/// Persists an <see cref="AdoMonitor"/> per project under
/// <c>LocalApplicationData/rtfm/ado/monitor</c> — a subfolder distinct from the
/// config files so the two never collide on a project's hashed filename.
/// </summary>
public static class AdoMonitorStore
{
    private const int Version = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Reads the monitor for <paramref name="project"/>, or an empty one if none exists / it can't be parsed.</summary>
    public static AdoMonitor Load(string project)
    {
        var file = PathFor(project);
        if (!File.Exists(file))
        {
            return new AdoMonitor();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<MonitorFile>(File.ReadAllText(file), Json);
            if (parsed?.Items is null)
            {
                return new AdoMonitor();
            }

            var items = parsed.Items.ToDictionary(
                kv => kv.Key,
                kv => new MonitoredWorkItem(kv.Key, kv.Value.R, kv.Value.F));
            return new AdoMonitor(items, parsed.LastPolledAt);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new AdoMonitor();
        }
    }

    /// <summary>Writes the monitor atomically (temp + move).</summary>
    public static void Save(string project, AdoMonitor monitor)
    {
        Directory.CreateDirectory(Directory_);
        var file = PathFor(project);
        var dto = new MonitorFile(
            Version,
            project,
            monitor.LastPolledAt,
            monitor.Items.ToDictionary(kv => kv.Key, kv => new ItemDto(kv.Value.LastRev, kv.Value.Full)));
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

    private static string Directory_ => Path.Combine(AdoConfigStore.Directory_, "monitor");

    private static string PathFor(string project) => Path.Combine(Directory_, $"{AdoConfigStore.HashName(project)}.json");

    private sealed record MonitorFile(int Version, string Project, DateTimeOffset? LastPolledAt, Dictionary<int, ItemDto> Items);

    private sealed record ItemDto(int R, bool F);
}

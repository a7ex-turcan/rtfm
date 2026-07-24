using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rtfm.Core.Jira;

/// <summary>
/// One ticket in a project's monitored set. <see cref="LastUpdated"/> is the
/// ticket's <c>updated</c> value at the last pull — the watch loop compares it
/// against the live value to detect change. <see cref="Full"/> preserves the
/// crawl fidelity (§2.16): a ticket first pulled as the seed keeps its comments
/// on re-pull; a deeper, description-only ticket stays description-only.
/// </summary>
public sealed record MonitoredTicket(string Key, DateTimeOffset? LastUpdated, bool Full);

/// <summary>
/// A project's monitored Jira ticket set (§2.16 Phase 25 step 3) — the expanded
/// key set a crawl produced, so <c>rtfm jira watch</c> knows what to re-poll.
/// Not thread-safe; mutate from a single path.
/// </summary>
public sealed class JiraMonitor
{
    private readonly Dictionary<string, MonitoredTicket> _tickets;

    public JiraMonitor() => _tickets = new Dictionary<string, MonitoredTicket>(StringComparer.OrdinalIgnoreCase);

    internal JiraMonitor(Dictionary<string, MonitoredTicket> tickets, DateTimeOffset? lastPolledAt)
    {
        _tickets = tickets;
        LastPolledAt = lastPolledAt;
    }

    /// <summary>When the watch loop last completed a poll (or the seeding index run).</summary>
    public DateTimeOffset? LastPolledAt { get; set; }

    public int Count => _tickets.Count;

    public IReadOnlyDictionary<string, MonitoredTicket> Tickets => _tickets;

    /// <summary>Adds or replaces a ticket (keyed by canonical upper-case key).</summary>
    public void Set(MonitoredTicket ticket)
    {
        var key = ticket.Key.Trim().ToUpperInvariant();
        _tickets[key] = ticket with { Key = key };
    }

    public bool Remove(string key) => _tickets.Remove(key.Trim().ToUpperInvariant());

    /// <summary>
    /// The monitored keys whose live <c>updated</c> is newer than what we last
    /// stored (or that we have no stored stamp for) — the re-pull set. A key
    /// absent from <paramref name="current"/> (deleted, or no longer visible) is
    /// left untouched, never treated as changed. Pure logic — unit-tested.
    /// </summary>
    public IReadOnlyList<string> SelectChanged(IReadOnlyDictionary<string, DateTimeOffset?> current)
    {
        var changed = new List<string>();
        foreach (var (key, ticket) in _tickets)
        {
            if (current.TryGetValue(key, out var now) && now is { } live
                && (ticket.LastUpdated is not { } stored || live > stored))
            {
                changed.Add(key);
            }
        }

        return changed;
    }
}

/// <summary>
/// Persists a <see cref="JiraMonitor"/> per project under
/// <c>LocalApplicationData/rtfm/jira/monitor</c> — a subfolder distinct from the
/// config files so the two never collide on a project's hashed filename. Atomic
/// temp+move write, unreadable files tolerated (same posture as the manifest
/// store).
/// </summary>
public static class JiraMonitorStore
{
    private const int Version = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Reads the monitor for <paramref name="project"/>, or an empty one if none exists / it can't be parsed.</summary>
    public static JiraMonitor Load(string project)
    {
        var file = PathFor(project);
        if (!File.Exists(file))
        {
            return new JiraMonitor();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<MonitorFile>(File.ReadAllText(file), Json);
            if (parsed?.Tickets is null)
            {
                return new JiraMonitor();
            }

            var tickets = parsed.Tickets.ToDictionary(
                kv => kv.Key,
                kv => new MonitoredTicket(kv.Key, kv.Value.U, kv.Value.F),
                StringComparer.OrdinalIgnoreCase);
            return new JiraMonitor(tickets, parsed.LastPolledAt);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new JiraMonitor();
        }
    }

    /// <summary>Writes the monitor atomically (temp + move).</summary>
    public static void Save(string project, JiraMonitor monitor)
    {
        Directory.CreateDirectory(Directory_);
        var file = PathFor(project);
        var dto = new MonitorFile(
            Version,
            project,
            monitor.LastPolledAt,
            monitor.Tickets.ToDictionary(kv => kv.Key, kv => new TicketDto(kv.Value.LastUpdated, kv.Value.Full)));
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

    private static string Directory_ => Path.Combine(StateDirectory, "rtfm", "jira", "monitor");

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

    private sealed record MonitorFile(int Version, string Project, DateTimeOffset? LastPolledAt, Dictionary<string, TicketDto> Tickets);

    private sealed record TicketDto(DateTimeOffset? U, bool F);
}

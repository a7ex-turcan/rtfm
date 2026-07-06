using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rtfm.Core.Embeddings;
using Rtfm.Core.OpenSearch;

namespace Rtfm.Core.Notes;

/// <summary>One user-confirmed correction (§2.13 C / Phase 13).</summary>
public sealed record Note(
    string Id,
    string Project,
    string Text,
    string? TargetPath,
    string Author,
    DateTimeOffset CreatedAt);

/// <summary>
/// CRUD + retrieval for override notes. Adding embeds the note text with the
/// same model as document chunks (when an embedder is supplied), so notes
/// participate in semantic matching; without one they still match lexically.
/// Human-in-the-loop is enforced by the callers (CLI prompt-free by intent —
/// typing the command *is* the confirmation; the MCP tool's description
/// requires explicit user approval before the agent may call it).
/// </summary>
public sealed class NotesStore(OpenSearchGateway gateway, ITextEmbedder? embedder = null)
{
    /// <summary>
    /// kNN score floor for query→note matching. Deliberately looser than
    /// contradiction detection's 0.75: that compares statement-to-statement
    /// (near-paraphrases), this compares *question*-to-statement, which runs
    /// structurally lower (a relevant note scored 0.67 in live validation).
    /// 0.6 ≈ cosine 0.67 — topically related; the reranker judges from there.
    /// </summary>
    internal const double MinSearchScore = 0.6;

    private const int MaxNoteCandidates = 5;

    public Task<bool> EnsureIndexAsync(CancellationToken cancellationToken = default)
        => gateway.EnsureIndexAsync(NotesIndex.Name, NotesIndex.DefinitionJson, cancellationToken);

    /// <summary>
    /// Persists a confirmed correction. Returns the stored note (id assigned
    /// here). The id is deterministic over (project, text, anchor) — mirroring
    /// <c>ContradictionPair.Id</c> — so a timed-out-then-retried add upserts
    /// the same note instead of double-adding (Phase 21); an identical retry
    /// only refreshes <c>CreatedAt</c>.
    /// </summary>
    public async Task<Note> AddAsync(
        string text, string project, string? targetPath = null, string? author = null, CancellationToken cancellationToken = default)
    {
        await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);

        var trimmedText = text.Trim();
        var normalizedTarget = string.IsNullOrWhiteSpace(targetPath) ? null : Indexing.PathNormalizer.Normalize(targetPath);

        var note = new Note(
            Id: DeterministicId(project, trimmedText, normalizedTarget),
            Project: project,
            Text: trimmedText,
            TargetPath: normalizedTarget,
            Author: string.IsNullOrWhiteSpace(author) ? Environment.UserName : author.Trim(),
            CreatedAt: DateTimeOffset.UtcNow);

        float[]? vector = null;
        try
        {
            vector = embedder?.Embed(note.Text);
        }
        catch
        {
            // Lexical-only note beats no note; matching just gets weaker.
        }

        var document = new
        {
            note_id = note.Id,
            project = note.Project,
            text = note.Text,
            target_path = note.TargetPath,
            author = note.Author,
            created_at = note.CreatedAt,
            content_vector = vector,
        };

        var payload = JsonSerializer.Serialize(new { index = new { _index = NotesIndex.Name, _id = note.Id } })
            + "\n"
            + JsonSerializer.Serialize(document, JsonOptions)
            + "\n";

        await gateway.BulkAsync(payload, cancellationToken).ConfigureAwait(false);
        await gateway.RefreshAsync(NotesIndex.Name, cancellationToken).ConfigureAwait(false);
        return note;
    }

    /// <summary>All notes, newest first, optionally scoped to one project.</summary>
    public async Task<IReadOnlyList<Note>> ListAsync(string? project = null, int topK = 100, CancellationToken cancellationToken = default)
    {
        if (!await gateway.IndexExistsAsync(NotesIndex.Name, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        object query = string.IsNullOrWhiteSpace(project)
            ? new { match_all = new { } }
            : new { term = new Dictionary<string, string> { ["project"] = project } };

        var body = JsonSerializer.Serialize(new { size = topK, query, sort = new object[] { new { created_at = "desc" } } });
        var json = await gateway.SearchAsync(NotesIndex.Name, body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseNotes(json).Notes;
    }

    /// <summary>Removes one note by id. Returns true when something was deleted.</summary>
    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!await gateway.IndexExistsAsync(NotesIndex.Name, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var deleted = await gateway.DeleteByTermAsync(NotesIndex.Name, "note_id", id, cancellationToken).ConfigureAwait(false);
        if (deleted > 0)
        {
            await gateway.RefreshAsync(NotesIndex.Name, cancellationToken).ConfigureAwait(false);
        }

        return deleted > 0;
    }

    /// <summary>Drops every note in a project (purge, §2.14). Safe when the index doesn't exist.</summary>
    public async Task<long> PurgeProjectAsync(string project, CancellationToken cancellationToken = default)
    {
        if (!await gateway.IndexExistsAsync(NotesIndex.Name, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        return await gateway.DeleteByTermAsync(NotesIndex.Name, "project", project, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Notes matching a query — semantic (with score floor) when a query vector
    /// is supplied, lexical otherwise. Scored notes join the search candidate
    /// pool in <c>DocumentSearch</c>.
    /// </summary>
    public async Task<IReadOnlyList<(Note Note, double Score)>> SearchAsync(
        string query, float[]? queryVector, string? project = null, CancellationToken cancellationToken = default)
    {
        if (!await gateway.IndexExistsAsync(NotesIndex.Name, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var body = BuildSearchQuery(query, queryVector, project);
        var json = await gateway.SearchAsync(NotesIndex.Name, body, cancellationToken: cancellationToken).ConfigureAwait(false);
        var (notes, scores) = ParseNotes(json);

        var results = new List<(Note, double)>();
        for (var i = 0; i < notes.Count; i++)
        {
            if (queryVector is null || scores[i] >= MinSearchScore)
            {
                results.Add((notes[i], scores[i]));
            }
        }

        return results;
    }

    /// <summary>Notes anchored to any of the given normalized source paths (the "annotates" half of §2.13 C).</summary>
    public async Task<IReadOnlyList<Note>> FindAnchoredAsync(
        IReadOnlyCollection<string> normalizedPaths, CancellationToken cancellationToken = default)
    {
        if (normalizedPaths.Count == 0 || !await gateway.IndexExistsAsync(NotesIndex.Name, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var body = JsonSerializer.Serialize(new
        {
            size = 50,
            query = new { terms = new Dictionary<string, object> { ["target_path"] = normalizedPaths } },
            sort = new object[] { new { created_at = "desc" } },
        });

        var json = await gateway.SearchAsync(NotesIndex.Name, body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseNotes(json).Notes;
    }

    /// <summary>Same inputs → same id, so re-adding is an upsert. Internal for tests.</summary>
    internal static string DeterministicId(string project, string text, string? targetPath)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{project}|{targetPath}|{text}")))[..16].ToLowerInvariant();

    internal static string BuildSearchQuery(string query, float[]? queryVector, string? project)
    {
        object queryClause;
        if (queryVector is not null)
        {
            object inner = string.IsNullOrWhiteSpace(project)
                ? new { vector = queryVector, k = MaxNoteCandidates }
                : new
                {
                    vector = queryVector,
                    k = MaxNoteCandidates,
                    filter = new { term = new Dictionary<string, string> { ["project"] = project } },
                };
            queryClause = new { knn = new Dictionary<string, object> { ["content_vector"] = inner } };
        }
        else
        {
            object match = new { match = new { text = query } };
            queryClause = string.IsNullOrWhiteSpace(project)
                ? match
                : new { @bool = new { must = new[] { match }, filter = new object[] { new { term = new Dictionary<string, string> { ["project"] = project } } } } };
        }

        return JsonSerializer.Serialize(new { size = MaxNoteCandidates, query = queryClause });
    }

    private static (IReadOnlyList<Note> Notes, IReadOnlyList<double> Scores) ParseNotes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var notes = new List<Note>();
        var scores = new List<double>();

        foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var s = hit.GetProperty("_source");
            notes.Add(new Note(
                Id: GetString(s, "note_id") ?? string.Empty,
                Project: GetString(s, "project") ?? string.Empty,
                Text: GetString(s, "text") ?? string.Empty,
                TargetPath: GetString(s, "target_path"),
                Author: GetString(s, "author") ?? string.Empty,
                CreatedAt: TryGetDate(s, "created_at") ?? DateTimeOffset.MinValue));
            scores.Add(hit.TryGetProperty("_score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : 0);
        }

        return (notes, scores);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string? GetString(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? TryGetDate(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
}
